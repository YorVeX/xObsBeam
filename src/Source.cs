// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
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
    public obs_source* TimestampFilter;
    public bool TimestampFilterAdded;
    public int ReceiveDelay;
    public int RenderDelay;
  }

  public unsafe struct FilterContext
  {
    public uint SourceId;
    public obs_source* FilterSource;
  }

  #region Class fields
  static uint _sourceCount;
  static readonly ConcurrentDictionary<uint, Source> _sourceList = new();
  #endregion Class fields

  #region Source instance fields
  public bool IsAudioOnly;
  readonly BeamReceiver BeamReceiver = new();
  public bool NetworkInterfacesHaveLocalAddress { get; private set; }
  IntPtr ContextPointer;
  ulong CurrentVideoTimestamp;
  ulong CurrentAudioTimestamp;
  int RenderDelayLimit;
  Beam.VideoPlaneInfo _videoPlaneInfo;
  int _audioPlanes;
  uint _audioBytesPerChannel;
  #endregion Source instance fields

  #region Helper methods
  public static unsafe void Register()
  {
    var sourceInfo = ObsBmem.bzalloc<obs_source_info>();
    fixed (byte* id = "Beam Source"u8)
    {
      sourceInfo->id = (sbyte*)id;
      sourceInfo->type = obs_source_type.OBS_SOURCE_TYPE_INPUT;
      sourceInfo->icon_type = obs_icon_type.OBS_ICON_TYPE_CUSTOM;
      sourceInfo->output_flags = ObsSource.OBS_SOURCE_ASYNC_VIDEO | ObsSource.OBS_SOURCE_AUDIO;
      sourceInfo->get_name = &source_get_name;
      sourceInfo->create = &source_create;
      sourceInfo->get_width = &source_get_width;
      sourceInfo->get_height = &source_get_height;
      sourceInfo->show = &source_show;
      sourceInfo->hide = &source_hide;
      sourceInfo->destroy = &source_destroy;
      sourceInfo->get_defaults = &source_get_defaults;
      sourceInfo->get_properties = &source_get_properties;
      sourceInfo->video_tick = &source_video_tick;
      sourceInfo->update = &source_update;
      sourceInfo->save = &source_save;
      ObsSource.obs_register_source_s(sourceInfo, (nuint)sizeof(obs_source_info));
    }
    ObsBmem.bfree(sourceInfo);

    var filterInfo = ObsBmem.bzalloc<obs_source_info>();
    fixed (byte* id = "Beam Timestamp Filter"u8)
    {
      filterInfo->id = (sbyte*)id;
      filterInfo->type = obs_source_type.OBS_SOURCE_TYPE_FILTER;
      filterInfo->output_flags = ObsSource.OBS_SOURCE_ASYNC_VIDEO | ObsSource.OBS_SOURCE_AUDIO | ObsSource.OBS_SOURCE_CAP_DISABLED;
      filterInfo->get_name = &filter_get_name;
      filterInfo->create = &filter_create;
      filterInfo->destroy = &filter_destroy;
      filterInfo->get_properties = &filter_get_properties;
      filterInfo->filter_video = &filter_video;
      filterInfo->filter_audio = &filter_audio;
      filterInfo->filter_remove = &filter_remove;
      ObsSource.obs_register_source_s(filterInfo, (nuint)sizeof(obs_source_info));
    }
    ObsBmem.bfree(filterInfo);
  }

  private static unsafe Source GetSource(obs_source* source)
  {
    return _sourceList.First(x => (((Context*)x.Value.ContextPointer)->Source == source)).Value;
  }

  private static unsafe Source GetSource(obs_data* settings)
  {
    return _sourceList.First(x => (((Context*)x.Value.ContextPointer)->Settings == settings)).Value;
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
      Module.Log($"Didn't find configured network interface \"{configuredNetworkInterfaceName}\", falling back to loopback interface.", ObsLogLevel.Error);
      return IPAddress.Loopback;
    }
  }

  private unsafe void Connect()
  {
    var context = (Context*)ContextPointer;
    var settings = context->Settings;

    CurrentVideoTimestamp = 0;
    CurrentAudioTimestamp = 0;
    context->ReceiveDelay = -1;
    context->RenderDelay = -1;

    fixed (byte*
      propertyRenderDelayLimitId = "render_delay_limit"u8,
      propertyFrameBufferTimeId = "frame_buffer_time"u8,
      propertyFrameBufferTimeFixedRenderDelayId = "frame_buffer_time_fixed_render_delay"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPortId = "port"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyManualConnectionSettingsId = "manual_connection_settings"u8,
      propertyPeerDiscoveryAvailablePipeFeedsId = "available_pipe_feeds_list"u8,
      propertyPeerDiscoveryAvailableSocketFeedsId = "available_socket_feeds_list"u8
    )
    {
      RenderDelayLimit = (int)ObsData.obs_data_get_int(settings, (sbyte*)propertyRenderDelayLimitId);
      BeamReceiver.FrameBufferTimeMs = (int)ObsData.obs_data_get_int(settings, (sbyte*)propertyFrameBufferTimeId);
      BeamReceiver.FrameBufferFixedDelay = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyFrameBufferTimeFixedRenderDelayId));

      context->Audio->format = audio_format.AUDIO_FORMAT_UNKNOWN; // make sure the first audio frame triggers a (re)initialization
      _audioPlanes = 0;
      _audioBytesPerChannel = 0;

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
          try
          {
            targetPipeName = PeerDiscovery.Peer.FromListItemValue(availableFeedsListSelection).Identifier;
          }
          catch
          {
          }
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
          if (string.IsNullOrEmpty(targetHost))
            targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetHostId))!;
          if (targetPort == 0)
            targetPort = (int)ObsData.obs_data_get_default_int(settings, (sbyte*)propertyTargetPortId);
          BeamReceiver.Connect(NetworkInterfaceAddress, targetHost, targetPort);
        }
        else
        {
          string availableFeedsListSelection = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId))!;
          if (string.IsNullOrEmpty(availableFeedsListSelection) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) || (availableFeedsListSelection == Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
          {
            Module.Log("No feed selected to connect to.", ObsLogLevel.Error);
            return;
          }
          try
          {
            var selectedPeer = PeerDiscovery.Peer.FromListItemValue(availableFeedsListSelection);
            BeamReceiver.Connect(NetworkInterfaceAddress, selectedPeer.IP, selectedPeer.Port, selectedPeer);
          }
          catch
          {
            if (string.IsNullOrEmpty(targetHost))
              targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetHostId))!;
            if (targetPort == 0)
              targetPort = (int)ObsData.obs_data_get_default_int(settings, (sbyte*)propertyTargetPortId);
            BeamReceiver.Connect(NetworkInterfaceAddress, targetHost, targetPort);
          }
        }
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
    bool foundExactPreviousPipePeer = false;
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
      var previousPipeFeedsListSelectionValue = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailablePipeFeedsId))!;
      var previousSocketFeedsListSelectionValue = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyPeerDiscoveryAvailableSocketFeedsId))!;
      PeerDiscovery.Peer previousSocketPeer;
      try
      {
        previousSocketPeer = PeerDiscovery.Peer.FromListItemValue(previousSocketFeedsListSelectionValue);
      }
      catch
      {
        previousSocketPeer = default;
      }
      PeerDiscovery.Peer previousPipePeer;
      try
      {
        previousPipePeer = PeerDiscovery.Peer.FromListItemValue(previousPipeFeedsListSelectionValue);
      }
      catch
      {
        previousPipePeer = default;
      }
      var previousPeerSocketItemUniqueIdentifier = previousSocketPeer.SocketUniqueIdentifier;
      var previousPipeFeedsListSelectionName = previousPipePeer.PipeListItemName;
      var previousSocketFeedsListSelectionName = previousSocketPeer.SocketListItemName;
      var newSocketFeedsListSelectionValue = "";

      ObsProperties.obs_property_list_clear(peerDiscoveryAvailablePipeFeedsList);
      ObsProperties.obs_property_list_clear(peerDiscoveryAvailableSocketFeedsList);
      nuint pipePeerIndex = 0;
      nuint socketPeerIndex = 0;
      foreach (var peer in discoveredPeers)
      {
        var peerSocketItemName = peer.SocketListItemName;
        var peerSocketItemValue = peer.SocketListItemValue;
        var peerPipeItemValue = peer.PipeListItemValue;
        bool versionMatch = (peer.SenderVersionString == Module.ModuleVersionString);
        string versionMismatchText = "";
        if (!versionMatch)
          versionMismatchText = " (" + Module.ObsTextString("PeerDiscoveryVersionMismatchText") + ")";
        fixed (byte*
          peerListPipeItemName = Encoding.UTF8.GetBytes(peer.PipeListItemName + versionMismatchText),
          peerListPipeItemValue = Encoding.UTF8.GetBytes(peerPipeItemValue),
          peerListSocketItemName = Encoding.UTF8.GetBytes(peerSocketItemName + versionMismatchText),
          peerListSocketItemValue = Encoding.UTF8.GetBytes(peerSocketItemValue)
        )
        {
          if (peer.ConnectionType == PeerDiscovery.ConnectionTypes.Pipe)
          {
            foundPipePeers = true;
            if (versionMatch && (previousPipeFeedsListSelectionValue == peerPipeItemValue))
              foundExactPreviousPipePeer = true;
            ObsProperties.obs_property_list_add_string(peerDiscoveryAvailablePipeFeedsList, (sbyte*)peerListPipeItemName, (sbyte*)peerListPipeItemValue);
            if (!versionMatch)
              ObsProperties.obs_property_list_item_disable(peerDiscoveryAvailablePipeFeedsList, pipePeerIndex, Convert.ToByte(true));
            pipePeerIndex++;
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
            foundSocketPeers = true;
            if (versionMatch && (previousSocketFeedsListSelectionValue == peerSocketItemValue))
              foundExactPreviousSocketPeer = true;
            string peerSocketItemUniqueIdentifier = peer.SocketUniqueIdentifier;
            if (versionMatch && (previousPeerSocketItemUniqueIdentifier == peerSocketItemUniqueIdentifier)) // this matches even when IP and/or port have changed, so we can restore the previous selection, but with updated IP/port
              newSocketFeedsListSelectionValue = peerSocketItemValue;
            ObsProperties.obs_property_list_add_string(peerDiscoveryAvailableSocketFeedsList, (sbyte*)peerListSocketItemName, (sbyte*)peerListSocketItemValue);
            if (!versionMatch)
              ObsProperties.obs_property_list_item_disable(peerDiscoveryAvailableSocketFeedsList, socketPeerIndex, Convert.ToByte(true));
            socketPeerIndex++;
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

        // was a pipe peer previously configured that wasn't discovered anymore this time?
        if (!foundExactPreviousPipePeer && !string.IsNullOrEmpty(previousPipeFeedsListSelectionValue) && (previousPipeFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) && (previousPipeFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
        {
          // then make this transparent by adding this as a disabled list item
          fixed (byte* listItemName = Encoding.UTF8.GetBytes(previousPipeFeedsListSelectionName), listItemValue = Encoding.UTF8.GetBytes(previousPipeFeedsListSelectionValue))
            ObsProperties.obs_property_list_insert_string(peerDiscoveryAvailablePipeFeedsList, 1, (sbyte*)listItemName, (sbyte*)listItemValue);
          ObsProperties.obs_property_list_item_disable(peerDiscoveryAvailablePipeFeedsList, 1, Convert.ToByte(true));
        }

        // was a socket peer previously configured that wasn't discovered anymore this time?
        if (!foundExactPreviousSocketPeer && !string.IsNullOrEmpty(previousSocketFeedsListSelectionValue) && (previousSocketFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedsFoundText")) && (previousSocketFeedsListSelectionValue != Module.ObsTextString("PeerDiscoveryNoFeedSelectedText")))
        {
          // then make this transparent by adding this as a disabled list item
          fixed (byte* listItemName = Encoding.UTF8.GetBytes(previousSocketFeedsListSelectionName), listItemValue = Encoding.UTF8.GetBytes(previousSocketFeedsListSelectionValue))
            ObsProperties.obs_property_list_insert_string(peerDiscoveryAvailableSocketFeedsList, 1, (sbyte*)listItemName, (sbyte*)listItemValue);
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

  public unsafe void CheckNetworkInterfaces(obs_properties* properties, obs_data* settings)
  {
    fixed (byte*
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyNetworkInterfaceNoLocalAddressWarningId = "network_interface_no_local_address_warning_text"u8
    )
    {
      var connectionTypePipe = !Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypeSocketId));
      var networkInterfaceAddress = NetworkInterfaceAddress;
      Module.Log($"Network interface set to: {NetworkInterfaceName}", ObsLogLevel.Info);
      bool showNoLocalAddressWarning = (!connectionTypePipe &&
                                        (((networkInterfaceAddress == IPAddress.Any) && !NetworkInterfacesHaveLocalAddress) ||
                                        ((networkInterfaceAddress != IPAddress.Any) && (!NetworkInterfaces.IsLocalAddress(networkInterfaceAddress)))));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyNetworkInterfaceNoLocalAddressWarningId), Convert.ToByte(showNoLocalAddressWarning));
      if (showNoLocalAddressWarning)
        Module.Log($"{NetworkInterfaceName}: {Module.ObsTextString("NetworkInterfaceNoLocalAddressWarningText")}", ObsLogLevel.Warning);
    }
  }
  #endregion Helper methods

#pragma warning disable IDE1006

  #region Timestamp measure filter API methods
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* filter_get_name(void* data)
  {
    fixed (byte* sourceName = "Beam Timestamp Helper"u8)
      return (sbyte*)sourceName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* filter_create(obs_data* settings, obs_source* source)
  {
    Module.Log("filter_create called", ObsLogLevel.Debug);
    var context = ObsBmem.bzalloc<FilterContext>();
    context->FilterSource = source;
    context->SourceId = 0;
    return context;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_destroy(void* data)
  {
    Module.Log("filter_destroy called", ObsLogLevel.Debug);
    ObsBmem.bfree(data);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_properties* filter_get_properties(void* data)
  {
    var properties = ObsProperties.obs_properties_create();
    fixed (byte*
      propertyFilterTimestampHelperId = "filter_timestamp_helper_text"u8,
      propertyFilterTimestampHelperText = Module.ObsText("FilterTimestampHelperText")
    )
    {
      ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyFilterTimestampHelperId, (sbyte*)propertyFilterTimestampHelperText, obs_text_type.OBS_TEXT_INFO);
    }
    return properties;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_source_frame* filter_video(void* data, obs_source_frame* frame)
  {
    // Module.Log("filter_video called with timestamp " + frame->timestamp, ObsLogLevel.Debug);
    var filterContext = (FilterContext*)data;
    if (filterContext->SourceId == 0)
    {
      //TODO: move this initialization to filter_add as soon as an OBS (probably the next after 29.1.3) with this new callback has been released: https://github.com/obsproject/obs-studio/commit/a494cf5ce493b77af682e4c4e2a64302d2ecc393
      // background: obs_filter_get_parent() is not guaranteed to work in filter_create, but it should be in filter_add, and then we don't need this check on every frame anymore
      var parentSource = Obs.obs_filter_get_parent(filterContext->FilterSource);
      if (parentSource != null)
        filterContext->SourceId = ((Context*)GetSource(parentSource).ContextPointer)->SourceId;
    }
    if (filterContext->SourceId > 0)
      _sourceList[filterContext->SourceId].CurrentVideoTimestamp = frame->timestamp;
    return frame;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_audio_data* filter_audio(void* data, obs_audio_data* frame)
  {
    // Module.Log("filter_audio called with timestamp " + frame->timestamp, ObsLogLevel.Debug);
    var filterContext = (FilterContext*)data;
    if (filterContext->SourceId == 0)
    {
      //TODO: move this initialization to filter_add as soon as an OBS (probably the next after 29.1.3) with this new callback has been released: https://github.com/obsproject/obs-studio/commit/a494cf5ce493b77af682e4c4e2a64302d2ecc393
      // background: obs_filter_get_parent() is not guaranteed to work in filter_create, but it should be in filter_add, and then we don't need this check on every frame anymore
      var parentSource = Obs.obs_filter_get_parent(filterContext->FilterSource);
      if (parentSource != null)
        filterContext->SourceId = ((Context*)GetSource(parentSource).ContextPointer)->SourceId;
    }
    if (filterContext->SourceId > 0)
      _sourceList[filterContext->SourceId].CurrentAudioTimestamp = frame->timestamp;
    return frame;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void filter_remove(void* data, obs_source* source)
  {
    Module.Log("filter_remove called", ObsLogLevel.Debug);
    var filterContext = (FilterContext*)data;
    if (filterContext->SourceId == 0)
    {
      var parentSource = Obs.obs_filter_get_parent(filterContext->FilterSource);
      if (parentSource != null)
        filterContext->SourceId = ((Context*)GetSource(parentSource).ContextPointer)->SourceId;
    }
    if (filterContext->SourceId > 0)
    {
      _sourceList[filterContext->SourceId].CurrentVideoTimestamp = 0;
      _sourceList[filterContext->SourceId].CurrentAudioTimestamp = 0;
      filterContext->SourceId = 0;
    }
  }
  #endregion Timestamp measure filter API methods

  #region Source API methods
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* source_get_name(void* data)
  {
    Module.Log("source_get_name called", ObsLogLevel.Debug);
    fixed (byte* sourceName = "Beam Receiver"u8)
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
    context->TimestampFilter = null;
    context->TimestampFilterAdded = false;
    context->ReceiveDelay = -1;
    context->RenderDelay = -1;
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
    if (context->TimestampFilterAdded) // if it was created by us during this session it needs to be released by us to prevent a memory in the log on exit - otherwise if it was already there from last session OBS will take care of that (and then releasing it here could even cause a crash)
      Obs.obs_source_release(context->TimestampFilter);
    Marshal.FreeCoTaskMem((IntPtr)context);
    Module.Log("source_destroy finished", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_show(void* data)
  {
    Module.Log("source_show called", ObsLogLevel.Debug);

    // add the timestamp helper filter if it doesn't exist yet
    ensureTimestampHelperFilterExists(data);

    // the activate/deactivate events are not triggered by Studio Mode, so we need to connect/disconnect in show/hide events if the source should also work in Studio Mode
    GetSource(data).Connect();
  }

  public static unsafe void ensureTimestampHelperFilterExists(void* data)
  {
    Context* context = (Context*)data;
    context->TimestampFilter = null;
    Obs.obs_source_enum_filters(context->Source, &sourceEnumFiltersFindTimestampHelperFilter, data);
    if (context->TimestampFilter == null) // timestamp helper filter hasn't been added yet to this source, add it now
    {
      Module.Log("Timestamp helper filter not found, adding.", ObsLogLevel.Debug);
      fixed (byte* id = "Beam Timestamp Filter"u8)
        context->TimestampFilter = Obs.obs_source_create_private((sbyte*)id, (sbyte*)id, null);
      Obs.obs_source_filter_add(context->Source, context->TimestampFilter);
      context->TimestampFilterAdded = true;
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void sourceEnumFiltersFindTimestampHelperFilter(obs_source* parent, obs_source* child, void* data)
  {
    Context* context = (Context*)data;
    if (Marshal.PtrToStringUTF8((IntPtr)Obs.obs_source_get_name(child)) == "Beam Timestamp Filter")
    {
      context->TimestampFilter = child;
      context->TimestampFilterAdded = false;
    }
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

    var thisSource = GetSource(data);

    var properties = ObsProperties.obs_properties_create();
    ObsProperties.obs_properties_set_flags(properties, ObsProperties.OBS_PROPERTIES_DEFER_UPDATE);
    fixed (byte*
      propertyRenderDelayLimitId = "render_delay_limit"u8,
      propertyRenderDelayLimitCaption = Module.ObsText("RenderDelayLimitCaption"),
      propertyRenderDelayLimitText = Module.ObsText("RenderDelayLimitText"),
      propertyRenderDelayLimitSuffix = " ms"u8,
      propertyRenderDelayLimitBelowFrameBufferTimeWarningId = "render_delay_limit_below_frame_buffer_time_warning"u8,
      propertyRenderDelayLimitBelowFrameBufferTimeWarningText = Module.ObsText("RenderDelayLimitBelowFrameBufferTimeWarningText"),
      propertyReceiveAndRenderDelayId = "receive_and_render_delay"u8,
      propertyReceiveAndRenderDelayCaption = Module.ObsText("ReceiveAndRenderDelayCaption"),
      propertyReceiveAndRenderDelayText = Module.ObsText("ReceiveAndRenderDelayText", "--", "--"),
      propertyReceiveAndRenderDelayRefreshButtonId = "receive_and_render_delay_refresh_button"u8,
      propertyReceiveAndRenderDelayRefreshButtonCaption = Module.ObsText("ReceiveAndRenderDelayRefreshButtonCaption"),
      propertyReceiveAndRenderDelayRefreshButtonText = Module.ObsText("ReceiveAndRenderDelayRefreshButtonText"),
      propertyFrameBufferTimeId = "frame_buffer_time"u8,
      propertyFrameBufferTimeCaption = Module.ObsText("FrameBufferTimeCaption"),
      propertyFrameBufferTimeText = Module.ObsText("FrameBufferTimeText"),
      propertyFrameBufferTimeMemoryUsageInfoId = "frame_buffer_time_info"u8,
      propertyFrameBufferTimeMemoryUsageInfoText = Module.ObsText("FrameBufferTimeMemoryUsageInfoText"),
      propertyFrameBufferTimeSuffix = " ms"u8,
      propertyFrameBufferTimeFixedRenderDelayId = "frame_buffer_time_fixed_render_delay"u8,
      propertyFrameBufferTimeFixedRenderDelayCaption = Module.ObsText("FrameBufferTimeFixedRenderDelayCaption"),
      propertyFrameBufferTimeFixedRenderDelayText = Module.ObsText("FrameBufferTimeFixedRenderDelayText"),
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
      propertyNetworkInterfaceListText = Module.ObsText("NetworkInterfaceListText"),
      propertyNetworkInterfaceNoLocalAddressWarningId = "network_interface_no_local_address_warning_text"u8,
      propertyNetworkInterfaceNoLocalAddressWarningText = Module.ObsText("NetworkInterfaceNoLocalAddressWarningText")
    )
    {
      // render delay limit
      var renderDelayLimitProperty = ObsProperties.obs_properties_add_int_slider(properties, (sbyte*)propertyRenderDelayLimitId, (sbyte*)propertyRenderDelayLimitCaption, 0, 5000, 50);
      ObsProperties.obs_property_set_long_description(renderDelayLimitProperty, (sbyte*)propertyRenderDelayLimitText);
      ObsProperties.obs_property_int_set_suffix(renderDelayLimitProperty, (sbyte*)propertyRenderDelayLimitSuffix);
      ObsProperties.obs_property_set_modified_callback(renderDelayLimitProperty, &RenderDelayLimitOrFrameBufferChangedEventHandler);
      // warning if the render delay is set below the frame buffer time
      var renderDelayLimitBelowFrameBufferTimeWarningProperty = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyRenderDelayLimitBelowFrameBufferTimeWarningId, (sbyte*)propertyRenderDelayLimitBelowFrameBufferTimeWarningText, obs_text_type.OBS_TEXT_INFO);
      ObsProperties.obs_property_text_set_info_type(renderDelayLimitBelowFrameBufferTimeWarningProperty, obs_text_info_type.OBS_TEXT_INFO_WARNING);
      ObsProperties.obs_property_set_visible(renderDelayLimitBelowFrameBufferTimeWarningProperty, Convert.ToByte(false));

      // label that can display the current delays for an active feed
      var receiveAndRenderDelayProperty = ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyReceiveAndRenderDelayId, (sbyte*)propertyReceiveAndRenderDelayCaption, obs_text_type.OBS_TEXT_INFO);
      ObsProperties.obs_property_set_long_description(receiveAndRenderDelayProperty, (sbyte*)propertyReceiveAndRenderDelayText);
      var receiveAndRenderDelayRefreshButton = ObsProperties.obs_properties_add_button(properties, (sbyte*)propertyReceiveAndRenderDelayRefreshButtonId, (sbyte*)propertyReceiveAndRenderDelayRefreshButtonCaption, &ReceiveAndRenderDelayRefreshButtonClickedEventHandler);
      ObsProperties.obs_property_set_long_description(receiveAndRenderDelayRefreshButton, (sbyte*)propertyReceiveAndRenderDelayRefreshButtonText);

      // frame buffer
      var frameBufferTimeProperty = ObsProperties.obs_properties_add_int_slider(properties, (sbyte*)propertyFrameBufferTimeId, (sbyte*)propertyFrameBufferTimeCaption, 0, 5000, 100);
      ObsProperties.obs_property_set_long_description(frameBufferTimeProperty, (sbyte*)propertyFrameBufferTimeText);
      ObsProperties.obs_property_int_set_suffix(frameBufferTimeProperty, (sbyte*)propertyFrameBufferTimeSuffix);
      ObsProperties.obs_property_set_modified_callback(frameBufferTimeProperty, &RenderDelayLimitOrFrameBufferChangedEventHandler);
      // frame buffer time memory usage info
      ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyFrameBufferTimeMemoryUsageInfoId, (sbyte*)propertyFrameBufferTimeMemoryUsageInfoText, obs_text_type.OBS_TEXT_INFO);
      // frame buffer fixed render delay
      var frameBufferTimeFixedRenderDelayProperty = ObsProperties.obs_properties_add_bool(properties, (sbyte*)propertyFrameBufferTimeFixedRenderDelayId, (sbyte*)propertyFrameBufferTimeFixedRenderDelayCaption);
      ObsProperties.obs_property_set_long_description(frameBufferTimeFixedRenderDelayProperty, (sbyte*)propertyFrameBufferTimeFixedRenderDelayText);

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
      var discoveryTask = Task.Run(() => thisSource.DiscoverFeeds(peerDiscoveryAvailablePipeFeedsList, peerDiscoveryAvailableSocketFeedsList, peerDiscoveryIdentifierConflictWarning));

      // network interface selection
      var networkInterfacesListProperty = ObsProperties.obs_properties_add_list(properties, (sbyte*)propertyNetworkInterfaceListId, (sbyte*)propertyNetworkInterfaceListCaption, obs_combo_type.OBS_COMBO_TYPE_LIST, obs_combo_format.OBS_COMBO_FORMAT_STRING);
      ObsProperties.obs_property_set_long_description(networkInterfacesListProperty, (sbyte*)propertyNetworkInterfaceListText);
      fixed (byte* networkInterfaceAnyListItem = "Any: 0.0.0.0"u8)
        ObsProperties.obs_property_list_add_string(networkInterfacesListProperty, (sbyte*)networkInterfaceAnyListItem, (sbyte*)networkInterfaceAnyListItem);
      thisSource.NetworkInterfacesHaveLocalAddress = false;
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
              thisSource.NetworkInterfacesHaveLocalAddress = true;
            Module.Log($"Found network interface: {networkInterfaceDisplayName} (Local: {NetworkInterfaces.IsLocalAddress(ip.Address)})", ObsLogLevel.Debug);
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
      propertyTargetPipeNameDefaultText = "Beam Sender"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetHostDefaultText = "127.0.0.1"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyTargetPortId = "port"u8
    )
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(false));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(true));
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

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_video_tick(void* data, float seconds)
  {
    // Module.Log($"source_video_tick {Obs.obs_get_video_frame_time()} (+{seconds})", (((seconds * 1_000_000_000) > Obs.obs_get_frame_interval_ns()) ? ObsLogLevel.Warning : ObsLogLevel.Debug));
    if (!Convert.ToBoolean(Obs.obs_source_showing(((Context*)data)->Source))) // nothing to do if the source is not visible
      return;

    var thisSource = GetSource(data);
    if (thisSource != null)
    {
      if (thisSource.IsAudioOnly || (thisSource.BeamReceiver?.AudioBuffer != null))
      {
        if (thisSource.BeamReceiver?.AudioBuffer != null)
        {
          Task.Run(() =>
          {
            foreach (var frame in thisSource.BeamReceiver.AudioBuffer.GetNextFrames(seconds))
              thisSource.AudioFrameReceivedEventHandler(thisSource, frame);
          });
        }
      }
      else
      {
        if (thisSource.BeamReceiver?.FrameBuffer != null)
        {
          Task.Run(() =>
          {
            foreach (var frame in thisSource.BeamReceiver.FrameBuffer.GetNextFrames(seconds))
            {
              if (frame.Type is Beam.Type.Video or Beam.Type.VideoOnly)
                thisSource.VideoFrameReceivedEventHandler(thisSource, (Beam.BeamVideoData)frame);
              else if (frame.Type is Beam.Type.Audio or Beam.Type.AudioOnly)
                thisSource.AudioFrameReceivedEventHandler(thisSource, (Beam.BeamAudioData)frame);
            }
          });
        }
      }
    }
  }

#pragma warning restore IDE1006
  #endregion Source API methods

  #region Event handlers

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte RenderDelayLimitOrFrameBufferChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte*
      propertyRenderDelayLimitId = "render_delay_limit"u8,
      propertyFrameBufferTimeId = "frame_buffer_time"u8,
      propertyFrameBufferTimeFixedRenderDelayId = "frame_buffer_time_fixed_render_delay"u8,
      propertyRenderDelayLimitBelowFrameBufferTimeWarningId = "render_delay_limit_below_frame_buffer_time_warning"u8
    )
    {
      var renderDelayLimit = ObsData.obs_data_get_int(settings, (sbyte*)propertyRenderDelayLimitId);
      var frameBufferTime = ObsData.obs_data_get_int(settings, (sbyte*)propertyFrameBufferTimeId);
      var renderDelayLimitBelowFrameBufferTimeWarningProperty = ObsProperties.obs_properties_get(properties, (sbyte*)propertyRenderDelayLimitBelowFrameBufferTimeWarningId);
      var warningVisible = Convert.ToBoolean(ObsProperties.obs_property_visible(renderDelayLimitBelowFrameBufferTimeWarningProperty));
      var frameBufferTimeFixedRenderDelayProperty = ObsProperties.obs_properties_get(properties, (sbyte*)propertyFrameBufferTimeFixedRenderDelayId);
      var frameBufferTimeFixedRenderDelayPropertyVisible = Convert.ToBoolean(ObsProperties.obs_property_visible(frameBufferTimeFixedRenderDelayProperty));

      bool visibilityChanged = false;

      if (!warningVisible && (renderDelayLimit > 0) && (renderDelayLimit <= frameBufferTime))
      {
        visibilityChanged = true;
        ObsProperties.obs_property_set_visible(renderDelayLimitBelowFrameBufferTimeWarningProperty, Convert.ToByte(true));
      }
      else if (warningVisible && ((renderDelayLimit == 0) || (renderDelayLimit > frameBufferTime)))
      {
        visibilityChanged = true;
        ObsProperties.obs_property_set_visible(renderDelayLimitBelowFrameBufferTimeWarningProperty, Convert.ToByte(false));
      }

      if (frameBufferTimeFixedRenderDelayPropertyVisible && (frameBufferTime == 0))
      {
        visibilityChanged = true;
        ObsProperties.obs_property_set_visible(frameBufferTimeFixedRenderDelayProperty, Convert.ToByte(false));
      }
      else if (!frameBufferTimeFixedRenderDelayPropertyVisible && (frameBufferTime > 0))
      {
        visibilityChanged = true;
        ObsProperties.obs_property_set_visible(frameBufferTimeFixedRenderDelayProperty, Convert.ToByte(true));
      }

      return Convert.ToByte(visibilityChanged);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ReceiveAndRenderDelayRefreshButtonClickedEventHandler(obs_properties* properties, obs_property* prop, void* data)
  {
    var context = (Context*)data;
    Module.Log("ReceiveAndRenderDelayRefreshButtonClickedEventHandler called", ObsLogLevel.Debug);
    fixed (byte*
      propertyReceiveAndRenderDelayId = "receive_and_render_delay"u8,
      propertyReceiveAndRenderDelayText = Module.ObsText("ReceiveAndRenderDelayText", (context->ReceiveDelay < 0 ? "--" : context->ReceiveDelay), (context->RenderDelay < 0 ? "--" : context->RenderDelay)),
      propertyReceiveAndRenderDelayRefreshButtonId = "receive_and_render_delay_refresh_button"u8
    )
    {
      var receiveAndRenderDelayProperty = ObsProperties.obs_properties_get(properties, (sbyte*)propertyReceiveAndRenderDelayId);
      ObsProperties.obs_property_set_long_description(receiveAndRenderDelayProperty, (sbyte*)propertyReceiveAndRenderDelayText);
    }
    return Convert.ToByte(true);
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
      GetSource(settings).CheckNetworkInterfaces(properties, settings);
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
      GetSource(settings).CheckNetworkInterfaces(properties, settings);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte NetworkInterfaceChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    GetSource(settings).CheckNetworkInterfaces(properties, settings);
    return Convert.ToByte(true);
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
      PeerDiscovery.Peer selectedPeer;
      try
      {
        selectedPeer = PeerDiscovery.Peer.FromListItemValue(availableFeedsListSelection);
      }
      catch
      {
        return Convert.ToByte(false);
      }
      fixed (byte*
        propertyTargetPipeName = Encoding.UTF8.GetBytes(selectedPeer.Identifier),
        propertyNoFeedsSelectedText = Module.ObsText("PeerDiscoveryNoFeedSelectedText")
      )
      {
        ObsData.obs_data_set_string(settings, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeName);
        ObsData.obs_data_set_string(settings, (sbyte*)propertyPeerDiscoveryAvailableFeedsId, (sbyte*)propertyNoFeedsSelectedText); // reset the selection, as this was only used as a helper and isn't meant to be persisted
      }
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
      PeerDiscovery.Peer selectedPeer;
      try
      {
        selectedPeer = PeerDiscovery.Peer.FromListItemValue(availableFeedsListSelection);
      }
      catch
      {
        return Convert.ToByte(false);
      }

      fixed (byte*
        propertyTargetHostText = Encoding.UTF8.GetBytes(selectedPeer.IP),
        propertyNoFeedsSelectedText = Module.ObsText("PeerDiscoveryNoFeedSelectedText")
      )
      {
        ObsData.obs_data_set_string(settings, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostText);
        ObsData.obs_data_set_int(settings, (sbyte*)propertyTargetPortId, selectedPeer.Port);
        ObsData.obs_data_set_string(settings, (sbyte*)propertyPeerDiscoveryAvailableFeedsId, (sbyte*)propertyNoFeedsSelectedText); // reset the selection, as this was only used as a helper and isn't meant to be persisted
      }

      return Convert.ToByte(true);
    }
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
    context->ReceiveDelay = -1;
    context->RenderDelay = -1;

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
    if ((context->Video->width != videoFrame.Header.Width) || (context->Video->height != videoFrame.Header.Height) || (context->Video->format != videoFrame.Header.Format) || (context->Video->full_range != videoFrame.Header.FullRange))
    {
      Module.Log($"VideoFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Video->format} {videoFrame.Type} {(Convert.ToBoolean(context->Video->full_range) ? "FULL" : "LIMITED")} {context->Video->width}x{context->Video->height} -> {videoFrame.Header.Format} {(Convert.ToBoolean(videoFrame.Header.FullRange) ? "FULL" : "LIMITED")} {videoFrame.Header.Width}x{videoFrame.Header.Height})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Video->format = videoFrame.Header.Format;
      context->Video->width = videoFrame.Header.Width;
      context->Video->height = videoFrame.Header.Height;
      context->Video->full_range = videoFrame.Header.FullRange;
      new Span<uint>(context->Video->linesize, Beam.VideoHeader.MAX_AV_PLANES).Clear(); // initialize all linesizes to 0 first
      _videoPlaneInfo = Beam.GetVideoPlaneInfo(context->Video->format, context->Video->width, context->Video->height);
      for (int planeIndex = 0; planeIndex < _videoPlaneInfo.Count; planeIndex++)
      {
        context->Video->linesize[planeIndex] = _videoPlaneInfo.Linesize[planeIndex];
        Module.Log("VideoFrameReceivedEventHandler(): linesize[" + planeIndex + "] = " + _videoPlaneInfo.Linesize[planeIndex], ObsLogLevel.Debug);
      }
      new ReadOnlySpan<float>(videoFrame.Header.ColorMatrix, 16).CopyTo(new Span<float>(context->Video->color_matrix, 16));
      new ReadOnlySpan<float>(videoFrame.Header.ColorRangeMin, 3).CopyTo(new Span<float>(context->Video->color_range_min, 3));
      new ReadOnlySpan<float>(videoFrame.Header.ColorRangeMax, 3).CopyTo(new Span<float>(context->Video->color_range_max, 3));
      Module.Log("VideoFrameReceivedEventHandler(): reinitialized", ObsLogLevel.Debug);
      IsAudioOnly = false;
    }

    if (_videoPlaneInfo.Count == 0) // unsupported format
      return;

    if ((RenderDelayLimit > 0) && (videoFrame.RenderDelayAverage > RenderDelayLimit))
    {
      Task.Run(BeamReceiver.Disconnect);
      Module.Log($"Render delay limit of {RenderDelayLimit} ms exceeded ({videoFrame.RenderDelayAverage} ms), reconnecting.", ObsLogLevel.Warning);
    }
    else
    {
      if (CurrentVideoTimestamp > 0)
        BeamReceiver!.SetLastOutputFrameTimestamp(CurrentVideoTimestamp);

      context->Video->timestamp = videoFrame.AdjustedTimestamp;
      context->ReceiveDelay = videoFrame.Header.ReceiveDelay;
      context->RenderDelay = videoFrame.RenderDelayAverage;

      fixed (byte* videoData = videoFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_video() creates a copy of the data anyway
      {
        // video data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane sizes
        for (int planeIndex = 0; planeIndex < _videoPlaneInfo.Count; planeIndex++)
          context->Video->data[planeIndex] = videoData + _videoPlaneInfo.Offsets[planeIndex];
        // Module.Log($"VideoFrameReceivedEventHandler(): Output timestamp {videoFrame.Header.Timestamp}", ObsLogLevel.Debug);
        Obs.obs_source_output_video(context->Source, context->Video);
      }
    }
    BeamReceiver.RawDataBufferPool.Return(videoFrame.Data);
  }

  private unsafe void AudioFrameReceivedEventHandler(object? sender, Beam.BeamAudioData audioFrame)
  {
    var context = (Context*)ContextPointer;

    // did the frame format or size change?
    if ((context->Audio->samples_per_sec != audioFrame.Header.SampleRate) || (context->Audio->speakers != audioFrame.Header.Speakers) || (context->Audio->format != audioFrame.Header.Format))
    {
      Module.Log($"AudioFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Audio->format} {audioFrame.Type} {context->Audio->samples_per_sec} {context->Audio->speakers} {context->Audio->frames} -> {audioFrame.Header.Format} {audioFrame.Header.SampleRate} {audioFrame.Header.Speakers} {audioFrame.Header.Frames})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Audio->samples_per_sec = audioFrame.Header.SampleRate;
      context->Audio->speakers = audioFrame.Header.Speakers;
      context->Audio->format = audioFrame.Header.Format;
      // calculate the plane size for the current frame format and size
      Beam.GetAudioPlaneInfo(audioFrame.Header.Format, audioFrame.Header.Speakers, out _audioPlanes, out _audioBytesPerChannel);
      Module.Log($"AudioFrameReceivedEventHandler(): reinitialized with {_audioPlanes} audio planes and {_audioBytesPerChannel} bytes per channel", ObsLogLevel.Debug);
      IsAudioOnly = (audioFrame.Type == Beam.Type.AudioOnly);
    }

    if (IsAudioOnly)
    {
      context->ReceiveDelay = audioFrame.Header.ReceiveDelay;
      context->RenderDelay = audioFrame.RenderDelayAverage;
      if ((RenderDelayLimit > 0) && (audioFrame.RenderDelayAverage > RenderDelayLimit))
      {
        Task.Run(BeamReceiver.Disconnect);
        Module.Log($"Render delay limit of {RenderDelayLimit} ms exceeded ({audioFrame.RenderDelayAverage} ms), reconnecting.", ObsLogLevel.Warning);
        return;
      }
    }
    context->Audio->timestamp = audioFrame.AdjustedTimestamp;
    context->Audio->frames = audioFrame.Header.Frames;

    uint audioPlaneSize = _audioBytesPerChannel * audioFrame.Header.Frames;
    fixed (byte* audioData = audioFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_audio() creates a copy of the data anyway
    {
      if (IsAudioOnly && (CurrentAudioTimestamp > 0))
        BeamReceiver!.SetLastOutputFrameTimestamp(CurrentAudioTimestamp);

      uint currentOffset = 0;
      // audio data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane/channel layout
      for (int planeIndex = 0; planeIndex < _audioPlanes; planeIndex++)
      {
        context->Audio->data[planeIndex] = audioData + currentOffset;
        currentOffset += audioPlaneSize;
      }
      // Module.Log($"AudioFrameReceivedEventHandler(): Output timestamp {audioFrame.Header.Timestamp}", ObsLogLevel.Debug);
      Obs.obs_source_output_audio(context->Source, context->Audio);
    }

    return;
  }
  #endregion Event handlers
}
