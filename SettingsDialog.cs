// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ObsInterop;

namespace xObsBeam;

public static class SettingsDialog
{

  static unsafe obs_data* _settings;
  static unsafe obs_source* _source;
  static unsafe obs_properties* _properties;

  static bool _initialized;
  static readonly Random _random = new();

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

  public static unsafe bool OutputEnabled
  {
    get
    {
      fixed (byte* propertyEnableId = "enable"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyEnableId));
    }
  }

  public static unsafe bool Lz4Compression { get; private set; }

  public static unsafe int Lz4CompressionLevel
  {
    get
    {
      fixed (byte* propertyLz4CompressionLevelId = "compression_lz4_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyLz4CompressionLevelId);
    }
  }

  public static unsafe bool JpegCompression { get; private set; }
  public static unsafe int JpegCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionJpegLevelId = "compression_jpeg_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionJpegLevelId);
    }
  }

  public static unsafe int JpegCompressionQuality
  {
    get
    {
      fixed (byte* propertyCompressionJpegQualityId = "compression_jpeg_quality"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionJpegQualityId);
    }
  }

  public static unsafe bool JpegCompressionLossless
  {
    get
    {
      fixed (byte* propertyCompressionJpegLosslessId = "compression_jpeg_lossless"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyCompressionJpegLosslessId));
    }
  }

  public static bool QoiCompression { get; private set; }

  public static unsafe int QoiCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoiLevelId = "compression_qoi_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionQoiLevelId);
    }
  }

  public static bool QoyCompression { get; private set; }

  public static unsafe int QoyCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoyLevelId = "compression_qoy_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionQoyLevelId);
    }
  }

  public static bool PngCompression { get; private set; }

  public static unsafe int PngCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionPngLevelId = "compression_png_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionPngLevelId);
    }
  }

  public static bool QoirCompression { get; private set; }

  public static unsafe int QoirCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoirLevelId = "compression_qoir_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionQoirLevelId);
    }
  }

  public static unsafe int QoirCompressionQuality
  {
    get
    {
      fixed (byte* propertyCompressionQoirQualityId = "compression_qoir_quality"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionQoirQualityId);
    }
  }

  public static unsafe bool QoirCompressionLossless
  {
    get
    {
      fixed (byte* propertyCompressionQoirLosslessId = "compression_qoir_lossless"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyCompressionQoirLosslessId));
    }
  }

  public static bool DensityCompression { get; private set; }

  public static unsafe int DensityCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionDensityLevelId = "compression_density_level"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionDensityLevelId);
    }
  }

  public static unsafe int DensityCompressionStrength
  {
    get
    {
      fixed (byte* propertyCompressionDensityStrengthId = "compression_density_strength"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyCompressionDensityStrengthId);
    }
  }

  public static unsafe bool CompressionMainThread
  {
    get
    {
      fixed (byte* propertyCompressionMainThreadId = "compression_main_thread"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyCompressionMainThreadId));
    }
  }

  public static video_format[]? RequireVideoFormats { get; private set; }

  public static unsafe bool UsePipe
  {
    get
    {
      fixed (byte* propertyConnectionTypePipeId = "connection_type_pipe"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyConnectionTypePipeId));
    }
  }

  public static unsafe string Identifier
  {
    get
    {
      fixed (byte* propertyIdentifierId = "identifier"u8)
        return Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(_settings, (sbyte*)propertyIdentifierId))!;
    }
  }

  public static unsafe string NetworkInterfaceName
  {
    get
    {
      fixed (byte* propertyNetworkInterfaceListId = "network_interface_list"u8)
        return Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(_settings, (sbyte*)propertyNetworkInterfaceListId))!;
    }
  }

  public static unsafe IPAddress NetworkInterfaceAddress
  {
    get
    {
      var configuredNetworkInterfaceName = NetworkInterfaceName;
      if (configuredNetworkInterfaceName == "Any: 0.0.0.0")
        return IPAddress.Any;

      foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
      {
        foreach (var ip in networkInterface.GetIPProperties().UnicastAddresses)
        {
          if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
            continue;
          string networkInterfaceDisplayName = networkInterface.Name + ": " + ip.Address + " / " + ip.IPv4Mask;
          if (networkInterfaceDisplayName == configuredNetworkInterfaceName)
            return ip.Address;
        }
      }
      Module.Log($"Didn't find configured network interface \"{configuredNetworkInterfaceName}\", falling back to loopback interface.", ObsLogLevel.Error);
      return IPAddress.Loopback;
    }
  }

  public static unsafe bool AutomaticPort
  {
    get
    {
      fixed (byte* propertyAutomaticListenPortId = "auto_listen_port"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyAutomaticListenPortId));
    }
  }

  public static unsafe int Port
  {
    get
    {
      fixed (byte* propertyListenPortId = "listen_port"u8)
      {
        if (AutomaticPort)
          return _random.Next(BeamSender.DefaultPort, BeamSender.DefaultPort + 10000);
        else
          return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyListenPortId);
      }
    }
  }

