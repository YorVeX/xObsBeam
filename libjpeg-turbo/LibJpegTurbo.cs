// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using ClangSharp;
using System.Runtime.InteropServices;
using static LibJpegTurbo.TJSAMP;

namespace LibJpegTurbo
{
  public enum TJINIT
  {
    TJINIT_COMPRESS,
    TJINIT_DECOMPRESS,
    TJINIT_TRANSFORM,
  }

  public enum TJSAMP
  {
    TJSAMP_444,
    TJSAMP_422,
    TJSAMP_420,
    TJSAMP_GRAY,
    TJSAMP_440,
    TJSAMP_411,
    TJSAMP_UNKNOWN = -1,
  }

  public enum TJPF
  {
    TJPF_RGB,
    TJPF_BGR,
    TJPF_RGBX,
    TJPF_BGRX,
    TJPF_XBGR,
    TJPF_XRGB,
    TJPF_GRAY,
    TJPF_RGBA,
    TJPF_BGRA,
    TJPF_ABGR,
    TJPF_ARGB,
    TJPF_CMYK,
    TJPF_UNKNOWN = -1,
  }

  public enum TJCS
  {
    TJCS_RGB,
    TJCS_YCbCr,
    TJCS_GRAY,
    TJCS_CMYK,
    TJCS_YCCK,
  }

  public enum TJPARAM
  {
    TJPARAM_STOPONWARNING,
    TJPARAM_BOTTOMUP,
    TJPARAM_NOREALLOC,
    TJPARAM_QUALITY,
    TJPARAM_SUBSAMP,
    TJPARAM_JPEGWIDTH,
    TJPARAM_JPEGHEIGHT,
    TJPARAM_PRECISION,
    TJPARAM_COLORSPACE,
    TJPARAM_FASTUPSAMPLE,
    TJPARAM_FASTDCT,
    TJPARAM_OPTIMIZE,
    TJPARAM_PROGRESSIVE,
    TJPARAM_SCANLIMIT,
    TJPARAM_ARITHMETIC,
    TJPARAM_LOSSLESS,
    TJPARAM_LOSSLESSPSV,
    TJPARAM_LOSSLESSPT,
    TJPARAM_RESTARTBLOCKS,
    TJPARAM_RESTARTROWS,
    TJPARAM_XDENSITY,
    TJPARAM_YDENSITY,
    TJPARAM_DENSITYUNITS,
  }

  public enum TJERR
  {
    TJERR_WARNING,
    TJERR_FATAL,
  }

  public enum TJXOP
  {
    TJXOP_NONE,
    TJXOP_HFLIP,
    TJXOP_VFLIP,
    TJXOP_TRANSPOSE,
    TJXOP_TRANSVERSE,
    TJXOP_ROT90,
    TJXOP_ROT180,
    TJXOP_ROT270,
  }

  public partial struct TJScalingFactor
  {
    public int num;

    public int denom;
  }

  public partial struct TJRegion
  {
    public int x;

    public int y;

    public int w;

    public int h;
  }

  public unsafe partial struct TJTransform
  {
    public TJRegion r;

    public int op;

    public int options;

    public void* data;

    [NativeTypeName("int (*)(short *, tjregion, tjregion, int, int, struct tjtransform *)")]
    public delegate* unmanaged[Cdecl]<short*, TJRegion, TJRegion, int, int, TJTransform*, int> customFilter;
  }

  public static unsafe class TurboJpeg
  {
    [NativeTypeName("const int[6]")]
    public static readonly int[] tjMCUWidth = new int[6]
    {
      8,
      16,
      16,
      8,
      8,
      32,
    };

    [NativeTypeName("const int[6]")]
    public static readonly int[] tjMCUHeight = new int[6]
    {
      8,
      8,
      16,
      8,
      16,
      8,
    };

    [NativeTypeName("const int[12]")]
    public static readonly int[] tjRedOffset = new int[12]
    {
      0,
      2,
      0,
      2,
      3,
      1,
      -1,
      0,
      2,
      3,
      1,
      -1,
    };

    [NativeTypeName("const int[12]")]
    public static readonly int[] tjGreenOffset = new int[12]
    {
      1,
      1,
      1,
      1,
      2,
      2,
      -1,
      1,
      1,
      2,
      2,
      -1,
    };

    [NativeTypeName("const int[12]")]
    public static readonly int[] tjBlueOffset = new int[12]
    {
      2,
      0,
      2,
      0,
      1,
      3,
      -1,
      2,
      0,
      1,
      3,
      -1,
    };

