// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace xObsBeam;

public class PeerDiscovery
{
  public enum ServiceTypes
  {
    Output,
    Filter,
  }

  public enum ConnectionTypes
  {
    Pipe,
    Socket,
  }

  public struct Peer
  {
    public string InterfaceId;
    public string Identifier;
    public ServiceTypes ServiceType;
    public ConnectionTypes ConnectionType;
    public string IP;
    public int Port;

    public string SocketUniqueIdentifier => (!IsEmpty ? $"{Identifier}{StringSeparator}{InterfaceId}" : "");

    public string SocketListItemName => (!IsEmpty ? $"{Identifier} [{ServiceType}] / {IP}:{Port}" : "");

    public string PipeListItemName => (!IsEmpty ? $"{Identifier} [{ServiceType}]" : "");

    public string SocketListItemValue => (!IsEmpty ? $"{Identifier}{StringSeparator}{InterfaceId}{StringSeparator}{ServiceType}{StringSeparator}{IP}:{Port}" : "");

    public string PipeListItemValue => (!IsEmpty ? $"{Identifier}{StringSeparator}{ServiceType}" : "");

    public static Peer FromListItemValue(string listItem)
    {
      var peer = new Peer();
      var items = listItem.Split(StringSeparator, StringSplitOptions.TrimEntries);
      if (items.Length is not 2 and not 4)
        throw new ArgumentException("Invalid list item string.");
      if (items.Length == 2) // Pipe peer
      {
        peer.ConnectionType = ConnectionTypes.Pipe;
        peer.Identifier = items[0];
        peer.InterfaceId = ConnectionTypes.Pipe.ToString();
        peer.ServiceType = Enum.Parse<ServiceTypes>(items[1]);
        peer.IP = "127.0.0.1";
        peer.Port = 0;
        return peer;
      }
      // Socket peer
      peer.ConnectionType = ConnectionTypes.Socket;
      peer.Identifier = items[0];
      peer.InterfaceId = items[1];
      peer.ServiceType = Enum.Parse<ServiceTypes>(items[2]);
      var ipPort = items[3].Split(':');
      peer.IP = ipPort[0];
      peer.Port = int.Parse(ipPort[1]);
      return peer;
    }

    public string ToMulticastString(string interfaceId, string ipAddress)
    {
      return MulticastPrefix + StringSeparator + "Service" + StringSeparator + interfaceId + StringSeparator + ipAddress + StringSeparator + Port + StringSeparator + ServiceType + StringSeparator + ConnectionType + StringSeparator + Identifier.Replace(StringSeparator, StringSeparatorReplacement);
    }

    public static Peer FromMulticastString(string multicastString)
    {
      var peerStrings = multicastString.Split(StringSeparator, StringSplitOptions.TrimEntries);
      if ((peerStrings.Length != 8) || (peerStrings[0] != MulticastPrefix) || (peerStrings[1] != "Service"))
        throw new ArgumentException("Invalid multicast string.");
      return new Peer
      {
        InterfaceId = peerStrings[2],
        IP = peerStrings[3],
        Port = Convert.ToInt32(peerStrings[4]),
        ServiceType = Enum.Parse<ServiceTypes>(peerStrings[5]),
        ConnectionType = Enum.Parse<ConnectionTypes>(peerStrings[6]),
        Identifier = peerStrings[7]
      };
    }

    public bool IsEmpty => string.IsNullOrEmpty(Identifier);
  }

  const string MulticastPrefix = "BeamDiscovery";
  const string MulticastGroupAddress = "224.0.0.79";
  const int MulticastPort = 13639;
  public const string StringSeparator = "｜";
  public const string StringSeparatorReplacement = "|";

  UdpClient _udpServer = new();
  Peer _serverPeer;
  bool _udpIsListening;
  IPAddress _serviceAddress = IPAddress.Any;

