// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ObsInterop;
using LibJpegTurbo;
using QoirLib;
using DensityApi;

namespace xObsBeam;

enum Encoders
{
  LibJpegTurboV2,
  LibJpegTurboV3,
  Qoir,
  Density,
}

public static class EncoderSupport
{
  static readonly Dictionary<Encoders, bool> _checkResults = [];
  static readonly Dictionary<Encoders, bool> _checkResults = [];
  static readonly unsafe ConcurrentDictionary<IntPtr, (GCHandle, byte[])> _gcHandles = new();

#pragma warning disable CA1864 // in the below cases "ContainsKey" must be used first to determine whether the actual availability check should be performed at all, and race condition double-adds are handled by using a fire and forget TryAdd call
#pragma warning disable CA1864 // in the below cases "ContainsKey" must be used first to determine whether the actual availability check should be performed at all, and race condition double-adds are handled by using a fire and forget TryAdd call
  public static unsafe bool QoirLib
  {
    get
    {
      var encoder = Encoders.Qoir;
      if (!_checkResults.ContainsKey(encoder))
      {
        try
        {
          Qoir.qoir_encode(null, null);
          _checkResults.TryAdd(encoder, true);
          _checkResults.TryAdd(encoder, true);
        }
        catch (Exception ex)
        {
          _checkResults.Add(encoder, false);
          Module.Log($"{encoder} encoder availability check failed with {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
        }
        Module.Log($"{encoder} encoder is " + (_checkResults[encoder] ? "available." : "not available."), ObsLogLevel.Info);
      }
      return _checkResults[encoder];
    }
  }

  public static unsafe bool DensityApi
  {
    get
    {
      var encoder = Encoders.Density;
      if (!_checkResults.ContainsKey(encoder))
      {
        string densityVersionString = "";
        try
        {
          densityVersionString = " " + Density.density_version_major() + "." + Density.density_version_minor() + "." + Density.density_version_revision();
          _checkResults.TryAdd(encoder, true);
          _checkResults.TryAdd(encoder, true);
        }
        catch (Exception ex)
        {
          _checkResults.Add(encoder, false);
          Module.Log($"{encoder} encoder availability check failed with {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
        }
        Module.Log($"{encoder}{densityVersionString} encoder is " + (_checkResults[encoder] ? "available." : "not available."), ObsLogLevel.Info);
      }
      return _checkResults[encoder];
    }
  }

  public static unsafe bool LibJpegTurbo
  {
    get
    {
      var encoder = Encoders.LibJpegTurboV2;
      if (!_checkResults.ContainsKey(encoder))
      {
        try
        {
          _ = TurboJpeg.tjDestroy(TurboJpeg.tjInitCompress());
          _checkResults.TryAdd(encoder, true);
          _checkResults.TryAdd(encoder, true);
        }
        catch (Exception ex)
        {
          _checkResults.Add(encoder, false);
          Module.Log($"{encoder} encoder availability check failed with {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
        }
        Module.Log($"{encoder} encoder is " + (_checkResults[encoder] ? "available." : "not available."), ObsLogLevel.Info);
      }
      return _checkResults[encoder];
    }
  }

  public static unsafe bool LibJpegTurboV3
  {
    get
    {
      var encoder = Encoders.LibJpegTurboV3;
      if (!_checkResults.ContainsKey(encoder))
      {
        try
        {
          TurboJpeg.tj3Destroy(TurboJpeg.tj3Init((int)TJINIT.TJINIT_COMPRESS));
          _checkResults.TryAdd(encoder, true);
          _checkResults.TryAdd(encoder, true);
        }
        catch (Exception ex)
        {
          _checkResults.Add(encoder, false);
          Module.Log($"{encoder} encoder availability check failed with {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Debug);
        }
        Module.Log($"{encoder} encoder is " + (_checkResults[encoder] ? "available." : "not available."), ObsLogLevel.Info);
      }
      return _checkResults[encoder];
    }
  }
#pragma warning restore CA1864

  // check format_is_yuv function in OBS video-io.h for reference: https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/video-io.h
  public static bool FormatIsYuv(video_format format)
  {
#pragma warning disable IDE0066
    switch (format)
    {
      case video_format.VIDEO_FORMAT_I420:
      case video_format.VIDEO_FORMAT_NV12:
      case video_format.VIDEO_FORMAT_I422:
      case video_format.VIDEO_FORMAT_I210:
      case video_format.VIDEO_FORMAT_YVYU:
      case video_format.VIDEO_FORMAT_YUY2:
      case video_format.VIDEO_FORMAT_UYVY:
      case video_format.VIDEO_FORMAT_I444:
      case video_format.VIDEO_FORMAT_I412:
      case video_format.VIDEO_FORMAT_I40A:
      case video_format.VIDEO_FORMAT_I42A:
      case video_format.VIDEO_FORMAT_YUVA:
      case video_format.VIDEO_FORMAT_YA2L:
      case video_format.VIDEO_FORMAT_AYUV:
      case video_format.VIDEO_FORMAT_I010:
      case video_format.VIDEO_FORMAT_P010:
      case video_format.VIDEO_FORMAT_P216:
      case video_format.VIDEO_FORMAT_P416:
      case video_format.VIDEO_FORMAT_V210:
        return true;
      case video_format.VIDEO_FORMAT_NONE:
      case video_format.VIDEO_FORMAT_RGBA:
      case video_format.VIDEO_FORMAT_BGRA:
      case video_format.VIDEO_FORMAT_BGRX:
      case video_format.VIDEO_FORMAT_Y800:
      case video_format.VIDEO_FORMAT_BGR3:
        return false;
      default:
        return false;
    }
  }

  // check video_format comments in OBS video-io.h for reference: https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/video-io.h
  public static bool YuvFormatIsPacked(video_format format)
  {
    if (!FormatIsYuv(format))
      throw new InvalidOperationException("Not a YUV format");
    switch (format)
    {
      case video_format.VIDEO_FORMAT_NV12:
      case video_format.VIDEO_FORMAT_YVYU:
      case video_format.VIDEO_FORMAT_YUY2:
      case video_format.VIDEO_FORMAT_UYVY:
      case video_format.VIDEO_FORMAT_AYUV:
      case video_format.VIDEO_FORMAT_P216:
      case video_format.VIDEO_FORMAT_P416:
      case video_format.VIDEO_FORMAT_V210:
        return true;
      default:
        return false;
    }
  }

  public static TJSAMP ObsToJpegSubsampling(video_format obsVideoFormat)
  {
    switch (obsVideoFormat)
    {
      case video_format.VIDEO_FORMAT_Y800:
        return TJSAMP.TJSAMP_GRAY;
      case video_format.VIDEO_FORMAT_I420:
      case video_format.VIDEO_FORMAT_I40A:
      case video_format.VIDEO_FORMAT_I010:
      case video_format.VIDEO_FORMAT_NV12:
      case video_format.VIDEO_FORMAT_P010:
        return TJSAMP.TJSAMP_420;
      case video_format.VIDEO_FORMAT_I422:
      case video_format.VIDEO_FORMAT_I42A:
      case video_format.VIDEO_FORMAT_YVYU:
      case video_format.VIDEO_FORMAT_YUY2:
      case video_format.VIDEO_FORMAT_UYVY:
      case video_format.VIDEO_FORMAT_I210:
      case video_format.VIDEO_FORMAT_P216:
      case video_format.VIDEO_FORMAT_V210:
        return TJSAMP.TJSAMP_422;
      case video_format.VIDEO_FORMAT_I412:
      case video_format.VIDEO_FORMAT_I444:
      case video_format.VIDEO_FORMAT_AYUV:
      case video_format.VIDEO_FORMAT_YUVA:
      case video_format.VIDEO_FORMAT_YA2L:
      case video_format.VIDEO_FORMAT_P416:
        return TJSAMP.TJSAMP_444;
      case video_format.VIDEO_FORMAT_NONE:
      case video_format.VIDEO_FORMAT_RGBA:
      case video_format.VIDEO_FORMAT_BGRA:
      case video_format.VIDEO_FORMAT_BGRX:
      case video_format.VIDEO_FORMAT_BGR3:
        return TJSAMP.TJSAMP_444;
    }
    return TJSAMP.TJSAMP_444;
  }
#pragma warning restore IDE0066

  public static TJPF ObsToJpegPixelFormat(video_format obsVideoFormat)
  {
    return obsVideoFormat switch
    {
      video_format.VIDEO_FORMAT_BGR3 => TJPF.TJPF_BGR,
      video_format.VIDEO_FORMAT_BGRA => TJPF.TJPF_BGRA,
      video_format.VIDEO_FORMAT_BGRX => TJPF.TJPF_BGRX,
      video_format.VIDEO_FORMAT_RGBA => TJPF.TJPF_RGBA,
      video_format.VIDEO_FORMAT_Y800 => TJPF.TJPF_GRAY,
      _ => TJPF.TJPF_UNKNOWN
    };
  }

  public static video_format JpegDropAlpha(video_format obsVideoFormat)
  {
    return obsVideoFormat switch
    {
      video_format.VIDEO_FORMAT_I40A => video_format.VIDEO_FORMAT_I420,
      video_format.VIDEO_FORMAT_I42A => video_format.VIDEO_FORMAT_I422,
      video_format.VIDEO_FORMAT_YUVA => video_format.VIDEO_FORMAT_I444,
      _ => obsVideoFormat
    };
  }

  public static TJCS ObsToJpegColorSpace(video_format obsVideoFormat)
  {
    if (obsVideoFormat == video_format.VIDEO_FORMAT_Y800)
      return TJCS.TJCS_GRAY;
    return (FormatIsYuv(obsVideoFormat)) ? TJCS.TJCS_YCbCr : TJCS.TJCS_RGB;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static unsafe void Nv12ToI420(byte* sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoNv12, Beam.VideoPlaneInfo planeInfoI420)
  {
    // copy the Y plane
    new ReadOnlySpan<byte>(sourceBuffer, (int)planeInfoNv12.PlaneSizes[0]).CopyTo(destinationBuffer);

    // copy and deinterleave the UV plane
    byte* uvPlane = sourceBuffer + planeInfoNv12.Offsets[1];
    int chromaPlaneSize = (int)planeInfoI420.PlaneSizes[1];
    var uPlane = destinationBuffer.Slice((int)planeInfoI420.Offsets[1], (int)planeInfoI420.PlaneSizes[1]);
    var vPlane = destinationBuffer.Slice((int)planeInfoI420.Offsets[2], (int)planeInfoI420.PlaneSizes[2]);
    for (int i = 0; i < chromaPlaneSize; i++)
    {
      uPlane[i] = uvPlane[(2 * i) + 0];
      vPlane[i] = uvPlane[(2 * i) + 1];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void I420ToNv12(Span<byte> sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoNv12, Beam.VideoPlaneInfo planeInfoI420)
  {
    // copy the Y plane
    sourceBuffer[..(int)planeInfoI420.PlaneSizes[0]].CopyTo(destinationBuffer);

    // interleave the U and V planes into a single UV plane
    var uPlane = sourceBuffer.Slice((int)planeInfoI420.Offsets[1], (int)planeInfoI420.PlaneSizes[1]);
    var vPlane = sourceBuffer.Slice((int)planeInfoI420.Offsets[2], (int)planeInfoI420.PlaneSizes[2]);
    int uvPlaneSize = (int)(planeInfoNv12.PlaneSizes[1]);
    var uvPlane = destinationBuffer.Slice((int)planeInfoNv12.Offsets[1], uvPlaneSize);
    var chromaPlaneSize = (int)planeInfoI420.PlaneSizes[1]; // in theory ((PlaneSizes[1] + PlaneSizes[2]) / 2), but PlaneSizes[1] == PlaneSizes[2] for I420
    for (int i = 0; i < chromaPlaneSize; i++)
    {
      uvPlane[(2 * i) + 0] = uPlane[i];
      uvPlane[(2 * i) + 1] = vPlane[i];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static unsafe void YvyuToI422(byte* sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // copy and deinterleave the UV plane
    var yPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);

    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      yPlane[i] = sourceBuffer[i * 2];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      uPlane[i] = sourceBuffer[(4 * i) + 3];
      vPlane[i] = sourceBuffer[(4 * i) + 1];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void I422ToYvyu(Span<byte> sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // interleave the Y, U and V planes into a single packed plane
    var yPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);
    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      destinationBuffer[i * 2] = yPlane[i];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      destinationBuffer[(4 * i) + 3] = uPlane[i];
      destinationBuffer[(4 * i) + 1] = vPlane[i];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static unsafe void UyvyToI422(byte* sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // copy and deinterleave the UV plane
    var yPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);

    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      yPlane[i] = sourceBuffer[(i * 2) + 1];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      uPlane[i] = sourceBuffer[(4 * i) + 0];
      vPlane[i] = sourceBuffer[(4 * i) + 2];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void I422ToUyvy(Span<byte> sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // interleave the Y, U and V planes into a single packed plane
    var yPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);
    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      destinationBuffer[(i * 2) + 1] = yPlane[i];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      destinationBuffer[(4 * i) + 0] = uPlane[i];
      destinationBuffer[(4 * i) + 2] = vPlane[i];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static unsafe void Yuy2ToI422(byte* sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // copy and deinterleave the UV plane
    var yPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = destinationBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);

    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      yPlane[i] = sourceBuffer[i * 2];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      uPlane[i] = sourceBuffer[(4 * i) + 1];
      vPlane[i] = sourceBuffer[(4 * i) + 3];
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void I422ToYuy2(Span<byte> sourceBuffer, Span<byte> destinationBuffer, Beam.VideoPlaneInfo planeInfoI422)
  {
    // interleave the Y, U and V planes into a single packed plane
    var yPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[0], (int)planeInfoI422.PlaneSizes[0]);
    var uPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[1], (int)planeInfoI422.PlaneSizes[1]);
    var vPlane = sourceBuffer.Slice((int)planeInfoI422.Offsets[2], (int)planeInfoI422.PlaneSizes[2]);
    for (int i = 0; i < planeInfoI422.PlaneSizes[0]; i++)
      destinationBuffer[i * 2] = yPlane[i];
    for (int i = 0; i < planeInfoI422.PlaneSizes[1]; i++)
    {
      destinationBuffer[(4 * i) + 1] = uPlane[i];
      destinationBuffer[(4 * i) + 3] = vPlane[i];
    }
  }

#pragma warning disable IDE0060 // we don't make use of the memory_func_context parameter but it needs to be there

  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void* QoirMAlloc(void* memory_func_context, nuint len)
  {
    var pooledByteArray = ArrayPool<byte>.Shared.Rent((int)len);
    var gcHandle = GCHandle.Alloc(pooledByteArray, GCHandleType.Pinned); // pin the array so it can be used by unmanaged code
    var arrayTuple = (gcHandle, pooledByteArray);
    var pinnedHandle = gcHandle.AddrOfPinnedObject();
    _gcHandles.AddOrUpdate(pinnedHandle, arrayTuple, (key, oldArrayTuple) => arrayTuple);
    return pinnedHandle.ToPointer();
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void QoirFree(void* memory_func_context, void* ptr)
  {
    _gcHandles.Remove((IntPtr)ptr, out var arrayTuple);
    arrayTuple.Item1.Free(); // free the pinned GC handle
    ArrayPool<byte>.Shared.Return(arrayTuple.Item2); // return the allocated array memory to the pool
  }

#pragma warning restore IDE0060 // we don't make use of the memory_func_context parameter but it needs to be there

  public static unsafe T* MAllocPooledPinned<T>() where T : unmanaged
  {
    var pooledByteArray = ArrayPool<byte>.Shared.Rent(sizeof(T));
    var gcHandle = GCHandle.Alloc(pooledByteArray, GCHandleType.Pinned); // pin the array so it can be used by unmanaged code
    var arrayTuple = (gcHandle, pooledByteArray);
    var pinnedHandle = gcHandle.AddrOfPinnedObject();
    _gcHandles.AddOrUpdate(pinnedHandle, arrayTuple, (key, oldArrayTuple) => arrayTuple);
    return (T*)pinnedHandle.ToPointer();
  }

  public static unsafe void FreePooledPinned(void* ptr)
  {
    _gcHandles.Remove((IntPtr)ptr, out var arrayTuple);
    arrayTuple.Item1.Free(); // free the pinned GC handle
    ArrayPool<byte>.Shared.Return(arrayTuple.Item2); // return the allocated array memory to the pool
  }
}
