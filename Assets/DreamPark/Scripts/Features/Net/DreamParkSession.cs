using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Defective.JSON;

namespace DreamPark
{
    // ─────────────────────────────────────────────────────────────────────
    //  DreamParkSession.cs — who leads, what time it is, and a shared seed.
    //
    //  Every multiplayer game needs the same three things and every game used
    //  to build them by hand (mp_kit.lua's new_room / new_clock, Birthday
    //  Bash's kit.leader with its "ready" flag and hysteresis):
    //
    //    LEADER  the most SENIOR live, SYNCED headset — the incumbent keeps
    //            the lead until it leaves or is reaped, so a newcomer never
    //            takes over mid-round (DreamSports, ArcadeLand, WizardsWay,
    //            SuperAdventureLand and LaserTag all chose sticky join order).
    //            Ties and fresh rooms break by lowest id. Phones lead only a
    //            room with no headset; the Editor counts as a headset. Scoped
    //            per TITLE: only devices that announced the title count
    //            (LeaderFor), and dp.leader() inside a script uses its title.
    //            Re-derived on every call, identical on every device.
    //            Within a title, devices WEARING the title's PlayerRig come
    //            first: while anyone wears it, a device standing in another
    //            title's zone (or between zones) never leads it or ranks in it.
    //    RANK    the same order as a 1-based join-order slot (colours, queues).
    //    CLOCK   one timeline for the whole session (seconds). The leader beats
    //            its SYNCED clock value, everyone else slews toward it (mp_kit's
    //            new_clock, plus a one-way latency estimate from both pings),
    //            accepting beats from the LEADER only. Unscaled real time: it
    //            keeps counting through pauses, and once synced it never runs
    //            backwards (backward errors slew at < 1 s/s; only forward
    //            errors jump). A hand-off keeps counting: the new leader was
    //            synced too.
    //    SEED    one random seed per session, chosen by the first leader and
    //            carried on the leader's beat, so Random01(key, salt) is the
    //            same number on every device.
    //
    //  SYNCED is the late-join gate (MULTIPLAYER.md rule 5): a device is
    //  synced once it has heard a synced peer's beat, or — when nobody synced
    //  answers — after a short silence once the relay link is up (the lowest
    //  headset first; the others adopt its clock). When the arbiter KNOWS the
    //  room is empty (ISessionRoomHint: it hosts and nobody is on its relay,
    //  or it heard no host in a full listen window) there is nobody to wait
    //  for and the device syncs at once. An unsynced device never
    //  leads while a synced one is present, so a late joiner with the lowest id
    //  cannot take the room over before it knows the room's state.
    //
    //  MERGE (Oct 11 2026): a device that played SOLO and then connects does
    //  not drop its game at once. It holds its timeline ("mg") until it hears
    //  the room. A room of exactly one synced device that was alone ("ra")
    //  is merged by age: the OLDER timeline ("ag", seconds synced) keeps its
    //  clock, seed and shared state, the other adopts it (ties within 1 s:
    //  headset, then lower id). An established room (anyone else synced in
    //  it, or an older build that sends no "ra"/"ag") always wins, as before.
    //  The loser un-syncs (MergeLosses++): its SharedStates re-ask and its
    //  NetBodies give up their revisions. Who adopted whom travels as "af".
    //  Ordering puts SYNCED before device kind, so an unsynced headset can
    //  adopt a synced phone's timeline (it used to rank itself first and wait).
    //
    //  Wire (session-level events, no netId — DreamBoxClient routes them to
    //  OnGlobalEvent):
    //    "__dps"     {"i":id,"dv":"headset"|"phone","sy":0|1,"ld":0|1,
    //                 "g":"title,title","w":"title,title","t":clock,"j":joinedAt,
    //                 "pg":pingMs,"sd":"seedhex","ag":age,"mg":1,"ra":1,"af":id}
    //                 ~1 Hz per device (t/j/sd/ag/ra/af once synced; mg while holding a solo timeline).
    //                 w = titles whose PlayerRig this device wears ("" = none;
    //                 absent on builds before Oct 8 2026 = unknown, old rule).
    //                 Parsed in C# as doubles — never through Lua's float32
    //                 json_parse — and the seed travels as hex text.
    //    "__dps_bye" {"i":id}                                   on quit
    //  Types starting "__" are SDK system traffic; content must not use them.
    //
    //  This is an internal engine service, not a component: nothing to add to
    //  a prefab. Lua reaches it through dp.leader() / dp.is_leader() /
    //  dp.on_leader_changed() / dp.clock() / dp.clock_synced() / dp.random().
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>The relay as the session sees it. Swapped for a simulated relay in tests.</summary>
    public interface ISessionLink
    {
        /// <summary>A relay client exists in this process (connected or not).</summary>
        bool Present { get; }
        bool Connected { get; }
        /// <summary>One-way latency to the relay in ms (LiteNetLib Ping), 0 if unknown.</summary>
        int PingMs { get; }
        /// <summary>Send a session-level event (no netId) to every other device.</summary>
        void Publish(string type, string payloadJson);
    }

    /// <summary>
    /// Optional extra a link can implement: what the session arbiter KNOWS about
    /// the room. <see cref="KnownAlone"/> is true only when nobody else can be in
    /// the session right now — this device hosts the relay and nobody else is
    /// connected to it, or it listened a full discovery window and heard no host
    /// at all. Unknown = false (the session then waits for the room as before).
    /// </summary>
    public interface ISessionRoomHint
    {
        bool KnownAlone { get; }
    }

    /// <summary>
    /// The ONE token bucket for SDK system traffic on a headset: session beats,
    /// SharedState, and every dp.net_body. The relay drops anything past
    /// <see cref="DreamBoxClient.DesignBudget"/> (60) messages/s per headset,
    /// silently, so the floor is split once, here, and nothing else may grow:
    ///   presence  <see cref="PresenceShare"/>  (PlayerPresence, ≤ 12 Hz)
    ///   SDK       <see cref="SdkShare"/>       (this bucket)
    ///   game      <see cref="GameShare"/>      guaranteed to content net_send
    /// Priority classes inside the bucket (<see cref="NetBodyPriority"/>):
    ///   High   — must arrive (snapshots, write requests, claims, settled poses):
    ///            may borrow up to <see cref="CriticalDebt"/>;
    ///   Normal — live streams (body poses): keep <see cref="NormalReserve"/> spare for High;
    ///   Low    — catch-up traffic (heartbeats, late-join answers): only the top half.
    /// Lua: dp.budget().
    /// </summary>
    public sealed class NetSendBudget
    {
        public const int PresenceShare = 12;
        public const int SdkShare = 24;
        public const int GameShare = DreamBoxClient.DesignBudget - PresenceShare - SdkShare;   // 24

