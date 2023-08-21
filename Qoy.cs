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
- These encoder changes together resulted in a 13.7% speedup in tests on one specific machine with some very simple "benchmarking" (obviously very subjective)
  - Encoder: Replaced some field assignments for diff calculation with direct input data array access
  - Encoder: Added lookup table to speed up Cb and Cr diff bit calculations
  - Encoder: Added Value field to diff struct for faster zero checks (original code also has a very minor bug there that is implicitly fixed by this)
- Encoder: Diff between adjacent Y values instead of "crossover" ([0] - [2] and [1] - [3]) for better compression ratio and maybe later potential SIMD optimizations
- Encoder: Added skipping for some pixels when a bunch of bad cases occurs in a row (666 or higher) for further speedup, sacrificing a bit of compression ratio

Original copyrights and licenses:
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
  const byte PixelSkipCount = 16;

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
  const byte QOY_OP_SKIP = 0xff;     /* 11111111 skip fixed amount of pixel packs                                         */

  [StructLayout(LayoutKind.Explicit)]
  internal unsafe ref struct PixelPack
  {
    [FieldOffset(0)] public fixed byte Y[4];
    [FieldOffset(4)] public byte Cb;
    [FieldOffset(5)] public byte Cr;
  }

  [StructLayout(LayoutKind.Explicit)]
  internal unsafe ref struct PixelPackDiff
  {
    [FieldOffset(0)] public fixed sbyte Y[4];
    [FieldOffset(4)] public sbyte Cb;
    [FieldOffset(5)] public sbyte Cr;
    [FieldOffset(4)] public ushort CbCrValue;
    [FieldOffset(6)] public short Zero; // needed to fill two more bytes with zero so that...
    [FieldOffset(0)] public ulong Value; // ...this can be used to compare the whole struct at once (the previous 8 bytes combined as one ulong)
  }

  [StructLayout(LayoutKind.Explicit)]
  internal struct PixelPackCbCr
  {
    [FieldOffset(0)] public sbyte Cb;
    [FieldOffset(1)] public sbyte Cr;
    [FieldOffset(0)] public ushort Value; // ...this can be used to compare the whole struct at once (the previous 2 bytes combined as one ushort)
  }

  public static unsafe int GetMaxSize(int width, int height)
  {
    int internal_width = (width + 1) & ~0x01;
    int internal_height = (height + 1) & ~0x01;
    return ((((internal_width * internal_height) + 3) >> 2) * 7);
  }

  static readonly ushort[] CbCrBitsLookup = new ushort[ushort.MaxValue + 1];
  static bool _initialized;
  public static unsafe void Initialize()
  {
    if (_initialized)
      return;

    byte cbBits, crBits;

    for (short cbDiff = -128; cbDiff < 128; cbDiff++)
    {
      for (short crDiff = -128; crDiff < 128; crDiff++)
      {
        if (cbDiff is >= -2 and < 2)
          cbBits = 2;
        else if (cbDiff is >= -4 and < 4)
          cbBits = 3;
        else if (cbDiff is >= -16 and < 16)
          cbBits = 5;
        else if (cbDiff is >= -32 and < 32)
          cbBits = 6;
        else
          cbBits = 8;

        if (crDiff is >= -1 and < 1)
          crBits = 1;
        else if (crDiff is >= -4 and < 4)
          crBits = 3;
        else if (crDiff is >= -8 and < 8)
          crBits = 4;
        else if (crDiff is >= -16 and < 16)
          crBits = 5;
        else if (crDiff is >= -32 and < 32)
          crBits = 6;
        else
          crBits = 8;

        CbCrBitsLookup[((byte)crDiff << 8) | (byte)cbDiff] = (ushort)((crBits << 8) | cbBits);
      }
    }
    _initialized = true;
  }

  public static unsafe int Encode(byte* data, uint width, uint height, int startIndex, int dataSize, byte[] output)
  {
    int writeIndex = 0;

    PixelPackDiff pixelPackDiff = default;
    PixelPackCbCr bitsCbCr = default;

    uint pixelCount = width * height;

    uint cbCrIndex = pixelCount;
    int run = 0;
    int badPixels = 0;
    int skipBadPixels = 0;
    int yBits;
    byte* yPlane = null;
    byte* cPlane = null;
    int zeroInt;
    byte* yPlanePrevious = (byte*)&zeroInt; // need a place to point to for the first comparison to the previous pixel so that it's zero
    byte* cPlanePrevious = (byte*)&zeroInt;

    for (var yIndex = startIndex; yIndex < pixelCount; yIndex += 4)
    {
      yPlane = (data + yIndex);
      cPlane = (data + cbCrIndex);

      if ((badPixels > 4) || (skipBadPixels > 0))
      {
        if (skipBadPixels == 0)
        {
          skipBadPixels = PixelSkipCount;
          badPixels--; // push it back just below the threshold so that next run after skipBadPixels reached 0 will do a diff, but if that is a bad pixel again we will immediately be back here to skip another round (and if it's not reset and go on without skipping)
          output[writeIndex++] = QOY_OP_SKIP;
        }
        new ReadOnlySpan<byte>(yPlane, 4).CopyTo(output.AsSpan(writeIndex));
        writeIndex += 4;
        new ReadOnlySpan<byte>(cPlane, 2).CopyTo(output.AsSpan(writeIndex));
        writeIndex += 2;

        yPlanePrevious = yPlane;
        cPlanePrevious = cPlane;
        cbCrIndex += 2; // unlike yIndex this is not incremented by the main loop
        skipBadPixels--;
        continue;
      }

      pixelPackDiff.Y[0] = (sbyte)(yPlane[0] - yPlanePrevious[3]);
      pixelPackDiff.Y[1] = (sbyte)(yPlane[1] - yPlane[0]);
      pixelPackDiff.Y[2] = (sbyte)(yPlane[2] - yPlane[1]);
      pixelPackDiff.Y[3] = (sbyte)(yPlane[3] - yPlane[2]);
      pixelPackDiff.Cb = (sbyte)(cPlane[0] - cPlanePrevious[0]);
      pixelPackDiff.Cr = (sbyte)(cPlane[1] - cPlanePrevious[1]);

      if (pixelPackDiff.Value == 0)
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

        sbyte yMin = pixelPackDiff.Y[0];
        sbyte yMax = pixelPackDiff.Y[0];
        if (pixelPackDiff.Y[1] < yMin)
          yMin = pixelPackDiff.Y[1];
        if (pixelPackDiff.Y[1] > yMax)
          yMax = pixelPackDiff.Y[1];
        if (pixelPackDiff.Y[2] < yMin)
          yMin = pixelPackDiff.Y[2];
        if (pixelPackDiff.Y[2] > yMax)
          yMax = pixelPackDiff.Y[2];
        if (pixelPackDiff.Y[3] < yMin)
          yMin = pixelPackDiff.Y[3];
        if (pixelPackDiff.Y[3] > yMax)
          yMax = pixelPackDiff.Y[3];

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

        // according to tests using this lookup table is faster than calculating the bit count here (unlike for yBits above, where tests have shown the opposite)
        bitsCbCr.Value = CbCrBitsLookup[pixelPackDiff.CbCrValue];

        if (yBits <= 3 && bitsCbCr.Cb <= 2 && bitsCbCr.Cr <= 1)
        {
          badPixels = 0;
          output[writeIndex++] = (byte)(QOY_OP_321 | ((pixelPackDiff.Y[0] + 4) << 4) | ((pixelPackDiff.Y[1] + 4) << 1) | ((pixelPackDiff.Y[2] + 4) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[2] + 4) << 6) | ((pixelPackDiff.Y[3] + 4) << 3) | ((pixelPackDiff.Cb + 2) << 1) | (pixelPackDiff.Cr + 1));
        }
        else if (yBits <= 4 && bitsCbCr.Cb <= 3 && bitsCbCr.Cr <= 3)
        {
          badPixels = 0;
          output[writeIndex++] = (byte)(QOY_OP_433 | ((pixelPackDiff.Y[0] + 8) << 2) | ((pixelPackDiff.Y[1] + 8) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[1] + 8) << 6) | ((pixelPackDiff.Y[2] + 8) << 2) | ((pixelPackDiff.Y[3] + 8) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[3] + 8) << 6) | ((pixelPackDiff.Cb + 4) << 3) | (pixelPackDiff.Cr + 4));
        }
        else if (yBits <= 5 && bitsCbCr.Cb <= 5 && bitsCbCr.Cr <= 4)
        {
          badPixels = 0;
          output[writeIndex++] = (byte)(QOY_OP_554 | (pixelPackDiff.Y[0] + 16));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[1] + 16) << 3) | ((pixelPackDiff.Y[2] + 16) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[2] + 16) << 6) | ((pixelPackDiff.Y[3] + 16) << 1) | ((pixelPackDiff.Cb + 16) >> 4));
          output[writeIndex++] = (byte)(((pixelPackDiff.Cb + 16) << 4) | (pixelPackDiff.Cr + 8));
        }
        else if (yBits <= 6 && bitsCbCr.Cb <= 6 && bitsCbCr.Cr <= 6)
        {
          badPixels++;
          output[writeIndex++] = (byte)(QOY_OP_666 | ((pixelPackDiff.Y[0] + 32) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[0] + 32) << 6) | (pixelPackDiff.Y[1] + 32));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[2] + 32) << 2) | ((pixelPackDiff.Y[3] + 32) >> 4));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[3] + 32) << 4) | ((pixelPackDiff.Cb + 32) >> 2));
          output[writeIndex++] = (byte)(((pixelPackDiff.Cb + 32) << 6) | (pixelPackDiff.Cr + 32));
        }
        else if (yBits <= 8 && bitsCbCr.Cb <= 6 && bitsCbCr.Cr <= 5)
        {
          badPixels++;
          output[writeIndex++] = (byte)(QOY_OP_865 | ((pixelPackDiff.Y[0] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[0] + 128) << 3) | ((pixelPackDiff.Y[1] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[1] + 128) << 3) | ((pixelPackDiff.Y[2] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[2] + 128) << 3) | ((pixelPackDiff.Y[3] + 128) >> 5));
          output[writeIndex++] = (byte)(((pixelPackDiff.Y[3] + 128) << 3) | ((pixelPackDiff.Cb + 32) >> 3));
          output[writeIndex++] = (byte)(((pixelPackDiff.Cb + 32) << 5) | (pixelPackDiff.Cr + 16));
        }
        else
        {
          badPixels++;
          output[writeIndex++] = QOY_OP_888;
          new ReadOnlySpan<byte>(yPlane, 4).CopyTo(output.AsSpan(writeIndex));
          writeIndex += 4;
          new ReadOnlySpan<byte>(cPlane, 2).CopyTo(output.AsSpan(writeIndex));
          writeIndex += 2;
        }
      }

      yPlanePrevious = yPlane;
      cPlanePrevious = cPlane;
      cbCrIndex += 2; // unlike yIndex this is not incremented by the main loop
    }

    // var compressionPercentage = 100 - ((dataSize - writeIndex) * 100 / dataSize);
    // Module.Log($"---------- QOY compressed {dataSize} to {writeIndex} ({compressionPercentage} %) --------------------------------------------", ObsLogLevel.Warning);
    return writeIndex;
  }

  public static unsafe void Decode(ReadOnlySpan<byte> input, uint width, uint height, long inSize, byte[] output, long outSize)
  {
    PixelPack pixelPack = default;

    int readIndex = 0;
    int run = 0;
    int skipPixels = 0;
    uint pixelCount = width * height;

    uint cbCrIndex = pixelCount;

    for (var yIndex = 0; yIndex < pixelCount; yIndex += 4)
    {
      if (run > 0)
      {
        pixelPack.Y[0] = pixelPack.Y[3];
        pixelPack.Y[1] = pixelPack.Y[3];
        pixelPack.Y[2] = pixelPack.Y[3];
        run--;
      }
      else if (skipPixels > 0) // skipped means they are not encoded and have no opcodes, just read the raw data
      {
        pixelPack.Y[0] = input[readIndex++];
        pixelPack.Y[1] = input[readIndex++];
        pixelPack.Y[2] = input[readIndex++];
        pixelPack.Y[3] = input[readIndex++];
        pixelPack.Cb = input[readIndex++];
        pixelPack.Cr = input[readIndex++];
        skipPixels--;
      }
      else
      {
        var b1 = input[readIndex++];
        if ((b1 & QOY_OP_RUN_MASK) == QOY_OP_RUN_1)
        {
          pixelPack.Y[0] = pixelPack.Y[3];
          pixelPack.Y[1] = pixelPack.Y[3];
          pixelPack.Y[2] = pixelPack.Y[3];
        }
        else if ((b1 & QOY_OP_RUN_MASK) == QOY_OP_RUN_X)
        {
          pixelPack.Y[0] = pixelPack.Y[3];
          pixelPack.Y[1] = pixelPack.Y[3];
          pixelPack.Y[2] = pixelPack.Y[3];
          var b2 = input[readIndex++];
          run = b2 + 2 - 1;
        }
        else if (b1 == QOY_OP_SKIP)
        {
          pixelPack.Y[0] = input[readIndex++];
          pixelPack.Y[1] = input[readIndex++];
          pixelPack.Y[2] = input[readIndex++];
          pixelPack.Y[3] = input[readIndex++];
          pixelPack.Cb = input[readIndex++];
          pixelPack.Cr = input[readIndex++];
          skipPixels = (PixelSkipCount - 1);
        }
        else if ((b1 & QOY_OP_888_MASK) == QOY_OP_888)
        {
          pixelPack.Y[0] = input[readIndex++];
          pixelPack.Y[1] = input[readIndex++];
          pixelPack.Y[2] = input[readIndex++];
          pixelPack.Y[3] = input[readIndex++];
          pixelPack.Cb = input[readIndex++];
          pixelPack.Cr = input[readIndex++];
        }
        else if ((b1 & QOY_OP_321_MASK) == QOY_OP_321)
        {
          var b2 = input[readIndex++];
          pixelPack.Y[0] = (byte)(pixelPack.Y[3] + ((b1 >> 4)) - 4);
          pixelPack.Y[1] = (byte)(pixelPack.Y[0] + ((b1 >> 1) & 0x07) - 4);
          pixelPack.Y[2] = (byte)(pixelPack.Y[1] + ((b1 & 0x01) << 2) + (b2 >> 6) - 4);
          pixelPack.Y[3] = (byte)(pixelPack.Y[2] + ((b2 >> 3) & 0x07) - 4);
          pixelPack.Cb = (byte)(pixelPack.Cb + ((b2 >> 1) & 0x03) - 2);
          pixelPack.Cr = (byte)(pixelPack.Cr + (b2 & 0x01) - 1);
        }
        else if ((b1 & QOY_OP_433_MASK) == QOY_OP_433)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          pixelPack.Y[0] = (byte)(pixelPack.Y[3] + ((b1 >> 2) & 0x0F) - 8);
          pixelPack.Y[1] = (byte)(pixelPack.Y[0] + ((b1 & 0x03) << 2) + (b2 >> 6) - 8);
          pixelPack.Y[2] = (byte)(pixelPack.Y[1] + ((b2 >> 2) & 0x0F) - 8);
          pixelPack.Y[3] = (byte)(pixelPack.Y[2] + ((b2 & 0x03) << 2) + (b3 >> 6) - 8);
          pixelPack.Cb = (byte)(pixelPack.Cb + ((b3 >> 3) & 0x07) - 4);
          pixelPack.Cr = (byte)(pixelPack.Cr + (b3 & 0x07) - 4);
        }
        else if ((b1 & QOY_OP_554_MASK) == QOY_OP_554)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          pixelPack.Y[0] = (byte)(pixelPack.Y[3] + (b1 & 0x1F) - 16);
          pixelPack.Y[1] = (byte)(pixelPack.Y[0] + ((b2 >> 3) & 0x1F) - 16);
          pixelPack.Y[2] = (byte)(pixelPack.Y[1] + ((b2 & 0x07) << 2) + (b3 >> 6) - 16);
          pixelPack.Y[3] = (byte)(pixelPack.Y[2] + ((b3 >> 1) & 0x1F) - 16);
          pixelPack.Cb = (byte)(pixelPack.Cb + ((b3 & 0x01) << 4) + (b4 >> 4) - 16);
          pixelPack.Cr = (byte)(pixelPack.Cr + (b4 & 0x0F) - 8);
        }
        else if ((b1 & QOY_OP_666_MASK) == QOY_OP_666)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          var b5 = input[readIndex++];
          pixelPack.Y[0] = (byte)(pixelPack.Y[3] + ((b1 & 0x0F) << 2) + (b2 >> 6) - 32);
          pixelPack.Y[1] = (byte)(pixelPack.Y[0] + (b2 & 0x3F) - 32);
          pixelPack.Y[2] = (byte)(pixelPack.Y[1] + ((b3 >> 2) & 0x3F) - 32);
          pixelPack.Y[3] = (byte)(pixelPack.Y[2] + ((b3 & 0x03) << 4) + (b4 >> 4) - 32);
          pixelPack.Cb = (byte)(pixelPack.Cb + ((b4 & 0x0F) << 2) + (b5 >> 6) - 32);
          pixelPack.Cr = (byte)(pixelPack.Cr + (b5 & 0x3F) - 32);
        }
        else if ((b1 & QOY_OP_865_MASK) == QOY_OP_865)
        {
          var b2 = input[readIndex++];
          var b3 = input[readIndex++];
          var b4 = input[readIndex++];
          var b5 = input[readIndex++];
          var b6 = input[readIndex++];
          pixelPack.Y[0] = (byte)(pixelPack.Y[3] + ((b1 & 0x07) << 5) + (b2 >> 3) - 128);
          pixelPack.Y[1] = (byte)(pixelPack.Y[0] + ((b2 & 0x07) << 5) + (b3 >> 3) - 128);
          pixelPack.Y[2] = (byte)(pixelPack.Y[1] + ((b3 & 0x07) << 5) + (b4 >> 3) - 128);
          pixelPack.Y[3] = (byte)(pixelPack.Y[2] + ((b4 & 0x07) << 5) + (b5 >> 3) - 128);
          pixelPack.Cb = (byte)(pixelPack.Cb + ((b5 & 0x07) << 3) + (b6 >> 5) - 32);
          pixelPack.Cr = (byte)(pixelPack.Cr + (b6 & 0x1F) - 16);
        }
      }

      output[yIndex + 0] = pixelPack.Y[0];
      output[yIndex + 1] = pixelPack.Y[1];
      output[yIndex + 2] = pixelPack.Y[2];
      output[yIndex + 3] = pixelPack.Y[3];
      output[cbCrIndex + 0] = pixelPack.Cb;
      output[cbCrIndex + 1] = pixelPack.Cr;
      cbCrIndex += 2; // unlike yIndex this is not incremented by the main loop
    }
  }
}
