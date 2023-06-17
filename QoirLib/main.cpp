// main.cpp

#define QOIR_IMPLEMENTATION
#include "qoir.h" // Include your single-file C++ library header

extern "C" qoir_encode_result qoir_encode(const qoir_pixel_buffer * src_pixbuf, const qoir_encode_options * options);