        /// <summary>Messages per second available to SDK services on this device.</summary>
        public float Hz = SdkShare;
        /// <summary>How far a must-arrive message (state snapshot, claim) may borrow.</summary>
        public float CriticalDebt = 6f;
        /// <summary>Tokens Normal traffic leaves for High.</summary>
        public float NormalReserve = 4f;
        float _tokens = SdkShare;
        double _last = double.NaN;
        readonly Func<double> _time;
        public int Sent { get; private set; }
        public int Denied { get; private set; }

        public NetSendBudget(Func<double> time) { _time = time; }

        void Refill()
        {
            double now = _time();
            if (double.IsNaN(_last)) _last = now;
            _tokens = Mathf.Min(Hz, _tokens + (float)(now - _last) * Hz);
            _last = now;
        }

        /// <summary>Tokens available right now (may be negative while in debt).</summary>
        public float Available { get { Refill(); return _tokens; } }

        /// <summary>
        /// Take <paramref name="n"/> permits. Ordinary traffic needs them in hand
        /// (keeping <paramref name="reserve"/> spare for critical traffic);
        /// critical traffic may borrow up to <see cref="CriticalDebt"/>.
        /// </summary>
        public bool TryTake(int n = 1, bool critical = false, float reserve = 0f)
        {
            Refill();
            if (critical)
            {
                if (_tokens - n < -CriticalDebt) { Denied++; return false; }
            }
            else if (_tokens - n < reserve) { Denied++; return false; }
            _tokens -= n;
            Sent += n;
            return true;
        }

        /// <summary>Take one permit in a priority class (see the class doc).</summary>
        public bool TryTake(NetBodyPriority priority)
        {
            switch (priority)
            {
                case NetBodyPriority.High: return TryTake(1, critical: true);
                case NetBodyPriority.Low: return TryTake(1, critical: false, reserve: Hz * 0.5f);
                default: return TryTake(1, critical: false, reserve: NormalReserve);
            }
        }
    }

    /// <summary>
    /// The session logic, independent of Unity's lifecycle and of the real
    /// relay — so several of them can talk over a simulated relay in an
    /// EditMode test. <see cref="DreamParkSession"/> runs one bound to the real
    /// DreamBoxClient.
    /// </summary>
    public sealed class SessionEngine
    {
        public const string TypeBeat = "__dps";
        public const string TypeBye = "__dps_bye";
        public const string TypeState = "__ss";

        // Tuning (seconds). Public for tests and the rare venue that needs it.
        public double BeatInterval = 1.0;
        public double PeerTimeout = 4.0;
        public double SilenceWindow = 1.5;          // connected, nobody synced answered
        public double SilenceWindowFollower = 4.0;  // ...when another headset should go first
        public double SoloGraceNoClient = 0.25;     // no relay client in the process at all
        public double SoloGraceDisconnected = 6.0;  // a client exists but never connected
        public double ClockSlew = 0.25;             // forward correction, seconds per second
        public double ClockSlewBack = 0.5;          // backward correction (< 1: never runs backwards)
        public double ClockSnap = 0.75;             // jump FORWARD instead of slewing past this

        public string LocalId { get; }
        public ISessionLink Link { get; }
        public NetSendBudget Budget { get; }
        readonly Func<double> _time;
        readonly Func<bool> _isPhone;
        readonly Func<IEnumerable<string>> _titles;
        readonly Func<IEnumerable<string>> _worn;

        sealed class Peer
        {
            public string id;
            public bool phone;
            public bool synced;
            public double heard;
            public double joined = double.NaN;   // seniority: shared-clock time it joined
            public readonly HashSet<string> titles = new HashSet<string>(StringComparer.Ordinal);
            public bool wornKnown;                // the beat carried "w"
            public readonly HashSet<string> worn = new HashSet<string>(StringComparer.Ordinal);
            // Merge (Oct 11 2026): see the file header.
            public bool merging;                  // "mg": holding a solo timeline, room not heard yet
            public bool roomAlone;                // "ra": synced and alone in its room
            public double age = double.NaN;       // "ag": seconds its timeline has been synced
            public string adoptedFrom = "";       // "af": whose timeline it adopted
        }

        readonly Dictionary<string, Peer> _peers = new Dictionary<string, Peer>(StringComparer.Ordinal);
        readonly List<string> _scratch = new List<string>();

        readonly double _createdAt;
        double _lastTick = double.NaN;
        bool _wasConnected;
        double _connectedSince = -1, _disconnectedAt = -1;
        bool _synced, _syncedSolo;
        double _joined = double.NaN;
        double _offset, _target;
        bool _haveOffset;
        uint _seed;
        bool _haveSeed;
        double _nextBeat;
        double _lastUrgent = -1;
        bool _urgent;
        string _leader;
        readonly HashSet<string> _myTitles = new HashSet<string>(StringComparer.Ordinal);
        string _myTitlesWire = "";
        double _nextTitleScan = -1;
        readonly HashSet<string> _myWorn = new HashSet<string>(StringComparer.Ordinal);
        string _myWornWire = "";
        double _nextWornScan = -1;
        // Merge (Oct 11 2026).
        double _syncedAt = double.NaN;      // local time this device's timeline began (adopted: the room's)
        bool _merging;
        double _mergingSince;
        string _adoptedFrom = "";
        string _mergeLoser;                 // the lone room we just beat, until it shows it adopted us
        /// <summary>Ages closer than this are a tie (headset, then lower id). Beats are ~30 ms stale.</summary>
        public double MergeTieSeconds = 1.0;
        /// <summary>A solo device that connected is holding its timeline until it hears the room.</summary>
        public bool Merging => _merging;
        /// <summary>Times this device gave its timeline up to another one (SharedState / NetBodies watch it).</summary>
        public int MergeLosses { get; private set; }
        /// <summary>Seconds this device's timeline has been synced (0 when not synced).</summary>
        public double SyncedAge => _synced && !double.IsNaN(_syncedAt) ? Math.Max(0, _time() - _syncedAt) : 0;

        internal readonly List<SharedState> States = new List<SharedState>();
        /// <summary>Shared states this session drives.</summary>
        public IReadOnlyList<SharedState> SharedStates => States;

        /// <summary>Fired with (oldLeaderId, newLeaderId) whenever the session-wide leader changes.</summary>
        public event Action<string, string> LeaderChanged;
        /// <summary>Fired on the rising edge of the relay link (after the session reset its sync).</summary>
        public event Action Reconnected;

