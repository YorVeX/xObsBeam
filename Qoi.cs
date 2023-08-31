// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace xObsBeam;

sealed class Qoi
{

  const int SeenPixelsBufferLength = 64; // we have 6 bits for indexing the buffer, meaning 2^6 = 64 entries
  const int MaxRunLength = 62; // need to stay below 62, otherwise combined with the QOI_OP_RUN opcode this would collide with the QOI_OP_RGB(A) opcodes

  const byte QOI_OP_INDEX = 0x00; // 00......
  const byte QOI_OP_DIFF = 0x40;  // 01......
  const byte QOI_OP_LUMA = 0x80;  // 10......
  const byte QOI_OP_RUN = 0xc0;   // 11......
  const byte QOI_OP_RGB = 0xfe;   // 11111110
  const byte QOI_OP_RGBA = 0xff;  // 11111111
  const byte QOI_MASK_2 = 0xc0;   // 11000000

  [StructLayout(LayoutKind.Explicit)]
  internal ref struct Pixel
  {
    [FieldOffset(0)] public byte R;
    [FieldOffset(1)] public byte G;
    [FieldOffset(2)] public byte B;
    [FieldOffset(3)] public byte A;
    [FieldOffset(0)] public int Value; // this offers a fast way to compare the whole pixel at once
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  static int PixelHash(Pixel pixel)
  {
    // crunches the pixel values into a number that fits into 6 bits - has lots of potential collisions, but it's fast and does the job of picking a slot in the buffer
    return ((pixel.R * 3) + (pixel.G * 5) + (pixel.B * 7) + (pixel.A * 11)) % 64;
  }

  public static unsafe int Encode(byte* data, int startIndex, int dataSize, int channels, byte[] output)
  {
    var writeIndex = 0;

    Pixel previous = default;
    previous.A = 255;

    Pixel pixel = default;
    byte run = 0; // "run" as in good old RLE
    var finalPixelIndex = (startIndex + dataSize) - channels;

    var seenBuffer = stackalloc Pixel[SeenPixelsBufferLength];

    for (var readIndex = startIndex; readIndex < (startIndex + dataSize); readIndex += channels)
    {
      // read the current pixel RGB color channels...
      pixel.B = data[readIndex + 0];
      pixel.G = data[readIndex + 1];
      pixel.R = data[readIndex + 2];

      // ...and the alpha channel if there is one
      if (channels == 4)
        pixel.A = data[readIndex + 3];
      else
        pixel.A = previous.A;

      if (pixel.Value == previous.Value) // full match with the previous pixel, increase the run length
      {
        run++;
        if (run == MaxRunLength || readIndex == finalPixelIndex) // did we reach the maximum run length or the end of the image?
        {
          // in that case finish the run
          output[writeIndex++] = (byte)(QOI_OP_RUN | (run - 1));
          run = 0;
        }
      }
      else
      {
        if (run > 0) // no more full match, finish previous run if there was one
        {
          output[writeIndex++] = (byte)(QOI_OP_RUN | (run - 1));
          run = 0;
        }

        // check if we already saw this pixel before in the last 64 pixels
        var hash = PixelHash(pixel);
        if (seenBuffer[hash].Value == pixel.Value)
          output[writeIndex++] = (byte)(QOI_OP_INDEX | hash); // 2 bit opcode, 6 bit index into the last 64 pixels
        else
        {
          seenBuffer[hash] = pixel; // move the new pixel into the last 64 pixels buffer

          if (pixel.A == previous.A) // if the alpha channel is the same as the previous pixel, we can use a more efficient opcode for RGB
          {
            // calculate the difference of each color channel with its previous pixel
            var diffR = (sbyte)(pixel.R - previous.R);
            var diffG = (sbyte)(pixel.G - previous.G);
            var diffB = (sbyte)(pixel.B - previous.B);

            // calculate the difference of the green channel difference with the red and blue channel differences
            var diffGR = (sbyte)(diffR - diffG);
            var diffBR = (sbyte)(diffB - diffG);

            if ( // does the difference of each color channel with its previous pixel fit into 2 bits, for a total of 6 bits?
                diffR > -3 && diffR < 2 &&
                diffG > -3 && diffG < 2 &&
                diffB > -3 && diffB < 2
            )
            {
              // 2 bits for the opcode, the remaining 6 bits for the difference of each color channel, puzzle it all together here
              output[writeIndex++] = (byte)(QOI_OP_DIFF | ((diffR + 2) << 4) | ((diffG + 2) << 2) | (diffB + 2));
            }
            else if ( // does the difference of the green channel with its previous pixel and with the red and blue channel differences fit into a total of 14 bits (so that it's 16 in total with the 2 bit opcode)?
                diffGR > -9 && diffGR < 8 && // 15 values = 4 bits
                diffG > -33 && diffG < 32 && // 63 values = 6 bits
                diffBR > -9 && diffBR < 8    // 15 values = 4 bits
            )
            {
              // the first byte contains the opcode and the difference of the green channel with its previous pixel
              output[writeIndex++] = (byte)(QOI_OP_LUMA | (diffG + 32));
              // the second byte contains the difference of the green channel difference with the red and blue channel differences
              output[writeIndex++] = (byte)(((diffGR + 8) << 4) | (diffBR + 8));
            }
            else // worst case scenario for RGB, all channel values plus the opcode have to be stored, meaning originally 3 bytes turned into 4 bytes
            {
              output[writeIndex++] = QOI_OP_RGB;
              output[writeIndex++] = pixel.R;
              output[writeIndex++] = pixel.G;
              output[writeIndex++] = pixel.B;
            }
          }
          else // worst case scenario for RGBA, all channel values plus the opcode have to be stored, meaning originally 4 bytes turned into 5 bytes
          {
            output[writeIndex++] = QOI_OP_RGBA;
            output[writeIndex++] = pixel.R;
            output[writeIndex++] = pixel.G;
            output[writeIndex++] = pixel.B;
            output[writeIndex++] = pixel.A;
          }
        }
      }

      previous = pixel; // remember the current pixel for the next iteration
    }
    return writeIndex;
  }

  public static unsafe void Decode(ReadOnlySpan<byte> input, long inSize, byte[] output, long outSize)
  {
    var inCursor = 0;

    var run = 0;

    Pixel pixel = default;
    pixel.A = 255;

    var seenBuffer = stackalloc Pixel[SeenPixelsBufferLength];

    for (var outCursor = 0; outCursor < outSize; outCursor += 4) // iterate in packs of 4 bytes for all color channels
    {
      if (run > 0) // within a run, all pixels are the same as the previous one
        run--;
      else if (inCursor < inSize) // not in a run, as long as the end of the input hasn't been reached yet, read the next opcode
      {
        int b1 = input[inCursor++]; // read the first byte that will contain the opcode

        if (b1 == QOI_OP_RGB)
        {
          // need to read 3 bytes for RGB
          pixel.R = input[inCursor++];
          pixel.G = input[inCursor++];
          pixel.B = input[inCursor++];
        }
        else if (b1 == QOI_OP_RGBA)
        {
          // need to read 4 bytes for RGBA
          pixel.R = input[inCursor++];
          pixel.G = input[inCursor++];
          pixel.B = input[inCursor++];
          pixel.A = input[inCursor++];
        }
        else if ((b1 & QOI_MASK_2) == QOI_OP_INDEX)
        {
          // get the pixel from the last 64 pixels buffer indexed by the 6 bits next to the two 0 opcode bits
          pixel = seenBuffer[b1];
        }
        else if ((b1 & QOI_MASK_2) == QOI_OP_DIFF)
        {
          // apply the difference of each color channel to the previous pixel values that are still in the pixel variable
          pixel.R += (byte)(((b1 >> 4) & 0x03) - 2);
          pixel.G += (byte)(((b1 >> 2) & 0x03) - 2);
          pixel.B += (byte)((b1 & 0x03) - 2);
        }
        else if ((b1 & QOI_MASK_2) == QOI_OP_LUMA)
        {
          // get the difference of the green channel with its previous pixel to the previous pixel values that are still in the pixel variable
          int b2 = input[inCursor++];
          int vg = (b1 & 0x3f) - 32;

          // apply the difference of the green channel difference with the red and blue channel differences to the previous pixel values that are still in the pixel variable
          pixel.R += (byte)(vg - 8 + ((b2 >> 4) & 0x0f));
          pixel.G += (byte)vg;
          pixel.B += (byte)(vg - 8 + (b2 & 0x0f));
        }
        else if ((b1 & QOI_MASK_2) == QOI_OP_RUN)
          run = b1 & 0x3f; // the current pixel will be repeated this many times

        seenBuffer[PixelHash(pixel) % 64] = pixel; // move the new pixel into the last 64 pixels buffer
      }

      // write the current pixel to the output buffer
      output[outCursor + 0] = pixel.B;
      output[outCursor + 1] = pixel.G;
      output[outCursor + 2] = pixel.R;
      output[outCursor + 3] = pixel.A;
    }
  }
}
