using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Defective.JSON;

namespace DreamPark
{
    // ─────────────────────────────────────────────────────────────────────
    //  SharedState.cs — one replicated, revisioned key/value table per name.
    //
    //  The encounter-as-a-snapshot pattern (MULTIPLAYER.md rules 2, 4, 5, 6)
    //  that every game used to hand-roll — Birthday Bash's boss `st`, LaserTag's
    //  round state, mp_kit's snapshot-on-hello — as an SDK service:
    //
    //    ONE WRITER   the state's leader applies every write. Anyone else's
    //                 set() becomes a request the leader applies, so two
    //                 players pressing Start at once produce one fight.
    //    SNAPSHOTS    every change goes out as the WHOLE table with a revision
    //                 (rev, then writer id). Receivers keep the newest. A lost
    //                 message is repaired by the next one, never accumulated.
    //    LATE JOIN    a new holder asks (on creation and on every relay
    //                 (re)connect); the leader answers at once and — in case
    //                 the leader just left — any other holder with a newer rev
    //                 answers after a short staggered delay. Until a snapshot
    //                 arrives the state is NOT synced, unless the room is
    //                 provably empty: every live player of the title announced
    //                 itself here and none holds it (the first of them starts
    //                 it), or 10 s passed with no holder answering. With no
    //                 relay client it syncs alone after 0.25 s, with a client
    //                 that never connects after 6 s — at once when the arbiter
    //                 KNOWS the room is empty (it hosts with nobody on the
    //                 relay, or heard no host at all: SessionEngine.RoomKnownEmpty);
    //                 a state synced alone that later connects adopts the
    //                 room's snapshot (unless that room is its own empty one). Writes made in
    //                 the unsynced window are held,
    //                 then applied if the room was empty and DROPPED if the
    //                 room already had state, so a late joiner's start() can
    //                 never reset a fight in progress.
    //    MERGE        (Oct 11 2026) a state synced alone that connects HOLDS
    //                 (no sends, ignores the room) while the session settles
    //                 whose timeline survives (SessionEngine merge: a lone,
    //                 younger room adopts ours; an established room wins). The
    //                 side that loses re-asks like a late joiner — but keeps
    //                 its table aside, and if the winner's answer is EMPTY
    //                 (rev 0) it restores it and seeds the room with it: the
    //                 side holding state seeds a room that has none.
    //    HAND-OFF     the writer is picked among the devices holding this
    //                 state: synced first, headsets before phones, then
    //                 SENIORITY (the incumbent keeps writing until it leaves),
    //                 then id as the tie-break — the session leader whenever
    //                 the leader has it. Every holder keeps the full table, so the next
    //                 writer continues from the current rev; heartbeats repair
    //                 anyone who fell behind (max-rev merge).
    //
    //  Values are JSON fragments: numbers, strings, bools, small tables, and
    //  Vector3 / rotation / pose ATTRACTION-RELATIVE ({"$a":[x,y,z]} beside the
    //  park-local {"$p":...} — see DreamParkSession.FrameOf), so a position
    //  means the same spot in the attraction on every headset.
    //
    //  Bus: an address every device computes identically — the attraction's
    //  NetId (or the id a NetId there would hash to), the given object's, or,
    //  outside any attraction (Player rig, Game Manager), the title — per
    //  package INSTANCE for a Game Manager whose package scope names one
    //  ("pkg|{title}|manager|{instance}"), so two packages of one title in a
    //  park keep separate tables. Traffic
    //  is session-level ("__ss"), so it never reaches a script's onnet.
    //
    //  Wire: "__ss" {"b":bus,"n":name,"k":kind,"u":sender,"sy":0|1, ...}
    //    rq {r}           please send me the state (I have rev r)
    //    sn {r,w,d:{..}}  full snapshot
    //    hb {r,w}         I hold this state (every ~3 s)
    //    ws {kv:{..},x:[..]}   write request to the leader (x = deleted keys)
    //    rk {q,p}         request(kind, payload) to the leader
    //    lv {}            I no longer hold this state
    //
    //  Internal engine service, not a component. Lua: dp.shared_state(name[, go]).
    // ─────────────────────────────────────────────────────────────────────
    public sealed class SharedState
    {
        public const int MaxKeyLength = 64;
        public const int MaxValueChars = 4096;
        public const int MaxSnapshotChars = 14000;   // relay cap is 16 KB per message
        public const double HeartbeatInterval = 3.0;
        public const double MemberTimeout = 10.0;
        public const double FlushInterval = 1.0 / 15.0;

        /// <summary>Warning sink (the Unity console; swapped by out-of-Editor test runners).</summary>
        internal static Action<string> Warn = m => Debug.LogWarning(m);

        public string Name { get; }
        /// <summary>Content id this state belongs to (leader/sync scope), or null.</summary>
        public string Title { get; private set; }
        /// <summary>No holder answered for this long while title peers exist: the room really is empty.</summary>
        public const double EmptyRoomTimeout = 10.0;
        /// <summary>Wire address, null until resolved (a NetId must have registered).</summary>
        public string BusKey { get; private set; }
        /// <summary>Attraction frame Vector3 values are relative to (null = park-local).</summary>
        public Transform Frame { get; private set; }
        public SessionEngine Engine { get; }
        public bool Disposed { get; private set; }

        /// <summary>Revision of the table (0 = nothing written yet).</summary>
        public int Rev { get; private set; }
        /// <summary>Who wrote <see cref="Rev"/>.</summary>
        public string Writer { get; private set; } = "";
        /// <summary>Bumped on every local change of the table (cache key).</summary>
        public int Version { get; private set; }
        /// <summary>False until a snapshot arrived or the room is known to be empty (see the file header).</summary>
        public bool Synced { get; private set; }

        /// <summary>(key, canonical JSON or null when deleted) — fired on every applied change.</summary>
        public event Action<string, string> Changed;
        /// <summary>(kind, payload JSON, fromId) — fired on the LEADER only.</summary>
        public event Action<string, string, string> Requested;

        readonly SortedDictionary<string, string> _values = new SortedDictionary<string, string>(StringComparer.Ordinal);

        sealed class Member { public double heard; public bool synced; }
        readonly Dictionary<string, Member> _members = new Dictionary<string, Member>(StringComparer.Ordinal);

        // Writes / requests made before Synced, and requests that reached an
        // unsynced leader. Applied (or dropped) when sync completes.
        readonly List<KeyValuePair<string, string>> _queuedWrites = new List<KeyValuePair<string, string>>();
        readonly List<(string kind, string json)> _queuedRequests = new List<(string, string)>();
        readonly List<(string from, JSONObject p)> _queuedIncoming = new List<(string, JSONObject)>();
        bool _warnedDrop;

        double _openedAt;
        double _requestedAt = -1, _firstRequestAt = -1;
        bool _rqPending;
        double _lastRq = -9, _lastSn = -9, _lastHbAnswer = -9;
        double _nextHb;
        bool _dirty;
        double _answerAt = -1;
        int _answerFor;
        bool _wasConnected, _syncedSolo;

        // Merge (Oct 11 2026; see the file header).
        bool _mergeHold;                          // synced alone, connected, session still deciding
        int _mergeLossSeen;                       // Engine.MergeLosses already handled
        SortedDictionary<string, string> _stash;  // our table, kept aside after losing a merge
        int _stashRev;
        string _stashWriter;