        public SessionEngine(string localId, ISessionLink link, Func<double> time, Func<bool> isPhone,
            Func<IEnumerable<string>> titles = null, Func<IEnumerable<string>> wornTitles = null)
        {
            _worn = wornTitles;
            LocalId = localId ?? "";
            Link = link;
            _time = time ?? (() => Time.realtimeSinceStartupAsDouble);
            _isPhone = isPhone ?? (() => false);
            _titles = titles;
            Budget = new NetSendBudget(_time);
            _createdAt = _time();
            _leader = LocalId;
        }

        public double LocalTime => _time();
        public bool IsPhoneSelf => _isPhone();

        // ── Public state ─────────────────────────────────────────────

        /// <summary>Session-wide leader (every title). Prefer <see cref="LeaderFor"/> with a title.</summary>
        public string LeaderId => _leader ?? "";
        public bool IsLeader => !string.IsNullOrEmpty(LocalId) && _leader == LocalId;
        public bool Synced => _synced;
        public bool ClockSynced => _synced;
        public double Now => _time() + _offset;
        public double ClockOffset => _offset;
        public uint Seed => _seed;
        /// <summary>This device's seniority (shared-clock time it joined the room), NaN until synced.</summary>
        public double JoinedAt => _joined;

        /// <summary>Live peers (not this device), sorted by id.</summary>
        public List<string> PeerIds()
        {
            var list = new List<string>();
            double now = _time();
            foreach (var p in _peers.Values) if (now - p.heard <= PeerTimeout) list.Add(p.id);
            list.Sort(string.CompareOrdinal);
            return list;
        }

        /// <summary>True when <paramref name="id"/> is this device or a peer heard within the timeout.</summary>
        public bool IsLive(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id == LocalId) return true;
            return _peers.TryGetValue(id, out var p) && _time() - p.heard <= PeerTimeout;
        }

        /// <summary>Phone per the peer's own beat (this device: DreamParkDevice). Unknown = headset.</summary>
        public bool IsPhone(string id)
        {
            if (id == LocalId) return _isPhone();
            return _peers.TryGetValue(id, out var p) && p.phone;
        }

        public bool IsSyncedPeer(string id)
        {
            if (id == LocalId) return _synced;
            return _peers.TryGetValue(id, out var p) && p.synced;
        }

        /// <summary>Does this device run <paramref name="title"/>? (Always, without a title source.)</summary>
        public bool RunsTitle(string title)
        {
            if (string.IsNullOrEmpty(title) || _titles == null || _myTitles.Contains(title)) return true;
            // Content can load between scans: look again now (rate-limited).
            double now = _time();
            if (now < _nextForcedScan) return false;
            _nextForcedScan = now + 0.25;
            ScanTitles();
            return _myTitles.Contains(title);
        }
        double _nextForcedScan = -1;

        /// <summary>Seniority of a device (NaN when unknown / not synced).</summary>
        public double JoinedAtOf(string id)
        {
            if (id == LocalId) return _joined;
            return _peers.TryGetValue(id, out var p) ? p.joined : double.NaN;
        }

        /// <summary>Does <paramref name="id"/> run <paramref name="title"/> (null/empty title = yes)?</summary>
        public bool HasTitle(string id, string title)
        {
            if (string.IsNullOrEmpty(title)) return true;
            if (id == LocalId) return RunsTitle(title);
            return _peers.TryGetValue(id, out var p) && p.titles.Contains(title);
        }

        /// <summary>
        /// Deterministic [0,1) from the session seed, a key and a salt — the same
        /// number on every device once <see cref="ClockSynced"/>.
        /// </summary>
        public float Random01(string key, int salt = 0) => HashToUnit(_seed, key, salt);

        public static float HashToUnit(uint seed, string key, int salt)
        {
            uint h = 2166136261u;
            h = Mix(h, seed);
            if (key != null)
                for (int i = 0; i < key.Length; i++) { h ^= key[i]; h *= 16777619u; }
            h = Mix(h, unchecked((uint)salt));
            // murmur3 finalizer: spreads every input bit over the output
            h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
            return (h >> 8) * (1f / 16777216f);
        }

        static uint Mix(uint h, uint v)
        {
            for (int i = 0; i < 4; i++) { h ^= (v >> (i * 8)) & 0xffu; h *= 16777619u; }
            return h;
        }

        // ── Leader & rank ────────────────────────────────────────────

        /// <summary>A device as the leader rule sees it.</summary>
        public struct Candidate
        {
            public string id;
            public bool phone;
            public bool synced;
            public double joined;   // NaN = unknown
            public Candidate(string id, bool phone, bool synced, double joined = double.NaN)
            { this.id = id; this.phone = phone; this.synced = synced; this.joined = joined; }
        }

        /// <summary>
        /// The order every device agrees on: synced before unsynced (a late
        /// joiner never leads before it holds the room's state — and an
        /// unsynced headset adopts a synced phone's timeline instead of ranking
        /// itself first and waiting forever, Oct 11 2026), then headsets before
        /// phones (among synced devices a phone leads only a room with no
        /// headset), then SENIORITY (who joined the room first — the incumbent
        /// keeps the lead until it leaves or is reaped, so a newcomer never
        /// takes over mid-round), then id.
        /// </summary>
        public static int Compare(Candidate a, Candidate b)
        {
            int c = (a.synced ? 0 : 1).CompareTo(b.synced ? 0 : 1);
            if (c != 0) return c;
            c = (a.phone ? 1 : 0).CompareTo(b.phone ? 1 : 0);
            if (c != 0) return c;
            if (a.synced && b.synced)
            {
                bool an = double.IsNaN(a.joined), bn = double.IsNaN(b.joined);
                if (an != bn) return an ? 1 : -1;
                if (!an)
                {
                    // At the WIRE's precision: "j" travels with 4 decimals, so a
                    // device's own full-precision join time must never be compared
                    // with its peers' rounded ones. Two devices that synced on the
                    // same tick (equal to 4 decimals, not to the bit) each saw the
                    // OTHER as senior and both named the other leader (Birthday
                    // Bash, 0 ms ping). Equal at that precision = equal: the id decides.
                    long ja = JoinTick(a.joined), jb = JoinTick(b.joined);
                    if (ja != jb) return ja.CompareTo(jb);
                }
            }
            return string.CompareOrdinal(a.id, b.id);
        }

        /// <summary>Seniority in wire units (1e-4 s), the precision every device sees.</summary>
        static long JoinTick(double joined) => (long)Math.Round(joined * 10000.0, MidpointRounding.AwayFromZero);