    [NativeTypeName("const int[12]")]
    public static readonly int[] tjAlphaOffset = new int[12]
    {
      -1,
      -1,
      -1,
      -1,
      -1,
      -1,
      -1,
      3,
      3,
      0,
      0,
      -1,
    };

    [NativeTypeName("const int[12]")]
    public static readonly int[] tjPixelSize = new int[12]
    {
      3,
      3,
      4,
      4,
      4,
      4,
      1,
      4,
      4,
      4,
      4,
      4,
    };

    [NativeTypeName("const tjregion")]
    public static readonly TJRegion TJUNCROPPED = new()
    {
      x = 0,
      y = 0,
      w = 0,
      h = 0,
    };

    [NativeTypeName("const tjscalingfactor")]
    public static readonly TJScalingFactor TJUNSCALED = new()
    {
      num = 1,
      denom = 1,
    };

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("tjhandle")]
    public static extern void* tj3Init(int initType);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Set([NativeTypeName("tjhandle")] void* handle, int param1, int value);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Get([NativeTypeName("tjhandle")] void* handle, int param1);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Compress8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("size_t *")] nuint* jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Compress12([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const short *")] short* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("size_t *")] nuint* jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Compress16([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned short *")] ushort* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("size_t *")] nuint* jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3CompressFromYUV8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int align, int height, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("size_t *")] nuint* jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3CompressFromYUVPlanes8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *const *")] byte** srcPlanes, int width, [NativeTypeName("const int *")] int* strides, int height, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("size_t *")] nuint* jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint tj3JPEGBufSize(int width, int height, int jpegSubsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint tj3YUVBufSize(int width, int align, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint tj3YUVPlaneSize(int componentID, int width, int stride, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3YUVPlaneWidth(int componentID, int width, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3YUVPlaneHeight(int componentID, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3EncodeYUV8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char *")] byte* dstBuf, int align);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3EncodeYUVPlanes8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** dstPlanes, int* strides);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3DecompressHeader([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern TJScalingFactor* tj3GetScalingFactors(int* numScalingFactors);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3SetScalingFactor([NativeTypeName("tjhandle")] void* handle, TJScalingFactor scalingFactor);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3SetCroppingRegion([NativeTypeName("tjhandle")] void* handle, TJRegion croppingRegion);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Decompress8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int pitch, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Decompress12([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, short* dstBuf, int pitch, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Decompress16([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, [NativeTypeName("unsigned short *")] ushort* dstBuf, int pitch, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3DecompressToYUV8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int align);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3DecompressToYUVPlanes8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, [NativeTypeName("unsigned char **")] byte** dstPlanes, int* strides);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3DecodeYUV8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int align, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3DecodeYUVPlanes8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *const *")] byte** srcPlanes, [NativeTypeName("const int *")] int* strides, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3Transform([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("size_t")] nuint jpegSize, int n, [NativeTypeName("unsigned char **")] byte** dstBufs, [NativeTypeName("size_t *")] nuint* dstSizes, [NativeTypeName("const tjtransform *")] TJTransform* transforms);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void tj3Destroy([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void* tj3Alloc([NativeTypeName("size_t")] nuint bytes);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned char *")]
    public static extern byte* tj3LoadImage8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, int* width, int align, int* height, int* pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern short* tj3LoadImage12([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, int* width, int align, int* height, int* pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned short *")]
    public static extern ushort* tj3LoadImage16([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, int* width, int align, int* height, int* pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3SaveImage8([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, [NativeTypeName("const unsigned char *")] byte* buffer, int width, int pitch, int height, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3SaveImage12([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, [NativeTypeName("const short *")] short* buffer, int width, int pitch, int height, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3SaveImage16([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const char *")] sbyte* filename, [NativeTypeName("const unsigned short *")] ushort* buffer, int width, int pitch, int height, int pixelFormat);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void tj3Free(void* buffer);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("char *")]
    public static extern sbyte* tj3GetErrorStr([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tj3GetErrorCode([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint TJBUFSIZE(int width, int height);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjCompress([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelSize, [NativeTypeName("unsigned char *")] byte* dstBuf, [NativeTypeName("unsigned long *")] nuint* compressedSize, int jpegSubsamp, int jpegQual, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompress([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelSize, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressHeader([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, int* width, int* height);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDestroy([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("char *")]
    public static extern sbyte* tjGetErrorStr();

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("tjhandle")]
    public static extern void* tjInitCompress();

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("tjhandle")]
    public static extern void* tjInitDecompress();

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint TJBUFSIZEYUV(int width, int height, int jpegSubsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressHeader2([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, int* width, int* height, int* jpegSubsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressToYUV([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjEncodeYUV([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int subsamp, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned char *")]
    public static extern byte* tjAlloc(int bytes);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint tjBufSize(int width, int height, int jpegSubsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint tjBufSizeYUV(int width, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjCompress2([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("unsigned long *")] nuint* jpegSize, int jpegSubsamp, int jpegQual, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompress2([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelFormat, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjEncodeYUV2([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char *")] byte* dstBuf, int subsamp, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void tjFree([NativeTypeName("unsigned char *")] byte* buffer);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern TJScalingFactor* tjGetScalingFactors(int* numscalingfactors);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("tjhandle")]
    public static extern void* tjInitTransform();

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjTransform([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, int n, [NativeTypeName("unsigned char **")] byte** dstBufs, [NativeTypeName("unsigned long *")] nuint* dstSizes, TJTransform* transforms, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint tjBufSizeYUV2(int width, int align, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjCompressFromYUV([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int align, int height, int subsamp, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("unsigned long *")] nuint* jpegSize, int jpegQual, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjCompressFromYUVPlanes([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char **")] byte** srcPlanes, int width, [NativeTypeName("const int *")] int* strides, int height, int subsamp, [NativeTypeName("unsigned char **")] byte** jpegBuf, [NativeTypeName("unsigned long *")] nuint* jpegSize, int jpegQual, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecodeYUV([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int align, int subsamp, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelFormat, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecodeYUVPlanes([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char **")] byte** srcPlanes, [NativeTypeName("const int *")] int* strides, int subsamp, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int pitch, int height, int pixelFormat, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressHeader3([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, int* width, int* height, int* jpegSubsamp, int* jpegColorspace);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressToYUV2([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, [NativeTypeName("unsigned char *")] byte* dstBuf, int width, int align, int height, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjDecompressToYUVPlanes([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* jpegBuf, [NativeTypeName("unsigned long")] nuint jpegSize, [NativeTypeName("unsigned char **")] byte** dstPlanes, int width, int* strides, int height, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjEncodeYUV3([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char *")] byte* dstBuf, int align, int subsamp, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjEncodeYUVPlanes([NativeTypeName("tjhandle")] void* handle, [NativeTypeName("const unsigned char *")] byte* srcBuf, int width, int pitch, int height, int pixelFormat, [NativeTypeName("unsigned char **")] byte** dstPlanes, int* strides, int subsamp, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjPlaneHeight(int componentID, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned long")]
    public static extern nuint tjPlaneSizeYUV(int componentID, int width, int stride, int height, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjPlaneWidth(int componentID, int width, int subsamp);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjGetErrorCode([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("char *")]
    public static extern sbyte* tjGetErrorStr2([NativeTypeName("tjhandle")] void* handle);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("unsigned char *")]
    public static extern byte* tjLoadImage([NativeTypeName("const char *")] sbyte* filename, int* width, int align, int* height, int* pixelFormat, int flags);

    [DllImport("turbojpeg", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int tjSaveImage([NativeTypeName("const char *")] sbyte* filename, [NativeTypeName("unsigned char *")] byte* buffer, int width, int pitch, int height, int pixelFormat, int flags);

    [NativeTypeName("#define TJ_NUMINIT 3")]
    public const int TJ_NUMINIT = 3;

    [NativeTypeName("#define TJ_NUMSAMP 6")]
    public const int TJ_NUMSAMP = 6;

    [NativeTypeName("#define TJ_NUMPF 12")]
    public const int TJ_NUMPF = 12;

    [NativeTypeName("#define TJ_NUMCS 5")]
    public const int TJ_NUMCS = 5;

    [NativeTypeName("#define TJ_NUMERR 2")]
    public const int TJ_NUMERR = 2;

    [NativeTypeName("#define TJ_NUMXOP 8")]
    public const int TJ_NUMXOP = 8;

    [NativeTypeName("#define TJXOPT_PERFECT (1 << 0)")]
    public const int TJXOPT_PERFECT = (1 << 0);

    [NativeTypeName("#define TJXOPT_TRIM (1 << 1)")]
    public const int TJXOPT_TRIM = (1 << 1);

    [NativeTypeName("#define TJXOPT_CROP (1 << 2)")]
    public const int TJXOPT_CROP = (1 << 2);

    [NativeTypeName("#define TJXOPT_GRAY (1 << 3)")]
    public const int TJXOPT_GRAY = (1 << 3);

    [NativeTypeName("#define TJXOPT_NOOUTPUT (1 << 4)")]
    public const int TJXOPT_NOOUTPUT = (1 << 4);

    [NativeTypeName("#define TJXOPT_PROGRESSIVE (1 << 5)")]
    public const int TJXOPT_PROGRESSIVE = (1 << 5);

    [NativeTypeName("#define TJXOPT_COPYNONE (1 << 6)")]
    public const int TJXOPT_COPYNONE = (1 << 6);

    [NativeTypeName("#define TJXOPT_ARITHMETIC (1 << 7)")]
    public const int TJXOPT_ARITHMETIC = (1 << 7);

    [NativeTypeName("#define TJXOPT_OPTIMIZE (1 << 8)")]
    public const int TJXOPT_OPTIMIZE = (1 << 8);

    [NativeTypeName("#define NUMSUBOPT TJ_NUMSAMP")]
    public const int NUMSUBOPT = 6;

    [NativeTypeName("#define TJ_444 TJSAMP_444")]
    public const TJSAMP TJ_444 = TJSAMP_444;

    [NativeTypeName("#define TJ_422 TJSAMP_422")]
    public const TJSAMP TJ_422 = TJSAMP_422;

    [NativeTypeName("#define TJ_420 TJSAMP_420")]
    public const TJSAMP TJ_420 = TJSAMP_420;

    [NativeTypeName("#define TJ_411 TJSAMP_420")]
    public const TJSAMP TJ_411 = TJSAMP_420;

    [NativeTypeName("#define TJ_GRAYSCALE TJSAMP_GRAY")]
    public const TJSAMP TJ_GRAYSCALE = TJSAMP_GRAY;

    [NativeTypeName("#define TJ_BGR 1")]
    public const int TJ_BGR = 1;

    [NativeTypeName("#define TJ_BOTTOMUP TJFLAG_BOTTOMUP")]
    public const int TJ_BOTTOMUP = 2;

    [NativeTypeName("#define TJ_FORCEMMX TJFLAG_FORCEMMX")]
    public const int TJ_FORCEMMX = 8;

    [NativeTypeName("#define TJ_FORCESSE TJFLAG_FORCESSE")]
    public const int TJ_FORCESSE = 16;

    [NativeTypeName("#define TJ_FORCESSE2 TJFLAG_FORCESSE2")]
    public const int TJ_FORCESSE2 = 32;

    [NativeTypeName("#define TJ_ALPHAFIRST 64")]
    public const int TJ_ALPHAFIRST = 64;

    [NativeTypeName("#define TJ_FORCESSE3 TJFLAG_FORCESSE3")]
    public const int TJ_FORCESSE3 = 128;

    [NativeTypeName("#define TJ_FASTUPSAMPLE TJFLAG_FASTUPSAMPLE")]
    public const int TJ_FASTUPSAMPLE = 256;

    [NativeTypeName("#define TJ_YUV 512")]
    public const int TJ_YUV = 512;

    [NativeTypeName("#define TJFLAG_BOTTOMUP 2")]
    public const int TJFLAG_BOTTOMUP = 2;

    [NativeTypeName("#define TJFLAG_FORCEMMX 8")]
    public const int TJFLAG_FORCEMMX = 8;

    [NativeTypeName("#define TJFLAG_FORCESSE 16")]
    public const int TJFLAG_FORCESSE = 16;

    [NativeTypeName("#define TJFLAG_FORCESSE2 32")]
    public const int TJFLAG_FORCESSE2 = 32;

    [NativeTypeName("#define TJFLAG_FORCESSE3 128")]
    public const int TJFLAG_FORCESSE3 = 128;

    [NativeTypeName("#define TJFLAG_FASTUPSAMPLE 256")]
    public const int TJFLAG_FASTUPSAMPLE = 256;

    [NativeTypeName("#define TJFLAG_NOREALLOC 1024")]
    public const int TJFLAG_NOREALLOC = 1024;

    [NativeTypeName("#define TJFLAG_FASTDCT 2048")]
    public const int TJFLAG_FASTDCT = 2048;

    [NativeTypeName("#define TJFLAG_ACCURATEDCT 4096")]
    public const int TJFLAG_ACCURATEDCT = 4096;

    [NativeTypeName("#define TJFLAG_STOPONWARNING 8192")]
    public const int TJFLAG_STOPONWARNING = 8192;

    [NativeTypeName("#define TJFLAG_PROGRESSIVE 16384")]
    public const int TJFLAG_PROGRESSIVE = 16384;

    [NativeTypeName("#define TJFLAG_LIMITSCANS 32768")]
    public const int TJFLAG_LIMITSCANS = 32768;
  }
}
