// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using ClangSharp;
using System.Runtime.InteropServices;

namespace QoirLib
{
  public unsafe partial struct qoir_size_result_struct
  {
    [NativeTypeName("const char *")]
    public sbyte* status_message;

    [NativeTypeName("size_t")]
    public nuint value;
  }

  public partial struct qoir_pixel_configuration_struct
  {
    [NativeTypeName("qoir_pixel_format")]
    public uint pixfmt;

    [NativeTypeName("uint32_t")]
    public uint width_in_pixels;

    [NativeTypeName("uint32_t")]
    public uint height_in_pixels;
  }

  public unsafe partial struct qoir_pixel_buffer_struct
  {
    [NativeTypeName("qoir_pixel_configuration")]
    public qoir_pixel_configuration_struct pixcfg;

    [NativeTypeName("uint8_t *")]
    public byte* data;

    [NativeTypeName("size_t")]
    public nuint stride_in_bytes;
  }

  public partial struct qoir_rectangle_struct
  {
    [NativeTypeName("int32_t")]
    public int x0;

    [NativeTypeName("int32_t")]
    public int y0;

    [NativeTypeName("int32_t")]
    public int x1;

    [NativeTypeName("int32_t")]
    public int y1;
  }

  public unsafe partial struct qoir_decode_pixel_configuration_result_struct
  {
    [NativeTypeName("const char *")]
    public sbyte* status_message;

    [NativeTypeName("qoir_pixel_configuration")]
    public qoir_pixel_configuration_struct dst_pixcfg;
  }

  public partial struct qoir_decode_buffer_struct
  {
    [NativeTypeName("struct (anonymous struct at QoirLibCs.h:327:3)")]
    public _private_impl_e__Struct private_impl;

    public unsafe partial struct _private_impl_e__Struct
    {
      [NativeTypeName("uint8_t[16384]")]
      public fixed byte ops[16384];

      [NativeTypeName("uint8_t[16388]")]
      public fixed byte literals[16388];
    }
  }

  public unsafe partial struct qoir_decode_result_struct
  {
    [NativeTypeName("const char *")]
    public sbyte* status_message;

    public void* owned_memory;

    [NativeTypeName("qoir_pixel_buffer")]
    public qoir_pixel_buffer_struct dst_pixbuf;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_cicp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_cicp_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_iccp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_iccp_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_exif_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_exif_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_xmp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_xmp_len;
  }

  public unsafe partial struct qoir_decode_options_struct
  {
    [NativeTypeName("void *(*)(void *, size_t)")]
    public delegate* unmanaged[Cdecl]<void*, nuint, void*> contextual_malloc_func;

    [NativeTypeName("void (*)(void *, void *)")]
    public delegate* unmanaged[Cdecl]<void*, void*, void> contextual_free_func;

    public void* memory_func_context;

    [NativeTypeName("qoir_decode_buffer *")]
    public qoir_decode_buffer_struct* decbuf;

    [NativeTypeName("qoir_pixel_buffer")]
    public qoir_pixel_buffer_struct pixbuf;

    [NativeTypeName("qoir_pixel_format")]
    public uint pixfmt;

    [NativeTypeName("qoir_rectangle")]
    public qoir_rectangle_struct dst_clip_rectangle;

    [NativeTypeName("qoir_rectangle")]
    public qoir_rectangle_struct src_clip_rectangle;

    [NativeTypeName("bool")]
    public byte use_dst_clip_rectangle;

    [NativeTypeName("bool")]
    public byte use_src_clip_rectangle;

    [NativeTypeName("int32_t")]
    public int offset_x;

    [NativeTypeName("int32_t")]
    public int offset_y;
  }

  public partial struct qoir_encode_buffer_struct
  {
    [NativeTypeName("struct (anonymous struct at QoirLibCs.h:411:3)")]
    public _private_impl_e__Struct private_impl;

    public unsafe partial struct _private_impl_e__Struct
    {
      [NativeTypeName("uint8_t[20544]")]
      public fixed byte ops[20544];

      [NativeTypeName("uint8_t[16388]")]
      public fixed byte literals[16388];
    }
  }

  public unsafe partial struct qoir_encode_result_struct
  {
    [NativeTypeName("const char *")]
    public sbyte* status_message;

    public void* owned_memory;

    [NativeTypeName("uint8_t *")]
    public byte* dst_ptr;

    [NativeTypeName("size_t")]
    public nuint dst_len;
  }

  public unsafe partial struct qoir_encode_options_struct
  {
    [NativeTypeName("void *(*)(void *, size_t)")]
    public delegate* unmanaged[Cdecl]<void*, nuint, void*> contextual_malloc_func;

    [NativeTypeName("void (*)(void *, void *)")]
    public delegate* unmanaged[Cdecl]<void*, void*, void> contextual_free_func;

    public void* memory_func_context;

    [NativeTypeName("qoir_encode_buffer *")]
    public qoir_encode_buffer_struct* encbuf;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_cicp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_cicp_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_iccp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_iccp_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_exif_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_exif_len;

