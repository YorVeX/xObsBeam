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
  readonly List<Beam.IBeamData> _frameList = new();

  //TODO: make buffering more useful
  /*
  the current implementation solves only one specific problem, and this not even good:
  frames not received in the right order will be sorted if arriving within the buffering time, hence sent to the output with the right sorting.
  however, the frame that is late will be delayed by the buffering time, so the output will still receive it all with a delay.
  for the same reason A/V desyncs will not be corrected, because the audio frames will be delayed by the buffering time as well.
  and: if some frames are late due to a network lag they will be put delayed into the buffer and also retrieved with the same delay out of the buffer.

  so the big problem is, that the buffering time is applied to all frames, no matter if they are late or not.

  what about this:
  - from the buffering time calculate the number of frames to buffer (do that on BeamReceiver class already, FrameBuffer then only has a FrameBufferCount setting)
  - as long long as the buffer is not full, ProcessFrame just adds the frames to the buffer but doesn't return any frames
  - as soon as the buffer is full ProcessFrame will output a like for like frame, so one video frame for each input video frame and one audio frame for each input audio frame

  problems with this:
  - what happens if e.g. a few video frames are missing?
  - can we fill those gaps, i.e. duplicate the previous frame? otherwise we would have to break with the like for like frame returning
  - but when we don't receive frames for a while we also don't have the trigger to output an old frame, so we alternative means to trigger the output of an old frame
  - can we still use video_render and audio_render callbacks even though we're an async source? and use that all the time, or only to check whether a gap needs to be filled?
  */
  public int FrameBufferTimeMs { get; set; } = 1000;

  public void ProcessFrame(Beam.IBeamData frame)
  {
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

  public Beam.IBeamData[] GetNextFrames(int searchFrameCount)
  {
    var result = new List<Beam.IBeamData>(searchFrameCount);

    var now = DateTime.UtcNow;
    int frameIndex = 0;
    int searchCount = 0;
    while ((frameIndex < _frameList.Count) && (searchCount < searchFrameCount))
    {
      var frame = _frameList[frameIndex];
      if (now.Subtract(frame.Created).TotalMilliseconds >= FrameBufferTimeMs) // has this frame been long enough in the buffer?
      {
        _frameList.RemoveAt(frameIndex);
        result.Add(frame);
        if (result.Count >= searchFrameCount)
          break;
      }
      else
        frameIndex++;
      searchCount++;
    }
    return result.ToArray();
  }
}