        // Runtime binding (null for detached/test states).
        GameObject _busObject;
        bool _hadBusObject;
        string _titleKey;
        int _openFrame;
        string _registryKey;

        internal bool LuaListening;
        internal int Handle;

        SharedState(SessionEngine engine, string name)
        {
            Engine = engine;
            Name = name ?? "";
            _openedAt = engine.LocalTime;
            _nextHb = _openedAt + HeartbeatInterval * (0.5 + Jitter(engine.LocalId + name));
            _mergeLossSeen = engine.MergeLosses;
        }

        /// <summary>
        /// Follow the session's merge (Oct 11 2026): when this device gave its
        /// timeline up, re-ask the room like a late joiner, keeping our table
        /// aside (restored if the room turns out to hold nothing); when the
        /// merge ended in our favour, stop holding.
        /// </summary>
        void FollowMerge()
        {
            if (Engine.MergeLosses != _mergeLossSeen)
            {
                _mergeLossSeen = Engine.MergeLosses;
                if (Synced)
                {
                    if (Rev > 0 || _values.Count > 0)
                    {
                        _stash = new SortedDictionary<string, string>(_values, StringComparer.Ordinal);
                        _stashRev = Rev;
                        _stashWriter = Writer;
                    }
                    Synced = false; _syncedSolo = false; Rev = 0; Writer = "";
                    _requestedAt = -1; _firstRequestAt = -1; _rqPending = false; _answerAt = -1; _dirty = false;
                    NetLog.V($"[SharedState] '{Name}' merge lost: asking the room (our rev {_stashRev} kept aside)");
                }
                _mergeHold = false;
                return;
            }
            if (_mergeHold && !Engine.Merging)
            {
                _mergeHold = false;
                _syncedSolo = false;
                _rqPending = false;
                // The room adopts our table: whoever asks gets it, and anyone
                // already here hears it now.
                if (Synced && _members.Count > 0) _dirty = true;
                NetLog.V($"[SharedState] '{Name}' merge kept our table (rev {Rev})");
            }
        }

        bool Holding => _mergeHold && Engine.Merging;

        /// <summary>Restore the table kept aside after a lost merge. True when there was one.</summary>
        bool RestoreStash()
        {
            if (_stash == null) return false;
            var changed = new List<string>();
            foreach (var kv in _stash)
                if (!_values.TryGetValue(kv.Key, out var old) || old != kv.Value) changed.Add(kv.Key);
            foreach (var k in _values.Keys)
                if (!_stash.ContainsKey(k)) changed.Add(k);
            _values.Clear();
            foreach (var kv in _stash) _values[kv.Key] = kv.Value;
            Rev = _stashRev;
            Writer = _stashWriter ?? "";
            _stash = null;
            if (changed.Count > 0) Version++;
            for (int i = 0; i < changed.Count; i++) RaiseChanged(changed[i]);
            NetLog.V($"[SharedState] '{Name}' the room held nothing: seeding it with our table (rev {Rev})");
            return true;
        }

        static double Jitter(string s)
        {
            uint h = 2166136261u;
            if (s != null) for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return (h % 1000) / 1000.0;
        }

        // ── Creation ─────────────────────────────────────────────────

        /// <summary>A state on an explicit bus key, driven by <paramref name="engine"/> (tests, tools).</summary>
        public static SharedState CreateDetached(SessionEngine engine, string busKey, string name, Transform frame = null, string title = null)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            var s = new SharedState(engine, name) { BusKey = busKey, Frame = frame, Title = title };
            engine.States.Add(s);
            return s;
        }

        static readonly Dictionary<string, SharedState> _registry = new Dictionary<string, SharedState>(StringComparer.Ordinal);
        static int _nextHandle = 1;
        static readonly Dictionary<int, SharedState> _byHandle = new Dictionary<int, SharedState>();

        internal static void ResetRegistry() { _registry.Clear(); _byHandle.Clear(); }

        /// <summary>
        /// Oct 15 2026 (game master RESTART): the host's per-title RUN suffix for bus keys
        /// (core LocationPlaybackRules.RunSalt: "~r{restartSeq}" once a title was restarted,
        /// "" otherwise). A restarted title's states open on a NEW bus, so a peer that has not
        /// rebuilt yet never hands the old round back. Null / "" = the bus as always.
        /// </summary>
        public static Func<string, string> BusSuffix;

