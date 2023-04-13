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

  unsafe static video_output_info* _videoInfo = null;
  unsafe static audio_output_info* _audioInfo = null;
  static ulong _videoFrameCycleCounter = 0;
  static ulong _audioFrameCycleCounter = 0;
  static BeamSender _beamSender = new BeamSender();

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
      ObsOutput.obs_register_output_s(&outputInfo, (nuint)Marshal.SizeOf(outputInfo));
    }
  }
  public static unsafe void Create()
  {
    fixed (byte* id = "Beam Output"u8)
      Obs.obs_output_create((sbyte*)id, (sbyte*)id, null, null);
  }

  public static unsafe bool IsReady
  {
    get => (_outputData.Output != null);
  }

  public static unsafe bool IsActive
  {
    get => ((_outputData.Output != null) && Convert.ToBoolean(Obs.obs_output_active(_outputData.Output)));
  }

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
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* output_get_name(void* data)
  {
    Module.Log("output_get_name called", ObsLogLevel.Debug);
    var asciiBytes = "Beam Output"u8;
    fixed (byte* outputName = asciiBytes)
      return (sbyte*)outputName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* output_create(obs_data* settings, obs_output* output)
  {
    Module.Log("output_create called", ObsLogLevel.Debug);

    var context = new Context();
    IntPtr mem = Marshal.AllocCoTaskMem(Marshal.SizeOf(context));
    Context* obsOutputDataPointer = (Context*)mem;
    obsOutputDataPointer->Settings = settings;
    obsOutputDataPointer->Output = output;
    _outputDataPointer = mem;
    _outputData = *obsOutputDataPointer;
    return (void*)mem;
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
    
    // QOI can only work with BGRA format, force this on the output if QOI is enabled
    //FIXME: this doesn't work, apparently the BGRA output we get from obs_output_get_video_conversion() differs from the one that we get when globally setting the output format to BGRA
    // leads to a flickering feed of a frozen frame mixed with full green frames
    /*
    if (SettingsDialog.QoiCompression)
    {
      video_scale_info* videoScaleInfo = ObsBmem.bzalloc<video_scale_info>();
      videoScaleInfo->format = video_format.VIDEO_FORMAT_BGRA;
      Module.Log($"Setting video conversion to {videoScaleInfo->width}x{videoScaleInfo->height} {videoScaleInfo->format} {videoScaleInfo->colorspace} {videoScaleInfo->range}", ObsLogLevel.Debug);
      Obs.obs_output_set_video_conversion(_outputData.Output, videoScaleInfo);
    }
    */

    Obs.obs_output_begin_data_capture(_outputData.Output, ObsOutput.OBS_OUTPUT_AV);

    return Convert.ToByte(true);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_stop(void* data, ulong ts)
  {
    //BUG: crashes sometimes when a client is connected and the output is stopped, it doesn't matter whether QOI is enabled or not, also if BeamSender.Stop() isn't called it still crashes
    Module.Log("output_stop called", ObsLogLevel.Debug);
    Obs.obs_output_end_data_capture(_outputData.Output);
    _beamSender.Stop();
    _videoFrameCycleCounter = 0;
    _audioFrameCycleCounter = 0;
    _videoInfo = null;
    _audioInfo = null;
  }

  private static void startSenderIfPossible()
  {
    if (_beamSender.CanStart)
    {
      if (SettingsDialog.UsePipe)
        _beamSender.Start(SettingsDialog.Identifier, SettingsDialog.Identifier);
      else
        _beamSender.Start(SettingsDialog.Identifier, SettingsDialog.NetworkInterfaceAddress, SettingsDialog.Port);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void output_raw_video(void* data, video_data* frame)
  {
    if (_videoInfo == null) // this is the first frame since the last output (re)start, get video info
    {
      _videoInfo = ObsVideo.video_output_get_info(Obs.obs_output_video(_outputData.Output));
      // video_scale_info* videoScaleInfo = Obs.obs_output_get_video_conversion(_outputData.Output);
      // the correct behavior would be: if videoScaleInfo is not null (meaning conversion is active), then fields in videoScaleInfo override the general settings in _videoInfo and these should be considered
      // but obs_output_get_video_conversion() was only added in OBS 29.1.X (in beta at time of writing this on April 9th, 2023), so we can't use it yet
      // the good news is that in the case of this plugin it doesn't matter too much, since it's our own output we know which settings we changed
      try
      {
        if (SettingsDialog.QoiCompression)
        {
          //FIXME: this doesn't work, apparently the BGRA output we get from obs_output_get_video_conversion() differs from the one that we get when globally setting the output format to BGRA
          // _videoInfo->format = video_format.VIDEO_FORMAT_BGRA;

          // instead we just warn the user about it
          if (_videoInfo->format != video_format.VIDEO_FORMAT_BGRA)
            Module.Log(Module.ObsTextString("CompressionQOINoBGRAWarningText"), ObsLogLevel.Warning);
        }
        _beamSender.SetVideoParameters(_videoInfo, frame->linesize);
        startSenderIfPossible();
      }
      catch (Exception ex)
      {
        Module.Log($"output_raw_video(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    _beamSender.SendVideo(frame->timestamp, frame->data.e0);

    _videoFrameCycleCounter++;
    if ((_videoFrameCycleCounter > 5) && (_videoFrameCycleCounter > _videoInfo->fps_num)) // do this only roughly once per second
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
      _audioInfo = ObsAudio.audio_output_get_info(Obs.obs_get_audio());
      try
      {
        _beamSender.SetAudioParameters(_audioInfo, frames->frames);
        startSenderIfPossible();
      }
      catch (Exception ex)
      {
        Module.Log($"output_raw_audio(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    _beamSender.SendAudio(frames->timestamp, (int)_audioInfo->speakers, frames->data.e0);
    _audioFrameCycleCounter++;
    if ((_audioFrameCycleCounter > 5) && (_audioFrameCycleCounter > Obs.obs_get_active_fps())) // do this only roughly once per second
    {
      _audioFrameCycleCounter = 1;
      Module.Log("output_raw_audio called, frame timestamp: " + frames->timestamp, ObsLogLevel.Debug);
    }

  }
  #endregion Output API methods


}