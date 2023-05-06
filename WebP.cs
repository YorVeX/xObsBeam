// SPDX-FileCopyrightText: Copyright (c) 2020 Jose M. Piñeiro
// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT
// This is a modified version of: Wrapper for WebP format in C#. (MIT) Jose M. Piñeiro (https://github.com/JosePineiro/WebP-wrapper/blob/8f38e32be564a1f4af16c9465943e78aaa0ef51b/WebPTest/WebPWrapper.cs)

using System.Runtime.InteropServices;
using System.Security;

namespace xObsBeam
{
  public static class WebP
  {
    private const int WEBP_MAX_DIMENSION = 16383;
    #region | Public Decode Functions |

    //TODO: Linux support

    private static readonly bool _isAvailable = false;
    private static readonly uint _version = 0;
    private static readonly string _versionString = "";

    public static bool IsAvailable { get => _isAvailable; }
    public static string VersionString { get => _versionString; }
    public static uint Version { get => _version; }

    static WebP()
    {
      try
      {
        _version = (uint)WebPNativeMethods.WebPGetDecoderVersion();
        var revision = _version % 256;
        var minor = (_version >> 8) % 256;
        var major = (_version >> 16) % 256;
        _versionString = major + "." + minor + "." + revision;
        //TODO: change this as soon as the other TODO was done and newer versions are supported
        _isAvailable = (_versionString == "1.2.1");
        if (_isAvailable)
          WebPNativeMethods.OnCallback = new WebPNativeMethods.WebPMemoryWrite(MyWriter);
      }
      catch (DllNotFoundException ex)
      {
        Module.Log(ex.GetType().Name + " while trying to load libwebp: " + ex.Message, ObsLogLevel.Debug);
      }
      catch (Exception ex)
      {
        Module.Log(ex.GetType().Name + " while trying to load libwebp: " + ex.Message, ObsLogLevel.Error);
      }
    }

    public static unsafe void Decode(byte[] input, int inSize, byte[] output, int outSize, int stride)
    {
      // prefer the "into" variant so that we have the memory allocation under our own control
      fixed (byte* ptrData = input, targetData = output)
        WebPNativeMethods.WebPDecodeBGRAInto((IntPtr)ptrData, inSize, (IntPtr)targetData, outSize, stride);
    }

    /// <summary>Lossless encoding image in bitmap (Advanced encoding API)</summary>
    /// <param name="data">Raw image data.</param>
    /// <param name="speed">Between 0 (fastest, lowest compression) and 9 (slower, best compression)</param>
    /// <returns>Compressed data</returns>
    public static unsafe int EncodeLossless(byte* data, int speed, int width, int height, int channels, byte[] output)
    {
      //Initialize configuration structure
      WebPConfig config = new WebPConfig();

      //Set compression parameters
      if (WebPNativeMethods.WebPConfigInitInternal(ref config, WebPPreset.WEBP_PRESET_DEFAULT, (speed + 1) * 10, WebPNativeMethods.WEBP_DECODER_ABI_VERSION) == 0)
        throw new Exception("WebP encoder config initialization failed.");

      if (WebPNativeMethods.WebPConfigLosslessPreset(ref config, speed) == 0)
        throw new Exception("Configuring lossless preset failed.");
      config.pass = speed + 1;
      config.thread_level = 1;
      config.use_sharp_yuv = 0;
      config.alpha_filtering = 0;
      config.exact = 0;

      return AdvancedEncode(data, config, width, height, channels, output);
    }

