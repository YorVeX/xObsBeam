// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using K4os.Compression.LZ4;
using ObsInterop;

namespace xObsBeam;

public class BeamSender
{
  public const int MaxFrameQueueSize = 5;
  public const int DefaultPort = 13629;

  ConcurrentDictionary<string, BeamSenderClient> _clients = new ConcurrentDictionary<string, BeamSenderClient>();
  ConcurrentDictionary<string, NamedPipeServerStream> _pipeServers = new ConcurrentDictionary<string, NamedPipeServerStream>();

  TcpListener _listener = new TcpListener(IPAddress.Loopback, DefaultPort);
  string _pipeName = "";
  CancellationTokenSource _listenCancellationSource = new CancellationTokenSource();
  ArrayPool<byte>? _qoiVideoDataPool;
  int _qoiVideoDataPoolMaxSize = 0;
  ArrayPool<byte>? _lz4VideoDataPool;
  int _lz4VideoDataPoolMaxSize = 0;
  int _qoiFrameCycle = 1;
  Beam.VideoHeader _videoHeader;
  Beam.AudioHeader _audioHeader;
  int _videoDataSize = 0;
  int _audioDataSize = 0;
  int _audioBytesPerSample = 0;

  // cached compression settings
  bool _qoiCompression = false;
  int _qoiSkipEveryXFrame = 0;
  bool _lz4Compression = false;
  LZ4Level _lz4CompressionLevel = LZ4Level.L00_FAST;
  bool _compressionThreadingSync = true;

