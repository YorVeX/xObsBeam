﻿// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

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


  //TODO: beside sockets and named pipes explore a memory mapped file approach, it's also a stream and can be used by PipeReader and PipeWriter
  public void Connect()
  {
    Task.Run(() => ConnectAsync());
  }

  public async Task ConnectAsync()
  {
    if ((_targetHostname == "") && (_pipeName == ""))
      throw new InvalidOperationException("No connection target specified. Call Connect(address, port) or Connect(pipe name) first.");
    if (_targetHostname != "")
      await ConnectAsync(_targetHostname, _targetPort);
    else
      await ConnectAsync(_pipeName);
  }

  public void Connect(string hostname, int port = BeamSender.DefaultPort)
  {
    Task.Run(() => ConnectAsync(hostname, port));
  }

  public async Task ConnectAsync(string hostname, int port = BeamSender.DefaultPort)
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
  public void Connect(string hostname, string pipeName)
  {
    Task.Run(() => ConnectAsync(hostname, pipeName));
  }

  public async Task ConnectAsync(string hostname, string pipeName)
  {
    if (_isConnecting || _isConnected)
      return;

    _isConnecting = true;
    _targetHostname = hostname;
    _pipeName = pipeName;
    while (_pipeName != "")
    {
      if (!_cancellationSource.TryReset())
      {
        _cancellationSource.Dispose();
        _cancellationSource = new CancellationTokenSource();
      }
      var pipeStream = new NamedPipeClientStream(hostname, pipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
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

    int biggestHeaderSize = Math.Max(Beam.VideoHeader.VideoHeaderDataSize, Beam.AudioHeader.AudioHeaderDataSize);

    uint fps = 30;
    uint logCycle = 0;

    ulong lastVideoTimestamp = 0;
    ulong lastAudioTimestamp = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        ReadResult readResult = await pipeReader.ReadAtLeastAsync(biggestHeaderSize, cancellationToken);
        if (readResult.IsCanceled || (readResult.Buffer.IsEmpty && readResult.IsCompleted))
        {
          if (readResult.IsCanceled)
            Module.Log("processDataLoopAsync() exit from reading header data through cancellation.", ObsLogLevel.Debug);
          else
            Module.Log("processDataLoopAsync() exit from reading header data through completion.", ObsLogLevel.Debug);
          break;
        }

        var beamType = Beam.GetBeamType(readResult.Buffer);

        if (beamType == Beam.Type.Video)
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
          else
            Module.Log($"Video data: Received frame {videoHeader.Timestamp} out of order, expected > {lastVideoTimestamp}.", ObsLogLevel.Warning);
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

            lock (_sizeLock)
            {
              _width = videoHeader.Width;
              _height = videoHeader.Height;
            }
            OnVideoFrameReceived(new Beam.BeamVideoData(videoHeader, readResult.Buffer.Slice(0, videoHeader.DataSize).ToArray()));

            long receiveLength = readResult.Buffer.Length; // remember this here, before the buffer is invalidated with the next line
            pipeReader.AdvanceTo(readResult.Buffer.GetPosition(videoHeader.DataSize), readResult.Buffer.End);

            // tell the sender the current video timestamp that was received
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
          Module.Log($"Received unknown header type, aborting connection.", ObsLogLevel.Error);
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