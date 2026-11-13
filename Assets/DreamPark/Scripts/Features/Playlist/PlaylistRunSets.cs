// ─────────────────────────────────────────────────────────────────────
//  PlaylistRunSets.cs — SETS replace authored rounds (PLAYLIST-PLAN § 3.1, pl-sets, Oct 21 2026).
//
//  A playlist is an attraction POOL (each with the challenge it offers and its options) + Lua. The Lua
//  decides what is out: run.show{attractions, rooms, place} / run.add / run.remove. Each set it puts out is
//  ONE LEG of the run (id "set<n>", legIndex n-1, occurrences pl-<playlist>-<n-1>-<i>), so every platform
//  path that knows legs keeps working unchanged: the leader packs the set (PlaylistRun → AdvanceLan →
//  core PackRound → PlaylistPlanBridge.PackLegAsync → the SAME round-swap seam SwapBegins / HoldSwap /
//  SwapDone the round diorama listens to), followers attach from the run's `lan`, readiness / preload,
//  offline cache + resume, time caps.
//    • The set the Lua asked for lives in the run's SharedState ("set") and in this device's registry
//      (Register / Find), so a pack or a follower's plan (PlaylistRunPlanFromManifest.Build) resolves "set<n>".
//    • The FIRST set is known before anything is packed: Preview runs the playlist Lua's on_start in a
//      throwaway sandbox (same inputs → the same set the leader's real on_start shows).
//    • An arc[] record (v1/v2 rounds) keeps playing: ConvertArc wraps its Lua into the default driver
//      run.rounds({...}, own) — one run.show per old round, in order (choose_next still picks).
//
//  PlaylistAttempt: the per-player attempt rules of a challenge (§ 3.2): inside / failed / finished, the
//  attempt closes when everyone inside is done or time is up, solo = a hit loses, the skip after 10+ lost
//  attempts. Pure (no UnityEngine calls) — the offline sim runs it verbatim.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Defective.JSON;

namespace DreamPark
{
    public static class PlaylistSets
    {
        public const string Prefix = "set";
        /// A stop run.add put into the STANDING set: it streams in at once on its own seat (PlaylistRunRules.StreamDue).
        public const string AddNow = "@now";
        /// Lost attempts before the skip may be offered (§ 3.2: "after 10+ lost attempts").
        public const int SkipAfterLosses = 10;
        public static readonly string[] Wishes = { "biggest", "entrance", "outdoor", "far_from" };

