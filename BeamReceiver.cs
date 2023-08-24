// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibJpegTurbo;
using QoirLib;
using DensityApi;
using K4os.Compression.LZ4;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using ObsInterop;

namespace xObsBeam;

public class BeamReceiver
{
  CancellationTokenSource _cancellationSource = new();
  Task _processDataLoopTask = Task.CompletedTask;
  readonly object _sizeLock = new();
  string _targetHostname = "";
  int _targetPort = -1;
  string _pipeName = "";
  bool _isConnecting;
  ulong _frameTimestampOffset;
  readonly ConcurrentQueue<ulong> _lastRenderedVideoFrameTimestamps = new();
  unsafe void* _turboJpegDecompress = null;
  unsafe qoir_decode_options_struct* _qoirDecodeOptions;

  public ArrayPool<byte> RawDataBufferPool { get; private set; } = ArrayPool<byte>.Create();

  public FrameBuffer? FrameBuffer { get; private set; }

  uint _width;
  public uint Width
  {
    get
    {
      lock (_sizeLock)
        return _width;
    }
  }

  uint _height;
  public uint Height
  {
    get
    {
      lock (_sizeLock)
        return _height;
    }
  }

  public bool IsConnected { get; private set; }

  public int FrameBufferTimeMs { get; set; }

  public void Connect(IPAddress bindAddress, string hostname, int port, PeerDiscovery.Peer currentPeer = default)
  {
    Task.Run(() => ConnectAsync(bindAddress, hostname, port, currentPeer));
  }

  public async Task ConnectAsync(IPAddress bindAddress, string hostname, int port, PeerDiscovery.Peer currentPeer = default)
  {
    if (_isConnecting || IsConnected)
      return;

    _isConnecting = true;
    _targetHostname = hostname;
    _targetPort = port;

    while (_targetHostname != "")
    {
      if (!_cancellationSource.TryReset())
      {
        _cancellationSource.Dispose();
        _cancellationSource = new CancellationTokenSource();
      }

      // do peer discovery if discovery information is available
      if (!currentPeer.IsEmpty)
      {
        var discoveredPeers = PeerDiscovery.Discover(currentPeer).Result;
        if (discoveredPeers.Count > 0)
        {
          _targetHostname = discoveredPeers[0].IP;
          _targetPort = discoveredPeers[0].Port;
        }
      }

      var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
      try
      {
        Module.Log($"Connecting to {_targetHostname}:{_targetPort}...", ObsLogLevel.Debug);
        socket.Bind(new IPEndPoint(bindAddress, 0));
        await socket.ConnectAsync(_targetHostname, _targetPort, _cancellationSource.Token);
      }
      catch (Exception ex)
      {
        Module.Log($"Connection to {_targetHostname}:{_targetPort} failed ({ex.GetType().Name}: {ex.Message}), retrying in 10 seconds.", ObsLogLevel.Error);
        try { socket.Close(); } catch { }
        try { Task.Delay(10000).Wait(_cancellationSource.Token); } catch (OperationCanceledException) { Module.Log("Retrying aborted.", ObsLogLevel.Info); break; }
        if (_cancellationSource.IsCancellationRequested)
        {
          Module.Log("Retrying aborted.", ObsLogLevel.Info);
          break;
        }
        continue; // auto-reconnect mechanism, keep on retrying
      }
      if (!socket.Connected)
      {
        Module.Log($"Connection to {_targetHostname}:{_targetPort} failed, retrying in 10 seconds.", ObsLogLevel.Error);
        try { socket.Close(); } catch { }
        try { Task.Delay(10000).Wait(_cancellationSource.Token); } catch (OperationCanceledException) { Module.Log("Retrying aborted.", ObsLogLevel.Info); break; }
        if (_cancellationSource.IsCancellationRequested)
        {
          Module.Log("Retrying aborted.", ObsLogLevel.Info);
          break;
        }
        continue; // auto-reconnect mechanism, keep on retrying
      }
      IsConnected = true;
      _processDataLoopTask = ProcessDataLoopAsync(socket, null, _cancellationSource.Token);
      break;
    }
    _isConnecting = false;
  }
  public void Connect(string pipeName)
  {
    Task.Run(() => ConnectAsync(pipeName));
  }

