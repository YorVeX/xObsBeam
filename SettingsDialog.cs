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
      ObsSource.obs_register_source_s(&sourceInfo, (nuint)Marshal.SizeOf(sourceInfo));
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

  public static unsafe bool QoiCompression
  {
    get
    {
      fixed (byte* propertyQoiCompressionId = "compression_qoi"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(_settings, (sbyte*)propertyQoiCompressionId));
    }
  }

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

  public static unsafe int Port
  {
    get
    {
      fixed (byte* propertyListenPortId = "listen_port"u8)
        return (int)ObsData.obs_data_get_int(_settings, (sbyte*)propertyListenPortId);
    }
  }

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
      propertyCompressionQoiId = "compression_qoi"u8,
      propertyCompressionQoiCaption = Module.ObsText("CompressionQOICaption"),
      propertyCompressionQoiText = Module.ObsText("CompressionQOIText"),
      propertyCompressionQoiLevelId = "compression_qoi_level"u8,
      propertyCompressionQoiLevelCaption = Module.ObsText("CompressionQOILevelCaption"),
      propertyCompressionQoiLevelText = Module.ObsText("CompressionQOILevelText"),
      propertyConnectionTypeId = "connection_type"u8,
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

      // compression selection group
      var compressionQoiPropertyGroup = ObsProperties.obs_properties_create();
      var compressionQoiProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyCompressionQoiId, (sbyte*)propertyCompressionQoiCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionQoiPropertyGroup);
      ObsProperties.obs_property_set_long_description(compressionQoiProperty, (sbyte*)propertyCompressionQoiText);
      // compression level
      var compressionQoiLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionQoiPropertyGroup, (sbyte*)propertyCompressionQoiLevelId, (sbyte*)propertyCompressionQoiLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionQoiLevelProperty, (sbyte*)propertyCompressionQoiLevelText);
      ObsProperties.obs_property_set_enabled(compressionQoiLevelProperty, Convert.ToByte(false)); //TODO: QOI: implemented skipping compression for frames

      // identifier configuration text box
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyIdentifierText);

      // connection type selection group
      var connectionTypePropertyGroup = ObsProperties.obs_properties_create();
      var connectionTypeProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyConnectionTypeId, (sbyte*)propertyConnectionTypeCaption, obs_group_type.OBS_GROUP_NORMAL, connectionTypePropertyGroup);
      ObsProperties.obs_property_set_long_description(connectionTypeProperty, (sbyte*)propertyConnectionTypeText);
      // connection type pipe option
      var connectionTypePipeProperty = ObsProperties.obs_properties_add_bool(connectionTypePropertyGroup, (sbyte*)propertyConnectionTypePipeId, (sbyte*)propertyConnectionTypePipeCaption);
      ObsProperties.obs_property_set_long_description(connectionTypePipeProperty, (sbyte*)propertyConnectionTypePipeText);
      ObsProperties.obs_property_set_modified_callback(connectionTypePipeProperty, &ConnectionTypePipeChangedEventHandler);
      // connection type socket option
      var connectionTypeSocketProperty = ObsProperties.obs_properties_add_bool(connectionTypePropertyGroup, (sbyte*)propertyConnectionTypeSocketId, (sbyte*)propertyConnectionTypeSocketCaption);
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
      // ObsProperties.obs_property_set_modified_callback(automaticListenPortProperty, &AutomaticListenPortEnabledChangedEventHandler); //TODO: PeerDiscovery: activate this as soon as peer discovery is implemented
      ObsProperties.obs_property_set_enabled(automaticListenPortProperty, Convert.ToByte(false)); //TODO: PeerDiscovery: remove this as soon as peer discovery is implemented
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_int(properties, (sbyte*)propertyListenPortId, (sbyte*)propertyListenPortCaption, 1024, 65535, 1), (sbyte*)propertyListenPortText);



    }
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
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyEnableId, Convert.ToByte(false));
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierDefaultText);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoiLevelId, 10);
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(true));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(false));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyAutomaticListenPortId, Convert.ToByte(false)); //TODO: PeerDiscovery: make this default to true as soon as peer discovery is implemented
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyListenPortId, BeamSender.DefaultPort);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void settings_update(void* data, obs_data* settings)
  {
    Module.Log("settings_update called", ObsLogLevel.Debug);
    fixed (byte*
      propertyEnableId = "enable"u8,
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyListenPortId = "listen_port"u8
    )
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



}