// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ObsInterop;

namespace xObsBeam;

public class Filter
{
  public unsafe struct Context
  {
    public uint FilterId;
    public obs_data* Settings;
    public obs_source* Source;
    public obs_source* ParentSource;
  }

  #region Class fields
  static uint _filterCount;
  static readonly ConcurrentDictionary<uint, Filter> _filterList = new();
  #endregion Class fields

  #region Instance fields
  unsafe Context* ContextPointer;
  unsafe uint FilterId => ContextPointer->FilterId;
  private string _parentSourceName = "";
  public BeamSenderProperties Properties { get; private set; }
  public bool IsEnabled { get; private set; }
  public bool IsActive { get; private set; }
  bool _isFirstVideoFrame = true;
  bool _isFirstAudioFrame = true;
  readonly BeamSender _beamSender = new();
  DateTime _lastFrame;
  #endregion Instance fields

  public unsafe Filter(Context* contextPointer)
  {
    Module.Log("Filter constructor", ObsLogLevel.Debug);
    ContextPointer = contextPointer;
    Properties = new BeamSenderProperties(Beam.SenderTypes.FilterAudioVideo, this, ContextPointer->Source, ContextPointer->Settings);
    IsEnabled = Properties.Enabled;
  }

  public unsafe void Dispose()
  {
    Properties.Dispose();
    Marshal.FreeHGlobal((IntPtr)ContextPointer);
  }

  #region Instance methods

  public unsafe string UniquePrefix
  {
    get
    {
      if (string.IsNullOrEmpty(_parentSourceName))
        return "<" + ContextPointer->FilterId + ">";
      return "<" + ContextPointer->FilterId + "/" + _parentSourceName + ">";
    }
  }

  public void Enable()
  {
    Module.Log($"{UniquePrefix} Filter Enable(): IsEnabled={IsEnabled}, IsActive={IsActive}, CanStart={_beamSender.CanStart}", ObsLogLevel.Debug);
    IsEnabled = true;
  }

  public void Disable()
  {
    Module.Log($"{UniquePrefix} Filter Disable(): IsEnabled={IsEnabled}, IsActive={IsActive}, CanStart={_beamSender.CanStart}", ObsLogLevel.Debug);
    IsEnabled = false;
    StopSender();
  }

  private void StartSenderIfPossible()
  {
    Module.Log($"{UniquePrefix} Filter StartSenderIfPossible(): IsEnabled={IsEnabled}, IsActive={IsActive}, CanStart={_beamSender.CanStart}", ObsLogLevel.Debug);
    if (_beamSender.CanStart)
    {
      if (Properties.UsePipe)
        _beamSender.Start(Beam.SenderTypes.FilterAudioVideo, Properties.Identifier, Properties.Identifier);
      else
        _beamSender.Start(Beam.SenderTypes.FilterAudioVideo, Properties.Identifier, Properties.NetworkInterfaceAddress, Properties.Port, Properties.AutomaticPort);
      IsActive = true;
    }
  }

  private void StopSender()
  {
    Module.Log($"{UniquePrefix} Filter StopSender(): IsEnabled={IsEnabled}, IsActive={IsActive}, CanStart={_beamSender.CanStart}", ObsLogLevel.Debug);
    if (IsActive)
    {
      _beamSender.Stop();
      IsActive = false;
      _isFirstVideoFrame = true;
      _isFirstAudioFrame = true;
    }
  }

  private unsafe void ProcessTick(float seconds)
  {
    if (IsActive && (DateTime.UtcNow.Subtract(_lastFrame).TotalMilliseconds >= 500) && (seconds < 0.5f)) // the (seconds < 0.5f) check makes sure OBS wasn't just lagging for a short time
    {
      Module.Log($"{UniquePrefix} Filter has not received frame data for a while, stopping sender.", ObsLogLevel.Info);
      StopSender();
    }
  }