#pragma warning disable IDE1006

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* settings_get_name(void* data)
  {
    return null;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* settings_create(obs_data* settings, obs_source* source)
  {
    Module.Log("settings_create called", ObsLogLevel.Debug);
    _settings = settings;
    _source = source;
    return settings;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void settings_destroy(void* data)
  {
    Module.Log("settings_destroy called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_properties* settings_get_properties(void* data)
  {
    var properties = ObsProperties.obs_properties_create();
    //BUG: this flag became useless as of OBS 29.1.2, causing constant updates on every GUI element change, see: https://github.com/obsproject/obs-studio/commit/58fb63030cf2ee858ccf72d242cfabf3394f16f1
    ObsProperties.obs_properties_set_flags(properties, ObsProperties.OBS_PROPERTIES_DEFER_UPDATE);

    fixed (byte*
      propertyEnableId = "enable"u8,
      propertyEnableCaption = Module.ObsText("EnableOutputCaption"),
      propertyEnableText = Module.ObsText("EnableOutputText"),
      propertyIdentifierId = "identifier"u8,
      propertyIdentifierCaption = Module.ObsText("IdentifierCaption"),
      propertyIdentifierText = Module.ObsText("IdentifierText"),
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyNetworkInterfaceListCaption = Module.ObsText("NetworkInterfaceListCaption"),
      propertyNetworkInterfaceListText = Module.ObsText("NetworkInterfaceListText"),
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyAutomaticListenPortCaption = Module.ObsText("AutomaticListenPortCaption"),
      propertyAutomaticListenPortText = Module.ObsText("AutomaticListenPortText"),
      propertyListenPortId = "listen_port"u8,
      propertyListenPortCaption = Module.ObsText("ListenPortCaption"),
      propertyListenPortText = Module.ObsText("ListenPortText"),
      propertyCompressionId = "compression"u8,
      propertyCompressionCaption = Module.ObsText("CompressionCaption"),
      propertyCompressionLevelText = Module.ObsText("CompressionLevelText"),
      propertyCompressionJpegId = "compression_jpeg"u8,
      propertyCompressionJpegCaption = Module.ObsText("CompressionJpegCaption"),
      propertyCompressionJpegText = Module.ObsText("CompressionJpegText"),
      propertyCompressionJpegLosslessId = "compression_jpeg_lossless"u8,
      propertyCompressionJpegLosslessCaption = Module.ObsText("CompressionJpegLosslessCaption"),
      propertyCompressionJpegLosslessText = Module.ObsText("CompressionJpegLosslessText"),
      propertyCompressionJpegQualityId = "compression_jpeg_quality"u8,
      propertyCompressionJpegQualityCaption = Module.ObsText("CompressionJpegQualityCaption"),
      propertyCompressionJpegQualityText = Module.ObsText("CompressionJpegQualityText"),
      propertyCompressionJpegLevelId = "compression_jpeg_level"u8,
      propertyCompressionJpegLevelCaption = Module.ObsText("CompressionJpegLevelCaption"),
      propertyCompressionQoiId = "compression_qoi"u8,
      propertyCompressionQoiCaption = Module.ObsText("CompressionQOICaption"),
      propertyCompressionQoiText = Module.ObsText("CompressionQOIText"),
      propertyCompressionQoiLevelId = "compression_qoi_level"u8,
      propertyCompressionQoiLevelCaption = Module.ObsText("CompressionQOILevelCaption"),
      propertyCompressionQoyId = "compression_qoy"u8,
      propertyCompressionQoyCaption = Module.ObsText("CompressionQoyCaption"),
      propertyCompressionQoyText = Module.ObsText("CompressionQoyText"),
      propertyCompressionQoyLevelId = "compression_qoy_level"u8,
      propertyCompressionQoyLevelCaption = Module.ObsText("CompressionQoyLevelCaption"),
      propertyCompressionPngId = "compression_png"u8,
      propertyCompressionPngCaption = Module.ObsText("CompressionPngCaption"),
      propertyCompressionPngText = Module.ObsText("CompressionPngText"),
      propertyCompressionPngLevelId = "compression_png_level"u8,
      propertyCompressionPngLevelCaption = Module.ObsText("CompressionPngLevelCaption"),
      propertyCompressionQoirId = "compression_qoir"u8,
      propertyCompressionQoirCaption = Module.ObsText("CompressionQoirCaption"),
      propertyCompressionQoirText = Module.ObsText("CompressionQoirText"),
      propertyCompressionQoirLosslessId = "compression_qoir_lossless"u8,
      propertyCompressionQoirLosslessCaption = Module.ObsText("CompressionQoirLosslessCaption"),
      propertyCompressionQoirLosslessText = Module.ObsText("CompressionQoirLosslessText"),
      propertyCompressionQoirQualityId = "compression_qoir_quality"u8,
      propertyCompressionQoirQualityCaption = Module.ObsText("CompressionQoirQualityCaption"),
      propertyCompressionQoirQualityText = Module.ObsText("CompressionQoirQualityText"),
      propertyCompressionQoirLevelId = "compression_qoir_level"u8,
      propertyCompressionQoirLevelCaption = Module.ObsText("CompressionQoirLevelCaption"),
      propertyCompressionLz4Id = "compression_lz4"u8,
      propertyCompressionLz4Caption = Module.ObsText("CompressionLZ4Caption"),
      propertyCompressionLz4Text = Module.ObsText("CompressionLZ4Text"),
      propertyCompressionLz4LevelId = "compression_lz4_level"u8,
      propertyCompressionLz4LevelCaption = Module.ObsText("CompressionLZ4LevelCaption"),
      propertyCompressionDensityId = "compression_density"u8,
      propertyCompressionDensityCaption = Module.ObsText("CompressionDensityCaption"),
      propertyCompressionDensityText = Module.ObsText("CompressionDensityText"),
      propertyCompressionDensityLevelId = "compression_density_level"u8,
      propertyCompressionDensityLevelCaption = Module.ObsText("CompressionDensityLevelCaption"),
      propertyCompressionDensityStrengthId = "compression_density_strength"u8,
      propertyCompressionDensityStrengthCaption = Module.ObsText("CompressionDensityStrengthCaption"),
      propertyCompressionDensityStrengthText = Module.ObsText("CompressionDensityStrengthText"),
      propertyCompressionMainThreadId = "compression_main_thread"u8,
      propertyCompressionMainThreadCaption = Module.ObsText("CompressionMainThreadCaption"),
      propertyCompressionMainThreadText = Module.ObsText("CompressionMainThreadText"),
      propertyCompressionFormatWarningId = "compression_format_warning_text"u8,
      propertyCompressionFormatWarningText = Module.ObsText("CompressionFormatWarningText"),
      propertyConnectionTypeId = "connection_type_group"u8,
      propertyConnectionTypeCaption = Module.ObsText("ConnectionTypeCaption"),
      propertyConnectionTypeText = Module.ObsText("ConnectionTypeText"),
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypePipeCaption = Module.ObsText("ConnectionTypePipeCaption"),
      propertyConnectionTypePipeText = Module.ObsText("ConnectionTypePipeText"),
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyConnectionTypeSocketCaption = Module.ObsText("ConnectionTypeSocketCaption"),
      propertyConnectionTypeSocketText = Module.ObsText("ConnectionTypeSocketText")
    )
    {
      // enable or disable the output
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyEnableId, (sbyte*)propertyEnableCaption), (sbyte*)propertyEnableText);

      // identifier configuration text box
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyIdentifierText);

      // compression group
      var compressionGroup = ObsProperties.obs_properties_create();
      var compressionGroupProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyCompressionId, (sbyte*)propertyCompressionCaption, obs_group_type.OBS_GROUP_NORMAL, compressionGroup);

      // JPEG compression options group
      var compressionJpegGroup = ObsProperties.obs_properties_create();
      var compressionJpegGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionJpegId, (sbyte*)propertyCompressionJpegCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionJpegGroup);
      ObsProperties.obs_property_set_visible(compressionJpegGroupProperty, Convert.ToByte(EncoderSupport.LibJpegTurbo));
      ObsProperties.obs_property_set_long_description(compressionJpegGroupProperty, (sbyte*)propertyCompressionJpegText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegGroupProperty, &CompressionSettingChangedEventHandler);
      // JPEG lossless compression option
      var compressionJpegLosslessProperty = ObsProperties.obs_properties_add_bool(compressionJpegGroup, (sbyte*)propertyCompressionJpegLosslessId, (sbyte*)propertyCompressionJpegLosslessCaption);
      ObsProperties.obs_property_set_long_description(compressionJpegLosslessProperty, (sbyte*)propertyCompressionJpegLosslessText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegLosslessProperty, &CompressionSettingChangedEventHandler);
      ObsProperties.obs_property_set_visible(compressionJpegLosslessProperty, Convert.ToByte(EncoderSupport.LibJpegTurboLossless));
      // JPEG compression quality
      var compressionJpegQualityProperty = ObsProperties.obs_properties_add_int_slider(compressionJpegGroup, (sbyte*)propertyCompressionJpegQualityId, (sbyte*)propertyCompressionJpegQualityCaption, 1, 100, 1);
      ObsProperties.obs_property_set_long_description(compressionJpegQualityProperty, (sbyte*)propertyCompressionJpegQualityText);
      // JPEG compression level (skip frames)
      var compressionJpegLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionJpegGroup, (sbyte*)propertyCompressionJpegLevelId, (sbyte*)propertyCompressionJpegLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionJpegLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegLevelProperty, &CompressionSettingChangedEventHandler);

      // PNG compression options group
      var compressionPngGroup = ObsProperties.obs_properties_create();
      var compressionPngGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionPngId, (sbyte*)propertyCompressionPngCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionPngGroup);
      ObsProperties.obs_property_set_visible(compressionPngGroupProperty, Convert.ToByte(EncoderSupport.FpngeLib));
      ObsProperties.obs_property_set_long_description(compressionPngGroupProperty, (sbyte*)propertyCompressionPngText);
      ObsProperties.obs_property_set_modified_callback(compressionPngGroupProperty, &CompressionSettingChangedEventHandler);
      // PNG compression level (skip frames)
      var compressionPngLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionPngGroup, (sbyte*)propertyCompressionPngLevelId, (sbyte*)propertyCompressionPngLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionPngLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionPngLevelProperty, &CompressionSettingChangedEventHandler);

      // QOI compression options group
      var compressionQoiGroup = ObsProperties.obs_properties_create();
      var compressionQoiGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionQoiId, (sbyte*)propertyCompressionQoiCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionQoiGroup);
      ObsProperties.obs_property_set_long_description(compressionQoiGroupProperty, (sbyte*)propertyCompressionQoiText);
      ObsProperties.obs_property_set_modified_callback(compressionQoiGroupProperty, &CompressionSettingChangedEventHandler);
      // QOI compression level (skip frames)
      var compressionQoiLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionQoiGroup, (sbyte*)propertyCompressionQoiLevelId, (sbyte*)propertyCompressionQoiLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionQoiLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionQoiLevelProperty, &CompressionSettingChangedEventHandler);

      // QOY compression options group
      var compressionQoyGroup = ObsProperties.obs_properties_create();
      var compressionQoyGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionQoyId, (sbyte*)propertyCompressionQoyCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionQoyGroup);
      ObsProperties.obs_property_set_long_description(compressionQoyGroupProperty, (sbyte*)propertyCompressionQoyText);
      ObsProperties.obs_property_set_modified_callback(compressionQoyGroupProperty, &CompressionSettingChangedEventHandler);
      // QOY compression level (skip frames)
      var compressionQoyLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionQoyGroup, (sbyte*)propertyCompressionQoyLevelId, (sbyte*)propertyCompressionQoyLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionQoyLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionQoyLevelProperty, &CompressionSettingChangedEventHandler);

      // QOIR compression options group
      var compressionQoirGroup = ObsProperties.obs_properties_create();
      var compressionQoirGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionQoirId, (sbyte*)propertyCompressionQoirCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionQoirGroup);
      ObsProperties.obs_property_set_visible(compressionQoirGroupProperty, Convert.ToByte(EncoderSupport.QoirLib));
      ObsProperties.obs_property_set_long_description(compressionQoirGroupProperty, (sbyte*)propertyCompressionQoirText);
      ObsProperties.obs_property_set_modified_callback(compressionQoirGroupProperty, &CompressionSettingChangedEventHandler);
      // QOIR lossless compression option
      var compressionQoirLosslessProperty = ObsProperties.obs_properties_add_bool(compressionQoirGroup, (sbyte*)propertyCompressionQoirLosslessId, (sbyte*)propertyCompressionQoirLosslessCaption);
      ObsProperties.obs_property_set_long_description(compressionQoirLosslessProperty, (sbyte*)propertyCompressionQoirLosslessText);
      ObsProperties.obs_property_set_modified_callback(compressionQoirLosslessProperty, &CompressionSettingChangedEventHandler);
      // QOIR compression quality
      var compressionQoirQualityProperty = ObsProperties.obs_properties_add_int_slider(compressionQoirGroup, (sbyte*)propertyCompressionQoirQualityId, (sbyte*)propertyCompressionQoirQualityCaption, 1, 7, 1);
      ObsProperties.obs_property_set_long_description(compressionQoirQualityProperty, (sbyte*)propertyCompressionQoirQualityText);
      // QOIR compression level (skip frames)
      var compressionQoirLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionQoirGroup, (sbyte*)propertyCompressionQoirLevelId, (sbyte*)propertyCompressionQoirLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionQoirLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionQoirLevelProperty, &CompressionSettingChangedEventHandler);

      // LZ4 compression options group
      var compressionLz4Group = ObsProperties.obs_properties_create();
      var compressionLz4GroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionLz4Id, (sbyte*)propertyCompressionLz4Caption, obs_group_type.OBS_GROUP_CHECKABLE, compressionLz4Group);
      ObsProperties.obs_property_set_long_description(compressionLz4GroupProperty, (sbyte*)propertyCompressionLz4Text);
      ObsProperties.obs_property_set_modified_callback(compressionLz4GroupProperty, &CompressionSettingChangedEventHandler);
      // LZ4 compression level (skip frames)
      var compressionLz4LevelProperty = ObsProperties.obs_properties_add_int_slider(compressionLz4Group, (sbyte*)propertyCompressionLz4LevelId, (sbyte*)propertyCompressionLz4LevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionLz4LevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionLz4LevelProperty, &CompressionSettingChangedEventHandler);

      // Density compression options group
      var compressionDensityGroup = ObsProperties.obs_properties_create();
      var compressionDensityGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionDensityId, (sbyte*)propertyCompressionDensityCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionDensityGroup);
      ObsProperties.obs_property_set_visible(compressionDensityGroupProperty, Convert.ToByte(EncoderSupport.DensityApi));
      ObsProperties.obs_property_set_long_description(compressionDensityGroupProperty, (sbyte*)propertyCompressionDensityText);
      ObsProperties.obs_property_set_modified_callback(compressionDensityGroupProperty, &CompressionSettingChangedEventHandler);
      // Density compression level (skip frames)
      var compressionDensityLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionDensityGroup, (sbyte*)propertyCompressionDensityLevelId, (sbyte*)propertyCompressionDensityLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionDensityLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionDensityLevelProperty, &CompressionSettingChangedEventHandler);
      // Density compression strength
      var compressionDensityStrengthProperty = ObsProperties.obs_properties_add_int_slider(compressionDensityGroup, (sbyte*)propertyCompressionDensityStrengthId, (sbyte*)propertyCompressionDensityStrengthCaption, 1, 3, 1);
      ObsProperties.obs_property_set_long_description(compressionDensityStrengthProperty, (sbyte*)propertyCompressionDensityStrengthText);
      ObsProperties.obs_property_set_modified_callback(compressionDensityStrengthProperty, &CompressionSettingChangedEventHandler);

      // warning message shown when video color format conversion is necessary
      var compressionFormatWarningProperty = ObsProperties.obs_properties_add_text(compressionGroup, (sbyte*)propertyCompressionFormatWarningId, (sbyte*)propertyCompressionFormatWarningText, obs_text_type.OBS_TEXT_INFO);
      ObsProperties.obs_property_text_set_info_type(compressionFormatWarningProperty, obs_text_info_type.OBS_TEXT_INFO_WARNING);

      // compress from OBS render thread option
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_bool(compressionGroup, (sbyte*)propertyCompressionMainThreadId, (sbyte*)propertyCompressionMainThreadCaption), (sbyte*)propertyCompressionMainThreadText);

      // connection type selection group
      var connectionTypeGroup = ObsProperties.obs_properties_create();
      var connectionTypeGroupProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyConnectionTypeId, (sbyte*)propertyConnectionTypeCaption, obs_group_type.OBS_GROUP_NORMAL, connectionTypeGroup);
      ObsProperties.obs_property_set_long_description(connectionTypeGroupProperty, (sbyte*)propertyConnectionTypeText);
      // connection type pipe option
      var connectionTypePipeProperty = ObsProperties.obs_properties_add_bool(connectionTypeGroup, (sbyte*)propertyConnectionTypePipeId, (sbyte*)propertyConnectionTypePipeCaption);
      ObsProperties.obs_property_set_long_description(connectionTypePipeProperty, (sbyte*)propertyConnectionTypePipeText);
      ObsProperties.obs_property_set_modified_callback(connectionTypePipeProperty, &ConnectionTypePipeChangedEventHandler);
      // connection type socket option
      var connectionTypeSocketProperty = ObsProperties.obs_properties_add_bool(connectionTypeGroup, (sbyte*)propertyConnectionTypeSocketId, (sbyte*)propertyConnectionTypeSocketCaption);
      ObsProperties.obs_property_set_long_description(connectionTypeSocketProperty, (sbyte*)propertyConnectionTypeSocketText);
      ObsProperties.obs_property_set_modified_callback(connectionTypeSocketProperty, &ConnectionTypeSocketChangedEventHandler);

      // network interface selection
      var networkInterfacesList = ObsProperties.obs_properties_add_list(properties, (sbyte*)propertyNetworkInterfaceListId, (sbyte*)propertyNetworkInterfaceListCaption, obs_combo_type.OBS_COMBO_TYPE_LIST, obs_combo_format.OBS_COMBO_FORMAT_STRING);
      ObsProperties.obs_property_set_long_description(networkInterfacesList, (sbyte*)propertyNetworkInterfaceListText);
      fixed (byte* networkInterfaceAnyListItem = "Any: 0.0.0.0"u8)
        ObsProperties.obs_property_list_add_string(networkInterfacesList, (sbyte*)networkInterfaceAnyListItem, (sbyte*)networkInterfaceAnyListItem);
      foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (networkInterface.OperationalStatus == OperationalStatus.Up)
        {
          foreach (var ip in networkInterface.GetIPProperties().UnicastAddresses)
          {
            if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
              continue;
            string networkInterfaceDisplayName = networkInterface.Name + ": " + ip.Address + " / " + ip.IPv4Mask;
            Module.Log($"Found network interface: {networkInterfaceDisplayName}", ObsLogLevel.Debug);
            fixed (byte* networkInterfaceListItem = Encoding.UTF8.GetBytes(networkInterfaceDisplayName))
              ObsProperties.obs_property_list_add_string(networkInterfacesList, (sbyte*)networkInterfaceListItem, (sbyte*)networkInterfaceListItem);
          }
        }
      }

      // listen port configuration
      var automaticListenPortProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyAutomaticListenPortId, (sbyte*)propertyAutomaticListenPortCaption);
      ObsProperties.obs_property_set_long_description(automaticListenPortProperty, (sbyte*)propertyAutomaticListenPortText);
      ObsProperties.obs_property_set_modified_callback(automaticListenPortProperty, &AutomaticListenPortEnabledChangedEventHandler);
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_int(properties, (sbyte*)propertyListenPortId, (sbyte*)propertyListenPortCaption, 1024, 65535, 1), (sbyte*)propertyListenPortText);

    }
    _properties = properties;
    return properties;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void settings_get_defaults(obs_data* settings)
  {
    Module.Log("settings_get_defaults called", ObsLogLevel.Debug);
    fixed (byte*
      propertyEnableId = "enable"u8,
      propertyIdentifierId = "identifier"u8,
      propertyIdentifierDefaultText = "BeamSender"u8,
      propertyCompressionQoiLevelId = "compression_qoi_level"u8,
      propertyCompressionQoyLevelId = "compression_qoy_level"u8,
      propertyCompressionDensityLevelId = "compression_density_level"u8,
      propertyCompressionDensityStrengthId = "compression_density_strength"u8,
      propertyCompressionPngLevelId = "compression_png_level"u8,
      propertyCompressionQoirQualityId = "compression_qoir_quality"u8,
      propertyCompressionQoirLevelId = "compression_qoir_level"u8,
      propertyCompressionQoirLosslessId = "compression_qoir_lossless"u8,
      propertyCompressionJpegQualityId = "compression_jpeg_quality"u8,
      propertyCompressionJpegLevelId = "compression_jpeg_level"u8,
      propertyCompressionLz4LevelId = "compression_lz4_level"u8,
      propertyCompressionMainThreadId = "compression_main_thread"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyEnableId, Convert.ToByte(false));
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierDefaultText);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoiLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoyLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionPngLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoirQualityId, 7);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoirLevelId, 10);
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyCompressionQoirLosslessId, Convert.ToByte(true));
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionJpegQualityId, 90);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionJpegLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionLz4LevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionDensityLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionDensityStrengthId, 2);
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyCompressionMainThreadId, Convert.ToByte(true));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(true));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(false));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyAutomaticListenPortId, Convert.ToByte(true));
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyListenPortId, BeamSender.DefaultPort);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void settings_update(void* data, obs_data* settings)
  {
    Module.Log("settings_update called", ObsLogLevel.Debug);

    if (!_initialized)
    {
      _initialized = true;
      // compression settings use global variables that need to be initialized
      UpdateCompressionSettings(_properties, settings);
    }

    fixed (byte* propertyEnableId = "enable"u8)
    {
      var isEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyEnableId));
      if (Output.IsReady)
      {
        if (Output.IsActive || !isEnabled) // need to stop the output to apply settings changes
        {
          Output.Stop();
          if (isEnabled) // a bit of delay is necessary if the output was started before
            Task.Delay(1000).ContinueWith((t) => Output.Start());
        }
        else if (isEnabled)
          Output.Start();
      }
    }
  }