        /// <summary>A join time as it travels ("F4"), so this device's own value is exactly what its peers hold.</summary>
        static double WireJoined(double joined)
            => double.Parse(joined.ToString("F4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        public static string PickLeader(IList<Candidate> cands)
        {
            bool have = false;
            Candidate best = default;
            for (int i = 0; i < cands.Count; i++)
            {
                if (string.IsNullOrEmpty(cands[i].id)) continue;
                if (!have || Compare(cands[i], best) < 0) { best = cands[i]; have = true; }
            }
            return have ? best.id : "";
        }

        /// <summary>
        /// Merge of two lone timelines (Oct 11 2026): does A's timeline win over
        /// B's? The OLDER one (seconds synced) wins; ages within
        /// <paramref name="tie"/> are a tie: headset over phone, then lower id.
        /// Both devices evaluate it with the same numbers, so they agree.
        /// </summary>
        public static bool MergeWinner(double ageA, bool phoneA, string idA, double ageB, bool phoneB, string idB, double tie = 1.0)
        {
            if (double.IsNaN(ageB)) return true;
            if (double.IsNaN(ageA)) return false;
            if (Math.Abs(ageA - ageB) > tie) return ageA > ageB;
            if (phoneA != phoneB) return !phoneA;
            return string.CompareOrdinal(idA ?? "", idB ?? "") < 0;
        }

        readonly List<Candidate> _cands = new List<Candidate>();

        void Gather(string title, List<Candidate> into, bool forceSelfUnsynced = false)
        {
            into.Clear();
            if (!string.IsNullOrEmpty(LocalId) && RunsTitle(title))
                into.Add(new Candidate(LocalId, _isPhone(), _synced && !forceSelfUnsynced, _joined));
            double now = _time();
            foreach (var p in _peers.Values)
            {
                if (now - p.heard > PeerTimeout) continue;
                if (!string.IsNullOrEmpty(title) && !p.titles.Contains(title)) continue;
                into.Add(new Candidate(p.id, p.phone, p.synced && !forceSelfUnsynced, p.joined));
            }
            if (string.IsNullOrEmpty(title)) return;

            // Players WEARING this title's rig. A device whose beat says it wears
            // another title's rig (or none: between zones, rig hidden) is not in
            // this title's room while anyone is — it never leads it or ranks in
            // it (LaserTag/SAL: a player in another game's zone led this one).
            // Unknown (a build that does not send "w") stays eligible, and with
            // no wearer at all every device running the title counts, as before.
            bool anyWearer = false;
            for (int i = 0; i < into.Count && !anyWearer; i++) anyWearer = Wears(into[i].id, title) == true;
            if (anyWearer) into.RemoveAll(c => Wears(c.id, title) == false);
        }

        /// <summary>Does <paramref name="id"/> wear <paramref name="title"/>'s PlayerRig? null = unknown.</summary>
        public bool? Wears(string id, string title)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;
            if (id == LocalId)
            {
                if (_worn == null) return null;
                ScanWorn(_time());
                return _myWorn.Contains(title);
            }
            if (!_peers.TryGetValue(id, out var p) || !p.wornKnown) return null;
            return p.worn.Contains(title);
        }

        /// <summary>
        /// The leader among live devices that run <paramref name="title"/>
        /// (null = every device in the session). Re-derived on every call.
        /// </summary>
        readonly List<Candidate> _tmp = new List<Candidate>();
        public string LeaderFor(string title)
        {
            Gather(title, _tmp);
            return PickLeader(_tmp);
        }

        /// <summary>
        /// 1-based join-order rank of <paramref name="id"/> among live devices
        /// running <paramref name="title"/> (headsets and phones alike, in
        /// seniority order; unsynced devices last). 0 = not in the room.
        /// Stable for everyone already here, so it can pick colours and queues.
        /// </summary>
        public int Rank(string id, string title = null)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            var list = new List<Candidate>();
            Gather(title, list);
            for (int i = 0; i < list.Count; i++) { var c = list[i]; c.phone = false; list[i] = c; }
            list.Sort(Compare);
            for (int i = 0; i < list.Count; i++) if (list[i].id == id) return i + 1;
            return 0;
        }

        void Recompute()
        {
            Gather(null, _cands);
            string next = PickLeader(_cands);
            if (next == _leader) return;
            string old = _leader;
            _leader = next;
            NetLog.V($"[DreamParkSession] leader {old} -> {next}");
            // A new leader answers right away: newcomers and the clock need it.
            if (next == LocalId) _urgent = true;
            try { LeaderChanged?.Invoke(old, next); }
            catch (Exception e) { Debug.LogWarning("[DreamParkSession] LeaderChanged handler threw: " + e.Message); }
        }

        // ── Tick ─────────────────────────────────────────────────────

