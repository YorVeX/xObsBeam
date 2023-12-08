// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace xObsBeam;

/// <summary>
/// This class globally provides a cached list of network interfaces available in the system that is constantly updated in the background
/// based on NetworkInformation.NetworkAvailabilityChanged and NetworkInformation.NetworkAddressChanged events.
/// The main motivation of having this is that NetworkInterface.GetAllNetworkInterfaces() calls are rather slow (250 ms or even a lot more), but the results are needed quickly,
/// and on the other hand the list of network interfaces is not expected to change often.
/// </summary>
public static partial class NetworkInterfaces
{

  private static NetworkInterface[] _networkInterfaces = [];
  private static List<(UnicastIPAddressInformation, string)> _unicastAddressesWithIds = [];
  private static List<IPAddress> _multicastInterfaceIps = [];
  private static readonly object _networkInterfacesLock = new();

  public static NetworkInterface[] AllNetworkInterfaces
  {
    get
    {
      lock (_networkInterfacesLock)
        return _networkInterfaces;
    }
    private set
    {
      lock (_networkInterfacesLock)
        _networkInterfaces = value;
    }
  }

  public static List<IPAddress> MulticastInterfaceIps
  {
    get
    {
      lock (_networkInterfacesLock)
        return _multicastInterfaceIps;
    }
    private set
    {
      lock (_networkInterfacesLock)
        _multicastInterfaceIps = value;
    }
  }

  public static List<(UnicastIPAddressInformation, string)> UnicastAddressesWithIds
  {
    get
    {
      lock (_networkInterfacesLock)
        return _unicastAddressesWithIds;
    }
    private set
    {
      lock (_networkInterfacesLock)
        _unicastAddressesWithIds = value;
    }
  }

  // this is not called before the first method from this class was used, meaning the first call to GetAllNetworkInterfaces() or GetUnicastAddressesWithIds() will implicitly invoke this
  static NetworkInterfaces()
  {
    UpdateNetworkInterfaces();
    NetworkChange.NetworkAvailabilityChanged += (sender, e) =>
    {
      Module.Log($"Received NetworkAvailabilityChanged event.", ObsLogLevel.Debug);
      UpdateNetworkInterfaces();
    };
    NetworkChange.NetworkAddressChanged += (sender, e) =>
    {
      Module.Log($"Received NetworkAddressChanged event.", ObsLogLevel.Debug);
      UpdateNetworkInterfaces();
    };
  }

  public static void UpdateNetworkInterfaces()
  {
    var networkInterfacesWithIds = new List<(UnicastIPAddressInformation, string)>();
    var multicastInterfaceIps = new List<IPAddress>();
    Module.Log($"Refreshing list of network interfaces...", ObsLogLevel.Debug);
    var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
    foreach (var networkInterface in networkInterfaces)
    {
      if (networkInterface.OperationalStatus == OperationalStatus.Up)
      {
        var ipProperties = networkInterface.GetIPProperties();
        foreach (var ip in ipProperties.UnicastAddresses)
        {
          if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
          {
            // remember working unicast addresses (this includes localhost and virtual interfaces)
            string identifierString = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ? "localhost" : networkInterface.GetPhysicalAddress().ToString();
            if (string.IsNullOrEmpty(identifierString))
              identifierString = networkInterface.Name;
            string hashIdentifier = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identifierString))).Replace("-", "");
            networkInterfacesWithIds.Add((ip, hashIdentifier));
            // Module.Log("NIC: \"" + networkInterface.Name + "\": " + ip.Address + " / " + identifierString + " / " + hashIdentifier, ObsLogLevel.Debug);

            // remember multicast capable interfaces (this does not include localhost and virtual interfaces)
            if ((networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback) && networkInterface.SupportsMulticast && (ipProperties.MulticastAddresses.Count > 0))
              multicastInterfaceIps.Add(ip.Address);
          }
        }
      }
    }
    lock (_networkInterfacesLock)
    {
      _networkInterfaces = networkInterfaces;
      _unicastAddressesWithIds = networkInterfacesWithIds;
      _multicastInterfaceIps = multicastInterfaceIps;
    }
    Module.Log($"Refreshing list of network interfaces done.", ObsLogLevel.Debug);
  }

  // source: https://regex101.com/r/JCLOZL/15
  [GeneratedRegex(@"\b(127\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|0?10\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|172\.0?1[6-9]\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|172\.0?2[0-9]\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|172\.0?3[01]\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|192\.168\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|169\.254\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)|::1|[fF][cCdD][0-9a-fA-F]{2}(?:[:][0-9a-fA-F]{0,4}){0,7}|[fF][eE][89aAbB][0-9a-fA-F](?:[:][0-9a-fA-F]{0,4}){0,7})(?:\/([789]|1?[0-9]{2}))?\b")]
  private static partial Regex RegexLocalAddress();

  public static bool IsLocalAddress(IPAddress address)
  {
    return (IPAddress.IsLoopback(address) || RegexLocalAddress().IsMatch(address.ToString()));
  }
}
