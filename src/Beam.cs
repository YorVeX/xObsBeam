// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ObsInterop;
namespace xObsBeam;

public class Beam
{
  public const int ReceiveTimestampLength = (sizeof(byte) + sizeof(long));
  public const int MaxReceiveTimestampLength = ReceiveTimestampLength * 4; // sometimes we might get timestamps of the same type in a row, they then shouldn't make us discard the other type

  public enum SenderTypes
  {
    None,
    Relay,
    Output,
    FilterAudioVideo,
    FilterAudio,
    FilterVideo,
  }

  public enum CompressionTypes : int
  {
    None = 0,
    Qoi = 1,
    Lz4 = 2,
    Jpeg = 3,
    Qoir = 4,
    Density = 5,
    Qoy = 6,
  }

  public enum ReceiveTimestampTypes : byte
  {
    Receive = 0,
    Render = 1,
  }
  #region helper methods
  /// <summary>Sequences could be split accross multiple spans, this method handles both split and non-split cases.</summary>
  public static Type GetBeamType(ReadOnlySequence<byte> sequence)
  {
    int result;
    if ((sequence.IsSingleSegment) || (sequence.FirstSpan.Length >= 4))
      result = BinaryPrimitives.ReadInt32LittleEndian(sequence.FirstSpan);
    else
    {
      var reader = new SequenceReader<byte>(sequence);
      if (!reader.TryReadLittleEndian(out result))
        throw new ArgumentException("Failed to read enough data from sequence.");
    }
    return (Type)result;
  }

