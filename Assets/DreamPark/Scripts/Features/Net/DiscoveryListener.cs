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
    }

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

    public void Start()
    {
        if (_running) return;

        _running = true;

        // Android/Quest drops broadcast packets at the Wi-Fi driver unless a
        // MulticastLock is held (loopback bypasses this, which is why localhost
        // testing worked). No-op on other platforms. Requires
        // CHANGE_WIFI_MULTICAST_STATE in the manifest.
        global::DreamPark.NetPlatform.AcquireMulticastLock();

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));

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
                    sourceHost = sourceHost
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
            catch (ObjectDisposedException)
            {
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