        static string SuffixFor(string title)
        {
            if (BusSuffix == null || string.IsNullOrEmpty(title)) return "";
            try { return BusSuffix(title) ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// Oct 15 2026 (RESTART): stop holding every live state of <paramref name="title"/> (tell
        /// peers, forget it) — title-bus states outlive their scripts otherwise, and a rebuilt
        /// Game Manager would reopen the same table. Returns how many were disposed.
        /// </summary>
        public static int DisposeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            var doomed = new List<SharedState>();
            foreach (var st in _registry.Values)
                if (st != null && !st.Disposed && string.Equals(st.Title, title, StringComparison.OrdinalIgnoreCase)) doomed.Add(st);
            foreach (var st in doomed) st.Dispose();
            return doomed.Count;
        }

        internal static SharedState ByHandle(int h)
        {
            if (!_byHandle.TryGetValue(h, out var s)) return null;
            return s != null && !s.Disposed ? s : null;
        }

        /// <summary>
        /// The state called <paramref name="name"/> on this device's session.
        /// Bus: <paramref name="explicitObject"/> when given; otherwise the
        /// caller's attraction (nearest LevelTemplate, else its prop/attraction
        /// root); outside any attraction, the caller's title. Same arguments →
        /// same instance (every script sharing a name shares one table).
        /// </summary>
        public static SharedState Open(string name, GameObject explicitObject = null, GameObject caller = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            GameObject target = explicitObject != null ? explicitObject : DefaultBusObject(caller);
            string titleKey = null;
            string objKey;
            string busTitle = null;
            if (target != null) objKey = "i:" + target.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            else
            {
                titleKey = TitleOf(caller) ?? "";
                busTitle = TitleBusKey(titleKey, caller) + SuffixFor(titleKey);
                objKey = "g:" + busTitle;
            }
            string key = objKey + "\n" + name;
            if (_registry.TryGetValue(key, out var existing) && existing != null && !existing.Disposed)
            {
                existing.NoteOpener(caller);
                return existing;
            }

            var engine = DreamParkSession.Engine;
            var s = new SharedState(engine, name)
            {
                _busObject = target,
                _hadBusObject = target != null,
                _titleKey = busTitle,
                _openFrame = Time.frameCount,
                _registryKey = key,
                Frame = DreamParkSession.FrameOf(target != null ? target : caller),
                Title = target != null ? (TitleOf(target) ?? TitleOf(caller)) : titleKey,
            };
            if (string.IsNullOrEmpty(s.Title)) s.Title = null;
            s.NoteOpener(caller != null ? caller : target);
            s.Handle = _nextHandle++;
            _registry[key] = s;
            _byHandle[s.Handle] = s;
            engine.States.Add(s);
            return s;
        }

        static GameObject DefaultBusObject(GameObject caller)
        {
            if (caller == null) return null;
            var level = caller.GetComponentInParent<LevelTemplate>(true);
            if (level != null) return level.gameObject;
            return DreamParkLuaAPI.AttractionRoot(caller);
        }

        internal static string TitleOf(GameObject caller)
        {
            if (caller == null) return null;
            string id = DreamParkLuaAPI.GameId(caller);
            if (!string.IsNullOrEmpty(id)) return id;
            var rig = caller.GetComponentInParent<PlayerRig>(true);
            if (rig != null && !string.IsNullOrEmpty(rig.gameId)) return rig.gameId;
            var host = DreamParkLuaAPI.Package(caller);
            if (host != null && !string.IsNullOrEmpty(host.contentId)) return host.contentId;
            var level = host != null ? host.GetComponent<LevelTemplate>() : null;
            if (level != null && !string.IsNullOrEmpty(level.gameId)) return level.gameId;
            // Package scopes core stamps on the rig/manager: "pkg|{contentId}|{role}".
            for (var t = caller.transform; t != null; t = t.parent)
            {
                if (!t.TryGetComponent(out NetScope scope) || string.IsNullOrEmpty(scope.scopeKey)) continue;
                string k = scope.scopeKey;
                if (k.StartsWith("pkg|", StringComparison.Ordinal))
                {
                    int end = k.IndexOf('|', 4);
                    string cid = end > 4 ? k.Substring(4, end - 4) : null;
                    if (!string.IsNullOrEmpty(cid)) return cid;
                }
            }
            return null;
        }

        /// <summary>
        /// The title bus a state opened outside any attraction uses: the title,
        /// plus the package instance when the caller sits under a Game Manager
        /// scope that names one ("pkg|{title}|manager|{instance}", core).
        /// </summary>
        static string TitleBusKey(string title, GameObject caller)
        {
            string instance = PackageInstanceOf(caller);
            return string.IsNullOrEmpty(instance) ? title : title + "#" + instance;
        }

        /// <summary>The package instance a "pkg|{title}|{role}|{instance}" scope above <paramref name="caller"/> names, or null.</summary>
        internal static string PackageInstanceOf(GameObject caller)
        {
            for (var t = caller != null ? caller.transform : null; t != null; t = t.parent)
            {
                if (!t.TryGetComponent(out NetScope scope) || string.IsNullOrEmpty(scope.scopeKey)) continue;
                string k = scope.scopeKey;
                if (!k.StartsWith("pkg|", StringComparison.Ordinal)) return null;   // the first scope decides, as for NetId
                int a = k.IndexOf('|', 4);
                int b = a > 0 ? k.IndexOf('|', a + 1) : -1;
                return b > 0 && b + 1 < k.Length ? k.Substring(b + 1) : null;
            }
            return null;
        }

        // Objects whose scripts opened this state (dp.shared_state's caller), so
        // tools can find the bus a target's state ACTUALLY uses (BusKeysFor).
        readonly List<GameObject> _openers = new List<GameObject>();
        void NoteOpener(GameObject go)
        {
            if (go == null || _openers.Contains(go)) return;
            _openers.RemoveAll(o => o == null);
            if (_openers.Count < 16) _openers.Add(go);
        }

        /// <summary>
        /// Every bus a dp.shared_state opened by a script on <paramref name="target"/>
        /// lives on: first the resolved bus of every live state a script on the
        /// target (or below it) actually opened — the "b" on the wire — then the
        /// predicted ones: the object's own NetId ("o:"), its default bus (the
        /// attraction's), or — outside any attraction — its title bus ("g:").
        /// Tools (the simulated-peer scenarios) use it to recognise a game's
        /// state traffic for a target.
        /// </summary>
        public static List<string> BusKeysFor(GameObject target)
        {
            var keys = new List<string>();
            if (target == null) return keys;
            foreach (var st in _registry.Values)
            {
                if (st == null || st.Disposed || string.IsNullOrEmpty(st.BusKey) || keys.Contains(st.BusKey)) continue;
                foreach (var o in st._openers)
                    if (o != null && (o == target || o.transform.IsChildOf(target.transform))) { keys.Add(st.BusKey); break; }
            }
            string run = SuffixFor(TitleOf(target));
            if (target.TryGetComponent(out NetId own) && own.IsRegistered)
                keys.Add("o:" + own.Id.ToString(CultureInfo.InvariantCulture) + run);
            var bus = DefaultBusObject(target);
            if (bus != null)
            {
                string k = bus.TryGetComponent(out NetId nid) && nid.IsRegistered
                    ? "o:" + nid.Id.ToString(CultureInfo.InvariantCulture) + run
                    : "o:" + NetId.HashPath(bus.transform).ToString(CultureInfo.InvariantCulture) + run;
                if (!keys.Contains(k)) keys.Add(k);
            }
            else keys.Add("g:" + TitleBusKey(TitleOf(target) ?? "", target) + run);
            return keys;
        }

        void TryResolveBus()
        {
            if (_titleKey != null) { BusKey = "g:" + _titleKey; return; }
            if (_busObject == null) return;
            // The park spawner parents and stamps NetScopes after Instantiate;
            // a state opened in that same frame waits one frame for it.
            if (Application.isPlaying && Time.frameCount <= _openFrame) return;
            if (_busObject.TryGetComponent(out NetId nid))
            {
                if (!nid.IsRegistered) return;
                BusKey = "o:" + nid.Id.ToString(CultureInfo.InvariantCulture) + SuffixFor(Title);
            }
            else BusKey = "o:" + NetId.HashPath(_busObject.transform).ToString(CultureInfo.InvariantCulture) + SuffixFor(Title);
            NetLog.V($"[SharedState] '{Name}' bus {BusKey}");
        }

        /// <summary>Stop holding this state (tell peers) and forget it.</summary>
        public void Dispose()
        {
            if (Disposed) return;
            SendLeave();
            Disposed = true;
            Engine.States.Remove(this);
            if (_registryKey != null && _registry.TryGetValue(_registryKey, out var cur) && cur == this)
                _registry.Remove(_registryKey);
            _byHandle.Remove(Handle);
        }

        // ── Read ─────────────────────────────────────────────────────

        /// <summary>Plain keys and merge cells.</summary>
        public IEnumerable<string> Keys
        {
            get
            {
                foreach (var k in _values.Keys) yield return k;
                foreach (var kv in _merge) if (Aggregate(kv.Key, kv.Value) != null) yield return kv.Key;
            }
        }
        public int Count => _values.Count;
        public bool Has(string key) => key != null && (_values.ContainsKey(key) || (_merge.TryGetValue(key, out var c) && Aggregate(key, c) != null));

        /// <summary>Canonical JSON of the value, or null.</summary>
        public string GetJson(string key)
        {
            if (key == null) return null;
            if (_values.TryGetValue(key, out var j)) return j;
            return _merge.TryGetValue(key, out var c) ? Aggregate(key, c) : null;
        }

        public double GetNumber(string key, double fallback = 0)
        {
            var j = Parse(GetJson(key));
            return j != null && j.type == JSONObject.Type.Number ? j.doubleValue : fallback;
        }

        public string GetString(string key, string fallback = null)
        {
            var j = Parse(GetJson(key));
            return j != null && j.type == JSONObject.Type.String ? j.stringValue : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            var j = Parse(GetJson(key));
            return j != null && j.type == JSONObject.Type.Bool ? j.boolValue : fallback;
        }

        /// <summary>A stored point in THIS device's world (attraction-relative on the wire).</summary>
        public bool TryGetVector3(string key, out Vector3 world)
        {
            world = default;
            var j = Parse(GetJson(key));
            if (j == null || j.type != JSONObject.Type.Object) return false;
            var a = j.GetField("$a");
            if (a != null && Frame != null && TryVec(a, out var local)) { world = Frame.TransformPoint(local); return true; }
            var p = j.GetField("$p");
            if (p != null && TryVec(p, out var park)) { world = ParkRoot.FromPark(park); return true; }
            return false;
        }

        static bool TryVec(JSONObject arr, out Vector3 v)
        {
            v = default;
            if (arr == null || arr.type != JSONObject.Type.Array || arr.list == null || arr.list.Count < 3) return false;
            v = new Vector3((float)arr.list[0].doubleValue, (float)arr.list[1].doubleValue, (float)arr.list[2].doubleValue);
            return true;
        }

        static JSONObject Parse(string json) => ParseFragment(json);

        /// <summary>
        /// Parse one JSON value (number, string, bool, object, array). Wrapped in an
        /// object because JSONObject cannot parse a bare one-character value ("0").
        /// Returns null for invalid input.
        /// </summary>
        public static JSONObject ParseFragment(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return new JSONObject("{\"v\":" + json + "}").GetField("v"); } catch (Exception) { return null; }
        }

