using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Defective.JSON;

/// <summary>
/// Listens for UDP broadcast beacons from dream-pub on port 7700.
/// When a valid beacon is received, invokes OnRelayDiscovered on the main thread.
/// </summary>
public class DiscoveryListener : IDisposable
{
    public const int BeaconPort = 7700;
    private const string ServiceFilter = "dream-pub";

    public struct BeaconInfo
    {
        public string host;
        public int port;
        public string key;
        public string dreamboxId;

        // Peer-host extension fields (absent on kiosk beacons — see
        // dreampark-core Docs/LAN-PeerHost-Spec.md §3.2). Old beacons parse
        // fine: these stay "".
        public string hostType;   // "" / "dreambox" = kiosk, "peer" = elected headset
        public string hostId;     // stable device id of the peer host
        public string parkId;     // session scope — two parks on one LAN don't merge
        public int seq;           // beacon sequence number
        public int v;             // peer protocol version (0 = pre-versioning/kiosk)
        public string channel;    // "prod" (core builds) / "sdk" (creator projects) — peer sessions don't cross channels
        public int msgCap;        // messages/sec/peer this relay enforces; 0 = not advertised

        /// <summary>
        /// Where the datagram actually came from, which is not the same claim as
        /// <see cref="host"/> and is worth strictly more. `host` is the sender's
        /// guess at its own LAN address — one interface out of however many it
        /// has, chosen without knowing who is listening. `sourceHost` is the
        /// address a packet demonstrably just arrived from, on a path that
        /// demonstrably works, because we are holding the packet.
        ///
        /// They agree on a healthy network. When they disagree, the advertised
        /// one is the one that is wrong, and a joiner that only knows `host`
        /// will connect into a black hole with no way to tell that is what
        /// happened. Carry both; let the arbiter fall back.
        /// </summary>
        public string sourceHost;

        /// <summary>
        /// Oct 11 2026 (net-arbiter): <see cref="Clock"/> seconds when the
        /// datagram arrived, stamped on the discovery thread. The arbiter drains
        /// beacons on the main thread, which does not run while the app is
        /// paused — beacons queued then must age from when they ARRIVED, or a
        /// host that died during the pause looks alive on resume.
        /// </summary>
        public double receivedAt;
    }

    /// <summary>
    /// Monotonic seconds, readable from any thread (Time.unscaledTime is
    /// main-thread only). Compare against <see cref="BeaconInfo.receivedAt"/>.
    /// </summary>
    public static double Clock => _clock.Elapsed.TotalSeconds;
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Fired on background thread when a valid beacon is received.</summary>
    public event Action<BeaconInfo> OnRelayDiscovered;

    /// <summary>
    /// Fired on background thread for EVERY valid dream-pub beacon, regardless of
    /// dreamboxIdFilter. Used by NetSessionArbiter to rank hosts and detect
    /// staleness. OnRelayDiscovered keeps its original filtered semantics.
    /// </summary>
    public event Action<BeaconInfo> OnBeacon;

    /// <summary>
    /// When set, only beacons whose dreamboxId matches this value will fire OnRelayDiscovered.
    /// Used after session pairing to filter out beacons from other DreamBoxes on the LAN.
    /// </summary>
    public string dreamboxIdFilter;

    private UdpClient _udp;
    private Thread _listenThread;
    private volatile bool _running;

    public bool IsRunning => _running;

    /// <summary>Why the listener last stopped on its own (bind failure, socket error); null while healthy.</summary>
    public string LastError { get; private set; }

    public void Start()
    {
        if (_running) return;
        if (_udp != null) Stop();   // a loop that died on a socket error: close it before rebinding

        // Android/Quest drops broadcast packets at the Wi-Fi driver unless a
        // MulticastLock is held (loopback bypasses this, which is why localhost
        // testing worked). No-op on other platforms. Requires
        // CHANGE_WIFI_MULTICAST_STATE in the manifest.
        global::DreamPark.NetPlatform.AcquireMulticastLock();

        // Oct 11 2026 (net-arbiter, FINDINGS "E"): running only once the bind
        // succeeded. It used to be set first, so a bind that threw left
        // IsRunning true with no socket — and every StartDiscovery() returned
        // early on it forever. Now a failed bind reports not-running and the
        // arbiter retries.
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.LogWarning($"[DreamBox] Discovery: could not bind :{BeaconPort} ({ex.Message}) — will retry.");
            try { _udp?.Close(); } catch { }
            _udp = null;
            global::DreamPark.NetPlatform.ReleaseMulticastLock();
            return;
        }

