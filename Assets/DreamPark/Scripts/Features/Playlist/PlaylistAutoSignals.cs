// ─────────────────────────────────────────────────────────────────────
//  PlaylistAutoSignals.cs — the platform's SIGNAL ADAPTER (Docs/PLAYLISTS.md
//  § 13): playlist objectives for titles that never call dp.playlist.
//  It listens to what titles already do (PlaylistAutoTaps, badge moments,
//  game storage, GameArea zones) and to what stands in each stop (markers /
//  names: collectibles, bosses, exits), runs PlaylistAutoRules, and feeds the
//  live PlaylistRunner exactly as a title's dp.playlist.signal would
//  (Signal / ReportProgress / Result — requests the leader merges, so two
//  devices seeing one boss die still count once).
//
//  repair-oct20 (CONTRACT § 1–§ 2): every tap is ALSO an event on the bus
//  (PlaylistEvents: net, request, state, storage, badge, zone, enemy, and
//  `gone` = a script object under a live stop destroyed / disabled), which
//  goal blocks listen to. Precedence is per STOP (the title-wide opt-in is
//  gone): a stop a goal block owns, or whose title calls dp.playlist for it,
//  gets no auto signal. Only a PLAYING leg is fed. Navigation is NOT this file's job: the
//  central dp.load_level guard belongs to playlist-runner (PLAYLISTS.md § 14.7).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PlaylistAutoSignals : MonoBehaviour
    {
        /// Master switch (Test Mode / harnesses).
        public static bool Enabled = true;
        /// Every auto signal fired on this device: (occurrenceId, kind, n or NaN, rule) — logs and harnesses.
        public static event Action<string, string, double, string> Fired;
        const double TickSeconds = 0.25, RescanSeconds = 4.0, ExitRadius = 0.6;

        static PlaylistAutoSignals s_inst;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static PlaylistAutoSignals Ensure()
        {
            if (s_inst != null) return s_inst;
            var go = new GameObject("[PlaylistAutoSignals]") { hideFlags = HideFlags.HideInHierarchy };
            if (Application.isPlaying) DontDestroyOnLoad(go);
            s_inst = go.AddComponent<PlaylistAutoSignals>();
            return s_inst;
        }

        // ── Per-run state ─────────────────────────────────────────────
        sealed class Watch
        {
            public GameObject root;
            public readonly List<GameObject> coins = new List<GameObject>();
            public readonly HashSet<GameObject> coinsSeenActive = new HashSet<GameObject>();
            public readonly List<(GameObject go, string kind, string rule)> gone = new List<(GameObject, string, string)>();
            public readonly HashSet<GameObject> goneSeenActive = new HashSet<GameObject>();
            public readonly List<(Collider col, Transform t)> exits = new List<(Collider, Transform)>();
            // Script objects under the stop (Game Graph script ids), for `gone` events.
            public readonly List<(GameObject go, string script, string path)> scripted = new List<(GameObject, string, string)>();
            public readonly HashSet<GameObject> scriptedSeen = new HashSet<GameObject>();
            public readonly HashSet<GameObject> scriptedGone = new HashSet<GameObject>();
            public bool titleScripts;   // a script under this stop calls dp.playlist itself
            public double nextScan;
        }

        sealed class Run
        {
            public PlaylistRunner r;
            public readonly HashSet<string> fired = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> best = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, HashSet<string>> distinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public readonly Dictionary<string, Watch> watch = new Dictionary<string, Watch>(StringComparer.Ordinal);
            public double nextTick;
        }

        readonly Dictionary<PlaylistRunner, Run> _runs = new Dictionary<PlaylistRunner, Run>();
        readonly Dictionary<string, string> _prev = new Dictionary<string, string>(StringComparer.Ordinal);

        void OnEnable()
        {
            PlaylistAutoTaps.NetSent += OnNet;
            PlaylistAutoTaps.StateChanged += OnState;
            PlaylistAutoTaps.StateRequested += OnRequest;
            PlaylistAutoTaps.EnemyDied += OnEnemy;
            API.ProfileAPI.OnBadgeMoment += OnBadge;
            API.GameStorageAPI.OnNumericOp += OnStorage;
            GameArea.OnContentZoneChanged += OnZone;
        }

        void OnDisable()
        {
            PlaylistAutoTaps.NetSent -= OnNet;
            PlaylistAutoTaps.StateChanged -= OnState;
            PlaylistAutoTaps.StateRequested -= OnRequest;
            PlaylistAutoTaps.EnemyDied -= OnEnemy;
            API.ProfileAPI.OnBadgeMoment -= OnBadge;
            API.GameStorageAPI.OnNumericOp -= OnStorage;
            GameArea.OnContentZoneChanged -= OnZone;
            if (s_inst == this) s_inst = null;
        }

        // ── Runs ──────────────────────────────────────────────────────
        List<Run> Live()
        {
            var live = new List<Run>();
            foreach (var r in PlaylistRunner.All)
            {
                if (r == null || r.Plan == null) continue;
                if (!_runs.TryGetValue(r, out var run)) { run = Adopt(r); _runs[r] = run; }
                if (Enabled && r.Row.phase == PlaylistRunRules.Playing) live.Add(run);
            }
            if (_runs.Count > PlaylistRunner.All.Count)
            {
                var dead = new List<PlaylistRunner>();
                foreach (var k in _runs.Keys) if (k == null || k.Plan == null) dead.Add(k);
                foreach (var k in dead) _runs.Remove(k);
            }
            return live;
        }

        Run Adopt(PlaylistRunner r)
        {
            var run = new Run { r = r };
            // Seed "previous value" from every open table so a first change compares against the truth.
            foreach (var st in SharedState.OpenStates())
                foreach (var k in st.Keys) _prev[PrevKey(st, k)] = st.GetJson(k);
            Log(r, $"adopted '{r.Plan.name}' leg {r.Plan.legIndex + 1} (precedence per stop: goal block → title calls → auto rules)");
            return run;
        }

        /// Per-STOP precedence (CONTRACT § 2): the auto rules feed a stop only when no goal block owns it and its title
        /// does not drive it (a dp.playlist call for it, or a script under it that calls dp.playlist).
        bool Auto(Run run, PlaylistRunStop stop)
        {
            if (stop == null) return false;
            string occ = stop.occurrenceId;
            if (run.r.GoalDriven(occ) || run.r.TitleDriven(occ)) return false;
            return !(run.watch.TryGetValue(occ ?? "", out var w) && w.titleScripts);
        }

        bool InCurrentLeg(Run run, PlaylistRunStop s) => s != null && run.r.Plan.Current != null && run.r.Plan.Current.stops.Contains(s);

        static bool RuleOf(PlaylistAutoRule rule, string content)
        {
            if (PlaylistAutoRules.Generic.Contains(rule)) return true;
            foreach (var x in PlaylistAutoRules.ForTitle(content)) if (x == rule) return true;
            return false;
        }

        static IEnumerable<PlaylistAutoRule> RulesForLeg(Run run)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in PlaylistAutoRules.Generic) yield return g;
            var leg = run.r.Plan.Current;
            if (leg == null) yield break;
            foreach (var s in leg.stops)
            {
                string c = run.r.Plan.ContentOf(s) ?? "";
                if (!seen.Add(c)) continue;
                foreach (var x in PlaylistAutoRules.ForTitle(c)) yield return x;
            }
        }

        List<PlaylistRunStop> StopsFor(Run run, PlaylistAutoRule rule, GameObject emitter, JSONObject cur, JSONObject prev)
        {
            var l = new List<PlaylistRunStop>();
            var leg = run.r.Plan.Current;
            string at = rule.at ?? "";
            if (at.StartsWith("@new:", StringComparison.Ordinal)) return PlaylistAutoRules.NewlyNamed(at, leg, cur, prev);
            if (at == "target") { var t = run.r.ResolveStop(null); if (t != null) l.Add(t); return l; }
            if (at.Length > 0)
            {
                if (leg != null) foreach (var s in leg.stops) if (PlaylistAutoRules.Glob(at, PlaylistAutoRules.Leaf(s?.attraction))) l.Add(s);
                return l;
            }
            var st = emitter != null ? run.r.StopOf(emitter) : null;
            if (st != null) l.Add(st);
            return l;
        }

        // ── The one evaluation path ───────────────────────────────────
        void Evaluate(string source, string name, string key, JSONObject cur, JSONObject prev, GameObject emitter, double? value)
        {
            foreach (var run in Live())
            {
                var leg = run.r.Plan.Current;
                foreach (var rule in RulesForLeg(run))
                {
                    if (!PlaylistAutoRules.Hits(rule, source, name, key)) continue;
                    if (!PlaylistAutoRules.When(rule, cur, prev)) continue;
                    foreach (var stop in StopsFor(run, rule, emitter, cur, prev))
                    {
                        if (!InCurrentLeg(run, stop) || !Auto(run, stop) || !RuleOf(rule, run.r.Plan.ContentOf(stop))) continue;
                        if (!PlaylistAutoRules.Applies(rule, leg, stop)) continue;
                        int count = 0;
                        if (rule.n == "count")
                        {
                            string dk = stop.occurrenceId + "|" + rule;
                            if (!run.distinct.TryGetValue(dk, out var set)) run.distinct[dk] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(PlaylistAutoRules.DistinctKey(rule, key, cur));
                            count = set.Count;
                        }
                        Fire(run, stop, rule.kind, PlaylistAutoRules.N(rule, cur, value, count), rule.ToString());
                    }
                }
            }
        }

        void Fire(Run run, PlaylistRunStop stop, string kind, double? n, string why)
        {
            string occ = stop.occurrenceId;
            if (kind == PlaylistAutoRules.Result)
            {
                if (!run.fired.Add(occ + "|result")) return;
                run.r.Result(occ, n ?? 0, true, 0);
            }
            else if (PlaylistAutoRules.IsProgress(kind))
            {
                if (!n.HasValue) { if (!run.fired.Add(occ + "|" + kind)) return; }
                else
                {
                    string bk = occ + "|" + kind;
                    if (run.best.TryGetValue(bk, out var b) && n.Value <= b) return;
                    run.best[bk] = n.Value;
                }
                run.r.Signal(occ, kind, n);
            }
            else
            {
                if (!run.fired.Add(occ + "|" + kind)) return;
                run.r.Signal(occ, kind, n);
            }
            Log(run.r, $"{kind}{(n.HasValue ? " " + n.Value : "")} at {stop.attraction} ({occ}) ← {why}");
            try { Fired?.Invoke(occ, kind, n ?? double.NaN, why); } catch (Exception e) { Debug.LogWarning("[PlaylistAuto] Fired listener threw: " + e.Message); }
        }

        // ── Sources ───────────────────────────────────────────────────
        static JSONObject Parse(string json) => string.IsNullOrEmpty(json) ? null : SharedState.ParseFragment(json);
        static string PrevKey(SharedState s, string key) => s.Handle + "|" + key;

        // ── The event bus (CONTRACT § 1): every tap becomes a PlaylistEvent when a goal block listens ──
        static string StopOfGo(GameObject go)
        {
            if (go == null) return null;
            foreach (var r in PlaylistRunner.All) { var s = r != null && r.Plan != null ? r.StopOf(go) : null; if (s != null) return s.occurrenceId; }
            return null;
        }

        static bool Wants(string source) { try { return PlaylistRunWorld.Main.Wants(source); } catch (Exception) { return false; } }

        static string Obj(string json, string addKey = null, string addVal = null)
        {
            var j = string.IsNullOrEmpty(json) ? new JSONObject() : SharedState.ParseFragment(json);
            if (j == null) j = new JSONObject();
            if (j.type != JSONObject.Type.Object) { var w = new JSONObject(); w.AddField("value", j); j = w; }
            if (addKey != null && !j.HasField(addKey)) j.AddField(addKey, addVal ?? "");
            return j.Print();
        }

        void OnNet(GameObject go, string kind, string payload)
        {
            if (_runs.Count == 0 && PlaylistRunner.All.Count == 0) return;
            if (Wants(PlaylistEvent.Net)) PlaylistEvents.Emit(PlaylistEvent.Net, kind ?? "", Obj(payload), StopOfGo(go));
            Evaluate(PlaylistAutoRules.Net, kind, null, Parse(payload), null, go, null);
        }

        void OnState(SharedState s, string key, string json)
        {
            if (s == null || (s.Name != null && s.Name.StartsWith(PlaylistRunner.StateName, StringComparison.Ordinal))) return;
            string pk = PrevKey(s, key);
            _prev.TryGetValue(pk, out var before);
            if (_prev.Count > 4096) _prev.Clear();   // tables come and go with attractions; keep the cache bounded
            _prev[pk] = json;
            if (PlaylistRunner.All.Count == 0) return;
            if (Wants(PlaylistEvent.State))
            {
                var d = new JSONObject();
                d.AddField("key", key ?? "");
                var v = Parse(json); if (v != null) d.AddField("value", v);
                var pv = Parse(before); if (pv != null) d.AddField("prev", pv);
                PlaylistEvents.Emit(PlaylistEvent.State, s.Name ?? "", d.Print(), StopOfGo(s.BusObject), null);
            }
            Evaluate(PlaylistAutoRules.State, s.Name, key, Parse(json), Parse(before), s.BusObject, null);
        }

        void OnRequest(SharedState s, string kind, string json)
        {
            if (s == null || PlaylistRunner.All.Count == 0) return;
            if (s.Name != null && s.Name.StartsWith(PlaylistRunner.StateName, StringComparison.Ordinal)) return;
            if (Wants(PlaylistEvent.Request)) PlaylistEvents.Emit(PlaylistEvent.Request, kind ?? "", Obj(json, "table", s.Name), StopOfGo(s.BusObject));
            Evaluate(PlaylistAutoRules.Request, kind, null, Parse(json), null, s.BusObject, null);
        }

        void OnBadge(string badgeId, string contentId, long atMs)
        {
            if (PlaylistRunner.All.Count == 0) return;
            if (Wants(PlaylistEvent.Badge))
                PlaylistEvents.Emit(PlaylistEvent.Badge, badgeId ?? "", "{\"content\":\"" + SharedState.JsonEscape(contentId ?? "") + "\"}",
                    StopOfGo(GameArea.currentGameArea != null ? GameArea.currentGameArea.gameObject : null));
            Evaluate(PlaylistAutoRules.Badge, badgeId, null, null, null, GameArea.currentGameArea != null ? GameArea.currentGameArea.gameObject : null, null);
        }

        void OnStorage(string contentId, string slug, string key, string op, double amount, double next)
        {
            if (PlaylistRunner.All.Count == 0) return;
            // Where: the stop the player stands in, else the stop whose attraction slug the write carries.
            GameObject at = GameArea.currentGameArea != null ? GameArea.currentGameArea.gameObject : null;
            if (at == null && !string.IsNullOrEmpty(slug))
                foreach (var run in Live())
                    foreach (var s in run.r.Plan.Current?.stops ?? new List<PlaylistRunStop>())
                        if (API.GameStorageAPI.Slugify(PlaylistAutoRules.Leaf(s.attraction)) == slug) { at = PlaylistRunner.Stage?.StopRoot(run.r, s); break; }
            if (Wants(PlaylistEvent.Storage))
                PlaylistEvents.Emit(PlaylistEvent.Storage, key ?? "", "{\"op\":\"" + SharedState.JsonEscape(op ?? "") + "\",\"amount\":" + PlaylistRunRules.Num(amount)
                    + ",\"next\":" + PlaylistRunRules.Num(next) + ",\"slug\":\"" + SharedState.JsonEscape(slug ?? "") + "\",\"content\":\"" + SharedState.JsonEscape(contentId ?? "") + "\"}", StopOfGo(at));
            // The value a stop gets is what THIS write reported (a max's amount is the round's score, next is the best ever).
            Evaluate(PlaylistAutoRules.Storage, key, null, null, null, at, op == "increment" ? next : amount);
        }

        void OnZone(GameArea before, GameArea now)
        {
            if (PlaylistRunner.All.Count > 0 && Wants(PlaylistEvent.Zone))
            {
                string bo = before != null ? StopOfGo(before.gameObject) : null, no = now != null ? StopOfGo(now.gameObject) : null;
                if (bo != null && bo != no) PlaylistEvents.Emit(PlaylistEvent.Zone, "leave", "{\"area\":\"" + SharedState.JsonEscape(before.name) + "\"}", bo);
                if (no != null && no != bo) PlaylistEvents.Emit(PlaylistEvent.Zone, "enter", "{\"area\":\"" + SharedState.JsonEscape(now.name) + "\"}", no);
            }
            if (now == null || PlaylistRunner.All.Count == 0) return;
            foreach (var run in Live())
            {
                var stop = run.r.StopOf(now.gameObject);
                if (stop == null) continue;
                // A zone rule names the attraction it is about.
                foreach (var rule in RulesForLeg(run))
                {
                    if (rule.source != PlaylistAutoRules.Zone || !PlaylistAutoRules.Glob(rule.name, PlaylistAutoRules.Leaf(stop.attraction))) continue;
                    if (!InCurrentLeg(run, stop) || !Auto(run, stop) || !RuleOf(rule, run.r.Plan.ContentOf(stop))) continue;
                    if (!PlaylistAutoRules.Applies(rule, run.r.Plan.Current, stop)) continue;
                    Fire(run, stop, rule.kind, null, rule.ToString());
                }
            }
        }

        void OnEnemy(GameObject go)
        {
            if (go == null || PlaylistRunner.All.Count == 0) return;
            bool boss = IsBoss(go);
            if (Wants(PlaylistEvent.Enemy))
            {
                var sc = PlaylistEvents.ScriptsOf(go);
                PlaylistEvents.Emit(PlaylistEvent.Enemy, "died", "{\"boss\":" + (boss ? "true" : "false") + ",\"object\":\"" + SharedState.JsonEscape(go.name)
                    + "\",\"script\":\"" + SharedState.JsonEscape(sc.Count > 0 ? sc[0] : "") + "\"}", StopOfGo(go));
            }
            if (!boss) return;
            Evaluate(PlaylistAutoRules.Enemy, "boss", null, null, null, go, null);
        }

        static bool IsBoss(GameObject go)
        {
            var m = go.GetComponentInParent<PlaylistObjectiveMarker>(true);
            if (m != null) return m.kind == PlaylistObjectiveMarkerKind.Boss;
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (PlaylistAutoRules.LooksBoss(t.name, SafeTag(t.gameObject))) return true;
                if (t.GetComponent<DreamParkPackageMembership>() != null) break;
            }
            return false;
        }

        static string SafeTag(GameObject go) { try { return go.tag; } catch (Exception) { return ""; } }

        // ── Watchers: what stands in each live stop ───────────────────
        void Update()
        {
            if (PlaylistRunner.All.Count == 0) return;
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var run in Live())
            {
                if (now < run.nextTick) continue;
                run.nextTick = now + TickSeconds;
                var leg = run.r.Plan.Current;
                if (leg == null) continue;
                foreach (var stop in leg.stops)
                {
                    if (stop == null) continue;
                    var root = PlaylistRunner.Stage?.StopRoot(run.r, stop);
                    if (root == null || !root.activeInHierarchy) continue;
                    if (!run.watch.TryGetValue(stop.occurrenceId, out var w) || w.root != root)
                    {
                        run.watch[stop.occurrenceId] = w = new Watch { root = root };
                        // Per-stop opt-in: a script UNDER this stop that calls dp.playlist drives it (no auto signals here).
                        foreach (var lb in root.GetComponentsInChildren<LuaBehaviour>(true))
                            if (lb != null && lb.luaScript != null && PlaylistAutoRules.OptedIn(lb.luaScript.text)) { w.titleScripts = true; break; }
                    }
                    if (now >= w.nextScan) { ScanScripted(w); if (Auto(run, stop)) Scan(run, leg, stop, w); w.nextScan = now + RescanSeconds; }
                    TickGone(stop, w);
                    if (Auto(run, stop)) Tick(run, leg, stop, w);
                }
            }
        }

        static bool Gone(GameObject go) => go == null || !go.activeSelf;

        // `gone` (CONTRACT § 1): a script object under a live stop, seen live, then destroyed / disabled.
        void ScanScripted(Watch w)
        {
            foreach (var lb in w.root.GetComponentsInChildren<LuaBehaviour>(true))
            {
                if (lb == null || lb.luaScript == null || lb.gameObject == w.root) continue;
                var go = lb.gameObject;
                if (w.scripted.FindIndex(x => x.go == go) >= 0) continue;
                w.scripted.Add((go, PlaylistEvents.ScriptId(lb.luaScript.name), PlaylistEvents.PathUnder(w.root.transform, go.transform)));
            }
        }

        void TickGone(PlaylistRunStop stop, Watch w)
        {
            if (w.scripted.Count == 0) return;
            bool want = Wants(PlaylistEvent.Gone);
            for (int i = 0; i < w.scripted.Count; i++)
            {
                var (go, script, path) = w.scripted[i];
                bool live = go != null && go.activeInHierarchy;
                if (live) { w.scriptedSeen.Add(go); w.scriptedGone.Remove(go); continue; }
                if (!w.scriptedSeen.Contains(go) || w.scriptedGone.Contains(go)) continue;
                // An object whose whole stop is hidden (round locks, holds) is not gone.
                if (go != null && !w.root.activeInHierarchy) continue;
                w.scriptedGone.Add(go);
                if (want) PlaylistEvents.Emit(PlaylistEvent.Gone, script, "{\"script\":\"" + SharedState.JsonEscape(script) + "\",\"path\":\"" + SharedState.JsonEscape(path)
                    + "\",\"destroyed\":" + (go == null ? "true" : "false") + "}", stop.occurrenceId, null);
            }
            w.scripted.RemoveAll(x => x.go == null && w.scriptedGone.Contains(x.go));
        }

        void Scan(Run run, PlaylistRunLeg leg, PlaylistRunStop stop, Watch w)
        {
            string obj = leg.objective?.type ?? "";
            bool bossStop = stop.role == "boss" || stop.role == "miniboss" || obj == PlaylistRunRules.DefeatBoss;
            var markers = w.root.GetComponentsInChildren<PlaylistObjectiveMarker>(true);
            var inactiveRules = new List<PlaylistAutoRule>();
            foreach (var rule in PlaylistAutoRules.ForTitle(run.r.Plan.ContentOf(stop)))
                if (rule.source == PlaylistAutoRules.Inactive && PlaylistAutoRules.Applies(rule, leg, stop)) inactiveRules.Add(rule);

            void AddGone(GameObject go, string kind, string why)
            {
                foreach (var g in w.gone) if (g.go == go) return;
                w.gone.Add((go, kind, why));
            }
            void AddCoin(GameObject go) { if (!w.coins.Contains(go)) w.coins.Add(go); }

            if (markers.Length > 0)
                foreach (var m in markers)
                {
                    if (m == null) continue;
                    switch (m.kind)
                    {
                        case PlaylistObjectiveMarkerKind.Collectible: AddCoin(m.gameObject); break;
                        case PlaylistObjectiveMarkerKind.Boss: AddGone(m.gameObject, PlaylistRunRules.DefeatBoss, "marker:boss"); break;
                        case PlaylistObjectiveMarkerKind.Goal: AddGone(m.gameObject, PlaylistRunRules.SignalComplete, "marker:goal"); break;
                        case PlaylistObjectiveMarkerKind.Exit:
                            if (w.exits.FindIndex(e => e.t == m.transform) < 0) w.exits.Add((m.GetComponent<Collider>(), m.transform));
                            break;
                    }
                }
            bool names = markers.Length == 0;
            if (!names && inactiveRules.Count == 0) return;
            foreach (var t in w.root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == w.root.transform) continue;
                var go = t.gameObject;
                foreach (var rule in inactiveRules)
                    if (PlaylistAutoRules.Glob(rule.name, (t.parent != null ? t.parent.name + "/" : "") + t.name))
                        AddGone(go, rule.kind, rule.ToString());
                if (!names) continue;
                string tag = SafeTag(go);
                if (obj == PlaylistRunRules.Collect && PlaylistAutoRules.LooksCollectible(t.name, tag)) AddCoin(go);
                else if (bossStop && PlaylistAutoRules.LooksBoss(t.name, tag) && (t.parent == null || !PlaylistAutoRules.LooksBoss(t.parent.name, SafeTag(t.parent.gameObject))))
                    AddGone(go, PlaylistRunRules.DefeatBoss, "name:boss");
                else if (obj == PlaylistRunRules.ReachExit && PlaylistAutoRules.LooksExit(t.name, tag) && w.exits.FindIndex(e => e.t == t) < 0)
                    w.exits.Add((go.GetComponent<Collider>(), t));
            }
        }

        void Tick(Run run, PlaylistRunLeg leg, PlaylistRunStop stop, Watch w)
        {
            // Collectibles: each one seen live, then gone = one collected.
            if (w.coins.Count > 0)
            {
                int gone = 0, total = 0;
                foreach (var c in w.coins)
                {
                    if (c != null && c.activeSelf) w.coinsSeenActive.Add(c);
                    if (!w.coinsSeenActive.Contains(c)) continue;
                    total++;
                    if (Gone(c)) gone++;
                }
                if (total > 0)
                {
                    string pk = stop.occurrenceId + "|progress";
                    if (!run.best.TryGetValue(pk, out var lastTotal) || Math.Abs(lastTotal - (total * 10000 + gone)) > 0.5)
                    {
                        run.best[pk] = total * 10000 + gone;
                        run.r.ReportProgress(stop.occurrenceId, gone, total, "coins");
                    }
                    if (gone > 0) Fire(run, stop, PlaylistRunRules.Collect, gone, "collectibles");
                }
            }
            // Bosses / goals / title-named objects: seen live, then gone.
            foreach (var g in w.gone)
            {
                if (g.go != null && g.go.activeInHierarchy) w.goneSeenActive.Add(g.go);
                if (w.goneSeenActive.Contains(g.go) && Gone(g.go)) Fire(run, stop, g.kind, null, g.rule);
            }
            // Exits: the local player's head reaches one.
            if (w.exits.Count > 0)
            {
                var cam = PlayerHead.ContentCamera();
                if (cam == null) return;
                Vector3 head = cam.transform.position;
                foreach (var (col, t) in w.exits)
                {
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    bool inside = col != null && col.enabled
                        ? col.bounds.Contains(new Vector3(head.x, Mathf.Clamp(head.y, col.bounds.min.y, col.bounds.max.y), head.z))
                        : Vector2.Distance(new Vector2(head.x, head.z), new Vector2(t.position.x, t.position.z)) <= ExitRadius;
                    if (inside) { Fire(run, stop, PlaylistRunRules.ReachExit, null, "exit:" + t.name); break; }
                }
            }
        }

        static void Log(PlaylistRunner r, string m) => Debug.Log($"[PlaylistAuto] {(r != null && r.Plan != null ? r.Plan.playlistId + ": " : "")}{m}");
    }
}