#pragma warning restore IDE1006

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte AutomaticListenPortEnabledChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyEnableId = "enable"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
    {
      var automaticListenPort = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyAutomaticListenPortId));
      Module.Log($"Automatic listen port enabled: {automaticListenPort}", ObsLogLevel.Debug);
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!automaticListenPort));
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypePipeChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
    {
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyAutomaticListenPortId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceListId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!connectionTypePipe));
      Module.Log("Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypeSocketChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
    {
      var connectionTypePipe = !Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypeSocketId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyAutomaticListenPortId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceListId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!connectionTypePipe));
      Module.Log("Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
      return Convert.ToByte(true);
    }
  }

  public static unsafe video_format GetRequiredVideoFormatConversion()
  {
    video_format requiredVideoFormat = video_format.VIDEO_FORMAT_NONE;
    if (RequireVideoFormats == null)
      return requiredVideoFormat;

    // get current video format
    obs_video_info* obsVideoInfo = ObsBmem.bzalloc<obs_video_info>();
    if (Convert.ToBoolean(Obs.obs_get_video_info(obsVideoInfo)) && (obsVideoInfo != null))
    {
      // some compression algorithms can only work with specific color formats
      if (!RequireVideoFormats.Contains(obsVideoInfo->output_format)) // is a specific format required that is not the currently configured format?
        requiredVideoFormat = RequireVideoFormats[0]; // the first item on the list is always the preferred format
    }
    ObsBmem.bfree(obsVideoInfo);
    return requiredVideoFormat;
  }

  private static unsafe void UpdateCompressionSettings(obs_properties* properties, obs_data* settings)
  {
    fixed (byte*
      propertyCompressionJpegId = "compression_jpeg"u8,
      propertyCompressionJpegLosslessId = "compression_jpeg_lossless"u8,
      propertyCompressionJpegQualityId = "compression_jpeg_quality"u8,
      propertyCompressionJpegLevelId = "compression_jpeg_level"u8,
      propertyCompressionQoiId = "compression_qoi"u8,
      propertyCompressionQoiLevelId = "compression_qoi_level"u8,
      propertyCompressionQoyId = "compression_qoy"u8,
      propertyCompressionQoyLevelId = "compression_qoy_level"u8,
      propertyCompressionPngId = "compression_png"u8,
      propertyCompressionPngLevelId = "compression_png_level"u8,
      propertyCompressionQoirId = "compression_qoir"u8,
      propertyCompressionQoirLosslessId = "compression_qoir_lossless"u8,
      propertyCompressionQoirQualityId = "compression_qoir_quality"u8,
      propertyCompressionQoirLevelId = "compression_qoir_level"u8,
      propertyCompressionLz4Id = "compression_lz4"u8,
      propertyCompressionLz4LevelId = "compression_lz4_level"u8,
      propertyCompressionDensityId = "compression_density"u8,
      propertyCompressionDensityLevelId = "compression_density_level"u8,
      propertyCompressionMainThreadId = "compression_main_thread"u8,
      propertyCompressionFormatWarningId = "compression_format_warning_text"u8
    )
    {
      // get current settings after the change
      var jpegCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionJpegId));
      var qoiCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoiId));
      var qoyCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoyId));
      var pngCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionPngId));
      var qoirCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoirId));
      var qoiLevel = ObsData.obs_data_get_int(settings, (sbyte*)propertyCompressionQoiLevelId);
      var lz4CompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionLz4Id));
      var densityCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionDensityId));

      // handle the special case where JPEG is enabled but the library couldn't be loaded, in this case force disable this option
      if (jpegCompressionEnabled && !EncoderSupport.LibJpegTurbo)
      {
        jpegCompressionEnabled = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(jpegCompressionEnabled));
      }

      // handle the special case where QOIR is enabled but the library couldn't be loaded, in this case force disable this option
      if (qoirCompressionEnabled && !EncoderSupport.QoirLib)
      {
        qoirCompressionEnabled = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(qoirCompressionEnabled));
      }

      // handle the special case where PNG is enabled but the library couldn't be loaded, in this case force disable this option
      if (pngCompressionEnabled && !EncoderSupport.FpngeLib)
      {
        pngCompressionEnabled = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(pngCompressionEnabled));
      }

      // handle the special case where Density is enabled but the library couldn't be loaded, in this case force disable this option
      if (densityCompressionEnabled && !EncoderSupport.DensityApi)
      {
        densityCompressionEnabled = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(densityCompressionEnabled));
      }

      // react to setting changes, avoid mixing incompatible settings
      if (jpegCompressionEnabled && !JpegCompression)
      {
        JpegCompression = jpegCompressionEnabled;
        QoiCompression = false;
        QoyCompression = false;
        PngCompression = false;
        QoirCompression = false;
        Lz4Compression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionLz4Id, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (qoiCompressionEnabled && !QoiCompression)
      {
        QoiCompression = qoiCompressionEnabled;
        QoyCompression = false;
        JpegCompression = false;
        Lz4Compression = false;
        PngCompression = false;
        QoirCompression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (qoyCompressionEnabled && !QoyCompression)
      {
        QoyCompression = qoyCompressionEnabled;
        QoiCompression = false;
        JpegCompression = false;
        Lz4Compression = false;
        PngCompression = false;
        QoirCompression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (pngCompressionEnabled && !PngCompression)
      {
        PngCompression = pngCompressionEnabled;
        QoiCompression = false;
        QoyCompression = false;
        Lz4Compression = false;
        JpegCompression = false;
        QoirCompression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionLz4Id, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (qoirCompressionEnabled && !QoirCompression)
      {
        QoirCompression = qoirCompressionEnabled;
        PngCompression = false;
        QoiCompression = false;
        QoyCompression = false;
        Lz4Compression = false;
        JpegCompression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionLz4Id, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (lz4CompressionEnabled && !Lz4Compression)
      {
        Lz4Compression = lz4CompressionEnabled;
        QoiCompression = false;
        QoyCompression = false;
        JpegCompression = false;
        PngCompression = false;
        QoirCompression = false;
        DensityCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
      }
      else if (densityCompressionEnabled && !DensityCompression)
      {
        DensityCompression = densityCompressionEnabled;
        QoiCompression = false;
        QoyCompression = false;
        Lz4Compression = false;
        JpegCompression = false;
        PngCompression = false;
        QoirCompression = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionPngId, Convert.ToByte(PngCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionLz4Id, Convert.ToByte(Lz4Compression));
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
      }
      else
      {
        JpegCompression = jpegCompressionEnabled;
        QoiCompression = qoiCompressionEnabled;
        QoyCompression = qoyCompressionEnabled;
        PngCompression = pngCompressionEnabled;
        QoirCompression = qoirCompressionEnabled;
        Lz4Compression = lz4CompressionEnabled;
        DensityCompression = densityCompressionEnabled;
      }

      // JPEG: enable quality setting and disable level setting if lossless is disabled or vice versa
      var jpegLossless = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionJpegLosslessId));
      if (jpegLossless && !EncoderSupport.LibJpegTurboV3) // handle the special case where JPEG lossless is enabled but the library doesn't support it, in this case force disable this option
      {
        jpegLossless = false;
        ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegLosslessId, Convert.ToByte(jpegLossless));
      }
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionJpegQualityId), Convert.ToByte(!jpegLossless));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionJpegLevelId), Convert.ToByte(jpegLossless));

      // QOIR: enable quality setting and disable level setting if lossless is disabled or vice versa
      var qoirLossless = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoirLosslessId));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionQoirQualityId), Convert.ToByte(!qoirLossless));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionQoirLevelId), Convert.ToByte(qoirLossless));

      // derive video format requirements from the settings
      if (QoiCompression || QoirCompression || (JpegCompression && JpegCompressionLossless))
        RequireVideoFormats = new[] { video_format.VIDEO_FORMAT_BGRA };
      else if (QoyCompression)
        RequireVideoFormats = new[] { video_format.VIDEO_FORMAT_NV12 };
      else if (PngCompression)
        RequireVideoFormats = new[] { video_format.VIDEO_FORMAT_RGBA };
      else if (JpegCompression && !JpegCompressionLossless)
        RequireVideoFormats = new[] { video_format.VIDEO_FORMAT_I420, video_format.VIDEO_FORMAT_I444, video_format.VIDEO_FORMAT_NV12 };
      else
        RequireVideoFormats = null;

      var requiredVideoFormatConversion = GetRequiredVideoFormatConversion();
      if (requiredVideoFormatConversion != video_format.VIDEO_FORMAT_NONE)
      {
        var compressionFormatWarningProperty = ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionFormatWarningId);
        fixed (byte* propertyCompressionFormatWarningText = Module.ObsText("CompressionFormatWarningText", requiredVideoFormatConversion.ToString().Replace("VIDEO_FORMAT_", "")))
          ObsProperties.obs_property_set_description(compressionFormatWarningProperty, (sbyte*)propertyCompressionFormatWarningText);
        ObsProperties.obs_property_set_visible(compressionFormatWarningProperty, Convert.ToByte(true));
      }
      else
        ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionFormatWarningId), Convert.ToByte(false));

      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyCompressionMainThreadId), Convert.ToByte(QoiCompression || QoyCompression || PngCompression || QoirCompression || JpegCompression || Lz4Compression || DensityCompression));
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte CompressionSettingChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    UpdateCompressionSettings(properties, settings);
    return Convert.ToByte(true);
  }
}
