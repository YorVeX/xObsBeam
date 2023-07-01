// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ObsInterop;

namespace xObsBeam;

public class Source
{
  public unsafe struct Context
  {
    public uint SourceId;
    public obs_data* Settings;
    public obs_source* Source;
    public obs_source_frame* Video;
    public obs_source_audio* Audio;
  }

  #region Class fields
  static uint _sourceCount;
  static readonly ConcurrentDictionary<uint, Source> _sourceList = new();
  #endregion Class fields

  #region Instance fields
  readonly BeamReceiver BeamReceiver = new();
  IntPtr ContextPointer;
  uint[] _videoPlaneSizes = Array.Empty<uint>();
  uint _audioPlaneSize;

  #endregion Instance fields

  #region Helper methods
  public static unsafe void Register()
  {
    var sourceInfo = new obs_source_info();
    fixed (byte* id = "Beam Source"u8)
    {
      sourceInfo.id = (sbyte*)id;
      sourceInfo.type = obs_source_type.OBS_SOURCE_TYPE_INPUT;
      sourceInfo.icon_type = obs_icon_type.OBS_ICON_TYPE_CUSTOM;
      sourceInfo.output_flags = ObsSource.OBS_SOURCE_ASYNC_VIDEO | ObsSource.OBS_SOURCE_AUDIO;
      sourceInfo.get_name = &source_get_name;
      sourceInfo.create = &source_create;
      sourceInfo.get_width = &source_get_width;
      sourceInfo.get_height = &source_get_height;
      sourceInfo.show = &source_show;
      sourceInfo.hide = &source_hide;
      sourceInfo.destroy = &source_destroy;
      sourceInfo.get_defaults = &source_get_defaults;
      sourceInfo.get_properties = &source_get_properties;
      sourceInfo.update = &source_update;
      sourceInfo.save = &source_save;
      ObsSource.obs_register_source_s(&sourceInfo, (nuint)sizeof(obs_source_info));
    }
  }

  private static unsafe Source GetSource(void* data)
  {
    var context = (Context*)data;
    return _sourceList[(*context).SourceId];
  }

