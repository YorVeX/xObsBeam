// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ObsInterop;

namespace xObsBeam;

public class BeamSenderProperties
{

  public static BeamSenderProperties Empty => new(Beam.SenderTypes.None);

  unsafe struct Context
  {
    public uint PropertiesId;
    public obs_data* Settings;
    public obs_source* Source; // for outputs this is the dummy source used for settings management, for filters it's the filter itself
    public obs_properties* Properties;
  }

  bool _initialized;
  readonly Random _random = new();

  #region Class fields and helper functions
  static uint _propertiesCount;
  static readonly ConcurrentDictionary<uint, BeamSenderProperties> _propertiesList = new();

  private static unsafe BeamSenderProperties GetProperties(obs_properties* properties)
  {
    return _propertiesList.First(x => ((x.Value.ContextPointer)->Properties == properties)).Value;
  }
  #endregion Class fields and helper functions

  #region Instance fields
  readonly unsafe Context* ContextPointer;
  readonly List<string> _initializedEventHandlers = new();
  unsafe obs_properties* Properties;
  readonly Beam.SenderTypes PropertiesType;
  bool NeedSenderRestart;
  public string UniquePrefix;
  #endregion Instance fields

  #region Public properties
  public unsafe Filter? Filter { get; private set; }
  public unsafe obs_source* Source { get => ContextPointer->Source; set => ContextPointer->Source = value; }
  public unsafe obs_data* Settings { get => ContextPointer->Settings; set => ContextPointer->Settings = value; }

  public unsafe bool Enabled
  {
    get
    {
      fixed (byte* propertyEnableId = "enable"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(ContextPointer->Settings, (sbyte*)propertyEnableId));
    }
  }

  public unsafe bool Lz4Compression { get; private set; }

  public unsafe int Lz4CompressionLevel
  {
    get
    {
      fixed (byte* propertyLz4CompressionLevelId = "compression_lz4_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyLz4CompressionLevelId);
    }
  }

  public unsafe bool JpegCompression { get; private set; }
  public unsafe int JpegCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionJpegLevelId = "compression_jpeg_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionJpegLevelId);
    }
  }

  public unsafe int JpegCompressionQuality
  {
    get
    {
      fixed (byte* propertyCompressionJpegQualityId = "compression_jpeg_quality"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionJpegQualityId);
    }
  }

  public bool QoiCompression { get; private set; }

  public unsafe int QoiCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoiLevelId = "compression_qoi_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionQoiLevelId);
    }
  }

  public bool QoyCompression { get; private set; }

  public unsafe int QoyCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoyLevelId = "compression_qoy_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionQoyLevelId);
    }
  }

  public bool QoirCompression { get; private set; }

  public unsafe int QoirCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionQoirLevelId = "compression_qoir_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionQoirLevelId);
    }
  }

  public bool DensityCompression { get; private set; }

  public unsafe int DensityCompressionLevel
  {
    get
    {
      fixed (byte* propertyCompressionDensityLevelId = "compression_density_level"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionDensityLevelId);
    }
  }

  public unsafe int DensityCompressionStrength
  {
    get
    {
      fixed (byte* propertyCompressionDensityStrengthId = "compression_density_strength"u8)
        return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyCompressionDensityStrengthId);
    }
  }

  public unsafe bool CompressionMainThread
  {
    get
    {
      fixed (byte* propertyCompressionMainThreadId = "compression_main_thread"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(ContextPointer->Settings, (sbyte*)propertyCompressionMainThreadId));
    }
  }

  public readonly Dictionary<Beam.CompressionTypes, video_format[]> RequiredVideoFormats = new();
  public video_format[]? RequireVideoFormats { get; private set; }

  public unsafe bool UsePipe
  {
    get
    {
      fixed (byte* propertyConnectionTypePipeId = "connection_type_pipe"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(ContextPointer->Settings, (sbyte*)propertyConnectionTypePipeId));
    }
  }

  public unsafe string Identifier
  {
    get
    {
      fixed (byte* propertyIdentifierId = "identifier"u8)
        return Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(ContextPointer->Settings, (sbyte*)propertyIdentifierId))!;
    }
  }

  public unsafe string NetworkInterfaceName
  {
    get
    {
      fixed (byte* propertyNetworkInterfaceListId = "network_interface_list"u8)
        return Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(ContextPointer->Settings, (sbyte*)propertyNetworkInterfaceListId))!;
    }
  }

  public unsafe IPAddress NetworkInterfaceAddress
  {
    get
    {
      var configuredNetworkInterfaceName = NetworkInterfaceName;
      if (configuredNetworkInterfaceName == "Any: 0.0.0.0")
        return IPAddress.Any;

      foreach (var networkInterface in NetworkInterfaces.GetAllNetworkInterfaces())
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
      Module.Log($"{UniquePrefix} Didn't find configured network interface \"{configuredNetworkInterfaceName}\", falling back to loopback interface.", ObsLogLevel.Error);
      return IPAddress.Loopback;
    }
  }

  public bool NetworkInterfacesHaveLocalAddress { get; private set; }

  public unsafe bool AutomaticPort
  {
    get
    {
      fixed (byte* propertyAutomaticListenPortId = "auto_listen_port"u8)
        return Convert.ToBoolean(ObsData.obs_data_get_bool(ContextPointer->Settings, (sbyte*)propertyAutomaticListenPortId));
    }
  }

  public unsafe int Port
  {
    get
    {
      fixed (byte* propertyListenPortId = "listen_port"u8)
      {
        if (AutomaticPort)
          return _random.Next(BeamSender.DefaultPort, BeamSender.DefaultPort + 10000);
        else
          return (int)ObsData.obs_data_get_int(ContextPointer->Settings, (sbyte*)propertyListenPortId);
      }
    }
  }
  #endregion Public properties

  #region Instance methods
  // called by filters
  public unsafe BeamSenderProperties(Beam.SenderTypes propertiesType, Filter filter, obs_source* source, obs_data* settings)
  {
    Initialize(settings);
    UniquePrefix = filter.UniquePrefix;
    PropertiesType = propertiesType;
    Filter = filter;
    Context* context = (Context*)Marshal.AllocHGlobal(sizeof(Context)); ;
    context->PropertiesId = ++_propertiesCount;
    context->Source = source;
    context->Settings = settings;

    _propertiesList.TryAdd(context->PropertiesId, this);
    ContextPointer = context;
  }

  // called by the output and relays
  public unsafe BeamSenderProperties(Beam.SenderTypes propertiesType)
  {
    PropertiesType = propertiesType;
    UniquePrefix = PropertiesType.ToString();
    Context* context = (Context*)Marshal.AllocHGlobal(sizeof(Context)); ;
    context->PropertiesId = ++_propertiesCount;

    _propertiesList.TryAdd(context->PropertiesId, this);
    ContextPointer = context;
  }

  public unsafe void Dispose()
  {
    Marshal.FreeHGlobal((IntPtr)ContextPointer);
  }

  private unsafe void Initialize(obs_data* settings)
  {
    // compression settings use global variables that need to be initialized - for an output this is called from settings_update during OBS startup, for filters from the constructor, for relays it's not needed as they don't compress
    if (!_initialized)
    {
      _initialized = true;
      UpdateCompressionSettings(settings);
    }
  }
  #endregion Instance methods