        public void Tick()
        {
            double now = _time();
            double dt = double.IsNaN(_lastTick) ? 0 : Math.Max(0, now - _lastTick);
            _lastTick = now;

            bool present = Link != null && Link.Present;
            bool connected = Link != null && Link.Connected;

            if (connected && !_wasConnected)
            {
                _connectedSince = now;
                // Synced alone, or away long enough that the room reaped us: the
                // room is authoritative and we rejoin as its newest member —
                // unless the room is our own empty one (we just started hosting
                // and nobody is on the relay): then the solo timeline IS the room.
                bool reaped = _disconnectedAt >= 0 && now - _disconnectedAt > PeerTimeout;
                if (_syncedSolo && !reaped && RoomKnownEmpty) _syncedSolo = false;
                else if (_syncedSolo && !reaped)
                {
                    // Oct 11 2026: hold the solo game until we hear the room. A
                    // room that is one lone, younger device adopts OUR timeline
                    // (the phone playing solo when a headset relay appears);
                    // anything else wins as before (see ResolveMerge).
                    _merging = true;
                    _mergingSince = now;
                    NetLog.V($"[DreamParkSession] connected with a solo timeline ({SyncedAge:F1} s): holding it until the room answers");
                }
                else if (_syncedSolo || reaped) Unsync();
                _urgent = true;
                _wasConnected = true;
                _disconnectedAt = -1;
                try { Reconnected?.Invoke(); }
                catch (Exception e) { Debug.LogWarning("[DreamParkSession] Reconnected handler threw: " + e.Message); }
            }
            else if (!connected && _wasConnected)
            {
                _wasConnected = false;
                _connectedSince = -1;
                _disconnectedAt = now;
                _peers.Clear();   // nobody is reachable; the roster is just us
                _merging = false; // still solo: nothing to merge with
            }

            // Age out silent peers.
            _scratch.Clear();
            foreach (var p in _peers.Values)
                if (now - p.heard > PeerTimeout) _scratch.Add(p.id);
            for (int i = 0; i < _scratch.Count; i++) _peers.Remove(_scratch[i]);

            if (_mergeLoser != null && !_peers.ContainsKey(_mergeLoser)) _mergeLoser = null;   // it left

            // Holding a solo timeline and no synced room spoke up: we are the room.
            if (_merging && now - _mergingSince >= SilenceWindowFollower) FinishMerge("no synced device in the room");

            if (_worn != null) ScanWorn(now);

            if (!_synced)
            {
                if (!present)
                {
                    if (now - _createdAt >= SoloGraceNoClient) BecomeSynced(solo: true);
                }
                else if (!connected)
                {
                    // The arbiter heard no host in a full listen window: nobody
                    // can answer, so play solo now instead of after the grace.
                    if (now - _createdAt >= SoloGraceDisconnected || RoomKnownEmpty) BecomeSynced(solo: true);
                }
                else
                {
                    double waited = now - _connectedSince;
                    if (!AnySyncedPeer())
                    {
                        // Nobody synced is here: a fresh room. Its first headset
                        // (by the same order) starts it; the others wait for its
                        // beat (bounded). A room the arbiter knows is empty (we
                        // host it, nobody else is on the relay) has nobody to
                        // wait for: start it at once.
                        Gather(null, _cands, forceSelfUnsynced: true);
                        bool first = PickLeader(_cands) == LocalId;
                        if ((first && (waited >= SilenceWindow || RoomKnownEmpty)) || waited >= SilenceWindowFollower)
                            BecomeSynced(solo: false);
                    }
                    else if (waited >= SilenceWindowFollower)
                        _urgent = true;   // a synced peer exists but its beat never reached us: ask again
                }
            }

            // Slew toward the leader's beat so a countdown never jumps a frame
            // and the clock NEVER runs backwards (backward slew < 1 s/s).
            if (_haveOffset && _offset != _target)
            {
                double d = _target - _offset;
                double step = (d > 0 ? ClockSlew : ClockSlewBack) * dt;
                _offset = Math.Abs(d) <= step ? _target : _offset + (d > 0 ? step : -step);
            }

            if (_titles != null && now >= _nextTitleScan)
            {
                _nextTitleScan = now + 2.0;
                ScanTitles();
            }

            Recompute();

            if (connected)
            {
                bool urgentOk = _urgent && now - _lastUrgent >= 0.25;
                if (now >= _nextBeat || urgentOk)
                {
                    if (Budget.TryTake(1, critical: true))
                    {
                        SendBeat();
                        _nextBeat = now + BeatInterval;
                        if (urgentOk) { _urgent = false; _lastUrgent = now; }
                    }
                }
            }

            for (int i = States.Count - 1; i >= 0; i--)
            {
                if (i >= States.Count) continue;
                try { States[i].Tick(); }
                catch (Exception e) { Debug.LogWarning("[SharedState] tick threw: " + e.Message); }
            }
        }