        // ── Write ────────────────────────────────────────────────────

        public bool SetNumber(string key, double v) => Set(key, NumJson(v));
        public bool SetString(string key, string v) => Set(key, v == null ? "null" : "\"" + JsonEscape(v) + "\"");
        public bool SetBool(string key, bool v) => Set(key, v ? "true" : "false");

        /// <summary>Store a world point; it travels attraction-relative (and park-local for tools).</summary>
        public bool SetVector3(string key, Vector3 world)
        {
            var ic = CultureInfo.InvariantCulture;
            Vector3 park = ParkRoot.ToPark(world);
            var sb = new StringBuilder(96);
            sb.Append("{\"$p\":[").Append(park.x.ToString("F3", ic)).Append(',').Append(park.y.ToString("F3", ic)).Append(',').Append(park.z.ToString("F3", ic)).Append(']');
            if (Frame != null)
            {
                Vector3 a = Frame.InverseTransformPoint(world);
                sb.Append(",\"$a\":[").Append(a.x.ToString("F3", ic)).Append(',').Append(a.y.ToString("F3", ic)).Append(',').Append(a.z.ToString("F3", ic)).Append(']');
            }
            sb.Append('}');
            return Set(key, sb.ToString());
        }

        public bool Delete(string key) => Set(key, null);

        /// <summary>
        /// Write one key (JSON fragment; null or "null" deletes). The leader
        /// applies it now; anyone else sends it to the leader. Before Synced it
        /// is held (see the header). Returns false when the write is invalid.
        /// </summary>
        public bool Set(string key, string json)
        {
            if (Disposed) return false;
            if (!ValidKey(key)) return false;
            if (_merge.ContainsKey(key))
            {
                Warn($"[SharedState] '{Name}.{key}' is a merge cell; use add / union / mine / max on it.");
                return false;
            }
            string canon = Canonical(json, key);
            if (canon == Invalid) return false;
            var batch = new List<KeyValuePair<string, string>>(1) { new KeyValuePair<string, string>(key, canon) };
            Write(batch);
            return true;
        }

        const string Invalid = "\u0000invalid";

