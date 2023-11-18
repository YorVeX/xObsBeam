// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

namespace xObsBeam;

public class AudioBuffer
{
  const int MinimumAudioFrameBufferCount = 2;
  readonly object _frameListLock = new();
  readonly List<Beam.BeamAudioData> _frameList = [];
  bool _rampUp = true;
  bool _isFirstAudioFrame = true;
  readonly double _senderFps;
  ulong _lastAudioTimestamp;
  long _timestampAdjustment;
  int _frameAdjustment;
  int _lastRenderDelay;
  float _tickAdjustmentCycle;
  float _tickSecondCycle;

  /// <summary>The expected timestamp difference between two audio frames in nanoseconds.</summary>
  long _audioTimestampStepNs;
  ulong _maxAudioTimestampDeviation;
  readonly int _audioTimestampToleranceMs;

  public int FrameBufferTimeMs { get; private set; } = 1000;
  public int AudioFrameBufferCount { get; private set; } = 40;
  public bool FixedDelay { get; private set; }

  public AudioBuffer(int frameBufferTimeMs, bool fixedDelay, double senderFps)
  {
    FixedDelay = fixedDelay;
    _senderFps = senderFps;
    _audioTimestampStepNs = (long)(1_000_000_000 / _senderFps);
    _audioTimestampToleranceMs = (int)(1_000 / _senderFps);
    AudioFrameBufferCount = (int)((double)frameBufferTimeMs / 1000 * _senderFps);
    if (AudioFrameBufferCount < MinimumAudioFrameBufferCount)
    {
      AudioFrameBufferCount = MinimumAudioFrameBufferCount;
      Module.Log($"Warning: Enforcing minimum audio frame buffer size of {AudioFrameBufferCount} frames.", ObsLogLevel.Warning);
    }
    FrameBufferTimeMs = AudioFrameBufferCount * 1000 / (int)_senderFps;
  }

  private void Reset()
  {
    lock (_frameListLock)
    {
      _rampUp = true;
      _isFirstAudioFrame = true;
      _lastAudioTimestamp = 0;
      _audioTimestampStepNs = 0;
      _maxAudioTimestampDeviation = 0;
      AudioFrameBufferCount = (int)((double)FrameBufferTimeMs / 1000 * _senderFps);
      _lastRenderDelay = 0;
      _frameAdjustment = 0;
      _timestampAdjustment = 0;
      _tickAdjustmentCycle = 0;
      _tickSecondCycle = 0;
    }
  }