    /// <summary>Near lossless encoding image in bitmap</summary>
    /// <param name="data">Raw image data.</param>
    /// <param name="quality">Between 0 (lower quality, lowest file size) and 100 (highest quality, higher file size)</param>
    /// <param name="speed">Between 0 (fastest, lowest compression) and 9 (slower, best compression)</param>
    /// <returns>Compress data</returns>
    public static unsafe int EncodeNearLossless(byte* data, int quality, int speed, int width, int height, int channels, byte[] output)
    {
      //Inicialize config struct
      WebPConfig config = new WebPConfig();

      //Set compression parameters
      if (WebPNativeMethods.WebPConfigInitInternal(ref config, WebPPreset.WEBP_PRESET_DEFAULT, (speed + 1) * 10, WebPNativeMethods.WEBP_DECODER_ABI_VERSION) == 0)
        throw new Exception("WebP encoder config initialization failed.");
      if (WebPNativeMethods.WebPConfigLosslessPreset(ref config, speed) == 0)
        throw new Exception("Configuring lossless preset failed.");
      config.pass = speed + 1;
      config.near_lossless = quality;
      config.thread_level = 1;
      config.alpha_filtering = 1;
      config.use_sharp_yuv = 0;
      config.exact = 0;

      return AdvancedEncode(data, config, width, height, channels, output);
    }
    #endregion

    #region | Other Public Functions |
    /// <summary>Get the libwebp version</summary>
    /// <returns>Version of library</returns>
    public static string GetVersion()
    {
      try
      {
        uint v = (uint)WebPNativeMethods.WebPGetDecoderVersion();
        var revision = v % 256;
        var minor = (v >> 8) % 256;
        var major = (v >> 16) % 256;
        return major + "." + minor + "." + revision;
      }
      catch (Exception ex) { throw new Exception(ex.Message + "\r\nIn WebP.GetVersion"); }
    }
    #endregion

    #region | Private Methods |
    /// <summary>Encoding image  using Advanced encoding API</summary>
    /// <param name="data">Raw image data.</param>
    /// <param name="config">Configuration for encode</param>
    /// <param name="width">Width of image</param>
    /// <param name="height">Height of image</param>
    /// <param name="channels">Number of channels</param>
    /// <param name="output">Output data buffer</param>
    /// <returns>Compressed data</returns>
    private static unsafe int AdvancedEncode(byte* data, WebPConfig config, int width, int height, int channels, byte[] output)
    {
      WebPPicture webPPicture = new WebPPicture();
      try
      {
        if (WebPNativeMethods.WebPValidateConfig(ref config) != 1)
          throw new Exception("Invalid WebP configuration.");
        if (WebPNativeMethods.WebPPictureInitInternal(ref webPPicture, WebPNativeMethods.WEBP_DECODER_ABI_VERSION) != 1)
          throw new Exception("WebP initialization failed.");
        webPPicture.use_argb = 1;
        webPPicture.width = width;
        webPPicture.height = height;

        if (channels == 4)
        {
          int result = WebPNativeMethods.WebPPictureImportBGRA(ref webPPicture, (IntPtr)data, width * channels);
          if (result != 1)
            throw new Exception("BGRA colorspace conversion failed.");
          webPPicture.colorspace = (uint)WEBP_CSP_MODE.MODE_bgrA;
        }
        else
        {
          int result = WebPNativeMethods.WebPPictureImportBGR(ref webPPicture, (IntPtr)data, width * channels);
          if (result != 1)
            throw new Exception("BGR colorspace conversion failed.");
        }

        fixed (byte* ptr = output)
        {
          webPPicture.custom_ptr = (IntPtr)ptr;

          //Set up a byte-writing method (write-to-memory, in this case)
          webPPicture.writer = Marshal.GetFunctionPointerForDelegate(WebPNativeMethods.OnCallback!);

          //TODO: support libwebp versions newer than 1.2.1 (i.e. 1.2.4 and 1.3.0) - currently for these versions the WebPEncode call fails with VP8_ENC_ERROR_INVALID_CONFIGURATION - from looking at the source code this means a WebPValidateConfig call failed, which is weird, because a few lines up we did the same and it succeeded
          //compress the input samples
          if (WebPNativeMethods.WebPEncode(ref config, ref webPPicture) != 1)
            throw new Exception("Encoding error: " + ((WebPEncodingError)webPPicture.error_code));

          // size is detected from how much the pointer was advanced
          int size = (int)((long)webPPicture.custom_ptr - (long)ptr);
          return size;
        }
      }
      finally
      {
        if (webPPicture.argb != IntPtr.Zero)
          WebPNativeMethods.WebPPictureFree(ref webPPicture);
      }
    }