#pragma warning disable IDE1006
  #region Callbacks
  public unsafe obs_properties* settings_get_properties(void* data, obs_properties* properties = null, obs_properties* propertiesParent = null)
  {
    _initializedEventHandlers.Clear(); // settings have been freshly opened, reset this
    if (properties == null)
    {
      properties = ObsProperties.obs_properties_create();
      ObsProperties.obs_properties_set_flags(properties, ObsProperties.OBS_PROPERTIES_DEFER_UPDATE);
    }

    fixed (byte*
      propertyEnableId = "enable"u8,
      propertyEnableOutputCaption = Module.ObsText("EnableOutputCaption"),
      propertyEnableOutputText = Module.ObsText("EnableOutputText"),
      propertyEnableFilterCaption = Module.ObsText("EnableFilterCaption"),
      propertyEnableFilterText = Module.ObsText("EnableFilterText"),
      propertyEnableRelayCaption = Module.ObsText("EnableRelayCaption"),
      propertyEnableRelayText = Module.ObsText("EnableRelayText"),
      propertyIdentifierId = "identifier"u8,
      propertyIdentifierCaption = Module.ObsText("IdentifierCaption"),
      propertyIdentifierText = Module.ObsText("IdentifierText"),
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyNetworkInterfaceListCaption = Module.ObsText("NetworkInterfaceListCaption"),
      propertyNetworkInterfaceListText = Module.ObsText("NetworkInterfaceListText"),
      propertyNetworkInterfaceNoLocalAddressWarningId = "network_interface_no_local_address_warning_text"u8,
      propertyNetworkInterfaceNoLocalAddressWarningText = Module.ObsText("NetworkInterfaceNoLocalAddressWarningText"),
      propertyAutomaticListenPortId = "auto_listen_port"u8,
      propertyAutomaticListenPortCaption = Module.ObsText("AutomaticListenPortCaption"),
      propertyAutomaticListenPortText = Module.ObsText("AutomaticListenPortText"),
      propertyListenPortId = "listen_port"u8,
      propertyListenPortCaption = Module.ObsText("ListenPortCaption"),
      propertyListenPortText = Module.ObsText("ListenPortText"),
      propertyCompressionHelpId = "compression_help"u8,
      propertyCompressionHelpCaption = Module.ObsText("CompressionHelpCaption"),
      propertyCompressionHelpText = Module.ObsText("CompressionHelpText"),
      propertyCompressionHelpUrl = "https://github.com/YorVeX/xObsBeam/wiki/Compression-help"u8,
      propertyCompressionShowOnlyRecommendedId = "compression_recommended_only"u8,
      propertyCompressionShowOnlyRecommendedCaption = Module.ObsText("CompressionShowOnlyRecommendedCaption"),
      propertyCompressionShowOnlyRecommendedText = Module.ObsText("CompressionShowOnlyRecommendedText"),
      propertyCompressionId = "compression"u8,
      propertyCompressionCaption = Module.ObsText("CompressionCaption"),
      propertyCompressionLevelText = Module.ObsText("CompressionLevelText"),
      propertyCompressionJpegId = "compression_jpeg"u8,
      propertyCompressionJpegCaption = Module.ObsText("CompressionJpegCaption"),
      propertyCompressionJpegText = Module.ObsText("CompressionJpegText"),
      propertyCompressionJpegQualityId = "compression_jpeg_quality"u8,
      propertyCompressionJpegQualityCaption = Module.ObsText("CompressionJpegQualityCaption"),
      propertyCompressionJpegQualityText = Module.ObsText("CompressionJpegQualityText"),
      propertyCompressionJpegLevelId = "compression_jpeg_level"u8,
      propertyCompressionJpegLevelCaption = Module.ObsText("CompressionJpegLevelCaption"),
      propertyCompressionJpegLibraryMissingWarningId = "compression_jpeg_library_missing_warning_text"u8,
      propertyCompressionJpegLibraryMissingWarningText = Module.ObsText("CompressionJpegLibraryMissingWarningText"),
      propertyCompressionJpegLibraryMissingHelpId = "compression_jpeg_library_missing_help"u8,
      propertyCompressionJpegLibraryMissingHelpCaption = Module.ObsText("CompressionJpegLibraryMissingHelpCaption"),
      propertyCompressionJpegLibraryMissingHelpText = Module.ObsText("CompressionJpegLibraryMissingHelpText"),
      propertyCompressionJpegLibraryMissingHelpUrl = "https://github.com/YorVeX/xObsBeam/tree/main/lib/libjpeg-turbo#using-the-wrapper-class"u8,
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
      propertyCompressionQoirId = "compression_qoir"u8,
      propertyCompressionQoirCaption = Module.ObsText("CompressionQoirCaption"),
      propertyCompressionQoirText = Module.ObsText("CompressionQoirText"),
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
      // enable or disable the output...
      if (PropertiesType == Beam.SenderTypes.Output)
      {
        var enableProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyEnableId, (sbyte*)propertyEnableOutputCaption);
        ObsProperties.obs_property_set_long_description(enableProperty, (sbyte*)propertyEnableOutputText);
        ObsProperties.obs_property_set_modified_callback(enableProperty, &EnableChangedEventHandler);
      }
      else if (PropertiesType != Beam.SenderTypes.Relay) // ...or filter (exclude relays, as they're controlled by show/hide of its source, so that it's consistent with receiver sources)
      {
        var enableProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyEnableId, (sbyte*)propertyEnableFilterCaption);
        ObsProperties.obs_property_set_long_description(enableProperty, (sbyte*)propertyEnableFilterText);
        ObsProperties.obs_property_set_modified_callback(enableProperty, &EnableChangedEventHandler);
      }
      ObsProperties.obs_property_set_modified_callback(enableProperty, &EnableChangedEventHandler);
    }

    // identifier configuration text box
    var identifierProperty = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierCaption, obs_text_type.OBS_TEXT_DEFAULT);
    ObsProperties.obs_property_set_long_description(identifierProperty, (sbyte*)propertyIdentifierText);
    ObsProperties.obs_property_set_modified_callback(identifierProperty, &IdentifierSettingChangedEventHandler);

    if (PropertiesType is not Beam.SenderTypes.FilterAudio and not Beam.SenderTypes.Relay)
    {
      // compression group
      var compressionGroup = ObsProperties.obs_properties_create();
      var compressionGroupProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyCompressionId, (sbyte*)propertyCompressionCaption, obs_group_type.OBS_GROUP_NORMAL, compressionGroup);
      // show only recommended compression options toggle
      var compressionShowOnlyRecommendedProperty = ObsProperties.obs_properties_add_bool(compressionGroup, (sbyte*)propertyCompressionShowOnlyRecommendedId, (sbyte*)propertyCompressionShowOnlyRecommendedCaption);
      ObsProperties.obs_property_set_long_description(compressionShowOnlyRecommendedProperty, (sbyte*)propertyCompressionShowOnlyRecommendedText);
      ObsProperties.obs_property_set_modified_callback(compressionShowOnlyRecommendedProperty, &CompressionSettingChangedEventHandler);
      // compression help button
      var compressionHelpProperty = ObsProperties.obs_properties_add_button(compressionGroup, (sbyte*)propertyCompressionHelpId, (sbyte*)propertyCompressionHelpCaption, null);
      ObsProperties.obs_property_set_long_description(compressionHelpProperty, (sbyte*)propertyCompressionHelpText);
      ObsProperties.obs_property_button_set_type(compressionHelpProperty, obs_button_type.OBS_BUTTON_URL);
      ObsProperties.obs_property_button_set_url(compressionHelpProperty, (sbyte*)propertyCompressionHelpUrl);

      // JPEG compression options group
      var compressionJpegGroup = ObsProperties.obs_properties_create();
      var compressionJpegGroupProperty = ObsProperties.obs_properties_add_group(compressionGroup, (sbyte*)propertyCompressionJpegId, (sbyte*)propertyCompressionJpegCaption, obs_group_type.OBS_GROUP_CHECKABLE, compressionJpegGroup);
      ObsProperties.obs_property_set_long_description(compressionJpegGroupProperty, (sbyte*)propertyCompressionJpegText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegGroupProperty, &CompressionSettingChangedEventHandler);
      // JPEG compression level (skip frames)
      var compressionJpegLevelProperty = ObsProperties.obs_properties_add_int_slider(compressionJpegGroup, (sbyte*)propertyCompressionJpegLevelId, (sbyte*)propertyCompressionJpegLevelCaption, 1, 10, 1);
      ObsProperties.obs_property_set_long_description(compressionJpegLevelProperty, (sbyte*)propertyCompressionLevelText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegLevelProperty, &CompressionSettingChangedEventHandler);
      // JPEG compression quality
      var compressionJpegQualityProperty = ObsProperties.obs_properties_add_int_slider(compressionJpegGroup, (sbyte*)propertyCompressionJpegQualityId, (sbyte*)propertyCompressionJpegQualityCaption, 1, 100, 1);
      ObsProperties.obs_property_set_long_description(compressionJpegQualityProperty, (sbyte*)propertyCompressionJpegQualityText);
      ObsProperties.obs_property_set_modified_callback(compressionJpegQualityProperty, &CompressionJpegQualitySettingChangedEventHandler);
      // JPEG compression warning message shown when library not found
      if (!EncoderSupport.LibJpegTurbo)
      {
        ObsProperties.obs_property_set_enabled(compressionJpegGroupProperty, Convert.ToByte(false));
        var compressionJpegLibraryMissingWarningProperty = ObsProperties.obs_properties_add_text(compressionGroup, (sbyte*)propertyCompressionJpegLibraryMissingWarningId, (sbyte*)propertyCompressionJpegLibraryMissingWarningText, obs_text_type.OBS_TEXT_INFO);
        ObsProperties.obs_property_text_set_info_type(compressionJpegLibraryMissingWarningProperty, obs_text_info_type.OBS_TEXT_INFO_WARNING);
        var compressionJpegLibraryMissingHelpProperty = ObsProperties.obs_properties_add_button(compressionGroup, (sbyte*)propertyCompressionJpegLibraryMissingHelpId, (sbyte*)propertyCompressionJpegLibraryMissingHelpCaption, null);
        ObsProperties.obs_property_set_long_description(compressionJpegLibraryMissingHelpProperty, (sbyte*)propertyCompressionJpegLibraryMissingHelpText);
        ObsProperties.obs_property_button_set_type(compressionJpegLibraryMissingHelpProperty, obs_button_type.OBS_BUTTON_URL);
        ObsProperties.obs_property_button_set_url(compressionJpegLibraryMissingHelpProperty, (sbyte*)propertyCompressionJpegLibraryMissingHelpUrl);
      }

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
      var compressionMainThreadProperty = ObsProperties.obs_properties_add_bool(compressionGroup, (sbyte*)propertyCompressionMainThreadId, (sbyte*)propertyCompressionMainThreadCaption);
      ObsProperties.obs_property_set_long_description(compressionMainThreadProperty, (sbyte*)propertyCompressionMainThreadText);
      ObsProperties.obs_property_set_modified_callback(compressionMainThreadProperty, &CompressionMainThreadChangedEventHandler);
    }

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
    var networkInterfacesListProperty = ObsProperties.obs_properties_add_list(properties, (sbyte*)propertyNetworkInterfaceListId, (sbyte*)propertyNetworkInterfaceListCaption, obs_combo_type.OBS_COMBO_TYPE_LIST, obs_combo_format.OBS_COMBO_FORMAT_STRING);
    ObsProperties.obs_property_set_long_description(networkInterfacesListProperty, (sbyte*)propertyNetworkInterfaceListText);
    fixed (byte* networkInterfaceAnyListItem = "Any: 0.0.0.0"u8)
      ObsProperties.obs_property_list_add_string(networkInterfacesListProperty, (sbyte*)networkInterfaceAnyListItem, (sbyte*)networkInterfaceAnyListItem);
    NetworkInterfacesHaveLocalAddress = false;
    foreach (var networkInterface in NetworkInterfaces.GetAllNetworkInterfaces())
    {
      if (networkInterface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
      {
        foreach (var ip in networkInterface.GetIPProperties().UnicastAddresses)
        {
          if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
            continue;
          string networkInterfaceDisplayName = networkInterface.Name + ": " + ip.Address + " / " + ip.IPv4Mask;
          if (NetworkInterfaces.IsLocalAddress(ip.Address))
            NetworkInterfacesHaveLocalAddress = true;
          Module.Log($"{UniquePrefix} Found network interface: {networkInterfaceDisplayName} (Local: {NetworkInterfaces.IsLocalAddress(ip.Address)})", ObsLogLevel.Debug);
          fixed (byte* networkInterfaceListItem = Encoding.UTF8.GetBytes(networkInterfaceDisplayName))
            ObsProperties.obs_property_list_add_string(networkInterfacesListProperty, (sbyte*)networkInterfaceListItem, (sbyte*)networkInterfaceListItem);
        }
      }
    }
    ObsProperties.obs_property_set_modified_callback(networkInterfacesListProperty, &NetworkInterfaceChangedEventHandler);

    // warning if the selected interface doesn't have a local address
    var networkInterfaceNoLocalAddressWarningProperty = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyNetworkInterfaceNoLocalAddressWarningId, (sbyte*)propertyNetworkInterfaceNoLocalAddressWarningText, obs_text_type.OBS_TEXT_INFO);
    ObsProperties.obs_property_text_set_info_type(networkInterfaceNoLocalAddressWarningProperty, obs_text_info_type.OBS_TEXT_INFO_WARNING);
    ObsProperties.obs_property_set_visible(networkInterfaceNoLocalAddressWarningProperty, Convert.ToByte(false));

    // listen port configuration
    var automaticListenPortProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyAutomaticListenPortId, (sbyte*)propertyAutomaticListenPortCaption);
    ObsProperties.obs_property_set_long_description(automaticListenPortProperty, (sbyte*)propertyAutomaticListenPortText);
    ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_int(properties, (sbyte*)propertyListenPortId, (sbyte*)propertyListenPortCaption, 1024, 65535, 1), (sbyte*)propertyListenPortText);
    ObsProperties.obs_property_set_modified_callback(automaticListenPortProperty, &AutomaticListenPortEnabledChangedEventHandler);

  }

  Properties = (propertiesParent != null ? propertiesParent : properties);
    ContextPointer->Properties = Properties;

    return properties;
  }

