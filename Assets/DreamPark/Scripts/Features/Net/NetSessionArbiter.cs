using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Owns the multiplayer fallback ladder (see dreampark-core
    /// Docs/LAN-PeerHost-Spec.md):
    ///
    ///   SEARCHING ── DreamBox beacon ─────────▶ CLIENT (dreambox)   [kiosk always wins]
    ///       │
    ///       ├── peer beacon ──────────────────▶ CLIENT (peer)
    ///       │
    ///       └── silence for T_listen ─────────▶ HOSTING (PeerRelayServer + beacon
    ///                                            + self-connect via 127.0.0.1)
    ///
    /// Host loss (beacon silence 3 s, connection drop, or host_leaving) triggers
    /// coordinator-free re-election: every client sorts the live hostIds the same
    /// way; rank 0 hosts immediately, rank n waits n × 750 ms and joins whichever
    /// beacon appears first. Two simultaneous hosts resolve deterministically —
    /// lowest hostId keeps hosting, the other yields.
    ///
    /// Add this component next to DreamBoxClient. When present, it takes over
    /// discovery (DreamBoxClient skips its own auto-discovery). Gameplay code and
    /// Lua are unaffected — they only ever see the DreamBoxClient connection.
    /// </summary>
    [RequireComponent(typeof(DreamBoxClient))]
    public class NetSessionArbiter : MonoBehaviour
    {
        public enum SessionState { Idle, Searching, ClientDreamBox, ClientPeer, Hosting, Reelection }

        [Header("Session")]
        [Tooltip("Scopes peer sessions so two groups in different parks on one LAN don't merge. Empty = match any.")]
        public string parkId = "";

        [Tooltip("Allow this device to self-elect as host. Hosting is compiled for Android (Quest) and the Editor; iOS builds are always client-only.")]
        public bool allowHosting = true;

        [Header("Timing (seconds)")]
        public float listenBaseSeconds = 3f;      // + deterministic 0..2 s jitter from hostId
        public float beaconStaleSeconds = 3f;     // host beacons are 1 Hz → 3 missed = dead
        public float reelectionStepSeconds = 0.75f;
        public float joinTimeoutSeconds = 10f;    // beacons flowing but connect never succeeds
        public float kioskGraceSeconds = 8f;      // kiosk silent + not connected → abandon kiosk

        public SessionState State { get; private set; } = SessionState.Idle;
        public string HostId { get; private set; }          // our own stable id
        public string CurrentHostId { get; private set; }   // who we're connected/connecting to

        /// <summary>
        /// Messages/sec/peer the current host says it enforces, or 0 when it does
        /// not advertise a cap (every kiosk today, and any pre-msgCap peer).
        /// Read straight off the beacon we joined from — this is the only
        /// authoritative source; everything else is inference.
        /// </summary>
        public int HostAdvertisedCap { get; private set; }
        public bool IsHost => State == SessionState.Hosting;
        public int HostedPeerCount => _relay?.PeerCount ?? 0;
        public event Action<SessionState> OnStateChanged;

        /// <summary>True while an enabled arbiter exists — DreamBoxClient defers discovery to it.</summary>
        public static bool ArbiterActive { get; private set; }

        /// <summary>The enabled arbiter instance (one per app).</summary>
        public static NetSessionArbiter Instance { get; private set; }

        /// <summary>
        /// Set the park scope from wherever park identity is known (project code
        /// calls this on park load; kiosk pairing sets it automatically via
        /// SessionContext). Sessions only form between devices in the same park.
        /// If the park changes mid-session, the arbiter leaves the old session
        /// and re-runs the ladder — sessions are per-park by design.
        /// </summary>
        public static void SetParkContext(string newParkId)
        {
            var a = Instance;
            if (a == null) return;
            newParkId ??= "";
            if (a.parkId == newParkId) return;

            Debug.Log($"[Arbiter] Park context: '{a.parkId}' → '{newParkId}'");
            a.parkId = newParkId;

            // Membership in the old park's session is no longer valid.
            if (a.State == SessionState.Hosting)
            {
                a.StopHosting(announce: true);
                a._client.Disconnect();
                a.BeginSearching();
            }
            else if (a.State == SessionState.ClientPeer)
            {
                a._client.Disconnect();
                a.BeginSearching();
            }
            // ClientDreamBox is left alone: the kiosk relay is park-agnostic and
            // paired sessions are governed by SessionContext, not the beacon scope.
        }

#if UNITY_ANDROID || UNITY_EDITOR
        const bool PlatformCanHost = true;
#else
        const bool PlatformCanHost = false;   // iOS: client-only in v1 (no multicast entitlement)
#endif
        public bool CanHost => allowHosting && PlatformCanHost;

        /// <summary>
        /// Discovery channel: peer sessions never form across channels, so an
        /// SDK creator testing in a studio can't attract production headsets on
        /// the same Wi-Fi (or vice versa). Derived from the DREAMPARKCORE define
        /// — core builds are "prod", creator SDK projects compile to "sdk" with
        /// zero configuration. Set channelOverride to cross intentionally
        /// (e.g. "prod" in an SDK project to test against a real build).
        /// Kiosk/dev-relay beacons are channel-exempt: they're explicit
        /// infrastructure, and the dev relay flow must keep working in SDK projects.
        /// </summary>
        [Tooltip("Leave empty for automatic (prod in core builds, sdk in creator projects). Set to cross channels intentionally.")]
        public string channelOverride = "";

#if DREAMPARKCORE
        const string DefaultChannel = "prod";
#else
        const string DefaultChannel = "sdk";
#endif
        public string Channel => string.IsNullOrEmpty(channelOverride) ? DefaultChannel : channelOverride;

        const string HostLeavingEvent = "host_leaving";
        const float BeaconRecordTtl = 5f;
        const float BlacklistSeconds = 5f;

        /// <summary>
        /// Failed joins in a row before we stop treating a host as joinable.
        /// Two, not one: a single miss is a blip and yielding is cheap. A second
        /// consecutive miss means the path to that host is broken rather than
        /// busy, and continuing to yield to it costs us the entire session.
        /// </summary>
        const int UnreachableAfterFailures = 2;

        /// <summary>
        /// How long an unreachable verdict stands. Long enough to break the
        /// yield → fail → re-host cycle, short enough that a host which comes
        /// back (Wi-Fi roam, app resume, a developer fixing their VPN) gets
        /// another chance without anyone restarting anything.
        /// </summary>
        const float JoinFailureMemorySeconds = 60f;

        /// <summary>Cadence of the "two hosts in one park" warning.</summary>
        const float SplitBrainReportSeconds = 10f;

        DreamBoxClient _client;
        DiscoveryListener _discovery;
        PeerRelayServer _relay;
        BeaconBroadcaster _beacon;

        // Beacons arrive on the discovery thread — queue and drain on main thread.
        readonly object _beaconLock = new();
        readonly List<DiscoveryListener.BeaconInfo> _pendingBeacons = new();

        // hostId -> latest beacon + when we saw it (peer hosts only)
        readonly Dictionary<string, BeaconRecord> _peerHosts = new();
        struct BeaconRecord { public DiscoveryListener.BeaconInfo info; public float seenAt; }

        BeaconRecord? _dreamBox;                  // latest kiosk beacon, if any
        readonly Dictionary<string, float> _blacklist = new();   // dead hostId -> expiry

        float _listenDeadline;
        float _reelectionDeadline;
        float _joinStartedAt;
        float _stateEnteredAt;
        string _sessionKey;

        // Addresses to try for the host we are currently joining, in order:
        // the one its beacon advertised, then the one its beacon actually came
        // from. See DiscoveryListener.BeaconInfo.sourceHost for why the second
        // is worth more than the first when they disagree.
        readonly List<string> _joinAddresses = new();
        int _joinAddressIndex;
        DiscoveryListener.BeaconInfo _joinBeacon;
        bool _joinLanded;          // this join reached Connected at least once

        // hostId -> consecutive failed joins and when the last one was. A host
        // we cannot reach must stop winning elections, or the deterministic
        // tie-break hands the room to a device nobody can talk to.
        readonly Dictionary<string, JoinFailure> _joinFailures = new();
        struct JoinFailure { public int count; public float lastAt; }

        float _nextSplitBrainReportAt;

        // One "ignored beacon" log per hostId+reason — visibility without spam.
        readonly HashSet<string> _ignoredLogged = new();

        // One "refused to yield" log per hostId — same reasoning.
        readonly HashSet<string> _yieldRefusedLogged = new();

        void LogIgnoredOnce(string hostId, string reason)
        {
            string key = hostId + "|" + reason;
            if (_ignoredLogged.Add(key))
                Debug.LogWarning($"[Arbiter] Ignoring peer beacon from {hostId}: {reason}");
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        void Awake()
        {
            _client = GetComponent<DreamBoxClient>();
            // Stable, short, sortable identity. Deterministic per device.
            string raw = SystemInfo.deviceUniqueIdentifier ?? Guid.NewGuid().ToString();
            HostId = raw.Replace("-", "").ToLowerInvariant();
            if (HostId.Length > 12) HostId = HostId.Substring(0, 12);
        }

        void OnEnable()
        {
            ArbiterActive = true;
            Instance = this;
            _client.OnGlobalEvent += OnGlobalEvent;
            SessionContext.OnSessionPaired += OnSessionPaired;
            BeginSearching();
        }

        void OnDisable()
        {
            ArbiterActive = false;
            if (Instance == this) Instance = null;
            _client.OnGlobalEvent -= OnGlobalEvent;
            SessionContext.OnSessionPaired -= OnSessionPaired;
            StopDiscovery();
            StopHosting(announce: false);
            SetState(SessionState.Idle);
        }

        /// <summary>
        /// Kiosk pairing carries the canonical park id (locationId) — adopt it
        /// so a later fall-back to peer hosting stays scoped to the right park.
        /// </summary>
        void OnSessionPaired(SessionConfig config)
        {
            if (!string.IsNullOrEmpty(config.locationId))
                SetParkContext(config.locationId);
        }

        void OnApplicationPause(bool paused)
        {
            // Quest suspends the app the moment the host doffs the headset —
            // this is our only (best-effort) window to say goodbye. Never rely
            // on it: clients also detect beacon silence + connection loss.
            if (paused && State == SessionState.Hosting)
            {
                AnnounceHostLeaving("suspend");
                // Give the loopback relay a few pumps to fan the goodbye out.
                for (int i = 0; i < 3; i++)
                {
                    _relay?.Poll();
                    System.Threading.Thread.Sleep(20);
                }
            }
        }

        // ------------------------------------------------------------------
        // Main loop
        // ------------------------------------------------------------------

        void Update()
        {
            DrainBeacons();
            ExpireRecords();

            if (State == SessionState.Hosting)
            {
                _relay?.Poll();
                _beacon?.Tick();
            }

            switch (State)
            {
                case SessionState.Searching:     TickSearching();     break;
                case SessionState.ClientDreamBox: TickClientDreamBox(); break;
                case SessionState.ClientPeer:    TickClientPeer();    break;
                case SessionState.Hosting:       TickHosting();       break;
                case SessionState.Reelection:    TickReelection();    break;
            }
        }

        // ------------------------------------------------------------------
        // States
        // ------------------------------------------------------------------

        void BeginSearching()
        {
            // Deterministic jitter: two headsets booting together must not both
            // self-elect. hash(hostId) spreads deadlines across 0..2 s.
            uint h = Fnv1a(HostId);
            _listenDeadline = Time.unscaledTime + listenBaseSeconds + (h % 2000u) / 1000f;
            StartDiscovery();
            SetState(SessionState.Searching);
        }

        void TickSearching()
        {
            // 1. Kiosk always wins.
            if (_dreamBox.HasValue) { Join(_dreamBox.Value.info, SessionState.ClientDreamBox); return; }

            // 2. Existing peer host (lowest hostId if several are converging).
            var best = BestPeerHost();
            if (best.HasValue) { Join(best.Value.info, SessionState.ClientPeer); return; }

            // 3. Silence — become the host.
            if (Time.unscaledTime >= _listenDeadline && CanHost)
                StartHosting();
        }

        void TickClientDreamBox()
        {
            // Kiosk sessions keep DreamBoxClient's own reconnect ladder for
            // transient blips. But if the kiosk has ALSO stopped beaconing and
            // we're not connected, it's gone — don't sit out the full multi-
            // minute backoff ladder while ignoring perfectly good peer hosts.
            if (_client.ConnectionState == DreamBoxClient.State.Disconnected)
            {
                BeginSearching();
                return;
            }

            bool kioskSilent = !_dreamBox.HasValue;   // beacon expired (5 s TTL)
            bool notConnected = _client.ConnectionState != DreamBoxClient.State.Connected;
            if (kioskSilent && notConnected && Time.unscaledTime - _stateEnteredAt > kioskGraceSeconds)
            {
                Debug.Log("[Arbiter] Kiosk beacon gone and not connected — abandoning kiosk, back to ladder.");
                _client.Disconnect();
                BeginSearching();
            }
        }

        void TickClientPeer()
        {
            if (_dreamBox.HasValue)
            {
                // Kiosk appeared mid-session — everyone migrates (host yields too).
                _client.Disconnect();
                Join(_dreamBox.Value.info, SessionState.ClientDreamBox);
                return;
            }

            // Host death signals. IMPORTANT: a live ReliableOrdered connection is
            // ground truth — UDP broadcast is lossy on some networks (phone
            // hotspots especially), so beacon silence alone must NEVER kill a
            // healthy session. Beacon staleness only matters when we're not
            // connected (it disambiguates "host gone" from "my link blipped").
            //  - joinHung  — connect never lands within the timeout (blocked port)
            //  - linkDead  — client's reconnect ladder exhausted
            //  - beaconStale && !connected — host stopped beaconing AND we lost the link
            bool connected = _client.ConnectionState == DreamBoxClient.State.Connected;
            if (connected && !_joinLanded)
            {
                // The join worked. Forgive the host's history so a device that
                // was unreachable for a while isn't held against it forever.
                _joinLanded = true;
                ClearJoinFailures(CurrentHostId);
            }

            bool beaconStale = !IsPeerHostAlive(CurrentHostId);
            bool joinHung = !connected && Time.unscaledTime - _joinStartedAt > joinTimeoutSeconds
                         && _client.ConnectionState == DreamBoxClient.State.Connecting;
            bool linkDead = _client.ConnectionState == DreamBoxClient.State.Disconnected;

            if (joinHung || linkDead || (beaconStale && !connected && linkDead)
                || (beaconStale && _client.ConnectionState == DreamBoxClient.State.Reconnecting))
            {
                // We never got in at all — before writing this host off, try the
                // other address we have for it. Any of the failure paths above
                // qualifies: what matters is that this join never landed, not
                // which flavour of not-landing the client reported. Only once
                // every candidate has failed is the host itself the problem.
                if (!_joinLanded && TryNextJoinAddress())
                    return;

                if (!_joinLanded) RecordJoinFailure(CurrentHostId);
                if (beaconStale || joinHung) Blacklist(CurrentHostId);
                BeginReelection(deadHostId: CurrentHostId);
            }
        }

        void TickHosting()
        {
            if (_dreamBox.HasValue)
            {
                // Kiosk preemption: hand the room to the DreamBox.
                AnnounceHostLeaving("dreambox");
                var target = _dreamBox.Value.info;
                StopHosting(announce: false);
                Join(target, SessionState.ClientDreamBox);
                return;
            }

            // Deterministic tie-break: if another peer host with a lower hostId
            // is beaconing, we yield and join them. (They ignore ours.)
            // Note the two questions being asked here. The first is "does anyone
            // outrank me at all", unreachable hosts included, purely so we can
            // name the thing in the log. The second is "who should I actually
            // hand the room to", which only reachable hosts can answer.
            //
            // Yielding to a host we cannot reach tears down a working relay in
            // exchange for nothing: the join times out, re-election puts us back
            // in the chair, the next beacon starts it over. From outside that
            // loop looks exactly like "two hosts in the same park, neither
            // talking to the other" — which is the bug this guard exists for.
            // Sorting lower wins an election; it does not make a host joinable.
            var outranking = BestPeerHost(includeUnreachable: true);
            if (outranking.HasValue && IsUnreachable(outranking.Value.info.hostId))
            {
                string id = outranking.Value.info.hostId;
                if (_yieldRefusedLogged.Add(id))
                    Debug.LogWarning($"[Arbiter] Not yielding to {id} despite its lower hostId — we have failed to " +
                                     "reach it repeatedly. Staying host so this park keeps a joinable session. " +
                                     "Expect two hosts on this LAN until that device is reachable again.");
            }

            var rival = BestPeerHost();
            if (rival.HasValue && string.CompareOrdinal(rival.Value.info.hostId, HostId) < 0)
            {
                AnnounceHostLeaving(rival.Value.info.hostId);
                var target = rival.Value.info;
                StopHosting(announce: false);
                Join(target, SessionState.ClientPeer);
                return;
            }

            ReportSplitBrain();
        }

        /// <summary>
        /// Two peer hosts in one park is always a bug — the ladder exists to
        /// collapse them. It is also completely silent from inside either one:
        /// each sees a healthy relay, a connected client, and its own traffic
        /// flowing. Say it out loud on a slow cadence, with the reason, so the
        /// next person to hit this reads it in the console instead of inferring
        /// it from a LAN capture.
        /// </summary>
        void ReportSplitBrain()
        {
            if (Time.unscaledTime < _nextSplitBrainReportAt) return;

            int live = 0;
            string detail = null;
            foreach (var kv in _peerHosts)
            {
                if (!IsPeerHostAlive(kv.Key)) continue;
                live++;
                string why = IsUnreachable(kv.Key) ? "unreachable"
                           : string.CompareOrdinal(kv.Key, HostId) < 0 ? "lower hostId — should have taken over"
                           : "higher hostId — should be yielding to us";
                detail = detail == null ? $"{kv.Key} @ {kv.Value.info.host} ({why})"
                                        : detail + $", {kv.Key} @ {kv.Value.info.host} ({why})";
            }

            _nextSplitBrainReportAt = Time.unscaledTime + SplitBrainReportSeconds;
            if (live == 0) return;

            Debug.LogWarning($"[Arbiter] Hosting park '{parkId}' alongside {live} other live peer host(s) on this " +
                             $"LAN: {detail}. One of us should have yielded — players on the two hosts cannot see " +
                             "each other.");
        }

        void BeginReelection(string deadHostId)
        {
            _client.Disconnect();
            CurrentHostId = null;

            // Coordinator-free: everyone sorts the same live-host list. Rank 0
            // hosts now; rank n waits n × step and joins whatever beacon appears.
            var candidates = new List<string>();
            if (CanHost) candidates.Add(HostId);
            foreach (var kv in _peerHosts)
                if (kv.Key != deadHostId && !IsBlacklisted(kv.Key) && IsPeerHostAlive(kv.Key)
                    && !IsUnreachable(kv.Key))   // ranking behind a host we can't join is ranking behind nobody
                    candidates.Add(kv.Key);
            candidates.Sort(StringComparer.Ordinal);

            int rank = CanHost ? Mathf.Max(0, candidates.IndexOf(HostId)) : int.MaxValue;
            if (rank == 0 && CanHost)
            {
                StartHosting();
                return;
            }

            float wait = rank == int.MaxValue
                ? reelectionStepSeconds * 4f                       // pure client: just wait for a beacon
                : reelectionStepSeconds * rank;
            _reelectionDeadline = Time.unscaledTime + wait;
            SetState(SessionState.Reelection);
        }

        void TickReelection()
        {
            if (_dreamBox.HasValue) { Join(_dreamBox.Value.info, SessionState.ClientDreamBox); return; }

            var best = BestPeerHost();
            if (best.HasValue) { Join(best.Value.info, SessionState.ClientPeer); return; }

            if (Time.unscaledTime >= _reelectionDeadline)
            {
                // Nobody above us stepped up. Host if we can; otherwise restart
                // the ladder and keep listening.
                if (CanHost) StartHosting();
                else BeginSearching();
            }
        }

        // ------------------------------------------------------------------
        // Transitions
        // ------------------------------------------------------------------

        void Join(DiscoveryListener.BeaconInfo info, SessionState asState)
        {
            CurrentHostId = string.IsNullOrEmpty(info.hostId) ? info.dreamboxId : info.hostId;
            HostAdvertisedCap = info.msgCap;
            _joinBeacon = info;
            _joinLanded = false;

            // Normally the beacon's advertised address and the address it was
            // sent from are the same string and there is one candidate. When
            // they differ, take the one we observed over the one we were told:
            // the source address is where a packet demonstrably just came from,
            // over a path that demonstrably works, while the advertised address
            // is the host's guess at which of its own interfaces we can see —
            // and a host cannot check that guess, because it can always reach
            // itself. The relay binds 0.0.0.0, so it answers on either.
            //
            // The advertised address stays as the fallback rather than being
            // dropped: a venue could deliberately front a relay on an address
            // it does not broadcast from, and that setup should still work.
            _joinAddresses.Clear();
            bool mismatch = !string.IsNullOrEmpty(info.sourceHost)
                            && !string.IsNullOrEmpty(info.host)
                            && info.sourceHost != info.host;

            if (mismatch)
            {
                _joinAddresses.Add(info.sourceHost);
                _joinAddresses.Add(info.host);
                Debug.LogWarning(
                    $"[Arbiter] Host {CurrentHostId} advertises {info.host} but its beacon arrived from " +
                    $"{info.sourceHost} — joining {info.sourceHost}, which we know reaches it. If {info.host} " +
                    "is a VPN or virtual adapter on that machine, fix the interface pick there: headsets on " +
                    "the real Wi-Fi cannot route to it, and every one of them will fail this join first.");
            }
            else
            {
                if (!string.IsNullOrEmpty(info.host)) _joinAddresses.Add(info.host);
                else if (!string.IsNullOrEmpty(info.sourceHost)) _joinAddresses.Add(info.sourceHost);
            }

            if (_joinAddresses.Count == 0)
            {
                Debug.LogWarning($"[Arbiter] Beacon from {CurrentHostId} carries no usable address — ignoring.");
                RecordJoinFailure(CurrentHostId);
                BeginReelection(deadHostId: CurrentHostId);
                return;
            }

            _joinAddressIndex = 0;
            ConnectToJoinCandidate();
            SetState(asState);
        }

        void ConnectToJoinCandidate()
        {
            _joinStartedAt = Time.unscaledTime;
            string addr = _joinAddresses[_joinAddressIndex];
            _client.Connect(addr, _joinBeacon.port,
                            string.IsNullOrEmpty(_joinBeacon.key) ? null : _joinBeacon.key);
        }

        /// <summary>
        /// Move to the next candidate address for the host we're joining.
        /// Returns false when we've tried them all — that's when the host counts
        /// as a failure, not on the first address that didn't answer.
        /// </summary>
        bool TryNextJoinAddress()
        {
            if (_joinAddressIndex + 1 >= _joinAddresses.Count) return false;

            string failed = _joinAddresses[_joinAddressIndex];
            _joinAddressIndex++;
            string next = _joinAddresses[_joinAddressIndex];
            Debug.LogWarning($"[Arbiter] {failed}:{_joinBeacon.port} did not answer for host {CurrentHostId} — " +
                             $"retrying at {next}.");

            _client.Disconnect();
            ConnectToJoinCandidate();
            return true;
        }

        void RecordJoinFailure(string hostId)
        {
            if (string.IsNullOrEmpty(hostId)) return;
            _joinFailures.TryGetValue(hostId, out var f);
            f.count++;
            f.lastAt = Time.unscaledTime;
            _joinFailures[hostId] = f;

            if (f.count == UnreachableAfterFailures)
                Debug.LogWarning($"[Arbiter] Host {hostId} has failed {f.count} joins in a row — treating it as " +
                                 $"unreachable for {JoinFailureMemorySeconds:0}s. We will not yield the room to it " +
                                 "while that stands, so that at least one host on this LAN is joinable.");
        }

        void ClearJoinFailures(string hostId)
        {
            if (string.IsNullOrEmpty(hostId)) return;
            _joinFailures.Remove(hostId);
            _yieldRefusedLogged.Remove(hostId);
        }

        /// <summary>
        /// A host we've repeatedly failed to reach. It may be beaconing happily
        /// — beacons are broadcast and travel fine across a boundary that unicast
        /// does not — so "I can hear it" is not evidence that we can join it.
        /// </summary>
        bool IsUnreachable(string hostId) =>
            hostId != null
            && _joinFailures.TryGetValue(hostId, out var f)
            && f.count >= UnreachableAfterFailures;

        void StartHosting()
        {
            _sessionKey = GenerateKey(8);
            _relay = new PeerRelayServer();
            if (!_relay.Start(_sessionKey))
            {
                // Couldn't bind at all — stay a client and keep listening.
                _relay = null;
                BeginSearching();
                return;
            }

            _beacon = new BeaconBroadcaster();
            if (!_beacon.Start(HostId, parkId, _relay.Port, _sessionKey, Channel))
            {
                // No LAN IP (Wi-Fi down / AP isolation). Solo play, retry later.
                // Say so: a host that cannot beacon is invisible to every other
                // device and to the LAN Monitor, and from the outside that is
                // indistinguishable from the app not running at all.
                Debug.LogWarning("[Arbiter] Relay is up but the beacon could not start — no LAN address to " +
                                 "advertise. Nobody can discover this session; falling back to searching. " +
                                 $"Interfaces: {NetPlatform.DescribeCandidates()}");
                _relay.Stop(); _relay = null; _beacon = null;
                BeginSearching();
                return;
            }

            // Pin the Wi-Fi radio out of power save while we're the relay
            // everyone routes through (released in StopHosting).
            NetPlatform.AcquireWifiLowLatencyLock();

            // The host is just another client of its own relay — gameplay and
            // Lua cannot tell the difference. Loopback, so always reachable.
            CurrentHostId = HostId;
            HostAdvertisedCap = PeerRelayServer.MaxMessagesPerPeerPerSecond;   // we ARE the relay
            _client.Connect("127.0.0.1", _relay.Port, _sessionKey);
            SetState(SessionState.Hosting);
        }

        void StopHosting(bool announce)
        {
            if (announce && State == SessionState.Hosting) AnnounceHostLeaving("stop");
            _beacon?.Stop(); _beacon = null;
            _relay?.Stop(); _relay = null;
            NetPlatform.ReleaseWifiLowLatencyLock();
        }

        void AnnounceHostLeaving(string successor)
        {
            if (_client.ConnectionState == DreamBoxClient.State.Connected)
                _client.PublishRaw(HostLeavingEvent,
                    "{\"hostId\":\"" + HostId + "\",\"successor\":\"" + successor + "\"}");
        }

        void OnGlobalEvent(string type, string json)
        {
            if (type != HostLeavingEvent || State != SessionState.ClientPeer) return;

            // Hardening: only honor a goodbye that claims to be from the host
            // we're actually connected to. A rogue peer spamming host_leaving
            // (the key is LAN-observable) can't trigger re-election churn.
            // Worst case if the claim is forged with the right hostId, we fall
            // back to what beacon staleness would have told us anyway.
            string claimedHostId = ExtractJsonString(json, "hostId");
            if (claimedHostId == null || claimedHostId != CurrentHostId)
            {
                Debug.LogWarning($"[Arbiter] Ignoring host_leaving from '{claimedHostId ?? "?"}' (current host: {CurrentHostId}).");
                return;
            }

            // Fast path: the host said goodbye — don't wait out beacon staleness.
            Blacklist(CurrentHostId);
            BeginReelection(deadHostId: CurrentHostId);
        }

        /// <summary>Cheap {"key":"value"} string extraction, same style as DreamBoxClient's netId scan.</summary>
        static string ExtractJsonString(string json, string key)
        {
            int idx = json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length + 4;
            int end = json.IndexOf('"', start);
            if (end <= start || end - start > 64) return null;
            return json.Substring(start, end - start);
        }

        void SetState(SessionState s)
        {
            if (State == s) return;
            State = s;
            _stateEnteredAt = Time.unscaledTime;
            Debug.Log($"[Arbiter] → {s}" + (CurrentHostId != null ? $" (host {CurrentHostId})" : ""));
            OnStateChanged?.Invoke(s);
        }

        // ------------------------------------------------------------------
        // Discovery plumbing
        // ------------------------------------------------------------------

        void StartDiscovery()
        {
            if (_discovery != null && _discovery.IsRunning) return;
            _discovery = new DiscoveryListener();
            _discovery.OnBeacon += QueueBeacon;
            _discovery.Start();
        }

        void StopDiscovery()
        {
            if (_discovery == null) return;
            _discovery.OnBeacon -= QueueBeacon;
            _discovery.Stop();
            _discovery = null;
        }

        void QueueBeacon(DiscoveryListener.BeaconInfo info)   // discovery thread!
        {
            lock (_beaconLock) _pendingBeacons.Add(info);
        }

        void DrainBeacons()
        {
            lock (_beaconLock)
            {
                for (int i = 0; i < _pendingBeacons.Count; i++)
                {
                    var b = _pendingBeacons[i];
                    bool isPeer = b.hostType == "peer";

                    if (isPeer)
                    {
                        if (b.hostId == HostId) continue;                    // our own echo
                        if (b.v > BeaconBroadcaster.ProtocolVersion) { LogIgnoredOnce(b.hostId, $"protocol v{b.v} > v{BeaconBroadcaster.ProtocolVersion}"); continue; }
                        if (!ChannelMatches(b.channel)) { LogIgnoredOnce(b.hostId, $"channel '{b.channel}' != ours '{Channel}' (set channelOverride to cross)"); continue; }
                        if (!ParkMatches(b.parkId)) { LogIgnoredOnce(b.hostId, $"parkId '{b.parkId}' != ours '{parkId}'"); continue; }
                        if (IsBlacklisted(b.hostId)) continue;
                        _peerHosts[b.hostId] = new BeaconRecord { info = b, seenAt = Time.unscaledTime };
                    }
                    else
                    {
                        // No hostType (or "dreambox") = kiosk beacon. Kiosk outranks
                        // everything; park scoping doesn't apply to it.
                        _dreamBox = new BeaconRecord { info = b, seenAt = Time.unscaledTime };
                    }
                }
                _pendingBeacons.Clear();
            }
        }

        void ExpireRecords()
        {
            float now = Time.unscaledTime;

            if (_dreamBox.HasValue && now - _dreamBox.Value.seenAt > BeaconRecordTtl)
                _dreamBox = null;

            List<string> drop = null;
            foreach (var kv in _peerHosts)
                if (now - kv.Value.seenAt > BeaconRecordTtl)
                    (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _peerHosts.Remove(k);

            drop = null;
            foreach (var kv in _blacklist)
                if (now > kv.Value) (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null) foreach (var k in drop) _blacklist.Remove(k);

            // An unreachable verdict has to expire, or a host that recovers can
            // never win an election again for the life of the app.
            drop = null;
            foreach (var kv in _joinFailures)
                if (now - kv.Value.lastAt > JoinFailureMemorySeconds) (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null)
                foreach (var k in drop)
                {
                    _joinFailures.Remove(k);
                    _yieldRefusedLogged.Remove(k);
                    Debug.Log($"[Arbiter] Giving host {k} another chance (unreachable verdict expired).");
                }
        }

        /// <summary>
        /// Lowest live hostId — the one everyone converges on.
        ///
        /// Hosts we have repeatedly failed to join are excluded by default:
        /// every caller but one is asking "who should I join", and the answer
        /// must not be a device we already know we cannot reach. TickHosting
        /// passes <paramref name="includeUnreachable"/> because it is asking a
        /// different question — "is someone out there who outranks me" — and it
        /// wants to say so in the log before declining to yield.
        /// </summary>
        BeaconRecord? BestPeerHost(bool includeUnreachable = false)
        {
            BeaconRecord? best = null;
            foreach (var kv in _peerHosts)
            {
                if (!IsPeerHostAlive(kv.Key)) continue;
                if (!includeUnreachable && IsUnreachable(kv.Key)) continue;
                if (best == null || string.CompareOrdinal(kv.Value.info.hostId, best.Value.info.hostId) < 0)
                    best = kv.Value;
            }
            return best;
        }

        bool IsPeerHostAlive(string hostId) =>
            hostId != null
            && _peerHosts.TryGetValue(hostId, out var r)
            && Time.unscaledTime - r.seenAt <= beaconStaleSeconds;

        bool ParkMatches(string beaconParkId) =>
            string.IsNullOrEmpty(parkId) || string.IsNullOrEmpty(beaconParkId) || beaconParkId == parkId;

        // Beacons without a channel field ("" — v1 peers before this field, or
        // hand-rolled) are treated as prod, so production behavior is unchanged.
        bool ChannelMatches(string beaconChannel) =>
            (string.IsNullOrEmpty(beaconChannel) ? "prod" : beaconChannel) == Channel;

        /// <summary>
        /// Ignore a host's beacons for a while. The window grows with each
        /// consecutive failure: a flat 5 s is shorter than the 10 s join
        /// timeout, so a host we cannot reach used to come back off the
        /// blacklist before we had finished failing to reach it — a retry loop
        /// that never widened and never gave up.
        /// </summary>
        void Blacklist(string hostId)
        {
            if (string.IsNullOrEmpty(hostId)) return;

            _joinFailures.TryGetValue(hostId, out var f);
            float window = Mathf.Min(BlacklistSeconds * Mathf.Pow(2f, Mathf.Max(0, f.count - 1)), 60f);

            _blacklist[hostId] = Time.unscaledTime + window;
            _peerHosts.Remove(hostId);
        }

        bool IsBlacklisted(string hostId) =>
            hostId != null && _blacklist.ContainsKey(hostId);

        // ------------------------------------------------------------------
        // Utils
        // ------------------------------------------------------------------

        static uint Fnv1a(string s)
        {
            uint hash = 2166136261;
            foreach (char c in s) { hash ^= c; hash *= 16777619; }
            return hash;
        }

        static string GenerateKey(int length)
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var buf = new char[length];
            for (int i = 0; i < length; i++)
                buf[i] = chars[UnityEngine.Random.Range(0, chars.Length)];
            return new string(buf);
        }
    }
}