  private unsafe void ProcessVideo(void* data, obs_source_frame* frame)
  {
    if (!IsEnabled)
      return;

    _lastFrame = DateTime.UtcNow;
    if (_isFirstVideoFrame) // this is the first frame since the last output (re)start, get video info
    {
      _isFirstVideoFrame = false;

      // identify and remember the parent source (obs_filter_get_parent is only valid during specific callbacks, so we need to do this here)
      if (ContextPointer->ParentSource == null)
      {
        //TODO: move this initialization to filter_add as soon as an OBS (probably the next after 29.1.3) with this new callback has been released: https://github.com/obsproject/obs-studio/commit/a494cf5ce493b77af682e4c4e2a64302d2ecc393
        // background: obs_filter_get_parent() is not guaranteed to work in filter_create, but it should be in filter_add, and then we don't need this here anymore where it might be too late and also doubled for first audio and video frame
        ContextPointer->ParentSource = Obs.obs_filter_get_parent(ContextPointer->Source);
        if (ContextPointer->ParentSource != null)
          _parentSourceName = Marshal.PtrToStringUTF8((IntPtr)Obs.obs_source_get_name(ContextPointer->ParentSource))!;
      }

      var requiredVideoFormatConversion = SettingsDialog.Properties.GetRequiredVideoFormatConversion(frame->format);
      if (requiredVideoFormatConversion != video_format.VIDEO_FORMAT_NONE)
      {
        Module.Log($"{UniquePrefix} Filter data has unsupported format {frame->format} (need {requiredVideoFormatConversion}).", ObsLogLevel.Error);
        return;
      }

      var obsVideoInfo = ObsBmem.bzalloc<obs_video_info>();
      if (Convert.ToBoolean(Obs.obs_get_video_info(obsVideoInfo)) && (obsVideoInfo != null))
      {
        try
        {
          if (_beamSender.SetVideoParameters(Properties, frame->format, requiredVideoFormatConversion, frame->width, frame->height, obsVideoInfo->fps_num, obsVideoInfo->fps_den, frame->full_range, frame->color_matrix, frame->color_range_min, frame->color_range_max, frame->linesize, *(video_data._data_e__FixedBuffer*)&frame->data))
            StartSenderIfPossible();
        }
        catch (Exception ex)
        {
          Module.Log($"{UniquePrefix} ProcessVideo(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
          throw;
        }
      }
      ObsBmem.bfree(obsVideoInfo);
    }

    _beamSender.SendVideo(frame->timestamp, frame->data.e0);

  }

  private unsafe void ProcessAudio(void* data, obs_audio_data* frame)
  {
    if (!IsEnabled)
      return;

    if (_isFirstAudioFrame) // this is the first frame since the last output (re)start, get audio info
    {
      _isFirstAudioFrame = false;

      // identify and remember the parent source (obs_filter_get_parent is only valid during specific callbacks, so we need to do this here)
      if (ContextPointer->ParentSource == null)
      {
        //TODO: move this initialization to filter_add as soon as an OBS (probably the next after 29.1.3) with this new callback has been released: https://github.com/obsproject/obs-studio/commit/a494cf5ce493b77af682e4c4e2a64302d2ecc393
        // background: obs_filter_get_parent() is not guaranteed to work in filter_create, but it should be in filter_add, and then we don't need this here anymore where it might be too late and also doubled for first audio and video frame
        ContextPointer->ParentSource = Obs.obs_filter_get_parent(ContextPointer->Source);
        if (ContextPointer->ParentSource != null)
          _parentSourceName = Marshal.PtrToStringUTF8((IntPtr)Obs.obs_source_get_name(ContextPointer->ParentSource))!;
      }

      var audioInfo = ObsBmem.bzalloc<obs_audio_info>(); // need this for samples_per_sec info
      var audioOutputInfo = ObsAudio.audio_output_get_info(Obs.obs_get_audio()); // need this for format info, it's not in the global audio info
      var speakerLayout = (ContextPointer->ParentSource != null ? Obs.obs_source_get_speaker_layout(ContextPointer->ParentSource) : audioInfo->speakers);
      if (Convert.ToBoolean(Obs.obs_get_audio_info(audioInfo)) && (audioInfo != null) && (audioOutputInfo != null))
      {
        try
        {
          _beamSender.SetAudioParameters(audioOutputInfo->format, speakerLayout, audioInfo->samples_per_sec, frame->frames);
          StartSenderIfPossible();
        }
        catch (Exception ex)
        {
          Module.Log($"{UniquePrefix} ProcessAudio(): {ex.GetType().Name} in BeamSender initialization: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
          throw;
        }
      }
      ObsBmem.bfree(audioInfo);
    }

    _beamSender.SendAudio(frame->timestamp, frame->frames, frame->data.e0);
  }
  #endregion Instance methods

  #region Helper methods
  public static unsafe void Register()
  {
    var sourceInfo = new obs_source_info();
    fixed (byte* id = "Beam Receiver Filter AV"u8)
    {
      sourceInfo.id = (sbyte*)id;
      sourceInfo.type = obs_source_type.OBS_SOURCE_TYPE_FILTER;
      sourceInfo.output_flags = ObsSource.OBS_SOURCE_ASYNC_VIDEO | ObsSource.OBS_SOURCE_AUDIO;
      sourceInfo.get_name = &filter_av_get_name;
      sourceInfo.create = &filter_create;
      sourceInfo.filter_remove = &filter_remove;
      sourceInfo.destroy = &filter_destroy;
      sourceInfo.get_defaults = &filter_get_defaults_av;
      sourceInfo.get_properties = &filter_get_properties;
      sourceInfo.update = &filter_update;
      sourceInfo.save = &filter_save;
      sourceInfo.video_tick = &filter_video_tick;
      sourceInfo.filter_video = &filter_video;
      sourceInfo.filter_audio = &filter_audio;
      ObsSource.obs_register_source_s(&sourceInfo, (nuint)sizeof(obs_source_info));
    }
  }

  private static unsafe Filter GetFilter(void* data)
  {
    var context = (Context*)data;
    return _filterList[(*context).FilterId];
  }

  private static unsafe Filter GetFilter(obs_data* settings)
  {
    return _filterList.First(x => ((x.Value.ContextPointer)->Settings == settings)).Value;
  }
  #endregion Helper methods

  #region Filter API methods
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* filter_av_get_name(void* data)
  {
    Module.Log("filter_get_name called", ObsLogLevel.Debug);
    fixed (byte* filterName = "Beam Receiver Filter (Audio/Video)"u8)
      return (sbyte*)filterName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* filter_create(obs_data* settings, obs_source* source)
  {
    Module.Log("filter_create called", ObsLogLevel.Debug);
    Context* context = (Context*)Marshal.AllocHGlobal(sizeof(Context));
    context->FilterId = ++_filterCount;
    context->Settings = settings;
    context->Source = source;
    context->ParentSource = null; // has to be detected later from specific callbacks

    var thisFilter = new Filter(context);
    _filterList.TryAdd(context->FilterId, thisFilter);
    thisFilter.ContextPointer = context;

    return context;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_remove(void* data, obs_source* source)
  {
    Module.Log("filter_remove called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_destroy(void* data)
  {
    Module.Log("filter_destroy called", ObsLogLevel.Debug);

    var filter = GetFilter(data);
    _filterList.TryRemove(filter.FilterId, out _);

    filter.Dispose();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_properties* filter_get_properties(void* data)
  {
    Module.Log("filter_get_properties called");
    var filter = GetFilter(data);
    return filter.Properties.settings_get_properties(data);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_get_defaults_av(obs_data* settings)
  {
    Module.Log("filter_get_defaults called");
    BeamSenderProperties.settings_get_defaults(Beam.SenderTypes.FilterAudioVideo, settings);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_update(void* data, obs_data* settings)
  {
    Module.Log("filter_update called", ObsLogLevel.Debug);
    GetFilter(data)?.Properties.settings_update(settings);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_save(void* data, obs_data* settings)
  {
    Module.Log("filter_save called", ObsLogLevel.Debug);
    GetFilter(data)?.Properties.settings_save(settings);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_video_tick(void* data, float seconds)
  {
    GetFilter(data)?.ProcessTick(seconds);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_source_frame* filter_video(void* data, obs_source_frame* frame)
  {
    GetFilter(data)?.ProcessVideo(data, frame);
    return frame;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_audio_data* filter_audio(void* data, obs_audio_data* frame)
  {
    GetFilter(data)?.ProcessAudio(data, frame);
    return frame;
  }
  #endregion Filter API methods

}
