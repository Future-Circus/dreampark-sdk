using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LanMonitor;

/// <summary>
/// Listens for dream-pub discovery beacons on UDP :7700 and keeps a live
/// table of the sessions on this LAN.
///
/// This is the read half of <c>DiscoveryListener.cs</c> in the Unity SDK,
/// reimplemented standalone. It parses the same fields, including the
/// peer-host extensions (hostType/hostId/parkId/seq/v/ch/msgCap) documented in
/// dreampark-core <c>Docs/LAN-PeerHost-Spec.md</c> §3.2.
///
/// ⚠️ THIS CLASS NEVER TRANSMITS, AND MUST NEVER LEARN HOW.
/// There is no <c>UdpClient.Send</c>, no <c>EnableBroadcast</c>, and no
/// beacon-writing counterpart anywhere in this tool — by construction, not by
/// discipline. A kiosk beacon always outranks a peer session
/// (<c>NetSessionArbiter</c>'s ladder), so a monitor that advertised itself
/// would preempt the peer election and destroy the very session it was
/// brought in to observe. Keep the send path absent.
/// </summary>
public sealed class BeaconWatcher : IDisposable
{
    private const string ServiceFilter = "dream-pub";

    private readonly MonitorState _state;
    private readonly int _port;
    private UdpClient? _udp;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Null when the socket bound cleanly; otherwise why it didn't.</summary>
    public string? BindError { get; private set; }

    public bool IsRunning => _running;

    public BeaconWatcher(MonitorState state, int port)
    {
        _state = state;
        _port = port;
    }

    public bool Start()
    {
        if (_running) return true;

        try
        {
            _udp = new UdpClient();
            // A Unity Editor running DreamBoxClient — or the dev relay — may
            // already hold :7700 on this machine. Ask the OS to let us share it
            // rather than making the developer close the Editor to watch their
            // own traffic. Whether the request is honoured is platform- and
            // socket-type-dependent, which is why the bind failure below is a
            // degraded mode and not a fatal error.
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException ex)
        {
            BindError =
                $"could not bind UDP :{_port} ({ex.SocketErrorCode}). " +
                "Another process on this machine already holds it — usually a Unity Editor " +
                "with DreamBoxClient running, or the DreamBox dev relay. Discovery is off; " +
                "attach to a session by hand from the panel.";
            Console.Error.WriteLine($"[beacon] {BindError}");
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            return false;
        }

        _running = true;
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "BeaconWatcher" };
        _thread.Start();

        Console.WriteLine($"[beacon] listening on :{_port} (read-only — this tool never broadcasts)");
        return true;
    }

    private void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            byte[] data;
            try
            {
                data = _udp!.Receive(ref remote);
            }
            catch (SocketException) when (!_running) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (_running) Console.Error.WriteLine($"[beacon] receive error: {ex.Message}");
                continue;
            }

            try
            {
                var beacon = Parse(Encoding.UTF8.GetString(data), remote.Address.ToString());
                if (beacon != null) _state.RecordBeacon(beacon);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[beacon] parse error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parse one beacon datagram. Returns null for anything that isn't a
    /// well-formed dream-pub beacon — the LAN carries plenty of other UDP
    /// broadcast traffic on shared ports.
    /// </summary>
    internal static BeaconInfo? Parse(string json, string sourceAddress)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Str(root, "service") != ServiceFilter) return null;

            var host = Str(root, "host");
            if (string.IsNullOrEmpty(host)) return null;
            if (!TryInt(root, "port", out var port) || port <= 0) return null;

            var hostType = Str(root, "hostType");
            // Kiosk beacons carry no hostType (or "dreambox"); absence marks them.
            var isPeer = string.Equals(hostType, "peer", StringComparison.OrdinalIgnoreCase);

            return new BeaconInfo
            {
                Host = host,
                Port = port,
                Key = Str(root, "key"),
                DreamboxId = Str(root, "dreamboxId"),
                HostType = isPeer ? "peer" : "dreambox",
                HostId = Str(root, "hostId"),
                ParkId = Str(root, "parkId"),
                Seq = TryInt(root, "seq", out var seq) ? seq : 0,
                ProtocolVersion = TryInt(root, "v", out var v) ? v : 0,
                Channel = Str(root, "ch"),
                MsgCap = TryInt(root, "msgCap", out var cap) ? cap : 0,
                SourceAddress = sourceAddress,
                ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString(), out value);
        return false;
    }

    public void Stop()
    {
        if (!_running && _udp == null) return;
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
        _thread = null;
    }

    public void Dispose() => Stop();
}

/// <summary>One parsed beacon datagram.</summary>
public sealed class BeaconInfo
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Key { get; init; } = "";
    public string DreamboxId { get; init; } = "";
    public string HostType { get; init; } = "";
    public string HostId { get; init; } = "";
    public string ParkId { get; init; } = "";
    public int Seq { get; init; }
    public int ProtocolVersion { get; init; }
    public string Channel { get; init; } = "";
    public int MsgCap { get; init; }

    /// <summary>Datagram source address — differs from Host if the beacon advertises a stale IP.</summary>
    public string SourceAddress { get; init; } = "";
    public long ReceivedAtMs { get; init; }

    /// <summary>Stable identity for a session: what you would connect to.</summary>
    public string SessionId => $"{Host}:{Port}";
}
