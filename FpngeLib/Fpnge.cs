// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using ClangSharp;
using System.Runtime.InteropServices;

namespace FpngeLib
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

  public static unsafe partial class Fpnge
  {
    [DllImport("FpngeLib", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint FPNGEEncode([NativeTypeName("size_t")] nuint bytes_per_channel, [NativeTypeName("size_t")] nuint num_channels, [NativeTypeName("const void *")] void* data, [NativeTypeName("size_t")] nuint width, [NativeTypeName("size_t")] nuint row_stride, [NativeTypeName("size_t")] nuint height, void* output, [NativeTypeName("const struct FPNGEOptions *")] FPNGEOptions* options);

    [DllImport("FpngeLib", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint FPNGEOutputAllocSize([NativeTypeName("size_t")] nuint bytes_per_channel, [NativeTypeName("size_t")] nuint num_channels, [NativeTypeName("size_t")] nuint width, [NativeTypeName("size_t")] nuint height);

    [NativeTypeName("#define FPNGE_COMPRESS_LEVEL_DEFAULT 4")]
    public const int FPNGE_COMPRESS_LEVEL_DEFAULT = 4;

    [NativeTypeName("#define FPNGE_COMPRESS_LEVEL_BEST 5")]
    public const int FPNGE_COMPRESS_LEVEL_BEST = 5;
  }
}
