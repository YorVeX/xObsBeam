// Copyright 2022 Nigel Tao.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// The modifications to this file compared to the original are that 1) "DLL_EXPORT"
// has been added to the qoir_encode and qoir_decode function declarations that is
// defined on Windows and Linux so that it's exporting these functions to a library
// and 2) the QOIR_IMPLEMENTATION define has been removed and all code that was within
// that define. This is so that this class can serve as a pure header file template
// for the C# wrapper.

#if defined(_WIN32)
#define DLL_EXPORT __declspec(dllexport)
#elif defined(__linux__)
#define DLL_EXPORT __attribute__ ((visibility ("default")))
#endif

#ifndef QOIR_INCLUDE_GUARD
#define QOIR_INCLUDE_GUARD

// QOIR is a fast, simple image file format.
//
// Most users will want the qoir_decode and qoir_encode functions, which read
// from and write to a contiguous block of memory.
//
// This file also contains a stand-alone implementation of LZ4 block
// compression, a general format that is not limited to compressing images. The
// qoir_lz4_block_decode and qoir_lz4_block_encode functions also read from and
// write to a contiguous block of memory.

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#ifdef __cplusplus
extern "C" {
#endif

// ================================ +Public Interface

// QOIR ships as a "single file C library" or "header file library" as per
// https://github.com/nothings/stb/blob/master/docs/stb_howto.txt
//
// To use that single file as a "foo.c"-like implementation, instead of a
// "foo.h"-like header, #define QOIR_IMPLEMENTATION before #include'ing or
// compiling it.

// -------- Compile-time Configuration

// The compile-time configuration macros are:
//  - QOIR_CONFIG__DISABLE_LARGE_LOOK_UP_TABLES
//  - QOIR_CONFIG__DISABLE_SIMD
//  - QOIR_CONFIG__STATIC_FUNCTIONS
//  - QOIR_CONFIG__USE_OFFICIAL_LZ4_LIBRARY

// ----

// If using e.g. "CFLAGS='-DQOIR_CONFIG__USE_OFFICIAL_LZ4_LIBRARY -O3'") then
// you probably also want "LDFLAGS=-llz4", otherwise you'll get "undefined
// reference to `LZ4_decompress_safe'".

// ----

// Define QOIR_CONFIG__STATIC_FUNCTIONS (combined with QOIR_IMPLEMENTATION) to
// make all of QOIR's functions have static storage.
//
// This can help the compiler ignore or discard unused code, which can produce
// faster compiles and smaller binaries. Other motivations are discussed in the
// "ALLOW STATIC IMPLEMENTATION" section of
// https://raw.githubusercontent.com/nothings/stb/master/docs/stb_howto.txt
#if defined(QOIR_CONFIG__STATIC_FUNCTIONS)
#define QOIR_MAYBE_STATIC static
#else
#define QOIR_MAYBE_STATIC
#endif  // defined(QOIR_CONFIG__STATIC_FUNCTIONS)

// -------- Other Macros

// Clang also #define's "__GNUC__".
#if defined(__GNUC__) || defined(_MSC_VER)
#define QOIR_RESTRICT __restrict
#else
#define QOIR_RESTRICT
#endif

// -------- Basic Result Types

typedef struct qoir_size_result_struct {
  const char* status_message;
  size_t value;
} qoir_size_result;

// -------- Status Messages

extern const char qoir_lz4_status_message__error_dst_is_too_short[];
extern const char qoir_lz4_status_message__error_invalid_data[];
extern const char qoir_lz4_status_message__error_src_is_too_long[];

extern const char qoir_status_message__error_invalid_argument[];
extern const char qoir_status_message__error_invalid_data[];
extern const char qoir_status_message__error_out_of_memory[];
extern const char qoir_status_message__error_unsupported_metadata_size[];
extern const char qoir_status_message__error_unsupported_pixbuf_dimensions[];
extern const char qoir_status_message__error_unsupported_pixfmt[];
extern const char qoir_status_message__error_unsupported_tile_format[];

// -------- Pixel Buffers

// A pixel format combines an alpha transparency choice, a color model choice
// and other configuration (such as pixel byte order).
//
// Values less than 0x10 are directly representable by the file format (and by
// this implementation's API), using the same bit pattern.
//
// Values greater than or equal to 0x10 are representable by the API but not by
// the file format:
//  - the 0x10 bit means 3 (not 4) bytes per (fully opaque) pixel.
//  - the 0x20 bit means RGBA (not BGRA) byte order.

typedef uint32_t qoir_pixel_alpha_transparency;
typedef uint32_t qoir_pixel_color_model;
typedef uint32_t qoir_pixel_format;

// clang-format off

#define QOIR_PIXEL_ALPHA_TRANSPARENCY__OPAQUE                  0x01
#define QOIR_PIXEL_ALPHA_TRANSPARENCY__NONPREMULTIPLIED_ALPHA  0x02
#define QOIR_PIXEL_ALPHA_TRANSPARENCY__PREMULTIPLIED_ALPHA     0x03

#define QOIR_PIXEL_COLOR_MODEL__BGRA  0x00

#define QOIR_PIXEL_FORMAT__MASK_FOR_ALPHA_TRANSPARENCY  0x03
#define QOIR_PIXEL_FORMAT__MASK_FOR_COLOR_MODEL         0x0C

#define QOIR_PIXEL_FORMAT__INVALID         0x00
#define QOIR_PIXEL_FORMAT__BGRX            0x01
#define QOIR_PIXEL_FORMAT__BGRA_NONPREMUL  0x02
#define QOIR_PIXEL_FORMAT__BGRA_PREMUL     0x03
#define QOIR_PIXEL_FORMAT__BGR             0x11
#define QOIR_PIXEL_FORMAT__RGBX            0x21
#define QOIR_PIXEL_FORMAT__RGBA_NONPREMUL  0x22
#define QOIR_PIXEL_FORMAT__RGBA_PREMUL     0x23
#define QOIR_PIXEL_FORMAT__RGB             0x31

// clang-format on

typedef struct qoir_pixel_configuration_struct {
  qoir_pixel_format pixfmt;
  uint32_t width_in_pixels;
  uint32_t height_in_pixels;
} qoir_pixel_configuration;

typedef struct qoir_pixel_buffer_struct {
  qoir_pixel_configuration pixcfg;
  uint8_t* data;
  size_t stride_in_bytes;
} qoir_pixel_buffer;

// All int32_t points (x, y) such that ((x0 <= x) && (x < x1) && (y0 <= y) &&
// (y < y1)). The low bounds are inclusive. The high bounds are exclusive.
typedef struct qoir_rectangle_struct {
  int32_t x0;
  int32_t y0;
  int32_t x1;
  int32_t y1;
} qoir_rectangle;

static inline uint32_t               //
qoir_pixel_format__bytes_per_pixel(  //
    qoir_pixel_format pixfmt) {
  return (pixfmt & 0x10) ? 3 : 4;
}

static inline bool           //
qoir_pixel_buffer__is_zero(  //
    qoir_pixel_buffer pixbuf) {
  return (pixbuf.pixcfg.pixfmt == 0) &&            //
         (pixbuf.pixcfg.width_in_pixels == 0) &&   //
         (pixbuf.pixcfg.height_in_pixels == 0) &&  //
         (pixbuf.data == NULL) &&                  //
         (pixbuf.stride_in_bytes == 0);
}

static inline qoir_rectangle  //
qoir_make_rectangle(          //
    int32_t x0,               //
    int32_t y0,               //
    int32_t x1,               //
    int32_t y1) {
  qoir_rectangle ret;
  ret.x0 = x0;
  ret.y0 = y0;
  ret.x1 = x1;
  ret.y1 = y1;
  return ret;
}

static inline qoir_rectangle  //
qoir_rectangle__intersect(    //
    qoir_rectangle r,         //
    qoir_rectangle s) {
  qoir_rectangle ret;
  ret.x0 = (r.x0 > s.x0) ? r.x0 : s.x0;
  ret.y0 = (r.y0 > s.y0) ? r.y0 : s.y0;
  ret.x1 = (r.x1 < s.x1) ? r.x1 : s.x1;
  ret.y1 = (r.y1 < s.y1) ? r.y1 : s.y1;
  return ret;
}

static inline bool         //
qoir_rectangle__is_empty(  //
    qoir_rectangle r) {
  return (r.x1 <= r.x0) || (r.y1 <= r.y0);
}

static inline uint32_t  //
qoir_rectangle__width(  //
    qoir_rectangle r) {
  return (r.x1 > r.x0) ? ((uint32_t)r.x1 - (uint32_t)r.x0) : 0;
}

static inline uint32_t   //
qoir_rectangle__height(  //
    qoir_rectangle r) {
  return (r.y1 > r.y0) ? ((uint32_t)r.y1 - (uint32_t)r.y0) : 0;
}

// -------- Tiling

#define QOIR_TILE_MASK 0x3F
#define QOIR_TILE_SIZE 0x40
#define QOIR_TILE_SHIFT 6

// QOIR_LITERALS_PRE_PADDING is large enough to hold the previous pixel, at 4
// bytes per pixel.
#define QOIR_LITERALS_PRE_PADDING 4

// QOIR_TS2 is the maximum (inclusive) number of pixels in a tile.
#define QOIR_TS2 (QOIR_TILE_SIZE * QOIR_TILE_SIZE)

static inline uint32_t              //
qoir_calculate_number_of_tiles_1d(  //
    uint32_t number_of_pixels) {
  uint64_t rounded_up = (uint64_t)number_of_pixels + QOIR_TILE_MASK;
  return (uint32_t)(rounded_up >> QOIR_TILE_SHIFT);
}

static inline uint64_t              //
qoir_calculate_number_of_tiles_2d(  //
    uint32_t width_in_pixels,
    uint32_t height_in_pixels) {
  uint64_t w = ((uint64_t)width_in_pixels + QOIR_TILE_MASK) >> QOIR_TILE_SHIFT;
  uint64_t h = ((uint64_t)height_in_pixels + QOIR_TILE_MASK) >> QOIR_TILE_SHIFT;
  return w * h;
}

// -------- LZ4 Decode

// QOIR_LZ4_BLOCK_DECODE_MAX_INCL_SRC_LEN is the maximum (inclusive) supported
// input length for this file's LZ4 decode functions. The LZ4 block format can
// generally support longer inputs, but this implementation specifically is
// more limited, to simplify overflow checking.
//
// With sufficiently large input, qoir_lz4_block_encode (note that that's
// encode, not decode) may very well produce output that is longer than this.
// That output is valid (in terms of the LZ4 file format) but isn't decodable
// by qoir_lz4_block_decode.
//
// 0x00FFFFFF = 16777215, which is over 16 million bytes.
#define QOIR_LZ4_BLOCK_DECODE_MAX_INCL_SRC_LEN 0x00FFFFFF

// qoir_lz4_block_decode writes to dst the LZ4 block decompressed form of src,
// returning the number of bytes written.
//
// It fails with qoir_lz4_status_message__error_dst_is_too_short if dst_len is
// not long enough to hold the decompressed form.
QOIR_MAYBE_STATIC qoir_size_result         //
qoir_lz4_block_decode(                     //
    uint8_t* QOIR_RESTRICT dst_ptr,        //
    size_t dst_len,                        //
    const uint8_t* QOIR_RESTRICT src_ptr,  //
    size_t src_len);

// -------- LZ4 Encode

// QOIR_LZ4_BLOCK_ENCODE_MAX_INCL_SRC_LEN is the maximum (inclusive) supported
// input length for this file's LZ4 encode functions. The LZ4 block format can
// generally support longer inputs, but this implementation specifically is
// more limited, to simplify overflow checking.
//
// 0x7E000000 = 2113929216, which is over 2 billion bytes.
#define QOIR_LZ4_BLOCK_ENCODE_MAX_INCL_SRC_LEN 0x7E000000

// qoir_lz4_block_encode_worst_case_dst_len returns the maximum (inclusive)
// number of bytes required to LZ4 block compress src_len input bytes.
QOIR_MAYBE_STATIC qoir_size_result         //
qoir_lz4_block_encode_worst_case_dst_len(  //
    size_t src_len);

// qoir_lz4_block_encode writes to dst the LZ4 block compressed form of src,
// returning the number of bytes written.
//
// Unlike the LZ4_compress_default function from the official implementation
// (https://github.com/lz4/lz4), it fails immediately with
// qoir_lz4_status_message__error_dst_is_too_short if dst_len is less than
// qoir_lz4_block_encode_worst_case_dst_len(src_len), even if the worst case is
// unrealized and the compressed form would actually fit.
QOIR_MAYBE_STATIC qoir_size_result         //
qoir_lz4_block_encode(                     //
    uint8_t* QOIR_RESTRICT dst_ptr,        //
    size_t dst_len,                        //
    const uint8_t* QOIR_RESTRICT src_ptr,  //
    size_t src_len);

// -------- QOIR Decode

typedef struct qoir_decode_pixel_configuration_result_struct {
  const char* status_message;
  qoir_pixel_configuration dst_pixcfg;
} qoir_decode_pixel_configuration_result;

QOIR_MAYBE_STATIC qoir_decode_pixel_configuration_result  //
qoir_decode_pixel_configuration(                          //
    const uint8_t* src_ptr,                               //
    size_t src_len);

typedef struct qoir_decode_buffer_struct {
  struct {
    // ops has to be before literals, so that (worst case) we can read (and
    // ignore) 8 bytes past the end of the ops array. See §
    uint8_t ops[4 * QOIR_TS2];
    uint8_t literals[QOIR_LITERALS_PRE_PADDING + (4 * QOIR_TS2)];
  } private_impl;
} qoir_decode_buffer;

typedef struct qoir_decode_result_struct {
  const char* status_message;
  void* owned_memory;
  qoir_pixel_buffer dst_pixbuf;

  // Optional metadata chunks.

  const uint8_t* metadata_cicp_ptr;
  size_t metadata_cicp_len;

  const uint8_t* metadata_iccp_ptr;
  size_t metadata_iccp_len;

  const uint8_t* metadata_exif_ptr;
  size_t metadata_exif_len;

  const uint8_t* metadata_xmp_ptr;
  size_t metadata_xmp_len;
} qoir_decode_result;

typedef struct qoir_decode_options_struct {
  // Custom malloc/free implementations. NULL etc_func pointers means to use
  // the standard malloc and free functions. Non-NULL etc_func pointers will be
  // passed the memory_func_context.
  void* (*contextual_malloc_func)(void* memory_func_context, size_t len);
  void (*contextual_free_func)(void* memory_func_context, void* ptr);
  void* memory_func_context;

  // Pre-allocated 'scratch space' used during decoding. Its contents need not
  // be initialized when passed to qoir_decode.
  //
  // If NULL, the 'scratch space' will be dynamically allocated and freed.
  qoir_decode_buffer* decbuf;

  // Pre-allocated pixel buffer to decode into.
  //
  // If zero, it will be dynamically allocated and memory ownership (i.e. the
  // responsibility to call free or equivalent) will be returned as the
  // qoir_decode_result owned_memory field.
  qoir_pixel_buffer pixbuf;

  // If non-zero, this is the pixel format to use when dynamically allocating
  // the pixel buffer to decode into (if the pixbuf field is zero). This pixfmt
  // field has no effect if the pixbuf field is non-zero.
  //
  // A zero-valued pixfmt field is equivalent to the default pixel format:
  // QOIR_PIXEL_FORMAT__RGBA_NONPREMUL.
  qoir_pixel_format pixfmt;

  // Clipping rectangles, in the destination or source (or both) coordinate
  // spaces. The clips (the qoir_rectangle typed fields) have no effect unless
  // the corresponding boolean typed field is true.
  qoir_rectangle dst_clip_rectangle;
  qoir_rectangle src_clip_rectangle;
  bool use_dst_clip_rectangle;
  bool use_src_clip_rectangle;

  // The position (in destination coordinate space) to place the top-left
  // corner of the decoded source image. The Y axis grows down.
  int32_t offset_x;
  int32_t offset_y;
} qoir_decode_options;

// Decodes a pixel buffer from the QOIR format.
//
// A NULL options is valid and is equivalent to a non-NULL pointer to a
// zero-valued struct (where all fields are zero / NULL / false).
DLL_EXPORT qoir_decode_result  //
qoir_decode(                          //
    const uint8_t* src_ptr,           //
    const size_t src_len,             //
    const qoir_decode_options* options);

// -------- QOIR Encode

typedef struct qoir_encode_buffer_struct {
  struct {
    // ops' size is ((5 * QOIR_TS2) + 64), not (4 * QOIR_TS2), because in the
    // worst case (during encoding, before discarding the too-long ops in favor
    // of literals), each pixel uses QOIR_OP_BGRA8, 5 bytes each. The +64 is
    // for the same reason as §, but the +8 is rounded up to a multiple of a
    // typical cache line size.
    uint8_t ops[(5 * QOIR_TS2) + 64];
    uint8_t literals[QOIR_LITERALS_PRE_PADDING + (4 * QOIR_TS2)];
  } private_impl;
} qoir_encode_buffer;

typedef struct qoir_encode_result_struct {
  const char* status_message;
  void* owned_memory;
  uint8_t* dst_ptr;
  size_t dst_len;
} qoir_encode_result;

typedef struct qoir_encode_options_struct {
  // Custom malloc/free implementations. NULL etc_func pointers means to use
  // the standard malloc and free functions. Non-NULL etc_func pointers will be
  // passed the memory_func_context.
  void* (*contextual_malloc_func)(void* memory_func_context, size_t len);
  void (*contextual_free_func)(void* memory_func_context, void* ptr);
  void* memory_func_context;

  // Pre-allocated 'scratch space' used during encoding. Its contents need not
  // be initialized when passed to qoir_encode.
  //
  // If NULL, the 'scratch space' will be dynamically allocated and freed.
  qoir_encode_buffer* encbuf;

  // Optional metadata chunks.

  const uint8_t* metadata_cicp_ptr;
  size_t metadata_cicp_len;

  const uint8_t* metadata_iccp_ptr;
  size_t metadata_iccp_len;

  const uint8_t* metadata_exif_ptr;
  size_t metadata_exif_len;

  const uint8_t* metadata_xmp_ptr;
  size_t metadata_xmp_len;

  // Lossiness ranges from 0 (lossless) to 7 (very lossy), inclusive.
  uint32_t lossiness;

  // Whether to dither the lossy encoding. This option has no effect if
  // lossiness is zero.
  //
  // The dithering algorithm is relatively simple. Fancier algorithms like
  // https://nigeltao.github.io/blog/2022/gamma-aware-ordered-dithering.html
  // can produce higher quality results, especially for lossiness levels at 6
  // or 7 re overall brightness, but they are out of scope of this library. To
  // use alternative dithering algorithms, apply them to src_pixbuf before
  // passing to qoir_encode.
  bool dither;
} qoir_encode_options;

// Encodes a pixel buffer to the QOIR format.
//
// A NULL options is valid and is equivalent to a non-NULL pointer to a
// zero-valued struct (where all fields are zero / NULL / false).
DLL_EXPORT qoir_encode_result      //
qoir_encode(                              //
    const qoir_pixel_buffer* src_pixbuf,  //
    const qoir_encode_options* options);

// ================================ -Public Interface


#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // QOIR_INCLUDE_GUARD
