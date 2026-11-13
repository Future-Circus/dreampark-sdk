// ─────────────────────────────────────────────────────────────────────
//  PlaylistAnnounce.cs — objects announce what they are (PLAYLIST-PLAN § 4.2; pl-announce, Oct 21 2026).
//
//    dp.announce(kind, { item = 'coin' })            "I'm here and I'm a collect / defeat / break / target / reach /
//                                                     find / protect / catch thing."
//    dp.announce(kind, { holds = { collect = 1 } })  "I hold 1 collectible": counted in the total from the start, marked.
//    dp.announce(kind, { from = <source> })          "I came out of that source": the count moves from the source to
//                                                     this object — the total never changes, nothing counts twice.
//    dp.announce_done({ by = player })               collected / defeated / broken. Destroyed or switched off = done.
//    Announce component                              the same for prefabs with no Lua (kind, item, holds).
//
//  THE LIVE LIST (this file, engine-free): one per attraction, shared through three UNION cells of a SharedState
//  ("dp.announce" on the attraction's bus), so every device — a late joiner too — derives the same total / remaining
//  / by-player from the same sets, and any device writes its own part without a leader round trip:
//    a   announced   "<hash>.<gen>|<kind code>|<holds>|<from hash.gen>"
//    d   done        "<hash>.<gen>"
//    b   done by     "<hash>.<gen>|<player>"        (several claims → the lowest player id is credited, on every device)
//  hash = FNV-1a of the object's stable key (its path under the attraction root — the same on every device and after a
//  reset, since a reset hides and restores objects instead of destroying them); a hand-off child's key is
//  "<source key>@<source gen>#<n>" (the n-th thing that source released here).
//  gen = the object's GENERATION: a game reset re-announces an object that was done (or that released what it held), and
//  the re-announce starts generation gen+1 — the sets are grow-only, older generations simply stop counting. A device
//  that already sees a newer generation adopts it (two headsets resetting together bump once). A late joiner's FIRST
//  announce never bumps: it joins whatever generation the room is on.
//  Counting (per kind K, current generations only):
//    a plain object of kind K                  total +1, remaining +1 until done
//    a holder holding h of K with n children   total + max(h, n), remaining + max(0, h − n) + its children not done
//                                              (a holder of kind K that holds K counts only as what it holds)
//    a child whose source is a stale generation (a reset cleaned it up) does not count.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DreamPark
{
    /// The eight announce kinds (§ 4.2).
    public enum AnnounceKind { Collect = 0, Defeat = 1, Break = 2, Target = 3, Reach = 4, Find = 5, Protect = 6, Catch = 7, Waypoint = 8 }

    public static class AnnounceKinds
    {
        public static readonly string[] All = { "collect", "defeat", "break", "target", "reach", "find", "protect", "catch", "waypoint" };
        static readonly char[] Codes = { 'c', 'd', 'b', 't', 'r', 'f', 'p', 'k', 'w' };
        /// PLAYLIST-PLAN § 6.7: the map's done / not-done places. Every attraction announces one by default (its root, key
        /// <see cref="PlatformWaypointKey"/>); a game may add its own (dp.announce('waypoint', {label})).
        public const string Waypoint = "waypoint";
        /// The key of the platform's own waypoint for an attraction (its root object).
        public const string PlatformWaypointKey = ".";

        /// The lowercase kind name, or null when it is not one of the eight.
        public static string Normalize(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            string k = kind.Trim().ToLowerInvariant();
            return Array.IndexOf(All, k) >= 0 ? k : null;
        }

        public static string Name(AnnounceKind k) => All[(int)k];
        public static char Code(string kind) { int i = Array.IndexOf(All, kind); return i >= 0 ? Codes[i] : '?'; }
        public static string FromCode(char c) { int i = Array.IndexOf(Codes, c); return i >= 0 ? All[i] : null; }
    }

    /// stop.count{kind}: the live total, what is left, what is done and by whom.
    public sealed class PlaylistAnnounceCount
    {
        public int total, remaining, done;
        public readonly SortedDictionary<string, int> by = new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void Add(PlaylistAnnounceCount o)
        {
            if (o == null) return;
            total += o.total; remaining += o.remaining; done += o.done;
            foreach (var kv in o.by) by[kv.Key] = (by.TryGetValue(kv.Key, out int n) ? n : 0) + kv.Value;
        }

        public string ToJson()
        {
            var sb = new StringBuilder("{\"total\":").Append(total).Append(",\"remaining\":").Append(remaining)
                .Append(",\"done\":").Append(done).Append(",\"by\":{");
            bool first = true;
            foreach (var kv in by)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(SharedState.JsonEscape(kv.Key)).Append("\":").Append(kv.Value);
            }
            return sb.Append("}}").ToString();
        }

        public override string ToString() => $"{remaining}/{total} left (done {done})";
    }

    /// Where a list keeps its three sets: a SharedState's union cells (shared), or memory (no session).
    public interface IAnnounceStore
    {
        void Union(string cell, string item);
        List<string> Items(string cell);
        /// Bumped on every change (local or merged) — the list's parse cache key.
        int Version { get; }
        bool Disposed { get; }
    }

    public sealed class SharedAnnounceStore : IAnnounceStore
    {
        public readonly SharedState State;
        public SharedAnnounceStore(SharedState s) { State = s; }
        public void Union(string cell, string item) => State?.Union(cell, new[] { item });
        public List<string> Items(string cell) => State != null ? State.Items(cell) : new List<string>();
        public int Version => State != null ? State.Version : 0;
        public bool Disposed => State == null || State.Disposed;
    }

    public sealed class MemoryAnnounceStore : IAnnounceStore
    {
        readonly Dictionary<string, SortedSet<string>> _sets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        public int Version { get; private set; }
        public bool Disposed => false;
        public void Union(string cell, string item)
        {
            if (string.IsNullOrEmpty(item) || item.Length > SharedState.MaxKeyLength) return;
            if (!_sets.TryGetValue(cell, out var s)) _sets[cell] = s = new SortedSet<string>(StringComparer.Ordinal);
            if (s.Add(item)) Version++;
        }
        public List<string> Items(string cell) => _sets.TryGetValue(cell, out var s) ? new List<string>(s) : new List<string>();
    }

    /// One attraction's live list of announced objects (engine-free; the Unity glue is PlaylistAnnounce).
    public sealed class PlaylistAnnounceList
    {
        public const string CellAnnounced = "a", CellDone = "d", CellBy = "b";
        public static Action<string> Log = m => UnityEngine.Debug.Log(m);

        public readonly IAnnounceStore Store;
        /// The attraction root (Unity: its GameObject) — opaque here.
        public object Root;
        public string Name = "";

        public PlaylistAnnounceList(IAnnounceStore store) { Store = store ?? new MemoryAnnounceStore(); }

        sealed class Local
        {
            public string key, hash;
            public int gen;
            public string kind, item, holds = "", fromHg = "";
            public int order = -1;
            public int released;          // hand-offs out of this object at `gen` on THIS device (its children's #n)
            public object handle;         // the object (Unity: its GameObject), for markers
        }

        readonly Dictionary<string, Local> _local = new Dictionary<string, Local>(StringComparer.Ordinal);

        /// FNV-1a 32-bit as 8 hex (sal-net's object hash).
        public static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        // ── the shared sets, parsed once per store version ──────────────
        sealed class Entry { public string hash; public int gen; public string kind; public Dictionary<string, int> holds; public string from; public int order = int.MaxValue; }
        sealed class Parsed
        {
            public readonly Dictionary<string, int> cur = new Dictionary<string, int>(StringComparer.Ordinal);   // hash → newest gen
            public readonly Dictionary<string, Entry> at = new Dictionary<string, Entry>(StringComparer.Ordinal);  // "hash.gen" → entry
            public readonly HashSet<string> done = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> by = new Dictionary<string, string>(StringComparer.Ordinal); // "hash.gen" → player
            public readonly Dictionary<string, List<string>> kids = new Dictionary<string, List<string>>(StringComparer.Ordinal); // holder "h.g" → children "h.g"
        }
        Parsed _p;
        int _pVersion = int.MinValue;

        Parsed P()
        {
            int v = Store.Version;
            if (_p != null && v == _pVersion) return _p;
            var p = new Parsed();
            foreach (var it in Store.Items(CellAnnounced))
            {
                var e = ParseItem(it);
                if (e == null) continue;
                string hg = e.hash + "." + e.gen.ToString(CultureInfo.InvariantCulture);
                if (p.at.ContainsKey(hg)) continue;   // sorted set: the first item of a hash.gen wins everywhere
                p.at[hg] = e;
                if (!p.cur.TryGetValue(e.hash, out int g) || e.gen > g) p.cur[e.hash] = e.gen;
            }
            foreach (var it in Store.Items(CellDone)) p.done.Add(it);
            foreach (var it in Store.Items(CellBy))
            {
                int bar = it.IndexOf('|');
                if (bar <= 0) continue;
                string hg = it.Substring(0, bar), who = it.Substring(bar + 1);
                if (who.Length == 0) continue;
                if (!p.by.TryGetValue(hg, out var w) || string.CompareOrdinal(who, w) < 0) p.by[hg] = who;
            }
            foreach (var kv in p.at)
            {
                var e = kv.Value;
                if (e.gen != p.cur[e.hash]) continue;
                if (string.IsNullOrEmpty(e.from) || !ValidSource(p, e.from)) continue;
                if (!p.kids.TryGetValue(e.from, out var l)) p.kids[e.from] = l = new List<string>();
                l.Add(kv.Key);
            }
            _p = p; _pVersion = v;
            return p;
        }

        /// A child's source counts only while it is on the generation the child came out of (else a reset cleaned it up).
        static bool ValidSource(Parsed p, string fromHg) => SplitHg(fromHg, out string h, out int g) && p.cur.TryGetValue(h, out int cg) && cg == g;
        /// The source is a STALE generation (known, older): the child is a leftover of a reset and never counts.
        static bool StaleSource(Parsed p, string fromHg) => SplitHg(fromHg, out string h, out int g) && p.cur.TryGetValue(h, out int cg) && cg > g;

        static bool SplitHg(string hg, out string hash, out int gen)
        {
            hash = null; gen = 0;
            int dot = hg?.LastIndexOf('.') ?? -1;
            if (dot <= 0) return false;
            hash = hg.Substring(0, dot);
            return int.TryParse(hg.Substring(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out gen);
        }

        static Entry ParseItem(string it)
        {
            if (string.IsNullOrEmpty(it)) return null;
            var parts = it.Split('|');
            if (parts.Length < 2 || !SplitHg(parts[0], out string h, out int g)) return null;
            var e = new Entry { hash = h, gen = g, kind = parts[1].Length == 1 ? AnnounceKinds.FromCode(parts[1][0]) : null, from = parts.Length > 3 ? parts[3] : "" };
            if (parts.Length > 4 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)) e.order = o;
            if (parts.Length > 2 && parts[2].Length > 0)
                foreach (var hp in parts[2].Split(','))
                {
                    if (hp.Length < 2) continue;
                    string k = AnnounceKinds.FromCode(hp[0]);
                    if (k == null || !int.TryParse(hp.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) continue;
                    (e.holds ??= new Dictionary<string, int>(StringComparer.Ordinal))[k] = n;
                }
            return e;
        }

        static string HoldsCode(IDictionary<string, int> holds)
        {
            if (holds == null || holds.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var k in AnnounceKinds.All)   // canonical order: every device writes the same item
                if (holds.TryGetValue(k, out int n) && n > 0)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(AnnounceKinds.Code(k)).Append(Math.Min(n, 999).ToString(CultureInfo.InvariantCulture));
                }
            return sb.ToString();
        }

        bool Changed(Parsed p, string hg) => p.done.Contains(hg) || (p.kids.TryGetValue(hg, out var l) && l.Count > 0);

        // ── writes ──────────────────────────────────────────────────────

        /// Announce (or re-announce) the object with this stable key. `fromKey` = its source's key (a hand-off): the
        /// object's own key then becomes "<source key>@<gen>#<n>" (returned). Null when the kind is unknown.
        public string Announce(string key, string kind, string item, IDictionary<string, int> holds, string fromKey, object handle, int order = -1)
        {
            kind = AnnounceKinds.Normalize(kind);
            if (kind == null || string.IsNullOrEmpty(key)) return null;
            var p = P();
            string fromHg = "";
            if (!_local.ContainsKey(key) && !string.IsNullOrEmpty(fromKey))
            {
                if (_local.TryGetValue(fromKey, out var src))
                {
                    SyncGen(p, src);
                    fromHg = src.hash + "." + src.gen.ToString(CultureInfo.InvariantCulture);
                    order = src.released;   // a hand-off is numbered by its release index under its source
                    key = fromKey + "@" + src.gen.ToString(CultureInfo.InvariantCulture) + "#" + (src.released++).ToString(CultureInfo.InvariantCulture);
                }
                else Log?.Invoke($"[pl-announce] {Name}: '{key}' came from '{fromKey}', which never announced: counted on its own");
            }
            if (!_local.TryGetValue(key, out var L))
            {
                L = new Local { key = key, hash = Hash(key), fromHg = fromHg, order = order };
                L.gen = p.cur.TryGetValue(L.hash, out int g) ? g : 0;   // a late joiner joins the room's generation
                _local[key] = L;
            }
            else
            {
                int cur = p.cur.TryGetValue(L.hash, out int g) ? g : L.gen;
                if (cur > L.gen) { L.gen = cur; L.released = 0; }                     // another headset reset it first
                else if (Changed(p, L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture)))
                {
                    L.gen++; L.released = 0;                                           // a reset brought it back
                    Log?.Invoke($"[pl-announce] {Name}: {key} back → gen {L.gen}");
                }
                else if (L.kind == kind && HoldsCode(holds) == L.holds) { if (handle != null) L.handle = handle; return key; }   // nothing new
            }
            L.kind = kind; L.item = item; L.holds = HoldsCode(holds);
            if (handle != null) L.handle = handle;
            if (order >= 0 && L.order < 0) L.order = order;
            string itemStr = L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture) + "|" + AnnounceKinds.Code(kind) + "|" + L.holds + "|" + L.fromHg
                + (L.order >= 0 ? "|" + L.order.ToString(CultureInfo.InvariantCulture) : "");
            if (itemStr.Length > SharedState.MaxKeyLength) itemStr = itemStr.Substring(0, SharedState.MaxKeyLength);
            Store.Union(CellAnnounced, itemStr);
            return key;
        }

        void SyncGen(Parsed p, Local L)
        {
            if (p.cur.TryGetValue(L.hash, out int g) && g > L.gen) { L.gen = g; L.released = 0; }
        }

        /// Done (collected / defeated / broken) at its current generation. `by` = the player, or null.
        public bool Done(string key, string by)
        {
            if (string.IsNullOrEmpty(key) || !_local.TryGetValue(key, out var L)) return false;
            var p = P();
            SyncGen(p, L);
            string hg = L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(by))
            {
                string b = hg + "|" + by;
                if (b.Length > SharedState.MaxKeyLength) b = b.Substring(0, SharedState.MaxKeyLength);
                Store.Union(CellBy, b);
            }
            if (p.done.Contains(hg)) return false;
            Store.Union(CellDone, hg);
            return true;
        }

        // ── reads ───────────────────────────────────────────────────────

        public bool Has(string key) => key != null && _local.ContainsKey(key);
        public object HandleOf(string key) => key != null && _local.TryGetValue(key, out var L) ? L.handle : null;

        public bool IsDone(string key)
        {
            if (key == null || !_local.TryGetValue(key, out var L)) return false;
            var p = P();
            SyncGen(p, L);
            return p.done.Contains(L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture));
        }

        /// The live total / remaining / done / by-player for one kind (null kind = every kind).
        public PlaylistAnnounceCount Count(string kind)
        {
            var c = new PlaylistAnnounceCount();
            if (kind == null) { foreach (var k in AnnounceKinds.All) c.Add(Count(k)); return c; }
            kind = AnnounceKinds.Normalize(kind);
            if (kind == null) return c;
            var p = P();
            void Credit(string hg)
            {
                c.done++;
                if (p.by.TryGetValue(hg, out var who)) c.by[who] = (c.by.TryGetValue(who, out int n) ? n : 0) + 1;
            }
            // Waypoints (§ 6.7): the attraction's own waypoint stands for the whole attraction only while the game announced
            // none of its own; with game waypoints, the attraction is those (done when they all are).
            string platformHash = kind == AnnounceKinds.Waypoint ? Hash(AnnounceKinds.PlatformWaypointKey) : null;
            string platformHg = null;
            foreach (var kv in p.cur)
            {
                string hg = kv.Key + "." + kv.Value.ToString(CultureInfo.InvariantCulture);
                if (!p.at.TryGetValue(hg, out var e)) continue;
                if (platformHash != null && kv.Key == platformHash) { if (e.kind == kind) platformHg = hg; continue; }
                bool child = !string.IsNullOrEmpty(e.from) && ValidSource(p, e.from);
                if (!child && !string.IsNullOrEmpty(e.from) && StaleSource(p, e.from)) continue;   // a reset's leftover
                // A holder: what it holds of this kind, covered by what already came out of it.
                int hold = e.holds != null && e.holds.TryGetValue(kind, out int h) ? h : 0;
                // Itself, unless it holds its own kind (a ! block announced 'collect' holding 2 coins = those 2 coins).
                if (!child && e.kind == kind && hold == 0)
                {
                    c.total++;
                    if (p.done.Contains(hg)) Credit(hg); else c.remaining++;
                }
                int n = 0;
                if (p.kids.TryGetValue(hg, out var kids))
                    foreach (var k in kids)
                    {
                        if (!p.at.TryGetValue(k, out var ke) || ke.kind != kind) continue;
                        n++;
                        if (p.done.Contains(k)) Credit(k); else c.remaining++;
                    }
                if (hold > 0 || n > 0)
                {
                    c.total += Math.Max(hold, n);
                    c.remaining += Math.Max(0, hold - n);
                }
            }
            if (platformHg != null && c.total == 0)
            {
                c.total = 1;
                if (p.done.Contains(platformHg)) Credit(platformHg); else c.remaining = 1;
            }
            return c;
        }

        // ── fixed numbers (Aidan, Oct 22: "just make all of them have a number, preset number") ──
        // Per kind, every current object (and every holder of that kind) gets 1…N in a stable order: its announced `order`
        // (the runtime: cm from the attraction's entry side), then its hash — the same on every headset and after a reset
        // (same objects, same keys, same order). A hand-off takes its source's number (its first release; later releases
        // follow after N). Collecting one never renumbers the others.
        readonly Dictionary<string, Dictionary<string, int>> _numbers = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        int _numbersVersion = int.MinValue;

        Dictionary<string, int> Numbers(string kind)
        {
            var p = P();
            if (_numbersVersion != _pVersion) { _numbers.Clear(); _numbersVersion = _pVersion; }
            if (_numbers.TryGetValue(kind, out var map)) return map;
            map = new Dictionary<string, int>(StringComparer.Ordinal);
            var main = new List<(int order, string hash, string hg)>();
            var kids = new List<(string from, int order, string hash, string hg)>();
            foreach (var kv in p.cur)
            {
                string hg = kv.Key + "." + kv.Value.ToString(CultureInfo.InvariantCulture);
                if (!p.at.TryGetValue(hg, out var e)) continue;
                bool holds = e.holds != null && e.holds.ContainsKey(kind);
                if (e.kind != kind && !holds) continue;
                if (!string.IsNullOrEmpty(e.from) && StaleSource(p, e.from)) continue;
                if (!string.IsNullOrEmpty(e.from) && ValidSource(p, e.from)) kids.Add((e.from, e.order, kv.Key, hg));
                else main.Add((e.order, kv.Key, hg));
            }
            main.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.hash, b.hash));
            for (int i = 0; i < main.Count; i++) map[main[i].hg] = i + 1;
            int next = main.Count + 1;
            kids.Sort((a, b) =>
            {
                int na = map.TryGetValue(a.from, out int x) ? x : int.MaxValue, nb = map.TryGetValue(b.from, out int y) ? y : int.MaxValue;
                if (na != nb) return na.CompareTo(nb);
                return a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.hash, b.hash);
            });
            var inherited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in kids)
            {
                if (map.TryGetValue(c.from, out int src) && inherited.Add(c.from)) map[c.hg] = src;   // the first release: the source's number
                else map[c.hg] = next++;
            }
            _numbers[kind] = map;
            return map;
        }

        /// This object's fixed number for `kind` (1…N), or 0 when it is not one of that kind's objects.
        public int NumberOf(string key, string kind)
        {
            kind = AnnounceKinds.Normalize(kind);
            if (kind == null || key == null || !_local.TryGetValue(key, out var L)) return 0;
            var p = P();
            SyncGen(p, L);
            return Numbers(kind).TryGetValue(L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture), out int n) ? n : 0;
        }

        /// The fixed number of the local object `handle` (a marker's target), or 0.
        public int NumberOfHandle(object handle, string kind)
        {
            if (handle == null) return 0;
            foreach (var L in _local.Values) if (ReferenceEquals(L.handle, handle) || Equals(L.handle, handle)) return NumberOf(L.key, kind);
            return 0;
        }

        /// One waypoint on THIS device's list: its key, label, whether it is the attraction's own (platform) waypoint,
        /// done (shared), and its object (Unity: the GameObject; null when gone).
        public sealed class WaypointRow { public string key, label; public bool platform, done; public object handle; }

        /// The live waypoints announced here (current generations), platform first, then in key order.
        public List<WaypointRow> Waypoints()
        {
            var l = new List<WaypointRow>();
            var p = P();
            foreach (var L in _local.Values)
            {
                SyncGen(p, L);
                string hg = L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture);
                if (!p.at.TryGetValue(hg, out var e) || e.kind != AnnounceKinds.Waypoint) continue;
                l.Add(new WaypointRow { key = L.key, label = L.item, platform = L.key == AnnounceKinds.PlatformWaypointKey, done = p.done.Contains(hg), handle = L.handle });
            }
            l.Sort((a, b) => a.platform != b.platform ? (a.platform ? -1 : 1) : string.CompareOrdinal(a.key, b.key));
            return l;
        }

        /// What to mark for a kind on THIS device: its objects of that kind not done, and its holders still holding some.
        public List<object> Live(string kind)
        {
            var l = new List<object>();
            kind = AnnounceKinds.Normalize(kind);
            if (kind == null) return l;
            var p = P();
            foreach (var L in _local.Values)
            {
                if (L.handle == null) continue;
                SyncGen(p, L);
                string hg = L.hash + "." + L.gen.ToString(CultureInfo.InvariantCulture);
                if (!p.at.TryGetValue(hg, out var e)) continue;
                if (!string.IsNullOrEmpty(e.from) && StaleSource(p, e.from)) continue;
                if (e.kind == kind && !p.done.Contains(hg)) { l.Add(L.handle); continue; }
                int hold = e.holds != null && e.holds.TryGetValue(kind, out int h) ? h : 0;
                if (hold <= 0) continue;
                int n = 0;
                if (p.kids.TryGetValue(hg, out var kids)) foreach (var k in kids) if (p.at.TryGetValue(k, out var ke) && ke.kind == kind) n++;
                if (hold > n) l.Add(L.handle);
            }
            return l;
        }

        /// Local keys announced here (checks).
        public IEnumerable<string> Keys => _local.Keys;
        public int LocalCount => _local.Count;

        /// Drop the handle of an object that is gone for good (its key stays: a reset may bring it back).
        public void Forget(object handle)
        {
            if (handle == null) return;
            foreach (var L in _local.Values) if (ReferenceEquals(L.handle, handle)) L.handle = null;
        }
    }
}
