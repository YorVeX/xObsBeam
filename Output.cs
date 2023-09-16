// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using ObsInterop;

namespace xObsBeam;

public static class Output
{
  unsafe struct Context
  {
    public obs_data* Settings;
    public obs_output* Output;
  }

  static Context _outputData;
  static IntPtr _outputDataPointer;

  static unsafe video_output_info* _videoInfo = null;
  static unsafe audio_output_info* _audioInfo = null;
  static unsafe video_format _conversionVideoFormat = video_format.VIDEO_FORMAT_NONE;
  static ulong _videoFrameCycleCounter;
  static ulong _audioFrameCycleCounter;
  static readonly BeamSender _beamSender = new(Beam.SenderTypes.Output);
  static bool _firstFrame = true;

  #region Helper methods
  public static unsafe void Register()
  {
    var outputInfo = new obs_output_info();
    fixed (byte* id = "Beam Output"u8)
    {
      outputInfo.id = (sbyte*)id;
      outputInfo.flags = ObsOutput.OBS_OUTPUT_AV;
      outputInfo.get_name = &output_get_name;
      outputInfo.create = &output_create;
      outputInfo.destroy = &output_destroy;
      outputInfo.start = &output_start;
      outputInfo.stop = &output_stop;
      outputInfo.raw_video = &output_raw_video;
      outputInfo.raw_audio = &output_raw_audio;
      ObsOutput.obs_register_output_s(&outputInfo, (nuint)sizeof(obs_output_info));
    }
  }
  public static unsafe void Create()
  {
    fixed (byte* id = "Beam Output"u8)
      Obs.obs_output_create((sbyte*)id, (sbyte*)id, null, null);
  }

  public static unsafe bool IsReady => (_outputData.Output != null);

  public static unsafe bool IsActive => ((_outputData.Output != null) && Convert.ToBoolean(Obs.obs_output_active(_outputData.Output)));

  public static unsafe void Start()
  {
    if (!IsReady)
      return;
    else if (!IsActive)
    {
      Module.Log("Starting output...");

      // recreate output, otherwise OBS settings changes like resolution will lead to a crash upon output start
      Obs.obs_output_release(_outputData.Output);
      fixed (byte* id = "Beam Output"u8)
        Obs.obs_output_create((sbyte*)id, (sbyte*)id, null, null);

      Obs.obs_output_start(_outputData.Output);
      Module.Log("Output started.");
    }
    else
      Module.Log("Output not started, already running.");
  }
  public static unsafe void Stop()
  {
    if (IsActive)
    {
      Module.Log("Stopping output...");
      Obs.obs_output_stop(_outputData.Output);
      Module.Log("Output stopped.");
    }
    else
      Module.Log("Output not stopped, wasn't running.");
  }

  public static unsafe void Dispose()
  {
    Obs.obs_output_release(_outputData.Output);
  }
  #endregion Helper methods

