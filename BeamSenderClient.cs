// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;

namespace xObsBeam;

sealed class BeamSenderClient
{
  readonly Socket? _socket;
  readonly NamedPipeServerStream? _pipeStream;
  Stream? _stream;
  readonly ConcurrentQueue<ulong> _frameTimestampQueue = new();
  readonly ConcurrentDictionary<ulong, Beam.IBeamData> _frames = new();
  long _receiveDelayMs;
  long _renderDelayMs;
  readonly AutoResetEvent _frameAvailable = new(false);
  long _videoFrameCount = -1;
  long _audioFrameCount = -1;
  readonly CancellationTokenSource _cancellationSource = new();
  readonly ArrayPool<byte> _videoDataPool;
  Beam.AudioHeader _audioHeader;
  readonly ArrayPool<byte> _audioDataPool;
  DateTime _lastFrameTime = DateTime.MaxValue;
  readonly ManualResetEvent _sendLoopExited = new(false);

  public string ClientId { get; } = "";

  public BeamSenderClient(string clientId, Socket socket, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    ClientId = clientId;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, BeamSender.MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, BeamSender.MaxFrameQueueSize * 2);
    Module.Log($"<{ClientId}> New client connected.", ObsLogLevel.Info);
    _socket = socket;
  }

  public BeamSenderClient(string clientId, NamedPipeServerStream pipeStream, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    ClientId = clientId;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, BeamSender.MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, BeamSender.MaxFrameQueueSize * 2);
    Module.Log($"<{ClientId}> New client connected.", ObsLogLevel.Info);
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
    _ = Task.Run(() => SendLoopAsync(PipeWriter.Create(_stream), _cancellationSource.Token));
    _ = Task.Run(() => ReceiveLoopAsync(PipeReader.Create(_stream), _cancellationSource.Token));
  }

  public void Disconnect(int blockingTimeout = 1000)
  {
    Module.Log($"<{ClientId}> Disconnecting client...", ObsLogLevel.Info);
    _cancellationSource.Cancel();
    _frameAvailable.Set();
    if (!_sendLoopExited.WaitOne(blockingTimeout))
      Module.Log($"<{ClientId}> Disconnecting client timed out.", ObsLogLevel.Error);
  }

  public event EventHandler<EventArgs>? Disconnected;

  void OnDisconnected()
  {
    Task.Run(() => Disconnected?.Invoke(this, EventArgs.Empty));
  }

  async Task CheckReceiverAliveLoopAsync(CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        await Task.Delay(1000, cancellationToken);
        if (_lastFrameTime < DateTime.UtcNow.AddSeconds(-1))
        {
          Module.Log($"<{ClientId}> Receiver timeout.", ObsLogLevel.Error);
          Disconnect();
          break;
        }
      }
    }
    catch (OperationCanceledException ex)
    {
      Module.Log($"<{ClientId}> checkReceiverAliveLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
    }
    catch (Exception ex)
    {
      Module.Log($"<{ClientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
    }
  }

  async Task ReceiveLoopAsync(PipeReader pipeReader, CancellationToken cancellationToken)
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
      Module.Log($"<{ClientId}> receiveLoopAsync() started.", ObsLogLevel.Debug);
      _lastFrameTime = DateTime.UtcNow;
      _ = Task.Run(() => CheckReceiverAliveLoopAsync(cancellationToken), cancellationToken);
      while (!cancellationToken.IsCancellationRequested)
      {
        ReadResult readResult = await pipeReader.ReadAtLeastAsync(sizeof(byte) + sizeof(ulong), cancellationToken);
        if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
        {
          if (readResult.IsCanceled)
            Module.Log($"<{ClientId}> receiveLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
          else
          {
            Module.Log($"<{ClientId}> receiveLoopAsync() exit through completion.", ObsLogLevel.Debug);
            _cancellationSource.Cancel();
            _frameAvailable.Set();
          }
          break;
        }
        pipeReader.AdvanceTo(Beam.GetReceiveTimestamp(readResult.Buffer, out Beam.ReceiveTimestampTypes receiveTimestampType, out ulong timestamp), readResult.Buffer.End);
        _lastFrameTime = DateTime.UtcNow;
        if (receiveTimestampType == Beam.ReceiveTimestampTypes.Receive)
        {
          var receiveDelayMs = (ObsInterop.Obs.obs_get_video_frame_time() - timestamp) / 1_000_000;
          // Module.Log($"<{ClientId}> Receiver received video frame {timestamp} with a delay of {receiveDelayMs} ms", ObsLogLevel.Debug);
          Interlocked.Exchange(ref _receiveDelayMs, (long)receiveDelayMs);
        }
        else if (receiveTimestampType == Beam.ReceiveTimestampTypes.Render)
        {
          var renderDelayNs = (ObsInterop.Obs.obs_get_video_frame_time() - timestamp) / 1_000_000;
          // Module.Log($"<{ClientId}> Receiver rendered video frame {timestamp} with a delay of {renderDelayNs} ms", ObsLogLevel.Debug);
          Interlocked.Exchange(ref _renderDelayMs, (long)renderDelayNs);
        }
      }
    }
    catch (OperationCanceledException ex)
    {
      Module.Log($"<{ClientId}> receiveLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
    }
    catch (Exception ex)
    {
      Module.Log($"<{ClientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
    }
    try { pipeReader.Complete(); } catch { }
  }

  async Task SendLoopAsync(PipeWriter pipeWriter, CancellationToken cancellationToken)
  {
    /*
    We want to send data out as fast as possible, however, a small queue is still needed for two reasons:
    1. The main rendering thread should be blocked as short as possible, we don't want to wait for network functions.
    2. The PipeWriter workflow ends with "FlushAsync" (implicitly through WriteAsync). Since this call is async it could still be busy while the next frame is already being processed (and in tests this indeed has occasionally happened).
       As part of this processing the next call to PipeWriter.GetMemory() would be made, but: "Calling GetMemory or GetSpan while there's an incomplete call to FlushAsync isn't safe.", see:
       https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines#pipewriter-common-problems
    3. If compression is done asynchonously, frames could be finished by their worker threads in the wrong order, the queue gives enough time to collect all frames before sending them out in the correct order.
    */
    try
    {
      try
      {
        if (pipeWriter == null)
          return;
        double fps = 30;
        uint frameCycle = 1;
        ulong totalBytes = 0;
        bool pipeWriterComplete = true;
        _frameTimestampQueue.Clear();
        _frames.Clear();
        _receiveDelayMs = 0;
        _renderDelayMs = 0;

        var stopwatch = new System.Diagnostics.Stopwatch();
        long waitTime = 0;
        long dequeueTime = 0;
        long sendVideoTime = 0;
        long sendAudioTime = 0;

        Module.Log($"<{ClientId}> sendLoopAsync() started.", ObsLogLevel.Debug);
        Interlocked.Increment(ref _videoFrameCount); // this first increment from -1 to 0 signalizes that the send loop is ready to process the queue
        Interlocked.Increment(ref _audioFrameCount); // this first increment from -1 to 0 signalizes that the send loop is ready to process the queue
        while (!cancellationToken.IsCancellationRequested)
        {
          stopwatch.Reset();
          stopwatch.Start();
          // wait for the next signal if the queue is empty right now
          _frameAvailable.WaitOne();
          // waitTime += (long)stopwatch.Elapsed.TotalNanoseconds;
          waitTime += stopwatch.ElapsedTicks;
          stopwatch.Reset();
          stopwatch.Start();
          if (cancellationToken.IsCancellationRequested)
            break;

          if (_frameTimestampQueue.TryPeek(out var frameTimestamp) && (_frames.TryRemove(frameTimestamp, out var frame))) // is the next queued frame already available?
          {
            _frameTimestampQueue.TryDequeue(out _);
            dequeueTime += stopwatch.ElapsedTicks;
            stopwatch.Reset();
            stopwatch.Start();
            if (frame is Beam.BeamVideoData videoFrame)
            {
              var videoFrameCount = Interlocked.Decrement(ref _videoFrameCount);
              if ((videoFrame.Header.Fps > 0) && (fps != (videoFrame.Header.Fps / videoFrame.Header.FpsDenominator)))
              {
                totalBytes = 0;
                frameCycle = 1;
                fps = (videoFrame.Header.Fps / videoFrame.Header.FpsDenominator);

                waitTime = 0;
                dequeueTime = 0;
                sendVideoTime = 0;
                sendAudioTime = 0;
              }

              // write video data
              try
              {
                // get current delay values to add to the header for sending
                var receiveDelayMs = (int)Interlocked.Read(ref _receiveDelayMs);
                var renderDelayMs = (int)Interlocked.Read(ref _renderDelayMs);

                // write video header data
                var headerBytes = videoFrame.Header.WriteTo(pipeWriter.GetSpan(Beam.VideoHeader.VideoHeaderDataSize), videoFrame.Timestamp, receiveDelayMs, renderDelayMs);
                pipeWriter.Advance(headerBytes);

                // write video frame data - need to slice videoFrame.Data, since the arrays we get from _videoDataPool are often bigger than what we requested
                var writeResult = await pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(videoFrame.Data)[..videoFrame.Header.DataSize], cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
                _videoDataPool.Return(videoFrame.Data); // return video frame data to the memory pool

                sendVideoTime += stopwatch.ElapsedTicks;
                stopwatch.Reset();
                stopwatch.Start();

                totalBytes += (ulong)headerBytes + (ulong)videoFrame.Header.DataSize;
                if (frameCycle >= fps)
                {
                  var mBitsPerSecond = (totalBytes * 8) / 1000000;
                  Module.Log($"<{ClientId}> Sent {headerBytes} + {videoFrame.Header.DataSize} bytes of video data ({mBitsPerSecond} mbps), queue length: {videoFrameCount} ({_frameTimestampQueue.Count})", ObsLogLevel.Debug);
                  totalBytes = 0;

                  Module.Log($"<{ClientId}> Time stats: wait {waitTime}, dequeue {dequeueTime}, send video {sendVideoTime}, send audio {sendAudioTime}", ObsLogLevel.Debug);
                  waitTime = 0;
                  dequeueTime = 0;
                  sendVideoTime = 0;
                  sendAudioTime = 0;
                }
                if (writeResult.IsCanceled || writeResult.IsCompleted)
                {
                  if (writeResult.IsCanceled)
                    Module.Log($"<{ClientId}> sendLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
                  else
                    Module.Log($"<{ClientId}> sendLoopAsync() exit through completion.", ObsLogLevel.Debug);
                  break;
                }
              }
              catch (OperationCanceledException ex)
              {
                // happens when cancellation token is signalled
                Module.Log($"<{ClientId}> sendLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
                break;
              }
              catch (IOException ex)
              {
                // happens when the receiver closes the connection
                Module.Log($"<{ClientId}> Lost connection to receiver ({ex.GetType().Name}) while trying to send video data.", ObsLogLevel.Error);
                pipeWriterComplete = false; // this would internally try to flush and by this throw another exception
                break;
              }
              catch (Exception ex)
              {
                Module.Log($"<{ClientId}> sendLoopAsync(): {ex.GetType().Name} sending video data: {ex.Message}", ObsLogLevel.Error);
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
                var headerBytes = audioFrame.Header.WriteTo(pipeWriter.GetSpan(Beam.AudioHeader.AudioHeaderDataSize), audioFrame.Timestamp);
                pipeWriter.Advance(headerBytes);

                // write audio frame data - need to slice audioFrame.Data, since the arrays we get from the shared ArrayPool are often bigger than what we requested
                var writeResult = await pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(audioFrame.Data)[..audioFrame.Header.DataSize], cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
                ArrayPool<byte>.Shared.Return(audioFrame.Data); // return audio frame data to the memory pool
                if (frameCycle >= fps)
                  Module.Log($"<{ClientId}> Sent {headerBytes} + {audioFrame.Header.DataSize} bytes of audio data, queue length: {audioFrameCount} ({_frameTimestampQueue.Count})", ObsLogLevel.Debug);
                sendAudioTime += stopwatch.ElapsedTicks;
                stopwatch.Reset();
                stopwatch.Start();
                if (writeResult.IsCanceled || writeResult.IsCompleted)
                {
                  if (writeResult.IsCanceled)
                    Module.Log($"<{ClientId}> sendLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
                  else
                    Module.Log($"<{ClientId}> sendLoopAsync() exit through completion.", ObsLogLevel.Debug);
                  break;
                }
              }
              catch (OperationCanceledException ex)
              {
                Module.Log($"<{ClientId}> sendLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
                break;
              }
              catch (IOException ex)
              {
                // happens when the receiver closes the connection
                Module.Log($"<{ClientId}> Lost connection to receiver ({ex.GetType().Name}) while trying to send audio data.", ObsLogLevel.Error);
                pipeWriterComplete = false; // this would internally try to flush and by this throw another exception
                break;
              }
              catch (Exception ex)
              {
                Module.Log($"<{ClientId}> sendLoopAsync(): {ex.GetType().Name} sending audio data: {ex.Message}", ObsLogLevel.Error);
                break;
              }
            }

            if (!_frameTimestampQueue.IsEmpty)
              _frameAvailable.Set(); // make sure the send loop continues immediately if there is more data in the queue
          }
        }
        if (pipeWriterComplete)
          pipeWriter.Complete();
      }
      catch (Exception ex)
      {
        Module.Log($"<{ClientId}> {ex.GetType().Name} in send loop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        try { pipeWriter.Complete(ex); } catch { }
      }

      Module.Log($"<{ClientId}> sendLoopAsync(): Exiting...", ObsLogLevel.Debug);
      if (!_cancellationSource.IsCancellationRequested) // make sure that also all other loops exit now
        _cancellationSource.Cancel();
      _frameTimestampQueue.Clear();
      _frames.Clear();
      _receiveDelayMs = 0;
      _renderDelayMs = 0;
      if (_socket != null)
      {
        _socket.Shutdown(SocketShutdown.Both);
        _socket.Disconnect(false);
        if (_socket.Connected)
          _socket.Close();
      }
      _stream?.Close();
      OnDisconnected();
      Module.Log($"<{ClientId}> Disconnected.", ObsLogLevel.Info);
    }
    catch (Exception ex)
    {
      Module.Log($"<{ClientId}> {ex.GetType().Name} in send loop finalization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      try { pipeWriter.Complete(ex); } catch { }
    }
    finally
    {
      _sendLoopExited.Set();
    }
  }

  public unsafe void EnqueueVideoTimestamp(ulong timestamp)
  {
    _frameTimestampQueue.Enqueue(timestamp);
  }

  public unsafe bool EnqueueVideoFrame(ulong timestamp, Beam.VideoHeader videoHeader, byte[] videoData)
  {
    long videoFrameCount = Interlocked.Increment(ref _videoFrameCount);
    double fps = (videoHeader.Fps / videoHeader.FpsDenominator);
    if (videoFrameCount > fps)
    {
      Module.Log($"<{ClientId}> Error: Max send queue size reached: {videoFrameCount} ({_frameTimestampQueue.Count}).", ObsLogLevel.Error);
      Disconnect(0);
      return false;
    }
    else if (videoFrameCount > (fps / 2))
    {
      videoHeader.DataSize = 0;
      var emptyFrame = new Beam.BeamVideoData(videoHeader, Array.Empty<byte>(), timestamp);
      _frames.AddOrUpdate(timestamp, emptyFrame, (key, oldValue) => emptyFrame);
      Module.Log($"<{ClientId}> Error: Send queue size {videoFrameCount} ({_frameTimestampQueue.Count}), skipping video frame {timestamp}.", ObsLogLevel.Error);
      return false;
    }
    else if (videoFrameCount > 2)
      Module.Log($"<{ClientId}> Warning: Send queue size {videoFrameCount} ({_frameTimestampQueue.Count}) at video frame {timestamp}.", ObsLogLevel.Warning);

    var frame = new Beam.BeamVideoData(videoHeader, _videoDataPool.Rent(videoHeader.DataSize), timestamp);
    videoData.AsSpan(0, videoHeader.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes
    _frames.AddOrUpdate(timestamp, frame, (key, oldValue) => frame);
    _frameAvailable.Set();
    return true;
  }

  public unsafe bool EnqueueVideoFrame(ulong timestamp, Beam.VideoHeader videoHeader, byte* videoData)
  {
    long videoFrameCount = Interlocked.Increment(ref _videoFrameCount);
    if (videoFrameCount > (videoHeader.Fps))
    {
      Module.Log($"<{ClientId}> Error: Max send queue size reached: {videoFrameCount} ({_frameTimestampQueue.Count}).", ObsLogLevel.Error);
      Disconnect(0);
      return false;
    }
    else if (videoFrameCount > (videoHeader.Fps / 2))
    {
      videoHeader.DataSize = 0;
      var emptyFrame = new Beam.BeamVideoData(videoHeader, Array.Empty<byte>(), timestamp);
      _frames.AddOrUpdate(timestamp, emptyFrame, (key, oldValue) => emptyFrame);
      Module.Log($"<{ClientId}> Error: Send queue size {videoFrameCount} ({_frameTimestampQueue.Count}), skipping video frame {timestamp}.", ObsLogLevel.Error);
      return false;
    }
    else if (videoFrameCount > 2)
      Module.Log($"<{ClientId}> Warning: Send queue size {videoFrameCount} ({_frameTimestampQueue.Count}) at video frame {timestamp}.", ObsLogLevel.Warning);

    var frame = new Beam.BeamVideoData(videoHeader, _videoDataPool.Rent(videoHeader.DataSize), timestamp);
    new Span<byte>(videoData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes
    _frames.AddOrUpdate(timestamp, frame, (key, oldValue) => frame);
    _frameAvailable.Set();
    return true;
  }

  public unsafe void EnqueueAudio(ulong timestamp, byte* audioData)
  {
    //TODO: the current assumption is that OBS can work with audio continuing while video frames are dropped/skipped, remove the below block if this can be confirmed to be true, otherwise uncomment
    // long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    // if (videoFrameCount > BeamSender.MaxFrameQueueSize)
    // {
    //   var emptyHeader = _audioHeader;
    //   emptyHeader.DataSize = 0;
    //   var emptyFrame = new Beam.BeamAudioData(_audioHeader, Array.Empty<byte>(), timestamp);
    //   _frames.AddOrUpdate(timestamp, emptyFrame, (key, oldValue) => emptyFrame);
    //   Module.Log($"<{_clientId}> Error: Send queue size {videoFrameCount} ({_frameTimestampQueue.Count}), skipping audio frame {timestamp}.", ObsLogLevel.Error);
    //   return;
    // }

    var frame = new Beam.BeamAudioData(_audioHeader, _audioDataPool.Rent(_audioHeader.DataSize), timestamp); // get an audio data memory buffer from the pool, avoiding allocations
    new Span<byte>(audioData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes

    _frameTimestampQueue.Enqueue(timestamp); // SendAudio is always called from a sync context, so for audio we can safely add to the queue here
    _frames.AddOrUpdate(timestamp, frame, (key, oldValue) => frame);
    Interlocked.Increment(ref _audioFrameCount);
    _frameAvailable.Set();
  }
}
