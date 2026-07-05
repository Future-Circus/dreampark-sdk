using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// In-process LiteNetLib relay — the peer-host counterpart of the DreamBox
    /// kiosk relay (Tools/DreamBoxServer). Mirrors its observable contract
    /// exactly: accept connections carrying the session key, then rebroadcast
    /// every inbound message verbatim to all *other* connected peers,
    /// ReliableOrdered. No parsing, no state, no authority — a dumb pipe, so
    /// gameplay cannot tell which host type it is talking to.
    ///
    /// Plain class: the owner (NetSessionArbiter) must call <see cref="Poll"/>
    /// every frame. The host headset connects to its own relay via
    /// 127.0.0.1 through the unmodified DreamBoxClient.
    /// </summary>
    public class PeerRelayServer
    {
        public const int DefaultPort = 7777;
        /// <summary>How many ports above DefaultPort to try if taken.</summary>
        const int PortSearchRange = 8;

        // Same inbound cap as DreamBoxClient — untrusted LAN peers.
        const int MaxIncomingMessageBytes = 16 * 1024;
        // Per-peer inbound rate cap so one client can't flood the fan-out.
        const int MaxMessagesPerPeerPerSecond = 60;

        /// <summary>
        /// Soft cap, tunable per venue — NOT a system limit. The Wi-Fi medium is
        /// the real constraint: relay traffic scales O(N²) (N senders × N-1
        /// recipients), so 16 keeps worst-case fan-out comfortably inside one
        /// headset's radio budget. Raise it for a DreamBox-class host.
        /// </summary>
        public int MaxPeers = 16;

        /// <summary>
        /// Hardening: connections allowed per source IP. Stops one LAN device
        /// from occupying every peer slot. 2 (not 1) so a desktop running two
        /// Editor test clients still works. Loopback is exempt — the host's own
        /// self-connect must never be blocked.
        /// </summary>
        public int MaxPeersPerAddress = 2;

        public bool IsRunning => _server != null && _server.IsRunning;
        public int Port { get; private set; }
        public string Key { get; private set; }
        public int PeerCount => _server?.ConnectedPeersCount ?? 0;
        public int RelayedMessageCount { get; private set; }

        public event Action<string> OnEventLog;

        NetManager _server;
        EventBasedNetListener _listener;

        // Per-peer rate limiting: peer.Id -> (windowStartMs, countInWindow)
        readonly Dictionary<int, RateWindow> _rate = new();
        struct RateWindow { public int startMs; public int count; }

        // Reused for non-alloc fan-out (this LiteNetLib fork has no ConnectedPeerList).
        readonly List<NetPeer> _fanout = new();

        /// <summary>
        /// Start listening. Tries <paramref name="preferredPort"/> first, then
        /// increments up to +8 (advertise the returned actual port in the beacon).
        /// Returns false if no port could be bound.
        /// </summary>
        public bool Start(string key, int preferredPort = DefaultPort)
        {
            if (IsRunning)
            {
                Debug.LogWarning("[PeerRelay] Start called while already running.");
                return true;
            }

            Key = key;
            _listener = new EventBasedNetListener();
            _server = new NetManager(_listener);

            bool bound = false;
            for (int port = preferredPort; port <= preferredPort + PortSearchRange; port++)
            {
                // LiteNetLib binds 0.0.0.0 when started with just a port — binding a
                // specific interface IP is the classic "server can't bind" Quest failure.
                if (_server.Start(port))
                {
                    Port = port;
                    bound = true;
                    break;
                }
            }

            if (!bound)
            {
                Debug.LogError($"[PeerRelay] Could not bind any port in {preferredPort}..{preferredPort + PortSearchRange}.");
                _server = null;
                _listener = null;
                return false;
            }

            _listener.ConnectionRequestEvent += request =>
            {
                if (_server.ConnectedPeersCount >= MaxPeers)
                {
                    request.Reject();
                    return;
                }

                // Per-IP cap: one rogue device can't exhaust the peer slots.
                var addr = request.RemoteEndPoint.Address;
                if (!IPAddress.IsLoopback(addr))
                {
                    _server.GetConnectedPeers(_fanout);
                    int fromSameAddress = 0;
                    foreach (var p in _fanout)
                        if (p.Address.Equals(addr)) fromSameAddress++;

                    if (fromSameAddress >= MaxPeersPerAddress)
                    {
                        Debug.LogWarning($"[PeerRelay] Rejecting {request.RemoteEndPoint}: per-address cap ({MaxPeersPerAddress}).");
                        request.RejectForce();
                        return;
                    }
                }

                request.AcceptIfKey(Key);
            };

            _listener.PeerConnectedEvent += peer =>
            {
                Debug.Log($"[PeerRelay] Peer connected: {peer} ({_server.ConnectedPeersCount}/{MaxPeers})");
                OnEventLog?.Invoke($"PEER JOIN {peer} ({_server.ConnectedPeersCount})");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _rate.Remove(peer.Id);
                Debug.Log($"[PeerRelay] Peer disconnected: {peer} ({info.Reason})");
                OnEventLog?.Invoke($"PEER LEAVE {peer} ({info.Reason})");
            };

            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try
                {
                    if (reader.AvailableBytes > MaxIncomingMessageBytes) return;
                    if (!AllowRate(peer.Id)) return;

                    // Forward the raw payload untouched — preserves the client's
                    // length-prefixed string encoding without re-serializing.
                    byte[] raw = reader.GetRemainingBytes();
                    if (raw == null || raw.Length == 0) return;

                    var writer = new NetDataWriter();
                    writer.Put(raw);

                    _server.GetConnectedPeers(_fanout);
                    int forwarded = 0;
                    foreach (var other in _fanout)
                    {
                        if (other.Id == peer.Id) continue;
                        other.Send(writer, DeliveryMethod.ReliableOrdered);
                        forwarded++;
                    }
                    RelayedMessageCount++;

                    // Verbose: "is the relay forwarding?" answerable from the log
                    // without a debugger. First 5 in full cadence, then every 50th.
                    if (RelayedMessageCount <= 5 || RelayedMessageCount % 50 == 0)
                        NetLog.V($"[PeerRelay] Relayed msg #{RelayedMessageCount}: {raw.Length}B from {peer} → {forwarded} peer(s).");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PeerRelay] Relay failed: {e.Message}");
                }
                finally
                {
                    reader.Recycle();
                }
            };

            _listener.NetworkErrorEvent += (endpoint, error) =>
            {
                Debug.LogWarning($"[PeerRelay] Network error: {error}");
            };

            Debug.Log($"[PeerRelay] Hosting on 0.0.0.0:{Port} (max {MaxPeers} peers).");
            OnEventLog?.Invoke($"HOSTING :{Port}");
            return true;
        }

        /// <summary>Pump network events. Call every frame while running.</summary>
        public void Poll() => _server?.PollEvents();

        public void Stop()
        {
            if (_server == null) return;
            try { _server.Stop(); }
            catch (Exception e) { Debug.LogWarning($"[PeerRelay] Stop failed: {e.Message}"); }
            _server = null;
            _listener = null;
            _rate.Clear();
            Debug.Log("[PeerRelay] Stopped.");
            OnEventLog?.Invoke("STOPPED");
        }

        bool AllowRate(int peerId)
        {
            int now = Environment.TickCount;
            if (!_rate.TryGetValue(peerId, out var w) || now - w.startMs >= 1000)
            {
                _rate[peerId] = new RateWindow { startMs = now, count = 1 };
                return true;
            }
            if (w.count >= MaxMessagesPerPeerPerSecond) return false;
            w.count++;
            _rate[peerId] = w;
            return true;
        }
    }
}
