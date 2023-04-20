// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using K4os.Compression.LZ4;

namespace xObsBeam;

public class BeamReceiver
{
  CancellationTokenSource _cancellationSource = new CancellationTokenSource();
  Task _processDataLoopTask = Task.CompletedTask;
  object _sizeLock = new object();
  string _targetHostname = "";
  int _targetPort = -1;
  string _pipeName = "";
  bool _isConnecting = false;
  bool _isConnected = false;
  ArrayPool<byte> _rawDataBufferPool = ArrayPool<byte>.Create();

  public ArrayPool<byte> RawDataBufferPool
  {
    get => _rawDataBufferPool;
  }
  
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

  public bool IsConnected
  {
    get => _isConnected;
  }

  public void Connect(string hostname, int port)
  {
    Task.Run(() => ConnectAsync(hostname, port));
  }

  public async Task ConnectAsync(string hostname, int port)
  {
    if (_isConnecting || _isConnected)
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
      var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
      try
      {
        Module.Log($"Connecting to {_targetHostname}:{_targetPort}...", ObsLogLevel.Debug);
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
      _isConnected = true;
      _processDataLoopTask = processDataLoopAsync(socket, null, _cancellationSource.Token);
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
    if (_isConnecting || _isConnected)
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
      catch (System.Exception ex)
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
      _isConnected = true;
      _processDataLoopTask = processDataLoopAsync(null, pipeStream, _cancellationSource.Token);
      break;
    }
    _isConnecting = false;
  }