    private static unsafe int MyWriter([InAttribute()] IntPtr data, int data_size, ref WebPPicture picture)
    {
      new ReadOnlySpan<byte>(data.ToPointer(), (int)data_size).CopyTo(new Span<byte>(picture.custom_ptr.ToPointer(), (int)data_size));
      picture.custom_ptr = IntPtr.Add(picture.custom_ptr, (int)data_size);
      return 1;
    }
    #endregion
  }

  #region | Import libwebp functions |
  [SuppressUnmanagedCodeSecurityAttribute]
  internal static class WebPNativeMethods
  {

    public static readonly int WEBP_DECODER_ABI_VERSION = 0x0208;

    /// <summary>This function will initialize the configuration according to a predefined set of parameters (referred to by 'preset') and a given quality factor</summary>
    /// <param name="config">The WebPConfig structure</param>
    /// <param name="preset">Type of image</param>
    /// <param name="quality">Quality of compression</param>
    /// <returns>0 if error</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPConfigInitInternal")]
    internal static extern int WebPConfigInitInternal(ref WebPConfig config, WebPPreset preset, float quality, int WEBP_DECODER_ABI_VERSION);

    /// <summary>Activate the lossless compression mode with the desired efficiency</summary>
    /// <param name="config">The WebPConfig struct</param>
    /// <param name="level">between 0 (fastest, lowest compression) and 9 (slower, best compression)</param>
    /// <returns>0 in case of parameter error</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPConfigLosslessPreset")]
    internal static extern int WebPConfigLosslessPreset(ref WebPConfig config, int level);

    /// <summary>Check that configuration is non-NULL and all configuration parameters are within their valid ranges</summary>
    /// <param name="config">The WebPConfig structure</param>
    /// <returns>1 if configuration is OK</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPValidateConfig")]
    internal static extern int WebPValidateConfig(ref WebPConfig config);

    /// <summary>Initialize the WebPPicture structure checking the DLL version</summary>
    /// <param name="wpic">The WebPPicture structure</param>
    /// <returns>1 if not error</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPPictureInitInternal")]
    internal static extern int WebPPictureInitInternal(ref WebPPicture wpic, int WEBP_DECODER_ABI_VERSION);

    /// <summary>Colorspace conversion function to import RGB samples</summary>
    /// <param name="wpic">The WebPPicture structure</param>
    /// <param name="bgr">Point to BGR data</param>
    /// <param name="stride">stride of BGR data</param>
    /// <returns>Returns 0 in case of memory error.</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPPictureImportBGR")]
    internal static extern int WebPPictureImportBGR(ref WebPPicture wpic, IntPtr bgr, int stride);

    /// <summary>Color-space conversion function to import RGB samples</summary>
    /// <param name="wpic">The WebPPicture structure</param>
    /// <param name="bgra">Point to BGRA data</param>
    /// <param name="stride">stride of BGRA data</param>
    /// <returns>Returns 0 in case of memory error.</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPPictureImportBGRA")]
    internal static extern int WebPPictureImportBGRA(ref WebPPicture wpic, IntPtr bgra, int stride);

    /// <summary>The writer type for output compress data</summary>
    /// <param name="data">Data returned</param>
    /// <param name="data_size">Size of data returned</param>
    /// <param name="wpic">Picture structure</param>
    /// <returns></returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WebPMemoryWrite([In()] IntPtr data, int data_size, ref WebPPicture wpic);
    internal static WebPMemoryWrite? OnCallback;