  #region Output API methods
#pragma warning disable IDE1006
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* output_get_name(void* data)
  {
    Module.Log("output_get_name called", ObsLogLevel.Debug);
    fixed (byte* outputName = "Beam Sender Output"u8)
      return (sbyte*)outputName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* output_create(obs_data* settings, obs_output* output)
  {
    Module.Log("output_create called", ObsLogLevel.Debug);

    IntPtr context = Marshal.AllocCoTaskMem(sizeof(Context));
    Context* obsOutputDataPointer = (Context*)context;
    obsOutputDataPointer->Settings = settings;
    obsOutputDataPointer->Output = output;
    _outputDataPointer = context;
    _outputData = *obsOutputDataPointer;
    return (void*)context;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_destroy(void* data)
  {
    Module.Log("output_destroy called", ObsLogLevel.Debug);
    Marshal.FreeCoTaskMem(_outputDataPointer);
    _outputData.Output = null;
    ObsData.obs_data_release(_outputData.Settings);
    _outputData.Settings = null;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte output_start(void* data)
  {
    Module.Log("output_start called", ObsLogLevel.Debug);

    if (!Convert.ToBoolean(Obs.obs_output_can_begin_data_capture(_outputData.Output, ObsOutput.OBS_OUTPUT_AV)))
      return Convert.ToByte(false);

    _videoInfo = ObsVideo.video_output_get_info(Obs.obs_output_video(_outputData.Output));
    _firstFrame = true;

    // color format compatibility check
    var requiredVideoFormatConversion = SettingsDialog.Properties.GetRequiredVideoFormatConversion();
    video_scale_info* videoScaleInfo = null;
    if (requiredVideoFormatConversion != video_format.VIDEO_FORMAT_NONE)
    {
      // request conversion from OBS
      videoScaleInfo = ObsBmem.bzalloc<video_scale_info>();
      _conversionVideoFormat = requiredVideoFormatConversion;
      videoScaleInfo->format = _conversionVideoFormat;
      videoScaleInfo->range = _videoInfo->range; // needs to be explicitly set to leave it as it is, otherwise it will be converted to whatever video_range_type.VIDEO_RANGE_DEFAULT is
      videoScaleInfo->colorspace = _videoInfo->colorspace; // needs to be explicitly set to leave it as it is, otherwise it will be converted to whatever video_colorspace.VIDEO_CS_DEFAULT is

      Module.Log($"Setting video conversion to {videoScaleInfo->width}x{videoScaleInfo->height} {videoScaleInfo->format} {videoScaleInfo->colorspace} {videoScaleInfo->range}", ObsLogLevel.Debug);
      Obs.obs_output_set_video_conversion(_outputData.Output, videoScaleInfo);
    }

    Obs.obs_output_begin_data_capture(_outputData.Output, ObsOutput.OBS_OUTPUT_AV);

    if (videoScaleInfo != null)
      ObsBmem.bfree(videoScaleInfo);

    return Convert.ToByte(true);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_stop(void* data, ulong ts)
  {
    Module.Log("output_stop called", ObsLogLevel.Debug);
    Obs.obs_output_end_data_capture(_outputData.Output);
    _beamSender.Stop();
    _videoFrameCycleCounter = 0;
    _audioFrameCycleCounter = 0;
    _videoInfo = null;
    _audioInfo = null;
    _conversionVideoFormat = video_format.VIDEO_FORMAT_NONE;
  }

  private static void StartSenderIfPossible()
  {
    if (_beamSender.CanStart)
    {
      if (SettingsDialog.Properties.UsePipe)
        _beamSender.Start(SettingsDialog.Properties.Identifier, SettingsDialog.Properties.Identifier);
      else
        _beamSender.Start(SettingsDialog.Properties.Identifier, SettingsDialog.Properties.NetworkInterfaceAddress, SettingsDialog.Properties.Port, SettingsDialog.Properties.AutomaticPort);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_raw_video(void* data, video_data* frame)
  {
    if (_firstFrame) // this is the first frame since the last output (re)start, get video info
    {
      _firstFrame = false;
      video_scale_info* videoScaleInfo = Obs.obs_output_get_video_conversion(_outputData.Output);
      if (videoScaleInfo != null)
      {
        Module.Log($"Output video conversion in effect: {_videoInfo->width}x{_videoInfo->height} -> {videoScaleInfo->width}x{videoScaleInfo->height}, format: {_videoInfo->format} -> {videoScaleInfo->format}, colorspace: {_videoInfo->colorspace} -> {videoScaleInfo->colorspace}, range: {_videoInfo->range} -> {videoScaleInfo->range}", ObsLogLevel.Info);
        _videoInfo->colorspace = videoScaleInfo->colorspace;
        _videoInfo->width = videoScaleInfo->width;
        _videoInfo->height = videoScaleInfo->height;
        _videoInfo->range = videoScaleInfo->range;
        _conversionVideoFormat = videoScaleInfo->format;
        // don't set _videoInfo->format, otherwise the info about manual conversions like from NV12 to I420 for JPEG will be lost
      }

      var video = ObsBmem.bzalloc<obs_source_frame>(); // only using this to store the color_matrix, color_range_min and color_range_max fields
      ObsVideo.video_format_get_parameters_for_format(_videoInfo->colorspace, _videoInfo->range, _videoInfo->format, video->color_matrix, video->color_range_min, video->color_range_max);
      try
      {
        if (_beamSender.SetVideoParameters(SettingsDialog.Properties, _videoInfo->format, _conversionVideoFormat, _videoInfo->width, _videoInfo->height, _videoInfo->fps_num, _videoInfo->fps_den, Convert.ToByte(_videoInfo->range == video_range_type.VIDEO_RANGE_FULL), video->color_matrix, video->color_range_min, video->color_range_max, frame->linesize, frame->data))
          StartSenderIfPossible();
      }
      catch (Exception ex)
      {
        Module.Log($"output_raw_video(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
      ObsBmem.bfree(video);
    }

    _beamSender.SendVideo(frame->timestamp, frame->data.e0);

    _videoFrameCycleCounter++;
    if ((_videoFrameCycleCounter > 5) && (_videoFrameCycleCounter > (ulong)Math.Round((double)_videoInfo->fps_num / _videoInfo->fps_den))) // do this only roughly once per second
    {
      _videoFrameCycleCounter = 1;
      Module.Log("output_raw_video called, frame timestamp: " + frame->timestamp, ObsLogLevel.Debug);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_raw_audio(void* data, audio_data* frames)
  {
    if (_audioInfo == null) // this is the first frame since the last output (re)start, get audio info
    {
      _audioInfo = ObsAudio.audio_output_get_info(Obs.obs_output_audio(_outputData.Output));
      try
      {
        _beamSender.SetAudioParameters(_audioInfo->format, _audioInfo->speakers, _audioInfo->samples_per_sec, frames->frames);
        StartSenderIfPossible();
      }
      catch (Exception ex)
      {
        Module.Log($"output_raw_audio(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }

    _beamSender.SendAudio(frames->timestamp, frames->frames, frames->data.e0);
    _audioFrameCycleCounter++;
    if ((_audioFrameCycleCounter > 5) && (_audioFrameCycleCounter > Obs.obs_get_active_fps())) // do this only roughly once per second
    {
      _audioFrameCycleCounter = 1;
      Module.Log("output_raw_audio called, frame timestamp: " + frames->timestamp, ObsLogLevel.Debug);
    }
  }
#pragma warning restore IDE1006
  #endregion Output API methods

}