public static unsafe void settings_get_defaults(Beam.SenderTypes propertiesType, obs_data* settings)
{
  Module.Log($"{propertiesType} settings_get_defaults called", ObsLogLevel.Debug);
  fixed (byte*
    propertyEnableId = "enable"u8,
    propertyIdentifierId = "identifier"u8,
    propertyIdentifierOutputDefaultText = "Beam Sender Output"u8,
    propertyIdentifierFilterAvDefaultText = "Beam Sender Filter (Audio/Video)"u8,
    propertyIdentifierFilterVideoDefaultText = "Beam Sender Filter (Video only)"u8,
    propertyIdentifierFilterAudioDefaultText = "Beam Sender Filter (Audio only)"u8,
    propertyIdentifierRelayDefaultText = "Beam Sender Relay"u8,
    propertyCompressionShowOnlyRecommendedId = "compression_recommended_only"u8,
    propertyCompressionQoiLevelId = "compression_qoi_level"u8,
    propertyCompressionQoyLevelId = "compression_qoy_level"u8,
    propertyCompressionDensityLevelId = "compression_density_level"u8,
    propertyCompressionDensityStrengthId = "compression_density_strength"u8,
    propertyCompressionQoirLevelId = "compression_qoir_level"u8,
    propertyCompressionJpegId = "compression_jpeg"u8,
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
    if (propertiesType == Beam.SenderTypes.Output)
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyEnableId, Convert.ToByte(false)); // an active output blocks several OBS settings, enabling this should be a conscious decision by the user
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierOutputDefaultText);
    }
    else
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyEnableId, Convert.ToByte(true)); // a filter or relay is not blocking anything and disabling it should be rarely necessary, so it can be enabled by default
      if (propertiesType == Beam.SenderTypes.FilterAudioVideo)
        ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierFilterAvDefaultText);
      else if (propertiesType == Beam.SenderTypes.FilterVideo)
        ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierFilterVideoDefaultText);
      else if (propertiesType == Beam.SenderTypes.FilterAudio)
        ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierFilterAudioDefaultText);
      else if (propertiesType == Beam.SenderTypes.Relay)
        ObsData.obs_data_set_default_string(settings, (sbyte*)propertyIdentifierId, (sbyte*)propertyIdentifierRelayDefaultText);
    }

    if (propertiesType is not Beam.SenderTypes.FilterAudio and not Beam.SenderTypes.Relay)
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyCompressionShowOnlyRecommendedId, Convert.ToByte(true));
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoiLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoyLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionQoirLevelId, 10);
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(true));
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionJpegQualityId, 90);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionJpegLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionLz4LevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionDensityLevelId, 10);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyCompressionDensityStrengthId, 2);
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyCompressionMainThreadId, Convert.ToByte(true));
    }
    ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(false));
    ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(true));
    ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyAutomaticListenPortId, Convert.ToByte(true));
    ObsData.obs_data_set_default_int(settings, (sbyte*)propertyListenPortId, BeamSender.DefaultPort);
  }
}

