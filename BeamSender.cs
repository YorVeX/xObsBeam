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
using FpngeLib;
using DensityApi;
using ObsInterop;

namespace xObsBeam;

public class BeamSender
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
  Beam.VideoHeader _videoHeader;
  Beam.AudioHeader _audioHeader;
  int _videoDataSize;
  uint[] _videoPlaneSizes = Array.Empty<uint>();
  uint[] _jpegYuvPlaneSizes = Array.Empty<uint>();
  int _audioDataSize;
  int _audioBytesPerSample;

  // cached compression settings
  int _jpegCompressionQuality = 90;
  TJSAMP _jpegSubsampling = TJSAMP.TJSAMP_444;
  TJPF _jpegPixelFormat = TJPF.TJPF_RGB;
  TJCS _jpegColorspace = TJCS.TJCS_RGB;
  bool _jpegYuv;
  bool _libJpegTurboV3;
  bool _qoirCompressionLossless;
  int _qoirCompressionQuality = 90;
  private double _compressionThreshold = 1;
  DENSITY_ALGORITHM _densityAlgorithm;
  bool _compressionThreadingSync = true;
  unsafe qoir_encode_options_struct* _qoirEncodeOptions = null;
  unsafe FPNGEOptions* _fpngeOptions = null;
  readonly PeerDiscovery _discoveryServer = new();

  public unsafe bool SetVideoParameters(video_output_info* info, video_format conversionVideoFormat, uint* linesize, video_data._data_e__FixedBuffer data)
  {
    // reset some variables
    _videoDataSize = 0;
    _videoFramesProcessed = 0;
    _videoFramesCompressed = 0;
    _compressionThreshold = 1;

    // allow potential previous compression buffers to be garbage collected
    _videoDataPool = null;

    // free previous options objects if they exist
    if (_fpngeOptions != null)
    {
      EncoderSupport.FreePooledPinned(_fpngeOptions);
      _fpngeOptions = null;
    }
    if (_qoirEncodeOptions != null)
    {
      EncoderSupport.FreePooledPinned(_qoirEncodeOptions);
      _qoirEncodeOptions = null;
    }

    var format = info->format;
    if (conversionVideoFormat != video_format.VIDEO_FORMAT_NONE)
      format = conversionVideoFormat;

    // get the plane sizes for the current frame format and size
    _videoPlaneSizes = Beam.GetPlaneSizes(format, info->height, linesize);
    if (_videoPlaneSizes.Length == 0) // unsupported format, will also be logged by the GetPlaneSizes() function
      return false;

    for (int i = 0; i < Beam.VideoHeader.MAX_AV_PLANES; i++)
      Module.Log("SetVideoParameters(): linesize[" + i + "] = " + linesize[i], ObsLogLevel.Debug);

    var pointerOffset = (IntPtr)data.e0;
    for (int planeIndex = 0; planeIndex < _videoPlaneSizes.Length; planeIndex++)
    {
      // calculate the total size of the video data by summing the plane sizes
      _videoDataSize += (int)_videoPlaneSizes[planeIndex];

      // validate actual video data plane pointers against the GetPlaneSizes() information
      if (pointerOffset != (IntPtr)data[planeIndex])
      {
        // either the GetPlaneSizes() returned wrong information or the video data plane pointers are not contiguous in memory (which we currently rely on)
        Module.Log($"Video data plane pointer for plane {planeIndex} of format {info->format} has a difference of {pointerOffset - (IntPtr)data[planeIndex]}.", ObsLogLevel.Warning);
        //BUG: this is currently happening for odd resolutions like 1279x719 on YUV formats, because padding is not properly handled by GetPlaneSizes(), leading to image distortion
      }
      pointerOffset += (int)_videoPlaneSizes[planeIndex];
    }

    // create the video header with current frame base info as a template for every frame - in most cases only the timestamp changes so that this instance can be reused without copies of it being created
    var videoHeader = new Beam.VideoHeader()
    {
      Type = Beam.Type.Video,
      DataSize = _videoDataSize,
      Width = info->width,
      Height = info->height,
      Fps = info->fps_num,
      FpsDenominator = info->fps_den,
      Format = format,
      Range = info->range,
      Colorspace = info->colorspace,
      Compression = Beam.CompressionTypes.None,
    };
    new ReadOnlySpan<uint>(linesize, Beam.VideoHeader.MAX_AV_PLANES).CopyTo(new Span<uint>(videoHeader.Linesize, Beam.VideoHeader.MAX_AV_PLANES));
    _videoHeader = videoHeader;

    // cache compression settings
    if (SettingsDialog.QoirCompression && EncoderSupport.QoirLib)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Qoir;
      _qoirCompressionLossless = SettingsDialog.QoirCompressionLossless;
      _qoirCompressionQuality = SettingsDialog.QoirCompressionQuality;
      _videoDataPoolMaxSize = (int)((info->width * info->height * 4)); // this buffer will only be used if compression actually reduced the frame size
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);

      _qoirEncodeOptions = EncoderSupport.MAllocPooledPinned<qoir_encode_options_struct>();
      new Span<byte>(_qoirEncodeOptions, sizeof(qoir_encode_options_struct)).Clear();
      // 0 = lossless - 1 loses 1 bit and results in 7 bit colors, 2 results in 6 bit colors, etc., even setting this to 7 is working :-D
      if (SettingsDialog.QoirCompressionLossless)
        _qoirEncodeOptions->lossiness = 0;
      else
        _qoirEncodeOptions->lossiness = 8 - (uint)SettingsDialog.QoirCompressionQuality;
      _qoirEncodeOptions->contextual_malloc_func = &EncoderSupport.QoirMAlloc; // important to use our own memory allocator, so that we can also free the memory later, also it's pooled memory
      _qoirEncodeOptions->contextual_free_func = &EncoderSupport.QoirFree;
      _compressionThreshold = SettingsDialog.QoirCompressionLevel / 10.0;
    }
    else if (SettingsDialog.JpegCompression && EncoderSupport.LibJpegTurbo)
    {
      if (SettingsDialog.JpegCompressionLossless && EncoderSupport.LibJpegTurboLossless)
      {
        _videoHeader.Compression = Beam.CompressionTypes.JpegLossless;
        _compressionThreshold = SettingsDialog.JpegCompressionLevel / 10.0;
      }
      else
        _videoHeader.Compression = Beam.CompressionTypes.JpegLossy;
      _jpegCompressionQuality = SettingsDialog.JpegCompressionQuality;
      _videoDataPoolMaxSize = _videoDataSize;
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);

      _libJpegTurboV3 = EncoderSupport.LibJpegTurboV3;
      _jpegPixelFormat = EncoderSupport.ObsToJpegPixelFormat(format);
      _jpegSubsampling = EncoderSupport.ObsToJpegSubsampling(format);
      _jpegColorspace = EncoderSupport.ObsToJpegColorSpace(format);
      _jpegYuv = EncoderSupport.FormatIsYuv(format);
      if (_jpegYuv) // get plane sizes for a format that libjpeg-turbo can handle (e.g. packed formats need to be deinterleaved)
      {
        _jpegYuvPlaneSizes = Beam.GetYuvPlaneSizes(format, info->width, info->height);
        if (_jpegYuvPlaneSizes.Length == 0)
          _jpegYuvPlaneSizes = _videoPlaneSizes; // no deinterleaving needed, fallback to the original plane sizes
      }
    }
    else if (SettingsDialog.PngCompression && EncoderSupport.FpngeLib)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Png;
      _videoDataPoolMaxSize = (int)Fpnge.FPNGEOutputAllocSize(1, 4, info->width, info->height);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);

      _fpngeOptions = EncoderSupport.MAllocPooledPinned<FPNGEOptions>();
      new Span<byte>(_fpngeOptions, sizeof(FPNGEOptions)).Clear();
      _fpngeOptions->cicp_colorspace = (sbyte)FPNGECicpColorspace.FPNGE_CICP_NONE; //TODO: implement support for 16 bit HDR with FPNGECicpColorspace.FPNGE_CICP_PQ, in that case the bytes_per_channel param of FPNGEEncode and FPNGEOutputAllocSize calls need to be set to 2
      _fpngeOptions->predictor = (sbyte)FPNGEOptionsPredictor.FPNGE_PREDICTOR_FIXED_AVG; //TODO: try other settings and look at the CPU vs. bandwidth trade-off - the default FPNGE_PREDICTOR_BEST has good compression but also the highest CPU usage and sometimes produces frames that IrfanView can display but ImageSharp fails to decode with "Invalid PNG data" message
      _compressionThreshold = SettingsDialog.PngCompressionLevel / 10.0;
    }
    else if (SettingsDialog.DensityCompression && EncoderSupport.DensityApi)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Density;
      _densityAlgorithm = (DENSITY_ALGORITHM)SettingsDialog.DensityCompressionStrength;
      _videoDataPoolMaxSize = (int)Density.density_compress_safe_size((ulong)videoHeader.DataSize);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = SettingsDialog.DensityCompressionLevel / 10.0;
    }
    else if (SettingsDialog.QoiCompression)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Qoi;
      _videoDataPoolMaxSize = (int)((info->width * info->height * 5)); // QOI's theoretical max size for BGRA is 5x the size of the original image
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = SettingsDialog.QoiCompressionLevel / 10.0;
    }
    else if (SettingsDialog.QoyCompression)
    {
      Qoy.Initialize();
      _videoHeader.Compression = Beam.CompressionTypes.Qoy;
      _videoDataPoolMaxSize = Qoy.GetMaxSize((int)info->width, (int)info->height);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = SettingsDialog.QoyCompressionLevel / 10.0;
    }
    else if (SettingsDialog.Lz4Compression)
    {
      _videoHeader.Compression = Beam.CompressionTypes.Lz4;
      _videoDataPoolMaxSize = LZ4Codec.MaximumOutputSize(_videoDataSize);
      if (_videoDataPoolMaxSize > 2147483591) // maximum byte array size
        _videoDataPoolMaxSize = 2147483591;
      _videoDataPool = ArrayPool<byte>.Create(_videoDataPoolMaxSize, MaxFrameQueueSize);
      _compressionThreshold = SettingsDialog.Lz4CompressionLevel / 10.0;
    }
    _compressionThreadingSync = SettingsDialog.CompressionMainThread;

    var videoBandwidthMbps = (((Beam.VideoHeader.VideoHeaderDataSize + _videoDataSize) * (info->fps_num / info->fps_den)) / 1024 / 1024) * 8;
    if (_videoHeader.Compression == Beam.CompressionTypes.None)
      Module.Log($"Video output feed initialized, theoretical uncompressed net bandwidth demand is {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    else
    {
      Module.Log($"Video output feed initialized with {_videoHeader.Compression} compression. Sync to render thread: {_compressionThreadingSync}. Theoretical uncompressed net bandwidth demand would be {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    }

    return true;
  }

  public unsafe void SetAudioParameters(audio_output_info* info, uint frames)
  {
    Beam.GetAudioDataSize(info->format, info->speakers, frames, out _audioDataSize, out _audioBytesPerSample);
    _audioHeader = new Beam.AudioHeader()
    {
      Type = Beam.Type.Audio,
      DataSize = _audioDataSize,
      Format = info->format,
      SampleRate = info->samples_per_sec,
      Speakers = info->speakers,
      Frames = frames,
    };
  }

  public bool CanStart => ((_videoDataSize > 0) && (_audioDataSize > 0));

  public async void Start(string identifier, IPAddress localAddr)
  {
    if (_videoDataSize == 0)
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if (_audioDataSize == 0)
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    int failCount = 0;
    int port = 0;
    while (failCount < 10)
    {
      port = SettingsDialog.Port;
      try
      {
        _listener = new TcpListener(localAddr, port);
        _listener.Start();
      }
      catch (SocketException)
      {
        if (SettingsDialog.AutomaticPort)
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
        _discoveryServer.StartServer(localAddr, port, PeerDiscovery.ServiceTypes.Output, identifier);
        break; // if we got here without exception the port is good
      }
      catch (SocketException)
      {
        try { _listener.Stop(); } catch { } // listening on the TCP port worked if we got here, so try to stop it again to try the next port
        _discoveryServer.StopServer();
        if (SettingsDialog.AutomaticPort)
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

    var stopwatch = new System.Diagnostics.Stopwatch();
    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {localAddr}:{port}.", ObsLogLevel.Debug);
        stopwatch.Restart();
        var clientSocket = await _listener.AcceptSocketAsync(_listenCancellationSource.Token);
        stopwatch.Stop();
        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;
        if ((clientSocket != null) && (clientSocket.RemoteEndPoint != null))
        {
          Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms! Pending: {_listener.Pending()}, Data available: {clientSocket.Available}, Blocking: {clientSocket.Blocking}, Connected: {clientSocket.Connected}, DontFragment: {clientSocket.DontFragment}, Dual: {clientSocket.DualMode}, Broadcast: {clientSocket.EnableBroadcast}, Excl: {clientSocket.ExclusiveAddressUse}, Bound: {clientSocket.IsBound}, Linger: {clientSocket.LingerState!}, NoDelay: {clientSocket.NoDelay}, RecvBuff: {clientSocket.ReceiveBufferSize}, SendBuff: {clientSocket.SendBufferSize}, TTL: {clientSocket.Ttl}", ObsLogLevel.Debug);
          string clientId = clientSocket.RemoteEndPoint.ToString()!;
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
    if (_videoDataSize == 0)
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if (_audioDataSize == 0)
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    _pipeName = pipeName;

    var pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    _discoveryServer.StartServer(PeerDiscovery.ServiceTypes.Output, identifier);

    Module.Log($"Listening on {_pipeName}.", ObsLogLevel.Info);

    if (!_listenCancellationSource.TryReset())
    {
      _listenCancellationSource.Dispose();
      _listenCancellationSource = new CancellationTokenSource();
    }

    var stopwatch = new System.Diagnostics.Stopwatch();
    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {_pipeName}.", ObsLogLevel.Debug);
        stopwatch.Restart();
        await pipeStream.WaitForConnectionAsync(_listenCancellationSource.Token);
        stopwatch.Stop();

        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;

        // generate GUID for the client
        string clientId = Guid.NewGuid().ToString();
        Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms!", ObsLogLevel.Debug);

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
      _videoDataSize = 0;
      _audioDataSize = 0;
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

      if (videoHeader.Compression is Beam.CompressionTypes.JpegLossy or Beam.CompressionTypes.JpegLossless) // apply JPEG compression if enabled
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
            _ = (videoHeader.Compression is Beam.CompressionTypes.JpegLossless)
              ? TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_LOSSLESS, 1)
              : TurboJpeg.tj3Set(turboJpegCompress, (int)TJPARAM.TJPARAM_QUALITY, _jpegCompressionQuality);
          }
          else
            turboJpegCompress = TurboJpeg.tjInitCompress();

          if (_jpegYuv)
          {
            if (videoHeader.Format == video_format.VIDEO_FORMAT_NV12) // packed format, was converted so that libjpeg-turbo can handle it, reflect this in the header
              videoHeader.Format = video_format.VIDEO_FORMAT_I420;

            // the data planes are contiguous in memory as validated by SetVideoParameters(), only need to set the pointers to the start of each plane
            var planes = stackalloc byte*[_jpegYuvPlaneSizes.Length];
            uint currentOffset = 0;
            for (int planeIndex = 0; planeIndex < _jpegYuvPlaneSizes.Length; planeIndex++)
            {
              planes[planeIndex] = rawData + currentOffset;
              currentOffset += _jpegYuvPlaneSizes[planeIndex];
            }

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
      else if (videoHeader.Compression is Beam.CompressionTypes.Png) // apply PNG compression if enabled
      {
        fixed (byte* pngBuf = encodedData)
          encodedDataLength = (int)Fpnge.FPNGEEncode(1, 4, rawData, videoHeader.Width, videoHeader.Linesize[0], videoHeader.Height, pngBuf, _fpngeOptions);
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
        qoirPixelBuffer->stride_in_bytes = videoHeader.Linesize[0];
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
      if ((_videoHeader.Compression is Beam.CompressionTypes.JpegLossy) && (_videoHeader.Format == video_format.VIDEO_FORMAT_NV12)) //TODO: support deinterleaving for more packed formats: VIDEO_FORMAT_YVYU, VIDEO_FORMAT_YUY2, VIDEO_FORMAT_UYVY, VIDEO_FORMAT_AYUV, VIDEO_FORMAT_V210
      {
        byte[]? managedDataCopy = _videoDataPool!.Rent(_videoDataPoolMaxSize);
        EncoderSupport.Nv12ToI420(data, managedDataCopy, _videoPlaneSizes);
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

      // to save an additional frame copy operation the JPEG deinterleaving is done in sync mode as part of the copy to managed memory that is needed anyway for the async handover
      if ((_videoHeader.Compression is Beam.CompressionTypes.JpegLossy) && (_videoHeader.Format == video_format.VIDEO_FORMAT_NV12)) //TODO: support deinterleaving for more packed formats: VIDEO_FORMAT_YVYU, VIDEO_FORMAT_YUY2, VIDEO_FORMAT_UYVY, VIDEO_FORMAT_AYUV, VIDEO_FORMAT_V210
        EncoderSupport.Nv12ToI420(data, managedDataCopy, _videoPlaneSizes);
      else
        new ReadOnlySpan<byte>(data, _videoDataSize).CopyTo(managedDataCopy); // just copy to managed as-is for formats that are not packed

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

  public unsafe void SendAudio(ulong timestamp, byte* data)
  {
    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.EnqueueAudio(timestamp, data);
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