  public void StartServer(IPAddress serviceAddress, int servicePort, ServiceTypes serviceType, string serviceIdentifier)
  {
    _serviceAddress = serviceAddress;
    Module.Log("Peer Discovery server: Starting...", ObsLogLevel.Debug);
    if (_udpIsListening)
      StopServer();
    _serverPeer.IP = _serviceAddress.ToString();
    _serverPeer.Port = servicePort;
    _serverPeer.ServiceType = serviceType;
    _serverPeer.ConnectionType = ConnectionTypes.Socket;
    _serverPeer.Identifier = serviceIdentifier;

    _udpServer = new UdpClient();
    _udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
    _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
    _udpServer.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress));
    _udpIsListening = true;
    Task.Run(UdpServerReceiveLoop);
    Module.Log("Peer Discovery server: Started and entered receive loop.", ObsLogLevel.Debug);
  }

  public void StartServer(ServiceTypes serviceType, string serviceIdentifier)
  {
    _serviceAddress = IPAddress.Loopback;
    Module.Log("Peer Discovery server: Starting...", ObsLogLevel.Debug);
    if (_udpIsListening)
      StopServer();
    _serverPeer.IP = serviceIdentifier;
    _serverPeer.Port = 0;
    _serverPeer.ServiceType = serviceType;
    _serverPeer.ConnectionType = ConnectionTypes.Pipe;
    _serverPeer.Identifier = serviceIdentifier;

    _udpServer = new UdpClient();
    _udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
    _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
    _udpServer.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress));
    _udpIsListening = true;
    Task.Run(UdpServerReceiveLoop);
    Module.Log("Peer Discovery server: Started and entered receive loop.", ObsLogLevel.Debug);
  }

  public void StopServer()
  {
    if (!_udpIsListening)
      return;
    _udpIsListening = false;
    _udpServer.Close();
    _udpServer.Dispose();
    Module.Log("Peer Discovery server: Stopped.", ObsLogLevel.Debug);
  }

  void UdpServerReceiveLoop()
  {
    try
    {
      IPEndPoint senderEndPoint = new(IPAddress.Any, 0);
      while (true)
      {
        byte[] data = _udpServer.Receive(ref senderEndPoint);
        string queryMessage = Encoding.UTF8.GetString(data);
        var queryItems = queryMessage.Split(StringSeparator, StringSplitOptions.TrimEntries);
        Module.Log("Peer Discovery server: Received query: " + queryMessage, ObsLogLevel.Info);

        if ((queryItems.Length == 2) && (queryItems[0] == MulticastPrefix) && (queryItems[1] == "Discover"))
        {
          // send a response to the original sender
          foreach (var networkInterface in GetNetworkInterfacesWithIds())
          {
            if ((_serviceAddress != IPAddress.Any) && (_serviceAddress.ToString() != networkInterface.Item1.Address.ToString()))
              continue;

            var responseBytes = Encoding.UTF8.GetBytes(_serverPeer.ToMulticastString(networkInterface.Item2, networkInterface.Item1.Address.ToString()));
            _udpServer.Send(responseBytes, responseBytes.Length, senderEndPoint);
          }
        }
      }
    }
    catch (SocketException)
    {
      // _udpServer has been closed, stop listening
      Module.Log("Peer Discovery server: Listening stopped.", ObsLogLevel.Info);
    }
    catch (Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in Peer Discovery server receive loop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  public static async Task<List<Peer>> Discover(Peer currentPeer = default, int waitTimeMs = 200)
  {
    Module.Log("Peer Discovery client: Starting discovery...", ObsLogLevel.Debug);
    var peers = new List<Peer>();
    using UdpClient udpClient = new();

    // prepare the discovery message
    string message = $"{MulticastPrefix}{StringSeparator}Discover";
    byte[] data = Encoding.UTF8.GetBytes(message);

    // broadcast the discovery message
    try
    {
      udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress));
      udpClient.Send(data, data.Length, MulticastGroupAddress, MulticastPort);
    }
    catch (SocketException ex)
    {
      Module.Log($"Peer Discovery client: {ex.GetType().Name} while sending discovery request: {ex.Message}", ObsLogLevel.Error);
      if (ex.StackTrace != null)
        Module.Log(ex.StackTrace, ObsLogLevel.Debug);
      return peers;
    }

    // collect responses
    CancellationTokenSource cancelAfterTimeout = new(waitTimeMs);
    await Task.Run(async () =>
    {
      try
      {
        while (true)
        {
          var receiveResult = await udpClient.ReceiveAsync(cancelAfterTimeout.Token);
          var responseString = Encoding.UTF8.GetString(receiveResult.Buffer);
          try
          {
            Peer discoveredPeer;
            try { discoveredPeer = Peer.FromMulticastString(responseString); } catch { continue; }
            if (!currentPeer.IsEmpty) // searching for a specific identifier? then don't fill the list with other peers that are not interesting
            {
              if ((currentPeer.Identifier == discoveredPeer.Identifier) && (currentPeer.InterfaceId == discoveredPeer.InterfaceId))
              {
                Module.Log($"Peer Discovery client: found specific {discoveredPeer.ServiceType} peer \"{discoveredPeer.Identifier}\" at {discoveredPeer.IP}:{discoveredPeer.Port}.", ObsLogLevel.Debug);
                peers.Add(discoveredPeer); // add only this entry to the list...
                break; // ...and stop the loop
              }
            }
            else
              peers.Add(discoveredPeer);
            Module.Log($"Peer Discovery client: found {discoveredPeer.ServiceType} peer \"{discoveredPeer.Identifier}\" at {discoveredPeer.IP}:{discoveredPeer.Port}.", ObsLogLevel.Debug);
          }
          catch (Exception ex)
          {
            Module.Log($"Peer Discovery client: {ex.GetType().Name} while processing response \"{responseString}\" from {receiveResult.RemoteEndPoint.Address}: {ex.Message}", ObsLogLevel.Error);
            if (ex.StackTrace != null)
              Module.Log(ex.StackTrace, ObsLogLevel.Debug);
          }
        }
      }
      catch (OperationCanceledException)
      {
        Module.Log($"Peer Discovery client: Discovery finished, found {peers.Count} peers.", ObsLogLevel.Debug);
        // this is the normal way to exit the loop after the timeout was reached
      }
      catch (Exception ex)
      {
        Module.Log($"Peer Discovery client: {ex.GetType().Name} while receiving discovery responses: {ex.Message}", ObsLogLevel.Error);
        if (ex.StackTrace != null)
          Module.Log(ex.StackTrace, ObsLogLevel.Debug);
      }
    }, cancelAfterTimeout.Token);
    return peers;
  }

  public static List<UnicastIPAddressInformation> GetNetworkInterfaces()
  {
    var networkInterfaces = new List<UnicastIPAddressInformation>();
    foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
      if (networkInterface.OperationalStatus == OperationalStatus.Up)
      {
        foreach (var ip in networkInterface.GetIPProperties().UnicastAddresses)
        {
          if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
            networkInterfaces.Add(ip);
        }
      }
    }
    return networkInterfaces;
  }

  public static List<(UnicastIPAddressInformation, string)> GetNetworkInterfacesWithIds()
  {
    var networkInterfaces = new List<(UnicastIPAddressInformation, string)>();
    foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
      if (networkInterface.OperationalStatus == OperationalStatus.Up)
      {
        foreach (var ip in networkInterface.GetIPProperties().UnicastAddresses)
        {
          if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
          {

            string identifierString = ((networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) ? "localhost" : networkInterface.GetPhysicalAddress().ToString());
            if (string.IsNullOrEmpty(identifierString))
              identifierString = networkInterface.Name;
            string hashIdentifier = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identifierString))).Replace("-", "");
            networkInterfaces.Add((ip, hashIdentifier));
            // Module.Log("NIC: \"" + networkInterface.Name + "\": " + ip.Address + " / " + identifierString + " / " + hashIdentifier, ObsLogLevel.Debug);
          }
        }
      }
    }
    return networkInterfaces;
  }
}

