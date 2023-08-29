// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using ObsInterop;
namespace xObsBeam;

public class Beam
{

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

  public static SequencePosition GetReceiveTimestamp(ReadOnlySequence<byte> sequence, out ReceiveTimestampTypes receiveTimestampType, out ulong timestamp)
  {
    var reader = new SequenceReader<byte>(sequence);
    if (!reader.TryRead(out byte byteResult))
      throw new ArgumentException("Failed to read enough data from sequence.");
    if (!reader.TryReadLittleEndian(out long longResult))
      throw new ArgumentException("Failed to read enough data from sequence.");
    timestamp = (ulong)longResult;
    receiveTimestampType = (ReceiveTimestampTypes)byteResult;
    return reader.Position;
  }

  public static uint[] GetYuvPlaneSizes(video_format format, uint width, uint height)
  {
    uint halfHeight;
    uint halfwidth;
    uint[] planeSizes;

    switch (format)
    {
      //TODO: support more YUV formats for JPEG compression
      case video_format.VIDEO_FORMAT_NV12: // deinterleave and convert to I420
        halfHeight = (height + 1) / 2;
        halfwidth = width / 2;
        planeSizes = new uint[3];
        planeSizes[0] = (width * height);
        planeSizes[1] = (halfwidth * halfHeight);
        planeSizes[2] = (halfwidth * halfHeight);
        return planeSizes;
      default: // doesn't need to be deinterleaved or not supported
        return Array.Empty<uint>();
    }
  }