        public static string SetId(int n) => Prefix + n.ToString(CultureInfo.InvariantCulture);
        /// "set3" → 3, anything else → 0.
        public static int SetNumber(string legId)
        {
            if (string.IsNullOrEmpty(legId) || !legId.StartsWith(Prefix, StringComparison.Ordinal)) return 0;
            return int.TryParse(legId.Substring(Prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
        }
        public static string OccurrenceId(string playlistId, int n, int i) => $"pl-{playlistId}-{n - 1}-{i}";

        // ── the registry: the sets this device knows (the run's "set" key, a preview, a cached plan) ──
        static readonly Dictionary<string, string> s_sets = new Dictionary<string, string>(StringComparer.Ordinal);
        public static Action<string> Log = m => UnityEngine.Debug.Log(m);

        public static void Register(string playlistId, JSONObject set)
        {
            if (set == null || set.type != JSONObject.Type.Object) return;
            int n = (int)PlaylistRunPlan.Num(set, "n");
            if (n <= 0) return;
            Hydrate(playlistId, set);
            lock (s_sets) s_sets[(playlistId ?? "") + "|" + SetId(n)] = set.Print();
        }

        /// The set "set<n>" of a playlist this device knows, or null.
        public static JSONObject Find(string playlistId, string legId)
        {
            string j;
            lock (s_sets) if (!s_sets.TryGetValue((playlistId ?? "") + "|" + (legId ?? ""), out j)) return null;
            return SharedState.ParseFragment(j);
        }

        public static void Clear() { lock (s_sets) s_sets.Clear(); lock (s_from) s_from.Clear(); lock (s_pools) s_pools.Clear(); }

        // ── the shared copy of a set (the run's "set" key) is COMPACT ───────────
        // A SharedState value is ≤ 4096 chars; a set's stops carry their pool entries' inline goal Lua (~1 KB each), so a set of
        // 4–5 attractions did not fit and the write was refused (a run.add after the first run.show was lost). The shared copy
        // drops each stop's goal; every device puts it back from the pool by ref (Hydrate) — the registry, Leg, set changes.
        static readonly Dictionary<string, List<PlaylistRunStop>> s_pools = new Dictionary<string, List<PlaylistRunStop>>();

        /// This device knows the pool of a sets playlist (PlaylistRunPlanFromManifest.Build): sets of it can be hydrated.
        public static void NotePool(string playlistId, List<PlaylistRunStop> pool)
        {
            if (string.IsNullOrEmpty(playlistId) || pool == null || pool.Count == 0) return;
            lock (s_pools) s_pools[playlistId] = pool;
        }

        /// The set JSON to share: the same, minus each stop's goal Lua (the pool has it).
        public static string Compact(JSONObject set)
        {
            var c = SharedState.ParseFragment(set?.Print());
            if (c == null) return null;
            foreach (var sj in c.GetField("stops")?.list ?? new List<JSONObject>())
                if (sj != null && sj.type == JSONObject.Type.Object && !string.IsNullOrEmpty(PlaylistRunPlan.Str(sj, "ref", null))) sj.RemoveField("goal");
            return c.Print();
        }

        /// Put each stop's goal back from the pool (by ref), in place. Idempotent; a stop with its own goal keeps it.
        public static JSONObject Hydrate(string playlistId, JSONObject set, List<PlaylistRunStop> pool = null)
        {
            if (set == null) return null;
            if (pool == null || pool.Count == 0) lock (s_pools) s_pools.TryGetValue(playlistId ?? "", out pool);
            if (pool == null) return set;
            foreach (var sj in set.GetField("stops")?.list ?? new List<JSONObject>())
            {
                if (sj == null || sj.type != JSONObject.Type.Object || sj.GetField("goal") != null) continue;
                string r = PlaylistRunPlan.Str(sj, "ref", null);
                if (string.IsNullOrEmpty(r)) continue;
                var pe = pool.Find(x => x != null && string.Equals(RefOf(x), r, StringComparison.OrdinalIgnoreCase));
                if (pe == null || string.IsNullOrEmpty(pe.goal) || pe.goal == "default") continue;
                var gj = new JSONObject(); gj.AddField("lua", pe.goal);
                sj.AddField("goal", gj);
            }
            return set;
        }

        /// The sets of a playlist this device registered (a new run of it is coming: its set 1 is previewed fresh).
        public static void Forget(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId)) return;
            lock (s_sets) foreach (var k in new List<string>(s_sets.Keys)) if (k.StartsWith(playlistId + "|", StringComparison.Ordinal)) s_sets.Remove(k);
        }

        // ── run.started_from ────────────────────────────────────────────
        // Noted on every device when an ended run's run.finish{next} starts (or goes to the phone's queue); read by the new run's
        // Lua (run.started_from) AND by its set-1 preview, so both put out the same first set.
        static readonly Dictionary<string, (string from, string runId, DateTime at)> s_from = new Dictionary<string, (string, string, DateTime)>();

        public static void NoteStartedFrom(string next, string from, string runId)
        {
            if (string.IsNullOrEmpty(next) || string.IsNullOrEmpty(from)) return;
            lock (s_from) s_from[next] = (from, runId ?? "", DateTime.UtcNow);
            Forget(next);
            Log?.Invoke($"[pl-sets] {next}: started from '{from}'" + (string.IsNullOrEmpty(runId) ? " (the phone's queue starts it)" : $" as run {runId}"));
        }

