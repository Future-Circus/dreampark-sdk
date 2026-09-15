using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Platform helpers for LAN networking.
    ///
    /// MulticastLock: Android's Wi-Fi stack drops non-unicast packets by default
    /// (battery saver), so a socket bound to the beacon port never sees UDP
    /// broadcasts on real Wi-Fi. Acquiring WifiManager.MulticastLock disables that
    /// filter. Requires CHANGE_WIFI_MULTICAST_STATE in the manifest (normal
    /// permission, no user prompt). Loopback traffic bypasses the filter, which is
    /// why localhost testing never surfaced this.
    ///
    /// Call Acquire/Release from the main thread (they attach JNI). Reference
    /// counted here so DiscoveryListener and future callers can nest safely.
    /// </summary>
    public static class NetPlatform
    {
        static int _lockRefCount;

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject _multicastLock;

        public static void AcquireMulticastLock()
        {
            _lockRefCount++;
            if (_multicastLock != null) return;

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                _multicastLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "dreampark-discovery");
                _multicastLock.Call("setReferenceCounted", false);
                _multicastLock.Call("acquire");
                Debug.Log("[NetPlatform] MulticastLock acquired.");
            }
            catch (Exception e)
            {
                // Some chipsets deliver broadcast without the lock — degrade, don't die.
                Debug.LogWarning($"[NetPlatform] MulticastLock acquire failed (discovery may not receive beacons): {e.Message}");
                _multicastLock = null;
            }
        }

        public static void ReleaseMulticastLock()
        {
            _lockRefCount = Mathf.Max(0, _lockRefCount - 1);
            if (_lockRefCount > 0 || _multicastLock == null) return;

            try { _multicastLock.Call("release"); }
            catch (Exception e) { Debug.LogWarning($"[NetPlatform] MulticastLock release failed: {e.Message}"); }
            finally
            {
                _multicastLock.Dispose();
                _multicastLock = null;
                Debug.Log("[NetPlatform] MulticastLock released.");
            }
        }
        // ------------------------------------------------------------------
        // Wi-Fi low-latency lock (peer host only)
        // ------------------------------------------------------------------
        // Android Wi-Fi power save idles the radio between packets, adding
        // 100-300 ms latency spikes — tolerable for a client, bad for the relay
        // every player routes through. WIFI_MODE_FULL_LOW_LATENCY (API 29+;
        // Horizon OS qualifies) pins the radio while held. Requires WAKE_LOCK
        // in the manifest. Hold ONLY while hosting — it costs battery.

        static AndroidJavaObject _wifiLock;
        const int WIFI_MODE_FULL_LOW_LATENCY = 4;

        public static void AcquireWifiLowLatencyLock()
        {
            if (_wifiLock != null) return;
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                _wifiLock = wifi.Call<AndroidJavaObject>("createWifiLock",
                    WIFI_MODE_FULL_LOW_LATENCY, "dreampark-peerhost");
                _wifiLock.Call("setReferenceCounted", false);
                _wifiLock.Call("acquire");
                Debug.Log("[NetPlatform] Wi-Fi low-latency lock acquired (hosting).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetPlatform] WifiLock acquire failed (host latency may spike): {e.Message}");
                _wifiLock = null;
            }
        }

        public static void ReleaseWifiLowLatencyLock()
        {
            if (_wifiLock == null) return;
            try { _wifiLock.Call("release"); }
            catch (Exception e) { Debug.LogWarning($"[NetPlatform] WifiLock release failed: {e.Message}"); }
            finally
            {
                _wifiLock.Dispose();
                _wifiLock = null;
                Debug.Log("[NetPlatform] Wi-Fi low-latency lock released.");
            }
        }
#else
        public static void AcquireMulticastLock() { _lockRefCount++; }
        public static void ReleaseMulticastLock() { _lockRefCount = Math.Max(0, _lockRefCount - 1); }
        public static void AcquireWifiLowLatencyLock() { }
        public static void ReleaseWifiLowLatencyLock() { }
