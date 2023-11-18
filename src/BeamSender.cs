// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using LibJpegTurbo;
using QoirLib;
using DensityApi;
using ObsInterop;

namespace xObsBeam;

public class BeamSender(Beam.SenderTypes senderType)
{
  public const int MaxFrameQueueSize = 5;
  public const int DefaultPort = 13629;

  readonly ConcurrentDictionary<string, BeamSenderClient> _clients = new();
  readonly ConcurrentDictionary<string, NamedPipeServerStream> _pipeServers = new();

  TcpListener _listener = new(IPAddress.Loopback, DefaultPort);
  string _pipeName = "";
  CancellationTokenSource _listenCancellationSource = new();
  ArrayPool<byte>? _videoDataPool;
  int _videoDataPoolMaxSize;
  private int _videoFramesProcessed;
  private int _videoFramesCompressed;
  readonly Beam.SenderTypes _senderType = senderType;
  Beam.VideoHeader _videoHeader;
  Beam.AudioHeader _audioHeader;
  byte[] _audioData = [];
  Beam.VideoPlaneInfo _videoPlaneInfo;
  Beam.VideoPlaneInfo _i420PlaneInfo;
  Beam.VideoPlaneInfo _i422PlaneInfo;
  uint _audioBytesPerChannel;
  int _audioPlanes;

  // cached compression settings
  int _jpegCompressionQuality = 90;
  TJSAMP _jpegSubsampling = TJSAMP.TJSAMP_444;
  TJPF _jpegPixelFormat = TJPF.TJPF_RGB;
  TJCS _jpegColorspace = TJCS.TJCS_RGB;
  bool _jpegYuv;
  bool _libJpegTurboV3;
  private double _compressionThreshold = 1;
  DENSITY_ALGORITHM _densityAlgorithm;
  bool _compressionThreadingSync = true;
  unsafe qoir_encode_options_struct* _qoirEncodeOptions = null;
  readonly PeerDiscovery _discoveryServer = new();

  public unsafe bool SetVideoParameters(BeamSenderProperties properties, video_format format, video_format conversionVideoFormat, uint width, uint height, uint fps_num, uint fps_den, byte full_range, float* color_matrix, float* color_range_min, float* color_range_max, uint* linesize, video_data._data_e__FixedBuffer data)
  {
    // reset some variables
    _videoFramesProcessed = 0;
    _videoFramesCompressed = 0;
    _compressionThreshold = 1;

    // allow potential previous compression buffers to be garbage collected
    _videoDataPool = null;

    // free previous options objects if they exist
    if (_qoirEncodeOptions != null)
    {
      EncoderSupport.FreePooledPinned(_qoirEncodeOptions);
      _qoirEncodeOptions = null;
    }

    if (conversionVideoFormat != video_format.VIDEO_FORMAT_NONE)
      format = conversionVideoFormat;

    if (properties.JpegCompression && EncoderSupport.LibJpegTurbo)
    {
      var jpegformat = EncoderSupport.JpegDropAlpha(format); // YUV formats with alpha channel are not supported for JPEG, for these formats just ignore the alpha plane that is at the end
      if (format != jpegformat)
      {
        Module.Log($"Warning: JPEG compression does not support alpha channel, dropping alpha plane for {format}, resulting in {jpegformat}.", ObsLogLevel.Warning);
        format = jpegformat;
      }
    }

    // get the plane information for the current frame format and size
    _videoPlaneInfo = Beam.GetVideoPlaneInfo(format, width, height);
    if (_videoPlaneInfo.Count == 0) // unsupported format, will also be logged by the GetPlaneInfo() function
      return false;

    for (int planeIndex = 0; planeIndex < _videoPlaneInfo.Count; planeIndex++)
    {
      Module.Log($"SetVideoParameters(): {format} line size[{planeIndex}] = {linesize[planeIndex]}, plane size[{planeIndex}] = {_videoPlaneInfo.PlaneSizes[planeIndex]}, offset[{planeIndex}] = {_videoPlaneInfo.Offsets[planeIndex]}", ObsLogLevel.Debug);
      // validate actual video data plane pointers against the GetVideoPlaneInfo() information
      var pointerOffset = (IntPtr)data.e0 + _videoPlaneInfo.Offsets[planeIndex];
      if (pointerOffset != (IntPtr)data[planeIndex])
      {
        // either GetPlaneInfo() returned wrong information or the video data plane pointers are not contiguous in memory (which we currently rely on)
        Module.Log($"Warning: Video data plane pointer for {format} plane {planeIndex} with resolution {width}x{height} has a difference of {pointerOffset - (IntPtr)data[planeIndex]} ({(IntPtr)data[planeIndex] - (IntPtr)data.e0} instead of {pointerOffset - (IntPtr)data.e0}).", ObsLogLevel.Warning);
      }
    }

    // create the video header with current frame base info as a template for every frame - in most cases only the timestamp changes so that this instance can be reused without copies of it being created
    var videoHeader = new Beam.VideoHeader()
    {
      Type = (_senderType == Beam.SenderTypes.FilterVideo ? Beam.Type.VideoOnly : Beam.Type.Video),
      DataSize = (int)_videoPlaneInfo.DataSize,
      Width = width,
      Height = height,
      Fps = fps_num,
      FpsDenominator = fps_den,
      Format = format,
      FullRange = full_range,
      Compression = Beam.CompressionTypes.None,
    };
    new ReadOnlySpan<float>(color_matrix, 16).CopyTo(new Span<float>(videoHeader.ColorMatrix, 16));
    new ReadOnlySpan<float>(color_range_min, 3).CopyTo(new Span<float>(videoHeader.ColorRangeMin, 3));
    new ReadOnlySpan<float>(color_range_max, 3).CopyTo(new Span<float>(videoHeader.ColorRangeMax, 3));
    _videoHeader = videoHeader;

    // cache compression settings
    if (properties.QoirCompression && EncoderSupport.QoirLib)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Qoir;
      _videoDataPoolMaxSize = (int)((width * height * 4)); // this buffer will only be used if compression actually reduced the frame size
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);