        /// The playlist whose run.finish{next} started this run of `playlistId`, or null. No runId (a preview: the plan has none
        /// yet) → the latest note; a note without a run (the phone's queue minted it) → the first run that asks claims it.
        /// Notes last 10 minutes.
        public static string StartedFrom(string playlistId, string runId)
        {
            if (string.IsNullOrEmpty(playlistId)) return null;
            lock (s_from)
            {
                if (!s_from.TryGetValue(playlistId, out var e) || (DateTime.UtcNow - e.at).TotalSeconds > 600) return null;
                if (string.IsNullOrEmpty(runId)) return e.from;
                if (e.runId.Length == 0) { s_from[playlistId] = (e.from, runId, e.at); return e.from; }
                return e.runId == runId ? e.from : null;
            }
        }

        /// The previewed first set, when the registry has none (the packing device, a follower's first attach).
        /// Default: the playlist Lua's on_start in this device's VM, with this space's room count.
        public static Func<string, PlaylistRunPlan, JSONObject> PreviewHook = (legId, plan) =>
            Preview(plan, PlaylistLuaRuntime.DefaultEnv?.Invoke(), PlaylistRunWorld.Main.SpaceRooms?.Invoke() ?? 1);

        /// Set 1 of a sets playlist: what its Lua's on_start shows (run.show) in a throwaway sandbox. Null = none / no VM.
        public static JSONObject Preview(PlaylistRunPlan plan, XLua.LuaEnv env, int rooms)
        {
            if (plan == null || env == null || string.IsNullOrWhiteSpace(plan.lua)) return null;
            var rt = PlaylistLuaRuntime.For(env);
            var ctx = new JSONObject(); ctx.AddField("runId", plan.runId ?? ""); ctx.AddField("playlistId", plan.playlistId ?? "");
            string sf = StartedFrom(plan.playlistId, plan.runId);
            if (!string.IsNullOrEmpty(sf)) ctx.AddField("startedFrom", sf);
            string err = "no Lua runtime";
            string req = rt != null ? rt.PreviewFirstSet(plan.lua, ctx.Print(), rooms, out err) : null;
            if (req == null) { Log?.Invoke($"[pl-sets] {plan.playlistId}: no first set from the playlist Lua ({err ?? "on_start showed nothing"})"); return null; }
            var missing = new List<string>();
            var reqJson = SharedState.ParseFragment(req);
            var set = BuildSet(1, plan.pool, reqJson, missing);
            int added = AppendAdded(set, plan.pool, reqJson?.GetField("added"), missing);
            if (missing.Count > 0) Log?.Invoke($"[pl-sets] {plan.playlistId}: set 1 names refs not in the pool: {string.Join(", ", missing)}");
            Log?.Invoke($"[pl-sets] {plan.playlistId}: set 1 previewed for {rooms} room(s): [{string.Join(", ", Refs(set))}]"
                + (added > 0 ? $" ({added} joined by on_start's run.add: packed with the set, each in a room the shown set does not use)" : ""));
            return set;
        }

        /// Oct 23 (pl-loading): on_start's run.add requests (after its run.show) folded into set 1 — each new ref becomes a stop
        /// flagged `"added": true` with the next occurrence index, exactly what the leader's run.add would have appended
        /// (ChangeStandingSet), so the real run.show adopts this set and its run.add finds them already out. Returns how many.
        public static int AppendAdded(JSONObject set, List<PlaylistRunStop> pool, JSONObject addedRequests, List<string> missing)
        {
            if (set == null || addedRequests == null || addedRequests.type != JSONObject.Type.Array || addedRequests.list == null) return 0;
            var stops = set.GetField("stops");
            if (stops == null || stops.type != JSONObject.Type.Array) return 0;
            int next = (int)PlaylistRunPlan.Num(set, "next", stops.count), n0 = stops.count;
            var have = new HashSet<string>(Refs(set), StringComparer.OrdinalIgnoreCase);
            foreach (var req in addedRequests.list)
            {
                var part = BuildSet((int)PlaylistRunPlan.Num(set, "n", 1), pool, req, missing);
                foreach (var sj in part.GetField("stops")?.list ?? new List<JSONObject>())
                {
                    string r = PlaylistRunPlan.Str(sj, "ref", "");
                    if (string.IsNullOrEmpty(r) || !have.Add(r)) continue;
                    sj.SetField("i", next++);
                    sj.SetField("added", true);
                    stops.Add(sj);
                }
            }
            set.SetField("next", next);
            return stops.count - n0;
        }