        LastError = null;
        _running = true;
        _listenThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "DiscoveryListener"
        };
        _listenThread.Start();

        Debug.Log($"[DreamBox] Discovery: listening on :{BeaconPort}...");
    }

    public void Stop()
    {
        if (!_running && _udp == null) return;
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
        _listenThread = null;
        global::DreamPark.NetPlatform.ReleaseMulticastLock();
    }

    public void Dispose() => Stop();

    private void ListenLoop()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref remoteEP);
                double receivedAt = Clock;
                // Capture before anything else can reassign remoteEP.
                string sourceHost = remoteEP.Address?.ToString() ?? "";
                string json = Encoding.UTF8.GetString(data);

                var obj = new JSONObject(json);
                if (obj == null || obj.type != JSONObject.Type.Object) continue;

                var serviceField = obj.GetField("service");
                if (serviceField == null || serviceField.stringValue != ServiceFilter) continue;

                var hostField = obj.GetField("host");
                var portField = obj.GetField("port");
                var keyField = obj.GetField("key");
                var idField = obj.GetField("dreamboxId");
                var hostTypeField = obj.GetField("hostType");
                var hostIdField = obj.GetField("hostId");
                var parkIdField = obj.GetField("parkId");
                var seqField = obj.GetField("seq");
                var vField = obj.GetField("v");
                var channelField = obj.GetField("ch");
                // Optional, and additive by construction — an absent field is 0,
                // which every reader already has to treat as "not advertised".
                // No version gate and no coordination with the Pi: a relay that
                // does not send it keeps working exactly as before, and starts
                // being believed the day it does.
                var capField = obj.GetField("msgCap");

                if (hostField == null || portField == null) continue;

                var info = new BeaconInfo
                {
                    host = hostField.stringValue,
                    port = portField.intValue,
                    key = keyField != null ? keyField.stringValue : "",
                    dreamboxId = idField != null ? idField.stringValue : "",
                    hostType = hostTypeField != null ? hostTypeField.stringValue : "",
                    hostId = hostIdField != null ? hostIdField.stringValue : "",
                    parkId = parkIdField != null ? parkIdField.stringValue : "",
                    seq = seqField != null ? seqField.intValue : 0,
                    v = vField != null ? vField.intValue : 0,
                    channel = channelField != null ? channelField.stringValue : "",
                    msgCap = capField != null ? capField.intValue : 0,
                    sourceHost = sourceHost,
                    receivedAt = receivedAt
                };

                // Unfiltered feed for the arbiter (every valid beacon).
                OnBeacon?.Invoke(info);

                // If a filter is set, skip beacons from other DreamBoxes
                if (!string.IsNullOrEmpty(dreamboxIdFilter) &&
                    info.dreamboxId != dreamboxIdFilter)
                {
                    continue;
                }

                // Per-beacon (1 Hz per host on the LAN) — verbose only.
                global::DreamPark.NetLog.V($"[DreamBox] Discovery: found relay at {info.host}:{info.port} (dreamboxId={info.dreamboxId}, hostType={info.hostType}, hostId={info.hostId}, ch={info.channel})"
                    + (info.sourceHost != info.host ? $" ⚠ beacon came from {info.sourceHost}, not the advertised {info.host}" : ""));
                OnRelayDiscovered?.Invoke(info);
            }
            catch (SocketException) when (!_running)
            {
                // Expected when Stop() closes the socket
                break;
            }
            catch (SocketException ex)
            {
                // Oct 11 2026 (net-arbiter, NR4): the socket itself failed while we
                // still wanted it — iOS reclaims a suspended app's sockets (recv
                // then fails with ENOTCONN on every call). Looping here was a hot
                // spin of warnings that never heard another beacon while IsRunning
                // stayed true. Stop instead; the owner sees IsRunning == false and
                // binds a fresh socket.
                LastError = ex.Message;
                Debug.LogWarning($"[DreamBox] Discovery socket failed ({ex.SocketErrorCode}: {ex.Message}) — listener stopped; the owner restarts it.");
                _running = false;
                break;
            }
            catch (ObjectDisposedException)
            {
                _running = false;   // closed under us: report it, so the owner rebinds
                break;
            }
            catch (Exception ex)
            {
                if (_running)
                    Debug.LogWarning($"[DreamBox] Discovery error: {ex.Message}");
            }
        }
    }
}
