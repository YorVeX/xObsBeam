// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

namespace xObsBeam;

/*
Buffering and sorting is necessary when receiving async feeds for two reasons:
1. Because compressing video frames takes time they are received with a delay compared to the audio frames.
2. Since the compression threads could complete after different runtimes the video frames could arrive in a different order than they were sent.

To solve this the idea is to buffer and collect all incoming frames for a while, then process them sorted by their timestamp. The buffering time needs
to be high enough for the longest video frame processing time that can occur.

With sync feeds these problems don't exist, the order stays unaltered and audio frame processing is blocked while the video frames are being processed.
*/

public class FrameBuffer
{
  readonly object _frameListLock = new();
  readonly List<Beam.IBeamData> _frameList = new();
  bool _rampUp = true;
  bool _isFirstAudioFrame = true;
  int _videoFrameCount;
  readonly uint _fps;
  ulong _lastVideoTimestamp;
  ulong _lastAudioTimestamp;

  /// <summary>The expected timestamp difference between two video frames in nanoseconds.</summary>
  ulong _videoTimestampStep;
  /// <summary>The expected timestamp difference between two audio frames in nanoseconds.</summary>
  ulong _audioTimestampStep;
  ulong _maxVideoTimestampDeviation;
  ulong _maxAudioTimestampDeviation;

  public int FrameBufferTimeMs { get; private set; } = 1000;
  public int VideoFrameBufferCount { get; private set; } = 60;
  public FrameBuffer(int frameBufferTimeMs, uint fps)
  {
    _fps = fps;
    VideoFrameBufferCount = (int)Math.Ceiling((double)frameBufferTimeMs / 1000 * _fps);
    FrameBufferTimeMs = VideoFrameBufferCount * 1000 / (int)_fps;
    _videoTimestampStep = (ulong)Math.Ceiling((1 / (double)_fps) * 1000000000);
    _maxVideoTimestampDeviation = (ulong)Math.Ceiling(_videoTimestampStep * 1.8);
    Module.Log($"Frame buffer max video deviation is: {_maxVideoTimestampDeviation}", ObsLogLevel.Debug);
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
          _maxAudioTimestampDeviation = (ulong)Math.Ceiling(_audioTimestampStep * 1.8);
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
      _videoTimestampStep = 0;
      _audioTimestampStep = 0;
      _maxVideoTimestampDeviation = 0;
      _maxAudioTimestampDeviation = 0;
    }
  }

  public List<Beam.BeamVideoData> Stop()
  {
    lock (_frameListLock)
    {
      Reset();
      var unusedVideoFrames = new List<Beam.BeamVideoData>();
      foreach (var frame in _frameList)
      {
        if (frame.Type == Beam.Type.Video)
          unusedVideoFrames.Add((Beam.BeamVideoData)frame);
      }
      _frameList.Clear();
      return unusedVideoFrames;
    }
  }

  public Beam.IBeamData[] GetNextFrames()
  {
    lock (_frameListLock)
    {
      int debugVideoFrameCount = 0; //HACK: for debugging only, remove later
      int debugAudioFrameCount = 0; //HACK: for debugging only, remove later

      if (_rampUp)
        return Array.Empty<Beam.IBeamData>(); // still in ramp-up phase, don't return anything yet
      else if (_frameList.Count == 0) // out of ramp-up phase but the buffer is empty?
      {
        //TODO: think about making this behavior configurable or whether a better solution is possible
        Reset(); // the buffer ran dry, start over with ramp up
        Module.Log("The frame buffer ran dry, refilling. Consider increasing the buffering time to compensate for longer gaps.", ObsLogLevel.Warning);
        return Array.Empty<Beam.IBeamData>();
      }

      if (_frameList.Count < VideoFrameBufferCount) //HACK: for debugging only, remove later
        Module.Log($"Frame buffer below target: {_frameList.Count}/{VideoFrameBufferCount}", ObsLogLevel.Warning);

      var result = new List<Beam.IBeamData>();

      bool foundEnoughVideoFrames = false;
      while (_frameList.Count > 0)
      {
        // ProcessFrame already did the sorting by timestamp while inserting, so we can just take the first frame to get the next frame that is due
        var frame = _frameList[0];
        if (foundEnoughVideoFrames)
        {
          if (frame.Type == Beam.Type.Video) // return only one video frame and all audio frames around it
            break;
          debugAudioFrameCount++;
          _frameList.RemoveAt(0);
          result.Add(frame);
        }
        else
        {
          if (frame.Type == Beam.Type.Video)
          {
            if ((_lastVideoTimestamp > 0) && ((long)(frame.Timestamp - _lastVideoTimestamp) > (long)_maxVideoTimestampDeviation))
            {
              Module.Log($"Missing video frame in frame buffer, timestamp deviation of {(long)(frame.Timestamp - _lastVideoTimestamp)} > max deviation of {_maxVideoTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);
              //TODO: add mode to keep the delay stable based on comparing the local system time to frame timestamps (need to fill gaps with dummy frames then)
              // it's not clear whether filling video frame gaps has any use outside of the "keep the delay stable" feature, e.g. missing video frames shouldn't trigger audio buffering, but that needs testing to be sure
              // if it _does_ has a separate use, offer a separate option for gap filling outside of the stable delay feature
            }
            _lastVideoTimestamp = frame.Timestamp;

            _videoFrameCount--;
            foundEnoughVideoFrames = (_videoFrameCount <= VideoFrameBufferCount); // only found enough video frames to return when the buffer is not filled more than its configured size
            debugVideoFrameCount++;
          }
          else if (frame.Type == Beam.Type.Audio)
          {
            debugAudioFrameCount++;
            if ((_lastAudioTimestamp > 0) && ((long)(frame.Timestamp - _lastAudioTimestamp) > (long)_maxAudioTimestampDeviation))
            {
              Module.Log($"Missing audio frame in frame buffer, timestamp deviation of {(long)(frame.Timestamp - _lastAudioTimestamp)} > max deviation of {_maxAudioTimestampDeviation}, consider increasing the frame buffer time if this happens frequently.", ObsLogLevel.Warning);
              //TODO: separate option to do this only for audio frames so that OBS doesn't trigger audio buffering
            }
            _lastAudioTimestamp = frame.Timestamp;
          }

          _frameList.RemoveAt(0);
          result.Add(frame);
        }
      }
      // Module.Log($"GetNextFrames returning {result.Count} frames ({debugVideoFrameCount} video and {debugAudioFrameCount} audio), {_frameList.Count} in buffer ({_videoFrameCount} video and {_frameList.Count - _videoFrameCount} audio).", ObsLogLevel.Debug);
      return result.ToArray();
    }
  }
}
