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
        /// </summary>
        public const int ProtocolVersion = 1;

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
                Debug.LogWarning("[Beacon] No LAN IPv4 found — cannot advertise (Wi-Fi down or AP-isolated?).");
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

            Debug.Log($"[Beacon] Advertising {AdvertisedHost}:{_port} as peer host {_hostId}.");
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
                if (!string.IsNullOrEmpty(ip)) AdvertisedHost = ip;
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
