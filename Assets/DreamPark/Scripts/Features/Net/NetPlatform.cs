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
        /// Best-guess LAN IPv4 for advertising in beacons. Prefers Wi-Fi/Ethernet
        /// interfaces that are up; skips loopback, link-local (169.254.*), and
        /// virtual adapters. Returns null if nothing suitable is found.
        /// </summary>
        public static string GetLocalIPv4()
        {
            string fallback = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = addr.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue; // link-local

                        bool preferred =
                            ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                            ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;

                        if (preferred) return ip;
                        fallback ??= ip;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetPlatform] LAN IP lookup failed: {e.Message}");
            }
            return fallback;
        }
    }
}
