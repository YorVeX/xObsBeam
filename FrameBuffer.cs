// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace xObsBeam;

public class FrameBuffer
{
  readonly object _frameListLock = new();
  readonly List<Beam.IBeamData> _frameList = new();
  bool _rampUp = true;
  bool _isFirstAudioFrame = true;
  int _videoFrameCount;
  readonly double _localFps;
  readonly double _senderFps;
  ulong _lastVideoTimestamp;
  ulong _lastAudioTimestamp;

  /// <summary>The expected timestamp difference between two audio frames in nanoseconds.</summary>
  ulong _audioTimestampStep;
  ulong _maxAudioTimestampDeviation;

  readonly ArrayPool<byte> _arrayPool;

  public int FrameBufferTimeMs { get; private set; } = 1000;
  public int VideoFrameBufferCount { get; private set; } = 60;

  public FrameBuffer(int frameBufferTimeMs, double senderFps, double localFps, ArrayPool<byte> arrayPool)
  {
    _arrayPool = arrayPool;
    _localFps = localFps;
    _senderFps = senderFps;
    VideoFrameBufferCount = (int)Math.Ceiling((double)frameBufferTimeMs / 1000 * _senderFps);
    FrameBufferTimeMs = VideoFrameBufferCount * 1000 / (int)_senderFps;

    //TODO: add mode to keep the delay stable based on comparing the local system time to frame timestamps - do this by adjusting the VideoFrameBufferCount property accordingly
    // e.g. at 30 FPS if the delay increased by more than 33.3333.. ms then decrease the buffer size by 1 frame (= 33.3333.. ms at 30 FPS) to counter this - or vice versa
  }

  public void ProcessFrame(Beam.IBeamData frame)
  {
    lock (_frameListLock)
    {
      if (frame.Type == Beam.Type.Video)
        _videoFrameCount++;

      if (_rampUp)
      {
        if (_videoFrameCount > VideoFrameBufferCount) // ramp-up is finished when the buffer is filled to its configured size
          _rampUp = false;

        if (_isFirstAudioFrame && (frame.Type == Beam.Type.Audio)) // calculate the max audio frame time deviation based on header information from the first audio frame
        {
          _isFirstAudioFrame = false;
          var audioFrame = (Beam.BeamAudioData)frame;
          _audioTimestampStep = (ulong)Math.Ceiling((1 / (double)audioFrame.Header.SampleRate) * audioFrame.Header.Frames * 1000000000);
          _maxAudioTimestampDeviation = (ulong)Math.Ceiling(_audioTimestampStep * 1.01);
          Module.Log($"Frame buffer max audio deviation is: {_maxAudioTimestampDeviation}", ObsLogLevel.Debug);
        }
      }

      int frameIndex = (_frameList.Count - 1);
      if (frameIndex < 0) // the very first frame, no need to apply any further logic
      {
        _frameList.Add(frame);
        return;
      }
      // the standard case should be that this frame timestamp was received in the right order and therefore can be added at the end, so check this first
      if (frame.Timestamp > _frameList[frameIndex].Timestamp)
        _frameList.Add(frame);
      else // not the standard case, need to see where to insert this frame
      {
        bool inserted = false;
        // run this loop backwards, if the new frame is not the very last in the list it should at least be close to the end
        while (--frameIndex >= 0)
        {
          if (frame.Timestamp > _frameList[frameIndex].Timestamp)
          {
            _frameList.Insert(frameIndex + 1, frame);
            inserted = true;
            break;
          }
        }
        if (!inserted)
          _frameList.Insert(0, frame);
      }
    }
  }

  private void Reset()
  {
    lock (_frameListLock)
    {
      _rampUp = true;
      _isFirstAudioFrame = true;
      _videoFrameCount = 0;
      _lastVideoTimestamp = 0;
      _lastAudioTimestamp = 0;
      _audioTimestampStep = 0;
      _maxAudioTimestampDeviation = 0;
    }
  }

  public void Stop()
  {
    lock (_frameListLock)
    {
      Reset();
      var unusedVideoFrames = new List<Beam.BeamVideoData>();
      foreach (var frame in _frameList)
      {
        if (frame.Type == Beam.Type.Video)
          _arrayPool.Return(((Beam.BeamVideoData)frame).Data); // don't leak those unused frames
      }
      _frameList.Clear();
    }
  }