  public unsafe string NetworkInterfaceName
  {
    get
    {
      fixed (byte* propertyNetworkInterfaceListId = "network_interface_list"u8)
        return Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(((Context*)ContextPointer)->Settings, (sbyte*)propertyNetworkInterfaceListId))!;
    }
  }

  public unsafe IPAddress NetworkInterfaceAddress
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

  private unsafe void Connect()
  {
    var context = (Context*)ContextPointer;
    var settings = context->Settings;

    fixed (byte*
      propertyFrameBufferTimeId = "frame_buffer_time"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPortId = "port"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyPeerDiscoveryAvailablePipeFeedsId = "available_pipe_feeds_list"u8,
      propertyPeerDiscoveryAvailableSocketFeedsId = "available_socket_feeds_list"u8
    )
    {
      BeamReceiver.FrameBufferTimeMs = (int)ObsData.obs_data_get_int(settings, (sbyte*)propertyFrameBufferTimeId);

      /*
        this audio reset helps to prevent OBS increasing the audio buffer under some circumstances, e.g. when
        - restarting a feed within the frame buffer time
        - increasing the frame buffer time on an already active feed (source is being shown)
      */
      context->Audio->timestamp = 0;
      context->Audio->samples_per_sec = 48000;
      context->Audio->speakers = speaker_layout.SPEAKERS_STEREO;
      context->Audio->format = audio_format.AUDIO_FORMAT_FLOAT;
      context->Audio->frames = 0;
      Obs.obs_source_output_audio(context->Source, context->Audio);

      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      if (connectionTypePipe)
      {
        string targetPipeName = "";
        if (useManualConnectionSettings)
          targetPipeName = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyTargetPipeNameId))!;
        else
        {
          string availableFeedsListSelection = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailablePipeFeedsId))!;
          if (string.IsNullOrEmpty(availableFeedsListSelection) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
          {
            Module.Log("No feed selected to connect to.", ObsLogLevel.Error);
            return;
          }
          targetPipeName = availableFeedsListSelection;
        }
        if (string.IsNullOrEmpty(targetPipeName))
          targetPipeName = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetPipeNameId))!;
        BeamReceiver.Connect(targetPipeName);
      }
      else
      {
        string targetHost = "";
        int targetPort = 0;
        if (useManualConnectionSettings)
        {
          targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyTargetHostId))!;
          targetPort = (int)ObsData.obs_data_get_int(settings, (sbyte*)propertyTargetPortId);
        }
        else
        {
          string availableFeedsListSelection = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId))!;
          if (string.IsNullOrEmpty(availableFeedsListSelection) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
          {
            Module.Log("No feed selected to connect to.", ObsLogLevel.Error);
            return;
          }
          var availableFeedsListSelectionIdentiferSplit = availableFeedsListSelection.Split(PeerDiscovery.StringSeparator, StringSplitOptions.TrimEntries);
          if (availableFeedsListSelectionIdentiferSplit.Length == 3)
          {
            var availableFeedsListSelectionHostPortSplit = availableFeedsListSelectionIdentiferSplit[2].Split(':', StringSplitOptions.TrimEntries);
            if (availableFeedsListSelectionHostPortSplit.Length == 2)
            {
              // do a fresh discovery based on the Beam identifier and the network interface ID, so that it also works when the IP address or port have changed
              var discoveredPeers = PeerDiscovery.Discover(availableFeedsListSelectionIdentiferSplit[0], availableFeedsListSelectionIdentiferSplit[1]).Result;
              if (discoveredPeers.Count > 0)
              {
                targetHost = discoveredPeers[0].IP;
                targetPort = discoveredPeers[0].Port;
              }
              else // if discovery failed, use the last known address information
              {
                targetHost = availableFeedsListSelectionHostPortSplit[0];
                targetPort = Convert.ToInt32(availableFeedsListSelectionHostPortSplit[1]);
              }
            }
          }
        }
        if (string.IsNullOrEmpty(targetHost))
          targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetHostId))!;
        if (targetPort == 0)
          targetPort = (int)ObsData.obs_data_get_default_int(settings, (sbyte*)propertyTargetPortId);
        BeamReceiver.Connect(NetworkInterfaceAddress, targetHost, targetPort);
      }
    }
  }

  private unsafe void DiscoverFeeds(obs_property* peerDiscoveryAvailablePipeFeedsList, obs_property* peerDiscoveryAvailableSocketFeedsList, obs_property* peerDiscoveryIdentifierConflictWarningProperty)
  {
    var context = (Context*)ContextPointer;
    var settings = context->Settings;

    var discoveredPeers = PeerDiscovery.Discover().Result;
    bool foundPipePeers = false;
    bool foundSocketPeers = false;
    bool foundExactPreviousSocketPeer = false;
    bool foundConflicts = false;
    var pipePeerItemValues = new List<string>();
    var socketPeerItemValues = new List<string>();

    fixed (byte*
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyPeerDiscoveryAvailablePipeFeedsId = "available_pipe_feeds_list"u8,
      propertyPeerDiscoveryAvailableSocketFeedsId = "available_socket_feeds_list"u8
    )
    {
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));

      // variables needed to restore the previous selection in the lists, even if an updated IP and/or port were detected through discovery
      var previousSocketFeedsListSelectionValue = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId))!;
      var previousSocketFeedsListSelectionItems = previousSocketFeedsListSelectionValue.Split(PeerDiscovery.StringSeparator, StringSplitOptions.TrimEntries);
      var previousPeerSocketItemUniqueIdentifier = (previousSocketFeedsListSelectionItems.Length >= 3 ? previousSocketFeedsListSelectionItems[0] + PeerDiscovery.StringSeparator + previousSocketFeedsListSelectionItems[1] : "");
      var newSocketFeedsListSelectionValue = "";
      var previousSocketFeedsListIpAndPort = previousSocketFeedsListSelectionItems[3].Split(':', StringSplitOptions.TrimEntries);
      var previousSocketFeedsListSelectionName = (previousSocketFeedsListIpAndPort.Length == 2 ? $"{previousSocketFeedsListSelectionItems[0]} [{previousSocketFeedsListSelectionItems[2]}] / {previousSocketFeedsListIpAndPort[0]}:{previousSocketFeedsListIpAndPort[1]}" : "");

      ObsProperties.obs_property_list_clear(peerDiscoveryAvailablePipeFeedsList);
      ObsProperties.obs_property_list_clear(peerDiscoveryAvailableSocketFeedsList);
      foreach (var peer in discoveredPeers)
      {
        var peerSocketItemName = $"{peer.Identifier} [{peer.ServiceType}] / {peer.IP}:{peer.Port}";
        var peerSocketItemValue = $"{peer.Identifier}{PeerDiscovery.StringSeparator}{peer.InterfaceId}{PeerDiscovery.StringSeparator}{peer.ServiceType}{PeerDiscovery.StringSeparator}{peer.IP}:{peer.Port}";
        fixed (byte*
          peerListPipeItemName = Encoding.UTF8.GetBytes($"{peer.Identifier} [{peer.ServiceType}]"),
          peerListPipeItemValue = Encoding.UTF8.GetBytes(peer.Identifier),
          peerListSocketItemName = Encoding.UTF8.GetBytes(peerSocketItemName),
          peerListSocketItemValue = Encoding.UTF8.GetBytes(peerSocketItemValue)
        )
        {
          if (peer.ConnectionType == PeerDiscovery.ConnectionTypes.Pipe)
          {
            foundPipePeers = true;
            ObsProperties.obs_property_list_add_string(peerDiscoveryAvailablePipeFeedsList, (sbyte*)peerListPipeItemName, (sbyte*)peerListPipeItemValue);
            if (pipePeerItemValues.Contains(peer.Identifier))
            {
              foundConflicts = true;
              Module.Log("Peer Discovery: found duplicate pipe peer: " + peer.Identifier, ObsLogLevel.Warning);
            }
            else
              pipePeerItemValues.Add(peer.Identifier);
          }
          else if (peer.ConnectionType == PeerDiscovery.ConnectionTypes.Socket)
          {
            if (previousSocketFeedsListSelectionValue == peerSocketItemValue)
              foundExactPreviousSocketPeer = true;
            string peerSocketItemUniqueIdentifier = $"{peer.Identifier}{PeerDiscovery.StringSeparator}{peer.InterfaceId}";
            if (previousPeerSocketItemUniqueIdentifier == peerSocketItemUniqueIdentifier) // this matches even when IP and/or port have changed, so we can restore the previous selection, but with updated IP/port
              newSocketFeedsListSelectionValue = peerSocketItemValue;
            foundSocketPeers = true;
            ObsProperties.obs_property_list_add_string(peerDiscoveryAvailableSocketFeedsList, (sbyte*)peerListSocketItemName, (sbyte*)peerListSocketItemValue);
            if (socketPeerItemValues.Contains(peerSocketItemUniqueIdentifier))
            {
              foundConflicts = true;
              Module.Log("Peer Discovery: found duplicate socket peer: \"" + peerSocketItemUniqueIdentifier + "\"", ObsLogLevel.Warning);
            }
            else
              socketPeerItemValues.Add(peerSocketItemUniqueIdentifier);
          }
        }
      }
      fixed (byte*
        noFeedsListItem = Module.ObsText("PeerDiscoveryNoFeedsFoundText"),
        noFeedSelectedListItem = Module.ObsText("PeerDiscoveryNoFeedSelectedText"),
        propertyPeerDiscoveryIdentifierConflictWarningid = "identifier_conflict_warning"u8
      )
      {
        ObsProperties.obs_property_set_visible(peerDiscoveryIdentifierConflictWarningProperty, Convert.ToByte(foundConflicts));

        if (foundPipePeers)
          ObsProperties.obs_property_list_insert_string(peerDiscoveryAvailablePipeFeedsList, 0, (sbyte*)noFeedSelectedListItem, (sbyte*)noFeedSelectedListItem);
        else
          ObsProperties.obs_property_list_add_string(peerDiscoveryAvailablePipeFeedsList, (sbyte*)noFeedsListItem, (sbyte*)noFeedsListItem);
        if (foundSocketPeers)
          ObsProperties.obs_property_list_insert_string(peerDiscoveryAvailableSocketFeedsList, 0, (sbyte*)noFeedSelectedListItem, (sbyte*)noFeedSelectedListItem);
        else
          ObsProperties.obs_property_list_add_string(peerDiscoveryAvailableSocketFeedsList, (sbyte*)noFeedsListItem, (sbyte*)noFeedsListItem);

        // was a peer previously configured that wasn't discovered anymore this time?
        if (!foundExactPreviousSocketPeer && (previousSocketFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) && (previousSocketFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
        {
          // then make this transparent by adding this as a disabled list item
          fixed (byte* previousSocketFeedsListSelectionValueBytes = Encoding.UTF8.GetBytes(previousSocketFeedsListSelectionValue), previousSocketFeedsListSelectionNameBytes = Encoding.UTF8.GetBytes(previousSocketFeedsListSelectionName))
            ObsProperties.obs_property_list_insert_string(peerDiscoveryAvailableSocketFeedsList, 1, (sbyte*)previousSocketFeedsListSelectionNameBytes, (sbyte*)previousSocketFeedsListSelectionValueBytes);
          ObsProperties.obs_property_list_item_disable(peerDiscoveryAvailableSocketFeedsList, 1, Convert.ToByte(true));
        }

        // did we find a selection that matches the previous one, but with changed IP and/or port?
        if (foundSocketPeers && (newSocketFeedsListSelectionValue != "") && (newSocketFeedsListSelectionValue != previousSocketFeedsListSelectionValue))
        {
          if (useManualConnectionSettings)
          {
            // for manual connection the user wouldn't expect their connection settings to be changed automatically by discovery results,
            // reset the list to no selection instead to indicate that the previous setting is not valid anymore
            ObsData.obs_data_set_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId, (sbyte*)noFeedSelectedListItem);
          }
          else
          {
            // in discovery mode the user would expect the connection settings to be updated automatically, so we do that here
            fixed (byte* propertyNewSocketFeedsListSelectionValue = Encoding.UTF8.GetBytes(newSocketFeedsListSelectionValue))
              ObsData.obs_data_set_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId, (sbyte*)propertyNewSocketFeedsListSelectionValue);
          }
        }
      }
    }
  }

  #endregion Helper methods

  #region Source API methods