      _qoirEncodeOptions = EncoderSupport.MAllocPooledPinned<qoir_encode_options_struct>();
      new Span<byte>(_qoirEncodeOptions, sizeof(qoir_encode_options_struct)).Clear();
      // 0 = lossless - 1 loses 1 bit and results in 7 bit colors, 2 results in 6 bit colors, etc., even setting this to 7 is working, but for lossy both CPU load and compression ratio are worse than JPEG, so it's useless
      _qoirEncodeOptions->lossiness = 0;
      _qoirEncodeOptions->contextual_malloc_func = &EncoderSupport.QoirMAlloc; // important to use our own memory allocator, so that we can also free the memory later, also it's pooled memory
      _qoirEncodeOptions->contextual_free_func = &EncoderSupport.QoirFree;
      _compressionThreshold = properties.QoirCompressionLevel / 10.0;
    }
    else if (properties.JpegCompression && EncoderSupport.LibJpegTurbo)
    {
      if (format == video_format.VIDEO_FORMAT_NV12) // NV12 is a special case, because it's already in YUV format, but the planes are interleaved
        _i420PlaneInfo = Beam.GetVideoPlaneInfo(video_format.VIDEO_FORMAT_I420, width, height);
      else if (format is video_format.VIDEO_FORMAT_YVYU or video_format.VIDEO_FORMAT_UYVY or video_format.VIDEO_FORMAT_YUY2) // more special cases where it's already in YUV format, but the planes are interleaved
        _i422PlaneInfo = Beam.GetVideoPlaneInfo(video_format.VIDEO_FORMAT_I422, width, height);

      _videoHeader.Compression = Beam.CompressionTypes.Jpeg;
      _jpegCompressionQuality = properties.JpegCompressionQuality;
      _videoDataPoolMaxSize = (int)_videoPlaneInfo.DataSize;
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);

      _libJpegTurboV3 = EncoderSupport.LibJpegTurboV3;
      _jpegPixelFormat = EncoderSupport.ObsToJpegPixelFormat(format);
      _jpegSubsampling = EncoderSupport.ObsToJpegSubsampling(format);
      _jpegColorspace = EncoderSupport.ObsToJpegColorSpace(format);
      _jpegYuv = EncoderSupport.FormatIsYuv(format);
      _compressionThreshold = properties.JpegCompressionLevel / 10.0;
    }
    else if (properties.DensityCompression && EncoderSupport.DensityApi)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Density;
      _densityAlgorithm = (DENSITY_ALGORITHM)properties.DensityCompressionStrength;
      _videoDataPoolMaxSize = (int)Density.density_compress_safe_size((ulong)videoHeader.DataSize);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = properties.DensityCompressionLevel / 10.0;
    }
    else if (properties.QoiCompression)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Qoi;
      _videoDataPoolMaxSize = (int)((width * height * 5)); // QOI's theoretical max size for BGRA is 5x the size of the original image
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = properties.QoiCompressionLevel / 10.0;
    }
    else if (properties.QoyCompression)
    {
      Qoy.Initialize();
      _videoHeader.Compression = Beam.CompressionTypes.Qoy;
      _videoDataPoolMaxSize = Qoy.GetMaxSize((int)width, (int)height);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = properties.QoyCompressionLevel / 10.0;
    }
    else if (properties.Lz4Compression)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Lz4;
      _videoDataPoolMaxSize = LZ4Codec.MaximumOutputSize((int)_videoPlaneInfo.DataSize);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = properties.Lz4CompressionLevel / 10.0;
    }
    _compressionThreadingSync = properties.CompressionMainThread;

    var videoBandwidthMbps = (((Beam.VideoHeader.VideoHeaderDataSize + _videoPlaneInfo.DataSize) * (fps_num / fps_den)) / 1024 / 1024) * 8;
    if (_videoHeader.Compression == Beam.CompressionTypes.None)
      Module.Log($"Video output feed initialized, theoretical uncompressed net bandwidth demand is {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    else
    {
      Module.Log($"Video output feed initialized with {_videoHeader.Compression} compression. Sync to render thread: {_compressionThreadingSync}. Theoretical uncompressed net bandwidth demand would be {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    }

    return true;
  }

  public unsafe bool VideoParametersChanged(video_format format, uint width, uint height, uint fps_num, uint fps_den, byte full_range, float* color_matrix, float* color_range_min, float* color_range_max)
  {
    var videoHeader = _videoHeader;
    return ((_videoHeader.Width != width) ||
        (_videoHeader.Height != height) ||
        (_videoHeader.Fps != fps_num) ||
        (_videoHeader.FpsDenominator != fps_den) ||
        (_videoHeader.Format != format) ||
        (_videoHeader.FullRange != full_range) ||
        new ReadOnlySpan<float>(color_matrix, 16).SequenceEqual(new ReadOnlySpan<float>(videoHeader.ColorMatrix, 16)) == false ||
        new ReadOnlySpan<float>(color_range_min, 3).SequenceEqual(new ReadOnlySpan<float>(videoHeader.ColorRangeMin, 3)) == false ||
        new ReadOnlySpan<float>(color_range_max, 3).SequenceEqual(new ReadOnlySpan<float>(videoHeader.ColorRangeMax, 3)) == false
    );
  }

  public unsafe void SetAudioParameters(audio_format format, speaker_layout speakers, uint samples_per_sec, uint frames)
  {
    Beam.GetAudioPlaneInfo(format, speakers, out _audioPlanes, out _audioBytesPerChannel);
    uint audioPlaneSize = _audioBytesPerChannel * frames;
    int audioDataSize = (int)audioPlaneSize * _audioPlanes;
    _audioHeader = new Beam.AudioHeader()
    {
      Type = (_senderType == Beam.SenderTypes.FilterAudio ? Beam.Type.AudioOnly : Beam.Type.Audio),
      DataSize = (audioDataSize * 2), // for filters there is some variance in the number of frames and therefore in the total data size, so we double it to be on the safe side (this field is used to determine the pool size)
      Format = format,
      SampleRate = samples_per_sec,
      Speakers = speakers,
      Frames = frames,
    };
    _audioData = new byte[_audioHeader.DataSize]; // don't need a pool here, because the audio data is processed in sync context

    Module.Log($"{_audioHeader.Type} output feed initialized, reserving {_audioHeader.DataSize} bytes of memory per packet.", ObsLogLevel.Debug);
  }

  public unsafe bool AudioParametersChanged(audio_format format, speaker_layout speakers, uint samples_per_sec)
  {
    return ((_audioHeader.Format != format) ||
        (_audioHeader.Speakers != speakers) ||
        (_audioHeader.SampleRate != samples_per_sec)
    );
  }

  public bool CanStart
  {
    get
    {
      if (_senderType is Beam.SenderTypes.Output or Beam.SenderTypes.FilterAudioVideo)
        return ((_videoPlaneInfo.DataSize > 0) && (_audioPlanes > 0));
      if (_senderType == Beam.SenderTypes.FilterVideo)
        return (_videoPlaneInfo.DataSize > 0);
      if (_senderType == Beam.SenderTypes.FilterAudio)
        return (_audioPlanes > 0);
      if (_senderType == Beam.SenderTypes.Relay)
        return true;
      return false;
    }
  }

  public async void Start(string identifier, IPAddress localAddr, int port, bool automaticPort)
  {
    if ((_senderType is Beam.SenderTypes.Output or Beam.SenderTypes.FilterAudioVideo or Beam.SenderTypes.FilterVideo) && (_videoPlaneInfo.DataSize == 0))
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if ((_senderType is Beam.SenderTypes.Output or Beam.SenderTypes.FilterAudioVideo or Beam.SenderTypes.FilterAudio) && (_audioPlanes == 0))
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    int failCount = 0;
    while (failCount < 10)
    {
      try
      {
        _listener = new TcpListener(localAddr, port);
        _listener.Start();
      }
      catch (SocketException)
      {
        if (automaticPort)
        {
          failCount++;
          Module.Log($"Failed to start TCP listener for {identifier} on {localAddr}:{port}, attempt {failCount} of 10.", ObsLogLevel.Debug);
          continue;
        }
        else
        {
          Module.Log($"Failed to start TCP listener for {identifier} on {localAddr}:{port}, try configuring a different port or use a different interface.", ObsLogLevel.Error);
          return;
        }
      }
      try
      {
        _discoveryServer.StartServer(localAddr, port, _senderType, identifier);
        break; // if we got here without exception the port is good
      }
      catch (SocketException)
      {
        try { _listener.Stop(); } catch { } // listening on the TCP port worked if we got here, so try to stop it again to try the next port
        _discoveryServer.StopServer();
        if (automaticPort)
        {
          failCount++;
          Module.Log($"Failed to start UDP listener for {identifier} on {localAddr}:{port}, attempt {failCount} of 10.", ObsLogLevel.Debug);
          continue;
        }
        else
        {
          Module.Log($"Failed to start UDP listener for {identifier} on {localAddr}:{port}, try configuring a different port or use a different interface.", ObsLogLevel.Error);
          return;
        }
      }
    }
    if (failCount >= 10)
    {
      Module.Log($"Failed to start listener for {identifier} on {localAddr}:{port}, try configuring a static port or use a different interface.", ObsLogLevel.Error);
      return;
    }

    Module.Log($"Listening on {localAddr}:{port}.", ObsLogLevel.Info);

    if (!_listenCancellationSource.TryReset())
    {
      _listenCancellationSource.Dispose();
      _listenCancellationSource = new CancellationTokenSource();
    }

    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {localAddr}:{port}.", ObsLogLevel.Debug);
        var clientSocket = await _listener.AcceptSocketAsync(_listenCancellationSource.Token);
        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;
        if ((clientSocket != null) && (clientSocket.RemoteEndPoint != null))
        {
          string clientId = clientSocket.RemoteEndPoint.ToString()!;
          Module.Log($"Socket client {clientId} accepted. Pending: {_listener.Pending()}, Data available: {clientSocket.Available}, Blocking: {clientSocket.Blocking}, Connected: {clientSocket.Connected}, DontFragment: {clientSocket.DontFragment}, Dual: {clientSocket.DualMode}, Broadcast: {clientSocket.EnableBroadcast}, Excl: {clientSocket.ExclusiveAddressUse}, Bound: {clientSocket.IsBound}, Linger: {clientSocket.LingerState!}, NoDelay: {clientSocket.NoDelay}, RecvBuff: {clientSocket.ReceiveBufferSize}, SendBuff: {clientSocket.SendBufferSize}, TTL: {clientSocket.Ttl}", ObsLogLevel.Debug);
          var client = new BeamSenderClient(clientId, clientSocket, _videoHeader, _audioHeader);
          client.Disconnected += ClientDisconnectedEventHandler;
          client.Start();
          _clients.AddOrUpdate(clientId, client, (key, oldClient) => client);
        }
        else
          Module.Log($"Listener accepted socket but it was null.", ObsLogLevel.Warning);
      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"Listener loop cancelled through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (Exception ex)
      {
        Module.Log($"{ex.GetType().Name} in BeamSender.Start: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    _listener.Stop();
    Module.Log($"Listener stopped.", ObsLogLevel.Info);
  }

  public async void Start(string identifier, string pipeName)
  {
    if ((_senderType is Beam.SenderTypes.Output or Beam.SenderTypes.FilterAudioVideo or Beam.SenderTypes.FilterVideo) && (_videoPlaneInfo.DataSize == 0))
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if ((_senderType is Beam.SenderTypes.Output or Beam.SenderTypes.FilterAudioVideo or Beam.SenderTypes.FilterAudio) && (_audioPlanes == 0))
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    _pipeName = pipeName;

    var pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    _discoveryServer.StartServer(_senderType, identifier);

    Module.Log($"Listening on {_pipeName}.", ObsLogLevel.Info);

    if (!_listenCancellationSource.TryReset())
    {
      _listenCancellationSource.Dispose();
      _listenCancellationSource = new CancellationTokenSource();
    }

    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {_pipeName}.", ObsLogLevel.Debug);
        await pipeStream.WaitForConnectionAsync(_listenCancellationSource.Token);

        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;

        // generate GUID for the client
        string clientId = Guid.NewGuid().ToString();
        Module.Log($"Pipe client {clientId} accepted.", ObsLogLevel.Debug);

        // create a new BeamSenderClient
        var client = new BeamSenderClient(clientId, pipeStream, _videoHeader, _audioHeader);
        client.Disconnected += ClientDisconnectedEventHandler;
        client.Start();
        _clients.AddOrUpdate(clientId, client, (key, oldClient) => client);

        // create a new pipe for the next client
        pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"Listener loop cancelled through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (Exception ex)
      {
        Module.Log($"{ex.GetType().Name} in BeamSender.Start: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    pipeStream.Close();
    pipeStream.Dispose();
    Module.Log($"Listener stopped.", ObsLogLevel.Info);
  }

  public void Stop()
  {
    try
    {
      Module.Log($"Stopping BeamSender...", ObsLogLevel.Debug);
      _listenCancellationSource.Cancel();
      foreach (var client in _clients.Values)
        client.Disconnect(); // this will block for up to 1000 ms per client to try and get a clean disconnect
      _audioPlanes = 0;
      _discoveryServer.StopServer();

      Module.Log($"Stopped BeamSender.", ObsLogLevel.Debug);
    }
    catch (Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in BeamReceiver.Stop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private unsafe void SendCompressed(ulong timestamp, Beam.VideoHeader videoHeader, byte* rawData, byte[]? encodedData)
  {
    try
    {
      int encodedDataLength = 0;

      if (videoHeader.Compression is Beam.CompressionTypes.Jpeg) // apply JPEG compression if enabled
      {
        fixed (byte* jpegBuf = encodedData)
        {
          // nuint jpegDataLength = (nuint)(videoHeader.Width * TurboJpeg.tjPixelSize[(int)TJPF.TJPF_BGRA] * videoHeader.Height);
          nuint jpegDataLength = (nuint)videoHeader.DataSize;
          int compressResult;

          void* turboJpegCompress; // needs to be recreated for every compression to be sure it's thread-safe, as stated here: https://github.com/libjpeg-turbo/libjpeg-turbo/issues/584
          if (EncoderSupport.LibJpegTurboV3)
          {
            turboJpegCompress = TurboJpeg.tj3Init((int)TJINIT.TJINIT_COMPRESS);
            _ = TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_NOREALLOC, 1);
            _ = TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_COLORSPACE, (int)_jpegColorspace);
            _ = TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_SUBSAMP, (int)_jpegSubsampling);
            _ = TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_QUALITY, _jpegCompressionQuality);
          }
          else
            turboJpegCompress = TurboJpeg.tjInitCompress();

          if (_jpegYuv)
          {
            var planeInfo = _videoPlaneInfo;
            if (videoHeader.Format == video_format.VIDEO_FORMAT_NV12)
              planeInfo = _i420PlaneInfo; // packed format, was converted to I420 so that libjpeg-turbo can handle it
            else if (videoHeader.Format is video_format.VIDEO_FORMAT_YVYU or video_format.VIDEO_FORMAT_UYVY or video_format.VIDEO_FORMAT_YUY2)
              planeInfo = _i422PlaneInfo; // packed format, was converted to I422 so that libjpeg-turbo can handle it

            // set the pointers to the start of each plane
            var planes = stackalloc byte*[planeInfo.Count];
            for (int planeIndex = 0; planeIndex < planeInfo.Count; planeIndex++)
              planes[planeIndex] = rawData + planeInfo.Offsets[planeIndex];

            if (_libJpegTurboV3)
            {
              compressResult = TurboJpeg.tj3CompressFromYUVPlanes8(turboJpegCompress, planes, (int)videoHeader.Width, null, (int)videoHeader.Height, &jpegBuf, &jpegDataLength);
              TurboJpeg.tj3Destroy(turboJpegCompress);
            }
            else
            {
              compressResult = TurboJpeg.tjCompressFromYUVPlanes(turboJpegCompress, planes, (int)videoHeader.Width, null, (int)videoHeader.Height, (int)_jpegSubsampling, &jpegBuf, &jpegDataLength, _jpegCompressionQuality, TurboJpeg.TJFLAG_NOREALLOC);
              _ = TurboJpeg.tjDestroy(turboJpegCompress);
            }
          }
          else
          {
            if (_libJpegTurboV3)
            {
              compressResult = TurboJpeg.tj3Compress8(turboJpegCompress, rawData, (int)videoHeader.Width, 0, (int)videoHeader.Height, (int)_jpegPixelFormat, &jpegBuf, &jpegDataLength);
              TurboJpeg.tj3Destroy(turboJpegCompress);
            }
            else
            {
              compressResult = TurboJpeg.tjCompress2(turboJpegCompress, rawData, (int)videoHeader.Width, 0, (int)videoHeader.Height, (int)_jpegPixelFormat, &jpegBuf, &jpegDataLength, (int)_jpegSubsampling, _jpegCompressionQuality, TurboJpeg.TJFLAG_NOREALLOC);
              _ = TurboJpeg.tjDestroy(turboJpegCompress);
            }
          }
          encodedDataLength = (int)jpegDataLength;

          if (compressResult != 0)
          {
            if (_libJpegTurboV3)
              Module.Log("JPEG compression failed with error " + TurboJpeg.tj3GetErrorCode(turboJpegCompress) + ": " + Marshal.PtrToStringUTF8((IntPtr)TurboJpeg.tj3GetErrorStr(turboJpegCompress)), ObsLogLevel.Error);
            else
              Module.Log("JPEG compression failed with error " + TurboJpeg.tjGetErrorCode(turboJpegCompress) + ": " + Marshal.PtrToStringUTF8((IntPtr)TurboJpeg.tjGetErrorStr2(turboJpegCompress)), ObsLogLevel.Error);
            return;
          }
        }
      }
      else if (videoHeader.Compression is Beam.CompressionTypes.Density) // apply Density compression if enabled
      {
        fixed (byte* densityBuf = encodedData)
        {
          var densityResult = Density.density_compress(rawData, (ulong)videoHeader.DataSize, densityBuf, (ulong)_videoDataPoolMaxSize, _densityAlgorithm);
          if (densityResult.state != DENSITY_STATE.DENSITY_STATE_OK)
          {
            Module.Log("Density compression failed with error " + densityResult.state, ObsLogLevel.Error);
            return;
          }
          encodedDataLength = (int)densityResult.bytesWritten;
        }
      }
      else if (videoHeader.Compression is Beam.CompressionTypes.Qoir) // apply QOIR compression if enabled
      {
        var qoirPixelBuffer = EncoderSupport.MAllocPooledPinned<qoir_pixel_buffer_struct>();
        qoirPixelBuffer->pixcfg.width_in_pixels = videoHeader.Width;
        qoirPixelBuffer->pixcfg.height_in_pixels = videoHeader.Height;
        qoirPixelBuffer->pixcfg.pixfmt = Qoir.QOIR_PIXEL_FORMAT__BGRA_NONPREMUL;
        qoirPixelBuffer->data = rawData;
        qoirPixelBuffer->stride_in_bytes = _videoPlaneInfo.Linesize[0];
        var qoirEncodeResult = Qoir.qoir_encode(qoirPixelBuffer, _qoirEncodeOptions);
        EncoderSupport.FreePooledPinned(qoirPixelBuffer);
        if (qoirEncodeResult.status_message != null)
        {
          Module.Log("QOIR compression failed with error: " + Marshal.PtrToStringUTF8((IntPtr)qoirEncodeResult.status_message), ObsLogLevel.Error);
          return;
        }
        if (qoirEncodeResult.dst_len == 0)
        {
          Module.Log("QOIR compression failed.", ObsLogLevel.Error);
          return;
        }
        encodedDataLength = (int)qoirEncodeResult.dst_len;
        new ReadOnlySpan<byte>(qoirEncodeResult.dst_ptr, encodedDataLength).CopyTo(encodedData);
        EncoderSupport.FreePooledPinned(qoirEncodeResult.dst_ptr);
      }
      else if (videoHeader.Compression is Beam.CompressionTypes.Qoi) // apply QOI compression if enabled
        encodedDataLength = Qoi.Encode(rawData, 0, videoHeader.DataSize, 4, encodedData!); // encode the frame with QOI
      else if (videoHeader.Compression is Beam.CompressionTypes.Qoy) // apply QOY compression if enabled
        encodedDataLength = Qoy.Encode(rawData, videoHeader.Width, videoHeader.Height, encodedData!); // encode the frame with QOY
      else if (videoHeader.Compression is Beam.CompressionTypes.Lz4)
      {
        fixed (byte* targetData = encodedData)
          encodedDataLength = LZ4Codec.Encode(rawData, videoHeader.DataSize, targetData, _videoDataPoolMaxSize, LZ4Level.L00_FAST);
      }

      if (encodedDataLength < videoHeader.DataSize) // did compression decrease the size of the data?
        videoHeader.DataSize = encodedDataLength;
      else
      {
        Module.Log(videoHeader.Compression + " compression did not decrease the size of the data, skipping frame " + timestamp, ObsLogLevel.Debug);
        videoHeader.Compression = Beam.CompressionTypes.None; // send raw instead
      }

      if (videoHeader.Compression != Beam.CompressionTypes.None)
      {
        foreach (var client in _clients.Values)
          client.EnqueueVideoFrame(timestamp, videoHeader, encodedData!);
      }
      else
      {
        foreach (var client in _clients.Values)
          client.EnqueueVideoFrame(timestamp, videoHeader, rawData);
      }
    }
    catch (Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in sendCompressed(): {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      throw;
    }
    finally
    {
      // return the rented encoding data buffers to the pool, each client has created a copy of that data for its own use
      if (encodedData != null)
        _videoDataPool?.Return(encodedData);
    }
  }

  public unsafe void RelayVideo(Beam.VideoHeader videoHeader, ReadOnlySequence<byte>? data = null)
  {
    foreach (var client in _clients.Values)
    {
      client.EnqueueVideoTimestamp(videoHeader.Timestamp);
      if (data != null)
        client.EnqueueVideoFrame(videoHeader.Timestamp, videoHeader, (ReadOnlySequence<byte>)data!);
      else
        client.EnqueueVideoFrame(videoHeader.Timestamp, videoHeader, []);
    }
  }

  public unsafe void SendVideo(ulong timestamp, byte* data)
  {
    if (_clients.IsEmpty)
      return;

    // make sure clients know the order of the frames, needs to be done here in a sync context
    foreach (var client in _clients.Values)
      client.EnqueueVideoTimestamp(timestamp);

    // no compression of any kind, we also stay sync so just enqueue the raw data right away from the original unmanaged memory
    if ((_videoHeader.Compression == Beam.CompressionTypes.None))
    {
      foreach (var client in _clients.Values)
        client.EnqueueVideoFrame(timestamp, _videoHeader, data);
      return;
    }

    bool compressThisFrame = ((_compressionThreshold == 1) || (((double)_videoFramesCompressed / _videoFramesProcessed) < _compressionThreshold));
    // frame skipping logic
    if (_compressionThreshold < 1) // is frame skipping enabled?
    {
      _videoFramesProcessed++;
      if (compressThisFrame)
        _videoFramesCompressed++;
      else if (_videoFramesProcessed >= 10)
      {
        _videoFramesProcessed = 0;
        _videoFramesCompressed = 0;
      }
    }
    if (!compressThisFrame)
    {
      var videoHeader = _videoHeader;
      videoHeader.Compression = Beam.CompressionTypes.None;
      foreach (var client in _clients.Values)
        client.EnqueueVideoFrame(timestamp, videoHeader, data);
      return;
    }

    // --- continue here if compression is enabled ---

    // prepare compression buffer
    byte[]? encodedData = _videoDataPool!.Rent(_videoDataPoolMaxSize);

    if (_compressionThreadingSync)
    {
      if ((_videoHeader.Compression == Beam.CompressionTypes.Jpeg) && (_videoHeader.Format is video_format.VIDEO_FORMAT_NV12 or video_format.VIDEO_FORMAT_YVYU or video_format.VIDEO_FORMAT_UYVY or video_format.VIDEO_FORMAT_YUY2))
      {
        byte[]? managedDataCopy = _videoDataPool!.Rent(_videoDataPoolMaxSize);
        switch (_videoHeader.Format)
        {
          case video_format.VIDEO_FORMAT_NV12:
            EncoderSupport.Nv12ToI420(data, managedDataCopy, _videoPlaneInfo, _i420PlaneInfo);
            break;
          case video_format.VIDEO_FORMAT_YVYU:
            EncoderSupport.YvyuToI422(data, managedDataCopy, _i422PlaneInfo);
            break;
          case video_format.VIDEO_FORMAT_UYVY:
            EncoderSupport.UyvyToI422(data, managedDataCopy, _i422PlaneInfo);
            break;
          case video_format.VIDEO_FORMAT_YUY2:
            EncoderSupport.Yuy2ToI422(data, managedDataCopy, _i422PlaneInfo);
            break;
        }
        fixed (byte* videoData = managedDataCopy)
          SendCompressed(timestamp, _videoHeader, videoData, encodedData);
        _videoDataPool!.Return(managedDataCopy);
      }
      else
        SendCompressed(timestamp, _videoHeader, data, encodedData); // in sync with this OBS render thread, hence the unmanaged data array and header instance stays valid and can directly be used
    }
    else
    {
      byte[]? managedDataCopy = _videoDataPool!.Rent(_videoDataPoolMaxSize); // need a managed memory copy for async handover

      // to save an additional frame copy operation the JPEG deinterleaving is done in the sync part of the code with an implicit copy operation to managed memory that is needed anyway for the async handover
      if ((_videoHeader.Compression is Beam.CompressionTypes.Jpeg) && (_videoHeader.Format == video_format.VIDEO_FORMAT_NV12))
        EncoderSupport.Nv12ToI420(data, managedDataCopy, _videoPlaneInfo, _i420PlaneInfo);
      else if ((_videoHeader.Compression is Beam.CompressionTypes.Jpeg) && (_videoHeader.Format == video_format.VIDEO_FORMAT_YVYU))
        EncoderSupport.YvyuToI422(data, managedDataCopy, _i422PlaneInfo);
      else if ((_videoHeader.Compression is Beam.CompressionTypes.Jpeg) && (_videoHeader.Format == video_format.VIDEO_FORMAT_UYVY))
        EncoderSupport.UyvyToI422(data, managedDataCopy, _i422PlaneInfo);
      else if ((_videoHeader.Compression is Beam.CompressionTypes.Jpeg) && (_videoHeader.Format == video_format.VIDEO_FORMAT_YUY2))
        EncoderSupport.Yuy2ToI422(data, managedDataCopy, _i422PlaneInfo);
      else
        new ReadOnlySpan<byte>(data, (int)_videoPlaneInfo.DataSize).CopyTo(managedDataCopy); // just copy to managed as-is for formats that are not packed

      var beamVideoData = new Beam.BeamVideoData(_videoHeader, managedDataCopy, timestamp); // create a copy of the video header and data, so that the data can be used in the thread
      Task.Factory.StartNew(state =>
      {
        var capturedBeamVideoData = (Beam.BeamVideoData)state!; // capture into thread-local context
        fixed (byte* videoData = capturedBeamVideoData.Data)
          SendCompressed(capturedBeamVideoData.Timestamp, capturedBeamVideoData.Header, videoData, encodedData);
        _videoDataPool!.Return(capturedBeamVideoData.Data);
      }, beamVideoData);
    }
  }

  public unsafe void RelayAudio(Beam.AudioHeader audioHeader, ReadOnlySequence<byte> data)
  {
    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.EnqueueAudio(audioHeader, data);
  }

  public unsafe void SendAudio(ulong timestamp, uint frames, audio_data._data_e__FixedBuffer data)
  {
    uint audioPlaneSize = _audioBytesPerChannel * frames;
    int audioDataSize = (int)audioPlaneSize * _audioPlanes;
    uint currentOffset = 0;
    for (int planeIndex = 0; planeIndex < _audioPlanes; planeIndex++)
    {
      new ReadOnlySpan<byte>(data[planeIndex], (int)audioPlaneSize).CopyTo(new Span<byte>(_audioData, (int)currentOffset, (int)audioPlaneSize));
      currentOffset += audioPlaneSize;
    }

    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.EnqueueAudio(timestamp, frames, _audioData, audioDataSize);
  }

  public unsafe void SendAudio(ulong timestamp, uint frames, obs_audio_data._data_e__FixedBuffer data)
  {
    uint audioPlaneSize = _audioBytesPerChannel * frames;
    int audioDataSize = (int)audioPlaneSize * _audioPlanes;
    uint currentOffset = 0;
    for (int planeIndex = 0; planeIndex < _audioPlanes; planeIndex++)
    {
      new ReadOnlySpan<byte>(data[planeIndex], (int)audioPlaneSize).CopyTo(new Span<byte>(_audioData, (int)currentOffset, (int)audioPlaneSize));
      currentOffset += audioPlaneSize;
    }

    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.EnqueueAudio(timestamp, frames, _audioData, audioDataSize);
  }
  #region Event handlers
  private unsafe void ClientDisconnectedEventHandler(object? sender, EventArgs e)
  {
    string clientId = ((BeamSenderClient)sender!).ClientId;
    if (!_clients.TryRemove(clientId, out _))
      Module.Log($"BeamSender.ClientDisconnectedEventHandler: Could not remove client from dictionary.", ObsLogLevel.Error);
    if (_pipeServers.TryRemove(clientId, out NamedPipeServerStream? pipeServer))
      pipeServer.Dispose();
  }
  #endregion Event handlers

}