  public Beam.IBeamData[] GetNextFrames(float videoFrameFrequencySeconds)
  {
    lock (_frameListLock)
    {
      // can be removed at some later point if the frame buffer has proven to work reliably
      int debugVideoFrameCount = 0;
      int debugAudioFrameCount = 0;

      if (_rampUp)
        return Array.Empty<Beam.IBeamData>(); // still in ramp-up phase, don't return anything yet
      else if (_frameList.Count == 0) // out of ramp-up phase but the buffer is empty?
      {
        //TODO: think about making this behavior configurable or whether a better solution is possible
        Reset(); // the buffer ran dry, start over with ramp up
        Module.Log("The frame buffer ran dry, refilling. Consider increasing the buffering time to compensate for longer gaps.", ObsLogLevel.Warning);
        return Array.Empty<Beam.IBeamData>();
      }

      if (_frameList.Count < VideoFrameBufferCount)
        Module.Log($"Frame buffer below target: {_frameList.Count}/{VideoFrameBufferCount}", ObsLogLevel.Warning);

      var videoTimestampStep = (ulong)(videoFrameFrequencySeconds * 1000000000);
      var maxVideoTimestampDeviation = (ulong)Math.Ceiling(videoTimestampStep * 1.01);
      var result = new List<Beam.IBeamData>();

      bool foundEnoughVideoFrames = false;
      while (_frameList.Count > 0)
      {
        // ProcessFrame already did the sorting by timestamp while inserting, so we can just take the first frame to get the next frame that is due
        var frame = _frameList[0];
        if (frame.Type == Beam.Type.Video)
        {
          if (foundEnoughVideoFrames)
            break;

          // if the timestamp of the next video frame is too far off from the expected value, we need to fill the gap with dummy frames
          if ((_lastVideoTimestamp > 0) && (_videoFrameCount < VideoFrameBufferCount) && ((long)(frame.Timestamp - _lastVideoTimestamp) > (long)maxVideoTimestampDeviation))
          {
            // only show this warning if sender FPS and local FPS are otherwise matching, so that constant missing frames aren't expected
            if (_senderFps >= _localFps) // only show a log warning if missing frames wouldn't be expected anyway because of FPS setting differences between sender and receiver
              Module.Log($"Missing video frame {_lastVideoTimestamp + videoTimestampStep} in frame buffer, timestamp deviation of {(long)(frame.Timestamp - _lastVideoTimestamp)} > max deviation of {maxVideoTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);

            _lastVideoTimestamp += videoTimestampStep; // close the timestamp gap by the step that was expected
            foundEnoughVideoFrames = true; // don't return any real video frames from the buffer this round, making sure it's not depleted
          }
          else
          {
            _lastVideoTimestamp = frame.Timestamp;

            _videoFrameCount--;
            foundEnoughVideoFrames = (_videoFrameCount <= VideoFrameBufferCount); // only found enough video frames when the buffer doesn't hold more of them than configured
            _frameList.RemoveAt(0);
            debugVideoFrameCount++;
            result.Add(frame);
          }
        }
        else if (frame.Type == Beam.Type.Audio)
        {
          debugAudioFrameCount++;
          if ((_lastAudioTimestamp > 0) && ((long)(frame.Timestamp - _lastAudioTimestamp) > (long)_maxAudioTimestampDeviation))
            Module.Log($"Missing audio frame in frame buffer, timestamp deviation of {(long)(frame.Timestamp - _lastAudioTimestamp)} > max deviation of {_maxAudioTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);
          _lastAudioTimestamp = frame.Timestamp;
          _frameList.RemoveAt(0);
          if (frame.Timestamp >= _lastVideoTimestamp)
            result.Add(frame);
          else // this would cause audio buffering in OBS, but that's a local mechanism and shouldn't be triggered by remote data, hence skip such audio frames entirely
            Module.Log($"Frame buffer is skipping audio frame {frame.Timestamp} that is older than the last video frame {_lastVideoTimestamp}.", ObsLogLevel.Warning);
        }
      }
      // Module.Log($"GetNextFrames returning {result.Count} frames ({debugVideoFrameCount} video and {debugAudioFrameCount} audio), {_frameList.Count} in buffer ({_videoFrameCount} video and {_frameList.Count - _videoFrameCount} audio).", ObsLogLevel.Debug);
      return result.ToArray();
    }
  }
}
