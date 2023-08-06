// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

/*
C# port of the QOY C++ code from here: https://github.com/Chainfire/qoy/tree/fd9f9ea359cdea0ec9101617159031cb06447583
Changes to the original code:
- Type changes necessary to make it work in C#
- Removed header data (Beam has its own headers)
- Removed EOF padding and any checks for it (covered by Beam headers)
- Removed some unnecessary binary ANDs after RSHs in the decode function
- Variable and function name changes to make it fit C# and into the style of this project
- Added separate GetMaxSize function, any necessary buffer memory is supposed to be allocated by the caller based on that
- Adapted the code to work with NV12 format as input and output (original seems to use some non-standard packing format not useful here)
- Removed any code for alpha channel data, since OBS doesn't support that for NV12 anyway
- QOY_OP_RUN_X only supports up to 257 pixels (only one byte for the run length) for reduced complexity

Original license:
-- LICENSE: The MIT License(MIT)

Copyright(c) 2021 Dominic Szablewski
Copyright(c) 2021 Jorrit "Chainfire" Jongma

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files(the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and / or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions :
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

NOTICE: QOY follows QOI's license. If QOI is re-licensed, you may apply that
license to QOY as well.

*/

using System.Runtime.InteropServices;

namespace xObsBeam;

sealed class Qoy
{
  const byte QOY_OP_321_MASK = 0x80; /* 1.......                                                                          */
  const byte QOY_OP_321 = 0x00;      /* 0yyyYYYy yyYYYbbr                                     y:4*3 cb:2 cr:1 --> 2 bytes */
  const byte QOY_OP_433_MASK = 0xc0; /* 11......                                                                          */
  const byte QOY_OP_433 = 0x80;      /* 10yyyyYY YYyyyyYY YYbbbrrr                            y:4*4 cb:3 cr:3 --> 3 bytes */
  const byte QOY_OP_554_MASK = 0xe0; /* 111.....                                                                          */
  const byte QOY_OP_554 = 0xc0;      /* 110yyyyy YYYYYyyy yyYYYYYb bbbbrrrr                   y:4*5 cb:5 cr:4 --> 4 bytes */
  const byte QOY_OP_666_MASK = 0xf0; /* 1111....                                                                          */
  const byte QOY_OP_666 = 0xe0;      /* 1110yyyy yyYYYYYY yyyyyyYY YYYYbbbb bbrrrrrr          y:4*6 cb:6 cr:6 --> 5 bytes */
  const byte QOY_OP_865_MASK = 0xf8; /* 11111...                                                                          */
  const byte QOY_OP_865 = 0xf0;      /* 11110yyy yyyyyYYY YYYYYyyy yyyyyYYY YYYYYbbb bbbrrrrr y:4*8 cb:6 cr:5 --> 6 bytes */

  // unused, just to remember it could be used for something if necessary (but probably not another diff combination, the existing ones cover the common cases very well)
  const byte QOY_OP_A_MASK = 0xfc;   /* 111111..                                                                          */
  const byte QOY_OP_A_ANY = 0xf8;    /* 11111000                                                                          */
  const byte QOY_OP_A18 = 0xf8;      /* 11111000 aaaaaaaa                                     a:1*8           --> 2 bytes */
  const byte QOY_OP_A42 = 0xf9;      /* 11111001 aaAAaaAA                                     a:4*2           --> 2 bytes */
  const byte QOY_OP_A44 = 0xfa;      /* 11111010 aaaaAAAA aaaaAAAA                            a:4*4           --> 3 bytes */
  const byte QOY_OP_A48 = 0xfb;      /* 11111011 aaaaaaaa AAAAAAAA aaaaaaaa AAAAAAAA          a:4*8           --> 5 bytes */

  const byte QOY_OP_RUN_MASK = 0xff; /* 11111111                                                                          */
  const byte QOY_OP_RUN_1 = 0xfc;    /* 11111100                                              [1]             --> 1 byte  */
  const byte QOY_OP_RUN_X = 0xfd;    /* 11111101 cccccccc                                     [2..257]        --> 2 bytes */

  const byte QOY_OP_888_MASK = 0xff; /* 11111111                                                                          */
  const byte QOY_OP_888 = 0xfe;      /* 11111110 y8 y8 y8 y8 b8 r8                            y:4*8 cb:8 cr:8 --> 7 bytes */

  const byte QOY_OP_UNUSED = 0xff;   /* 11111111 just to remember this could be used for something if necessary           */

  [StructLayout(LayoutKind.Explicit)]
  internal unsafe ref struct PixelPack
  {
    [FieldOffset(0)] public fixed byte y[4];
    [FieldOffset(4)] public byte cb;
    [FieldOffset(5)] public byte cr;
  }