    /// <summary>Compress to WebP format</summary>
    /// <param name="config">The configuration structure for compression parameters</param>
    /// <param name="picture">'picture' hold the source samples in both YUV(A) or ARGB input</param>
    /// <returns>Returns 0 in case of error, 1 otherwise. In case of error, picture->error_code is updated accordingly.</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPEncode")]
    internal static extern int WebPEncode(ref WebPConfig config, ref WebPPicture picture);

    /// <summary>Release the memory allocated by WebPPictureAlloc() or WebPPictureImport*()
    /// Note that this function does _not_ free the memory used by the 'picture' object itself.
    /// Besides memory (which is reclaimed) all other fields of 'picture' are preserved</summary>
    /// <param name="picture">Picture structure</param>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPPictureFree")]
    internal static extern void WebPPictureFree(ref WebPPicture wpic);

    /// <summary>Decode WEBP image pointed to by *data and returns BGRA samples into a preallocated buffer</summary>
    /// <param name="data">Pointer to WebP image data</param>
    /// <param name="data_size">This is the size of the memory block pointed to by data containing the image data</param>
    /// <param name="output_buffer">Pointer to decoded WebP image</param>
    /// <param name="output_buffer_size">Size of allocated buffer</param>
    /// <param name="output_stride">Specifies the distance between scan lines</param>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPDecodeBGRAInto")]
    internal static extern IntPtr WebPDecodeBGRAInto([InAttribute()] IntPtr data, int data_size, IntPtr output_buffer, int output_buffer_size, int output_stride);

    /// <summary>Get the WebP version library</summary>
    /// <returns>8bits for each of major/minor/revision packet in integer. E.g: v2.5.7 is 0x020507</returns>
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPGetDecoderVersion")]
    internal static extern int WebPGetDecoderVersion();
  }
  #endregion

  #region | Predefined |
  /// <summary>Enumerate some predefined settings for WebPConfig, depending on the type of source picture. These presets are used when calling WebPConfigPreset()</summary>
  internal enum WebPPreset
  {
    /// <summary>Default preset</summary>
    WEBP_PRESET_DEFAULT = 0,
    /// <summary>Digital picture, like portrait, inner shot</summary>
    WEBP_PRESET_PICTURE,
    /// <summary>Outdoor photograph, with natural lighting</summary>
    WEBP_PRESET_PHOTO,
    /// <summary>Hand or line drawing, with high-contrast details</summary>
    WEBP_PRESET_DRAWING,
    /// <summary>Small-sized colorful images</summary>
    WEBP_PRESET_ICON,
    /// <summary>Text-like</summary>
    WEBP_PRESET_TEXT
  };

  /// <summary>Encoding error conditions</summary>
  internal enum WebPEncodingError
  {
    /// <summary>No error</summary>
    VP8_ENC_OK = 0,
    /// <summary>Memory error allocating objects</summary>
    VP8_ENC_ERROR_OUT_OF_MEMORY,
    /// <summary>Memory error while flushing bits</summary>
    VP8_ENC_ERROR_BITSTREAM_OUT_OF_MEMORY,
    /// <summary>A pointer parameter is NULL</summary>
    VP8_ENC_ERROR_NULL_PARAMETER,
    /// <summary>Configuration is invalid</summary>
    VP8_ENC_ERROR_INVALID_CONFIGURATION,
    /// <summary>Picture has invalid width/height</summary>
    VP8_ENC_ERROR_BAD_DIMENSION,
    /// <summary>Partition is bigger than 512k</summary>
    VP8_ENC_ERROR_PARTITION0_OVERFLOW,
    /// <summary>Partition is bigger than 16M</summary>
    VP8_ENC_ERROR_PARTITION_OVERFLOW,
    /// <summary>Error while flushing bytes</summary>
    VP8_ENC_ERROR_BAD_WRITE,
    /// <summary>File is bigger than 4G</summary>
    VP8_ENC_ERROR_FILE_TOO_BIG,
    /// <summary>Abort request by user</summary>
    VP8_ENC_ERROR_USER_ABORT,
    /// <summary>List terminator. Always last</summary>
    VP8_ENC_ERROR_LAST,
  }