        /// The refs of a set's stops that joined it by run.add (`"added": true`).
        public static HashSet<string> AddedRefs(JSONObject set)
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sj in set?.GetField("stops")?.list ?? new List<JSONObject>())
                if (PlaylistRunPlan.Bool(sj, "added", false)) h.Add(PlaylistRunPlan.Str(sj, "ref", ""));
            return h;
        }

        /// A standing set leg back as set JSON (a cached plan on resume: packs and followers resolve it again).
        public static JSONObject FromLeg(PlaylistRunLeg leg)
        {
            int n = SetNumber(leg?.id);
            if (n <= 0) return null;
            var set = new JSONObject(); set.AddField("n", n); set.AddField("id", leg.id);
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var s in leg.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                var c = Clone(s); c.occurrenceId = ""; c.rectId = ""; c.roomId = ""; c.seat = null;
                var sj = PlaylistRunPlan.StopJson(c); sj.SetField("ref", RefOf(s));
                string occ = s.occurrenceId ?? ""; int dash = occ.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(occ.Substring(dash + 1), out int idx)) sj.SetField("i", idx);
                arr.Add(sj);
            }
            set.AddField("stops", arr);
            return set;
        }

        /// set<n> of a plan: the registry, else (set1) a preview of the playlist Lua's on_start.
        public static JSONObject Resolve(PlaylistRunPlan plan, string legId)
        {
            var s = Find(plan?.playlistId, legId);
            if (s != null || plan == null) return s;
            if (SetNumber(legId) != 1) return null;
            try { s = PreviewHook?.Invoke(legId, plan); } catch (Exception e) { Log?.Invoke("[pl-sets] preview threw: " + e.Message); }
            if (s != null) Register(plan.playlistId, s);
            return s;
        }

        // ── refs → pool entries ─────────────────────────────────────────

        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        static bool Eq(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// A playlist's ref to one pool attraction: its authored id, its attraction (prefab leaf), its catalog id or address.
        public static PlaylistRunStop PoolEntry(List<PlaylistRunStop> pool, string reference)
        {
            if (pool == null || string.IsNullOrEmpty(reference)) return null;
            foreach (var p in pool) if (p != null && Eq(p.stopId, reference)) return p;
            string leaf = Leaf(reference);
            foreach (var p in pool) if (p != null && (Eq(Leaf(p.attraction), leaf) || Eq(p.attractionId, reference) || Eq(Leaf(p.resourceAddress), leaf))) return p;
            return null;
        }

        /// The ref a stop answers to (its authored id, else its attraction leaf).
        public static string RefOf(PlaylistRunStop s) => s == null ? "" : !string.IsNullOrEmpty(s.stopId) ? s.stopId : Leaf(s.attraction);

        public static bool Matches(PlaylistRunStop s, string reference) =>
            s != null && !string.IsNullOrEmpty(reference)
            && (Eq(s.stopId, reference) || Eq(s.occurrenceId, reference) || Eq(Leaf(s.attraction), Leaf(reference)) || Eq(s.attractionId, reference));

        static PlaylistRunStop Clone(PlaylistRunStop p) => PlaylistRunPlan.StopFromJson(PlaylistRunPlan.StopJson(p));

        /// run.show's request → the set JSON {n, id, rooms, stops:[stop + lock?]} (unknown refs are reported in `missing`).
        /// attractions: an array of refs or {ref|attraction, challenge, options, place, lock}; place: {ref: wish | {far_from}};
        /// lock: {ref: {needs, sign} | true}.
        public static JSONObject BuildSet(int n, List<PlaylistRunStop> pool, JSONObject request, List<string> missing)
        {
            var set = new JSONObject();
            set.AddField("n", n);
            set.AddField("id", SetId(n));
            int rooms = (int)PlaylistRunPlan.Num(request, "rooms");
            if (rooms > 0) set.AddField("rooms", rooms);
            var place = request?.GetField("place");
            var locks = request?.GetField("lock");
            var stops = new JSONObject(JSONObject.Type.Array);
            var list = request?.GetField("attractions")?.list ?? new List<JSONObject>();
            foreach (var a in list)
            {
                if (a == null) continue;
                string reference = a.type == JSONObject.Type.String ? a.stringValue
                    : PlaylistRunPlan.Str(a, "ref", null) ?? PlaylistRunPlan.Str(a, "id", null) ?? PlaylistRunPlan.Str(a, "attraction", null);
                var p = PoolEntry(pool, reference);
                PlaylistRunStop st;
                if (p != null) st = Clone(p);
                else if (a.type == JSONObject.Type.Object && !string.IsNullOrEmpty(PlaylistRunPlan.Str(a, "attraction", null)))
                    st = new PlaylistRunStop { attraction = Leaf(PlaylistRunPlan.Str(a, "attraction", "")), stopId = PlaylistRunPlan.Str(a, "id", null), role = PlaylistRunPlan.Str(a, "role", "stage") };
                else { missing?.Add(reference ?? "?"); continue; }
                if (a.type == JSONObject.Type.Object)
                {
                    string ch = PlaylistRunPlan.Str(a, "challenge", null); if (!string.IsNullOrEmpty(ch)) st.challenge = ch;
                    var op = a.GetField("options"); if (op != null && op.type == JSONObject.Type.Object) st.options = op.Print();
                    string pl = WishText(a.GetField("place")); if (pl != null) st.place = pl;
                    string role = PlaylistRunPlan.Str(a, "role", null); if (!string.IsNullOrEmpty(role)) st.role = role;
                }
                string key = reference ?? RefOf(st);
                string w = WishText(place?.GetField(key)) ?? WishText(place?.GetField(RefOf(st)));
                if (w != null) st.place = w;
                st.occurrenceId = ""; st.standing = true;
                var sj = PlaylistRunPlan.StopJson(st);
                sj.SetField("ref", key);
                sj.SetField("i", stops.count);   // its occurrence index in the set: pl-<playlist>-<n-1>-<i>, stable across run.add / run.remove
                var lk = (a.type == JSONObject.Type.Object ? a.GetField("lock") : null) ?? locks?.GetField(key) ?? locks?.GetField(RefOf(st));
                if (lk != null && !(lk.type == JSONObject.Type.Bool && !lk.boolValue))
                    sj.SetField("lock", lk.type == JSONObject.Type.Object ? lk : new JSONObject());
                stops.Add(sj);
            }
            set.AddField("stops", stops);
            set.AddField("next", stops.count);
            return set;
        }

        /// A set stop's occurrence index ("i"; older sets: its position).
        public static int IndexOf(JSONObject setStop, int position) => (int)PlaylistRunPlan.Num(setStop, "i", position);

        /// A placement wish as text: "biggest" | "entrance" | "outdoor" | "far_from:<ref>" (null = none / unknown).
        public static string WishText(JSONObject w)
        {
            if (w == null) return null;
            if (w.type == JSONObject.Type.String)
            {
                string t = (w.stringValue ?? "").Trim().ToLowerInvariant();
                return t == "biggest" || t == "entrance" || t == "outdoor" || t.StartsWith("far_from:", StringComparison.Ordinal) ? t : null;
            }
            if (w.type == JSONObject.Type.Object)
            {
                string far = PlaylistRunPlan.Str(w, "far_from", null);
                if (!string.IsNullOrEmpty(far)) return "far_from:" + far;
                foreach (var k in Wishes) if (PlaylistRunPlan.Bool(w, k, false)) return k;
            }
            return null;
        }

        /// The set's refs (in order).
        public static List<string> Refs(JSONObject set)
        {
            var l = new List<string>();
            foreach (var s in set?.GetField("stops")?.list ?? new List<JSONObject>()) l.Add(PlaylistRunPlan.Str(s, "ref", ""));
            return l;
        }

        /// The set as a run leg (sets mode). place(setIndex, i, stop) = the device's placed stop (core: its packed
        /// occurrence), null = the set's stop as is with occurrence pl-<playlist>-<n-1>-<i>.
        public static PlaylistRunLeg Leg(string playlistId, JSONObject set, Func<int, int, PlaylistRunStop, PlaylistRunStop> place = null)
        {
            if (set == null) return null;
            Hydrate(playlistId, set);
            int n = Math.Max(1, (int)PlaylistRunPlan.Num(set, "n"));
            var leg = new PlaylistRunLeg { id = SetId(n), name = PlaylistRunPlan.Str(set, "name", "") ?? "", beat = n == 1 ? "opening" : "rising" };
            leg.objective = new PlaylistRunObjective { type = PlaylistRunRules.ReachExit };
            int i = 0;
            foreach (var sj in set.GetField("stops")?.list ?? new List<JSONObject>())
            {
                var s = PlaylistRunPlan.StopFromJson(sj);
                if (s == null) continue;
                int idx = IndexOf(sj, i);
                s.occurrenceId = OccurrenceId(playlistId, n, idx);
                s.standing = true; s.streamAfter = null;
                var placed = place?.Invoke(n - 1, idx, s);
                if (placed != null && placed != s)
                {
                    // The device placed it (occurrence / rect / room / seat): keep the set's authored challenge data.
                    placed.stopId ??= s.stopId; placed.challenge ??= s.challenge; placed.options ??= s.options;
                    placed.goal ??= s.goal; placed.displayName ??= s.displayName; placed.place ??= s.place;
                    placed.lockSign ??= s.lockSign; placed.dreamieJson ??= s.dreamieJson; placed.role ??= s.role;
                    if (string.IsNullOrEmpty(placed.attractionId)) placed.attractionId = s.attractionId;
                    s = placed;
                }
                leg.stops.Add(s);
                i++;
            }
            return leg;
        }

        // ── arc[] → the default Lua (records from before sets keep playing) ──

        /// True when a playlist's Lua already drives sets itself.
        public static bool DrivesSets(string lua) =>
            !string.IsNullOrEmpty(lua) && (lua.Contains("run.show") || lua.Contains("run.batches") || lua.Contains("run.rounds") || lua.Contains("run.add"));

        /// An arc[] plan's playlist Lua as run.show per old round, in order: the record's own hooks (if any) wrapped by the
        /// default driver run.rounds({ids…}, own). Null when the plan already drives sets or has no rounds.
        public static string ConvertArc(PlaylistRunPlan plan)
        {
            if (plan == null || plan.sets || plan.legs == null || plan.legs.Count == 0 || DrivesSets(plan.lua)) return null;
            var sb = new StringBuilder();
            sb.Append("-- Converted from arc[] rounds on load (pl-sets): one run.show per old round, in order.\n");
            sb.Append("local own = (function()\n");
            sb.Append(string.IsNullOrWhiteSpace(plan.lua) ? "return {}" : plan.lua);
            sb.Append("\nend)()\nreturn run.rounds({");
            for (int i = 0; i < plan.legs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('\'').Append((plan.legs[i]?.id ?? ("leg" + (i + 1))).Replace("\\", "\\\\").Replace("'", "\\'")).Append('\'');
            }
            sb.Append("}, own)\n");
            return sb.ToString();
        }

        // ── § 3.4 requires.badges ───────────────────────────────────────

        /// May this group play? Every required badge must be held by at least one present account (a group plays a playlist
        /// when any of its accounts unlocked it). holders: each present account's held badges.
        public static bool MayPlay(IList<string> required, IEnumerable<ICollection<string>> holders)
        {
            if (required == null || required.Count == 0) return true;
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in holders ?? Array.Empty<ICollection<string>>()) if (h != null) foreach (var b in h) all.Add(b);
            foreach (var r in required) if (!string.IsNullOrEmpty(r) && !all.Contains(r)) return false;
            return true;
        }

        /// requires.badges of a record: `requires: {badges: [...]}` (v3), else "badge:<id>" refs of the v1 list / requiresJson.
        public static List<string> RequiredBadges(JSONObject requires, IEnumerable<string> refs)
        {
            var l = new List<string>();
            if (requires != null && requires.type == JSONObject.Type.Object)
                foreach (var x in requires.GetField("badges")?.list ?? new List<JSONObject>())
                    if (x != null && x.type == JSONObject.Type.String && !string.IsNullOrEmpty(x.stringValue) && !l.Contains(x.stringValue)) l.Add(x.stringValue);
            if (requires != null && requires.type == JSONObject.Type.Array)
                foreach (var x in requires.list ?? new List<JSONObject>())
                {
                    string id = x == null ? null : x.type == JSONObject.Type.String ? (x.stringValue.StartsWith("badge:", StringComparison.Ordinal) ? x.stringValue.Substring(6) : null)
                        : PlaylistRunPlan.Str(x, "badge", null);
                    if (!string.IsNullOrEmpty(id) && !l.Contains(id)) l.Add(id);
                }
            foreach (var r in refs ?? Array.Empty<string>())
                if (r != null && r.StartsWith("badge:", StringComparison.Ordinal) && r.Length > 6 && !l.Contains(r.Substring(6))) l.Add(r.Substring(6));
            return l;
        }
    }

    /// One challenge's attempts (§ 3.2) — the leader's, shared as "at.{occ}" so a new leader resumes it.
    public sealed class PlaylistAttempt
    {
        public const string In = "in", Failed = "failed", Finished = "finished";
        /// The attempt number (1 = the first), its reset epoch (0 = never reset), lost attempts so far.
        public int n = 1, epoch, lost;
        /// The challenge is won (first win) / skipped; the attempt is closed (every player inside is done).
        public bool won, skipped, closed;
        /// A retry was sent: waiting for the game's reset_done {epoch} (outcomes of the old attempt are ignored meanwhile).
        public bool resetting;
        public double resetAt, startedAt;
        /// The skip is on offer (lost ≥ 10 and it is the last unfinished challenge of the set).
        public bool skipOffered;
        /// Players in THIS attempt → in | failed | finished.
        public readonly SortedDictionary<string, string> p = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// Players standing inside the attraction now (enter / leave), whatever the attempt.
        public readonly SortedSet<string> inside = new SortedSet<string>(StringComparer.Ordinal);

        public int Count(string state) { int c = 0; foreach (var v in p.Values) if (v == state) c++; return c; }
        public bool Solo => p.Count <= 1;

        public void Enter(string player)
        {
            if (string.IsNullOrEmpty(player)) return;
            inside.Add(player);
            if (!closed && !resetting && !p.ContainsKey(player)) p[player] = In;
        }

        /// True when this leave closes a won attempt (the last one still playing walked out).
        public bool Leave(string player)
        {
            if (string.IsNullOrEmpty(player)) return false;
            inside.Remove(player);
            if (p.TryGetValue(player, out var st) && st == In) p.Remove(player);
            if (won && !closed && !resetting && Count(In) == 0) { closed = true; return true; }
            return false;
        }

        /// stop.won{by}: "first" (the challenge is won now; the attempt stays open while others are inside), "open" (a later
        /// finisher, others still inside), "closed" (nobody left inside: the attempt closes), "ignored".
        public string Won(string by)
        {
            if (closed || resetting) return "ignored";
            bool first = !won;
            won = true;
            if (!string.IsNullOrEmpty(by)) p[by] = Finished;
            else foreach (var k in new List<string>(p.Keys)) if (p[k] == In) p[k] = Finished;
            if (p.Count == 0 && !string.IsNullOrEmpty(by)) p[by] = Finished;
            bool anyIn = Count(In) > 0;
            if (!anyIn) closed = true;
            return closed ? (first ? "first_closed" : "closed") : first ? "first" : "open";
        }

        /// stop.lost{why, player}: "out" (this player is out — a ghost — the others play on), "lost" (the attempt is lost: retry),
        /// "closed" (won earlier, now everyone inside is done), "ignored". Alone, a hit loses; a group loses when everyone
        /// has failed; a loss with no player (time up, the attraction broke) loses the whole attempt.
        public string Lost(string why, string player)
        {
            if (closed || resetting) return "ignored";
            bool whole = string.IsNullOrEmpty(player) || why == "time";
            if (!whole && !p.ContainsKey(player)) p[player] = In;
            if (!whole && !Solo)
            {
                p[player] = Failed;
                if (Count(In) > 0) return "out";
                if (won) { closed = true; return "closed"; }
                return "lost";
            }
            if (won)
            {
                foreach (var k in new List<string>(p.Keys)) if (p[k] == In) p[k] = Failed;
                closed = true;
                return "closed";
            }
            foreach (var k in new List<string>(p.Keys)) if (p[k] == In) p[k] = Failed;
            return "lost";
        }

        /// After "lost": count it, bump the epoch, wait for the reset.
        public void BeginRetry(double now)
        {
            lost++;
            epoch++;
            resetting = true;
            resetAt = now;
            p.Clear();
        }

        /// The game reset (reset_done {epoch}) or the fallback timer: a new attempt with whoever stands inside.
        public bool ResetDone(int e, double now)
        {
            if (!resetting || e < epoch) return false;
            resetting = false;
            n++;
            startedAt = now;
            p.Clear();
            foreach (var x in inside) p[x] = In;
            return true;
        }

        public string ToJson()
        {
            var j = new JSONObject();
            j.AddField("n", n); j.AddField("e", epoch); j.AddField("l", lost);
            if (won) j.AddField("w", true);
            if (skipped) j.AddField("sk", true);
            if (closed) j.AddField("c", true);
            if (resetting) { j.AddField("r", true); j.AddField("ra", Math.Round(resetAt, 3)); }
            if (skipOffered) j.AddField("so", true);
            if (startedAt > 0) j.AddField("t", Math.Round(startedAt, 3));
            var pj = new JSONObject();
            foreach (var kv in p) pj.AddField(kv.Key, kv.Value);
            j.AddField("p", pj);
            var ij = new JSONObject(JSONObject.Type.Array);
            foreach (var x in inside) ij.Add(x);
            j.AddField("in", ij);
            return j.Print();
        }

        public static PlaylistAttempt FromJson(JSONObject j)
        {
            var a = new PlaylistAttempt();
            if (j == null || j.type != JSONObject.Type.Object) return a;
            a.n = Math.Max(1, (int)PlaylistRunPlan.Num(j, "n", 1)); a.epoch = (int)PlaylistRunPlan.Num(j, "e"); a.lost = (int)PlaylistRunPlan.Num(j, "l");
            a.won = PlaylistRunPlan.Bool(j, "w", false); a.skipped = PlaylistRunPlan.Bool(j, "sk", false); a.closed = PlaylistRunPlan.Bool(j, "c", false);
            a.resetting = PlaylistRunPlan.Bool(j, "r", false); a.resetAt = PlaylistRunPlan.Num(j, "ra"); a.skipOffered = PlaylistRunPlan.Bool(j, "so", false);
            a.startedAt = PlaylistRunPlan.Num(j, "t");
            var pj = j.GetField("p");
            if (pj?.keys != null) for (int i = 0; i < pj.keys.Count; i++) if (pj.list[i]?.type == JSONObject.Type.String) a.p[pj.keys[i]] = pj.list[i].stringValue;
            foreach (var x in j.GetField("in")?.list ?? new List<JSONObject>()) if (x != null && x.type == JSONObject.Type.String) a.inside.Add(x.stringValue);
            return a;
        }
    }
}
