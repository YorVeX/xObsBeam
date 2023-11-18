// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text;
using ObsInterop;

namespace xObsBeam;

public static class SettingsDialog
{

  static unsafe obs_data* _settings;
  static unsafe obs_source* _source;

  public static BeamSenderProperties Properties { get; private set; } = new(Beam.SenderTypes.Output);

  public static unsafe void Register()
  {
    var sourceInfo = new obs_source_info();
    fixed (byte* id = "Beam Output Settings"u8)
    {
      sourceInfo.id = (sbyte*)id;
      sourceInfo.type = obs_source_type.OBS_SOURCE_TYPE_FILTER;
      sourceInfo.output_flags = ObsSource.OBS_SOURCE_CAP_DISABLED;
      sourceInfo.get_name = &settings_get_name;
      sourceInfo.create = &settings_create;
      sourceInfo.destroy = &settings_destroy;
      sourceInfo.update = &settings_update;
      sourceInfo.save = &settings_save;
      sourceInfo.get_defaults = &settings_get_defaults;
      sourceInfo.get_properties = &settings_get_properties;
      ObsSource.obs_register_source_s(&sourceInfo, (nuint)sizeof(obs_source_info));
      var source = Obs.obs_source_create((sbyte*)id, (sbyte*)id, null, null);
      string configPath = Module.GetString(Obs.obs_module_get_config_path(Module.ObsModule, null));
      Directory.CreateDirectory(configPath); // ensure this directory exists
      fixed (byte* configFile = Encoding.UTF8.GetBytes(Path.Combine(configPath, Module.ModuleName + ".json")))
      {
        var settings = ObsData.obs_data_create_from_json_file((sbyte*)configFile);
        Obs.obs_source_update(_source, settings);
        ObsData.obs_data_release(settings);
      }
      Properties.Source = source;
    }
  }

  public static unsafe void Show()
  {
    ObsFrontendApi.obs_frontend_open_source_properties(_source);
  }

  public static unsafe void Save()
  {
    fixed (byte* fileName = Encoding.UTF8.GetBytes(Module.ModuleName + ".json"))
    {
      var configPathObs = Obs.obs_module_get_config_path(Module.ObsModule, (sbyte*)fileName);
      ObsData.obs_data_save_json(_settings, configPathObs);
      ObsBmem.bfree(configPathObs);
    }
  }

  public static unsafe void Dispose()
  {
    Obs.obs_source_release(_source);
    ObsData.obs_data_release(_settings);
  }

#pragma warning disable IDE1006

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe sbyte* settings_get_name(void* data)
  {
    fixed (byte* dialogName = "Beam Sender Output Settings"u8)
      return (sbyte*)dialogName;
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe void* settings_create(obs_data* settings, obs_source* source)
  {
    Module.Log("settings_create called", ObsLogLevel.Debug);
    Properties.Settings = settings;
    _settings = settings;
    _source = source;
    return settings;
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe void settings_destroy(void* data)
  {
    Module.Log("settings_destroy called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe obs_properties* settings_get_properties(void* data)
  {
    Module.Log("settings_get_properties called", ObsLogLevel.Debug);
    return Properties.settings_get_properties(data);
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe void settings_get_defaults(obs_data* settings)
  {
    Module.Log("settings_get_defaults called", ObsLogLevel.Debug);
    BeamSenderProperties.settings_get_defaults(Beam.SenderTypes.Output, settings);
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe void settings_update(void* data, obs_data* settings)
  {
    Module.Log("settings_update called", ObsLogLevel.Debug);
    Properties.settings_update(settings);
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
  public static unsafe void settings_save(void* data, obs_data* settings)
  {
    Module.Log("settings_save called", ObsLogLevel.Debug);
  }
#pragma warning restore IDE1006

}