#endif

        /// <summary>
        /// LAN IPv4 to advertise in beacons.
        ///
        /// This address is the ONLY thing a joining peer has to go on: the relay
        /// binds 0.0.0.0 and is reachable on every interface, but the beacon
        /// names exactly one. Pick a Mac's VPN tunnel or a developer's container
        /// bridge here and every headset on the real Wi-Fi will hear the beacon,
        /// try to connect to an address that does not route, hang until the join
        /// timeout, and self-elect — two hosts in one park, no traffic between
        /// them, and nothing in any log saying why. That is not hypothetical; it
        /// is what this function used to do.
        ///
        /// The old rule was "first interface whose type is Wireless80211 or
        /// Ethernet". It has two failure modes and hit both:
        ///   - Mono reports NetworkInterfaceType as Unknown for wlan0 on Android,
        ///     so on Quest NOTHING is ever "preferred" and the fallback (first
        ///     enumerated address, any interface) silently decides.
        ///   - On macOS several interfaces report Ethernet, so "first" is really
        ///     "whatever order the OS listed them in" — a VPN or virtual adapter
        ///     that sorts before en0 wins outright.
        ///
        /// So rank instead of first-match, and lead with the signal that actually
        /// means "this interface reaches the LAN": it has a default gateway. A
        /// tunnel or a container bridge normally has none, and an interface type
        /// Mono cannot identify still scores correctly. Returns null if nothing
        /// suitable is found.
        /// </summary>
        public static string GetLocalIPv4() => PickBest(out _);

        /// <summary>
        /// Every candidate considered, with its score, formatted for one log
        /// line. Kept next to the pick so "why is it advertising THAT?" is
        /// answerable from the console instead of from a packet capture.
        /// </summary>
        public static string DescribeCandidates()
        {
            PickBest(out var described);
            return described;
        }

        static string PickBest(out string description)
        {
            description = "(no candidates — Wi-Fi down or AP-isolated?)";
            string bestIp = null;
            string anyAddress = null;   // last-resort: better a bad guess than no beacon
            int bestScore = int.MinValue;
            var lines = new System.Collections.Generic.List<string>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    string niName;
                    try { niName = ni.Name ?? ""; } catch { niName = ""; }

                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { continue; }

                    // A default gateway on this interface means packets to
                    // arbitrary hosts leave THROUGH it. That is the property we
                    // actually want, and the one no interface-type enum can tell
                    // us — least of all on Android, where the enum is Unknown.
                    //
                    // Guarded on its own, and deliberately so: GatewayAddresses
                    // is the least portable thing in this file. Mono implements
                    // it per-platform (parsing /proc/net/route, or the BSD route
                    // table) and it can throw or come back empty depending on
                    // the runtime and the sandbox. A ranking signal must never
                    // be able to take the whole enumeration down with it — the
                    // cost of losing it is a slightly worse guess, and the cost
                    // of throwing here is no beacon at all.
                    bool hasGateway = false;
                    try
                    {
                        foreach (var gw in props.GatewayAddresses)
                        {
                            if (gw?.Address == null) continue;
                            if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (gw.Address.Equals(IPAddress.Any)) continue;
                            hasGateway = true;
                            break;
                        }
                    }
                    catch { /* signal unavailable on this runtime — rank without it */ }

                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(addr.Address)) continue;
                        var ip = addr.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;   // link-local

                        anyAddress ??= ip;

                        int score = 0;
                        if (hasGateway) score += 100;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                        else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 10;
                        if (LooksVirtual(niName, ip)) score -= 200;

                        lines.Add($"{niName}={ip} [{score}{(hasGateway ? " gw" : "")} {ni.NetworkInterfaceType}]");

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestIp = ip;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetPlatform] LAN IP lookup failed partway: {e.Message}");
            }

            if (lines.Count > 0) description = string.Join(", ", lines);

            // Ranking is an improvement on the old behaviour, not a precondition
            // for it. If enumeration died before it scored anything, hand back
            // whatever address we did manage to see: a questionable beacon can
            // still be joined (the receiver falls back to the source address),
            // whereas returning null stops BeaconBroadcaster.Start, which stops
            // StartHosting, and the device drops off the LAN entirely.
            return bestIp ?? anyAddress;
        }

        /// <summary>
        /// Interfaces that exist to reach something other than the LAN. The name
        /// prefixes cover the usual suspects on the three platforms we ship on;
        /// the address ranges catch the ones that rename themselves.
        ///
        /// This is a tiebreaker, not a security boundary — a virtual interface
        /// that somehow owns the only default route still outscores a real one
        /// with no gateway, and in that situation that is the right answer.
        /// </summary>
        static bool LooksVirtual(string name, string ip)
        {
            if (!string.IsNullOrEmpty(name))
            {
                string n = name.ToLowerInvariant();
                if (n.StartsWith("utun") || n.StartsWith("tun") || n.StartsWith("tap") ||
                    n.StartsWith("ppp") || n.StartsWith("bridge") || n.StartsWith("docker") ||
                    n.StartsWith("veth") || n.StartsWith("vmnet") || n.StartsWith("vmenet") ||
                    n.StartsWith("vboxnet") || n.StartsWith("anpi") || n.StartsWith("llw") ||
                    n.StartsWith("awdl") || n.StartsWith("dummy") || n.StartsWith("rmnet") ||
                    n.StartsWith("cni") || n.StartsWith("flannel") || n.StartsWith("zt"))
                    return true;
            }

            // Tailscale (100.64/10 CGNAT), Docker's default bridge (172.17/16),
            // and Android's USB / Wi-Fi tether ranges (192.168.42-43).
            if (ip.StartsWith("100.")) return true;
            if (ip.StartsWith("172.17.")) return true;
            if (ip.StartsWith("192.168.42.") || ip.StartsWith("192.168.43.")) return true;

            return false;
        }
    }
}
