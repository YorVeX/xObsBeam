// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

/*
  Peer Discovery regression testing:
  - named pipes from remote systems are not listed
  - named pipes from the same system are listed
  - sockets from remote systems are listed
  - sockets from the same system are listed
  - sockets on localhost from remote systems are not listed
  - if a service is only provided on a specific interface, only the service on this interface is listed (for same system and remote systems)
  - sockets are redetected from interface ID when the IP address changed
  - sockets are redetected from interface ID when the port changed
  - no duplicates are listed
  - receiver on system with multiple interfaces discovers all services
  - sender on system with multiple interfaces announces all services
*/

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace xObsBeam;

public partial class PeerDiscovery
{

#if WINDOWS
  const int SIO_UDP_CONNRESET = -1744830452;
#endif

  public enum ConnectionTypes
  {
    Pipe,
    Socket,
  }

  public struct Peer : IComparable<Peer>
  {
    public string InterfaceId;
    public string Identifier;
    public Beam.SenderTypes SenderType;
    public string SenderVersionString;
    public ConnectionTypes ConnectionType;
    public string IP;
    public int Port;

    public string SocketUniqueIdentifier => !IsEmpty ? $"{Identifier}{StringSeparator}{InterfaceId}" : "";

    public string SocketListItemName => !IsEmpty ? $"{Identifier} [{SenderType}] / {IP}:{Port}" : "";

    public string PipeListItemName => !IsEmpty ? $"{Identifier} [{SenderType}]" : "";

    public string SocketListItemValue => !IsEmpty ? $"{Identifier}{StringSeparator}{InterfaceId}{StringSeparator}{SenderType}{StringSeparator}{IP}:{Port}" : "";