  /// <summary>Enumeration of the status codes</summary>
  internal enum VP8StatusCode
  {
    /// <summary>No error</summary>
    VP8_STATUS_OK = 0,
    /// <summary>Memory error allocating objects</summary>
    VP8_STATUS_OUT_OF_MEMORY,
    /// <summary>Configuration is invalid</summary>
    VP8_STATUS_INVALID_PARAM,
    VP8_STATUS_BITSTREAM_ERROR,
    /// <summary>Configuration is invalid</summary>
    VP8_STATUS_UNSUPPORTED_FEATURE,
    VP8_STATUS_SUSPENDED,
    /// <summary>Abort request by user</summary>
    VP8_STATUS_USER_ABORT,
    VP8_STATUS_NOT_ENOUGH_DATA,
  }

  /// <summary>Image characteristics hint for the underlying encoder</summary>
  internal enum WebPImageHint
  {
    /// <summary>Default preset</summary>
    WEBP_HINT_DEFAULT = 0,
    /// <summary>Digital picture, like portrait, inner shot</summary>
    WEBP_HINT_PICTURE,
    /// <summary>Outdoor photograph, with natural lighting</summary>
    WEBP_HINT_PHOTO,
    /// <summary>Discrete tone image (graph, map-tile etc)</summary>
    WEBP_HINT_GRAPH,
    /// <summary>List terminator. Always last</summary>
    WEBP_HINT_LAST
  };

  /// <summary>Describes the byte-ordering of packed samples in memory</summary>
  internal enum WEBP_CSP_MODE
  {
    /// <summary>Byte-order: R,G,B,R,G,B,..</summary>
    MODE_RGB = 0,
    /// <summary>Byte-order: R,G,B,A,R,G,B,A,..</summary>
    MODE_RGBA = 1,
    /// <summary>Byte-order: B,G,R,B,G,R,..</summary>
    MODE_BGR = 2,
    /// <summary>Byte-order: B,G,R,A,B,G,R,A,..</summary>
    MODE_BGRA = 3,
    /// <summary>Byte-order: A,R,G,B,A,R,G,B,..</summary>
    MODE_ARGB = 4,
    /// <summary>Byte-order: RGB-565: [a4 a3 a2 a1 a0 r5 r4 r3], [r2 r1 r0 g4 g3 g2 g1 g0], ...
    /// WEBP_SWAP_16BITS_CSP is defined, 
    /// Byte-order: RGB-565: [a4 a3 a2 a1 a0 b5 b4 b3], [b2 b1 b0 g4 g3 g2 g1 g0], ..</summary>
    MODE_RGBA_4444 = 5,
    /// <summary>Byte-order: RGB-565: [r4 r3 r2 r1 r0 g5 g4 g3], [g2 g1 g0 b4 b3 b2 b1 b0], ...
    /// WEBP_SWAP_16BITS_CSP is defined, 
    /// Byte-order: [b3 b2 b1 b0 a3 a2 a1 a0], [r3 r2 r1 r0 g3 g2 g1 g0], ..</summary>
    MODE_RGB_565 = 6,
    /// <summary>RGB-premultiplied transparent modes (alpha value is preserved)</summary>
    MODE_rgbA = 7,
    /// <summary>RGB-premultiplied transparent modes (alpha value is preserved)</summary>
    MODE_bgrA = 8,
    /// <summary>RGB-premultiplied transparent modes (alpha value is preserved)</summary>
    MODE_Argb = 9,
    /// <summary>RGB-premultiplied transparent modes (alpha value is preserved)</summary>
    MODE_rgbA_4444 = 10,
    /// <summary>YUV 4:2:0</summary>
    MODE_YUV = 11,
    /// <summary>YUV 4:2:0</summary>
    MODE_YUVA = 12,
    /// <summary>MODE_LAST -> 13</summary>
    MODE_LAST = 13,
  }

