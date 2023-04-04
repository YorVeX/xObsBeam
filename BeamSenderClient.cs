﻿// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using ObsInterop;

namespace xObsBeam;

class BeamSenderClient
{
  const int MaxFrameQueueSize = 5;

  Socket? _socket;
  NamedPipeServerStream? _pipeStream;
  Stream? _stream;
  PipeWriter? _pipeWriter;
  PipeReader? _pipeReader;
  ConcurrentQueue<Beam.IBeamData> _frameQueue = new ConcurrentQueue<Beam.IBeamData>();
  AutoResetEvent _frameAvailable = new AutoResetEvent(false);
  long _videoFrameCount = -1;
  long _audioFrameCount = -1;
  CancellationTokenSource _cancellationSource = new CancellationTokenSource();
  Task _sendTask = Task.CompletedTask;
  Task _receiveTask = Task.CompletedTask;
  string _clientId = "";
  Beam.VideoHeader _videoHeader;
  ArrayPool<byte> _videoDataPool;
  Beam.AudioHeader _audioHeader;
  ArrayPool<byte> _audioDataPool;

  public string ClientId { get => _clientId; }

  public BeamSenderClient(string clientId, Socket socket, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    _clientId = clientId;
    _videoHeader = videoHeader;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, MaxFrameQueueSize * 2);
    Module.Log($"<{_clientId}> New client connected.", ObsLogLevel.Info);
    _socket = socket;
  }

  public BeamSenderClient(string clientId, NamedPipeServerStream pipeStream, Beam.VideoHeader videoHeader, Beam.AudioHeader audioHeader)
  {
    _clientId = clientId;
    _videoHeader = videoHeader;
    _videoDataPool = ArrayPool<byte>.Create(videoHeader.DataSize, MaxFrameQueueSize);
    _audioHeader = audioHeader;
    _audioDataPool = ArrayPool<byte>.Create(audioHeader.DataSize, MaxFrameQueueSize * 2);
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
    _pipeWriter = PipeWriter.Create(_stream);
    _pipeReader = PipeReader.Create(_stream);
    _sendTask = Task.Run(() => sendLoopAsync(_cancellationSource.Token));
    _receiveTask = Task.Run(() => processDataLoopAsync(_socket, _cancellationSource.Token));

  }

  public void Disconnect(bool blocking = false)
  {
    Module.Log($"<{_clientId}> Disconnecting client...", ObsLogLevel.Info);
    _cancellationSource.Cancel();
    _frameAvailable.Set();
    if (blocking)
      _sendTask.Wait();
  }

  public event EventHandler<EventArgs>? Disconnected;

  void OnDisconnected()
  {
    Task.Run(() => Disconnected?.Invoke(this, EventArgs.Empty));
  }

  async Task processDataLoopAsync(Socket? socket, CancellationToken cancellationToken)
  {
    /*
    The only reason for this method to exist is because the mere existence of a receiving channel on the underlying socket ensures proper disconnect detection also for the sender.
    Therefore this method only logs debug messages and does not do anything else of impact, all error handling and closing code is done in the send loop.
    As a side effect the frame information from the sender can also be useful for debugging purposes.
    */
    if (_pipeReader == null)
      return;
    try
    {
      Module.Log($"<{_clientId}> processDataLoopAsync() started.", ObsLogLevel.Debug);
      while (!cancellationToken.IsCancellationRequested)
      {
        ulong timestamp;
        ReadResult readResult = await _pipeReader.ReadAtLeastAsync(8, cancellationToken);
        if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
        {
          if (readResult.IsCanceled)
            Module.Log($"<{_clientId}> processDataLoopAsync() exit through cancellation.", ObsLogLevel.Debug);
          else
            Module.Log($"<{_clientId}> processDataLoopAsync() exit through completion.", ObsLogLevel.Debug);
          break;
        }
        _pipeReader.AdvanceTo(Beam.GetTimestamp(readResult.Buffer, out timestamp), readResult.Buffer.End);
        //TODO: there is still a slight chance of the sender not detecting a receiver disconnect when using sockets, check here every second whether the timestamp received was not longer than a second ago
        // Module.Log($"<{_clientId}> Receiver is at video timestamp {timestamp}.", ObsLogLevel.Debug);
      }
    }
    catch (OperationCanceledException ex)
    {
      Module.Log($"<{_clientId}> processDataLoopAsync() exit through {ex.GetType().Name}.", ObsLogLevel.Debug);
    }
    catch (IOException ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}", ObsLogLevel.Debug);
    }
    catch (System.Exception ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} while trying to process or retrieve data: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
    }
  }

  async Task sendLoopAsync(CancellationToken cancellationToken)
  {
    /*
    We want to send data out as fast as possible, however, a small queue is still needed for two reasons:
    1. The main rendering thread should be blocked as short as possible, we don't want to wait for network functions.
    2. The PipeWriter workflow ends with "FlushAsync" (implicitly through WriteAsync). Since this call is async it could still be busy while the next frame is already being processed (and in tests this indeed has occasionally happened).
       As part of this processing the next call to PipeWriter.GetMemory() would be made, but: "Calling GetMemory or GetSpan while there's an incomplete call to FlushAsync isn't safe.", see:
       https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines#pipewriter-common-problems
    */

    if (_pipeWriter == null)
      return;
    try
    {
      uint fps = 30;
      uint logCycle = 0;
      bool pipeWriterComplete = true;

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

            if (videoFrame.Header.Fps > 0)
              fps = videoFrame.Header.Fps;

            // write video data
            try
            {
              // write video header data
              var headerBytes = videoFrame.Header.WriteTo(_pipeWriter.GetMemory(Beam.VideoHeader.VideoHeaderDataSize).Span, videoFrame.Timestamp);
              _pipeWriter.Advance(headerBytes);

              // write video frame data - need to slice videoFrame.Data, since the arrays we get from _videoDataPool are often bigger than what we requested
              var writeResult = await _pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(videoFrame.Data).Slice(0, videoFrame.Header.DataSize), cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
              _videoDataPool.Return(videoFrame.Data); // return video frame data to the memory pool

              if (logCycle >= fps)
                Module.Log($"<{_clientId}> Sent {headerBytes} + {videoFrame.Header.DataSize} bytes of video data, queue length: {_frameQueue.Count()}", ObsLogLevel.Debug);
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
            if (logCycle++ >= fps)
              logCycle = 0;
          }
          else if (frame is Beam.BeamAudioData audioFrame)
          {
            long audioFrameCount = Interlocked.Decrement(ref _audioFrameCount);

            // write audio data
            try
            {
              // write audio header data
              var headerBytes = audioFrame.Header.WriteTo(_pipeWriter.GetMemory(Beam.AudioHeader.AudioHeaderDataSize).Span, audioFrame.Timestamp);
              _pipeWriter.Advance(headerBytes);

              // write audio frame data - need to slice audioFrame.Data, since the arrays we get from the shared ArrayPool are often bigger than what we requested
              var writeResult = await _pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(audioFrame.Data).Slice(0, audioFrame.Header.DataSize), cancellationToken); // implicitly calls _pipeWriter.Advance and _pipeWriter.FlushAsync
              ArrayPool<byte>.Shared.Return(audioFrame.Data); // return audio frame data to the memory pool
              if (logCycle >= fps)
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
        _pipeWriter.Complete();
    }
    catch (System.Exception ex)
    {
      Module.Log($"<{_clientId}> {ex.GetType().Name} in send loop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      try { _pipeWriter.Complete(ex); } catch { }
    }
    try { _pipeReader?.Complete(); } catch { }
    _pipeWriter = null;
    _pipeReader = null;

    Module.Log($"<{_clientId}> sendLoopAsync(): Exiting...", ObsLogLevel.Debug);
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

  public unsafe void Enqueue(ulong timestamp, byte* videoData)
  {
    long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    if (videoFrameCount > 5)
    {
      int frameSleepTime = (int)Math.Ceiling((1 / (double)_videoHeader.Fps * 1000));
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
      Module.Log($"<{_clientId}> Send queue not ready yet at video frame {timestamp}.", ObsLogLevel.Debug);
      return;
    }

    Beam.BeamVideoData frame = new Beam.BeamVideoData(_videoHeader, _videoDataPool.Rent(_videoHeader.DataSize), timestamp); // get a video data memory buffer from the pool, avoiding allocations
    new Span<byte>(videoData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes

    _frameQueue.Enqueue(frame);
    Interlocked.Increment(ref _videoFrameCount);
    _frameAvailable.Set();
  }

  public unsafe void Enqueue(ulong timestamp, byte* audioData, int speakers, int audioBytesPerSample)
  {
    long videoFrameCount = Interlocked.Read(ref _videoFrameCount);
    if (videoFrameCount > MaxFrameQueueSize)
    {
      Module.Log($"<{_clientId}> Error: Send queue size {videoFrameCount} ({_frameQueue.Count}), skipping audio frame {timestamp}.", ObsLogLevel.Error);
      return;
    }
    else if (videoFrameCount < 0)
    {
      Module.Log($"<{_clientId}> Send queue not ready yet at audio frame {timestamp}.", ObsLogLevel.Debug);
      return;
    }

    Beam.BeamAudioData frame = new Beam.BeamAudioData(_audioHeader, _audioDataPool.Rent(_audioHeader.DataSize), timestamp); // get an audio data memory buffer from the pool, avoiding allocations
    new Span<byte>(audioData, frame.Header.DataSize).CopyTo(frame.Data); // copy the data to the managed array pool memory, OBS allocates this all in one piece so it can be copied in one go without worrying about planes

    _frameQueue.Enqueue(frame);
    Interlocked.Increment(ref _audioFrameCount);
    _frameAvailable.Set();
  }



}