  public async Task ConnectAsync(string pipeName)
  {
    if (_isConnecting || IsConnected)
      return;

    _isConnecting = true;
    _pipeName = pipeName;
    while (_pipeName != "")
    {
      if (!_cancellationSource.TryReset())
      {
        _cancellationSource.Dispose();
        _cancellationSource = new CancellationTokenSource();
      }
      var pipeStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
      try
      {
        Module.Log($"Connecting to {_pipeName}...", ObsLogLevel.Debug);
        await pipeStream.ConnectAsync(_cancellationSource.Token);
      }
      catch (Exception ex)
      {
        Module.Log($"Connection to {_pipeName} failed ({ex.GetType().Name}: {ex.Message}), retrying in 10 seconds.", ObsLogLevel.Error);
        try { pipeStream.Close(); } catch { }
        try { Task.Delay(10000).Wait(_cancellationSource.Token); } catch (OperationCanceledException) { Module.Log("Retrying aborted.", ObsLogLevel.Info); break; }
        if (_cancellationSource.IsCancellationRequested)
        {
          Module.Log("Retrying aborted.", ObsLogLevel.Info);
          break;
        }
        continue; // auto-reconnect mechanism, keep on retrying
      }
      if (!pipeStream.IsConnected)
      {
        Module.Log($"Connection to {_pipeName} failed, retrying in 10 seconds.", ObsLogLevel.Error);
        try { pipeStream.Close(); } catch { }
        try { Task.Delay(10000).Wait(_cancellationSource.Token); } catch (OperationCanceledException) { Module.Log("Retrying aborted.", ObsLogLevel.Info); break; }
        if (_cancellationSource.IsCancellationRequested)
        {
          Module.Log("Retrying aborted.", ObsLogLevel.Info);
          break;
        }
        continue; // auto-reconnect mechanism, keep on retrying
      }
      IsConnected = true;
      _processDataLoopTask = ProcessDataLoopAsync(null, pipeStream, _cancellationSource.Token);
      break;
    }
    _isConnecting = false;
  }

