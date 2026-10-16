using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Broadcasts dream-pub discovery beacons on UDP :7700 at 1 Hz while a peer
    /// host is running — the peer-host counterpart of the DreamBox kiosk beacon.
    ///
    /// Beacon format is backward compatible with DiscoveryListener's parser
    /// (old clients read service/host/port/key and ignore the rest):
    ///   {"service":"dream-pub","host":"192.168.x.x","port":7777,"key":"...",
    ///    "hostType":"peer","hostId":"...","parkId":"...","seq":123}
    ///
    /// Plain class: the owner (NetSessionArbiter) calls <see cref="Tick"/> every
    /// frame; sends are non-blocking single datagrams. Sending broadcast needs
    /// no MulticastLock (the lock is only required to *receive*).
    /// </summary>
    public class BeaconBroadcaster : IDisposable
    {
        public const float IntervalSeconds = 1f;
        const float IpRefreshSeconds = 5f;

        /// <summary>
        /// Peer-host protocol version, advertised in the beacon. Once this ships
        /// in the SDK the beacon + wire format are a compatibility contract with
        /// creator builds in the wild: bump this on breaking changes — clients
        /// ignore beacons from a newer major version instead of misbehaving.
        ///
        /// 2 (Oct 2026): NetIds changed — the owning title's content id is mixed
        /// into every hashed AND explicit id, Sequences stamp NetScopes — and the
        /// session gained DreamParkSession / SharedState traffic. A v1 and a v2
        /// headset compute different ids for the same object, so they must never
        /// share a peer session: each side now ignores the other's beacons
        /// (NetSessionArbiter rejects any version but its own).
        /// </summary>
        public const int ProtocolVersion = 2;

        public bool IsRunning { get; private set; }
        public int Seq { get; private set; }
        public string AdvertisedHost { get; private set; }

        UdpClient _udp;
        IPEndPoint _broadcastEP;
        string _hostId, _parkId, _key, _channel;
        int _port;
        float _nextSendTime;
        float _nextIpRefreshTime;

        /// <summary>Start advertising. Returns false if no LAN IP could be found.</summary>
        public bool Start(string hostId, string parkId, int relayPort, string key, string channel = "prod")
        {
            if (IsRunning) return true;

            AdvertisedHost = NetPlatform.GetLocalIPv4();
            if (string.IsNullOrEmpty(AdvertisedHost))
            {
                Debug.LogWarning("[Beacon] No LAN IPv4 found — cannot advertise (Wi-Fi down or AP-isolated?). " +
                                 $"Interfaces seen: {NetPlatform.DescribeCandidates()}");
                return false;
            }

            _hostId = hostId;
            _parkId = parkId ?? "";
            _port = relayPort;
            _key = key ?? "";
            _channel = string.IsNullOrEmpty(channel) ? "prod" : channel;
            Seq = 0;

            _udp = new UdpClient();
            _udp.EnableBroadcast = true;
            _broadcastEP = new IPEndPoint(IPAddress.Broadcast, DiscoveryListener.BeaconPort);

            _nextSendTime = 0f;                                   // send immediately
            _nextIpRefreshTime = Time.unscaledTime + IpRefreshSeconds;
            IsRunning = true;

            // Every joining peer is told to connect to exactly this address, so
            // print the runners-up too. An advertised IP on the wrong interface
            // is invisible from the host's own side — it can always reach itself
            // — and costs an afternoon of packet capture from the other side.
            // One line at host time turns that into a glance.
            Debug.Log($"[Beacon] Advertising {AdvertisedHost}:{_port} as peer host {_hostId}. " +
                      $"Candidates: {NetPlatform.DescribeCandidates()}");
            return true;
        }

        /// <summary>Call every frame from the main thread while hosting.</summary>
        public void Tick()
        {
            if (!IsRunning) return;

            float now = Time.unscaledTime;
            if (now < _nextSendTime) return;
            _nextSendTime = now + IntervalSeconds;

            // Wi-Fi roam/reconnect can change our address; refresh occasionally.
            if (now >= _nextIpRefreshTime)
            {
                _nextIpRefreshTime = now + IpRefreshSeconds;
                var ip = NetPlatform.GetLocalIPv4();
                if (!string.IsNullOrEmpty(ip) && ip != AdvertisedHost)
                {
                    // Clients already connected keep their socket; only joiners
                    // read the beacon. Say it out loud either way — an address
                    // that changes under a live session is worth knowing about.
                    Debug.Log($"[Beacon] Advertised address changed {AdvertisedHost} → {ip}.");
                    AdvertisedHost = ip;
                }
            }

            Seq++;
            string json =
                "{\"service\":\"dream-pub\"" +
                ",\"host\":\"" + AdvertisedHost + "\"" +
                ",\"port\":" + _port +
                ",\"key\":\"" + _key + "\"" +
                ",\"hostType\":\"peer\"" +
                ",\"hostId\":\"" + _hostId + "\"" +
                ",\"parkId\":\"" + _parkId + "\"" +
                ",\"seq\":" + Seq +
                ",\"v\":" + ProtocolVersion +
                ",\"ch\":\"" + _channel + "\"" +
                // What THIS relay actually enforces, so clients stop inferring it
                // from which rung of the ladder they think they are on.
                ",\"msgCap\":" + PeerRelayServer.MaxMessagesPerPeerPerSecond +
                "}";

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                _udp.Send(data, data.Length, _broadcastEP);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Beacon] Send failed: {e.Message}");
            }
        }

        /// <summary>The park id this beacon advertises right now.</summary>
        public string ParkId => _parkId;

        /// <summary>
        /// Oct 11 2026 (net-arbiter, FINDINGS "Restart didn't rejoin"): change the
        /// advertised park id in place — same relay, same key, same clients. A
        /// headset that started hosting before its park loaded (empty id) only
        /// learns the park now; tearing the session down for that is what made
        /// a restarted room fail to re-form. The next beacon carries it.
        /// </summary>
        public void SetParkId(string parkId)
        {
            parkId ??= "";
            if (_parkId == parkId) return;
            Debug.Log($"[Beacon] Park id '{_parkId}' → '{parkId}' (in place, session kept).");
            _parkId = parkId;
            _nextSendTime = 0f;   // tell listeners now, not in up to 1 s
        }

        /// <summary>
        /// Re-read the LAN address on the next Tick instead of within 5 s — the
        /// owner calls this when it knows the network just changed.
        /// </summary>
        public void RefreshAddressNow()
        {
            _nextIpRefreshTime = 0f;
            _nextSendTime = 0f;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
            Debug.Log("[Beacon] Stopped.");
        }

        public void Dispose() => Stop();
    }
}
