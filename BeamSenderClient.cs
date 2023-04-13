// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;

namespace xObsBeam;

class BeamSenderClient
{
  Socket? _socket;
  NamedPipeServerStream? _pipeStream;
  Stream? _stream;
  ConcurrentQueue<Beam.IBeamData> _frameQueue = new ConcurrentQueue<Beam.IBeamData>();
  AutoResetEvent _frameAvailable = new AutoResetEvent(false);
  long _videoFrameCount = -1;
  long _audioFrameCount = -1;
  CancellationTokenSource _cancellationSource = new CancellationTokenSource();
  string _clientId = "";
  ArrayPool<byte> _videoDataPool;
  Beam.AudioHeader _audioHeader;
  ArrayPool<byte> _audioDataPool;
  DateTime _lastFrameTime = DateTime.MaxValue;

  public string ClientId { get => _clientId; }

  public BeamSenderClient(string clientId, Socket socket, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    _clientId = clientId;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, BeamSender.MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, BeamSender.MaxFrameQueueSize * 2);
    Module.Log($"<{_clientId}> New client connected.", ObsLogLevel.Info);
    _socket = socket;
  }

  public BeamSenderClient(string clientId, NamedPipeServerStream pipeStream, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    _clientId = clientId;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, BeamSender.MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, BeamSender.MaxFrameQueueSize * 2);
    Module.Log($"<{_clientId}> New client connected.", ObsLogLevel.Info);
    _pipeStream = pipeStream;
  }

  public void Start()
  {
    if (_socket != null)
      _stream = new NetworkStream(_socket);
    else if (_pipeStream != null)
      _stream = _pipeStream;
    else
      throw new InvalidOperationException("No socket or pipe stream available.");
    _ = Task.Run(() => sendLoopAsync(PipeWriter.Create(_stream), _cancellationSource.Token));
    _ = Task.Run(() => receiveLoopAsync(PipeReader.Create(_stream), _cancellationSource.Token));
  }

  public void Disconnect()
  {
    Module.Log($"<{_clientId}> Disconnecting client...", ObsLogLevel.Info);
    _cancellationSource.Cancel();
    _frameAvailable.Set();
  }

  public event EventHandler<EventArgs>? Disconnected;

  void OnDisconnected()
  {
    Task.Run(() => Disconnected?.Invoke(this, EventArgs.Empty));
  }

  async Task checkReceiverAliveLoopAsync(CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        await Task.Delay(1000, cancellationToken);
        if (_lastFrameTime < DateTime.UtcNow.AddSeconds(-1))
        {
          Module.Log($"<{_clientId}> Receiver timeout.", ObsLogLevel.Error);
          Disconnect();
          break;
        }
      }
    }
    catch (OperationCanceledException ex)
    {
      Module.Log($"<{_clientId}> checkReceiverAliveLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
    }
    catch (System.Exception ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
    }
  }

  async Task receiveLoopAsync(PipeReader pipeReader, CancellationToken cancellationToken)
  {
    /*
    One reason to have this method is because the mere existence of an actively used receiving channel on the underlying socket ensures proper disconnect detection also for the sender.
    Another is that it is used to implement timeout detection for cases where the connection is still open but the receiver is not reading data anymore.
    As a side effect the frame timestamp information from the receiver can also be useful for debugging purposes.
    */
    if (pipeReader == null)
      return;
    try
    {
      Module.Log($"<{_clientId}> receiveLoopAsync() started.", ObsLogLevel.Debug);
      _lastFrameTime = DateTime.UtcNow;
      _ = Task.Run(() => checkReceiverAliveLoopAsync(cancellationToken));
      while (!cancellationToken.IsCancellationRequested)
      {
        ulong timestamp;
        ReadResult readResult = await pipeReader.ReadAtLeastAsync(8, cancellationToken);
        if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
        {
          if (readResult.IsCanceled)
            Module.Log($"<{_clientId}> receiveLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
          else
          {
            Module.Log($"<{_clientId}> receiveLoopAsync() exit through completion.", ObsLogLevel.Debug);
            _cancellationSource.Cancel();
            _frameAvailable.Set();
          }
          break;
        }
        pipeReader.AdvanceTo(Beam.GetTimestamp(readResult.Buffer, out timestamp), readResult.Buffer.End);
        _lastFrameTime = DateTime.UtcNow;

        // Module.Log($"<{_clientId}> Receiver is at video timestamp {timestamp}.", ObsLogLevel.Debug);
      }
    }
    catch (OperationCanceledException ex)
    {
      Module.Log($"<{_clientId}> receiveLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
    }
    catch (System.Exception ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
    }
    try { pipeReader.Complete(); } catch { }
  }

  async Task sendLoopAsync(PipeWriter pipeWriter, CancellationToken cancellationToken)
  {
    /*
    We want to send data out as fast as possible, however, a small queue is still needed for two reasons:
    1. The main rendering thread should be blocked as short as possible, we don't want to wait for network functions.
    2. The PipeWriter workflow ends with "FlushAsync" (implicitly through WriteAsync). Since this call is async it could still be busy while the next frame is already being processed (and in tests this indeed has occasionally happened).
       As part of this processing the next call to PipeWriter.GetMemory() would be made, but: "Calling GetMemory or GetSpan while there's an incomplete call to FlushAsync isn't safe.", see:
       https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines#pipewriter-common-problems
    */

    if (pipeWriter == null)
      return;
    try
    {
      uint fps = 30;
      uint frameCycle = 1;
      ulong totalBytes = 0;
      bool pipeWriterComplete = true;
      _frameQueue.Clear();

      Module.Log($"<{_clientId}> sendLoopAsync() started.", ObsLogLevel.Debug);
      Interlocked.Increment(ref _videoFrameCount); // this first increment from -1 to 0 signalizes that the send loop is ready to process the queue
      Interlocked.Increment(ref _audioFrameCount); // this first increment from -1 to 0 signalizes that the send loop is ready to process the queue
      while (!cancellationToken.IsCancellationRequested)
      {
        // wait for the next signal if the queue is empty right now
        _frameAvailable.WaitOne();
        if (cancellationToken.IsCancellationRequested)
          break;
        if (_frameQueue.TryDequeue(out var frame))
        {
          if (frame is Beam.BeamVideoData videoFrame)
          {
            Interlocked.Decrement(ref _videoFrameCount);

            if ((videoFrame.Header.Fps > 0) && (fps != videoFrame.Header.Fps))
            {
              totalBytes = 0;
              frameCycle = 1;
              fps = videoFrame.Header.Fps;
            }

            // write video data
            try
            {
              // write video header data
              var headerBytes = videoFrame.Header.WriteTo(pipeWriter.GetMemory(Beam.VideoHeader.VideoHeaderDataSize).Span, videoFrame.Timestamp);
              pipeWriter.Advance(headerBytes);

              // write video frame data - need to slice videoFrame.Data, since the arrays we get from _videoDataPool are often bigger than what we requested
              var writeResult = await pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(videoFrame.Data).Slice(0, videoFrame.Header.DataSize), cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
              _videoDataPool.Return(videoFrame.Data); // return video frame data to the memory pool

              totalBytes += (ulong)headerBytes + (ulong)videoFrame.Header.DataSize;
              if (frameCycle >= fps)
              {
                var mBitsPerSecond = (totalBytes * 8) / 1000000;
                Module.Log($"<{_clientId}> Sent {headerBytes} + {videoFrame.Header.DataSize} bytes of video data ({mBitsPerSecond} mbps), queue length: {_frameQueue.Count()}", ObsLogLevel.Debug);
                totalBytes = 0;
              }
              if (writeResult.IsCanceled || writeResult.IsCompleted)
              {
                if (writeResult.IsCanceled)
                  Module.Log($"<{_clientId}> sendLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
                else
                  Module.Log($"<{_clientId}> sendLoopAsync() exit through completion.", ObsLogLevel.Debug);
                break;
              }

            }
            catch (OperationCanceledException ex)
            {
              // happens when cancellation token is signalled
              Module.Log($"<{_clientId}> sendLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
              break;
            }
            catch (IOException ex)
            {
              // happens when the receiver closes the connection
              Module.Log($"<{_clientId}> Lost connection to receiver ({ex.GetType().Name}) while trying to send video data.", ObsLogLevel.Error);
              pipeWriterComplete = false; // this would internally try to flush and by this throw another exception
              break;
            }
            catch (System.Exception ex)
            {
              Module.Log($"<{_clientId}> sendLoopAsync(): {ex.GetType().Name} sending video data: {ex.Message}", ObsLogLevel.Error);
              break;
            }
            if (frameCycle++ >= fps)
              frameCycle = 1;
          }
          else if (frame is Beam.BeamAudioData audioFrame)
          {
            long audioFrameCount = Interlocked.Decrement(ref _audioFrameCount);

            // write audio data
            try
            {
              // write audio header data
              var headerBytes = audioFrame.Header.WriteTo(pipeWriter.GetMemory(Beam.AudioHeader.AudioHeaderDataSize).Span, audioFrame.Timestamp);
              pipeWriter.Advance(headerBytes);

              // write audio frame data - need to slice audioFrame.Data, since the arrays we get from the shared ArrayPool are often bigger than what we requested
              var writeResult = await pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(audioFrame.Data).Slice(0, audioFrame.Header.DataSize), cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
              ArrayPool<byte>.Shared.Return(audioFrame.Data); // return audio frame data to the memory pool
              if (frameCycle >= fps)
                Module.Log($"<{_clientId}> Sent {headerBytes} + {audioFrame.Header.DataSize} bytes of audio data, queue length: {audioFrameCount}", ObsLogLevel.Debug);
              if (writeResult.IsCanceled || writeResult.IsCompleted)
              {
                if (writeResult.IsCanceled)
                  Module.Log($"<{_clientId}> sendLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
                else
                  Module.Log($"<{_clientId}> sendLoopAsync() exit through completion.", ObsLogLevel.Debug);
                break;
              }
            }
            catch (OperationCanceledException ex)
            {
              Module.Log($"<{_clientId}> sendLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
              break;
            }
            catch (IOException ex)
            {
              // happens when the receiver closes the connection
              Module.Log($"<{_clientId}> Lost connection to receiver ({ex.GetType().Name}) while trying to send audio data.", ObsLogLevel.Error);
              pipeWriterComplete = false; // this would internally try to flush and by this throw another exception
              break;
            }
            catch (System.Exception ex)
            {
              Module.Log($"<{_clientId}> sendLoopAsync(): {ex.GetType().Name} sending audio data: {ex.Message}", ObsLogLevel.Error);
              break;
            }
          }

          if (_frameQueue.Count() > 0)
            _frameAvailable.Set(); // make sure the send loop continues immediately if there is more data in the queue
        }
      }
      if (pipeWriterComplete)
        pipeWriter.Complete();
    }
    catch (System.Exception ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} in send loop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      try { pipeWriter.Complete(ex); } catch { }
    }

    Module.Log($"<{_clientId}> sendLoopAsync(): Exiting...", ObsLogLevel.Debug);
    if (!_cancellationSource.IsCancellationRequested) // make sure that also all other loops exit now
      _cancellationSource.Cancel();
    _frameQueue.Clear();
    if (_socket != null)
    {
      _socket.Shutdown(SocketShutdown.Both);
      _socket.Disconnect(false);
      if (_socket.Connected)
        _socket.Close();
    }
    _stream?.Close();
    OnDisconnected();
    Module.Log($"<{_clientId}> Disconnected.", ObsLogLevel.Info);
  }

  public unsafe void Enqueue(ulong timestamp, Beam.VideoHeader videoHeader, byte[] videoData)
  {
    long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    if (videoFrameCount > 5)
    {
      int frameSleepTime = (int)Math.Ceiling((1 / (double)videoHeader.Fps * 1000));
      Module.Log($"<{_clientId}> Error: Send queue size {videoFrameCount} ({_frameQueue.Count}), skipping video frame {timestamp}.", ObsLogLevel.Error);
      Module.Log($"<{_clientId}> Blocking the OBS rendering pipeline for {frameSleepTime} ms.", ObsLogLevel.Debug);
      // intentionally block the OBS rendering pipeline for a full frame, making the skipped/lagged frames visible also in OBS stats so that it becomes transparent to the user
      Thread.Sleep(frameSleepTime);
      return;
    }
    else if (videoFrameCount > 1)
      Module.Log($"<{_clientId}> Warning: Send queue size {videoFrameCount} ({_frameQueue.Count}) at video frame {timestamp}.", ObsLogLevel.Warning);
    else if (videoFrameCount < 0)
    {
      Module.Log($"<{_clientId}> Send queue not ready at video frame {timestamp}.", ObsLogLevel.Debug);
      return;
    }

    Beam.BeamVideoData frame = new Beam.BeamVideoData(videoHeader, _videoDataPool.Rent(videoHeader.DataSize), timestamp);
    videoData.AsSpan<byte>(0, videoHeader.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes
    _frameQueue.Enqueue(frame);
    Interlocked.Increment(ref _videoFrameCount);
    _frameAvailable.Set();
  }

  public unsafe void Enqueue(ulong timestamp, Beam.VideoHeader videoHeader, byte* videoData)
  {
    long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    if (videoFrameCount > 5)
    {
      int frameSleepTime = (int)Math.Ceiling((1 / (double)videoHeader.Fps * 1000));
      Module.Log($"<{_clientId}> Error: Send queue size {videoFrameCount} ({_frameQueue.Count}), skipping video frame {timestamp}.", ObsLogLevel.Error);
      Module.Log($"<{_clientId}> Blocking the OBS rendering pipeline for {frameSleepTime} ms.", ObsLogLevel.Debug);
      // intentionally block the OBS rendering pipeline for a full frame, making the skipped/lagged frames visible also in OBS stats so that it becomes transparent to the user
      Thread.Sleep(frameSleepTime);
      return;
    }
    else if (videoFrameCount > 1)
      Module.Log($"<{_clientId}> Warning: Send queue size {videoFrameCount} ({_frameQueue.Count}) at video frame {timestamp}.", ObsLogLevel.Warning);
    else if (videoFrameCount < 0)
    {
      Module.Log($"<{_clientId}> Send queue not ready at video frame {timestamp}.", ObsLogLevel.Debug);
      return;
    }

    Beam.BeamVideoData frame = new Beam.BeamVideoData(videoHeader, _videoDataPool.Rent(videoHeader.DataSize), timestamp);
    new Span<byte>(videoData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes
    _frameQueue.Enqueue(frame);
    Interlocked.Increment(ref _videoFrameCount);
    _frameAvailable.Set();
  }

  public unsafe void Enqueue(ulong timestamp, byte* audioData, int speakers, int audioBytesPerSample)
  {
    long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    if (videoFrameCount > BeamSender.MaxFrameQueueSize)
    {
      Module.Log($"<{_clientId}> Error: Send queue size {videoFrameCount} ({_frameQueue.Count}), skipping audio frame {timestamp}.", ObsLogLevel.Error);
      return;
    }
    else if (videoFrameCount < 0)
    {
      Module.Log($"<{_clientId}> Send queue not ready at audio frame {timestamp}.", ObsLogLevel.Debug);
      return;
    }

    Beam.BeamAudioData frame = new Beam.BeamAudioData(_audioHeader, _audioDataPool.Rent(_audioHeader.DataSize), timestamp); // get an audio data memory buffer from the pool, avoiding allocations
    new Span<byte>(audioData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes

    _frameQueue.Enqueue(frame);
    Interlocked.Increment(ref _audioFrameCount);
    _frameAvailable.Set();
  }



}
