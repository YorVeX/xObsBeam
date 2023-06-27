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
    public string Identifier;
    public ServiceTypes ServiceType;
    public ConnectionTypes ConnectionType;
    public string IP;
    public int Port;
  }

  const string MulticastPrefix = "BeamDiscovery";
  const string MulticastGroupAddress = "224.0.0.79";
  const int MulticastPort = 13639;
  const string StringSeparator = "｜";
  const string StringSeparatorReplacement = "|";

  UdpClient _udpServer = new();
  Peer _serverPeer;
  bool _udpIsListening;
  IPAddress _serviceAddress = IPAddress.Any;

  public void StartServer(IPAddress serviceAddress, int servicePort, ServiceTypes serviceType, ConnectionTypes connectionType, string serviceIdentifier)
  {
    _serviceAddress = serviceAddress;
    Module.Log("Peer Discovery server: Starting...", ObsLogLevel.Debug);
    if (_udpIsListening)
      StopServer();
    _serverPeer.IP = _serviceAddress.ToString();
    _serverPeer.Port = servicePort;
    _serverPeer.ServiceType = serviceType;
    _serverPeer.ConnectionType = connectionType;
    _serverPeer.Identifier = serviceIdentifier;

    _udpServer = new UdpClient();
    _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
    _udpServer.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress));
    _udpIsListening = true;
    _udpServer.BeginReceive(ServerReceiveCallback, null);
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

  private void ServerReceiveCallback(IAsyncResult ar)
  {
    try
    {
      if (!_udpIsListening)
        return;

      IPEndPoint senderEndPoint = new(IPAddress.Any, MulticastPort);
      byte[] data = _udpServer.EndReceive(ar, ref senderEndPoint!);
      string queryMessage = Encoding.UTF8.GetString(data);
      var queryItems = queryMessage.Split(StringSeparator, StringSplitOptions.TrimEntries);
      Module.Log("Peer Discovery server: Received query: " + queryMessage, ObsLogLevel.Info);

      if ((queryItems.Length == 2) && (queryItems[0] == MulticastPrefix) && (queryItems[1] == "Discover"))
      {
        // send a response to the original sender
        foreach (var networkInterface in GetNetworkInterfaces())
        {
          if ((_serviceAddress != IPAddress.Any) && (_serviceAddress.ToString() != networkInterface.Address.ToString()))
            continue;

          string responseMessage = MulticastPrefix + StringSeparator + "Service" + StringSeparator + networkInterface.Address.ToString() + StringSeparator + _serverPeer.Port + StringSeparator + _serverPeer.ServiceType + StringSeparator + _serverPeer.ConnectionType + StringSeparator + _serverPeer.Identifier.Replace(StringSeparator, StringSeparatorReplacement);
          var responseBytes = Encoding.UTF8.GetBytes(responseMessage);
          _udpServer.Send(responseBytes, responseBytes.Length, senderEndPoint);
        }
      }

      // continue listening for more queries
      _udpServer.BeginReceive(ServerReceiveCallback, null);
    }
    catch (ObjectDisposedException)
    {
      // _udpServer has been closed, stop listening
      Module.Log("Peer Discovery server: Listening stopped.", ObsLogLevel.Info);
    }
    catch (Exception ex)
    {
      Module.Log($"Peer Discovery server: {ex.GetType().Name} in receive handler: {ex.Message}", ObsLogLevel.Error);
      if (ex.StackTrace != null)
        Module.Log(ex.StackTrace, ObsLogLevel.Debug);
    }
  }

  public static async Task<List<Peer>> Discover(int waitTimeMs = 500)
  {
    Module.Log("Peer Discovery client: Starting discovery...", ObsLogLevel.Debug);
    using UdpClient udpClient = new();

    // prepare the discovery message
    string message = $"{MulticastPrefix}{StringSeparator}Discover";
    byte[] data = Encoding.UTF8.GetBytes(message);

    // broadcast the discovery message
    udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastGroupAddress));
    udpClient.Send(data, data.Length, MulticastGroupAddress, MulticastPort);

    // collect responses
    var peers = new List<Peer>();
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
            var peerStrings = responseString.Split(StringSeparator, StringSplitOptions.TrimEntries);
            if ((peerStrings.Length != 7) || (peerStrings[0] != MulticastPrefix) || (peerStrings[1] != "Service"))
              continue;
            if (!Enum.TryParse(peerStrings[4], out ServiceTypes serviceType))
              continue;
            if (!Enum.TryParse(peerStrings[5], out ConnectionTypes connectionType))
              continue;
            var peer = new Peer
            {
              IP = peerStrings[2],
              Port = Convert.ToInt32(peerStrings[3]),
              ServiceType = serviceType,
              ConnectionType = connectionType,
              Identifier = peerStrings[6]
            };
            peers.Add(peer);
            Module.Log($"Peer Discovery client: found {peer.ServiceType} peer \"{peer.Identifier}\" at {peer.IP}:{peer.Port}.", ObsLogLevel.Debug);
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
}