  public static unsafe uint[] GetPlaneSizes(video_format format, uint height, uint* linesize)
  {
    uint halfHeight = 0;
    uint[] planeSizes;
    // check https://github.com/obsproject/obs-studio/blob/master/libobs/obs-source.c copy_frame_data() for reference on how the planes are laid out
    switch (format)
    {
      case video_format.VIDEO_FORMAT_I420:
      case video_format.VIDEO_FORMAT_I010:
        halfHeight = (height + 1) / 2;
        planeSizes = new uint[3];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * halfHeight);
        planeSizes[2] = (linesize[2] * halfHeight);
        return planeSizes;
      case video_format.VIDEO_FORMAT_NV12:
      case video_format.VIDEO_FORMAT_P010:
        halfHeight = (height + 1) / 2;
        planeSizes = new uint[2];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * halfHeight);
        return planeSizes;
      case video_format.VIDEO_FORMAT_P216:
      case video_format.VIDEO_FORMAT_P416:
        planeSizes = new uint[2];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * height);
        return planeSizes;
      case video_format.VIDEO_FORMAT_I444:
      case video_format.VIDEO_FORMAT_I422:
      case video_format.VIDEO_FORMAT_I210:
      case video_format.VIDEO_FORMAT_I412:
        planeSizes = new uint[3];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * height);
        planeSizes[2] = (linesize[2] * height);
        return planeSizes;
      case video_format.VIDEO_FORMAT_YVYU:
      case video_format.VIDEO_FORMAT_YUY2:
      case video_format.VIDEO_FORMAT_UYVY:
      case video_format.VIDEO_FORMAT_NONE:
      case video_format.VIDEO_FORMAT_RGBA:
      case video_format.VIDEO_FORMAT_BGRA:
      case video_format.VIDEO_FORMAT_BGRX:
      case video_format.VIDEO_FORMAT_Y800:
      case video_format.VIDEO_FORMAT_BGR3:
      case video_format.VIDEO_FORMAT_AYUV:
      case video_format.VIDEO_FORMAT_V210:
        planeSizes = new uint[1];
        planeSizes[0] = (linesize[0] * height);
        return planeSizes;
      case video_format.VIDEO_FORMAT_I40A:
        halfHeight = (height + 1) / 2;
        planeSizes = new uint[4];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * halfHeight);
        planeSizes[2] = (linesize[2] * halfHeight);
        planeSizes[3] = (linesize[3] * height);
        return planeSizes;
      case video_format.VIDEO_FORMAT_I42A:
      case video_format.VIDEO_FORMAT_YUVA:
      case video_format.VIDEO_FORMAT_YA2L:
        halfHeight = (height + 1) / 2;
        planeSizes = new uint[4];
        planeSizes[0] = (linesize[0] * height);
        planeSizes[1] = (linesize[1] * height);
        planeSizes[2] = (linesize[2] * height);
        planeSizes[3] = (linesize[3] * height);
        return planeSizes;
      default:
        Module.Log($"Unsupported video format: {format}", ObsLogLevel.Error);
        return Array.Empty<uint>();
    }
  }

  public static unsafe void GetAudioDataSize(audio_format format, speaker_layout speakers, uint frames, out int audioDataSize, out int audioBytesPerSample)
  {
    audioBytesPerSample = 0;
    switch (format)
    {
      case audio_format.AUDIO_FORMAT_U8BIT:
      case audio_format.AUDIO_FORMAT_U8BIT_PLANAR:
        audioBytesPerSample = 1;
        break;
      case audio_format.AUDIO_FORMAT_16BIT:
      case audio_format.AUDIO_FORMAT_16BIT_PLANAR:
        audioBytesPerSample = 2;
        break;
      case audio_format.AUDIO_FORMAT_FLOAT:
      case audio_format.AUDIO_FORMAT_FLOAT_PLANAR:
      case audio_format.AUDIO_FORMAT_32BIT:
      case audio_format.AUDIO_FORMAT_32BIT_PLANAR:
        audioBytesPerSample = 4;
        break;
    }
    audioDataSize = audioBytesPerSample * (int)speakers * (int)frames;
  }

  public static unsafe BeamVideoData CreateEmptyVideoFrame(ulong timestamp, video_format format, uint height, uint* linesize)
  {
    var planeSizes = GetPlaneSizes(format, height, linesize);
    int videoDataSize = 0;
    for (int planeIndex = 0; planeIndex < planeSizes.Length; planeIndex++)
      videoDataSize += (int)planeSizes[planeIndex];
    var header = new VideoHeader()
    {
      Format = format,
      Width = linesize[0],
      Height = height,
      Colorspace = video_colorspace.VIDEO_CS_DEFAULT,
      Range = video_range_type.VIDEO_RANGE_DEFAULT,
      Timestamp = timestamp,
      Compression = CompressionTypes.None,
      Fps = 30,
      FpsDenominator = 1,
      DataSize = videoDataSize,
    };
    new ReadOnlySpan<uint>(linesize, VideoHeader.MAX_AV_PLANES).CopyTo(new Span<uint>(header.Linesize, VideoHeader.MAX_AV_PLANES));
    return new BeamVideoData(header, new byte[videoDataSize], timestamp);
  }

  public static unsafe BeamAudioData CreateEmptyAudioFrame(ulong timestamp, audio_format format, speaker_layout speakers, uint frames, uint sampleRate)
  {
    GetAudioDataSize(format, speakers, frames, out int audioDataSize, out _);
    var header = new AudioHeader()
    {
      Format = format,
      Speakers = speakers,
      Frames = frames,
      Timestamp = timestamp,
      DataSize = audioDataSize,
      SampleRate = sampleRate,
    };
    return new BeamAudioData(header, new byte[audioDataSize], timestamp);
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
    public readonly ulong Timestamp { get; }
    public ulong AdjustedTimestamp { get; set; }
    public DateTime Received { get; }

    // used by receivers
    public BeamAudioData(AudioHeader header, byte[] data, DateTime created)
    {
      Header = header;
      Data = data;
      Timestamp = header.Timestamp;
      AdjustedTimestamp = Timestamp;
      Received = created;
    }

    // used by senders
    public BeamAudioData(AudioHeader header, byte[] data, ulong timestamp)
    {
      Header = header;
      Data = data;
      Timestamp = timestamp;
    }
  }

  public enum Type : uint
  {
    Undefined = 0,
    Video = 1,
    Audio = 2
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
    public fixed uint Linesize[MAX_AV_PLANES];
    public uint Fps;
    public uint FpsDenominator;
    public video_format Format;
    public video_range_type Range;
    public video_colorspace Colorspace;
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
      // read uint linesize from the next 8 chunks of 4 bytes in header
      for (int i = 0; i < MAX_AV_PLANES; i++)
      {
        reader.TryReadLittleEndian(out tempInt);
        Linesize[i] = (uint)tempInt;
      }
      // read uint fps from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Fps = (uint)tempInt;
      // read uint fps denominator from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      FpsDenominator = (uint)tempInt;
      // read video_format enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Format = (video_format)tempInt;
      // read video_range_type enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Range = (video_range_type)tempInt;
      // read video_colorspace enum from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Colorspace = (video_colorspace)tempInt;
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
      for (int i = 0; i < MAX_AV_PLANES; i++)
      {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Linesize[i]);
        headerBytes += 4;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Fps); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), FpsDenominator); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Range); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Colorspace); headerBytes += 4;
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

      // log the values that have been read
      // Module.Log($"Audio DataSize: {DataSize}, SampleRate: {SampleRate}, Frames: {Frames}, Format: {Format}, Speakers: {Speakers}, Timestamp: {Timestamp}", ObsLogLevel.Debug);

      return reader.Position;
    }

    public int WriteTo(Span<byte> span, ulong timestamp)
    {
      int headerBytes = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), (uint)Type); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), DataSize); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), SampleRate); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Frames); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Speakers); headerBytes += 4;
      BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(headerBytes, 8), timestamp); headerBytes += 8;
      return headerBytes;
    }
  }
}