#pragma warning disable IDE1006
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* source_get_name(void* data)
  {
    Module.Log("source_get_name called", ObsLogLevel.Debug);
    fixed (byte* sourceName = "Beam"u8)
      return (sbyte*)sourceName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* source_create(obs_data* settings, obs_source* source)
  {
    Module.Log("source_create called", ObsLogLevel.Debug);
    Context* context = (Context*)Marshal.AllocCoTaskMem(sizeof(Context));
    context->Settings = settings;
    context->Source = source;
    context->Video = ObsBmem.bzalloc<obs_source_frame>();
    context->Audio = ObsBmem.bzalloc<obs_source_audio>();
    context->SourceId = ++_sourceCount;
    var thisSource = new Source();
    _sourceList.TryAdd(context->SourceId, thisSource);
    thisSource.ContextPointer = (IntPtr)context;
    thisSource.BeamReceiver.VideoFrameReceived += thisSource.VideoFrameReceivedEventHandler;
    thisSource.BeamReceiver.AudioFrameReceived += thisSource.AudioFrameReceivedEventHandler;
    thisSource.BeamReceiver.Disconnected += thisSource.DisconnectedEventHandler;
    return context;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_destroy(void* data)
  {
    Module.Log("source_destroy called", ObsLogLevel.Debug);
    var thisSource = GetSource(data);
    thisSource.BeamReceiver.Disconnect();
    thisSource.BeamReceiver.Disconnected -= thisSource.DisconnectedEventHandler;
    thisSource.BeamReceiver.VideoFrameReceived -= thisSource.VideoFrameReceivedEventHandler;
    thisSource.BeamReceiver.AudioFrameReceived -= thisSource.AudioFrameReceivedEventHandler;
    var context = (Context*)data;
    ObsBmem.bfree(context->Video);
    ObsBmem.bfree(context->Audio);
    Marshal.FreeCoTaskMem((IntPtr)context);
    Module.Log("source_destroy finished", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_show(void* data)
  {
    Module.Log("source_show called", ObsLogLevel.Debug);

    // the activate/deactivate events are not triggered by Studio Mode, so we need to connect/disconnect in show/hide events if the source should also work in Studio Mode
    GetSource(data).Connect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_hide(void* data)
  {
    Module.Log("source_hide called", ObsLogLevel.Debug);
    // the activate/deactivate events are not triggered by Studio Mode, so we need to connect/disconnect in show/hide events if the source should also work in Studio Mode
    GetSource(data).BeamReceiver.Disconnect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_properties* source_get_properties(void* data)
  {
    Module.Log("source_get_properties called", ObsLogLevel.Debug);

    var properties = ObsProperties.obs_properties_create();
    ObsProperties.obs_properties_set_flags(properties, ObsProperties.OBS_PROPERTIES_DEFER_UPDATE);
    fixed (byte*
      propertyFrameBufferTimeId = "frame_buffer_time"u8,
      propertyFrameBufferTimeCaption = Module.ObsText("FrameBufferTimeCaption"),
      propertyFrameBufferTimeText = Module.ObsText("FrameBufferTimeText"),
      propertyFrameBufferTimeMemoryUsageInfoId = "frame_buffer_time_info"u8,
      propertyFrameBufferTimeMemoryUsageInfoText = Module.ObsText("FrameBufferTimeMemoryUsageInfoText"),
      propertyFrameBufferTimeSuffix = " ms"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPipeNameCaption = Module.ObsText("TargetPipeNameCaption"),
      propertyTargetPipeNameText = Module.ObsText("TargetPipeNameText"),
      propertyTargetHostId = "host"u8,
      propertyTargetHostCaption = Module.ObsText("TargetHostCaption"),
      propertyTargetHostText = Module.ObsText("TargetHostText"),
      propertyTargetPortId = "port"u8,
      propertyTargetPortCaption = Module.ObsText("TargetPortCaption"),
      propertyTargetPortText = Module.ObsText("TargetPortText"),
      propertyConnectionTypeId = "connection_type"u8,
      propertyConnectionTypeCaption = Module.ObsText("ConnectionTypeCaption"),
      propertyConnectionTypeText = Module.ObsText("ConnectionTypeText"),
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypePipeCaption = Module.ObsText("ConnectionTypePipeCaption"),
      propertyConnectionTypePipeText = Module.ObsText("ConnectionTypePipeText"),
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyConnectionTypeSocketCaption = Module.ObsText("ConnectionTypeSocketCaption"),
      propertyConnectionTypeSocketText = Module.ObsText("ConnectionTypeSocketText"),
      propertyPeerDiscoveryAvailablePipeFeedsId = "available_pipe_feeds_list"u8,
      propertyPeerDiscoveryAvailableSocketFeedsId = "available_socket_feeds_list"u8,
      propertyPeerDiscoveryAvailableFeedsCaption = Module.ObsText("PeerDiscoveryAvailableFeedsCaption"),
      propertyPeerDiscoveryAvailableFeedsText = Module.ObsText("PeerDiscoveryAvailableFeedsText"),
      propertyPeerDiscoveryIdentifierConflictWarningid = "identifier_conflict_warning"u8,
      propertyPeerDiscoveryIdentifierConflictWarningText = Module.ObsText("PeerDiscoveryIdentifierConflictWarningText"),
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyManualConnectionSettingsText = Module.ObsText("ManualConnectionSettingsText"),
      propertyManualConnectionSettingsCaption = Module.ObsText("ManualConnectionSettingsCaption"),
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyNetworkInterfaceListCaption = Module.ObsText("NetworkInterfaceListCaption"),
      propertyNetworkInterfaceListText = Module.ObsText("NetworkInterfaceListText")
    )
    {
      // frame buffer
      var frameBufferTimeProperty = ObsProperties.obs_properties_add_int_slider(properties, (sbyte*)propertyFrameBufferTimeId, (sbyte*)propertyFrameBufferTimeCaption, 0, 5000, 100);
      ObsProperties.obs_property_set_long_description(frameBufferTimeProperty, (sbyte*)propertyFrameBufferTimeText);
      ObsProperties.obs_property_int_set_suffix(frameBufferTimeProperty, (sbyte*)propertyFrameBufferTimeSuffix);
      // frame buffer time memory usage info
      var frameBufferTimeMemoryUsageInfoProperty = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyFrameBufferTimeMemoryUsageInfoId, (sbyte*)propertyFrameBufferTimeMemoryUsageInfoText, obs_text_type.OBS_TEXT_INFO);
      ObsProperties.obs_property_set_description(frameBufferTimeMemoryUsageInfoProperty, (sbyte*)propertyFrameBufferTimeMemoryUsageInfoText);

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

      // discovered peer feeds lists
      var peerDiscoveryAvailablePipeFeedsList = ObsProperties.obs_properties_add_list(properties, (sbyte*)propertyPeerDiscoveryAvailablePipeFeedsId, (sbyte*)propertyPeerDiscoveryAvailableFeedsCaption, obs_combo_type.OBS_COMBO_TYPE_LIST, obs_combo_format.OBS_COMBO_FORMAT_STRING);
      var peerDiscoveryAvailableSocketFeedsList = ObsProperties.obs_properties_add_list(properties, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId, (sbyte*)propertyPeerDiscoveryAvailableFeedsCaption, obs_combo_type.OBS_COMBO_TYPE_LIST, obs_combo_format.OBS_COMBO_FORMAT_STRING);
      ObsProperties.obs_property_set_long_description(peerDiscoveryAvailablePipeFeedsList, (sbyte*)propertyPeerDiscoveryAvailableFeedsText);
      ObsProperties.obs_property_set_long_description(peerDiscoveryAvailableSocketFeedsList, (sbyte*)propertyPeerDiscoveryAvailableFeedsText);
      ObsProperties.obs_property_set_modified_callback(peerDiscoveryAvailablePipeFeedsList, &PeerDiscoveryAvailablePipeFeedsListChangedEventHandler);
      ObsProperties.obs_property_set_modified_callback(peerDiscoveryAvailableSocketFeedsList, &PeerDiscoveryAvailableSocketFeedsListChangedEventHandler);

      // warning message shown when there is a conflict with peer identifier names
      var peerDiscoveryIdentifierConflictWarning = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyPeerDiscoveryIdentifierConflictWarningid, (sbyte*)propertyPeerDiscoveryIdentifierConflictWarningText, obs_text_type.OBS_TEXT_INFO);
      ObsProperties.obs_property_text_set_info_type(peerDiscoveryIdentifierConflictWarning, obs_text_info_type.OBS_TEXT_INFO_WARNING);

      // start peer discovery in the background while continuing to add properties elements
      var discoveryTask = Task.Run(() => GetSource(data).DiscoverFeeds(peerDiscoveryAvailablePipeFeedsList, peerDiscoveryAvailableSocketFeedsList, peerDiscoveryIdentifierConflictWarning));

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

      // manual connection settings checkbox
      var manualConnectionSettingsProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyManualConnectionSettingsId, (sbyte*)propertyManualConnectionSettingsCaption);
      ObsProperties.obs_property_set_long_description(manualConnectionSettingsProperty, (sbyte*)propertyManualConnectionSettingsText);
      ObsProperties.obs_property_set_modified_callback(manualConnectionSettingsProperty, &ManualConnectionSettingsChangedEventHandler);

      // target socket/pipe server address
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyTargetHostText);

      // target pipe name
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeNameCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyTargetPipeNameText);

      // target socket port
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_int(properties, (sbyte*)propertyTargetPortId, (sbyte*)propertyTargetPortCaption, 1024, 65535, 1), (sbyte*)propertyTargetPortText);

      discoveryTask.Wait();
    }
    return properties;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_get_defaults(obs_data* settings)
  {
    Module.Log("source_get_defaults called", ObsLogLevel.Debug);
    fixed (byte*
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPipeNameDefaultText = "BeamSender"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetHostDefaultText = "127.0.0.1"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyTargetPortId = "port"u8
    )
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(true));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(false));
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeNameDefaultText);
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostDefaultText);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyTargetPortId, BeamSender.DefaultPort);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_update(void* data, obs_data* settings)
  {
    Module.Log("source_update called", ObsLogLevel.Debug);
    var thisSource = GetSource(data);
    if (thisSource.BeamReceiver.IsConnected)
      thisSource.BeamReceiver.Disconnect();
    if (Convert.ToBoolean(Obs.obs_source_showing(((Context*)data)->Source))) // auto-reconnect if the source is visible
      thisSource.Connect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_save(void* data, obs_data* settings)
  {
    Module.Log("source_save called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe uint source_get_width(void* data)
  {
    return GetSource(data).BeamReceiver.Width;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe uint source_get_height(void* data)
  {
    return GetSource(data).BeamReceiver.Height;
  }
#pragma warning restore IDE1006
  #endregion Source API methods

  #region Event handlers

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte FrameBufferTimeChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte* propertyFrameBufferTimeId = "frame_buffer_time"u8)
    {
      var frameBufferTime = ObsData.obs_data_get_int(settings, (sbyte*)propertyFrameBufferTimeId);
      if (frameBufferTime < 0)
        ObsData.obs_data_set_int(settings, (sbyte*)propertyFrameBufferTimeId, 0);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypePipeChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyManualConnectionSettingsId = "manual_connection_settings"u8
    )
    {
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(!connectionTypePipe));
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      ConnectionTypeChanged(connectionTypePipe, useManualConnectionSettings, properties);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypeSocketChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyManualConnectionSettingsId = "manual_connection_settings"u8
    )
    {
      var connectionTypePipe = !Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypeSocketId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(connectionTypePipe));
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      ConnectionTypeChanged(connectionTypePipe, useManualConnectionSettings, properties);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte PeerDiscoveryAvailablePipeFeedsListChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyPeerDiscoveryAvailableFeedsId = "available_pipe_feeds_list"u8,
      propertyTargetPipeNameId = "pipe_name"u8
    )
    {
      // if not in manual mode only this list is relevant, don't overwrite the data in the manual fields
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      if (!useManualConnectionSettings)
        return Convert.ToByte(false);

      // if in manual mode this list is a helper to fill the manual fields
      string availableFeedsListSelection = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableFeedsId))!;
      if (string.IsNullOrEmpty(availableFeedsListSelection) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
        return Convert.ToByte(false);
      fixed (byte* propertyTargetPipeName = Encoding.UTF8.GetBytes(availableFeedsListSelection))
        ObsData.obs_data_set_string(settings, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeName);
    }
    return Convert.ToByte(true);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte PeerDiscoveryAvailableSocketFeedsListChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyPeerDiscoveryAvailableFeedsId = "available_socket_feeds_list"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetPortId = "port"u8
    )
    {
      // if not in manual mode only this list is relevant, don't overwrite the data in the manual fields
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      if (!useManualConnectionSettings)
        return Convert.ToByte(false);

      // if in manual mode this list is a helper to fill the manual fields
      string availableFeedsListSelection = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableFeedsId))!;
      if (string.IsNullOrEmpty(availableFeedsListSelection) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
        return Convert.ToByte(false);
      var availableFeedsListSelectionIdentiferSplit = availableFeedsListSelection.Split(PeerDiscovery.StringSeparator, StringSplitOptions.TrimEntries);
      if (availableFeedsListSelectionIdentiferSplit.Length != 4)
        return Convert.ToByte(false);
      var availableFeedsListSelectionHostPortSplit = availableFeedsListSelectionIdentiferSplit[3].Split(':', StringSplitOptions.TrimEntries);
      if (availableFeedsListSelectionHostPortSplit.Length != 2)
        return Convert.ToByte(false);
      fixed (byte* propertyTargetHostText = Encoding.UTF8.GetBytes(availableFeedsListSelectionHostPortSplit[0]))
        ObsData.obs_data_set_string(settings, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostText);
      ObsData.obs_data_set_int(settings, (sbyte*)propertyTargetPortId, Convert.ToInt32(availableFeedsListSelectionHostPortSplit[1]));
    }
    return Convert.ToByte(true);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ManualConnectionSettingsChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyDiscoveredPeersListId = "discovered_peers_list"u8
    )
    {
      var useManualConnectionSettings = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyManualConnectionSettingsId));
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      ConnectionTypeChanged(connectionTypePipe, useManualConnectionSettings, properties);
    }
    return Convert.ToByte(true);
  }

  private static unsafe void ConnectionTypeChanged(bool connectionTypePipe, bool useManualConnectionSettings, obs_properties* properties)
  {
    fixed (byte*
      propertyTargetHostId = "host"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPortId = "port"u8,
      propertyNetworkInterfaceListId = "network_interface_list"u8,
      propertyPeerDiscoveryAvailablePipeFeedsId = "available_pipe_feeds_list"u8,
      propertyPeerDiscoveryAvailableSocketFeedsId = "available_socket_feeds_list"u8
    )
    {
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetHostId), Convert.ToByte(!connectionTypePipe && useManualConnectionSettings));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetPipeNameId), Convert.ToByte(connectionTypePipe && useManualConnectionSettings));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceListId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetPortId), Convert.ToByte(!connectionTypePipe && useManualConnectionSettings));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyPeerDiscoveryAvailablePipeFeedsId), Convert.ToByte(connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId), Convert.ToByte(!connectionTypePipe));
      Module.Log("Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
    }
  }

  private unsafe void DisconnectedEventHandler(object? sender, EventArgs e)
  {
    var context = (Context*)ContextPointer;

    // reset video output
    Obs.obs_source_output_video(context->Source, null);
    context->Video->format = video_format.VIDEO_FORMAT_NONE; // make sure the source is reinitialized on the next frame

    if (Convert.ToBoolean(Obs.obs_source_showing(context->Source))) // auto-reconnect if the source is visible
    {
      Task.Delay(1000).ContinueWith(_ =>
      {
        if (Convert.ToBoolean(Obs.obs_source_showing(context->Source))) // some time has passed, check again whether the source is still visible
          Connect(); // reconnect
      });
    }
  }
  private unsafe void VideoFrameReceivedEventHandler(object? sender, Beam.BeamVideoData videoFrame)
  {
    var context = (Context*)ContextPointer;

    // did the frame format or size change?
    if ((context->Video->width != videoFrame.Header.Width) || (context->Video->height != videoFrame.Header.Height) || (context->Video->format != videoFrame.Header.Format) || (context->Video->full_range != Convert.ToByte(videoFrame.Header.Range == video_range_type.VIDEO_RANGE_FULL)))
    {
      Module.Log($"VideoFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Video->format} {(Convert.ToBoolean(context->Video->full_range) ? "FULL" : "LIMITED")} {context->Video->width}x{context->Video->height} -> {videoFrame.Header.Format} {((videoFrame.Header.Range == video_range_type.VIDEO_RANGE_FULL) ? "FULL" : "LIMITED")} {videoFrame.Header.Width}x{videoFrame.Header.Height})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Video->format = videoFrame.Header.Format;
      context->Video->width = videoFrame.Header.Width;
      context->Video->height = videoFrame.Header.Height;
      context->Video->full_range = Convert.ToByte(videoFrame.Header.Range == video_range_type.VIDEO_RANGE_FULL);
      // get the plane sizes for the current frame format and size
      if (videoFrame.Header.Compression == Beam.CompressionTypes.JpegLossy)
      {
        EncoderSupport.GetJpegPlaneSizes(context->Video->format, (int)context->Video->width, (int)context->Video->height, out _videoPlaneSizes, out var jpeglineSize);
        for (int i = 0; i < Beam.VideoHeader.MAX_AV_PLANES; i++)
        {
          context->Video->linesize[i] = jpeglineSize[i];
          Module.Log("VideoFrameReceivedEventHandler(): linesize[" + i + "] = " + context->Video->linesize[i], ObsLogLevel.Debug);
        }
      }
      else
      {
        for (int i = 0; i < Beam.VideoHeader.MAX_AV_PLANES; i++)
        {
          context->Video->linesize[i] = videoFrame.Header.Linesize[i];
          Module.Log("VideoFrameReceivedEventHandler(): linesize[" + i + "] = " + context->Video->linesize[i], ObsLogLevel.Debug);
        }
        _videoPlaneSizes = Beam.GetPlaneSizes(context->Video->format, context->Video->height, context->Video->linesize);
      }
      ObsVideo.video_format_get_parameters_for_format(videoFrame.Header.Colorspace, videoFrame.Header.Range, videoFrame.Header.Format, context->Video->color_matrix, context->Video->color_range_min, context->Video->color_range_max);
      Module.Log("VideoFrameReceivedEventHandler(): reinitialized", ObsLogLevel.Debug);
    }

    if (_videoPlaneSizes.Length == 0) // unsupported format
      return;

    context->Video->timestamp = videoFrame.Header.Timestamp;

    fixed (byte* videoData = videoFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_video() creates a copy of the data anyway
    {
      // video data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane sizes
      uint currentOffset = 0;
      for (int planeIndex = 0; planeIndex < _videoPlaneSizes.Length; planeIndex++)
      {
        context->Video->data[planeIndex] = videoData + currentOffset;
        currentOffset += _videoPlaneSizes[planeIndex];
      }
      // Module.Log($"VideoFrameReceivedEventHandler(): Output timestamp {videoFrame.Header.Timestamp}", ObsLogLevel.Debug);
      Obs.obs_source_output_video(context->Source, context->Video);
    }
    BeamReceiver.RawDataBufferPool.Return(videoFrame.Data);
  }

  private unsafe void AudioFrameReceivedEventHandler(object? sender, Beam.BeamAudioData audioFrame)
  {
    var context = (Context*)ContextPointer;

    // did the frame format or size change?
    if ((context->Audio->samples_per_sec != audioFrame.Header.SampleRate) || (context->Audio->frames != audioFrame.Header.Frames) || (context->Audio->speakers != audioFrame.Header.Speakers) || (context->Audio->format != audioFrame.Header.Format))
    {
      Module.Log($"AudioFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Audio->format} {context->Audio->samples_per_sec} {context->Audio->speakers} {context->Audio->frames} -> {audioFrame.Header.Format} {audioFrame.Header.SampleRate} {audioFrame.Header.Speakers} {audioFrame.Header.Frames})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Audio->samples_per_sec = audioFrame.Header.SampleRate;
      context->Audio->speakers = audioFrame.Header.Speakers;
      context->Audio->format = audioFrame.Header.Format;
      context->Audio->frames = audioFrame.Header.Frames;
      // calculate the plane size for the current frame format and size
      Beam.GetAudioDataSize(audioFrame.Header.Format, audioFrame.Header.Speakers, audioFrame.Header.Frames, out _, out int audioBytesPerSample);
      _audioPlaneSize = (uint)audioBytesPerSample * audioFrame.Header.Frames;
      Module.Log("AudioFrameReceivedEventHandler(): reinitialized", ObsLogLevel.Debug);
    }

    context->Audio->timestamp = audioFrame.Header.Timestamp;

    fixed (byte* audioData = audioFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_audio() creates a copy of the data anyway
    {
      // audio data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane sizes
      uint currentOffset = 0;
      for (int speakerIndex = 0; speakerIndex < (int)audioFrame.Header.Speakers; speakerIndex++)
      {
        context->Audio->data[speakerIndex] = audioData + currentOffset;
        currentOffset += _audioPlaneSize;
      }
      // Module.Log($"AudioFrameReceivedEventHandler(): Output timestamp {audioFrame.Header.Timestamp}", ObsLogLevel.Debug);
      Obs.obs_source_output_audio(context->Source, context->Audio);
    }

    return;
  }
  #endregion Event handlers
}
