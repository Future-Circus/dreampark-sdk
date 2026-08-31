using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LanMonitor;

/// <summary>
/// A read-only peer. Joins a relay exactly the way <c>DreamBoxClient</c> does —
/// same LiteNetLib version, same connection key from the beacon — and then
/// never transmits anything.
///
/// Why joining shows you everything: the relay is a dumb pipe that rebroadcasts
/// every inbound message verbatim to all OTHER connected peers, and the peer
/// host connects to its own relay over 127.0.0.1 as an ordinary client. So a
/// participant that only listens sees 100% of session traffic, the host's own
/// events included.
///
/// Two consequences worth keeping in mind while reading the feed:
///   • We occupy a peer slot (MaxPeers 16) and count against the host's
///     per-source-address cap (MaxPeersPerAddress 2). A laptop already running
///     an Editor instance in this session has one slot left.
///   • Sender attribution is gone. The relay adds no origin annotation and
///     everything reaches us over the single connection to the host, so "who
///     sent this" can only come from the payload's own `u` field.
///
/// ⚠️ There is deliberately no Send method on this class. Adding one would put
/// traffic on the wire that no headset produced, which is the one thing a
/// debugging observer must never do.
/// </summary>
public sealed class SessionObserver : IDisposable
{
    /// <summary>Matches DreamBoxClient and both relays. Anything larger can't have got past the host anyway.</summary>
    private const int MaxIncomingMessageBytes = 16 * 1024;

    private readonly MonitorState _state;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _client;

    private NetPeer? _peer;

    public string? SessionId { get; private set; }
    public BeaconInfo? Beacon { get; private set; }
    public bool IsConnected => _peer is { ConnectionState: ConnectionState.Connected };
    public int Ping => _peer?.Ping ?? 0;

    public SessionObserver(MonitorState state)
    {
        _state = state;

        _client = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnconnectedMessagesEnabled = false,
            DisconnectTimeout = 5000,
            // We never reconnect in place — the director re-attaches using the
            // newest beacon instead, because after a re-election the host, port
            // AND session key have all typically changed.
            MaxConnectAttempts = 5,
            ReconnectDelay = 500
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            _state.State = AttachState.Attached;
            _state.AttachedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _state.StatusMessage = $"observing {SessionId}";
            Console.WriteLine($"[observe] attached to {peer} — read-only, sending nothing");
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _peer = null;
            var reason = Describe(info);
            _state.State = info.Reason == DisconnectReason.ConnectionRejected
                ? AttachState.Rejected
                : AttachState.Idle;
            _state.StatusMessage = reason;
            _state.AttachedSessionId = null;
            Console.WriteLine($"[observe] detached from {peer}: {reason}");
        };

        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try
            {
                var available = reader.AvailableBytes;
                if (available > MaxIncomingMessageBytes)
                {
                    Console.Error.WriteLine($"[observe] oversized message ({available} B) — skipped.");
                    return;
                }

                string raw;
                try
                {
                    // DreamBoxClient writes with NetDataWriter.Put(string) and both
                    // relays forward the payload untouched, so the length prefix
                    // survives end to end and GetString() is the correct decode.
                    raw = reader.GetString();
                }
                catch
                {
                    // Something on this wire isn't our framing. Show it anyway.
                    raw = Encoding.UTF8.GetString(reader.GetRemainingBytes());
                }

                _state.RecordObserved(available);
                var entry = _state.Log.Record(raw, available, SessionId ?? "");

                if (_state.Config.Debug)
                {
                    var label = string.IsNullOrEmpty(entry.Type) ? "(unparsed)" : entry.Type;
                    Console.WriteLine($"[observe] {label} netId={entry.NetId?.ToString() ?? "-"} {available}B");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[observe] receive error: {ex.Message}");
            }
        };

        _listener.NetworkErrorEvent += (endpoint, error) =>
        {
            Console.Error.WriteLine($"[observe] network error from {endpoint}: {error}");
        };
    }

    public void Start() => _client.Start();

    public void Poll() => _client.PollEvents();

    /// <summary>Join a session. Any existing attachment is dropped first.</summary>
    public void Attach(BeaconInfo beacon)
    {
        Detach();

        Beacon = beacon;
        SessionId = beacon.SessionId;
        _state.AttachedSessionId = beacon.SessionId;
        _state.State = AttachState.Connecting;
        _state.StatusMessage = $"connecting to {beacon.SessionId}…";

        try
        {
            // The key rides in the beacon: "dreambox" for a kiosk, an 8-char
            // per-session random for a peer host.
            _peer = _client.Connect(beacon.Host, beacon.Port, beacon.Key ?? "");
            Console.WriteLine($"[observe] connecting to {beacon.SessionId} " +
                              $"(hostType={beacon.HostType}, parkId={Show(beacon.ParkId)}, ch={Show(beacon.Channel)})");
        }
        catch (Exception ex)
        {
            _state.State = AttachState.Failed;
            _state.StatusMessage = $"connect failed: {ex.Message}";
            _state.AttachedSessionId = null;
            Console.Error.WriteLine($"[observe] connect to {beacon.SessionId} failed: {ex.Message}");
        }
    }

    public void Detach()
    {
        if (_peer != null || _client.ConnectedPeersCount > 0)
        {
            try { _client.DisconnectAll(); } catch { /* best effort */ }
        }

        _peer = null;
        SessionId = null;
        Beacon = null;
        _state.AttachedSessionId = null;
        if (_state.State != AttachState.Rejected) _state.State = AttachState.Idle;
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;

    private static string Describe(DisconnectInfo info) => info.Reason switch
    {
        DisconnectReason.ConnectionRejected =>
            "host rejected the connection — wrong session key, the relay is at its 16-peer cap, " +
            "or this machine already holds the host's 2-connections-per-address limit",
        DisconnectReason.Timeout => "timed out — host went quiet (doffed headset, battery, or Wi-Fi drop)",
        DisconnectReason.RemoteConnectionClose => "host closed the session",
        DisconnectReason.DisconnectPeerCalled => "detached",
        DisconnectReason.ConnectionFailed => "could not reach the host — check you are on the same subnet, and that the LAN does not isolate clients",
        DisconnectReason.HostUnreachable => "host unreachable",
        DisconnectReason.NetworkUnreachable => "network unreachable",
        _ => info.Reason.ToString()
    };

    public void Dispose()
    {
        try { _client.DisconnectAll(); } catch { }
        try { _client.Stop(); } catch { }
    }
}