  public void ProcessFrame(Beam.BeamAudioData frame)
  {
    lock (_frameListLock)
    {
      _lastRenderDelay = frame.RenderDelayAverage; // need to get this from the newly added frames so that we work with the most recent value

      if (_rampUp)
      {
        if (_frameList.Count > AudioFrameBufferCount) // ramp-up is finished when the buffer is filled to its configured size
          _rampUp = false;

        if (_isFirstAudioFrame) // calculate the max audio frame time deviation based on header information from the first audio frame
        {
          _isFirstAudioFrame = false;
          var audioFrame = frame;
          _audioTimestampStepNs = (long)Math.Ceiling((1 / (double)audioFrame.Header.SampleRate) * audioFrame.Header.Frames * 1_000_000_000);
          _maxAudioTimestampDeviation = (ulong)Math.Ceiling(_audioTimestampStepNs * 1.01);
          Module.Log($"Audio frame buffer max deviation is: {_maxAudioTimestampDeviation}", ObsLogLevel.Debug);
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
      _frameList.Clear();
    }
  }

  public void AdjustDelay(int frameAdjustment)
  {
    lock (_frameListLock)
    {
      _frameAdjustment += frameAdjustment;
      AudioFrameBufferCount += frameAdjustment;
      _timestampAdjustment = (_frameAdjustment * _audioTimestampStepNs);
      Module.Log($"Audio frame buffer delay adjustment at {_lastRenderDelay} ms: {frameAdjustment:+#;-#;0} ({_frameAdjustment:+#;-#;0}) frames, new buffer size: {AudioFrameBufferCount} frames, timestamp adjustment: {_timestampAdjustment:+#;-#;0} ns.", ObsLogLevel.Info);
    }
  }

  public ulong GetOriginalAudioTimestamp(ulong adjustedTimestamp)
  {
    lock (_frameListLock)
      return (ulong)((long)adjustedTimestamp - _timestampAdjustment);
  }

  public Beam.BeamAudioData[] GetNextFrames(float videoFrameFrequencySeconds)
  {
    lock (_frameListLock)
    {
      if (_rampUp)
        return []; // still in ramp-up phase, don't return anything yet

      if (_frameList.Count < (AudioFrameBufferCount - 1))
        Module.Log($"Warning: Audio frame buffer below target: {_frameList.Count}/{AudioFrameBufferCount}", ObsLogLevel.Warning);

      var currentVideoTimestampStepNs = (ulong)(videoFrameFrequencySeconds * 1000000000);
      var maxVideoTimestampDeviation = (ulong)Math.Ceiling(currentVideoTimestampStepNs * 1.01);
      var result = new List<Beam.BeamAudioData>();

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
          if (_lastRenderDelay >= (FrameBufferTimeMs + _audioTimestampToleranceMs))
          {
            if (AudioFrameBufferCount > MinimumAudioFrameBufferCount)
            {
              AdjustDelay(-1);
              adjustedFrames = true; // so that no warning is shown for this case, since it is expected
            }
            else
              Module.Log($"Warning: Audio frame buffer delay of {_lastRenderDelay} ms is above tolerance range of {FrameBufferTimeMs} ± {_audioTimestampToleranceMs} ms, but can't be adjusted further because the buffer has already reached its minimum size. Consider increasing the frame buffer size to avoid this situation.", ObsLogLevel.Warning);
          }
          else if (_lastRenderDelay <= (FrameBufferTimeMs - _audioTimestampToleranceMs))
          {
            AdjustDelay(1);
            adjustedFrames = true; // so that no warning is shown for this case, since it is expected
          }
          else if (_tickSecondCycle == 0)
            Module.Log($"Audio frame buffer delay of {_lastRenderDelay} ms is within tolerance range of {FrameBufferTimeMs} ± {_audioTimestampToleranceMs} ms.", ObsLogLevel.Debug);
        }
      }

      bool foundEnoughAudioFrames = false;
      while (_frameList.Count > 0)
      {
        // ProcessFrame already did the sorting by timestamp while inserting, so we can just take the first frame to get the next frame that is due
        var frame = _frameList[0];

        if (_timestampAdjustment != 0)
          frame.AdjustedTimestamp = (ulong)((long)frame.Timestamp + _timestampAdjustment);

        if (foundEnoughAudioFrames)
          break;

        // check whether the next frame (by its timestamp) is not due yet
        bool isFrameDue = ((long)(frame.AdjustedTimestamp - _lastAudioTimestamp) < (long)currentVideoTimestampStepNs);
        bool isFrameMissing = ((long)(frame.AdjustedTimestamp - _lastAudioTimestamp) > (long)_maxAudioTimestampDeviation);
        if ((_lastAudioTimestamp > 0) && (_frameList.Count <= AudioFrameBufferCount) && (!isFrameDue || isFrameMissing))
        {
          if (!adjustedFrames && isFrameMissing)
            Module.Log($"Warning: Missing audio frame {_lastAudioTimestamp + (ulong)_audioTimestampStepNs} in frame buffer, timestamp deviation of {(long)(frame.AdjustedTimestamp - _lastAudioTimestamp)} > max deviation of {(long)_maxAudioTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);
          if (isFrameMissing)
            _lastAudioTimestamp += (ulong)_audioTimestampStepNs; // close the timestamp gap by the step that was expected
          foundEnoughAudioFrames = true; // don't return any frames from the buffer this round, making sure it's not depleted
        }
        else
        {
          _lastAudioTimestamp = frame.AdjustedTimestamp;

          _frameList.RemoveAt(0);
          foundEnoughAudioFrames = (_frameList.Count <= AudioFrameBufferCount); // only found enough audio frames when the buffer doesn't hold more of them than configured
          result.Add(frame);
        }
      }
      if (_tickSecondCycle == 0)
        Module.Log($"Audio frame buffer returning {result.Count} frames, {_frameList.Count} in buffer.", ObsLogLevel.Debug);

      if (_frameList.Count == 0) // out of ramp-up phase but the buffer is empty?
      {
        Reset(); // the buffer ran dry, start over with ramp up
        _frameList.Clear();
        Module.Log("Error: The audio frame buffer ran dry, refilling. Consider increasing the buffering time to compensate for longer gaps.", ObsLogLevel.Error);
        return [];
      }

      return [.. result];
    }
  }
}