public unsafe void settings_update(obs_data* settings)
{
  Module.Log($"{UniquePrefix} settings_update called", ObsLogLevel.Debug);
  Initialize(settings);
  RestartSenderIfNecessary(settings, (PropertiesType == Beam.SenderTypes.Output));
}

public unsafe void settings_save(obs_data* settings)
{
  Module.Log($"{UniquePrefix} settings_save called", ObsLogLevel.Debug);
  RestartSenderIfNecessary(settings);
}
#endregion Callbacks
#pragma warning restore IDE1006

public bool NativeVideoFormatSupport(Beam.CompressionTypes compressionType, video_format videoFormat)
{
  if (!RequiredVideoFormats.ContainsKey(compressionType))
    return true;
  return RequiredVideoFormats[compressionType].Contains(videoFormat);
}

public unsafe video_format GetRequiredVideoFormatConversion()
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

public unsafe video_format GetRequiredVideoFormatConversion(video_format currentFormat)
{
  video_format requiredVideoFormat = video_format.VIDEO_FORMAT_NONE;
  if (RequireVideoFormats == null)
    return requiredVideoFormat;

  // some compression algorithms can only work with specific color formats
  if (!RequireVideoFormats.Contains(currentFormat)) // is a specific format required that is not the currently configured format?
    requiredVideoFormat = RequireVideoFormats[0]; // the first item on the list is always the preferred format

  return requiredVideoFormat;
}