        static bool ValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key == "netId")
            {
                Warn($"[SharedState] invalid key '{key}' (1–{MaxKeyLength} chars; 'netId' is reserved by the relay).");
                return false;
            }
            for (int i = 0; i < key.Length; i++)
                if (char.IsControl(key[i])) { Warn("[SharedState] key has control characters."); return false; }
            return true;
        }

        /// <summary>Canonical form of a value (JSONObject.Print), null for delete, <see cref="Invalid"/> on error.</summary>
        static string Canonical(string json, string key)
        {
            if (json == null) return null;
            string t = json.Trim();
            if (t.Length == 0 || t == "null") return null;
            if (t.Length > MaxValueChars)
            {
                Warn($"[SharedState] value for '{key}' is {t.Length} chars (max {MaxValueChars}) — keep shared state small.");
                return Invalid;
            }
            if (t.IndexOf("\"netId\"", StringComparison.Ordinal) >= 0)
            {
                Warn($"[SharedState] value for '{key}' contains a 'netId' field, which the relay reserves.");
                return Invalid;
            }
            JSONObject j = ParseFragment(t);
            if (j == null || j.type == JSONObject.Type.Null)
            {
                Warn($"[SharedState] value for '{key}' is not valid JSON: {t}");
                return Invalid;
            }
            return j.Print();
        }

        void Write(List<KeyValuePair<string, string>> batch)
        {
            if (!Synced)
            {
                _queuedWrites.AddRange(batch);
                if (_queuedWrites.Count > 256) _queuedWrites.RemoveRange(0, _queuedWrites.Count - 256);
                return;
            }
            if (IsLeader) ApplyWrites(batch, LocalId);
            else SendWrites(batch);
        }

        /// <summary>
        /// Ask the leader to do something (it decides): on the leader this runs
        /// its <see cref="Requested"/> handlers right away, elsewhere it is sent.
        /// </summary>
        public void Request(string kind, string payloadJson)
        {
            if (Disposed || string.IsNullOrEmpty(kind)) return;
            string p = string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson.Trim();
            if (p.IndexOf("\"netId\"", StringComparison.Ordinal) >= 0)
            {
                Warn("[SharedState] request payload contains a 'netId' field, which the relay reserves.");
                return;
            }
            if (!Synced) { if (_queuedRequests.Count < 64) _queuedRequests.Add((kind, p)); return; }
            if (IsLeader) RaiseRequested(kind, p, LocalId);
            else SendRequestKind(kind, p);
        }

        void RaiseRequested(string kind, string json, string from)
        {
            try { Requested?.Invoke(kind, json, from); }
            catch (Exception e) { Warn($"[SharedState] '{Name}' request handler threw: {e.Message}"); }
        }

        void ApplyWrites(List<KeyValuePair<string, string>> batch, string writer)
        {
            List<string> changed = null;
            for (int i = 0; i < batch.Count; i++)
            {
                string k = batch[i].Key, v = batch[i].Value;
                if (v == null)
                {
                    if (_values.Remove(k)) (changed ??= new List<string>()).Add(k);
                }
                else if (!_values.TryGetValue(k, out var old) || old != v)
                {
                    _values[k] = v;
                    (changed ??= new List<string>()).Add(k);
                }
            }
            if (changed == null) return;
            Rev++;
            Writer = LocalId;
            Version++;
            _dirty = true;
            for (int i = 0; i < changed.Count; i++) RaiseChanged(changed[i]);
        }

        void RaiseChanged(string key)
        {
            string v = GetJson(key);
            try { Changed?.Invoke(key, v); }
            catch (Exception e) { Warn($"[SharedState] '{Name}' change handler threw: {e.Message}"); }
        }

        // ── Merge cells: shared without a leader round trip ──────────
        //
        // The register above covers phase / round / mode. Most games also keep
        // state that every player writes at once (audit, Oct 2026): per-player
        // damage and scores, claimed items, per-seat values. Those are merge
        // cells — conflict-free, so any device writes its own part directly,
        // every holder merges, and snapshots / heartbeats repair anyone who
        // missed a message:
        //   counter  Add(key, n)       each device grows its own cell; total = sum
        //   union    Union(key, items) grow-only set of strings
        //   owner    SetMine(key, v)   each device's own value (newest wins per device)
        //   max      Max(key, v)       largest value anyone offered
        // Clear(key) (leader only) starts a new epoch: older entries are dropped
        // everywhere, so a cell can be reused for the next round.
        // GetJson(key) on a cell returns its aggregate: counter → total, union →
        // sorted array, owner → {id: value}, max → number.
        sealed class Cell
        {
            public char kind;
            public int epoch;
            public readonly SortedDictionary<string, string> v = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public readonly SortedDictionary<string, double> q = new SortedDictionary<string, double>(StringComparer.Ordinal);
        }

        readonly SortedDictionary<string, Cell> _merge = new SortedDictionary<string, Cell>(StringComparer.Ordinal);
        bool _mergeBundleDue;
        double _lastMergeBundle = -9;

        public bool IsCell(string key) => key != null && _merge.ContainsKey(key);

        Cell CellFor(string key, char kind, bool create)
        {
            if (Disposed || !ValidKey(key)) return null;
            if (_values.ContainsKey(key))
            {
                Warn($"[SharedState] '{Name}.{key}' holds a plain value (set); it cannot also be a merge cell.");
                return null;
            }
            if (_merge.TryGetValue(key, out var c))
            {
                if (c.kind == kind) return c;
                Warn($"[SharedState] '{Name}.{key}' is a {KindName(c.kind)} cell, not a {KindName(kind)}.");
                return null;
            }
            if (!create) return null;
            c = new Cell { kind = kind };
            _merge[key] = c;
            return c;
        }

        static string KindName(char k) => k == 'c' ? "counter" : k == 'u' ? "union" : k == 'o' ? "owner" : "max";
        static double ParseNum(string json) =>
            double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        // Never exponent notation: JSONObject cannot parse "1E+20".
        internal static string NumJson(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (Math.Abs(d) < 1e15 && d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        /// <summary>Grow this device's part of a counter by <paramref name="n"/> (≥ 0). Returns this device's part.</summary>
        public double Add(string key, double n)
        {
            var c = CellFor(key, 'c', true);
            if (c == null) return 0;
            double mine = c.v.TryGetValue(LocalId, out var j) ? ParseNum(j) : 0;
            if (!(n > 0)) return mine;
            mine += n;
            c.v[LocalId] = NumJson(mine);
            CellChanged(key, c, LocalId);
            return mine;
        }

        public double Total(string key)
        {
            if (!_merge.TryGetValue(key ?? "", out var c) || c.kind != 'c') return 0;
            double sum = 0;
            foreach (var kv in c.v) sum += ParseNum(kv.Value);
            return sum;
        }

        /// <summary>Per-device parts of a counter (or owner values as JSON parsed to numbers where possible).</summary>
        public Dictionary<string, double> Cells(string key)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            if (_merge.TryGetValue(key ?? "", out var c) && c.kind == 'c')
                foreach (var kv in c.v) d[kv.Key] = ParseNum(kv.Value);
            return d;
        }

        /// <summary>Add items to a grow-only set. Returns true if anything was new.</summary>
        public bool Union(string key, IEnumerable<string> items)
        {
            var c = CellFor(key, 'u', true);
            if (c == null || items == null) return false;
            bool any = false;
            List<string> added = null;
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it) || it.Length > MaxKeyLength || c.v.ContainsKey(it)) continue;
                c.v[it] = "1";
                (added ??= new List<string>()).Add(it);
                any = true;
            }
            if (any) CellChanged(key, c, added.ToArray());
            return any;
        }

        public bool Contains(string key, string item) =>
            item != null && _merge.TryGetValue(key ?? "", out var c) && c.kind == 'u' && c.v.ContainsKey(item);

        public List<string> Items(string key)
        {
            var l = new List<string>();
            if (_merge.TryGetValue(key ?? "", out var c) && c.kind == 'u') l.AddRange(c.v.Keys);
            return l;
        }

        /// <summary>Set THIS device's value in an owner cell (any JSON fragment). Newest wins per device.</summary>
        public bool SetMine(string key, string json)
        {
            var c = CellFor(key, 'o', true);
            if (c == null) return false;
            string canon = Canonical(json, key);
            if (canon == Invalid) return false;
            canon ??= "null";
            double seq = Math.Max(Engine.Now, (c.q.TryGetValue(LocalId, out var old) ? old : 0) + 0.001);
            c.q[LocalId] = seq;
            c.v[LocalId] = canon;
            CellChanged(key, c, LocalId);
            return true;
        }

        /// <summary>A device's value in an owner cell (JSON), or null.</summary>
        public string MineOf(string key, string id) =>
            _merge.TryGetValue(key ?? "", out var c) && c.kind == 'o' && c.v.TryGetValue(id ?? "", out var j) && j != "null" ? j : null;

        /// <summary>Offer a value to a max cell. Returns the merged maximum.</summary>
        public double Max(string key, double value)
        {
            var c = CellFor(key, 'm', true);
            if (c == null) return 0;
            bool has = c.v.TryGetValue("v", out var j);
            double cur = has ? ParseNum(j) : double.NegativeInfinity;
            if (!double.IsNaN(value) && value > cur)
            {
                c.v["v"] = NumJson(value);
                CellChanged(key, c, "v");
                return value;
            }
            return has ? cur : value;
        }

        /// <summary>Leader only: empty a merge cell everywhere (new epoch).</summary>
        public bool Clear(string key)
        {
            if (!_merge.TryGetValue(key ?? "", out var c)) return false;
            if (!IsLeader || !Synced)
            {
                Warn($"[SharedState] '{Name}.{key}': only the leader clears a merge cell — send s.request(kind) and let the leader decide.");
                return false;
            }
            c.epoch++;
            c.v.Clear();
            c.q.Clear();
            CellChanged(key, c, null);
            return true;
        }

        string Aggregate(string key, Cell c)
        {
            switch (c.kind)
            {
                case 'c': return NumJson(Total(key));
                case 'm': return c.v.TryGetValue("v", out var m) ? m : null;
                case 'u':
                {
                    if (c.v.Count == 0) return null;
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (var k in c.v.Keys) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(JsonEscape(k)).Append('"'); }
                    return sb.Append(']').ToString();
                }
                case 'o':
                {
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (var kv in c.v)
                    {
                        if (kv.Value == "null") continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":").Append(kv.Value);
                    }
                    return first ? null : sb.Append('}').ToString();
                }
            }
            return null;
        }

        /// <summary>A cell on the wire; <paramref name="only"/> limits it to some entries (null = all).</summary>
        static void CellWire(StringBuilder sb, Cell c, string[] only)
        {
            sb.Append("{\"t\":\"").Append(c.kind).Append("\",\"e\":").Append(c.epoch).Append(",\"v\":{");
            bool first = true;
            foreach (var kv in c.v)
            {
                if (only != null && Array.IndexOf(only, kv.Key) < 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":").Append(kv.Value);
            }
            sb.Append('}');
            if (c.kind == 'o')
            {
                sb.Append(",\"q\":{");
                first = true;
                foreach (var kv in c.q)
                {
                    if (only != null && Array.IndexOf(only, kv.Key) < 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":").Append(NumJson(kv.Value));
                }
                sb.Append('}');
            }
            sb.Append('}');
        }

        void AppendCells(StringBuilder sb)
        {
            sb.Append(",\"m\":{");
            bool first = true;
            foreach (var kv in _merge)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":");
                CellWire(sb, kv.Value, null);
            }
            sb.Append('}');
        }

        void CellChanged(string key, Cell c, params string[] entries)
        {
            Version++;
            RaiseChanged(key);
            if (!Synced) return;   // contributions made before sync go out in one bundle at sync
            var sb = new StringBuilder(96);
            sb.Append(",\"m\":{\"").Append(JsonEscape(key)).Append("\":");
            // A cleared cell (new epoch) goes out whole; a write sends its own entries.
            CellWire(sb, c, entries != null && entries.Length > 0 && entries[0] != null ? entries : null);
            sb.Append('}');
            Engine.Budget.TryTake(1, critical: true);
            Send("mg", sb.ToString());
        }

        /// <summary>Merge every cell in <paramref name="m"/>; raises Changed for each cell that moved.</summary>
        void MergeCells(JSONObject m)
        {
            if (m == null || m.type != JSONObject.Type.Object || m.keys == null) return;
            for (int i = 0; i < m.keys.Count; i++)
            {
                string key = m.keys[i];
                var cw = m.list[i];
                if (cw == null || cw.type != JSONObject.Type.Object || !ValidKey(key) || _values.ContainsKey(key)) continue;
                string t = cw.GetField("t")?.stringValue;
                if (string.IsNullOrEmpty(t)) continue;
                char kind = t[0];
                if (kind != 'c' && kind != 'u' && kind != 'o' && kind != 'm') continue;
                if (!_merge.TryGetValue(key, out var c)) { c = new Cell { kind = kind }; _merge[key] = c; }
                if (c.kind != kind) continue;
                int e = (int)SessionEngine.Num(cw, "e");
                if (e < c.epoch) continue;
                bool changed = false;
                if (e > c.epoch)
                {
                    c.epoch = e;
                    if (c.v.Count > 0) { c.v.Clear(); c.q.Clear(); changed = true; }
                }
                var v = cw.GetField("v");
                var q = cw.GetField("q");
                if (v != null && v.type == JSONObject.Type.Object && v.keys != null)
                    for (int k = 0; k < v.keys.Count; k++)
                    {
                        string ek = v.keys[k];
                        var ev = v.list[k];
                        if (ev == null) continue;
                        switch (kind)
                        {
                            case 'c':
                            case 'm':
                            {
                                if (ev.type != JSONObject.Type.Number) break;
                                double nv = ev.doubleValue;
                                if (!c.v.TryGetValue(ek, out var cur) || ParseNum(cur) < nv) { c.v[ek] = NumJson(nv); changed = true; }
                                break;
                            }
                            case 'u':
                                if (!c.v.ContainsKey(ek)) { c.v[ek] = "1"; changed = true; }
                                break;
                            case 'o':
                            {
                                double qs = q != null ? SessionEngine.Num(q, ek) : 0;
                                if (!c.q.TryGetValue(ek, out var cq) || qs > cq) { c.q[ek] = qs; c.v[ek] = ev.Print(); changed = true; }
                                break;
                            }
                        }
                    }
                if (changed) { Version++; RaiseChanged(key); }
            }
        }

        /// <summary>FNV digest of every cell — heartbeats compare it to find a device that missed a merge.</summary>
        string MergeDigest()
        {
            if (_merge.Count == 0) return "0";
            var sb = new StringBuilder();
            AppendCells(sb);
            uint h = 2166136261u;
            string str = sb.ToString();
            for (int i = 0; i < str.Length; i++) { h ^= str[i]; h *= 16777619u; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        void SendMergeBundle()
        {
            if (_merge.Count == 0) return;
            if (!Engine.Budget.TryTake(1, critical: true)) return;
            var sb = new StringBuilder(256);
            AppendCells(sb);
            _lastMergeBundle = Engine.LocalTime;
            _mergeBundleDue = false;
            Send("mg", sb.ToString());
        }

        // ── Leader ───────────────────────────────────────────────────

        string LocalId => Engine.LocalId;

        readonly List<SessionEngine.Candidate> _cands = new List<SessionEngine.Candidate>();

        /// <summary>
        /// The writer: among live devices HOLDING this state (this one
        /// included), the session's order — headsets first, synced-to-this-state
        /// first, then seniority (the incumbent keeps writing until it leaves),
        /// then id. See SessionEngine.Compare.
        /// </summary>
        public string LeaderId
        {
            get
            {
                // Holding a solo table while the merge settles: still our own game.
                if (Holding) return LocalId;
                _cands.Clear();
                _cands.Add(new SessionEngine.Candidate(LocalId, Engine.IsPhoneSelf, Synced, Engine.JoinedAt));
                double now = Engine.LocalTime;
                bool connected = Engine.Link != null && Engine.Link.Connected;
                if (connected)
                    foreach (var kv in _members)
                    {
                        if (now - kv.Value.heard > MemberTimeout || !Engine.IsLive(kv.Key)) continue;
                        _cands.Add(new SessionEngine.Candidate(kv.Key, Engine.IsPhone(kv.Key), kv.Value.synced, Engine.JoinedAtOf(kv.Key)));
                    }
                return SessionEngine.PickLeader(_cands);
            }
        }

        /// <summary>Every live session peer running this title has announced itself on this bus.</summary>
        bool AllTitlePeersAreMembers()
        {
            double now = Engine.LocalTime;
            foreach (var id in Engine.PeerIds())
            {
                if (!Engine.HasTitle(id, Title)) continue;
                if (!_members.TryGetValue(id, out var m) || now - m.heard > MemberTimeout) return false;
            }
            return true;
        }

        public bool IsLeader => LeaderId == LocalId;

        /// <summary>Other devices currently holding this state.</summary>
        public List<string> Members()
        {
            var l = new List<string>();
            double now = Engine.LocalTime;
            foreach (var kv in _members) if (now - kv.Value.heard <= MemberTimeout && Engine.IsLive(kv.Key)) l.Add(kv.Key);
            l.Sort(string.CompareOrdinal);
            return l;
        }

        bool AnySyncedMember()
        {
            double now = Engine.LocalTime;
            foreach (var kv in _members)
                if (kv.Value.synced && now - kv.Value.heard <= MemberTimeout && Engine.IsLive(kv.Key)) return true;
            return false;
        }

        internal void ForgetMember(string id) => _members.Remove(id);

        // ── Tick ─────────────────────────────────────────────────────

        internal void Tick()
        {
            if (Disposed) return;
            if (_hadBusObject && _busObject == null) { Dispose(); return; }
            if (BusKey == null) { TryResolveBus(); if (BusKey == null) return; }

            double now = Engine.LocalTime;
            var link = Engine.Link;
            bool present = link != null && link.Present;
            bool connected = link != null && link.Connected;

            FollowMerge();
            if (connected && !_wasConnected)
            {
                _wasConnected = true;
                // Synced alone; the room we just joined is authoritative —
                // unless it is our own empty one (we host, nobody else is here),
                // or the session is still settling whose timeline survives
                // (Oct 11 2026: hold, then FollowMerge keeps or re-asks).
                if (_syncedSolo && Engine.RoomKnownEmpty) _syncedSolo = false;
                else if (_syncedSolo && Engine.Merging) _mergeHold = true;
                else if (_syncedSolo) { Synced = false; _syncedSolo = false; Rev = 0; Writer = ""; }
                _requestedAt = -1;
                _firstRequestAt = -1;
                _rqPending = Synced && !_mergeHold;   // a synced holder still re-asks: the room may be ahead
            }
            else if (!connected && _wasConnected)
            {
                _wasConnected = false;
                _members.Clear();
                _mergeHold = false;
            }

            // Holding our solo table: no traffic, nothing to decide yet.
            if (Holding) return;

            if (!Synced)
            {
                if (!present)
                {
                    if (now - _openedAt >= Engine.SoloGraceNoClient) FinishSync(false, solo: true);
                }
                else if (!connected)
                {
                    if (now - _openedAt >= Engine.SoloGraceDisconnected || Engine.RoomKnownEmpty) FinishSync(false, solo: true);
                }
                else if (!Engine.Synced)
                {
                    // Wait until the session knows the room (who is here,
                    // who is senior) before deciding anything about it.
                }
                else if (Engine.RoomKnownEmpty)
                {
                    // Our own relay with nobody else on it: nobody holds this
                    // state, so the room is empty by fact, not by silence.
                    FinishSync(false, solo: false);
                }
                else if (_requestedAt < 0)
                {
                    if (SendSyncRequest())
                    {
                        _requestedAt = now;
                        if (_firstRequestAt < 0) _firstRequestAt = now;
                    }
                }
                else
                {
                    // The empty-room rule, which a late joiner cannot slip
                    // through ("1.5 s of silence" could: a lost request let the
                    // newcomer start a second encounter — WizardsWay's 6 s valve,
                    // Birthday Bash's self-wake):
                    //  • a synced holder is known → keep asking until its
                    //    snapshot lands; never start on our own;
                    //  • every live peer running this title has announced itself
                    //    here and none is synced → the first of them (session
                    //    order) starts the room, the others take its snapshot;
                    //  • otherwise wait out EmptyRoomTimeout of silence from
                    //    every holder (each heartbeats every ~3 s, so silence
                    //    that long means nobody holds this state).
                    double waited = now - _requestedAt;
                    if (AnySyncedMember())
                    {
                        if (waited >= Engine.SilenceWindow) _requestedAt = -1;
                    }
                    else if (AllTitlePeersAreMembers() && LeaderId == LocalId)
                    {
                        if (waited >= Engine.SilenceWindow) FinishSync(false, solo: false);
                    }
                    else if (now - _firstRequestAt >= EmptyRoomTimeout) FinishSync(false, solo: false);
                    else if (waited >= Engine.SilenceWindow) _requestedAt = -1;
                }
            }
            else if (connected && _rqPending && now - _lastRq >= 1.0)
            {
                if (SendSyncRequest()) _rqPending = false;
            }

            if (_answerAt >= 0 && now >= _answerAt)
            {
                _answerAt = -1;
                if (Synced && Rev > _answerFor) SendSnapshot();
            }

            if (_dirty && connected && now - _lastSn >= FlushInterval) SendSnapshot();
            if (_mergeBundleDue && Synced && connected && now - _lastMergeBundle >= 2.0) SendMergeBundle();

            if (connected && now >= _nextHb)
            {
                if (Engine.Budget.TryTake(NetBodyPriority.Low))
                {
                    Send("hb", ",\"r\":" + Rev + ",\"w\":\"" + SessionEngine.Esc(Writer) + "\",\"mh\":\"" + MergeDigest() + "\"");
                    _nextHb = now + HeartbeatInterval * (0.85 + 0.3 * Jitter(LocalId + Name + now.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        void FinishSync(bool fromSnapshot, bool solo)
        {
            if (Synced) return;
            Synced = true;
            _syncedSolo = solo;
            // Lost a merge, and nobody in the room held anything: our table
            // (kept aside) seeds it — and counts as the room's state, so writes
            // made in the blip (a start() re-init) never reset it (Oct 11 2026).
            if (!fromSnapshot && RestoreStash()) { fromSnapshot = true; _dirty = true; }
            _stash = null;
            // Started an empty room with others waiting on it: they need our
            // (empty) snapshot to sync.
            if (!fromSnapshot && !solo && IsLeader && _members.Count > 0) _dirty = true;
            // Our own merge contributions (made before sync) go out now.
            if (_merge.Count > 0) _mergeBundleDue = true;
            NetLog.V($"[SharedState] '{Name}' synced ({(fromSnapshot ? "snapshot rev " + Rev : solo ? "solo" : "empty room")})");

            if (_queuedWrites.Count > 0)
            {
                if (fromSnapshot)
                {
                    if (!_warnedDrop)
                    {
                        _warnedDrop = true;
                        Warn($"[SharedState] '{Name}': dropped {_queuedWrites.Count} write(s) made before this device synced — " +
                                         "the room already had state, and a late joiner must not reset it. Gate starts on s.synced() " +
                                         "or use s.request(kind) and let the leader decide.");
                    }
                }
                else Write(new List<KeyValuePair<string, string>>(_queuedWrites));
                _queuedWrites.Clear();
            }

            if (_queuedRequests.Count > 0)
            {
                var reqs = new List<(string, string)>(_queuedRequests);
                _queuedRequests.Clear();
                foreach (var r in reqs) Request(r.Item1, r.Item2);
            }

            if (_queuedIncoming.Count > 0)
            {
                var inc = new List<(string, JSONObject)>(_queuedIncoming);
                _queuedIncoming.Clear();
                if (IsLeader) foreach (var x in inc) HandleLeaderMessage(x.Item1, x.Item2);
            }
        }

        // ── Send ─────────────────────────────────────────────────────

        void Send(string kind, string tail)
        {
            var link = Engine.Link;
            if (link == null || !link.Connected || BusKey == null) return;
            if (Holding) return;   // merge still settling: the room hears nothing from a solo table
            var sb = new StringBuilder(64 + (tail?.Length ?? 0));
            sb.Append("{\"b\":\"").Append(SessionEngine.Esc(BusKey)).Append("\",\"n\":\"").Append(SessionEngine.Esc(Name))
              .Append("\",\"k\":\"").Append(kind).Append("\",\"u\":\"").Append(SessionEngine.Esc(LocalId))
              .Append("\",\"sy\":").Append(Synced ? 1 : 0);
            if (!string.IsNullOrEmpty(tail)) sb.Append(tail);
            sb.Append('}');
            link.Publish(SessionEngine.TypeState, sb.ToString());
        }

        bool SendSyncRequest()
        {
            if (!Engine.Budget.TryTake(1, critical: true)) return false;
            _lastRq = Engine.LocalTime;
            Send("rq", ",\"r\":" + Rev);
            return true;
        }

        bool SendSnapshot()
        {
            if (!Engine.Budget.TryTake(1, critical: true)) return false;
            var sb = new StringBuilder(256);
            sb.Append(",\"r\":").Append(Rev).Append(",\"w\":\"").Append(SessionEngine.Esc(Writer)).Append("\",\"d\":{");
            bool first = true;
            foreach (var kv in _values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":").Append(kv.Value);
            }
            sb.Append('}');
            AppendCells(sb);
            if (sb.Length > MaxSnapshotChars)
            {
                Warn($"[SharedState] '{Name}' snapshot is {sb.Length} chars (max {MaxSnapshotChars}); not sent. Keep shared state small — it is a snapshot, not a store.");
                _dirty = false;
                return false;
            }
            Send("sn", sb.ToString());
            _dirty = false;
            _lastSn = Engine.LocalTime;
            _answerAt = -1;
            return true;
        }

        void SendWrites(List<KeyValuePair<string, string>> batch)
        {
            var sb = new StringBuilder(128);
            sb.Append(",\"kv\":{");
            bool first = true;
            var dels = new List<string>();
            foreach (var kv in batch)
            {
                if (kv.Value == null) { dels.Add(kv.Key); continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(JsonEscape(kv.Key)).Append("\":").Append(kv.Value);
            }
            sb.Append("},\"x\":[");
            for (int i = 0; i < dels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(JsonEscape(dels[i])).Append('"');
            }
            sb.Append(']');
            if (!Engine.Budget.TryTake(1, critical: true))
                Warn($"[SharedState] '{Name}': send budget exhausted; write request sent anyway (it must arrive).");
            Send("ws", sb.ToString());
        }

        void SendRequestKind(string kind, string payload)
        {
            Engine.Budget.TryTake(1, critical: true);
            Send("rk", ",\"q\":\"" + JsonEscape(kind) + "\",\"p\":" + payload);
        }

        internal void SendLeave()
        {
            if (Disposed || BusKey == null) return;
            var link = Engine.Link;
            if (link == null || !link.Connected) return;
            Send("lv", null);
        }

        // ── Receive ──────────────────────────────────────────────────

        internal void Receive(string from, JSONObject p)
        {
            if (Disposed) return;
            FollowMerge();
            double now = Engine.LocalTime;
            string kind = p.GetField("k")?.stringValue;
            if (kind == "lv") { _members.Remove(from); return; }
            if (!_members.TryGetValue(from, out var m)) _members[from] = m = new Member();
            m.heard = now;
            m.synced = SessionEngine.Num(p, "sy") >= 1;
            // Holding our solo table while the merge settles: the room's
            // snapshots / repairs / requests must not touch it yet (merge cells
            // are conflict-free and still merge).
            if (Holding && kind != "mg") return;

            switch (kind)
            {
                case "rq":
                {
                    if (!Synced) return;
                    int r = (int)SessionEngine.Num(p, "r");
                    if (IsLeader) SendSnapshot();
                    else if (Rev > r && _answerAt < 0)
                    {
                        // Redundant custody: answer only if the leader has not
                        // within a staggered delay (it may have just left).
                        _answerFor = r;
                        _answerAt = now + 0.35 + 0.1 * (Jitter(LocalId) * 6);
                    }
                    return;
                }
                case "sn":
                    ApplySnapshot(p);
                    return;
                case "mg":
                    MergeCells(p.GetField("m"));
                    return;
                case "hb":
                {
                    string mh = p.GetField("mh")?.stringValue;
                    if (Synced && mh != null && _merge.Count > 0 && mh != MergeDigest()) _mergeBundleDue = true;
                    int r = (int)SessionEngine.Num(p, "r");
                    string w = p.GetField("w")?.stringValue ?? "";
                    if (Newer(r, w))
                    {
                        if (Synced) _rqPending = true;   // we fell behind: ask
                    }
                    else if (Synced && IsLeader && (r < Rev || (r == Rev && w != Writer)) && now - _lastHbAnswer >= 1.0)
                    {
                        _lastHbAnswer = now;
                        SendSnapshot();                  // they fell behind: repair
                    }
                    return;
                }
                case "ws":
                case "rk":
                    if (!IsLeader) return;
                    if (!Synced) { if (_queuedIncoming.Count < 64) _queuedIncoming.Add((from, p)); return; }
                    HandleLeaderMessage(from, p);
                    return;
            }
        }

        bool Newer(int r, string w) => r > Rev || (r == Rev && string.CompareOrdinal(w ?? "", Writer ?? "") > 0);

        void HandleLeaderMessage(string from, JSONObject p)
        {
            string kind = p.GetField("k")?.stringValue;
            if (kind == "ws")
            {
                var batch = new List<KeyValuePair<string, string>>();
                var kv = p.GetField("kv");
                if (kv != null && kv.type == JSONObject.Type.Object && kv.keys != null)
                    for (int i = 0; i < kv.keys.Count; i++)
                    {
                        string k = kv.keys[i];
                        if (!ValidKey(k)) continue;
                        var v = kv.list[i];
                        batch.Add(new KeyValuePair<string, string>(k, v == null || v.type == JSONObject.Type.Null ? null : v.Print()));
                    }
                var x = p.GetField("x");
                if (x != null && x.type == JSONObject.Type.Array && x.list != null)
                    foreach (var d in x.list)
                        if (d != null && d.type == JSONObject.Type.String && ValidKey(d.stringValue))
                            batch.Add(new KeyValuePair<string, string>(d.stringValue, null));
                if (batch.Count > 0) ApplyWrites(batch, from);
            }
            else if (kind == "rk")
            {
                string q = p.GetField("q")?.stringValue;
                if (string.IsNullOrEmpty(q)) return;
                var pl = p.GetField("p");
                RaiseRequested(q, pl != null ? pl.Print() : "{}", from);
            }
        }

        void ApplySnapshot(JSONObject p)
        {
            int r = (int)SessionEngine.Num(p, "r");
            string w = p.GetField("w")?.stringValue ?? "";
            if (_answerAt >= 0 && r >= Rev) _answerAt = -1;   // someone answered for us
            var cells = p.GetField("m");
            bool roomHasCells = cells != null && cells.keys != null && cells.keys.Count > 0;
            MergeCells(cells);   // merge cells are conflict-free: always merged

            bool accept = Synced ? Newer(r, w) : (r >= Rev || Newer(r, w));
            if (!accept) return;

            var d = p.GetField("d");
            var next = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (d != null && d.type == JSONObject.Type.Object && d.keys != null)
                for (int i = 0; i < d.keys.Count; i++)
                {
                    var v = d.list[i];
                    if (v == null || v.type == JSONObject.Type.Null) continue;
                    next[d.keys[i]] = v.Print();
                }

            // Lost a merge to a room that holds nothing (rev 0, empty): keep our
            // table and seed the room with it (FinishSync restores + broadcasts).
            if (!Synced && _stash != null && r == 0 && next.Count == 0)
            {
                FinishSync(fromSnapshot: false, solo: false);
                return;
            }

            var changed = new List<string>();
            foreach (var kv in next)
                if (!_values.TryGetValue(kv.Key, out var old) || old != kv.Value) changed.Add(kv.Key);
            foreach (var k in _values.Keys)
                if (!next.ContainsKey(k)) changed.Add(k);

            _values.Clear();
            foreach (var kv in next) _values[kv.Key] = kv.Value;
            Rev = r;
            Writer = w;
            _dirty = false;   // the table we would have sent is superseded
            if (changed.Count > 0) Version++;

            if (!Synced) FinishSync(fromSnapshot: r > 0 || next.Count > 0 || roomHasCells, solo: false);
            for (int i = 0; i < changed.Count; i++) RaiseChanged(changed[i]);
        }

        internal static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
