// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using ClangSharp;
using System.Runtime.InteropServices;

namespace DensityApi
{
  public enum DENSITY_ALGORITHM
  {
    DENSITY_ALGORITHM_CHAMELEON = 1,
    DENSITY_ALGORITHM_CHEETAH = 2,
    DENSITY_ALGORITHM_LION = 3,
  }

  public enum DENSITY_STATE
  {
    DENSITY_STATE_OK = 0,
    DENSITY_STATE_ERROR_INPUT_BUFFER_TOO_SMALL,
    DENSITY_STATE_ERROR_OUTPUT_BUFFER_TOO_SMALL,
    DENSITY_STATE_ERROR_DURING_PROCESSING,
    DENSITY_STATE_ERROR_INVALID_CONTEXT,
    DENSITY_STATE_ERROR_INVALID_ALGORITHM,
  }

  public unsafe partial struct density_context
  {
    public DENSITY_ALGORITHM algorithm;

    [NativeTypeName("bool")]
    public byte dictionary_type;

    [NativeTypeName("size_t")]
    public nuint dictionary_size;

    public void* dictionary;
  }

  public unsafe partial struct density_processing_result
  {
    public DENSITY_STATE state;

    [NativeTypeName("uint_fast64_t")]
    public ulong bytesRead;

    [NativeTypeName("uint_fast64_t")]
    public ulong bytesWritten;

    public density_context* context;
  }

  public static unsafe partial class Density
  {
    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint8_t")]
    public static extern byte density_version_major();

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint8_t")]
    public static extern byte density_version_minor();

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint8_t")]
    public static extern byte density_version_revision();

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("size_t")]
    public static extern nuint density_get_dictionary_size(DENSITY_ALGORITHM algorithm);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint_fast64_t")]
    public static extern ulong density_compress_safe_size([NativeTypeName("const uint_fast64_t")] ulong input_size);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("uint_fast64_t")]
    public static extern ulong density_decompress_safe_size([NativeTypeName("const uint_fast64_t")] ulong expected_decompressed_output_size);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void density_free_context([NativeTypeName("density_context *const")] density_context* context, [NativeTypeName("void (*)(void *)")] delegate* unmanaged[Cdecl]<void*, void> mem_free);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_compress_prepare_context([NativeTypeName("const DENSITY_ALGORITHM")] DENSITY_ALGORITHM algorithm, [NativeTypeName("const bool")] byte custom_dictionary, [NativeTypeName("void *(*)(size_t)")] delegate* unmanaged[Cdecl]<nuint, void*> mem_alloc);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_compress_with_context([NativeTypeName("const uint8_t *")] byte* input_buffer, [NativeTypeName("const uint_fast64_t")] ulong input_size, [NativeTypeName("uint8_t *")] byte* output_buffer, [NativeTypeName("const uint_fast64_t")] ulong output_size, [NativeTypeName("density_context *const")] density_context* context);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_compress([NativeTypeName("const uint8_t *")] byte* input_buffer, [NativeTypeName("const uint_fast64_t")] ulong input_size, [NativeTypeName("uint8_t *")] byte* output_buffer, [NativeTypeName("const uint_fast64_t")] ulong output_size, [NativeTypeName("const DENSITY_ALGORITHM")] DENSITY_ALGORITHM algorithm);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_decompress_prepare_context([NativeTypeName("const uint8_t *")] byte* input_buffer, [NativeTypeName("const uint_fast64_t")] ulong input_size, [NativeTypeName("const bool")] byte custom_dictionary, [NativeTypeName("void *(*)(size_t)")] delegate* unmanaged[Cdecl]<nuint, void*> mem_alloc);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_decompress_with_context([NativeTypeName("const uint8_t *")] byte* input_buffer, [NativeTypeName("const uint_fast64_t")] ulong input_size, [NativeTypeName("uint8_t *")] byte* output_buffer, [NativeTypeName("const uint_fast64_t")] ulong output_size, [NativeTypeName("density_context *const")] density_context* context);

    [DllImport("density", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern density_processing_result density_decompress([NativeTypeName("const uint8_t *")] byte* input_buffer, [NativeTypeName("const uint_fast64_t")] ulong input_size, [NativeTypeName("uint8_t *")] byte* output_buffer, [NativeTypeName("const uint_fast64_t")] ulong output_size);
  }
}