  public unsafe void SetVideoParameters(video_output_info* info, uint* linesize)
  {
    _videoDataSize = 0;
    // get the plane sizes for the current frame format and size
    uint[] planeSizes = Beam.GetPlaneSizes(info->format, info->height, linesize);
    if (planeSizes.Length == 0) // unsupported format
      return;

    // calculate the total size of the video data by summing the plane sizes
    for (int planeIndex = 0; planeIndex < planeSizes.Length; planeIndex++)
      _videoDataSize += (int)planeSizes[planeIndex];

    // create the BeamVideoData object
    _videoHeader = new Beam.VideoHeader()
    {
      Type = Beam.Type.Video,
      DataSize = _videoDataSize,
      Width = info->width,
      Height = info->height,
      Linesize = new ReadOnlySpan<uint>(linesize, Beam.VideoHeader.MAX_AV_PLANES).ToArray(),
      Fps = info->fps_num,
      Format = info->format,
      Range = info->range,
      Colorspace = info->colorspace,
    };

    // cache settings values
    _qoiCompression = SettingsDialog.QoiCompression;
    _lz4Compression = SettingsDialog.Lz4Compression;
    _lz4CompressionLevel = SettingsDialog.Lz4CompressionLevel;
    _compressionThreadingSync = SettingsDialog.CompressionMainThread;

    // QOI's theoretical max size for BGRA is 5x the size of the original image
    if (_qoiCompression)
    {
      _qoiVideoDataPoolMaxSize = (int)((info->width * info->height * 5) + Qoi.PaddingLength);
      _qoiVideoDataPool = ArrayPool<byte>.Create(_qoiVideoDataPoolMaxSize, MaxFrameQueueSize);
      _qoiFrameCycle = 1; // reset the QOI frame cycle for the frame skipping feature
      if (SettingsDialog.QoiCompressionLevel == 10)
        _qoiSkipEveryXFrame = 0;
      else
        _qoiSkipEveryXFrame = SettingsDialog.QoiCompressionLevel + 1;
    }
    else
    {
      // allow potential previous compression buffers to be garbage collected
      _qoiVideoDataPool = null;
    }

    if (_lz4Compression)
    {
      _lz4VideoDataPoolMaxSize = LZ4Codec.MaximumOutputSize(_videoDataSize);
      _lz4VideoDataPool = ArrayPool<byte>.Create(_lz4VideoDataPoolMaxSize, MaxFrameQueueSize);
    }
    else
    {
      // allow potential previous compression buffers to be garbage collected
      _lz4VideoDataPool = null;
    }

    var videoBandwidthMbps = (((Beam.VideoHeader.VideoHeaderDataSize + _videoDataSize) * (info->fps_num / info->fps_den)) / 1024 / 1024) * 8;
    if (!_qoiCompression && !_lz4Compression)
      Module.Log($"Video output feed initialized, theoretical uncompressed net bandwidth demand is {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    else
    {
      string lz4WithLevelString = $"LZ4 ({SettingsDialog.Lz4CompressionLevel})";
      Module.Log($"Video output feed initialized with {(_qoiCompression ? (_lz4Compression ? "QOI + " + lz4WithLevelString : "QOI") : lz4WithLevelString)} compression. Sync to render thread: {_compressionThreadingSync}. Theoretical uncompressed net bandwidth demand would be {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    }
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

  public bool CanStart
  {
    get => (_videoDataSize > 0) && (_audioDataSize > 0);
  }

  public async void Start(string identifier, IPAddress localAddr, int port = DefaultPort)
  {
    if (_videoDataSize == 0)
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if (_audioDataSize == 0)
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    _listener = new TcpListener(localAddr, port);
    _listener.Start();

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
          Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms! Pending: {_listener.Pending()}, Data available: {clientSocket.Available}, Blocking: {clientSocket.Blocking}, Connected: {clientSocket.Connected}, DontFragment: {clientSocket.DontFragment}, Dual: {clientSocket.DualMode}, Broadcast: {clientSocket.EnableBroadcast}, Excl: {clientSocket.ExclusiveAddressUse}, Bound: {clientSocket.IsBound}, Linger: {clientSocket.LingerState!.ToString()}, NoDelay: {clientSocket.NoDelay}, RecvBuff: {clientSocket.ReceiveBufferSize}, SendBuff: {clientSocket.SendBufferSize}, TTL: {clientSocket.Ttl}", ObsLogLevel.Debug);
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
      catch (System.Exception ex)
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

    var pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

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
        string clientId = System.Guid.NewGuid().ToString();
        Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms!", ObsLogLevel.Debug);

        // create a new BeamSenderClient
        var client = new BeamSenderClient(clientId, pipeStream, _videoHeader, _audioHeader);
        client.Disconnected += ClientDisconnectedEventHandler;
        client.Start();
        _clients.AddOrUpdate(clientId, client, (key, oldClient) => client);

        // create a new pipe for the next client
        pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"Listener loop cancelled through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (System.Exception ex)
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
        client.Disconnect();
      _videoDataSize = 0;
      _audioDataSize = 0;

      Module.Log($"Stopped BeamSender.", ObsLogLevel.Debug);
    }
    catch (System.Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in BeamReceiver.Stop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  private unsafe void sendCompressed(ulong timestamp, Beam.VideoHeader videoHeader, byte* rawData, byte[]? encodedDataQoi, byte[]? encodedDataLz4, bool blockOnFrameQueueLimitReached)
  {
    try
    {
      int encodedDataLength = 0;

      // apply QOI compression if enabled
      if (encodedDataQoi != null)
      {
        encodedDataLength = Qoi.Encode(rawData, 0, videoHeader.DataSize, 4, encodedDataQoi); // encode the frame with QOI

        if (encodedDataLength < videoHeader.DataSize) // did compression decrease the size of the data?
        {
          videoHeader.Compression = Beam.CompressionTypes.Qoi;
          videoHeader.DataSize = encodedDataLength;
        }
      }

      // apply LZ4 compression if enabled
      if (encodedDataLz4 != null)
      {
        if (videoHeader.Compression == Beam.CompressionTypes.Qoi) // if QOI was applied before, compress the QOI data
        {
          fixed (byte* sourceData = encodedDataQoi, targetData = encodedDataLz4)
            encodedDataLength = LZ4Codec.Encode(sourceData, videoHeader.DataSize, targetData, _lz4VideoDataPoolMaxSize, _lz4CompressionLevel);
        }
        else // if QOI was not applied before, compress the raw data
        {
          fixed (byte* targetData = encodedDataLz4)
            encodedDataLength = LZ4Codec.Encode(rawData, videoHeader.DataSize, targetData, _lz4VideoDataPoolMaxSize, _lz4CompressionLevel);
        }

        if (encodedDataLength < videoHeader.DataSize) // did compression decrease the size of the data?
        {
          videoHeader.Compression = (videoHeader.Compression == Beam.CompressionTypes.Qoi) ? Beam.CompressionTypes.QoiLz4 : Beam.CompressionTypes.Lz4;
          if (videoHeader.Compression == Beam.CompressionTypes.QoiLz4) // if QOI was applied before, compress the QOI data
            videoHeader.QoiDataSize = videoHeader.DataSize; // remember the size of the QOI data
          videoHeader.DataSize = encodedDataLength;
        }
      }

      switch (videoHeader.Compression)
      {
        case Beam.CompressionTypes.Qoi:
          foreach (var client in _clients.Values)
            client.Enqueue(timestamp, videoHeader, encodedDataQoi!, blockOnFrameQueueLimitReached);
          break;
        case Beam.CompressionTypes.Lz4:
        case Beam.CompressionTypes.QoiLz4:
          foreach (var client in _clients.Values)
            client.Enqueue(timestamp, videoHeader, encodedDataLz4!, blockOnFrameQueueLimitReached);
          break;
        default:
          foreach (var client in _clients.Values)
            client.Enqueue(timestamp, videoHeader, rawData, blockOnFrameQueueLimitReached);
          break;
      }

    }
    catch (System.Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in sendCompressed(): {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      throw;
    }
    finally
    {
      // return the rented encoding data buffers to the pool, each client has created a copy of that data for its own use
      if (encodedDataQoi != null)
        _qoiVideoDataPool?.Return(encodedDataQoi);
      if (encodedDataLz4 != null)
        _lz4VideoDataPool?.Return(encodedDataLz4);
    }
  }

  public unsafe void SendVideo(ulong timestamp, byte* data)
  {
    if (_clients.Count == 0)
      return;

    // no compression of any kind, just enqueue the raw data right away from the original unmanaged memory
    if (!_qoiCompression && !_lz4Compression)
    {
      foreach (var client in _clients.Values)
        client.Enqueue(timestamp, _videoHeader, data, true);
      return;
    }

    // prepare compression buffers
    byte[]? encodedDataLz4 = null;
    byte[]? encodedDataQoi = null;
    if (_qoiCompression && (_qoiFrameCycle != _qoiSkipEveryXFrame))
      encodedDataQoi = _qoiVideoDataPool!.Rent(_qoiVideoDataPoolMaxSize);
    if (_qoiSkipEveryXFrame > 0) // is QOI frame skipping enabled?
    {
      _qoiFrameCycle++;
      if (_qoiFrameCycle > _qoiSkipEveryXFrame)
        _qoiFrameCycle = 1;
    }
    if (_lz4Compression)
      encodedDataLz4 = _lz4VideoDataPool!.Rent(_lz4VideoDataPoolMaxSize);

    if (_compressionThreadingSync)
      sendCompressed(timestamp, _videoHeader, data, encodedDataQoi, encodedDataLz4, true); // in sync with this OBS render thread, hence the unmanaged data array and header instance stays valid and can directly be used
    else
    {
      // will not run in sync with this OBS render thread, need a copy of the unmanaged data array
      var managedDataCopy = new ReadOnlySpan<byte>(data, _videoDataSize).ToArray();
      var beamVideoData = new Beam.BeamVideoData(_videoHeader, managedDataCopy, timestamp); // create a copy of the video header and data, so that the data can be used in the thread
      Task.Factory.StartNew(state =>
      {
        var capturedBeamVideoData = (Beam.BeamVideoData)state!;
        fixed (byte* videoData = capturedBeamVideoData.Data)
          sendCompressed(capturedBeamVideoData.Timestamp, capturedBeamVideoData.Header, videoData, encodedDataQoi, encodedDataLz4, false);
      }, beamVideoData);
    }
  }

  public unsafe void SendAudio(ulong timestamp, int speakers, byte* data)
  {
    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.Enqueue(timestamp, data, speakers, _audioBytesPerSample);
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