        void ScanTitles()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try { foreach (var t in _titles()) if (!string.IsNullOrEmpty(t) && t.IndexOf(',') < 0) set.Add(t); }
            catch (Exception e) { Debug.LogWarning("[DreamParkSession] title scan threw: " + e.Message); }
            foreach (var s in States) if (!string.IsNullOrEmpty(s.Title)) set.Add(s.Title);
            if (set.SetEquals(_myTitles)) return;
            _myTitles.Clear();
            foreach (var t in set) _myTitles.Add(t);
            var sorted = new List<string>(_myTitles);
            sorted.Sort(string.CompareOrdinal);
            _myTitlesWire = string.Join(",", sorted);
            _urgent = true;
        }

        /// <summary>
        /// The link's arbiter knows nobody else can be in this session right now
        /// (<see cref="ISessionRoomHint"/>) and no peer is live here either. The
        /// session and every SharedState then sync at once: nobody can answer.
        /// </summary>
        public bool RoomKnownEmpty
        {
            get
            {
                if (!(Link is ISessionRoomHint hint) || !hint.KnownAlone) return false;
                double now = _time();
                foreach (var p in _peers.Values) if (now - p.heard <= PeerTimeout) return false;
                return true;
            }
        }

        void ScanWorn(double now)
        {
            if (_worn == null || now < _nextWornScan) return;
            _nextWornScan = now + 0.25;
            var set = new HashSet<string>(StringComparer.Ordinal);
            try { foreach (var t in _worn()) if (!string.IsNullOrEmpty(t) && t.IndexOf(',') < 0) set.Add(t); }
            catch (Exception e) { Debug.LogWarning("[DreamParkSession] worn-rig scan threw: " + e.Message); }
            if (set.SetEquals(_myWorn)) return;
            _myWorn.Clear();
            foreach (var t in set) _myWorn.Add(t);
            var sorted = new List<string>(_myWorn);
            sorted.Sort(string.CompareOrdinal);
            _myWornWire = string.Join(",", sorted);
            _urgent = true;   // peers re-scope dp.leader() on the next beat
        }

        bool AnySyncedPeer()
        {
            double now = _time();
            foreach (var p in _peers.Values)
                if (p.synced && now - p.heard <= PeerTimeout) return true;
            return false;
        }

        void Unsync()
        {
            _synced = false;
            _syncedSolo = false;
            _joined = double.NaN;   // rejoin as the room's newest member
            _merging = false;
            _adoptedFrom = "";
            _syncedAt = double.NaN;
        }

        // ── Merge (Oct 11 2026; see the file header) ─────────────────

        /// <summary>Synced, not holding a solo timeline, and no other synced device is here (devices still holding theirs do not count; nor does <paramref name="except"/>).</summary>
        bool RoomAlone(string except = null)
        {
            if (!_synced || _merging) return false;
            double now = _time();
            foreach (var p in _peers.Values)
                if (p.synced && !p.merging && p.id != except && now - p.heard <= PeerTimeout) return false;
            return true;
        }

        /// <summary>A beat from a synced peer: settle a pending merge, on either side of it.</summary>
        void ResolveMerge(Peer peer)
        {
            if (!_synced || !peer.synced) return;
            double myAge = SyncedAge;
            if (_merging && !peer.merging)
            {
                // We hold a solo timeline and just heard the room.
                if (peer.adoptedFrom == LocalId) { FinishMerge(peer.id + " adopted our timeline"); return; }
                bool lone = peer.roomAlone && !double.IsNaN(peer.age);
                if (lone)
                {
                    // Any OTHER synced, non-holding device here = an established room.
                    double now = _time();
                    foreach (var p in _peers.Values)
                        if (p != peer && p.synced && !p.merging && now - p.heard <= PeerTimeout) { lone = false; break; }
                }
                if (lone && MergeWinner(myAge, _isPhone(), LocalId, peer.age, peer.phone, peer.id, MergeTieSeconds))
                {
                    FinishMerge($"older than {peer.id}'s lone room ({myAge:F1} s vs {peer.age:F1} s)");
                    // Its beats speak for the timeline it is about to drop until
                    // it shows it took ours: never take its clock before that.
                    _mergeLoser = peer.id;
                }
                else
                    LoseMerge(peer.id, lone ? $"{peer.id}'s lone room is older ({peer.age:F1} s vs {myAge:F1} s)" : $"{peer.id}'s room is established");
            }
            else if (!_merging && peer.merging)
            {
                // Someone holding a solo timeline joined OUR room.
                if (_adoptedFrom == peer.id) return;               // we already took theirs
                if (!RoomAlone(except: peer.id)) return;           // established room: they adopt ours
                if (double.IsNaN(peer.age)) return;
                if (!MergeWinner(myAge, _isPhone(), LocalId, peer.age, peer.phone, peer.id, MergeTieSeconds))
                    LoseMerge(peer.id, $"{peer.id} brought an older solo timeline ({peer.age:F1} s vs our {myAge:F1} s)");
            }
            else if (_merging && peer.merging)
            {
                // Two solo devices met in a room with nobody synced in it.
                if (!MergeWinner(myAge, _isPhone(), LocalId, peer.age, peer.phone, peer.id, MergeTieSeconds))
                    LoseMerge(peer.id, $"{peer.id}'s solo timeline is older");
            }
        }

        void FinishMerge(string why)
        {
            if (!_merging) return;
            _merging = false;
            _syncedSolo = false;
            _urgent = true;
            NetLog.V($"[DreamParkSession] merge: kept our timeline — {why}");
        }

        void LoseMerge(string winner, string why)
        {
            NetLog.V($"[DreamParkSession] merge: adopting {winner}'s timeline — {why}");
            _mergeLoser = null;
            Unsync();
            MergeLosses++;
            _urgent = true;
            Recompute();
        }

        void BecomeSynced(bool solo)
        {
            if (_synced) return;
            _synced = true;
            _syncedSolo = solo;
            _syncedAt = _time();
            _adoptedFrom = "";
            // A solo clock IS its timeline (offset as it stands). If it survives a
            // merge, the headset that adopted it beats it back with a small error:
            // slew that, never jump back (Oct 11 2026). A solo timeline that loses
            // un-syncs and adopts by jumping as before. (A room started by silence
            // keeps the old first-beat jump: two racing fresh rooms differ by minutes.)
            if (solo) { _target = _offset; _haveOffset = true; }
            if (!_haveSeed)
            {
                // First leader of a fresh room picks the seed. Anything unique
                // enough works: it is shared by value, never re-derived.
                uint s = unchecked((uint)DateTime.UtcNow.Ticks) ^ Fnv(LocalId) ^ (uint)Environment.TickCount;
                _seed = s == 0 ? 0x9e3779b9u : s;
                _haveSeed = true;
            }
            _joined = WireJoined(Now);
            _urgent = true;
            NetLog.V($"[DreamParkSession] synced ({(solo ? "solo" : "room")}) seed {_seed:x8}");
        }

        static uint Fnv(string s)
        {
            uint h = 2166136261u;
            if (s != null) for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h;
        }

        void SendBeat()
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(192);
            sb.Append("{\"i\":\"").Append(Esc(LocalId)).Append("\",\"dv\":\"").Append(_isPhone() ? "phone" : "headset")
              .Append("\",\"sy\":").Append(_synced ? 1 : 0)
              .Append(",\"ld\":").Append(IsLeader ? 1 : 0);
            if (_myTitlesWire.Length > 0) sb.Append(",\"g\":\"").Append(Esc(_myTitlesWire)).Append('"');
            if (_worn != null) sb.Append(",\"w\":\"").Append(Esc(_myWornWire)).Append('"');
            if (_synced)
            {
                // Parsed in C# as doubles (never through Lua's float32 json_parse).
                sb.Append(",\"t\":").Append(Now.ToString("F4", ic))
                  .Append(",\"j\":").Append(_joined.ToString("F4", ic))
                  .Append(",\"pg\":").Append(Link != null ? Math.Max(0, Link.PingMs) : 0)
                  .Append(",\"sd\":\"").Append(_seed.ToString("x8", ic)).Append('"');
                // Merge (Oct 11 2026): timeline age, lone room, holding, adopted-from.
                sb.Append(",\"ag\":").Append(SyncedAge.ToString("F2", ic));
                if (_merging) sb.Append(",\"mg\":1");
                else if (RoomAlone()) sb.Append(",\"ra\":1");
                if (_adoptedFrom.Length > 0) sb.Append(",\"af\":\"").Append(Esc(_adoptedFrom)).Append('"');
            }
            sb.Append('}');
            Link.Publish(TypeBeat, sb.ToString());
        }

        internal static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ── Receive ──────────────────────────────────────────────────

        /// <summary>Feed every session-level event here (type, full wire JSON).</summary>
        public void HandleGlobal(string type, string json)
        {
            if (type == null || json == null) return;
            if (type != TypeBeat && type != TypeBye && type != TypeState && type != PlayerPresence.TypeBye) return;
            JSONObject p;
            try
            {
                var obj = new JSONObject(json);
                p = obj.GetField("payload");
            }
            catch (Exception e) { Debug.LogWarning("[DreamParkSession] bad " + type + ": " + e.Message); return; }
            if (p == null || p.type != JSONObject.Type.Object) return;

            if (type == TypeState) { RouteState(p); return; }

            string id = p.GetField("i")?.stringValue;
            if (string.IsNullOrEmpty(id) || id == LocalId) return;

            if (type == TypeBye || type == PlayerPresence.TypeBye)
            {
                if (_peers.Remove(id)) Recompute();
                foreach (var st in States) st.ForgetMember(id);
                return;
            }

            HandleBeat(id, p);
        }

        void HandleBeat(string id, JSONObject p)
        {
            double now = _time();
            bool isNew = !_peers.TryGetValue(id, out var peer);
            if (isNew) { peer = new Peer { id = id }; _peers[id] = peer; }
            peer.heard = now;
            peer.phone = p.GetField("dv")?.stringValue == "phone";
            peer.synced = Num(p, "sy") >= 1;
            peer.joined = peer.synced && p.HasField("j") ? p.GetField("j").doubleValue : double.NaN;
            peer.titles.Clear();
            string g = p.GetField("g")?.stringValue;
            if (!string.IsNullOrEmpty(g)) foreach (var t in g.Split(',')) if (t.Length > 0) peer.titles.Add(t);
            peer.worn.Clear();
            peer.wornKnown = p.HasField("w");
            string w = peer.wornKnown ? p.GetField("w")?.stringValue : null;
            if (!string.IsNullOrEmpty(w)) foreach (var t in w.Split(',')) if (t.Length > 0) peer.worn.Add(t);
            peer.merging = peer.synced && Num(p, "mg") >= 1;
            peer.roomAlone = peer.synced && Num(p, "ra") >= 1;
            peer.age = peer.synced && p.HasField("ag") ? Num(p, "ag") : double.NaN;
            peer.adoptedFrom = p.GetField("af")?.stringValue ?? "";

            // A newcomer (or a peer that dropped back to unsynced) gets the
            // leader's beat at once instead of waiting out the interval.
            if ((isNew || !peer.synced) && _synced && IsLeader) _urgent = true;
            if (isNew) NetLog.V($"[DreamParkSession] peer {id} ({(peer.phone ? "phone" : "headset")}, synced={peer.synced})");

            Recompute();
            ResolveMerge(peer);
            // The room we beat has dropped its timeline (un-synced) or taken ours.
            if (_mergeLoser == id && (!peer.synced || peer.adoptedFrom == LocalId)) _mergeLoser = null;

            // Clock + seed come from the LEADER only (mp_kit took anyone's beat).
            // While we are unsynced our leader is the most senior synced device,
            // because synced devices order first. Never while holding our own
            // solo timeline (the merge decides whose clock survives), nor from
            // a room we beat until it has taken ours.
            if (peer.synced && id == _leader && p.HasField("t") && !_merging && id != _mergeLoser)
            {
                double t = p.GetField("t").doubleValue;
                double oneWay = (Math.Max(0, Link != null ? Link.PingMs : 0) + Math.Max(0, Num(p, "pg"))) / 1000.0;
                double target = t + oneWay - now;
                _target = target;
                // Jump only before we are synced, or FORWARD past the snap
                // distance. Backward corrections always slew (monotonic clock).
                if (!_haveOffset || !_synced || target - _offset > ClockSnap) _offset = target;
                _haveOffset = true;
                string sd = p.GetField("sd")?.stringValue;
                if (!string.IsNullOrEmpty(sd) && uint.TryParse(sd, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint seed))
                {
                    if (!_haveSeed || seed != _seed) NetLog.V($"[DreamParkSession] seed {_seed:x8} -> {seed:x8} from {id}");
                    _seed = seed; _haveSeed = true;
                }
                if (!_synced)
                {
                    _synced = true; _syncedSolo = false; _urgent = true;
                    _joined = WireJoined(Now);
                    // Our timeline is now theirs: its age, and who we took it from.
                    _syncedAt = double.IsNaN(peer.age) ? now : now - peer.age;
                    _adoptedFrom = id;
                    NetLog.V($"[DreamParkSession] synced to {id}");
                    Recompute();
                }
            }
        }

        void RouteState(JSONObject p)
        {
            string bus = p.GetField("b")?.stringValue;
            string name = p.GetField("n")?.stringValue;
            string from = p.GetField("u")?.stringValue;
            if (bus == null || name == null || string.IsNullOrEmpty(from) || from == LocalId) return;
            for (int i = 0; i < States.Count; i++)
            {
                var s = States[i];
                if (s.BusKey == bus && s.Name == name) { s.Receive(from, p); return; }
            }
        }

        internal static double Num(JSONObject p, string key)
        {
            var f = p.GetField(key);
            if (f == null) return 0;
            if (f.type == JSONObject.Type.Number) return f.doubleValue;
            if (f.type == JSONObject.Type.Bool) return f.boolValue ? 1 : 0;
            return 0;
        }

        /// <summary>Say goodbye so peers re-elect at once instead of timing us out.</summary>
        public void Shutdown()
        {
            if (Link == null || !Link.Connected) return;
            for (int i = 0; i < States.Count; i++) States[i].SendLeave();
            Link.Publish(TypeBye, "{\"i\":\"" + Esc(LocalId) + "\"}");
        }

        /// <summary>
        /// The headset was doffed / the app paused: say goodbye now so the room
        /// re-elects in a beat instead of stalling for the timeout, and come
        /// back as the room's newest member (it may have moved on without us).
        /// </summary>
        public void Pause(bool paused)
        {
            if (paused) { Shutdown(); _disconnectedAt = _time(); _wasConnected = false; _merging = false; }
            else if (_synced && !_syncedSolo) Unsync();
        }
    }

    /// <summary>
    /// The session for this device: leader, shared clock, session seed.
    /// Static facade over a <see cref="SessionEngine"/> bound to the real relay
    /// (DreamBoxClient). See the file header for the rules.
    /// </summary>
    public static class DreamParkSession
    {
        static SessionEngine _engine;

        /// <summary>The engine behind the facade (created on first use).</summary>
        public static SessionEngine Engine
        {
            get
            {
                if (_engine == null)
                {
                    _engine = new SessionEngine(PlayerPresence.MyId, new RelayLink(),
                        () => Time.realtimeSinceStartupAsDouble, () => DreamParkDevice.IsPhone, LoadedTitles, WornTitles);
                    DreamParkSessionDriver.Ensure();
                }
                return _engine;
            }
        }

        // Enter Play Mode without a domain reload keeps statics: start every
        // play session with a fresh engine, never last session's roster.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _engine = null;
            SharedState.ResetRegistry();
        }

        /// <summary>Replace the engine (tests). Returns the previous one.</summary>
        public static SessionEngine SwapEngine(SessionEngine engine)
        {
            var old = _engine;
            _engine = engine;
            return old;
        }

        /// <summary>Titles with content live on this device (announced so leaders can be scoped per title).</summary>
        static IEnumerable<string> LoadedTitles()
        {
            var areas = GameArea.allGameAreas;
            if (areas != null)
                for (int i = 0; i < areas.Count; i++)
                    if (areas[i] != null && !string.IsNullOrEmpty(areas[i].gameId)) yield return areas[i].gameId;
            if (PlayerRig.instances != null)
                foreach (var kv in PlayerRig.instances)
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Key)) yield return kv.Key;
        }

        /// <summary>
        /// Titles whose PlayerRig this device is WEARING right now: the shown rig
        /// (PlayerRig.Instance, active) under its gameId and every content alias
        /// it registered as. Empty between zones. Scopes dp.leader() / dp.rank().
        /// </summary>
        static IEnumerable<string> WornTitles()
        {
            var rig = PlayerRig.Instance;
            if (rig == null || !rig.gameObject.activeInHierarchy) yield break;
            if (!string.IsNullOrEmpty(rig.gameId)) yield return rig.gameId;
            if (PlayerRig.instances != null)
                foreach (var kv in PlayerRig.instances)
                    if (kv.Value == rig && !string.IsNullOrEmpty(kv.Key) && kv.Key != rig.gameId) yield return kv.Key;
        }

        /// <summary>This device's player id (= PlayerPresence.MyId = dp.me()).</summary>
        public static string LocalId => Engine.LocalId;
        /// <summary>Session-wide leader. Gameplay should use <see cref="LeaderFor"/> with its title.</summary>
        public static string LeaderId => Engine.LeaderId;
        public static bool IsLeader => Engine.IsLeader;

        /// <summary>Leader among live devices running <paramref name="title"/> (null = whole session).</summary>
        public static string LeaderFor(string title) => Engine.LeaderFor(title);
        public static bool IsLeaderFor(string title) => Engine.LeaderFor(title) == Engine.LocalId;

        /// <summary>1-based join-order rank of a device among those running <paramref name="title"/>; 0 = not here.</summary>
        public static int Rank(string id, string title = null) => Engine.Rank(id, title);

        /// <summary>(oldLeaderId, newLeaderId).</summary>
        public static event Action<string, string> LeaderChanged
        {
            add => Engine.LeaderChanged += value;
            remove { if (_engine != null) _engine.LeaderChanged -= value; }
        }

        /// <summary>Shared session clock, seconds. Agreed once <see cref="ClockSynced"/>.</summary>
        public static double Now => Engine.Now;
        public static bool ClockSynced => Engine.ClockSynced;
        public static uint Seed => Engine.Seed;
        public static float Random01(string key, int salt = 0) => Engine.Random01(key, salt);
        /// <summary>Times this device adopted another device's timeline in a merge (Oct 11 2026; NetBodies watches it). 0 before the session runs; never creates it.</summary>
        public static int MergeLosses => _engine != null ? _engine.MergeLosses : 0;
        /// <summary>A solo timeline is being held while the room answers (Oct 11 2026). Never creates the session.</summary>
        public static bool Merging => _engine != null && _engine.Merging;

        /// <summary>Shared token bucket for SDK system traffic on this device.</summary>
        public static NetSendBudget Budget => Engine.Budget;
        public static bool IsLive(string id) => Engine.IsLive(id);
        public static bool IsPhone(string id) => Engine.IsPhone(id);

        // ── Attraction-relative frames (wire + SharedState + physics) ──
        //
        // Positions that belong to an attraction travel RELATIVE TO THE
        // ATTRACTION, not the park: robust even if two headsets' park frames
        // disagree slightly (MULTIPLAYER.md rule 8). The frame is the nearest
        // LevelTemplate (attraction, Sequence level) — never a prop root,
        // which may itself be moving. Null = no attraction: park-local.

        /// <summary>The attraction frame for <paramref name="go"/>, or null.</summary>
        public static Transform FrameOf(GameObject go)
        {
            if (go == null) return null;
            var level = go.GetComponentInParent<LevelTemplate>(true);
            return level != null ? level.transform : null;
        }

        /// <summary>World → frame-local (park-local when <paramref name="frame"/> is null).</summary>
        public static Vector3 ToFrame(Transform frame, Vector3 world)
            => frame != null ? frame.InverseTransformPoint(world) : ParkRoot.ToPark(world);
        public static Vector3 FromFrame(Transform frame, Vector3 local)
            => frame != null ? frame.TransformPoint(local) : ParkRoot.FromPark(local);
        public static Quaternion ToFrameRot(Transform frame, Quaternion world)
            => frame != null ? Quaternion.Inverse(frame.rotation) * world : ParkRoot.ToParkRot(world);
        public static Quaternion FromFrameRot(Transform frame, Quaternion local)
            => frame != null ? frame.rotation * local : ParkRoot.FromParkRot(local);

        sealed class RelayLink : ISessionLink, ISessionRoomHint
        {
            /// <summary>What the session arbiter knows (NetSessionArbiter.KnownAlone).</summary>
            public bool KnownAlone
            {
                get { var a = NetSessionArbiter.Instance; return a != null && a.isActiveAndEnabled && a.KnownAlone; }
            }
            public bool Present => DreamBoxClient.Instance != null;
            public bool Connected
            {
                get { var c = DreamBoxClient.Instance; return c != null && c.ConnectionState == DreamBoxClient.State.Connected; }
            }
            public int PingMs { get { var c = DreamBoxClient.Instance; return c != null ? Math.Max(0, c.Ping) : 0; } }
            public void Publish(string type, string payloadJson)
            {
                var c = DreamBoxClient.Instance;
                if (c == null || c.ConnectionState != DreamBoxClient.State.Connected) return;
                c.PublishRaw(type, payloadJson);
            }
        }
    }

    /// <summary>
    /// Hidden runtime host for the session: binds the relay's session-level
    /// events and ticks the engine (and every SharedState) once a frame.
    /// Self-creating; never added by a creator.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [AddComponentMenu("")]
    internal sealed class DreamParkSessionDriver : MonoBehaviour
    {
        static DreamParkSessionDriver _instance;
        DreamBoxClient _client;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() { var _ = DreamParkSession.Engine; Ensure(); }

        internal static void Ensure()
        {
            if (_instance != null || !Application.isPlaying) return;
            var go = new GameObject("~DreamParkSession") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DreamParkSessionDriver>();
        }

        void Update()
        {
            var client = DreamBoxClient.Instance;
            if (client != _client)
            {
                if (_client != null) _client.OnGlobalEvent -= OnGlobal;
                _client = client;
                if (_client != null) _client.OnGlobalEvent += OnGlobal;
            }
            DreamParkSession.Engine.Tick();
        }

        void OnGlobal(string type, string json)
        {
            if (type == null || type.Length < 2) return;
            if (type[0] != '_' && type != PlayerPresence.TypeBye) return;
            DreamParkSession.Engine.HandleGlobal(type, json);
        }

        void OnApplicationQuit()
        {
            try { DreamParkSession.Engine.Shutdown(); } catch (Exception) { }
        }

        // Doffed headset / backgrounded phone: leave now (the room re-elects in
        // a beat instead of stalling for the timeout) and rejoin as newest.
        void OnApplicationPause(bool paused)
        {
            try { DreamParkSession.Engine.Pause(paused); } catch (Exception) { }
        }

        void OnDestroy()
        {
            if (_client != null) _client.OnGlobalEvent -= OnGlobal;
            if (_instance == this) _instance = null;
        }
    }
}
