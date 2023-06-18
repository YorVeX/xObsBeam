// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using ClangSharp;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FpngLib
{
  public enum FPNGECicpColorspace
  {
    FPNGE_CICP_NONE,
    FPNGE_CICP_PQ,
  }

  public enum FPNGEOptionsPredictor
  {
    FPNGE_PREDICTOR_FIXED_NOOP,
    FPNGE_PREDICTOR_FIXED_SUB,
    FPNGE_PREDICTOR_FIXED_TOP,
    FPNGE_PREDICTOR_FIXED_AVG,
    FPNGE_PREDICTOR_FIXED_PAETH,
    FPNGE_PREDICTOR_APPROX,
    FPNGE_PREDICTOR_BEST,
  }

  public partial struct FPNGEOptions
  {
    [NativeTypeName("char")]
    public sbyte predictor;

    [NativeTypeName("char")]
    public sbyte huffman_sample;

    [NativeTypeName("char")]
    public sbyte cicp_colorspace;
  }

  public static unsafe partial class Fpng
  {
    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?fpng_init@fpng@@YAXXZ", ExactSpelling = true)]
    public static extern void fpng_init();

    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?fpng_cpu_supports_sse41@fpng@@YA_NXZ", ExactSpelling = true)]
    [return: NativeTypeName("bool")]
    public static extern byte fpng_cpu_supports_sse41();

    [NativeTypeName("const uint32_t")]
    public const uint FPNG_CRC32_INIT = 0;

    [NativeTypeName("const uint32_t")]
    public const uint FPNG_ADLER32_INIT = 1;

    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?fpng_encode_image_to_memory@fpng@@YA_NPEBXIIIAEAV?$vector@EV?$allocator@E@std@@@std@@I@Z", ExactSpelling = true)]
    [return: NativeTypeName("bool")]
    public static extern byte fpng_encode_image_to_memory([NativeTypeName("const void *")] void* pImage, [NativeTypeName("uint32_t")] uint w, [NativeTypeName("uint32_t")] uint h, [NativeTypeName("uint32_t")] uint num_chans, [NativeTypeName("std::vector<uint8_t> &")] Vector<byte>* out_buf, [NativeTypeName("uint32_t")] uint flags = 0);

    public const int FPNG_DECODE_SUCCESS = 0;
    public const int FPNG_DECODE_NOT_FPNG = 1;
    public const int FPNG_DECODE_INVALID_ARG = 2;
    public const int FPNG_DECODE_FAILED_NOT_PNG = 3;
    public const int FPNG_DECODE_FAILED_HEADER_CRC32 = 4;
    public const int FPNG_DECODE_FAILED_INVALID_DIMENSIONS = 5;
    public const int FPNG_DECODE_FAILED_DIMENSIONS_TOO_LARGE = 6;
    public const int FPNG_DECODE_FAILED_CHUNK_PARSING = 7;
    public const int FPNG_DECODE_FAILED_INVALID_IDAT = 8;
    public const int FPNG_DECODE_FILE_OPEN_FAILED = 9;
    public const int FPNG_DECODE_FILE_TOO_LARGE = 10;
    public const int FPNG_DECODE_FILE_READ_FAILED = 11;
    public const int FPNG_DECODE_FILE_SEEK_FAILED = 12;

    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?fpng_get_info@fpng@@YAHPEBXIAEAI11@Z", ExactSpelling = true)]
    public static extern int fpng_get_info([NativeTypeName("const void *")] void* pImage, [NativeTypeName("uint32_t")] uint image_size, [NativeTypeName("uint32_t &")] uint* width, [NativeTypeName("uint32_t &")] uint* height, [NativeTypeName("uint32_t &")] uint* channels_in_file);

    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?fpng_decode_memory@fpng@@YAHPEBXIAEAV?$vector@EV?$allocator@E@std@@@std@@AEAI22I@Z", ExactSpelling = true)]
    public static extern int fpng_decode_memory([NativeTypeName("const void *")] void* pImage, [NativeTypeName("uint32_t")] uint image_size, [NativeTypeName("std::vector<uint8_t> &")] Vector<byte>* @out, [NativeTypeName("uint32_t &")] uint* width, [NativeTypeName("uint32_t &")] uint* height, [NativeTypeName("uint32_t &")] uint* channels_in_file, [NativeTypeName("uint32_t")] uint desired_channels);

    [NativeTypeName("#define FPNG_TRAIN_HUFFMAN_TABLES (0)")]
    public const int FPNG_TRAIN_HUFFMAN_TABLES = (0);

    [DllImport("FpngLib", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint FPNGEEncode([NativeTypeName("size_t")] nuint bytes_per_channel, [NativeTypeName("size_t")] nuint num_channels, [NativeTypeName("const void *")] void* data, [NativeTypeName("size_t")] nuint width, [NativeTypeName("size_t")] nuint row_stride, [NativeTypeName("size_t")] nuint height, void* output, [NativeTypeName("const struct FPNGEOptions *")] FPNGEOptions* options);

    [return: NativeTypeName("size_t")]
    public static nuint FPNGEOutputAllocSize([NativeTypeName("size_t")] nuint bytes_per_channel, [NativeTypeName("size_t")] nuint num_channels, [NativeTypeName("size_t")] nuint width, [NativeTypeName("size_t")] nuint height)
    {
      return 1024 + (2 * bytes_per_channel * width * num_channels + 1) * height;
    }

    [NativeTypeName("#define FPNGE_COMPRESS_LEVEL_DEFAULT 4")]
    public const int FPNGE_COMPRESS_LEVEL_DEFAULT = 4;

    [NativeTypeName("#define FPNGE_COMPRESS_LEVEL_BEST 5")]
    public const int FPNGE_COMPRESS_LEVEL_BEST = 5;
  }
}
