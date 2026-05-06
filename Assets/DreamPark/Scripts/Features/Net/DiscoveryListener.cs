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
    }

    /// <summary>Fired on background thread when a valid beacon is received.</summary>
    public event Action<BeaconInfo> OnRelayDiscovered;

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
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
        _listenThread = null;
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
                string json = Encoding.UTF8.GetString(data);

                var obj = new JSONObject(json);
                if (obj == null || obj.type != JSONObject.Type.Object) continue;

                var serviceField = obj.GetField("service");
                if (serviceField == null || serviceField.stringValue != ServiceFilter) continue;

                var hostField = obj.GetField("host");
                var portField = obj.GetField("port");
                var keyField = obj.GetField("key");
                var idField = obj.GetField("dreamboxId");

                if (hostField == null || portField == null) continue;

                var info = new BeaconInfo
                {
                    host = hostField.stringValue,
                    port = portField.intValue,
                    key = keyField != null ? keyField.stringValue : "",
                    dreamboxId = idField != null ? idField.stringValue : ""
                };

                // If a filter is set, skip beacons from other DreamBoxes
                if (!string.IsNullOrEmpty(dreamboxIdFilter) &&
                    info.dreamboxId != dreamboxIdFilter)
                {
                    continue;
                }

                Debug.Log($"[DreamBox] Discovery: found relay at {info.host}:{info.port} (dreamboxId={info.dreamboxId})");
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
