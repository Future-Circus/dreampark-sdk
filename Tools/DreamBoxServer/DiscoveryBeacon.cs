using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DreamBoxRelay;

/// <summary>
/// Sends periodic UDP broadcast beacons so headsets on the same LAN
/// can discover the relay without querying the backend.
/// </summary>
public sealed class DiscoveryBeacon : IDisposable
{
    private readonly RelayConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public DiscoveryBeacon(RelayConfig config)
    {
        _config = config;
    }

    public void Start()
    {
        if (!_config.DiscoveryEnabled)
        {
            Console.WriteLine("[beacon] Discovery disabled in config — skipping.");
            return;
        }

        _thread = new Thread(BeaconLoop)
        {
            IsBackground = true,
            Name = "DiscoveryBeacon"
        };
        _thread.Start();

        Console.WriteLine(
            $"[beacon] Broadcasting on :{_config.DiscoveryPort} every {_config.DiscoveryIntervalMs}ms (dreamboxId={_config.DreamboxId})");
    }

    private void BeaconLoop()
    {
        var token = _cts.Token;
        var lanIp = GetLanIp();
        var payload = BuildPayload(lanIp);
        var bytes = Encoding.UTF8.GetBytes(payload);

        // Broadcast (255.255.255.255) reaches LAN peers on physical interfaces
        // but does NOT loop back to 127.0.0.1 — so we also unicast a copy to
        // the loopback address. That makes same-machine dev testing (Unity
        // Editor + local relay on the same box) Just Work without any client
        // changes. Harmless on the Pi: there's no localhost client to receive.
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _config.DiscoveryPort);
        var loopbackEndpoint = new IPEndPoint(IPAddress.Loopback, _config.DiscoveryPort);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

        while (!token.IsCancellationRequested)
        {
            TrySend(socket, bytes, broadcastEndpoint, "broadcast");
            TrySend(socket, bytes, loopbackEndpoint, "loopback");

            try
            {
                Thread.Sleep(_config.DiscoveryIntervalMs);
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }
    }

    private static void TrySend(Socket socket, byte[] bytes, IPEndPoint endpoint, string label)
    {
        try
        {
            socket.SendTo(bytes, endpoint);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"[beacon] Send to {label} {endpoint} failed: {ex.Message}");
        }
    }

    private string BuildPayload(string lanIp)
    {
        var obj = new
        {
            service = "dream-pub",
            host = lanIp,
            port = _config.Port,
            key = _config.ConnectionKey,
            dreamboxId = _config.DreamboxId
        };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>
    /// Detect the LAN IP by opening a dummy UDP socket to 8.8.8.8:80.
    /// No traffic is sent — this just triggers a route lookup.
    /// Falls back to hostname resolution, then 127.0.0.1.
    /// </summary>
    private static string GetLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint ep && ep.Address.ToString() is var ip
                && ip != "0.0.0.0")
            {
                return ip;
            }
        }
        catch (SocketException) { }

        try
        {
            var host = Dns.GetHostName();
            var entry = Dns.GetHostEntry(host);
            foreach (var addr in entry.AddressList)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork
                    && addr.ToString() != "127.0.0.1")
                {
                    return addr.ToString();
                }
            }
        }
        catch (SocketException) { }

        Console.Error.WriteLine("[beacon] Could not determine LAN IP, falling back to 127.0.0.1");
        return "127.0.0.1";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Interrupt();
        _thread?.Join(2000);
        _cts.Dispose();
    }
}
