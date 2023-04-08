// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace xObsBeam;

class Qoi
{

  public const int PaddingLength = 8;
  const int SeenPixelsBufferLength = 64;
  const int MaxRunLength = 62;

  const byte QOI_OP_INDEX = 0x00;
  const byte QOI_OP_DIFF = 0x40;
  const byte QOI_OP_LUMA = 0x80;
  const byte QOI_OP_RUN = 0xc0;
  const byte QOI_OP_RGB = 0xfe;
  const byte QOI_OP_RGBA = 0xff;
  const byte QOI_MASK_2 = 0xc0;


  [StructLayout(LayoutKind.Explicit)]
  internal ref struct Pixel
  {
    [FieldOffset(0)] public byte R;
    [FieldOffset(1)] public byte G;
    [FieldOffset(2)] public byte B;
    [FieldOffset(3)] public byte A;
    [FieldOffset(0)] public int Value;
  }

  //TODO: explore options to work with frame difference based compression in addition, some ideas and a link to "QOV" here: https://github.com/phoboslab/qoi/issues/228 and here: https://github.com/nigeltao/qoi2-bikeshed/issues/37

  static int pixelHash(Pixel pixel) => (pixel.R * 3 + pixel.G * 5 + pixel.B * 7 + pixel.A * 11) % 64;
  public static unsafe int Encode(byte* data, int dataIndex, int dataSize, int channels, byte[] output)
  {
    var writeIndex = 0;

    Pixel previous = default;
    previous.A = 255;

    Pixel pixel = default;
    byte run = 0;
    var finalPixelIndex = dataSize - channels;

    var seenBuffer = stackalloc Pixel[SeenPixelsBufferLength];

    for (var readIndex = dataIndex; readIndex < dataSize; readIndex += channels)
    {
      pixel.B = data[readIndex + 0];
      pixel.G = data[readIndex + 1];
      pixel.R = data[readIndex + 2];

      if (channels == 4)
        pixel.A = data[readIndex + 3];
      else
        pixel.A = previous.A;

      if (pixel.Value == previous.Value)
      {
        run++;
        if (run == MaxRunLength || readIndex == finalPixelIndex)
        {
          output[writeIndex++] = (byte)(QOI_OP_RUN | (run - 1));
          run = 0;
        }
      }
      else
      {
        if (run > 0)
        {
          output[writeIndex++] = (byte)(QOI_OP_RUN | (run - 1));
          run = 0;
        }

        var hash = pixelHash(pixel);
        if (seenBuffer[hash].Value == pixel.Value)
          output[writeIndex++] = (byte)(QOI_OP_INDEX | hash);
        else
        {
          seenBuffer[hash] = pixel;

          if (pixel.A == previous.A)
          {
            var vr = (sbyte)(pixel.R - previous.R);
            var vg = (sbyte)(pixel.G - previous.G);
            var vb = (sbyte)(pixel.B - previous.B);

            var vg_r = (sbyte)(vr - vg);
            var vg_b = (sbyte)(vb - vg);

            if (
                vr > -3 && vr < 2 &&
                vg > -3 && vg < 2 &&
                vb > -3 && vb < 2
            )
            {
              output[writeIndex++] = (byte)(QOI_OP_DIFF | (vr + 2) << 4 | (vg + 2) << 2 | (vb + 2));
            }
            else if (
                vg_r > -9 && vg_r < 8 &&
                vg > -33 && vg < 32 &&
                vg_b > -9 && vg_b < 8
            )
            {
              output[writeIndex++] = (byte)(QOI_OP_LUMA | (vg + 32));
              output[writeIndex++] = (byte)((vg_r + 8) << 4 | (vg_b + 8));
            }
            else
            {
              output[writeIndex++] = QOI_OP_RGB;
              output[writeIndex++] = pixel.R;
              output[writeIndex++] = pixel.G;
              output[writeIndex++] = pixel.B;
            }
          }
          else
          {
            output[writeIndex++] = QOI_OP_RGBA;
            output[writeIndex++] = pixel.R;
            output[writeIndex++] = pixel.G;
            output[writeIndex++] = pixel.B;
            output[writeIndex++] = pixel.A;
          }
        }
      }

      previous = pixel;
    }

    writeIndex += 7;
    output[writeIndex++] = 1;
    return writeIndex;
  }

  public static byte[] Decode(ReadOnlySpan<byte> input, long outSize)
  {
    var inCursor = 0;

    var output = new byte[outSize];

    var run = 0;
    var chunksLen = input.Length - PaddingLength;

    Pixel pixel = default;
    pixel.A = 255;

    unsafe
    {
      var seenBuffer = stackalloc Pixel[SeenPixelsBufferLength];

      for (var outCursor = 0; outCursor < outSize; outCursor += 4)
      {
        if (run > 0)
        {
          run--;
        }
        else if (inCursor < chunksLen)
        {
          int b1 = input[inCursor++];

          if (b1 == QOI_OP_RGB)
          {
            pixel.R = input[inCursor++];
            pixel.G = input[inCursor++];
            pixel.B = input[inCursor++];
          }
          else if (b1 == QOI_OP_RGBA)
          {
            pixel.R = input[inCursor++];
            pixel.G = input[inCursor++];
            pixel.B = input[inCursor++];
            pixel.A = input[inCursor++];
          }
          else if ((b1 & QOI_MASK_2) == QOI_OP_INDEX)
          {
            pixel = seenBuffer[b1];
          }
          else if ((b1 & QOI_MASK_2) == QOI_OP_DIFF)
          {
            pixel.R += (byte)(((b1 >> 4) & 0x03) - 2);
            pixel.G += (byte)(((b1 >> 2) & 0x03) - 2);
            pixel.B += (byte)((b1 & 0x03) - 2);
          }
          else if ((b1 & QOI_MASK_2) == QOI_OP_LUMA)
          {
            int b2 = input[inCursor++];
            int vg = (b1 & 0x3f) - 32;

            pixel.R += (byte)(vg - 8 + ((b2 >> 4) & 0x0f));
            pixel.G += (byte)vg;
            pixel.B += (byte)(vg - 8 + (b2 & 0x0f));
          }
          else if ((b1 & QOI_MASK_2) == QOI_OP_RUN)
          {
            run = b1 & 0x3f;
          }

          seenBuffer[pixelHash(pixel) % 64] = pixel;
        }

        output[outCursor + 0] = pixel.B;
        output[outCursor + 1] = pixel.G;
        output[outCursor + 2] = pixel.R;
        output[outCursor + 3] = pixel.A;
      }
    }

    return output;
  }




}