  public void Disconnect()
  {
    Module.Log("BeamReceiver Disconnect() called", ObsLogLevel.Debug);
    _targetHostname = "";
    _targetPort = -1;
    _pipeName = "";

    // cancel the processing loop
    try
    {
      if (!_cancellationSource.IsCancellationRequested)
      {
        _cancellationSource.Cancel();
        _processDataLoopTask.Wait();
      }
    }
    catch (Exception ex)
    {
      Module.Log($"{ex.GetType().Name} while disconnecting from Beam: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  public void SetLastOutputVideoFrameTimestamp(ulong timestamp)
  {
    _lastRenderedVideoFrameTimestamps.Enqueue(timestamp);
  }

  public delegate Task AsyncEventHandler<TEventArgs>(object? sender, TEventArgs e);

  public event EventHandler<Beam.BeamVideoData>? VideoFrameReceived;
  void OnVideoFrameReceived(Beam.BeamVideoData frame)
  {
    if (VideoFrameReceived is not null)
      VideoFrameReceived(this, frame);
  }

  public event EventHandler<Beam.BeamAudioData>? AudioFrameReceived;
  void OnAudioFrameReceived(Beam.BeamAudioData frame)
  {
    if (AudioFrameReceived is not null)
      AudioFrameReceived(this, frame);
  }

  public event EventHandler<EventArgs>? Disconnected;
  void OnDisconnected()
  {
    Task.Run(() => Disconnected?.Invoke(this, EventArgs.Empty));
  }

  #region unsafe helper functions
  private unsafe void TurboJpegDecompressInit()
  {
    if (EncoderSupport.LibJpegTurboV3)
      _turboJpegDecompress = TurboJpeg.tj3Init((int)TJINIT.TJINIT_DECOMPRESS);
    else if (EncoderSupport.LibJpegTurbo)
      _turboJpegDecompress = TurboJpeg.tjInitDecompress();
  }

  private unsafe void TurboJpegDecompressDestroy()
  {
    if (_turboJpegDecompress != null)
    {
      if (EncoderSupport.LibJpegTurboV3)
        TurboJpeg.tj3Destroy(_turboJpegDecompress);
      else
        _ = TurboJpeg.tjDestroy(_turboJpegDecompress);
      _turboJpegDecompress = null;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private unsafe void TurboJpegDecompressToBgra(byte[] receivedFrameData, int dataSize, byte[] rawDataBuffer, int width, int height)
  {
    fixed (byte* jpegBuf = receivedFrameData, dstBuf = rawDataBuffer)
    {
      int compressResult;
      if (EncoderSupport.LibJpegTurboV3)
        compressResult = TurboJpeg.tj3Decompress8(_turboJpegDecompress, jpegBuf, (uint)dataSize, dstBuf, width * TurboJpeg.tjPixelSize[(int)TJPF.TJPF_BGRA], (int)TJPF.TJPF_BGRA);
      else if (EncoderSupport.LibJpegTurbo)
        compressResult = TurboJpeg.tjDecompress2(_turboJpegDecompress, jpegBuf, (uint)dataSize, dstBuf, width, width * TurboJpeg.tjPixelSize[(int)TJPF.TJPF_BGRA], height, (int)TJPF.TJPF_BGRA, 0);
      else
      {
        Module.Log($"Error: JPEG library is not available, cannot decompress received video data!", ObsLogLevel.Error);
        return;
      }
      if (compressResult != 0)
        Module.Log("turboJpegDecompressToBgra failed with error " + TurboJpeg.tjGetErrorCode(_turboJpegDecompress) + ": " + Marshal.PtrToStringUTF8((IntPtr)TurboJpeg.tjGetErrorStr2(_turboJpegDecompress)), ObsLogLevel.Error);
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private unsafe void TurboJpegDecompressToYuv(byte[] receivedFrameData, int dataSize, byte[] rawDataBuffer, uint[] videoPlaneSizes, int width, int height)
  {
    fixed (byte* jpegBuf = receivedFrameData, dstBuf = rawDataBuffer)
    {
      uint currentOffset = 0;
      var planePointers = stackalloc byte*[videoPlaneSizes.Length];
      for (int planeIndex = 0; planeIndex < videoPlaneSizes.Length; planeIndex++)
      {
        planePointers[planeIndex] = dstBuf + currentOffset;
        currentOffset += videoPlaneSizes[planeIndex];
      }
      int compressResult;
      if (EncoderSupport.LibJpegTurboV3)
        compressResult = TurboJpeg.tj3DecompressToYUVPlanes8(_turboJpegDecompress, jpegBuf, (uint)dataSize, planePointers, null);
      else if (EncoderSupport.LibJpegTurbo)
        compressResult = TurboJpeg.tjDecompressToYUVPlanes(_turboJpegDecompress, jpegBuf, (uint)dataSize, planePointers, width, null, height, 0);
      else
      {
        Module.Log($"Error: JPEG library is not available, cannot decompress received video data!", ObsLogLevel.Error);
        return;
      }
      if (compressResult != 0)
        Module.Log("turboJpegDecompressToYuv failed with error " + TurboJpeg.tjGetErrorCode(_turboJpegDecompress) + ": " + Marshal.PtrToStringUTF8((IntPtr)TurboJpeg.tjGetErrorStr2(_turboJpegDecompress)), ObsLogLevel.Error);
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private unsafe void QoirDecompress(byte[] receivedFrameData, int dataSize, byte[] rawDataBuffer, int rawDataSize)
  {
    fixed (byte* qoirBuf = receivedFrameData, dstBuf = rawDataBuffer)
    {
      var qoirDecodeResult = Qoir.qoir_decode(qoirBuf, (nuint)dataSize, _qoirDecodeOptions);
      if (qoirDecodeResult.status_message != null)
        Module.Log("QOIR decompression failed with error: " + Marshal.PtrToStringUTF8((IntPtr)qoirDecodeResult.status_message), ObsLogLevel.Error);
      new Span<byte>(qoirDecodeResult.dst_pixbuf.data, rawDataSize).CopyTo(new Span<byte>(dstBuf, rawDataSize));
      EncoderSupport.FreePooledPinned(qoirDecodeResult.owned_memory);
    }
  }

  private unsafe void QoirDecompressInit()
  {
    _qoirDecodeOptions = EncoderSupport.MAllocPooledPinned<qoir_decode_options_struct>();
    new Span<byte>(_qoirDecodeOptions, sizeof(qoir_decode_options_struct)).Clear();
    _qoirDecodeOptions->pixfmt = Qoir.QOIR_PIXEL_FORMAT__BGRA_NONPREMUL;
    _qoirDecodeOptions->contextual_malloc_func = &EncoderSupport.QoirMAlloc; // important to use our own memory allocator, so that we can also free the memory later
    _qoirDecodeOptions->contextual_free_func = &EncoderSupport.QoirFree;
  }

  private unsafe void QoirDecompressDestroy()
  {
    if (_qoirDecodeOptions != null)
    {
      EncoderSupport.FreePooledPinned(_qoirDecodeOptions);
      _qoirDecodeOptions = null;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private unsafe void DensityDecompress(byte[] receivedFrameData, int dataSize, byte[] rawDataBuffer, int rawDataSize)
  {
    fixed (byte* sourceBuf = receivedFrameData, dstBuf = rawDataBuffer)
    {
      var densityResult = Density.density_decompress(sourceBuf, (ulong)dataSize, dstBuf, (ulong)rawDataSize);
      if (densityResult.state != DENSITY_STATE.DENSITY_STATE_OK)
        Module.Log("Density decompression failed with error " + densityResult.state, ObsLogLevel.Error);
    }
  }

  private static unsafe uint GetRawVideoDataSize(Beam.VideoHeader videoHeader)
  {
    uint rawVideoDataSize = 0;
    // get the plane sizes for the current frame format and size
    var videoPlaneSizes = Beam.GetPlaneSizes(videoHeader.Format, videoHeader.Height, videoHeader.Linesize);
    if (videoPlaneSizes.Length == 0) // unsupported format
      return rawVideoDataSize;

    for (int planeIndex = 0; planeIndex < videoPlaneSizes.Length; planeIndex++)
      rawVideoDataSize += videoPlaneSizes[planeIndex];

    return rawVideoDataSize;
  }

  public static unsafe double GetLocalFps()
  {
    double fps = 0;
    // get current video format
    obs_video_info* obsVideoInfo = ObsBmem.bzalloc<obs_video_info>();
    if (Convert.ToBoolean(Obs.obs_get_video_info(obsVideoInfo)) && (obsVideoInfo != null))
      fps = ((double)obsVideoInfo->fps_num / obsVideoInfo->fps_den);
    ObsBmem.bfree(obsVideoInfo);
    return fps;
  }

  #endregion unsafe helper functions

  private async Task ProcessDataLoopAsync(Socket? socket, NamedPipeClientStream? pipeStream, CancellationToken cancellationToken)
  {
    _frameTimestampOffset = 0;
    _lastRenderedVideoFrameTimestamps.Clear();

    string endpointName;
    PipeReader pipeReader;
    PipeWriter pipeWriter;
    Stream stream;
    if (socket != null)
    {
      Module.Log($"Connected to {socket.RemoteEndPoint}.", ObsLogLevel.Info);
      // PipeReader and PipeWriter will use a network stream
      stream = new NetworkStream(socket);
      endpointName = socket.RemoteEndPoint!.ToString()!;
    }
    else if (pipeStream != null)
    {
      Module.Log($"Connected to {_pipeName}.", ObsLogLevel.Info);
      // PipeReader and PipeWriter will use a NamedPipeServerStream
      stream = pipeStream;
      endpointName = _pipeName;
    }
    else
      throw new ArgumentException("Either socket or pipeStream must be non-null.");

    pipeReader = PipeReader.Create(stream);
    pipeWriter = PipeWriter.Create(stream);

    var videoHeader = new Beam.VideoHeader();
    var audioHeader = new Beam.AudioHeader();

    int videoHeaderSize = Beam.VideoHeader.VideoHeaderDataSize;
    uint rawVideoDataSize = 0;
    uint[] videoPlaneSizes = new uint[Beam.VideoHeader.MAX_AV_PLANES];

    double senderFps = 30;
    uint logCycle = 0;
    int renderDelayAveragingFrameCount = (int)(senderFps / 2);
    int renderDelayAveragingCycle = 0;
    int[] renderDelays = Array.Empty<int>();
    int renderDelayAverage = -1;

    byte[] receivedFrameData = Array.Empty<byte>();

    var decoderOptions = new DecoderOptions
    {
      SkipMetadata = true,
      MaxFrames = 1
    };

    int decodeErrorCount = 1;

    DateTime frameReceivedTime;
    ulong lastVideoTimestamp = 0;
    ulong senderVideoTimestamp;

    bool firstVideoFrame = true;

    if (EncoderSupport.LibJpegTurbo)
      TurboJpegDecompressInit();
    if (EncoderSupport.QoirLib)
      QoirDecompressInit();

    // main loop
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        ReadResult readResult = await pipeReader.ReadAtLeastAsync(videoHeaderSize, cancellationToken);
        if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
        {
          if (readResult.IsCanceled)
            Module.Log("processDataLoopAsync() exit from reading header data through cancellation.", ObsLogLevel.Debug);
          else
            Module.Log("processDataLoopAsync() exit from reading header data through completion.", ObsLogLevel.Debug);
          break;
        }

        frameReceivedTime = DateTime.UtcNow;

        var beamType = Beam.GetBeamType(readResult.Buffer);
        if (beamType == Beam.Type.Video)
        {
          // read and validate header information
          pipeReader.AdvanceTo(videoHeader.FromSequence(readResult.Buffer), readResult.Buffer.End);
          if (videoHeader.Fps > 0)
            senderFps = ((double)videoHeader.Fps / videoHeader.FpsDenominator);
          if (logCycle >= senderFps)
            Module.Log($"Video data: Received header {videoHeader.Timestamp}, Receive/Render delay: {videoHeader.ReceiveDelay} / {videoHeader.RenderDelay} ({renderDelayAverage}) ms ", ObsLogLevel.Debug);
          if (videoHeader.DataSize == 0)
            Module.Log($"Video data: Frame {videoHeader.Timestamp} skipped by sender.", ObsLogLevel.Warning);
          else if (videoHeader.DataSize < 0)
          {
            Module.Log($"Video data: Header reports invalid data size of {videoHeader.DataSize} bytes, aborting connection.", ObsLogLevel.Error);
            break;
          }

          // set frame timestamp offset
          senderVideoTimestamp = videoHeader.Timestamp; // save the original sender timestamp
          if (_frameTimestampOffset == 0) // initialize the offset if this is the very first frame since connecting
          {
            _frameTimestampOffset = videoHeader.Timestamp;
            Module.Log($"Video data: Frame timestamp offset initialized to: {_frameTimestampOffset}", ObsLogLevel.Debug);
          }
          if (_frameTimestampOffset > videoHeader.Timestamp) // could happen for the very first pair of audio and video frames after connecting
          {
            Module.Log($"Video data: Frame timestamp offset {_frameTimestampOffset} is higher than current timestamp {videoHeader.Timestamp}, correcting to timestamp 1.", ObsLogLevel.Warning);
            videoHeader.Timestamp = 1;
          }
          else
            videoHeader.Timestamp -= _frameTimestampOffset; // normal operation, just apply the offset

          // read video data
          if (videoHeader.DataSize > 0)
          {
            // tell the sender the current video frame timestamp that was received - only done for frames that were not skipped by the sender
            pipeWriter.GetSpan(sizeof(byte))[0] = (byte)Beam.ReceiveTimestampTypes.Receive;
            pipeWriter.Advance(sizeof(byte));
            BinaryPrimitives.WriteUInt64LittleEndian(pipeWriter.GetSpan(sizeof(ulong)), senderVideoTimestamp);
            pipeWriter.Advance(sizeof(ulong));
            var writeReceivedTimestampResult = await pipeWriter.FlushAsync(cancellationToken);
            if (writeReceivedTimestampResult.IsCanceled || writeReceivedTimestampResult.IsCompleted)
            {
              if (writeReceivedTimestampResult.IsCanceled)
                Module.Log("processDataLoopAsync() exit from sending through cancellation.", ObsLogLevel.Debug);
              else
                Module.Log("processDataLoopAsync() exit from sending through completion.", ObsLogLevel.Debug);
              break;
            }

            readResult = await pipeReader.ReadAtLeastAsync(videoHeader.DataSize, cancellationToken);
            if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
            {
              if (readResult.IsCanceled)
                Module.Log("processDataLoopAsync() exit from reading video data through cancellation.", ObsLogLevel.Debug);
              else
                Module.Log("processDataLoopAsync() exit from reading video data through completion.", ObsLogLevel.Debug);
              break;
            }

            bool sizeChanged = false;
            lock (_sizeLock)
            {
              sizeChanged = ((_width != videoHeader.Width) || (_height != videoHeader.Height));
              _width = videoHeader.Width;
              _height = videoHeader.Height;
            }
            if (sizeChanged || firstVideoFrame) // re-allocate the arrays matching the new necessary size
            {
              firstVideoFrame = false;

              renderDelayAveragingFrameCount = (int)(senderFps / 2);
              renderDelays = new int[renderDelayAveragingFrameCount];

              if (videoHeader.Compression == Beam.CompressionTypes.JpegLossy)
              {
                var jpegSubsampling = (int)EncoderSupport.ObsToJpegSubsampling(videoHeader.Format);
                if (EncoderSupport.LibJpegTurboV3)
                  rawVideoDataSize = (uint)TurboJpeg.tj3YUVBufSize((int)videoHeader.Width, 1, (int)videoHeader.Height, jpegSubsampling);
                else if (EncoderSupport.LibJpegTurbo)
                  rawVideoDataSize = (uint)TurboJpeg.tjBufSizeYUV2((int)videoHeader.Width, 1, (int)videoHeader.Height, jpegSubsampling);
                else
                {
                  rawVideoDataSize = 0;
                  Module.Log($"Error: JPEG library is not available, cannot decompress received video data!", ObsLogLevel.Error);
                  break;
                }
                EncoderSupport.GetJpegPlaneSizes(videoHeader.Format, (int)videoHeader.Width, (int)videoHeader.Height, out videoPlaneSizes, out _);
              }
              else if (videoHeader.Compression == Beam.CompressionTypes.Density)
                rawVideoDataSize = (uint)Density.density_decompress_safe_size(GetRawVideoDataSize(videoHeader));
              else
                rawVideoDataSize = GetRawVideoDataSize(videoHeader);
              if (rawVideoDataSize == 0) // unsupported format
                break;

              receivedFrameData = new byte[rawVideoDataSize];
              RawDataBufferPool = ArrayPool<byte>.Create((int)rawVideoDataSize, 2);

              if (FrameBufferTimeMs > 0)
              {
                var localFps = GetLocalFps();
                FrameBuffer = new FrameBuffer(FrameBufferTimeMs, senderFps, localFps, RawDataBufferPool);
                Module.Log($"Buffering {FrameBuffer.VideoFrameBufferCount} video frames based on a frame buffer time of {FrameBuffer.FrameBufferTimeMs} ms for {senderFps:F} sender FPS (local: {localFps:F} FPS).", ObsLogLevel.Info);
              }
              else
              {
                FrameBuffer = null;
                Module.Log("Frame buffering disabled.", ObsLogLevel.Info);
              }
            }

            // average render delay calculation
            renderDelays[renderDelayAveragingCycle] = videoHeader.RenderDelay;
            if (++renderDelayAveragingCycle >= renderDelayAveragingFrameCount)
            {
              renderDelayAveragingCycle = 0;
              renderDelayAverage = (int)renderDelays.Average();
            }

            var rawDataBuffer = RawDataBufferPool.Rent((int)rawVideoDataSize);
            if (videoHeader.Compression == Beam.CompressionTypes.None)
              readResult.Buffer.Slice(0, videoHeader.DataSize).CopyTo(rawDataBuffer);
            else
            {
              readResult.Buffer.Slice(0, videoHeader.DataSize).CopyTo(receivedFrameData);

              switch (videoHeader.Compression)
              {
                case Beam.CompressionTypes.Lz4:
                  int decompressedSizeLz4 = LZ4Codec.Decode(receivedFrameData, 0, videoHeader.DataSize, rawDataBuffer, 0, (int)rawVideoDataSize);
                  if (decompressedSizeLz4 != rawVideoDataSize)
                    Module.Log($"LZ4 decompression failed, expected {rawVideoDataSize} bytes, got {decompressedSizeLz4} bytes.", ObsLogLevel.Error);
                  break;
                case Beam.CompressionTypes.Qoi:
                  Qoi.Decode(receivedFrameData, videoHeader.DataSize, rawDataBuffer, (int)rawVideoDataSize);
                  break;
                case Beam.CompressionTypes.Qoy:
                  Qoy.Decode(receivedFrameData, videoHeader.Width, videoHeader.Height, videoHeader.DataSize, rawDataBuffer, (int)rawVideoDataSize);
                  break;
                case Beam.CompressionTypes.Qoir:
                  QoirDecompress(receivedFrameData, videoHeader.DataSize, rawDataBuffer, (int)rawVideoDataSize);
                  break;
                case Beam.CompressionTypes.Density:
                  DensityDecompress(receivedFrameData, videoHeader.DataSize, rawDataBuffer, (int)rawVideoDataSize);
                  break;
                case Beam.CompressionTypes.JpegLossy:
                  TurboJpegDecompressToYuv(receivedFrameData, (int)rawVideoDataSize, rawDataBuffer, videoPlaneSizes, (int)videoHeader.Width, (int)videoHeader.Height);
                  break;
                case Beam.CompressionTypes.JpegLossless:
                  TurboJpegDecompressToBgra(receivedFrameData, (int)rawVideoDataSize, rawDataBuffer, (int)videoHeader.Width, (int)videoHeader.Height);
                  break;
                case Beam.CompressionTypes.Png:
                  try
                  {
                    using var memoryStream = new MemoryStream(receivedFrameData);
                    using var decodedImage = await PngDecoder.Instance.DecodeAsync<Rgba32>(decoderOptions, memoryStream, cancellationToken);
                    decodedImage.CopyPixelDataTo(rawDataBuffer);
                  }
                  catch (InvalidImageContentException ex)
                  {
                    string fileName = "DecodeError" + decodeErrorCount++ + ".png";
                    Module.Log($"PNG decompression failed with {ex.GetType().Name}: {ex.Message}\nData saved to {fileName} for review.", ObsLogLevel.Error);
                    _ = File.WriteAllBytesAsync(fileName, receivedFrameData, cancellationToken);
                  }
                  break;
              }
            }

            // process the frame
            if (FrameBuffer == null)
            {
              if (videoHeader.Timestamp < lastVideoTimestamp)
                Module.Log($"Warning: Received video frame {videoHeader.Timestamp} is older than previous frame {lastVideoTimestamp}. Use a high enough frame buffer if the sender is not compressing from the OBS render thread.", ObsLogLevel.Warning);
              else
                OnVideoFrameReceived(new Beam.BeamVideoData(videoHeader, rawDataBuffer, frameReceivedTime, renderDelayAverage));
              lastVideoTimestamp = videoHeader.Timestamp;
            }
            else
              FrameBuffer.ProcessFrame(new Beam.BeamVideoData(videoHeader, rawDataBuffer, frameReceivedTime, renderDelayAverage));
            long receiveLength = readResult.Buffer.Length; // remember this here, before the buffer is invalidated with the next line
            pipeReader.AdvanceTo(readResult.Buffer.GetPosition(videoHeader.DataSize), readResult.Buffer.End);

            if (logCycle >= senderFps)
              Module.Log($"Video data: Expected {videoHeader.DataSize} bytes, received {receiveLength} bytes", ObsLogLevel.Debug);

          }
          if (logCycle++ >= senderFps)
            logCycle = 0;
        }
        else if (beamType == Beam.Type.Audio)
        {
          // read and validate header information
          pipeReader.AdvanceTo(audioHeader.FromSequence(readResult.Buffer), readResult.Buffer.End);
          if (logCycle >= senderFps)
            Module.Log($"Audio data: Received header {audioHeader.Timestamp}", ObsLogLevel.Debug);
          if (audioHeader.DataSize == 0)
          {
            Module.Log($"Audio data: Frame {audioHeader.Timestamp} skipped by sender.", ObsLogLevel.Warning);
          }
          else if (audioHeader.DataSize < 0)
          {
            Module.Log($"Audio data: Header reports invalid data size of {audioHeader.DataSize} bytes, aborting connection.", ObsLogLevel.Error);
            break;
          }

          audioHeader.Timestamp -= _frameTimestampOffset;

          // read audio data
          if (audioHeader.DataSize > 0)
          {
            readResult = await pipeReader.ReadAtLeastAsync(audioHeader.DataSize, cancellationToken);
            if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
            {
              if (readResult.IsCanceled)
                Module.Log("processDataLoopAsync() exit from reading audio data through cancellation.", ObsLogLevel.Debug);
              else
                Module.Log("processDataLoopAsync() exit from reading audio data through completion.", ObsLogLevel.Debug);
              break;
            }

            // process the frame
            if (!firstVideoFrame && (_frameTimestampOffset > 0)) // Beam treads video frames as a kind of header in several ways, ignore audio frames that were received before the first video frame
            {
              if (FrameBuffer == null)
                OnAudioFrameReceived(new Beam.BeamAudioData(audioHeader, readResult.Buffer.Slice(0, audioHeader.DataSize).ToArray(), frameReceivedTime));
              else
                FrameBuffer.ProcessFrame(new Beam.BeamAudioData(audioHeader, readResult.Buffer.Slice(0, audioHeader.DataSize).ToArray(), frameReceivedTime));
            }

            long receiveLength = readResult.Buffer.Length; // remember this here, before the buffer is invalidated with the next line
            pipeReader.AdvanceTo(readResult.Buffer.GetPosition(audioHeader.DataSize), readResult.Buffer.End);

            if (logCycle >= senderFps)
              Module.Log($"Audio data: Expected {audioHeader.DataSize} bytes, received {receiveLength} bytes", ObsLogLevel.Debug);

          }
        }
        else
        {
          Module.Log($"Received unknown header type ({beamType}), aborting connection.", ObsLogLevel.Error);
          break;
        }

        // tell the sender the last video frame timestamps that were output by OBS
        while (_lastRenderedVideoFrameTimestamps.TryDequeue(out ulong lastRenderedVideoFrameTimestamp))
        {
          lastRenderedVideoFrameTimestamp += _frameTimestampOffset;
          pipeWriter.GetSpan(sizeof(byte))[0] = (byte)Beam.ReceiveTimestampTypes.Render;
          pipeWriter.Advance(sizeof(byte));
          BinaryPrimitives.WriteUInt64LittleEndian(pipeWriter.GetSpan(sizeof(ulong)), lastRenderedVideoFrameTimestamp);
          pipeWriter.Advance(sizeof(ulong));
          // Module.Log($"Receiver rendered video timestamp {lastRenderedVideoFrameTimestamp} SEND.", ObsLogLevel.Warning); //HACK: only for testing
          var writeRenderTimestampResult = await pipeWriter.FlushAsync(cancellationToken);
          if (writeRenderTimestampResult.IsCanceled || writeRenderTimestampResult.IsCompleted)
          {
            if (writeRenderTimestampResult.IsCanceled)
              Module.Log("processDataLoopAsync() exit from sending through cancellation.", ObsLogLevel.Debug);
            else
              Module.Log("processDataLoopAsync() exit from sending through completion.", ObsLogLevel.Debug);
            break;
          }
        }
      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"processDataLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (IOException ex)
      {
        Module.Log($"{ex.GetType().Name} while trying to process or retrieve data: {ex.Message}", ObsLogLevel.Error);
        pipeReader.Complete(ex);
        break;
      }
      catch (Exception ex)
      {
        Module.Log($"{ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        pipeReader.Complete(ex);
        break;
      }
    }

    // stop the frame buffer so that the source no longer shows any frames and return the remaining unused frames from the buffer to the pool
    FrameBuffer?.Stop();

    try { pipeReader.Complete(); } catch { } // exceptions are possible if the pipe is already closed, but can then be ignored
    try { pipeWriter.Complete(); } catch { } // exceptions are possible if the pipe is already closed, but can then be ignored

    // close the socket and stream, which also notifies the sender
    if (socket != null)
    {
      try { socket.Shutdown(SocketShutdown.Receive); } catch (Exception ex) { Module.Log($"{ex.GetType().Name} when doing socket shutdown: {ex.Message}", ObsLogLevel.Error); }
      try { socket.Close(); } catch (Exception ex) { Module.Log($"{ex.GetType().Name} when closing socket: {ex.Message}", ObsLogLevel.Error); }
    }
    else if (pipeStream != null)
    {
      try { pipeStream.Close(); } catch (Exception ex) { Module.Log($"{ex.GetType().Name} when doing pipe shutdown: {ex.Message}", ObsLogLevel.Error); }
      try { pipeStream.Dispose(); } catch (Exception ex) { Module.Log($"{ex.GetType().Name} when disposing pipe: {ex.Message}", ObsLogLevel.Error); }
    }
    Module.Log($"Disconnected from {endpointName}.", ObsLogLevel.Info);
    TurboJpegDecompressDestroy();
    QoirDecompressDestroy();
    stream?.Close();
    IsConnected = false;
    OnDisconnected();
  }
}