    public string PipeListItemValue => !IsEmpty ? $"{Identifier}{StringSeparator}{SenderType}" : "";

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
        peer.SenderType = Enum.Parse<Beam.SenderTypes>(items[1]);
        peer.IP = "127.0.0.1";
        peer.Port = 0;
        return peer;
      }
      // Socket peer
      peer.ConnectionType = ConnectionTypes.Socket;
      peer.Identifier = items[0];
      peer.InterfaceId = items[1];
      peer.SenderType = Enum.Parse<Beam.SenderTypes>(items[2]);
      var ipPort = items[3].Split(':');
      peer.IP = ipPort[0];
      peer.Port = int.Parse(ipPort[1]);
      return peer;
    }

    public string ToMulticastString(string interfaceId, string ipAddress)
    {
      return MulticastPrefix + StringSeparator + "Service" + StringSeparator + Module.ModuleVersionString + StringSeparator + interfaceId + StringSeparator + ipAddress + StringSeparator + Port + StringSeparator + SenderType + StringSeparator + ConnectionType + StringSeparator + Identifier.Replace(StringSeparator, StringSeparatorReplacement);
    }

    public static Peer FromMulticastString(string multicastString)
    {
      var peerStrings = multicastString.Split(StringSeparator, StringSplitOptions.TrimEntries);
      if (peerStrings.Length != 9 || peerStrings[0] != MulticastPrefix || peerStrings[1] != "Service")
        throw new ArgumentException("Invalid multicast string.");
      return new Peer
      {
        SenderVersionString = peerStrings[2],
        InterfaceId = peerStrings[3],
        IP = peerStrings[4],
        Port = Convert.ToInt32(peerStrings[5]),
        SenderType = Enum.Parse<Beam.SenderTypes>(peerStrings[6]),
        ConnectionType = Enum.Parse<ConnectionTypes>(peerStrings[7]),
        Identifier = peerStrings[8]
      };
    }

    public override bool Equals(object? obj)
    {
      return obj is Peer peer &&
             InterfaceId == peer.InterfaceId &&
             Identifier == peer.Identifier &&
             SenderType == peer.SenderType &&
             SenderVersionString == peer.SenderVersionString &&
             ConnectionType == peer.ConnectionType &&
             IP == peer.IP &&
             Port == peer.Port;
    }

    public bool IsEmpty => string.IsNullOrEmpty(Identifier);

    public override int GetHashCode()
    {
      HashCode hash = new();
      hash.Add(InterfaceId);
      hash.Add(Identifier);
      hash.Add(SenderType);
      hash.Add(SenderVersionString);
      hash.Add(ConnectionType);
      hash.Add(IP);
      hash.Add(Port);
      return hash.ToHashCode();
    }

    public int CompareTo(Peer other)
    {
      if (ConnectionType == ConnectionTypes.Socket)
        return SocketListItemName.CompareTo(other.SocketListItemName);
      return PipeListItemName.CompareTo(other.PipeListItemName);
    }

    public static bool operator ==(Peer left, Peer right)
    {
      return left.Equals(right);
    }

    public static bool operator !=(Peer left, Peer right)
    {
      return !(left == right);
    }
  }

  const string MulticastPrefix = "BeamDiscovery";
  const string MulticastGroupAddress = "224.0.0.79";
  const int MulticastPort = 13639;
  const int MulticastTtl = 2;
  public const string StringSeparator = "｜";
  public const string StringSeparatorReplacement = "|";

  CancellationTokenSource _cancellationSource = new();
  Peer _serverPeer;
  bool _udpIsListening;
  IPAddress _serviceAddress = IPAddress.Any;

  public void StartServer(IPAddress serviceAddress, int servicePort, Beam.SenderTypes serviceType, string serviceIdentifier)
  {
    _serviceAddress = serviceAddress;
    if (_udpIsListening)
      StopServer();
    _serverPeer.IP = _serviceAddress.ToString();
    _serverPeer.Port = servicePort;
    _serverPeer.SenderType = serviceType;
    _serverPeer.ConnectionType = ConnectionTypes.Socket;
    _serverPeer.Identifier = serviceIdentifier;

    if (!_cancellationSource.TryReset())
    {
      _cancellationSource.Dispose();
      _cancellationSource = new CancellationTokenSource();
    }
    Module.Log($"Peer Discovery server: Starting for {serviceType} \"{serviceIdentifier}\" on {IPAddress.Loopback}:{servicePort}...", ObsLogLevel.Info);
    Task.Run(() => UdpServerReceiveLoop(IPAddress.Loopback), _cancellationSource.Token);
    foreach (var multicastAddress in NetworkInterfaces.MulticastInterfaceIps)
    {
      Module.Log($"Peer Discovery server: Starting for {serviceType} \"{serviceIdentifier}\" on {multicastAddress}:{servicePort}...", ObsLogLevel.Info);
      Task.Run(() => UdpServerReceiveLoop(multicastAddress), _cancellationSource.Token);
    }
    _udpIsListening = true;
  }

  public void StartServer(Beam.SenderTypes serviceType, string serviceIdentifier)
  {
    _serviceAddress = IPAddress.Loopback;
    if (_udpIsListening)
      StopServer();
    Module.Log($"Peer Discovery server: Starting for {serviceType} \"{serviceIdentifier}\" on {_serviceAddress}...", ObsLogLevel.Info);
    _serverPeer.IP = serviceIdentifier;
    _serverPeer.Port = 0;
    _serverPeer.SenderType = serviceType;
    _serverPeer.ConnectionType = ConnectionTypes.Pipe;
    _serverPeer.Identifier = serviceIdentifier;

    if (!_cancellationSource.TryReset())
    {
      _cancellationSource.Dispose();
      _cancellationSource = new CancellationTokenSource();
    }
    Task.Run(() => UdpServerReceiveLoop(_serviceAddress), _cancellationSource.Token);
    _udpIsListening = true;
  }

  public void StopServer()
  {
    if (!_udpIsListening)
      return;
    _udpIsListening = false;
    _cancellationSource.Cancel();
    Module.Log("Peer Discovery server: Stopped.", ObsLogLevel.Debug);
  }

  async void UdpServerReceiveLoop(IPAddress bindIpAddress)
  {
    try
    {
      using var udpServer = new UdpClient(AddressFamily.InterNetwork);
      if (IPAddress.IsLoopback(bindIpAddress))
        udpServer.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress), bindIpAddress);
      else
        udpServer.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress), MulticastTtl);
#if WINDOWS
      udpServer.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); // prevent "ConnectionReset" (10054) SocketExceptions caused by clients via ICMP