#region Event handler helper functions
private unsafe void RestartSenderIfNecessary(obs_data* settings, bool forceRestart = false)
{
  if (!NeedSenderRestart && !forceRestart)
    return;
  fixed (byte* propertyEnableId = "enable"u8)
  {
    var isEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyEnableId));
    if (PropertiesType == Beam.SenderTypes.Output)
    {
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
    else if (Filter != null)
    {
      if (Filter.IsActive || !isEnabled) // need to stop the filter to apply settings changes
      {
        Filter.Disable();
        if (isEnabled) // a bit of delay is necessary if the filter was started before
          Task.Delay(1000).ContinueWith((t) => Filter.Enable());
      }
      else if (isEnabled)
        Filter.Enable();
    }
  }
  NeedSenderRestart = false;
}

private void EventHandlerNeedSenderRestartCheck(string eventHandlerName)
{
  if (PropertiesType is Beam.SenderTypes.Output or Beam.SenderTypes.Relay) // the output and relay source just always update on settings_update
    return;
  if (_initializedEventHandlers.Contains(eventHandlerName)) // this is not the init call, something actually changed, therefore a restart is necessary
  {
    NeedSenderRestart = true;
    Module.Log($"{UniquePrefix} sender restart requested from {eventHandlerName}.", ObsLogLevel.Debug);
  }
  else // this is just the init call after opening the settings, the setting didn't actually change, therefore no restart is necessary
    _initializedEventHandlers.Add(eventHandlerName);
}

