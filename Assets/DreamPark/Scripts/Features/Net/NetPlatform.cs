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

        // ------------------------------------------------------------------
        // LAN hold (Oct 29 2026, the DreamPark Portal iPhone app)
        // ------------------------------------------------------------------
        /// <summary>
        /// A host-app switch that keeps this stack off the local network until the host
        /// releases it: no beacon discovery bind/receive (DiscoveryListener), no LiteNetLib
        /// client socket or relay connect (DreamBoxClient), no arbiter search (NetSessionArbiter).
        /// iOS shows its Local Network permission alert on the first local-network operation, so
        /// an app that wants the alert only after a user action (the portal app: after Join)
        /// holds the LAN until then.
        ///
        /// Null (the default: SDK projects, the main iOS app, Quest) = never held, nothing changes.
        /// A host installs it before the scene loads (RuntimeInitializeOnLoadMethod BeforeSceneLoad)
        /// so the arbiter's OnEnable and DreamBoxClient.Start already see it. Held components poll
        /// <see cref="LanHeld"/> each frame; the first frame it reads false they start exactly as
        /// they would have at boot. A hold that throws reads as not held (today's behaviour).
        /// </summary>
        public static Func<bool> LanHold;

        /// <summary>True while the host app holds the LAN (see <see cref="LanHold"/>).</summary>
        public static bool LanHeld
        {
            get
            {
                var hold = LanHold;
                if (hold == null) return false;
                try { return hold(); }
                catch (Exception e) { Debug.LogWarning($"[NetPlatform] LAN hold check failed (treated as not held): {e.Message}"); return false; }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetLanHold() => LanHold = null;

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
        ///
        /// Nov 2 2026 (flags-perf-nov2, mt-net): cached. A pick younger than
        /// <see cref="IpCacheMs"/> is handed back as-is, so StartHosting's 2–3
        /// back-to-back lookups enumerate the interfaces once. Periodic watchers
        /// use <see cref="PeekLocalIPv4"/> instead, which never enumerates on the
        /// calling (main) thread. <see cref="InvalidateLocalIPv4"/> on a real
        /// network-change signal.
        /// </summary>
        public static string GetLocalIPv4()
        {
            lock (_ipLock)
            {
                if (_ipComputedAt != 0 && unchecked(Environment.TickCount - _ipComputedAt) < IpCacheMs)
                    return _cachedIp;
            }
            var ip = PickBest(false, out _);
            StoreIp(ip);
            return ip;
        }

        /// <summary>
        /// Watcher form (NetSessionArbiter 2 s LAN watch, BeaconBroadcaster 5 s
        /// refresh): returns the last pick immediately and, when it is older than
        /// <paramref name="maxAgeSeconds"/>, re-enumerates on a worker thread.
        /// A change therefore shows up one poll later than before (≤ one watch
        /// period) — in exchange the main thread never walks the interface list.
        /// Falls back to a synchronous lookup only when nothing was ever cached.
        /// </summary>
        public static string PeekLocalIPv4(float maxAgeSeconds)
        {
            int computedAt;
            string ip;
            lock (_ipLock) { computedAt = _ipComputedAt; ip = _cachedIp; }
            if (computedAt == 0) return GetLocalIPv4();
            if (unchecked(Environment.TickCount - computedAt) >= (int)(maxAgeSeconds * 1000f)
                && System.Threading.Interlocked.CompareExchange(ref _ipRefreshing, 1, 0) == 0)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { StoreIp(PickBest(false, out _)); }
                    catch (Exception e) { Debug.LogWarning($"[NetPlatform] Background LAN IP lookup failed: {e.Message}"); }
                    finally { System.Threading.Interlocked.Exchange(ref _ipRefreshing, 0); }
                });
            }
            return ip;
        }

        /// <summary>Forget the cached pick (network-change signal): the next lookup re-enumerates.</summary>
        public static void InvalidateLocalIPv4()
        {
            lock (_ipLock) _ipComputedAt = 0;
        }

        const int IpCacheMs = 1000;
        static readonly object _ipLock = new object();
        static string _cachedIp;
        static int _ipComputedAt;      // Environment.TickCount; 0 = never / invalidated
        static int _ipRefreshing;

        static void StoreIp(string ip)
        {
            int now = Environment.TickCount;
            if (now == 0) now = 1;
            lock (_ipLock) { _cachedIp = ip; _ipComputedAt = now; }
        }

        /// <summary>
        /// Every candidate considered, with its score, formatted for one log
        /// line. Kept next to the pick so "why is it advertising THAT?" is
        /// answerable from the console instead of from a packet capture.
        /// Only for log lines — it always re-enumerates and builds the strings.
        /// </summary>
        public static string DescribeCandidates()
        {
            StoreIp(PickBest(true, out var described));
            return described;
        }

        static string PickBest(bool describe, out string description)
        {
            description = "(no candidates — Wi-Fi down or AP-isolated?)";
            string bestIp = null;
            string anyAddress = null;   // last-resort: better a bad guess than no beacon
            int bestScore = int.MinValue;
            var lines = describe ? new System.Collections.Generic.List<string>() : null;

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
                    //
                    // Nov 2 2026 (flags-perf-nov2): not on Android. There Mono
                    // reads /proc/net/route, which SELinux denies for apps — an
                    // avc denial in logcat plus a thrown exception per interface,
                    // every lookup, so the signal was never available on Quest
                    // anyway. Scoring without it is exactly what Quest already did.
                    bool hasGateway = false;
#if !(UNITY_ANDROID && !UNITY_EDITOR)
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
#endif

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

                        if (describe) lines.Add($"{niName}={ip} [{score}{(hasGateway ? " gw" : "")} {ni.NetworkInterfaceType}]");

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

            if (lines != null && lines.Count > 0) description = string.Join(", ", lines);

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

    /// <summary>
    /// QUEUED TRACE FILE (Nov 2 2026, round flags-perf-nov2): the always-on trace logs
    /// (vps-trace, diorama-trace, title-switch-trace, netpose-trace, qr-trace,
    /// content-load-trace) each did a FileInfo stat + File.AppendAllText — open, append,
    /// close — per line ON THE CALLING THREAD, usually the main thread. This is BootTrace's
    /// approach (QuestBootStandUp.cs) shared: Append only enqueues; one Task.Run drains the
    /// queue (one stat + one append per batch), so line order is kept and the main thread
    /// never touches the disk. Rotation is unchanged per file (checked per batch).
    /// Every instance is drained synchronously on focus loss / pause and on quit.
    ///
    /// Lives in the SDK's Net folder (not core) because NetPoseTrace (SDK) uses it too.
    /// </summary>
    public sealed class QueuedTraceFile
    {
        public enum Overflow
        {
            /// Past MaxBytes the file becomes &lt;name&gt;.1.log (the previous .1 is deleted).
            Rotate,
            /// Past MaxBytes the file is deleted and starts again.
            Delete,
            /// Past MaxBytes only the newer half is kept.
            KeepNewerHalf,
        }

        readonly string _fileName;
        readonly string _rotatedName;
        readonly long _maxBytes;
        readonly Overflow _overflow;
        readonly string _failTag;
        readonly object _lock = new object();
        readonly object _writeGate = new object();
        readonly System.Collections.Generic.Queue<string> _pending = new System.Collections.Generic.Queue<string>();
        readonly System.Text.StringBuilder _chunk = new System.Text.StringBuilder();
        string _path;
        bool _flushing;
        volatile bool _failed;

        static readonly System.Collections.Generic.List<QueuedTraceFile> s_all = new System.Collections.Generic.List<QueuedTraceFile>();

        /// <param name="failTag">Non-null: the first write failure disables this trace with one
        /// "&lt;failTag&gt; trace disabled" warning (the old per-trace s_failed rule). Null: failures are silent and
        /// writing keeps trying (the old qr-trace / content-load-trace rule).</param>
        public QueuedTraceFile(string fileName, long maxBytes, Overflow overflow, string rotatedName = null, string failTag = null)
        {
            _fileName = fileName;
            _maxBytes = maxBytes;
            _overflow = overflow;
            _rotatedName = rotatedName;
            _failTag = failTag;
            lock (s_all) s_all.Add(this);
        }

        /// persistentDataPath/fileName, resolved on first use (persistentDataPath wants the main thread the first time).
        public string PathOnDisk
        {
            get
            {
                if (_path == null)
                {
                    try { _path = System.IO.Path.Combine(Application.persistentDataPath, _fileName); }
                    catch { _path = string.Empty; }
                }
                return _path;
            }
        }

        public bool Failed => _failed;

        /// Queue already-formatted text (end it with "\n"). Any thread; never blocks on IO.
        public void Append(string text)
        {
            if (_failed || string.IsNullOrEmpty(text)) return;
            if (string.IsNullOrEmpty(PathOnDisk)) return;
            lock (_lock)
            {
                _pending.Enqueue(text);
                if (_flushing) return;
                _flushing = true;
            }
            System.Threading.Tasks.Task.Run(DrainWorker);
        }

        void DrainWorker()
        {
            while (true)
            {
                lock (_writeGate)
                {
                    string chunk = TakeChunk(true);
                    if (chunk == null) return;
                    WriteChunk(chunk);
                }
            }
        }

        /// Write everything queued now, on this thread (pause / quit).
        public void FlushNow()
        {
            lock (_writeGate)
            {
                string chunk = TakeChunk(false);
                if (chunk != null) WriteChunk(chunk);
            }
        }

        /// Everything queued as one string, or null when empty (a worker then stops flushing).
        string TakeChunk(bool worker)
        {
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    if (worker) _flushing = false;
                    return null;
                }
                _chunk.Clear();
                while (_pending.Count > 0) _chunk.Append(_pending.Dequeue());
                return _chunk.ToString();
            }
        }

        void WriteChunk(string chunk)
        {
            if (_failed) return;
            string path = _path;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > _maxBytes)
                {
                    switch (_overflow)
                    {
                        case Overflow.Rotate:
                            string old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "",
                                _rotatedName ?? System.IO.Path.GetFileNameWithoutExtension(_fileName) + ".1.log");
                            if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                            System.IO.File.Move(path, old);
                            break;
                        case Overflow.Delete:
                            System.IO.File.Delete(path);
                            break;
                        case Overflow.KeepNewerHalf:
                            string text = System.IO.File.ReadAllText(path);
                            System.IO.File.WriteAllText(path, text.Substring(text.Length / 2));
                            break;
                    }
                }
                System.IO.File.AppendAllText(path, chunk);
            }
            catch (Exception e)
            {
                if (_failTag == null) return;   // silent traces keep trying, as before
                _failed = true;                  // never spam or throw from a trace
                lock (_lock) _pending.Clear();
                Debug.LogWarning(_failTag + " trace disabled: " + e.Message);
            }
        }

        public static void FlushAll()
        {
            QueuedTraceFile[] all;
            lock (s_all) all = s_all.ToArray();
            foreach (var t in all)
            {
                try { t.FlushNow(); } catch { }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void HookLifecycle()
        {
            Application.quitting -= FlushAll;
            Application.quitting += FlushAll;
            Application.focusChanged -= OnFocus;
            Application.focusChanged += OnFocus;
        }

        // Android/iOS pause and Quest doff arrive with focus loss first: drain then, the process may not come back.
        static void OnFocus(bool focused) { if (!focused) FlushAll(); }
    }
}