  #endregion

  #region | libwebp structs |
  /// <summary>Features gathered from the bit stream</summary>
  [StructLayoutAttribute(LayoutKind.Sequential)]
  internal struct WebPBitstreamFeatures
  {
    /// <summary>Width in pixels, as read from the bit stream</summary>
    public int Width;
    /// <summary>Height in pixels, as read from the bit stream</summary>
    public int Height;
    /// <summary>True if the bit stream contains an alpha channel</summary>
    public int Has_alpha;
    /// <summary>True if the bit stream is an animation</summary>
    public int Has_animation;
    /// <summary>0 = undefined (/mixed), 1 = lossy, 2 = lossless</summary>
    public int Format;
    /// <summary>Padding for later use</summary>
    [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 5, ArraySubType = UnmanagedType.U4)]
    private readonly uint[] pad;
  };

  /// <summary>Compression parameters</summary>
  [StructLayoutAttribute(LayoutKind.Sequential)]
  internal struct WebPConfig
  {
    /// <summary>Lossless encoding (0=lossy(default), 1=lossless)</summary>
    public int lossless;
    /// <summary>Between 0 (smallest file) and 100 (biggest)</summary>
    public float quality;
    /// <summary>Quality/speed trade-off (0=fast, 6=slower-better)</summary>
    public int method;
    /// <summary>Hint for image type (lossless only for now)</summary>
    public WebPImageHint image_hint;
    /// <summary>If non-zero, set the desired target size in bytes. Takes precedence over the 'compression' parameter</summary>
    public int target_size;
    /// <summary>If non-zero, specifies the minimal distortion to try to achieve. Takes precedence over target_size</summary>
    public float target_PSNR;
    /// <summary>Maximum number of segments to use, in [1..4]</summary>
    public int segments;
    /// <summary>Spatial Noise Shaping. 0=off, 100=maximum</summary>
    public int sns_strength;
    /// <summary>Range: [0 = off .. 100 = strongest]</summary>
    public int filter_strength;
    /// <summary>Range: [0 = off .. 7 = least sharp]</summary>
    public int filter_sharpness;
    /// <summary>Filtering type: 0 = simple, 1 = strong (only used if filter_strength > 0 or auto-filter > 0)</summary>
    public int filter_type;
    /// <summary>Auto adjust filter's strength [0 = off, 1 = on]</summary>
    public int autofilter;
    /// <summary>Algorithm for encoding the alpha plane (0 = none, 1 = compressed with WebP lossless). Default is 1</summary>
    public int alpha_compression;
    /// <summary>Predictive filtering method for alpha plane. 0: none, 1: fast, 2: best. Default if 1</summary>
    public int alpha_filtering;
    /// <summary>Between 0 (smallest size) and 100 (lossless). Default is 100</summary>
    public int alpha_quality;
    /// <summary>Number of entropy-analysis passes (in [1..10])</summary>
    public int pass;
    /// <summary>If true, export the compressed picture back. In-loop filtering is not applied</summary>
    public int show_compressed;
    /// <summary>Preprocessing filter (0=none, 1=segment-smooth, 2=pseudo-random dithering)</summary>
    public int preprocessing;
    /// <summary>Log2(number of token partitions) in [0..3] Default is set to 0 for easier progressive decoding</summary>
    public int partitions;
    /// <summary>Quality degradation allowed to fit the 512k limit on prediction modes coding (0: no degradation, 100: maximum possible degradation)</summary>
    public int partition_limit;
    /// <summary>If true, compression parameters will be remapped to better match the expected output size from JPEG compression. Generally, the output size will be similar but the degradation will be lower</summary>
    public int emulate_jpeg_size;
    /// <summary>If non-zero, try and use multi-threaded encoding</summary>
    public int thread_level;
    /// <summary>If set, reduce memory usage (but increase CPU use)</summary>
    public int low_memory;
    /// <summary>Near lossless encoding [0 = max loss .. 100 = off (default)]</summary>
    public int near_lossless;
    /// <summary>If non-zero, preserve the exact RGB values under transparent area. Otherwise, discard this invisible RGB information for better compression. The default value is 0</summary>
    public int exact;
    /// <summary>Reserved for future lossless feature</summary>
    public int delta_palettization;
    /// <summary>If needed, use sharp (and slow) RGB->YUV conversion</summary>
    public int use_sharp_yuv;
    /// <summary>Padding for later use</summary>
    private readonly int pad1;
    private readonly int pad2;
  };

  /// <summary>Main exchange structure (input samples, output bytes, statistics)</summary>
  [StructLayoutAttribute(LayoutKind.Sequential)]
  internal struct WebPPicture
  {
    /// <summary>Main flag for encoder selecting between ARGB or YUV input. Recommended to use ARGB input (*argb, argb_stride) for lossless, and YUV input (*y, *u, *v, etc.) for lossy</summary>
    public int use_argb;
    /// <summary>Color-space: should be YUV420 for now (=Y'CbCr). Value = 0</summary>
    public UInt32 colorspace;
    /// <summary>Width of picture (less or equal to WEBP_MAX_DIMENSION)</summary>
    public int width;
    /// <summary>Height of picture (less or equal to WEBP_MAX_DIMENSION)</summary>
    public int height;
    /// <summary>Pointer to luma plane</summary>
    public IntPtr y;
    /// <summary>Pointer to chroma U plane</summary>
    public IntPtr u;
    /// <summary>Pointer to chroma V plane</summary>
    public IntPtr v;
    /// <summary>Luma stride</summary>
    public int y_stride;
    /// <summary>Chroma stride</summary>
    public int uv_stride;
    /// <summary>Pointer to the alpha plane</summary>
    public IntPtr a;
    /// <summary>stride of the alpha plane</summary>
    public int a_stride;
    /// <summary>Padding for later use</summary>
    [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.U4)]
    private readonly uint[] pad1;
    /// <summary>Pointer to ARGB (32 bit) plane</summary>
    public IntPtr argb;
    /// <summary>This is stride in pixels units, not bytes</summary>
    public int argb_stride;
    /// <summary>Padding for later use</summary>
    [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.U4)]
    private readonly uint[] pad2;
    /// <summary>Byte-emission hook, to store compressed bytes as they are ready</summary>
    public IntPtr writer;
    /// <summary>Can be used by the writer</summary>
    public IntPtr custom_ptr;
    // map for extra information (only for lossy compression mode)
    /// <summary>1: intra type, 2: segment, 3: quant, 4: intra-16 prediction mode, 5: chroma prediction mode, 6: bit cost, 7: distortion</summary>
    public int extra_info_type;
    /// <summary>If not NULL, points to an array of size ((width + 15) / 16) * ((height + 15) / 16) that will be filled with a macroblock map, depending on extra_info_type</summary>
    public IntPtr extra_info;
    /// <summary>Pointer to side statistics (updated only if not NULL)</summary>
    public IntPtr stats;
    /// <summary>Error code for the latest error encountered during encoding</summary>
    public UInt32 error_code;
    /// <summary>If not NULL, report progress during encoding</summary>
    public IntPtr progress_hook;
    /// <summary>This field is free to be set to any value and used during callbacks (like progress-report e.g.)</summary>
    public IntPtr user_data;
    /// <summary>Padding for later use</summary>
    [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 13, ArraySubType = UnmanagedType.U4)]
    private readonly uint[] pad3;
    /// <summary>Row chunk of memory for YUVA planes</summary>
    private readonly IntPtr memory_;
    /// <summary>Row chunk of memory for ARGB planes</summary>
    private readonly IntPtr memory_argb_;
    /// <summary>Padding for later use</summary>
    [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.U4)]
    private readonly uint[] pad4;
  };


  #endregion
}