  public static SequencePosition GetReceiveTimestamps(ReadOnlySequence<byte> sequence, string clientId, out ulong receiveTimestamp, out ulong renderTimestamp)
  {
    receiveTimestamp = 0;
    renderTimestamp = 0;
    var reader = new SequenceReader<byte>(sequence);
    if (reader.Length > MaxReceiveTimestampLength)
    {
      Module.Log($"<{clientId}> Discarding excessive timestamp info from receiver ({reader.Length - MaxReceiveTimestampLength} bytes).", ObsLogLevel.Debug);
      reader.Advance(reader.Length - MaxReceiveTimestampLength); // skip past outdated timestamp information to keep unnecessary work for the following loop to a minimum
    }
    while (reader.TryRead(out byte byteResult))
    {
      if (!reader.TryReadLittleEndian(out long longResult))
      {
        reader.Rewind(1); // leave the ReceiveTimestampTypes byte in the buffer for the next round
        break;
      }
      // with this loop if a timestamp type is received more than once, only the last one will be used
      if ((ReceiveTimestampTypes)byteResult == ReceiveTimestampTypes.Receive)
        receiveTimestamp = (ulong)longResult;
      else if ((ReceiveTimestampTypes)byteResult == ReceiveTimestampTypes.Render)
        renderTimestamp = (ulong)longResult;
    }
    return reader.Position;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static uint AlignSize(uint size, int align)
  {
    return (uint)(size + (align - 1)) & (uint)~(align - 1);
  }

  public struct VideoPlaneInfo
  {
    public static readonly VideoPlaneInfo Empty = new(0);

    public int Count { get; private set; }
    public uint[] Offsets { get; private set; }
    public uint[] Linesize { get; private set; }
    public uint[] PlaneSizes { get; set; }
    public uint DataSize { get; set; }

    public VideoPlaneInfo(int count)
    {
      Count = count;
      Offsets = new uint[count];
      Linesize = new uint[count];
      PlaneSizes = new uint[count];
    }

    public VideoPlaneInfo(int count, uint dataSize)
    {
      Count = count;
      Offsets = new uint[count];
      Linesize = new uint[count];
      PlaneSizes = new uint[count];
      DataSize = dataSize;
    }
  }

  public static unsafe VideoPlaneInfo GetVideoPlaneInfo(video_format format, uint width, uint height)
  {
    var planeInfo = VideoPlaneInfo.Empty;
    var alignment = ObsBmem.base_get_alignment();
    uint halfHeight;
    uint halfWidth;
    uint halfArea;
    uint halfAreaSize;
    uint cbCrWidth;

    // check https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/video-frame.c video_frame_init() for reference on how offsets and linesizes are calculated
    // for plane size information see https://github.com/obsproject/obs-studio/blob/master/libobs/obs-source.c copy_frame_data()

    switch (format)
    {
      case video_format.VIDEO_FORMAT_I420:
        planeInfo = new VideoPlaneInfo(3, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfHeight = (height + 1) / 2;
        var quarter_area = halfWidth * halfHeight;
        planeInfo.DataSize += quarter_area;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += quarter_area;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = halfWidth;
        planeInfo.Linesize[2] = halfWidth;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * halfHeight);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * halfHeight);
        return planeInfo;
      case video_format.VIDEO_FORMAT_NV12:
        planeInfo = new VideoPlaneInfo(2, width * height);
        halfHeight = (height + 1) / 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        cbCrWidth = (width + 1) & (uint.MaxValue - 1);
        planeInfo.DataSize += cbCrWidth * ((height + 1) / 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = cbCrWidth;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * halfHeight);
        return planeInfo;
      case video_format.VIDEO_FORMAT_Y800:
        planeInfo = new VideoPlaneInfo(1, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_YVYU:
      case video_format.VIDEO_FORMAT_YUY2:
      case video_format.VIDEO_FORMAT_UYVY:
        planeInfo = new VideoPlaneInfo(1);
        var double_width = ((width + 1) & (uint.MaxValue - 1)) * 2;
        planeInfo.DataSize = double_width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = double_width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_RGBA:
      case video_format.VIDEO_FORMAT_BGRA:
      case video_format.VIDEO_FORMAT_BGRX:
      case video_format.VIDEO_FORMAT_AYUV:
        planeInfo = new VideoPlaneInfo(1, width * height * 4);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 4;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I444:
        planeInfo = new VideoPlaneInfo(3, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = width;
        planeInfo.Linesize[2] = width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I412:
        planeInfo = new VideoPlaneInfo(3, width * height * 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        planeInfo.DataSize += width * height * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += width * height * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = width * 2;
        planeInfo.Linesize[2] = width * 2;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_BGR3:
        planeInfo = new VideoPlaneInfo(1, width * height * 3);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 3;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I422:
        planeInfo = new VideoPlaneInfo(3, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfArea = halfWidth * height;
        planeInfo.DataSize += halfArea;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += halfArea;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = halfWidth;
        planeInfo.Linesize[2] = halfWidth;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I210:
        planeInfo = new VideoPlaneInfo(3, width * height * 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfArea = halfWidth * height;
        halfAreaSize = 2 * halfArea;
        planeInfo.DataSize += halfAreaSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += halfAreaSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = halfWidth * 2;
        planeInfo.Linesize[2] = halfWidth * 2;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I40A:
        planeInfo = new VideoPlaneInfo(4, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfHeight = (height + 1) / 2;
        quarter_area = halfWidth * halfHeight;
        planeInfo.DataSize += quarter_area;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += quarter_area;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[3] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = halfWidth;
        planeInfo.Linesize[2] = halfWidth;
        planeInfo.Linesize[3] = width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * halfHeight);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * halfHeight);
        planeInfo.PlaneSizes[3] = (planeInfo.Linesize[3] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I42A:
        planeInfo = new VideoPlaneInfo(4, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfArea = halfWidth * height;
        planeInfo.DataSize += halfArea;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += halfArea;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[3] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = halfWidth;
        planeInfo.Linesize[2] = halfWidth;
        planeInfo.Linesize[3] = width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        planeInfo.PlaneSizes[3] = (planeInfo.Linesize[3] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_YUVA:
        planeInfo = new VideoPlaneInfo(4, width * height);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[3] = planeInfo.DataSize;
        planeInfo.DataSize += width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width;
        planeInfo.Linesize[1] = width;
        planeInfo.Linesize[2] = width;
        planeInfo.Linesize[3] = width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        planeInfo.PlaneSizes[3] = (planeInfo.Linesize[3] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_YA2L:
        planeInfo = new VideoPlaneInfo(4);
        var ya2lLinesize = width * 2;
        var planeSize = ya2lLinesize * height;
        planeInfo.DataSize = planeSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        planeInfo.DataSize += planeSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += planeSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[3] = planeInfo.DataSize;
        planeInfo.DataSize += planeSize;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = ya2lLinesize;
        planeInfo.Linesize[1] = ya2lLinesize;
        planeInfo.Linesize[2] = ya2lLinesize;
        planeInfo.Linesize[3] = ya2lLinesize;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * height);
        planeInfo.PlaneSizes[3] = (planeInfo.Linesize[3] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_I010:
        planeInfo = new VideoPlaneInfo(3, width * height * 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        halfWidth = (width + 1) / 2;
        halfHeight = (height + 1) / 2;
        quarter_area = halfWidth * halfHeight;
        planeInfo.DataSize += quarter_area * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[2] = planeInfo.DataSize;
        planeInfo.DataSize += quarter_area * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = halfWidth * 2;
        planeInfo.Linesize[2] = halfWidth * 2;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * halfHeight);
        planeInfo.PlaneSizes[2] = (planeInfo.Linesize[2] * halfHeight);
        return planeInfo;
      case video_format.VIDEO_FORMAT_P010:
        planeInfo = new VideoPlaneInfo(2, width * height * 2);
        halfHeight = (height + 1) / 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        cbCrWidth = (width + 1) & (uint.MaxValue - 1);
        planeInfo.DataSize += cbCrWidth * ((height + 1) / 2) * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = cbCrWidth * 2;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * halfHeight);
        return planeInfo;
      case video_format.VIDEO_FORMAT_P216:
        planeInfo = new VideoPlaneInfo(2, width * height * 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        cbCrWidth = (width + 1) & (uint.MaxValue - 1);
        planeInfo.DataSize += cbCrWidth * height * 2;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = cbCrWidth * 2;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_P416:
        planeInfo = new VideoPlaneInfo(2, width * height * 2);
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Offsets[1] = planeInfo.DataSize;
        planeInfo.DataSize += width * height * 4;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = width * 2;
        planeInfo.Linesize[1] = width * 4;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        planeInfo.PlaneSizes[1] = (planeInfo.Linesize[1] * height);
        return planeInfo;
      case video_format.VIDEO_FORMAT_V210:
        planeInfo = new VideoPlaneInfo(1);
        var adjusted_width = ((width + 5) / 6) * 16;
        planeInfo.DataSize = adjusted_width * height;
        planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
        planeInfo.Linesize[0] = adjusted_width;
        planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
        return planeInfo;
      // case video_format.VIDEO_FORMAT_R10L: // not yet, newer OBS version
      //   planeInfo = new PlaneInfo(1, width * height * 4);
      //   planeInfo.DataSize = AlignSize(planeInfo.DataSize, alignment);
      //   planeInfo.Linesize[0] = width * 4;
      //   planeInfo.PlaneSizes[0] = (planeInfo.Linesize[0] * height);
      //   return planeInfo;
      default:
        Module.Log($"Unsupported video format: {format}", ObsLogLevel.Error);
        return planeInfo;
    }
  }

  // check https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/audio-io.h for reference of these functions in OBS
  public static unsafe int GetAudioBytesPerChannel(audio_format format)
  {
    var audioBytesPerChannel = 0;
    switch (format)
    {
      case audio_format.AUDIO_FORMAT_U8BIT:
      case audio_format.AUDIO_FORMAT_U8BIT_PLANAR:
        audioBytesPerChannel = 1;
        break;
      case audio_format.AUDIO_FORMAT_16BIT:
      case audio_format.AUDIO_FORMAT_16BIT_PLANAR:
        audioBytesPerChannel = 2;
        break;
      case audio_format.AUDIO_FORMAT_FLOAT:
      case audio_format.AUDIO_FORMAT_FLOAT_PLANAR:
      case audio_format.AUDIO_FORMAT_32BIT:
      case audio_format.AUDIO_FORMAT_32BIT_PLANAR:
        audioBytesPerChannel = 4;
        break;
    }
    return audioBytesPerChannel;
  }

  public static int GetAudioChannels(speaker_layout speakers)
  {
    return speakers switch
    {
      speaker_layout.SPEAKERS_MONO => 1,
      speaker_layout.SPEAKERS_STEREO => 2,
      speaker_layout.SPEAKERS_2POINT1 => 3,
      speaker_layout.SPEAKERS_4POINT0 => 4,
      speaker_layout.SPEAKERS_4POINT1 => 5,
      speaker_layout.SPEAKERS_5POINT1 => 6,
      speaker_layout.SPEAKERS_7POINT1 => 8,
      speaker_layout.SPEAKERS_UNKNOWN => 0,
      _ => 0,
    };
  }

  public static unsafe bool IsAudioPlanar(audio_format format)
  {
    return format switch
    {
      audio_format.AUDIO_FORMAT_U8BIT or audio_format.AUDIO_FORMAT_16BIT or audio_format.AUDIO_FORMAT_32BIT or audio_format.AUDIO_FORMAT_FLOAT => false,
      audio_format.AUDIO_FORMAT_U8BIT_PLANAR or audio_format.AUDIO_FORMAT_FLOAT_PLANAR or audio_format.AUDIO_FORMAT_16BIT_PLANAR or audio_format.AUDIO_FORMAT_32BIT_PLANAR => true,
      audio_format.AUDIO_FORMAT_UNKNOWN => false,
      _ => false,
    };
  }

  public static unsafe void GetAudioPlaneInfo(audio_format format, speaker_layout speakers, out int audioPlanes, out uint audioBytesPerChannel)
  {
    audioPlanes = (IsAudioPlanar(format) ? GetAudioChannels(speakers) : 1);
    audioBytesPerChannel = (uint)GetAudioBytesPerChannel(format);
  }
  #endregion helper methods

  public interface IBeamData
  {
    Type Type { get; set; }
    ulong Timestamp { get; }
    ulong AdjustedTimestamp { get; set; }
    DateTime Received { get; }
  }

  public struct BeamVideoData : IBeamData
  {
    public Type Type { get; set; } = Type.Video;
    public VideoHeader Header;
    public readonly byte[] Data;
    public ulong Timestamp { get; set; }
    public ulong AdjustedTimestamp { get; set; }
    public DateTime Received { get; }
    public int RenderDelayAverage { get; }

    // used by receivers
    public BeamVideoData(VideoHeader header, byte[] data, DateTime received, int renderDelayAverage)
    {
      Type = header.Type;
      Header = header;
      Data = data;
      Timestamp = header.Timestamp;
      AdjustedTimestamp = Timestamp;
      Received = received;
      RenderDelayAverage = renderDelayAverage;
    }

    // used by senders
    public BeamVideoData(VideoHeader header, byte[] data, ulong timestamp)
    {
      Type = header.Type;
      Header = header;
      Data = data;
      Timestamp = timestamp;
    }
  }

  public struct BeamAudioData : IBeamData
  {
    public Type Type { get; set; } = Type.Audio;
    public AudioHeader Header;
    public readonly byte[] Data;
    public readonly uint Frames { get; }
    public readonly int DataSize { get; }
    public readonly ulong Timestamp { get; }
    public ulong AdjustedTimestamp { get; set; }
    public DateTime Received { get; }
    public int RenderDelayAverage { get; }

    // used by receivers
    public BeamAudioData(AudioHeader header, byte[] data, DateTime created, int renderDelayAverage)
    {
      Type = header.Type;
      Header = header;
      Data = data;
      Frames = header.Frames;
      Timestamp = header.Timestamp;
      AdjustedTimestamp = Timestamp;
      Received = created;
      RenderDelayAverage = renderDelayAverage;
    }

    // used by senders
    public BeamAudioData(AudioHeader header, byte[] data, uint frames, int dataSize, ulong timestamp)
    {
      Type = header.Type;
      Header = header;
      DataSize = dataSize;
      Data = data;
      Frames = frames;
      Timestamp = timestamp;
    }
  }

  public enum Type : uint
  {
    Undefined = 0,
    Video = 1,
    Audio = 2,
    VideoOnly = 3,
    AudioOnly = 4,
  }

  public unsafe struct VideoHeader
  {
    public const int MAX_AV_PLANES = 8; // it's not in NetObsBindings but probably doesn't change a lot, currently it's defined here: https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/media-io-defs.h

    static int _headerDataSize = -1;

    public Type Type = Type.Video;
    public CompressionTypes Compression;
    public int DataSize;
    public uint Width;
    public uint Height;
    public uint Fps;
    public uint FpsDenominator;
    public video_format Format;
    public byte FullRange;
    public fixed float ColorMatrix[16];
    public fixed float ColorRangeMin[3];
    public fixed float ColorRangeMax[3];
    public ulong Timestamp;
    public int ReceiveDelay;
    public int RenderDelay;

    public VideoHeader()
    {
    }

    public static int VideoHeaderDataSize
    {
      get
      {
        if (_headerDataSize == -1)
          _headerDataSize = sizeof(VideoHeader);
        return _headerDataSize;
      }
    }

    public SequencePosition FromSequence(ReadOnlySequence<byte> header)
    {
      var reader = new SequenceReader<byte>(header);

#pragma warning disable IDE0018 // explicit declaration for better readability and visibility of the below comment
      int tempInt; // only needed for casting, annoying but necessary until this is implemented: https://github.com/dotnet/runtime/issues/30580
#pragma warning restore IDE0018

      // read uint Type from the first 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Type = (Type)tempInt;
      // read CompressionType enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Compression = (CompressionTypes)tempInt;
      // read int video DataSize from the next 4 bytes in header
      reader.TryReadLittleEndian(out DataSize);
      // read uint width from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Width = (uint)tempInt;
      // read uint height from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Height = (uint)tempInt;
      // read uint fps from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Fps = (uint)tempInt;
      // read uint fps denominator from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      FpsDenominator = (uint)tempInt;
      // read video_format enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Format = (video_format)tempInt;
      // read full_range from the next byte in header
      reader.TryRead(out FullRange);
      // read float color_range from the next 16 chunks of 4 bytes in header
      for (int i = 0; i < 16; i++)
      {
        reader.TryReadLittleEndian(out tempInt);
        ColorMatrix[i] = BitConverter.Int32BitsToSingle(tempInt);
      }
      // read float min_range from the next 3 chunks of 4 bytes in header
      for (int i = 0; i < 3; i++)
      {
        reader.TryReadLittleEndian(out tempInt);
        ColorRangeMin[i] = BitConverter.Int32BitsToSingle(tempInt);
      }
      // read float max_range from the next 3 chunks of 4 bytes in header
      for (int i = 0; i < 3; i++)
      {
        reader.TryReadLittleEndian(out tempInt);
        ColorRangeMax[i] = BitConverter.Int32BitsToSingle(tempInt);
      }
      // read timestamp from the next 8 bytes in header
      reader.TryReadLittleEndian(out long timestamp);
      Timestamp = (ulong)timestamp;
      // read int receive delay from the next 4 bytes in header
      reader.TryReadLittleEndian(out ReceiveDelay);
      // read int render delay from the next 4 bytes in header
      reader.TryReadLittleEndian(out RenderDelay);

      // log the values that have been read
      // Module.Log($"Video Type: {Type}, Compression: {Compression}, DataSize: {DataSize}, Width: {Width}, Height: {Height}, FPS: {Fps}, Format: {Format}, Range: {Range}, Colorspace: {Colorspace}, Timestamp: {Timestamp}", ObsLogLevel.Debug);

      return reader.Position;
    }

    public int WriteTo(Span<byte> span, ulong timestamp, int receiveDelay, int renderDelay)
    {
      // some values are handed over as parameters instead of being set directly to the fields so that this header instance can be reused (avoid unnecessary allocations) by the calling methods

      int headerBytes = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), (uint)Type); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Compression); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), DataSize); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Width); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Height); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Fps); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), FpsDenominator); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      span[headerBytes] = FullRange; headerBytes += 1;
      for (int i = 0; i < 16; i++)
      {
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(headerBytes, 4), ColorMatrix[i]);
        headerBytes += 4;
      }
      for (int i = 0; i < 3; i++)
      {
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(headerBytes, 4), ColorRangeMin[i]);
        headerBytes += 4;
      }
      for (int i = 0; i < 3; i++)
      {
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(headerBytes, 4), ColorRangeMax[i]);
        headerBytes += 4;
      }
      BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(headerBytes, 8), timestamp); headerBytes += 8;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), receiveDelay); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), renderDelay); headerBytes += 4;
      return headerBytes;
    }
  }

  public unsafe struct AudioHeader
  {
    static int _headerDataSize = -1;

    public Type Type = Type.Audio;
    public int DataSize;
    public audio_format Format;
    public uint SampleRate;
    public speaker_layout Speakers;
    public uint Frames;
    public ulong Timestamp;
    public int ReceiveDelay;
    public int RenderDelay;

    public AudioHeader()
    {
    }

    public static int AudioHeaderDataSize
    {
      get
      {
        if (_headerDataSize == -1)
          _headerDataSize = sizeof(AudioHeader);
        return _headerDataSize;
      }
    }

    public SequencePosition FromSequence(ReadOnlySequence<byte> header)
    {
      var reader = new SequenceReader<byte>(header);

#pragma warning disable IDE0018 // explicit declaration for better readability and visibility of the below comment
      int tempInt; // only needed for casting, annoying but necessary until this is implemented: https://github.com/dotnet/runtime/issues/30580
#pragma warning restore IDE0018

      // read uint Type from the first 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Type = (Type)tempInt;
      // read long audio DataSize from the next 4 bytes in header
      reader.TryReadLittleEndian(out DataSize);
      // read uint sample rate from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      SampleRate = (uint)tempInt;
      // read uint frames from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Frames = (uint)tempInt;
      // read audio_format enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Format = (audio_format)tempInt;
      // read speaker_layout enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Speakers = (speaker_layout)tempInt;
      // read timestamp from the next 8 bytes in header
      reader.TryReadLittleEndian(out long timestamp);
      Timestamp = (ulong)timestamp;
      if (Type == Type.AudioOnly)
      {
        // read int receive delay from the next 4 bytes in header
        reader.TryReadLittleEndian(out ReceiveDelay);
        // read int render delay from the next 4 bytes in header
        reader.TryReadLittleEndian(out RenderDelay);
      }

      // log the values that have been read
      // Module.Log($"Audio DataSize: {DataSize}, SampleRate: {SampleRate}, Frames: {Frames}, Format: {Format}, Speakers: {Speakers}, Timestamp: {Timestamp}", ObsLogLevel.Debug);

      return reader.Position;
    }

    public int WriteTo(Span<byte> span, uint frames, int dataSize, ulong timestamp)
    {
      int headerBytes = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), (uint)Type); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), dataSize); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), SampleRate); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), frames); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Speakers); headerBytes += 4;
      BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(headerBytes, 8), timestamp); headerBytes += 8;
      return headerBytes;
    }

    public int WriteTo(Span<byte> span, uint frames, int dataSize, ulong timestamp, int receiveDelay = 0, int renderDelay = 0)
    {
      int headerBytes = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), (uint)Type); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), dataSize); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), SampleRate); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), frames); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Speakers); headerBytes += 4;
      BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(headerBytes, 8), timestamp); headerBytes += 8;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), receiveDelay); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), renderDelay); headerBytes += 4;
      return headerBytes;
    }
  }
}