#endif
      udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
      udpServer.Client.Bind(new IPEndPoint(bindIpAddress, MulticastPort));
      Module.Log($"Peer Discovery server at {bindIpAddress}: Started and entered receive loop for {_serverPeer.SenderType} \"{_serverPeer.Identifier}\".", ObsLogLevel.Debug);

      while (!_cancellationSource.IsCancellationRequested)
      {
        var receiveResult = await udpServer.ReceiveAsync(_cancellationSource.Token);

        string queryMessage = Encoding.UTF8.GetString(receiveResult.Buffer);
        var queryItems = queryMessage.Split(StringSeparator, StringSplitOptions.TrimEntries);
        Module.Log($"Peer Discovery server at {bindIpAddress}: {_serverPeer.SenderType} \"{_serverPeer.Identifier}\" received query: {queryMessage}", ObsLogLevel.Debug);

        if (queryItems.Length == 2 && queryItems[0] == MulticastPrefix && queryItems[1] == "Discover")
        {
          // send a response to the original sender for every available interface
          foreach (var unicastAddress in NetworkInterfaces.UnicastAddressesWithIds)
          {
            // if a service is only provided on a specific interface, don't announce it via other interfaces
            if ((_serviceAddress != IPAddress.Any) && (_serviceAddress.ToString() != unicastAddress.Item1.Address.ToString()))
              continue;

            // don't announce loopback services via non-loopback interfaces (the remote receiver would not be able to connect to it anyway)
            if (IPAddress.IsLoopback(unicastAddress.Item1.Address) && !IPAddress.IsLoopback(bindIpAddress))
              continue;

            string multicastString = _serverPeer.ToMulticastString(unicastAddress.Item2, unicastAddress.Item1.Address.ToString());
            Module.Log($"Peer Discovery server at {bindIpAddress}: {_serverPeer.SenderType} \"{_serverPeer.Identifier}\" announcing service: \"{_serverPeer.ToMulticastString(unicastAddress.Item2, unicastAddress.Item1.Address.ToString())}\"", ObsLogLevel.Debug);
            var responseBytes = Encoding.UTF8.GetBytes(multicastString);
            udpServer.Send(responseBytes, responseBytes.Length, receiveResult.RemoteEndPoint);
          }
        }
      }
    }
    catch (SocketException)
    {
      // udpServer has been closed, stop listening
      Module.Log($"Peer Discovery server at {bindIpAddress}: Listening stopped (socket closed).", ObsLogLevel.Info);
    }
    catch (OperationCanceledException)
    {
      Module.Log($"Peer Discovery server at {bindIpAddress}: Listening stopped (task stopped).", ObsLogLevel.Info);
    }
    catch (Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in Peer Discovery server at {bindIpAddress} receive loop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  public static List<Peer> Discover(Peer currentPeer = default, int waitTimeMs = 100)
  {
    Module.Log("Peer Discovery client: Starting discovery...", ObsLogLevel.Debug);
    var peers = new ConcurrentDictionary<string, Peer>();

    // prepare the discovery message
    string message = $"{MulticastPrefix}{StringSeparator}Discover";
    byte[] data = Encoding.UTF8.GetBytes(message);

    // broadcast the discovery message
    List<Task> sendTasks = [];
    try
    {
      CancellationTokenSource cancelAfterTimeout = new(waitTimeMs);
      var multicastInterfaceIps = new List<IPAddress>(NetworkInterfaces.MulticastInterfaceIps)
      {
        IPAddress.Loopback // NetworkInterfaces.MulticastInterfaceIps doesn't include the loopback interface, so we need to add it manually
      };
      foreach (var multicastInterfaceIp in multicastInterfaceIps)
      {
        sendTasks.Add(Task.Run(async () =>
        {
          using UdpClient udpClient = new(AddressFamily.InterNetwork);

          // send discovery request
          try
          {
            Module.Log($"Peer Discovery client: Sending multicast discovery request from {multicastInterfaceIp}", ObsLogLevel.Debug);
            if (IPAddress.IsLoopback(multicastInterfaceIp))
              udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress), multicastInterfaceIp);
            else
              udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress), MulticastTtl);
            udpClient.Client.Bind(new IPEndPoint(multicastInterfaceIp, 0));
            udpClient.Send(data, data.Length, MulticastGroupAddress, MulticastPort);
          }
          catch (Exception ex)
          {
            Module.Log($"Peer Discovery client: {ex.GetType().Name} while sending discovery request from {multicastInterfaceIp}: {ex.Message}", ObsLogLevel.Error);
            if (ex.StackTrace != null)
              Module.Log(ex.StackTrace, ObsLogLevel.Debug);
            return;
          }

          // receive discovery responses
          try
          {
            while (true)
            {
              var receiveResult = await udpClient.ReceiveAsync(cancelAfterTimeout.Token);
              var responseString = Encoding.UTF8.GetString(receiveResult.Buffer);
              Module.Log($"Peer Discovery client: Response string: {responseString}.", ObsLogLevel.Debug);
              bool peerAdded = false;
              try
              {
                Peer discoveredPeer;
                try { discoveredPeer = Peer.FromMulticastString(responseString); } catch { continue; }
                if (!currentPeer.IsEmpty) // searching for a specific identifier? then don't fill the list with other peers that are not interesting
                {
                  if (currentPeer.Identifier == discoveredPeer.Identifier && currentPeer.InterfaceId == discoveredPeer.InterfaceId && discoveredPeer.SenderVersionString == Module.ModuleVersionString)
                  {
                    Module.Log($"Peer Discovery client: found specific {discoveredPeer.SenderVersionString} {discoveredPeer.SenderType} peer \"{discoveredPeer.Identifier}\" at {discoveredPeer.IP}:{discoveredPeer.Port}.", ObsLogLevel.Debug);
                    if (discoveredPeer.ConnectionType == ConnectionTypes.Socket)
                      peerAdded = peers.TryAdd(discoveredPeer.ConnectionType + discoveredPeer.SocketUniqueIdentifier, discoveredPeer);
                    else if (discoveredPeer.ConnectionType == ConnectionTypes.Pipe)
                      peerAdded = peers.TryAdd(discoveredPeer.ConnectionType + discoveredPeer.Identifier, discoveredPeer); // add only this entry to the list...
                    break; // ...and stop the loop
                  }
                }
                else
                {
                  Module.Log($"Peer Discovery client: Adding peer: {responseString}.", ObsLogLevel.Debug);
                  if (discoveredPeer.ConnectionType == ConnectionTypes.Socket)
                    peerAdded = peers.TryAdd(discoveredPeer.ConnectionType + discoveredPeer.SocketUniqueIdentifier, discoveredPeer);
                  else if (discoveredPeer.ConnectionType == ConnectionTypes.Pipe)
                    peerAdded = peers.TryAdd(discoveredPeer.ConnectionType + discoveredPeer.Identifier, discoveredPeer); // add only this entry to the list...
                  Module.Log($"Peer Discovery client: Peer added {peerAdded} for: {responseString}.", ObsLogLevel.Debug);
                }
                if (peerAdded)
                  Module.Log($"Peer Discovery client: Found v{discoveredPeer.SenderVersionString} {discoveredPeer.SenderType} peer \"{discoveredPeer.Identifier}\" at {discoveredPeer.IP}:{discoveredPeer.Port}.", ObsLogLevel.Debug);
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
            Module.Log($"Peer Discovery client: Discovery finished for network interface {multicastInterfaceIp}.", ObsLogLevel.Debug);
            // this is the normal way to exit the loop after the timeout was reached
          }
          catch (Exception ex)
          {
            Module.Log($"Peer Discovery client: {ex.GetType().Name} while receiving discovery responses on network interface {multicastInterfaceIp}: {ex.Message}", ObsLogLevel.Error);
            if (ex.StackTrace != null)
              Module.Log(ex.StackTrace, ObsLogLevel.Debug);
          }
        }, cancelAfterTimeout.Token));
      }
    }
    catch (SocketException ex)
    {
      Module.Log($"Peer Discovery client: {ex.GetType().Name} while sending discovery request: {ex.Message}", ObsLogLevel.Error);
      if (ex.StackTrace != null)
        Module.Log(ex.StackTrace, ObsLogLevel.Debug);
      return [.. peers.Values];
    }

    Task.WaitAll([.. sendTasks]);
    var peersList = new List<Peer>([.. peers.Values]);
    peersList.Sort();
    Module.Log($"Peer Discovery client: Discovery finished for all network interfaces, found {peers.Count} peers.", ObsLogLevel.Debug);
    return peersList;
  }
}