  public void Disconnect()
  {
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
    catch (System.Exception ex)
    {
      Module.Log($"{ex.GetType().Name} while disconnecting from Beam: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  public delegate Task AsyncEventHandler<TEventArgs>(object? sender, TEventArgs e);

  public event AsyncEventHandler<Beam.BeamVideoData>? VideoFrameReceived;
  async void OnVideoFrameReceived(Beam.BeamVideoData frame)
  {
    if (VideoFrameReceived is not null)
      await VideoFrameReceived(this, frame);
  }

  public event AsyncEventHandler<Beam.BeamAudioData>? AudioFrameReceived;
  async void OnAudioFrameReceived(Beam.BeamAudioData frame)
  {
    if (AudioFrameReceived is not null)
      await AudioFrameReceived(this, frame);
  }

  public event EventHandler<EventArgs>? Disconnected;
  void OnDisconnected()
  {
    Task.Run(() => Disconnected?.Invoke(this, EventArgs.Empty));
  }

  private async Task processDataLoopAsync(Socket? socket, NamedPipeClientStream? pipeStream, CancellationToken cancellationToken)
  {
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

    Dictionary<ulong, Beam.BeamVideoData> videoDataCache = new Dictionary<ulong, Beam.BeamVideoData>();
    Queue<ulong> videoDataCacheOrder = new Queue<ulong>();
      
    uint fps = 30;
    uint logCycle = 0;

    ulong lastVideoTimestamp = 0;
    ulong lastAudioTimestamp = 0;

    int maxVideoDataSize = 0;
    byte[] receivedFrameData = Array.Empty<byte>();
    byte[] lz4DecompressBuffer = Array.Empty<byte>();

    lock (_sizeLock)
    {
      if ((_width > 0) && (_height > 0)) // could still be set from a previous run, in this case use this information to initialize the buffers already
      {
        maxVideoDataSize = (int)(_width * _height * 4);
        receivedFrameData = new byte[maxVideoDataSize];
        lz4DecompressBuffer = new byte[maxVideoDataSize];
      }
    }

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

        var beamType = Beam.GetBeamType(readResult.Buffer);
        if (beamType == Beam.Type.VideoInfo)
        {
          pipeReader.AdvanceTo(videoHeader.FromSequence(readResult.Buffer), readResult.Buffer.End);
          videoDataCacheOrder.Enqueue(videoHeader.Timestamp); // remember the order of the video frames
        }
        else if (beamType == Beam.Type.Video)
        {
          // read and validate header information
          pipeReader.AdvanceTo(videoHeader.FromSequence(readResult.Buffer), readResult.Buffer.End);

          if (videoHeader.Fps > 0)
            fps = videoHeader.Fps;
          var headerDiff = videoHeader.Timestamp - lastVideoTimestamp;
          if (logCycle >= fps)
            Module.Log($"Video data: Received header {videoHeader.Timestamp}, diff: {headerDiff}", ObsLogLevel.Debug);
          if (videoHeader.Timestamp > lastVideoTimestamp)
            lastVideoTimestamp = videoHeader.Timestamp;
          if (videoHeader.DataSize <= 0)
          {
            Module.Log($"Video data: Header reports invalid data size of {videoHeader.DataSize} bytes, aborting connection.", ObsLogLevel.Error);
            break;
          }

          // read video data
          if (videoHeader.DataSize > 0)
          {
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
            if (sizeChanged) // re-allocate the arrays matching the new necessary size
            {
              maxVideoDataSize = (int)(videoHeader.Width * videoHeader.Height * 4);
              receivedFrameData = new byte[maxVideoDataSize];
              lz4DecompressBuffer = new byte[maxVideoDataSize];
              _rawDataBufferPool = ArrayPool<byte>.Create(maxVideoDataSize, 2); // in ideal case one that is still being processed by the async event handler and another one that is used here
            }

            var rawDataBuffer = _rawDataBufferPool.Rent(maxVideoDataSize);
            if (videoHeader.Compression == Beam.CompressionTypes.None)
              readResult.Buffer.Slice(0, videoHeader.DataSize).CopyTo(rawDataBuffer);
            else
            {
              readResult.Buffer.Slice(0, videoHeader.DataSize).CopyTo(receivedFrameData);

              // need to decompress both LZ4 and QOI
              if (videoHeader.Compression == Beam.CompressionTypes.QoiLz4)
              {
                // decompress LZ4 first
                int decompressedSize = LZ4Codec.Decode(receivedFrameData, 0, videoHeader.DataSize, lz4DecompressBuffer, 0, videoHeader.QoiDataSize);
                if (decompressedSize != videoHeader.QoiDataSize)
                  Module.Log($"LZ4 decompression failed, expected {videoHeader.QoiDataSize} bytes (QOI), got {decompressedSize} bytes.", ObsLogLevel.Error);

                // now decompress QOI
                Qoi.Decode(lz4DecompressBuffer, videoHeader.QoiDataSize, rawDataBuffer, maxVideoDataSize);

              }
              // need to decompress LZ4 only
              else if (videoHeader.Compression == Beam.CompressionTypes.Lz4)
              {
                int decompressedSize = LZ4Codec.Decode(receivedFrameData, 0, videoHeader.DataSize, rawDataBuffer, 0, maxVideoDataSize);
                if (decompressedSize != maxVideoDataSize)
                  Module.Log($"LZ4 decompression failed, expected {maxVideoDataSize} bytes, got {decompressedSize} bytes.", ObsLogLevel.Error);
              }
              // need to decompress QOI only
              else if (videoHeader.Compression == Beam.CompressionTypes.Qoi)
                Qoi.Decode(receivedFrameData, videoHeader.DataSize, rawDataBuffer, maxVideoDataSize);
            }

            var beamVideoData = new Beam.BeamVideoData(videoHeader, rawDataBuffer);
            if ((videoDataCacheOrder.Count == 0) || videoHeader.Timestamp == videoDataCacheOrder.Peek())
            {
              // tell the sender the current frame timestamp that is processed
              var timestampBuffer = pipeWriter.GetMemory(8);
              BinaryPrimitives.WriteUInt64LittleEndian(timestampBuffer.Span, videoHeader.Timestamp);
              pipeWriter.Advance(8);
              var writeResult = await pipeWriter.FlushAsync(cancellationToken);
              if (writeResult.IsCanceled || writeResult.IsCompleted)
              {
                if (writeResult.IsCanceled)
                  Module.Log("processDataLoopAsync() exit from sending through cancellation.", ObsLogLevel.Debug);
                else
                  Module.Log("processDataLoopAsync() exit from sending through completion.", ObsLogLevel.Debug);
                break;
              }
              
              // process the frame
              OnVideoFrameReceived(beamVideoData);
              if (videoDataCacheOrder.Count > 0)
                videoDataCacheOrder.Dequeue();
            }
            else // the frame that was just received is not the next one to be displayed, so cache it
            {
              videoDataCache.Add(videoHeader.Timestamp, beamVideoData);
              Module.Log($"Video data: Received frame {videoHeader.Timestamp} out of order, queued ({videoDataCache.Count}).", ObsLogLevel.Debug);
            }

            // check if there are more frames in the cache that can be processed now that the current frame is out of the way
            while ((videoDataCacheOrder.Count > 0) && videoDataCache.ContainsKey(videoDataCacheOrder.Peek()))
            {
              beamVideoData = videoDataCache[videoDataCacheOrder.Dequeue()];
              Module.Log($"Video data: Processing frame {beamVideoData.Header.Timestamp} from cache ({videoDataCache.Count}).", ObsLogLevel.Debug);
              videoDataCache.Remove(beamVideoData.Header.Timestamp);
              OnVideoFrameReceived(beamVideoData);
              
              // tell the sender the cached frame timestamp that is processed
              var timestampBuffer = pipeWriter.GetMemory(8);
              BinaryPrimitives.WriteUInt64LittleEndian(timestampBuffer.Span, videoHeader.Timestamp);
              pipeWriter.Advance(8);
              var writeResult = await pipeWriter.FlushAsync(cancellationToken);
              if (writeResult.IsCanceled || writeResult.IsCompleted)
              {
                if (writeResult.IsCanceled)
                  Module.Log("processDataLoopAsync() exit from sending through cancellation.", ObsLogLevel.Debug);
                else
                  Module.Log("processDataLoopAsync() exit from sending through completion.", ObsLogLevel.Debug);
                break;
              }
            }

            long receiveLength = readResult.Buffer.Length; // remember this here, before the buffer is invalidated with the next line
            pipeReader.AdvanceTo(readResult.Buffer.GetPosition(videoHeader.DataSize), readResult.Buffer.End);

            if (logCycle >= fps)
              Module.Log($"Video data: Expected {videoHeader.DataSize} bytes, received {receiveLength} bytes", ObsLogLevel.Debug);

          }
          else if (videoHeader.DataSize == 0)
            Module.Log($"Sender skipped video frame {videoHeader.Timestamp}.", ObsLogLevel.Warning);
          else
          {
            Module.Log($"Video data: Received invalid header, aborting connection.", ObsLogLevel.Error);
            break;
          }
          if (logCycle++ >= fps)
            logCycle = 0;
        }
        else if (beamType == Beam.Type.Audio)
        {
          // read and validate header information
          pipeReader.AdvanceTo(audioHeader.FromSequence(readResult.Buffer), readResult.Buffer.End);

          var headerDiff = audioHeader.Timestamp - lastAudioTimestamp;
          if (logCycle >= fps)
            Module.Log($"Audio data: Received header {audioHeader.Timestamp}, diff: {headerDiff}", ObsLogLevel.Debug);
          if (audioHeader.Timestamp > lastAudioTimestamp)
            lastAudioTimestamp = audioHeader.Timestamp;
          else
          {
            Module.Log($"Audio data: Received frame {audioHeader.Timestamp} out of order, expected > {lastAudioTimestamp}.", ObsLogLevel.Warning);
            break;
          }

          if (audioHeader.DataSize <= 0)
            Module.Log($"Audio data: Header reports invalid data size of {audioHeader.DataSize} bytes", ObsLogLevel.Error);

          // read audio data
          if (audioHeader.DataSize > 0)
          {
            readResult = await pipeReader.ReadAtLeastAsync(audioHeader.DataSize);
            if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
            {
              if (readResult.IsCanceled)
                Module.Log("processDataLoopAsync() exit from reading audio data through cancellation.", ObsLogLevel.Debug);
              else
                Module.Log("processDataLoopAsync() exit from reading audio data through completion.", ObsLogLevel.Debug);
              break;
            }

            long receiveLength = readResult.Buffer.Length; // remember this here, before the buffer is invalidated with the next line
            OnAudioFrameReceived(new Beam.BeamAudioData(audioHeader, readResult.Buffer.Slice(0, audioHeader.DataSize).ToArray()));

            pipeReader.AdvanceTo(readResult.Buffer.GetPosition(audioHeader.DataSize), readResult.Buffer.End);

            if (logCycle >= fps)
              Module.Log($"Audio data: Expected {audioHeader.DataSize} bytes, received {receiveLength} bytes", ObsLogLevel.Debug);

          }
          else if (audioHeader.DataSize == 0)
            Module.Log($"Sender skipped audio frame {audioHeader.Timestamp}.", ObsLogLevel.Warning);
          else
          {
            Module.Log($"Audio data: Received invalid header, aborting connection.", ObsLogLevel.Error);
            break;
          }
        }
        else
        {
          Module.Log($"Received unknown header type ({beamType}), aborting connection.", ObsLogLevel.Error);
          break;
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
      catch (System.Exception ex)
      {
        Module.Log($"{ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        pipeReader.Complete(ex);
        break;
      }
    }

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
    stream?.Close();
    _isConnected = false;
    OnDisconnected();
  }

}