    [NativeTypeName("const uint8_t *")]
    public byte* metadata_xmp_ptr;

    [NativeTypeName("size_t")]
    public nuint metadata_xmp_len;

    [NativeTypeName("uint32_t")]
    public uint lossiness;

    [NativeTypeName("bool")]
    public byte dither;
  }

  public static unsafe partial class Qoir
  {
    [DllImport("QoirLib", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("qoir_decode_result")]
    public static extern qoir_decode_result_struct qoir_decode([NativeTypeName("const uint8_t *")] byte* src_ptr, [NativeTypeName("const size_t")] nuint src_len, [NativeTypeName("const qoir_decode_options *")] qoir_decode_options_struct* options);

    [DllImport("QoirLib", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: NativeTypeName("qoir_encode_result")]
    public static extern qoir_encode_result_struct qoir_encode([NativeTypeName("const qoir_pixel_buffer *")] qoir_pixel_buffer_struct* src_pixbuf, [NativeTypeName("const qoir_encode_options *")] qoir_encode_options_struct* options);

    [NativeTypeName("#define QOIR_PIXEL_ALPHA_TRANSPARENCY__OPAQUE 0x01")]
    public const int QOIR_PIXEL_ALPHA_TRANSPARENCY__OPAQUE = 0x01;

    [NativeTypeName("#define QOIR_PIXEL_ALPHA_TRANSPARENCY__NONPREMULTIPLIED_ALPHA 0x02")]
    public const int QOIR_PIXEL_ALPHA_TRANSPARENCY__NONPREMULTIPLIED_ALPHA = 0x02;

    [NativeTypeName("#define QOIR_PIXEL_ALPHA_TRANSPARENCY__PREMULTIPLIED_ALPHA 0x03")]
    public const int QOIR_PIXEL_ALPHA_TRANSPARENCY__PREMULTIPLIED_ALPHA = 0x03;

    [NativeTypeName("#define QOIR_PIXEL_COLOR_MODEL__BGRA 0x00")]
    public const int QOIR_PIXEL_COLOR_MODEL__BGRA = 0x00;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__MASK_FOR_ALPHA_TRANSPARENCY 0x03")]
    public const int QOIR_PIXEL_FORMAT__MASK_FOR_ALPHA_TRANSPARENCY = 0x03;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__MASK_FOR_COLOR_MODEL 0x0C")]
    public const int QOIR_PIXEL_FORMAT__MASK_FOR_COLOR_MODEL = 0x0C;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__INVALID 0x00")]
    public const int QOIR_PIXEL_FORMAT__INVALID = 0x00;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__BGRX 0x01")]
    public const int QOIR_PIXEL_FORMAT__BGRX = 0x01;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__BGRA_NONPREMUL 0x02")]
    public const int QOIR_PIXEL_FORMAT__BGRA_NONPREMUL = 0x02;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__BGRA_PREMUL 0x03")]
    public const int QOIR_PIXEL_FORMAT__BGRA_PREMUL = 0x03;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__BGR 0x11")]
    public const int QOIR_PIXEL_FORMAT__BGR = 0x11;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__RGBX 0x21")]
    public const int QOIR_PIXEL_FORMAT__RGBX = 0x21;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__RGBA_NONPREMUL 0x22")]
    public const int QOIR_PIXEL_FORMAT__RGBA_NONPREMUL = 0x22;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__RGBA_PREMUL 0x23")]
    public const int QOIR_PIXEL_FORMAT__RGBA_PREMUL = 0x23;

    [NativeTypeName("#define QOIR_PIXEL_FORMAT__RGB 0x31")]
    public const int QOIR_PIXEL_FORMAT__RGB = 0x31;

    [NativeTypeName("#define QOIR_TILE_MASK 0x3F")]
    public const int QOIR_TILE_MASK = 0x3F;

    [NativeTypeName("#define QOIR_TILE_SIZE 0x40")]
    public const int QOIR_TILE_SIZE = 0x40;

    [NativeTypeName("#define QOIR_TILE_SHIFT 6")]
    public const int QOIR_TILE_SHIFT = 6;

    [NativeTypeName("#define QOIR_LITERALS_PRE_PADDING 4")]
    public const int QOIR_LITERALS_PRE_PADDING = 4;

    [NativeTypeName("#define QOIR_TS2 (QOIR_TILE_SIZE * QOIR_TILE_SIZE)")]
    public const int QOIR_TS2 = (0x40 * 0x40);

    [NativeTypeName("#define QOIR_LZ4_BLOCK_DECODE_MAX_INCL_SRC_LEN 0x00FFFFFF")]
    public const int QOIR_LZ4_BLOCK_DECODE_MAX_INCL_SRC_LEN = 0x00FFFFFF;

    [NativeTypeName("#define QOIR_LZ4_BLOCK_ENCODE_MAX_INCL_SRC_LEN 0x7E000000")]
    public const int QOIR_LZ4_BLOCK_ENCODE_MAX_INCL_SRC_LEN = 0x7E000000;
  }
}