private unsafe void UpdateCompressionSettings(obs_data* settings)
{
  fixed (byte*
    propertyCompressionShowOnlyRecommendedId = "compression_recommended_only"u8,
    propertyCompressionJpegId = "compression_jpeg"u8,
    propertyCompressionJpegQualityId = "compression_jpeg_quality"u8,
    propertyCompressionJpegLevelId = "compression_jpeg_level"u8,
    propertyCompressionJpegLibraryMissingWarningId = "compression_jpeg_library_missing_warning_text"u8,
    propertyCompressionJpegLibraryMissingHelpId = "compression_jpeg_library_missing_help"u8,
    propertyCompressionQoiId = "compression_qoi"u8,
    propertyCompressionQoiLevelId = "compression_qoi_level"u8,
    propertyCompressionQoyId = "compression_qoy"u8,
    propertyCompressionQoyLevelId = "compression_qoy_level"u8,
    propertyCompressionQoirId = "compression_qoir"u8,
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
    var showOnlyRecommended = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionShowOnlyRecommendedId));
    var jpegCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionJpegId));
    var qoiCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoiId));
    var qoyCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoyId));
    var qoirCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionQoirId));
    var lz4CompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionLz4Id));
    var densityCompressionEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyCompressionDensityId));

    // get video format currently configured in OBS
    obs_video_info* obsVideoInfo = ObsBmem.bzalloc<obs_video_info>();
    video_format obsVideoFormat = video_format.VIDEO_FORMAT_NONE;
    if (Convert.ToBoolean(Obs.obs_get_video_info(obsVideoInfo)) && (obsVideoInfo != null))
      obsVideoFormat = obsVideoInfo->output_format;
    ObsBmem.bfree(obsVideoInfo);

    // fixed list of compression algorithms and their supported video formats
    RequiredVideoFormats.Clear();
    RequiredVideoFormats.Add(Beam.CompressionTypes.Qoi, new[] { video_format.VIDEO_FORMAT_BGRA });
    RequiredVideoFormats.Add(Beam.CompressionTypes.Qoir, new[] { video_format.VIDEO_FORMAT_BGRA });
    RequiredVideoFormats.Add(Beam.CompressionTypes.Qoy, new[] { video_format.VIDEO_FORMAT_NV12 });
    RequiredVideoFormats.Add(Beam.CompressionTypes.Jpeg, new[]
    {
        video_format.VIDEO_FORMAT_I420, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_I40A, // no native support by libjpeg-turbo, alpha will be dropped so that it becomes I420
        video_format.VIDEO_FORMAT_I422, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_I42A, // no native support by libjpeg-turbo, alpha will be dropped so that it becomes I422
        video_format.VIDEO_FORMAT_I444, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_YUVA, // no native support by libjpeg-turbo, alpha will be dropped so that it becomes I444
        video_format.VIDEO_FORMAT_NV12, // no native support by libjpeg-turbo, will be unpacked to I420
        video_format.VIDEO_FORMAT_YVYU, // no native support by libjpeg-turbo, will be unpacked to I422
        video_format.VIDEO_FORMAT_UYVY, // no native support by libjpeg-turbo, will be unpacked to I422
        video_format.VIDEO_FORMAT_YUY2, // no native support by libjpeg-turbo, will be unpacked to I422
        video_format.VIDEO_FORMAT_BGRA, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_BGRX, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_BGR3, // native support by libjpeg-turbo
        video_format.VIDEO_FORMAT_RGBA, // native support by libjpeg-turbo
      });

    // if only recommended items are configured to be shown: hide compression algorithms that don't natively support the current OBS video format or have a superior alternative available
    var qoiRecommended = !showOnlyRecommended || (NativeVideoFormatSupport(Beam.CompressionTypes.Qoi, obsVideoFormat) && !EncoderSupport.QoirLib);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionQoiId), Convert.ToByte(qoiRecommended));
    var qoirRecommended = !showOnlyRecommended || NativeVideoFormatSupport(Beam.CompressionTypes.Qoir, obsVideoFormat);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionQoirId), Convert.ToByte(qoirRecommended));
    var qoyRecommended = !showOnlyRecommended || NativeVideoFormatSupport(Beam.CompressionTypes.Qoy, obsVideoFormat);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionQoyId), Convert.ToByte(qoyRecommended));
    var lz4Recommended = !showOnlyRecommended || (NativeVideoFormatSupport(Beam.CompressionTypes.Lz4, obsVideoFormat) && !EncoderSupport.DensityApi);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionLz4Id), Convert.ToByte(lz4Recommended));
    var densityRecommended = !showOnlyRecommended || NativeVideoFormatSupport(Beam.CompressionTypes.Density, obsVideoFormat);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionDensityId), Convert.ToByte(densityRecommended));
    var jpegRecommended = !showOnlyRecommended || NativeVideoFormatSupport(Beam.CompressionTypes.Jpeg, obsVideoFormat);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionJpegId), Convert.ToByte(jpegRecommended));
    if (!EncoderSupport.LibJpegTurbo) // if the JPEG library is missing, a warning text and help button have been added...
    {
      // ...only show these if the JPEG option is visible
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionJpegLibraryMissingWarningId), Convert.ToByte(jpegRecommended));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionJpegLibraryMissingHelpId), Convert.ToByte(jpegRecommended));
    }

    // handle the special case where JPEG is enabled but is hidden by the setting to only show recommended options or the library couldn't be loaded, in this case force disable this option
    if (jpegCompressionEnabled && (!EncoderSupport.LibJpegTurbo || !jpegRecommended))
    {
      jpegCompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(jpegCompressionEnabled));
    }

    // handle the special case where QOIR is enabled but is hidden by the setting to only show recommended options or the library couldn't be loaded, in this case force disable this option
    if (qoirCompressionEnabled && (!EncoderSupport.QoirLib || !qoirRecommended))
    {
      qoirCompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(qoirCompressionEnabled));
    }

    // handle the special case where Density is enabled but is hidden by the setting to only show recommended options or the library couldn't be loaded, in this case force disable this option
    if (densityCompressionEnabled && (!EncoderSupport.DensityApi || !densityRecommended))
    {
      densityCompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(densityCompressionEnabled));
    }

    // handle the special case where LZ4 is enabled but is hidden by the setting to only show recommended options, in this case force disable this option
    if (lz4CompressionEnabled && !lz4Recommended)
    {
      lz4CompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionLz4Id, Convert.ToByte(lz4CompressionEnabled));
    }

    // handle the special case where QOI is enabled but is hidden by the setting to only show recommended options, in this case force disable this option
    if (qoiCompressionEnabled && !qoiRecommended)
    {
      qoiCompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(qoiCompressionEnabled));
    }

    // handle the special case where QOY is enabled but is hidden by the setting to only show recommended options, in this case force disable this option
    if (qoyCompressionEnabled && !qoyRecommended)
    {
      qoyCompressionEnabled = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(qoyCompressionEnabled));
    }

    // react to setting changes, avoid mixing incompatible settings
    if (jpegCompressionEnabled && !JpegCompression)
    {
      JpegCompression = jpegCompressionEnabled;
      QoiCompression = false;
      QoyCompression = false;
      QoirCompression = false;
      Lz4Compression = false;
      DensityCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
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
      QoirCompression = false;
      DensityCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
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
      QoirCompression = false;
      DensityCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoiId, Convert.ToByte(QoiCompression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoyId, Convert.ToByte(QoyCompression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(Lz4Compression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionQoirId, Convert.ToByte(QoirCompression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionDensityId, Convert.ToByte(DensityCompression));
    }
    else if (qoirCompressionEnabled && !QoirCompression)
    {
      QoirCompression = qoirCompressionEnabled;
      QoiCompression = false;
      QoyCompression = false;
      Lz4Compression = false;
      JpegCompression = false;
      DensityCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
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
      QoirCompression = false;
      DensityCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
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
      QoirCompression = false;
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyCompressionJpegId, Convert.ToByte(JpegCompression));
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
      QoirCompression = qoirCompressionEnabled;
      Lz4Compression = lz4CompressionEnabled;
      DensityCompression = densityCompressionEnabled;
    }

    // derive video format requirements from the settings
    if (QoiCompression || QoirCompression)
      RequireVideoFormats = RequiredVideoFormats[Beam.CompressionTypes.Qoi];
    else if (QoyCompression)
      RequireVideoFormats = RequiredVideoFormats[Beam.CompressionTypes.Qoy];
    else if (JpegCompression)
      RequireVideoFormats = RequiredVideoFormats[Beam.CompressionTypes.Jpeg];
    else
      RequireVideoFormats = null;

    var requiredVideoFormatConversion = GetRequiredVideoFormatConversion();
    if (requiredVideoFormatConversion != video_format.VIDEO_FORMAT_NONE)
    {
      var compressionFormatWarningProperty = ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionFormatWarningId);
      fixed (byte* propertyCompressionFormatWarningText = Module.ObsText("CompressionFormatWarningText", requiredVideoFormatConversion.ToString().Replace("VIDEO_FORMAT_", "")))
        ObsProperties.obs_property_set_description(compressionFormatWarningProperty, (sbyte*)propertyCompressionFormatWarningText);
      ObsProperties.obs_property_set_visible(compressionFormatWarningProperty, Convert.ToByte(true));
    }
    else
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionFormatWarningId), Convert.ToByte(false));

    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(Properties, (sbyte*)propertyCompressionMainThreadId), Convert.ToByte(QoiCompression || QoyCompression || QoirCompression || JpegCompression || Lz4Compression || DensityCompression));
  }
}