  [StructLayout(LayoutKind.Explicit)]
  internal unsafe ref struct PixelPackDiff
  {
    [FieldOffset(0)] public fixed sbyte y[4];
    [FieldOffset(4)] public sbyte cb;
    [FieldOffset(5)] public sbyte cr;
  }

  public static unsafe int GetMaxSize(int width, int height)
  {
    int internal_width = (width + 1) & ~0x01;
    int internal_height = (height + 1) & ~0x01;
    return ((((internal_width * internal_height) + 3) >> 2) * 7);
  }

  public static unsafe int Encode(byte* data, uint width, uint height, int startIndex, int dataSize, byte[] output)
  {
    int writeIndex = 0;

    PixelPack pixelPack = default;
    PixelPack previousPixelPack = default;

    PixelPackDiff pixelPackDiff = default;

    uint pixelCount = width * height;
    uint stride = width * 4;

    uint cbCrIndex = pixelCount;
    int run = 0;

    for (var yIndex = startIndex; yIndex < pixelCount; yIndex += 4)
    {
      pixelPack.y[0] = data[yIndex + 0];
      pixelPack.y[1] = data[yIndex + 1];
      pixelPack.y[2] = data[yIndex + 2];
      pixelPack.y[3] = data[yIndex + 3];
      pixelPack.cb = data[cbCrIndex + 0];
      pixelPack.cr = data[cbCrIndex + 1];
      cbCrIndex += 2;

      pixelPackDiff.y[0] = (sbyte)(pixelPack.y[0] - previousPixelPack.y[2]);
      pixelPackDiff.y[1] = (sbyte)(pixelPack.y[1] - previousPixelPack.y[3]);
      pixelPackDiff.y[2] = (sbyte)(pixelPack.y[2] - pixelPack.y[0]);
      pixelPackDiff.y[3] = (sbyte)(pixelPack.y[3] - pixelPack.y[1]);
      pixelPackDiff.cb = (sbyte)(pixelPack.cb - previousPixelPack.cb);
      pixelPackDiff.cr = (sbyte)(pixelPack.cr - previousPixelPack.cr);

      if (pixelPackDiff.y[0] == 0 && pixelPackDiff.y[1] == 0 && pixelPackDiff.y[2] == 0 && pixelPackDiff.y[3] == 0 && pixelPackDiff.y[4] == 0 && pixelPackDiff.cb == 0 && pixelPackDiff.cr == 0)
      {
        run++;
        if (run == 258)
          run = 1;
        if (run == 1)
          output[writeIndex++] = QOY_OP_RUN_1;
        else if (run == 2)
        {
          output[writeIndex - 1] = QOY_OP_RUN_X;
          output[writeIndex++] = (byte)(run - 2);
        }
        else if (run < 258)
          output[writeIndex - 1] = (byte)(run - 2);
      }
      else
      {
        run = 0;
        int yBits, crBits, cbBits;

        sbyte yMin = pixelPackDiff.y[0];
        sbyte yMax = pixelPackDiff.y[0];
        if (pixelPackDiff.y[1] < yMin)
          yMin = pixelPackDiff.y[1];
        if (pixelPackDiff.y[1] > yMax)
          yMax = pixelPackDiff.y[1];
        if (pixelPackDiff.y[2] < yMin)
          yMin = pixelPackDiff.y[2];
        if (pixelPackDiff.y[2] > yMax)
          yMax = pixelPackDiff.y[2];
        if (pixelPackDiff.y[3] < yMin)
          yMin = pixelPackDiff.y[3];
        if (pixelPackDiff.y[3] > yMax)
          yMax = pixelPackDiff.y[3];

        if (yMin >= -4 && yMax < 4)
          yBits = 3;
        else if (yMin >= -8 && yMax < 8)
          yBits = 4;
        else if (yMin >= -16 && yMax < 16)
          yBits = 5;
        else if (yMin >= -32 && yMax < 32)
          yBits = 6;
        else
          yBits = 8;

        if (pixelPackDiff.cb is >= -2 and < 2)
          cbBits = 2;
        else if (pixelPackDiff.cb is >= -4 and < 4)
          cbBits = 3;
        else if (pixelPackDiff.cb is >= -16 and < 16)
          cbBits = 5;
        else if (pixelPackDiff.cb is >= -32 and < 32)
          cbBits = 6;
        else
          cbBits = 8;

        if (pixelPackDiff.cr is >= -1 and < 1)
          crBits = 1;
        else if (pixelPackDiff.cr is >= -4 and < 4)
          crBits = 3;
        else if (pixelPackDiff.cr is >= -8 and < 8)
          crBits = 4;
        else if (pixelPackDiff.cr is >= -16 and < 16)
          crBits = 5;
        else if (pixelPackDiff.cr is >= -32 and < 32)
          crBits = 6;
        else
          crBits = 8;

        if (yBits <= 3 && cbBits <= 2 && crBits <= 1)
        {
          output[writeIndex++] = (byte)(QOY_OP_321 | ((pixelPackDiff.y[0] + 4) << 4) | ((pixelPackDiff.y[1] + 4) << 1) | ((pixelPackDiff.y[2] + 4) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[2] + 4) << 6) | ((pixelPackDiff.y[3] + 4) << 3) | ((pixelPackDiff.cb + 2) << 1) | (pixelPackDiff.cr + 1));
        }
        else if (yBits <= 4 && cbBits <= 3 && crBits <= 3)
        {
          output[writeIndex++] = (byte)(QOY_OP_433 | ((pixelPackDiff.y[0] + 8) << 2) | ((pixelPackDiff.y[1] + 8) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[1] + 8) << 6) | ((pixelPackDiff.y[2] + 8) << 2) | ((pixelPackDiff.y[3] + 8) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[3] + 8) << 6) | ((pixelPackDiff.cb + 4) << 3) | (pixelPackDiff.cr + 4));
        }
        else if (yBits <= 5 && cbBits <= 5 && crBits <= 4)
        {
          output[writeIndex++] = (byte)(QOY_OP_554 | (pixelPackDiff.y[0] + 16));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[1] + 16) << 3) | ((pixelPackDiff.y[2] + 16) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[2] + 16) << 6) | ((pixelPackDiff.y[3] + 16) << 1) | ((pixelPackDiff.cb + 16) >> 4));
          output[writeIndex++] = (byte)(((pixelPackDiff.cb + 16) << 4) | (pixelPackDiff.cr + 8));
        }
        else if (yBits <= 6 && cbBits <= 6 && crBits <= 6)
        {
          output[writeIndex++] = (byte)(QOY_OP_666 | ((pixelPackDiff.y[0] + 32) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[0] + 32) << 6) | (pixelPackDiff.y[1] + 32));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[2] + 32) << 2) | ((pixelPackDiff.y[3] + 32) >> 4));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[3] + 32) << 4) | ((pixelPackDiff.cb + 32) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.cb + 32) << 6) | (pixelPackDiff.cr + 32));
        }
        else if (yBits <= 8 && cbBits <= 6 && crBits <= 5)
        {
          output[writeIndex++] = (byte)(QOY_OP_865 | ((pixelPackDiff.y[0] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[0] + 128) << 3) | ((pixelPackDiff.y[1] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[1] + 128) << 3) | ((pixelPackDiff.y[2] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[2] + 128) << 3) | ((pixelPackDiff.y[3] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.y[3] + 128) << 3) | ((pixelPackDiff.cb + 32) >> 3));
          output[writeIndex++] = (byte)(((pixelPackDiff.cb + 32) << 5) | (pixelPackDiff.cr + 16));
        }
        else
        {
          output[writeIndex++] = QOY_OP_888;
          output[writeIndex++] = pixelPack.y[0];
          output[writeIndex++] = pixelPack.y[1];
          output[writeIndex++] = pixelPack.y[2];
          output[writeIndex++] = pixelPack.y[3];
          output[writeIndex++] = pixelPack.cb;
          output[writeIndex++] = pixelPack.cr;
        }
      }

      previousPixelPack = pixelPack;
    }

    return writeIndex;
  }

  public static unsafe void Decode(ReadOnlySpan<byte> input, uint width, uint height, long inSize, byte[] output, long outSize)
  {
    PixelPack pixelPack = default;

    int readIndex = 0;
    int run = 0;
    uint pixelCount = width * height;

    uint cbCrIndex = pixelCount;

    for (var yIndex = 0; yIndex < pixelCount; yIndex += 4)
    {
      if (run > 0)
      {
        pixelPack.y[0] = pixelPack.y[2];
        pixelPack.y[1] = pixelPack.y[3];
        run--;
      }
      else
      {
        var b1 = input[readIndex++];
        if ((b1 & QOY_OP_RUN_MASK) == QOY_OP_RUN_1)
        {
          pixelPack.y[0] = pixelPack.y[2];
          pixelPack.y[1] = pixelPack.y[3];
        }
        else if ((b1 & QOY_OP_RUN_MASK) == QOY_OP_RUN_X)
        {
          pixelPack.y[0] = pixelPack.y[2];
          pixelPack.y[1] = pixelPack.y[3];
          var b2 = input[readIndex++];
          run = b2 + 2 - 1;
        }
        else if ((b1 & QOY_OP_888_MASK) == QOY_OP_888)
        {
          pixelPack.y[0] = input[readIndex++];
          pixelPack.y[1] = input[readIndex++];
          pixelPack.y[2] = input[readIndex++];
          pixelPack.y[3] = input[readIndex++];
          pixelPack.cb = input[readIndex++];
          pixelPack.cr = input[readIndex++];
        }
        else if ((b1 & QOY_OP_321_MASK) == QOY_OP_321)
        {
          var b2 = input[readIndex++];
          pixelPack.y[0] = (byte)(pixelPack.y[2] + ((b1 >> 4)) - 4);
          pixelPack.y[1] = (byte)(pixelPack.y[3] + ((b1 >> 1) & 0x07) - 4);
          pixelPack.y[2] = (byte)(pixelPack.y[0] + ((b1 & 0x01) << 2) + (b2 >> 6) - 4);
          pixelPack.y[3] = (byte)(pixelPack.y[1] + ((b2 >> 3) & 0x07) - 4);
          pixelPack.cb = (byte)(pixelPack.cb + ((b2 >> 1) & 0x03) - 2);
          pixelPack.cr = (byte)(pixelPack.cr + (b2 & 0x01) - 1);
        }
        else if ((b1 & QOY_OP_433_MASK) == QOY_OP_433)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          pixelPack.y[0] = (byte)(pixelPack.y[2] + ((b1 >> 2) & 0x0F) - 8);
          pixelPack.y[1] = (byte)(pixelPack.y[3] + ((b1 & 0x03) << 2) + (b2 >> 6) - 8);
          pixelPack.y[2] = (byte)(pixelPack.y[0] + ((b2 >> 2) & 0x0F) - 8);
          pixelPack.y[3] = (byte)(pixelPack.y[1] + ((b2 & 0x03) << 2) + (b3 >> 6) - 8);
          pixelPack.cb = (byte)(pixelPack.cb + ((b3 >> 3) & 0x07) - 4);
          pixelPack.cr = (byte)(pixelPack.cr + (b3 & 0x07) - 4);
        }
        else if ((b1 & QOY_OP_554_MASK) == QOY_OP_554)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          pixelPack.y[0] = (byte)(pixelPack.y[2] + (b1 & 0x1F) - 16);
          pixelPack.y[1] = (byte)(pixelPack.y[3] + ((b2 >> 3) & 0x1F) - 16);
          pixelPack.y[2] = (byte)(pixelPack.y[0] + ((b2 & 0x07) << 2) + (b3 >> 6) - 16);
          pixelPack.y[3] = (byte)(pixelPack.y[1] + ((b3 >> 1) & 0x1F) - 16);
          pixelPack.cb = (byte)(pixelPack.cb + ((b3 & 0x01) << 4) + (b4 >> 4) - 16);
          pixelPack.cr = (byte)(pixelPack.cr + (b4 & 0x0F) - 8);
        }
        else if ((b1 & QOY_OP_666_MASK) == QOY_OP_666)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          var b5 = input[readIndex++];
          pixelPack.y[0] = (byte)(pixelPack.y[2] + ((b1 & 0x0F) << 2) + (b2 >> 6) - 32);
          pixelPack.y[1] = (byte)(pixelPack.y[3] + (b2 & 0x3F) - 32);
          pixelPack.y[2] = (byte)(pixelPack.y[0] + ((b3 >> 2) & 0x3F) - 32);
          pixelPack.y[3] = (byte)(pixelPack.y[1] + ((b3 & 0x03) << 4) + (b4 >> 4) - 32);
          pixelPack.cb = (byte)(pixelPack.cb + ((b4 & 0x0F) << 2) + (b5 >> 6) - 32);
          pixelPack.cr = (byte)(pixelPack.cr + (b5 & 0x3F) - 32);
        }
        else if ((b1 & QOY_OP_865_MASK) == QOY_OP_865)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          var b5 = input[readIndex++];
          var b6 = input[readIndex++];
          pixelPack.y[0] = (byte)(pixelPack.y[2] + ((b1 & 0x07) << 5) + (b2 >> 3) - 128);
          pixelPack.y[1] = (byte)(pixelPack.y[3] + ((b2 & 0x07) << 5) + (b3 >> 3) - 128);
          pixelPack.y[2] = (byte)(pixelPack.y[0] + ((b3 & 0x07) << 5) + (b4 >> 3) - 128);
          pixelPack.y[3] = (byte)(pixelPack.y[1] + ((b4 & 0x07) << 5) + (b5 >> 3) - 128);
          pixelPack.cb = (byte)(pixelPack.cb + ((b5 & 0x07) << 3) + (b6 >> 5) - 32);
          pixelPack.cr = (byte)(pixelPack.cr + (b6 & 0x1F) - 16);
        }
      }

      output[yIndex + 0] = pixelPack.y[0];
      output[yIndex + 1] = pixelPack.y[1];
      output[yIndex + 2] = pixelPack.y[2];
      output[yIndex + 3] = pixelPack.y[3];
      output[cbCrIndex + 0] = pixelPack.cb;
      output[cbCrIndex + 1] = pixelPack.cr;
      cbCrIndex += 2; // unlike yIndex this is not incremented by the main loop
    }
  }
}
