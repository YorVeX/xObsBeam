// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace xObsBeam;

public class FrameBuffer
{
  const int MinimumVideoFrameBufferCount = 2;
  readonly object _frameListLock = new();
  readonly List<Beam.IBeamData> _frameList = new();
  bool _rampUp = true;
  bool _isFirstAudioFrame = true;
  int _videoFrameCount;
  readonly double _localFps;
  readonly double _senderFps;
  ulong _lastVideoTimestamp;
  ulong _lastAudioTimestamp;
  long _timestampAdjustment;
  readonly int _videoTimestampToleranceMs;
  readonly long _videoTimestampStepNs;
  int _frameAdjustment;
  int _lastRenderDelay;
  float _tickAdjustmentCycle;
  float _tickSecondCycle;

  /// <summary>The expected timestamp difference between two audio frames in nanoseconds.</summary>
  ulong _audioTimestampStep;
  ulong _maxAudioTimestampDeviation;

  readonly ArrayPool<byte> _arrayPool;

  public int FrameBufferTimeMs { get; private set; } = 1000;
  public int VideoFrameBufferCount { get; private set; } = 60;
  public bool FixedDelay { get; private set; }

  public FrameBuffer(int frameBufferTimeMs, bool fixedDelay, double senderFps, double localFps, ArrayPool<byte> arrayPool)
  {
    FixedDelay = fixedDelay;
    _arrayPool = arrayPool;
    _localFps = localFps;
    _senderFps = senderFps;
    _videoTimestampStepNs = (long)(1_000_000_000 / _senderFps);
    _videoTimestampToleranceMs = (int)(1_000 / _senderFps);
    VideoFrameBufferCount = (int)((double)frameBufferTimeMs / 1000 * _senderFps);
    if (VideoFrameBufferCount < MinimumVideoFrameBufferCount)
    {
      VideoFrameBufferCount = MinimumVideoFrameBufferCount;
      Module.Log($"Warning: Enforcing minimum frame buffer size of {VideoFrameBufferCount} frames.", ObsLogLevel.Warning);
    }
    FrameBufferTimeMs = VideoFrameBufferCount * 1000 / (int)_senderFps;
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
      VideoFrameBufferCount = (int)((double)FrameBufferTimeMs / 1000 * _senderFps);
      _lastRenderDelay = 0;
      _frameAdjustment = 0;
      _timestampAdjustment = 0;
      _tickAdjustmentCycle = 0;
      _tickSecondCycle = 0;
    }
  }

  public void ProcessFrame(Beam.IBeamData frame)
  {
    lock (_frameListLock)
    {
      if (frame.Type == Beam.Type.Video)
      {
        _lastRenderDelay = ((Beam.BeamVideoData)frame).RenderDelayAverage; // need to get this from the newly added frames so that we work with the most recent value
        _videoFrameCount++;
      }

      if (_rampUp)
      {
        if (_videoFrameCount > VideoFrameBufferCount) // ramp-up is finished when the buffer is filled to its configured size
          _rampUp = false;

        if (_isFirstAudioFrame && (frame.Type == Beam.Type.Audio)) // calculate the max audio frame time deviation based on header information from the first audio frame
        {
          _isFirstAudioFrame = false;
          var audioFrame = (Beam.BeamAudioData)frame;
          _audioTimestampStep = (ulong)Math.Ceiling((1 / (double)audioFrame.Header.SampleRate) * audioFrame.Header.Frames * 1_000_000_000);
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

  public void AdjustDelay(int frameAdjustment)
  {
    lock (_frameListLock)
    {
      _frameAdjustment += frameAdjustment;
      VideoFrameBufferCount += frameAdjustment;
      _timestampAdjustment = (_frameAdjustment * _videoTimestampStepNs);
      Module.Log($"Frame buffer delay adjustment at {_lastRenderDelay} ms: {frameAdjustment:+#;-#;0} ({_frameAdjustment:+#;-#;0}) frames, new buffer size: {VideoFrameBufferCount} frames, timestamp adjustment: {_timestampAdjustment:+#;-#;0} ns.", ObsLogLevel.Info);
    }
  }

  public ulong GetOriginalVideoTimestamp(ulong adjustedTimestamp)
  {
    lock (_frameListLock)
      return (ulong)((long)adjustedTimestamp - _timestampAdjustment);
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

      if (_frameList.Count < VideoFrameBufferCount)
        Module.Log($"Warning: Frame buffer below target: {_frameList.Count}/{VideoFrameBufferCount}", ObsLogLevel.Warning);

      var currentVideoTimestampStep = (ulong)(videoFrameFrequencySeconds * 1000000000);
      var maxVideoTimestampDeviation = (ulong)Math.Ceiling(currentVideoTimestampStep * 1.01);
      var result = new List<Beam.IBeamData>();

      bool adjustedFrames = false;
      _tickAdjustmentCycle += videoFrameFrequencySeconds;
      _tickSecondCycle += videoFrameFrequencySeconds;
      if (_tickAdjustmentCycle >= 0.5) // check roughly twice per second for necessary delay adjustments if fixed delay is enabled - more often wouldn't make sense, as each delay adjustment needs time to settle with OBS and be reflected in measurements
      {
        _tickAdjustmentCycle = 0;
        if (_tickSecondCycle >= 1)
          _tickSecondCycle = 0;
        if (FixedDelay && (_lastRenderDelay > 0))
        {
          // bigger AdjustDelay steps than 1 would be possible from this code, but in tests it was found that this doesn't lead to less adjustments overall, probably due to OBS reacting differently to bigger timestamp jumps
          if (_lastRenderDelay >= (FrameBufferTimeMs + _videoTimestampToleranceMs))
          {
            if (VideoFrameBufferCount > (MinimumVideoFrameBufferCount))
            {
              AdjustDelay(-1);
              adjustedFrames = true; // so that no warning is shown for this case, since it is expected
            }
            else
              Module.Log($"Warning: Frame buffer delay of {_lastRenderDelay} ms is above tolerance range of {FrameBufferTimeMs} ± {_videoTimestampToleranceMs} ms, but can't be adjusted further because the buffer has already reached its minimum size. Consider increasing the frame buffer size to avoid this situation.", ObsLogLevel.Warning);
          }
          else if (_lastRenderDelay <= (FrameBufferTimeMs - _videoTimestampToleranceMs))
          {
            AdjustDelay(1);
            adjustedFrames = true; // so that no warning is shown for this case, since it is expected
          }
          else if (_tickSecondCycle == 0)
            Module.Log($"Frame buffer delay of {_lastRenderDelay} ms is within tolerance range of {FrameBufferTimeMs} ± {_videoTimestampToleranceMs} ms.", ObsLogLevel.Debug);
        }
      }

      bool foundEnoughVideoFrames = false;
      while (_frameList.Count > 0)
      {
        // ProcessFrame already did the sorting by timestamp while inserting, so we can just take the first frame to get the next frame that is due
        var frame = _frameList[0];

        if (_timestampAdjustment != 0)
          frame.AdjustedTimestamp = (ulong)((long)frame.Timestamp + _timestampAdjustment);

        if (frame.Type == Beam.Type.Video)
        {
          if (foundEnoughVideoFrames)
            break;

          // check whether the next frame (by its timestamp) is missing
          if ((_lastVideoTimestamp > 0) && (_videoFrameCount < VideoFrameBufferCount) && ((long)(frame.AdjustedTimestamp - _lastVideoTimestamp) > (long)maxVideoTimestampDeviation))
          {
            if (!adjustedFrames && (_senderFps >= _localFps)) // only show a log warning if missing frames wouldn't be expected anyway because of FPS setting differences between sender and receiver or delay adjustments
              Module.Log($"Warning: Missing video frame {_lastVideoTimestamp + currentVideoTimestampStep} in frame buffer, timestamp deviation of {(long)(frame.AdjustedTimestamp - _lastVideoTimestamp)} > max deviation of {maxVideoTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);

            _lastVideoTimestamp += currentVideoTimestampStep; // close the timestamp gap by the step that was expected
            foundEnoughVideoFrames = true; // don't return any real video frames from the buffer this round, making sure it's not depleted
          }
          else
          {
            _lastVideoTimestamp = frame.AdjustedTimestamp;

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
          if (!adjustedFrames && (_lastAudioTimestamp > 0) && ((long)(frame.AdjustedTimestamp - _lastAudioTimestamp) > (long)_maxAudioTimestampDeviation))
            Module.Log($"Warning: Missing audio frame in frame buffer, timestamp deviation of {(long)(frame.AdjustedTimestamp - _lastAudioTimestamp)} > max deviation of {_maxAudioTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);
          _lastAudioTimestamp = frame.AdjustedTimestamp;
          _frameList.RemoveAt(0);
          if (frame.AdjustedTimestamp >= _lastVideoTimestamp)
            result.Add(frame);
          else // this would cause audio buffering in OBS, but that's a local mechanism and shouldn't be triggered by remote data, hence skip such audio frames entirely
            Module.Log($"Warning: Frame buffer is skipping audio frame {frame.AdjustedTimestamp} that is older than the last video frame {_lastVideoTimestamp}.", ObsLogLevel.Warning);
        }
      }
      if (_tickSecondCycle == 0)
        Module.Log($"Frame buffer returning {result.Count} frames ({debugVideoFrameCount} video and {debugAudioFrameCount} audio), {_frameList.Count} in buffer ({_videoFrameCount} video and {_frameList.Count - _videoFrameCount} audio).", ObsLogLevel.Debug);

      if (_frameList.Count == 0) // out of ramp-up phase but the buffer is empty?
      {
        Reset(); // the buffer ran dry, start over with ramp up
        _frameList.Clear();
        Module.Log("Error: The frame buffer ran dry, refilling. Consider increasing the buffering time to compensate for longer gaps.", ObsLogLevel.Error);
        return Array.Empty<Beam.IBeamData>();
      }

      return result.ToArray();
    }
  }
}