public unsafe void CheckNetworkInterfaces(obs_properties* properties, obs_data* settings)
{
  fixed (byte*
    propertyConnectionTypeSocketId = "connection_type_socket"u8,
    propertyNetworkInterfaceNoLocalAddressWarningId = "network_interface_no_local_address_warning_text"u8
  )
  {
    var connectionTypePipe = !Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypeSocketId));
    var networkInterfaceAddress = NetworkInterfaceAddress;
    Module.Log($"{UniquePrefix} Network interface set to: {NetworkInterfaceName}", ObsLogLevel.Info);
    bool showNoLocalAddressWarning = (!connectionTypePipe &&
                                      (((networkInterfaceAddress == IPAddress.Any) && !NetworkInterfacesHaveLocalAddress) ||
                                      ((networkInterfaceAddress != IPAddress.Any) && (!NetworkInterfaces.IsLocalAddress(networkInterfaceAddress)))));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceNoLocalAddressWarningId), Convert.ToByte(showNoLocalAddressWarning));
    if (showNoLocalAddressWarning)
      Module.Log($"{UniquePrefix} {NetworkInterfaceName}: {Module.ObsTextString("NetworkInterfaceNoLocalAddressWarningText")}", ObsLogLevel.Warning);
  }
}
#endregion Event handler helper functions

