// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ObsInterop;

namespace xObsBeam;


public class Beam
{

  public enum CompressionTypes : int
  {
    None = 0,
    Qoi = 1,
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

  public static SequencePosition GetTimestamp(ReadOnlySequence<byte> sequence, out ulong timestamp)
  {
    var reader = new SequenceReader<byte>(sequence);
    if (!reader.TryReadLittleEndian(out long longResult))
      throw new ArgumentException("Failed to read enough data from sequence.");
    timestamp = (ulong)longResult;
    return reader.Position;
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
        // case video_format.VIDEO_FORMAT_V210: // newer OBS
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
        return new uint[0];
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
  #endregion helper methods

  public interface IBeamData
  {
    Type Type { get; }
  }

  public struct BeamVideoData : IBeamData
  {
    public Type Type { get; } = Type.Video;
    public VideoHeader Header;
    public byte[] Data;
    public ulong Timestamp;

    public BeamVideoData(VideoHeader header, byte[] data)
    {
      Header = header;
      Data = data;
    }

    public BeamVideoData(VideoHeader header, byte[] data, ulong timestamp)
    {
      Header = header;
      Data = data;
      Timestamp = timestamp;
    }
  }

  public struct BeamAudioData : IBeamData
  {
    public Type Type { get; } = Type.Audio;
    public AudioHeader Header;
    public byte[] Data;
    public ulong Timestamp;

    public BeamAudioData(AudioHeader header, byte[] data)
    {
      Header = header;
      Data = data;
    }

    public BeamAudioData(AudioHeader header, byte[] data, ulong timestamp)
    {
      Header = header;
      Data = data;
      Timestamp = timestamp;
    }
  }

  public enum Type : uint
  {
    Video = 0,
    Audio = 1
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
    public uint[] Linesize = new uint[MAX_AV_PLANES];
    // public fixed uint Linesize[MAX_AV_PLANES]; 
    public uint Fps;
    public video_format Format;
    public video_range_type Range;
    public video_colorspace Colorspace;
    public ulong Timestamp;

    public VideoHeader()
    {
    }

    public static int VideoHeaderDataSize
    {
      get
      {
        if (_headerDataSize == -1)
          _headerDataSize = Marshal.SizeOf<VideoHeader>();
        return _headerDataSize;
      }
    }

    public SequencePosition FromSequence(ReadOnlySequence<byte> header)
    {
      var reader = new SequenceReader<byte>(header);

      int tempInt; // only needed for casting, annoying but necessary until this is implemented: https://github.com/dotnet/runtime/issues/30580

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
      for (int i = 0; i < VideoHeader.MAX_AV_PLANES; i++)
      {
        reader.TryReadLittleEndian(out tempInt);
        Linesize[i] = (uint)tempInt;
      }
      // read uint fps from the next 4 bytes in header
      reader.TryReadLittleEndian(out tempInt);
      Fps = (uint)tempInt;
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

      // log the values that have been read
      // Module.Log($"Video DataSize: {DataSize}, Width: {Width}, Height: {Height}, Linesize: {Linesize}, Format: {Format}, Range: {Range}, Rimestamp: {Timestamp}", ObsLogLevel.Debug);

      return reader.Position;
    }

    public int WriteTo(Span<byte> span, ulong timestamp)
    {
      int headerBytes = 0;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), (uint)Type); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Compression); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), DataSize); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Width); headerBytes += 4;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Height); headerBytes += 4;
      for (int i = 0; i < VideoHeader.MAX_AV_PLANES; i++)
      {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Linesize[i]);
        headerBytes += 4;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(headerBytes, 4), Fps); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Format); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Range); headerBytes += 4;
      BinaryPrimitives.WriteInt32LittleEndian(span.Slice(headerBytes, 4), (int)Colorspace); headerBytes += 4;
      BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(headerBytes, 8), timestamp); headerBytes += 8;
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
          _headerDataSize = Marshal.SizeOf<AudioHeader>();
        return _headerDataSize;
      }
    }

    public SequencePosition FromSequence(ReadOnlySequence<byte> header)
    {
      var reader = new SequenceReader<byte>(header);

      int tempInt; // only needed for casting, annoying but necessary until this is implemented: https://github.com/dotnet/runtime/issues/30580

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

