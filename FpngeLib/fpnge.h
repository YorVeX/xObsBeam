// Copyright 2021 Google LLC
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

// The modification to this file compared to the original is that "DLL_EXPORT"
// has been added to the FPNGEEncode and FPNGEOutputAllocSize function
// declarations that is defined on Windows and Linux so that it's exporting
// these functions to a library. In addition, the FPNGEOutputAllocSize function
// had its inline implementation replaced by a pure header declaration.

#ifndef FPNGE_H
#define FPNGE_H
#include <stdlib.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define DLL_EXPORT __declspec(dllexport)
#elif defined(__linux__)
#define DLL_EXPORT __attribute__ ((visibility ("default")))
#endif

enum FPNGECicpColorspace { FPNGE_CICP_NONE, FPNGE_CICP_PQ };

enum FPNGEOptionsPredictor {
  FPNGE_PREDICTOR_FIXED_NOOP,
  FPNGE_PREDICTOR_FIXED_SUB,
  FPNGE_PREDICTOR_FIXED_TOP,
  FPNGE_PREDICTOR_FIXED_AVG,
  FPNGE_PREDICTOR_FIXED_PAETH,
  FPNGE_PREDICTOR_APPROX,
  FPNGE_PREDICTOR_BEST
};
struct FPNGEOptions {
  char predictor;        // FPNGEOptionsPredictor
  char huffman_sample;   // 0-127: how much of the image to sample
  char cicp_colorspace;  // FPNGECicpColorspace
};

#define FPNGE_COMPRESS_LEVEL_DEFAULT 4
#define FPNGE_COMPRESS_LEVEL_BEST 5
inline void FPNGEFillOptions(struct FPNGEOptions *options, int level,
                             int cicp_colorspace) {
  if (level == 0) level = FPNGE_COMPRESS_LEVEL_DEFAULT;
  options->cicp_colorspace = cicp_colorspace;
  options->huffman_sample = 1;
  switch (level) {
    case 1:
      options->predictor = 2;
      break;
    case 2:
      options->predictor = 4;
      break;
    case 3:
      options->predictor = 5;
      break;
    case 5:
      options->huffman_sample = 23;
      // fall through
    default:
      options->predictor = 6;
      break;
  }
}

// bytes_per_channel = 1/2 for 8-bit and 16-bit. num_channels: 1/2/3/4
// (G/GA/RGB/RGBA)
DLL_EXPORT size_t FPNGEEncode(size_t bytes_per_channel, size_t num_channels,
                   const void *data, size_t width, size_t row_stride,
                   size_t height, void *output,
                   const struct FPNGEOptions *options);

DLL_EXPORT size_t FPNGEOutputAllocSize(size_t bytes_per_channel,
                                   size_t num_channels, size_t width,
                                   size_t height);

#ifdef __cplusplus
}
#endif

#endif