#region Event handlers
[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte EnableChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  fixed (byte* propertyEnableId = "enable"u8)
  {
    var isEnabled = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyEnableId));
    var senderProperties = GetProperties(properties);
    senderProperties.EventHandlerNeedSenderRestartCheck("EnableChangedEventHandler");
    senderProperties.RestartSenderIfNecessary(settings);
  }
  return Convert.ToByte(false);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte AutomaticListenPortEnabledChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  fixed (byte*
    propertyConnectionTypePipeId = "connection_type_pipe"u8,
    propertyAutomaticListenPortId = "auto_listen_port"u8,
    propertyListenPortId = "listen_port"u8
  )
  {
    var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
    var automaticListenPort = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyAutomaticListenPortId));
    var senderProperties = GetProperties(properties);
    Module.Log($"{senderProperties.UniquePrefix} Automatic listen port enabled: {automaticListenPort}", ObsLogLevel.Debug);
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!connectionTypePipe && !automaticListenPort));
    senderProperties.EventHandlerNeedSenderRestartCheck("AutomaticListenPortEnabledChangedEventHandler");
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
    var automaticListenPort = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyAutomaticListenPortId));
    ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(!connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyAutomaticListenPortId), Convert.ToByte(!connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceListId), Convert.ToByte(!connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!connectionTypePipe && !automaticListenPort));
    var senderProperties = GetProperties(properties);
    senderProperties.CheckNetworkInterfaces(properties, settings);
    Module.Log($"{senderProperties.UniquePrefix} Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
    senderProperties.EventHandlerNeedSenderRestartCheck("ConnectionTypePipeChangedEventHandler");
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
    var automaticListenPort = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyAutomaticListenPortId));
    ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyAutomaticListenPortId), Convert.ToByte(!connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceListId), Convert.ToByte(!connectionTypePipe));
    ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyListenPortId), Convert.ToByte(!connectionTypePipe && !automaticListenPort));
    var senderProperties = GetProperties(properties);
    senderProperties.CheckNetworkInterfaces(properties, settings);
    Module.Log($"{senderProperties.UniquePrefix} Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
    senderProperties.EventHandlerNeedSenderRestartCheck("ConnectionTypeSocketChangedEventHandler");
    return Convert.ToByte(true);
  }
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte NetworkInterfaceChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  var senderProperties = GetProperties(properties);
  senderProperties.CheckNetworkInterfaces(properties, settings);
  senderProperties.EventHandlerNeedSenderRestartCheck("NetworkInterfaceChangedEventHandler");
  return Convert.ToByte(true);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte CompressionSettingChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  string propertyName = Marshal.PtrToStringUTF8((IntPtr)ObsProperties.obs_property_name(prop))!;
  var senderProperties = GetProperties(properties);
  senderProperties.UpdateCompressionSettings(settings);
  senderProperties.EventHandlerNeedSenderRestartCheck("CompressionSettingChangedEventHandler: " + propertyName);
  return Convert.ToByte(true);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte CompressionJpegQualitySettingChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  GetProperties(properties).EventHandlerNeedSenderRestartCheck("CompressionJpegQualitySettingChangedEventHandler");
  return Convert.ToByte(false);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte CompressionMainThreadChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  GetProperties(properties).EventHandlerNeedSenderRestartCheck("CompressionMainThreadChangedEventHandler");
  return Convert.ToByte(false);
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
public static unsafe byte IdentifierSettingChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
{
  GetProperties(properties).EventHandlerNeedSenderRestartCheck("IdentifierSettingChangedEventHandler");
  return Convert.ToByte(false);
}
  #endregion Event handlers
}
