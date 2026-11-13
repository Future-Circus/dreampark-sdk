// ─────────────────────────────────────────────────────────────────────
//  PlaylistRoundLocks.cs — holds the rounds that are not playing yet
//  (whole-arc playlists, Docs/PLAYLISTS.md § 7.10; rules in
//  PlaylistRoundRules).
//
//  Lives on the package owner NEXT TO the runner, so it survives the runner
//  swap a round change makes (Attach with the next legIndex, no re-pack):
//    • future rounds: GamePause.HoldScope on the stop root (visible, frozen,
//      no hazards) + a locked force field with a padlock and "Round 3";
//      walking up to one raises `stop_locked` (Dreamie: "That one's for
//      later!"), rate-limited per stop for this device's player;
//    • the current round: released, its field dissolving (the awaken
//      flourish);
//    • done rounds: held again with a faint "Cleared" mark (players focus on
//      the current round).
//  Every device runs it from its own copy of the plan (the round IS the
//  queue item every device follows), so all devices agree.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    [DisallowMultipleComponent]
    public sealed class PlaylistRoundLocks : MonoBehaviour
    {
        sealed class Entry
        {
            public PlaylistRunStop stop;
            public int leg;
            public PlaylistRoundRules.Round state;
            public GameObject root;
            public bool held;
            public PlaylistLockField field;
            public Vector3 centre;
            public Quaternion rot;
            public Vector2 size;
            public float height;
            public double lastCue = -1;
            public List<string> remaining;
        }

        readonly Dictionary<string, Entry> _stops = new Dictionary<string, Entry>(StringComparer.Ordinal);
        PlaylistRunner _runner;
        int _leg = -1;
        string _run;
        float _nextScan;

        /// For checks / the E2E harness.
        public int HeldCount { get { int n = 0; foreach (var e in _stops.Values) if (e.held) n++; return n; } }
        public int FieldCount { get { int n = 0; foreach (var e in _stops.Values) if (e.field != null && !e.field.Cleared && !e.field.Dissolving) n++; return n; } }
        public int CurrentLeg => _leg;
        public IEnumerable<(string occ, int leg, PlaylistRoundRules.Round state, bool held, bool field, bool standing)> Snapshot()
        {
            foreach (var kv in _stops)
                yield return (kv.Key, kv.Value.leg, kv.Value.state, kv.Value.held,
                    kv.Value.field != null && !kv.Value.field.Cleared && !kv.Value.field.Dissolving, kv.Value.root != null && kv.Value.root.activeInHierarchy);
        }

        /// The runner calls it every frame (any device). No-op unless the plan stands whole or a stop of this round is
        /// authored as locked (lockedUntil).
        public static void Sync(PlaylistRunner runner, IPlaylistStage stage)
        {
            if (runner == null || runner.Plan == null) return;
            bool whole = PlaylistRoundRules.WholeArc(runner.Plan), gated = PlaylistRoundRules.HasGates(runner.Plan.Current)
                || (runner.Run != null && runner.Run.HasLocks);   // the playlist Lua's run.lock (repair-oct20 CONTRACT § 3)
            if (!whole && !gated && !runner.TryGetComponent(out PlaylistRoundLocks _)) return;
            if (!runner.TryGetComponent(out PlaylistRoundLocks locks)) locks = runner.gameObject.AddComponent<PlaylistRoundLocks>();   // no ?? on Unity objects (editor fake-null)
            stage ??= new PlaylistDefaultStage();
            if (whole) locks.Apply(runner, stage);
            locks.Gates(runner, stage);
        }

        // ── AUTHORED LOCKS (Oct 19, Aidan: "only … a boss battle that would end the round and we want to gate it until
        // other objectives are complete") ─────────────────────────────────────────────────────────────────────────
        // A stop of the CURRENT round with `lockedUntil` (same-round occurrence ids) stands frozen behind the square field
        // with its sign ("Finish Lava Crossing and Mole Trench to unlock", the requirements checked off live) until every
        // one of them is over; then the field dissolves and the stop is released. Walking up to it: `stop_locked` with
        // the remaining names (Dreamie: "Finish … first!" / "Just … left!"), once per cooldown — at once when the list
        // changed. Driven only by the authored list, nothing title-specific.
        readonly Dictionary<string, Entry> _gates = new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _gateCueKey = new Dictionary<string, string>(StringComparer.Ordinal);
        float _nextGate;
        string _gateLeg;

        /// For checks: occurrence id → (locked, field standing, remaining names).
        public IEnumerable<(string occ, bool locked, bool field, List<string> remaining)> GateSnapshot()
        {
            foreach (var kv in _gates)
                yield return (kv.Key, kv.Value.held, kv.Value.field != null && !kv.Value.field.Dissolving, kv.Value.remaining);
        }

        void Gates(PlaylistRunner runner, IPlaylistStage stage)
        {
            var plan = runner.Plan;
            string legKey = (plan.runId ?? plan.playlistId) + "/" + plan.instanceId + "/" + plan.legIndex;
            if (_gateLeg != legKey) { ReleaseGates(); _gateLeg = legKey; }
            var leg = plan.Current;
            if (leg == null || Time.time < _nextGate) { TickGateNear(runner); return; }
            _nextGate = Time.time + 0.25f;
            foreach (var s in leg.stops)
            {
                var runLock = runner.Run?.LockOf(s?.occurrenceId);
                if (!PlaylistRoundRules.Gated(s) && runLock == null && !_gates.ContainsKey(s?.occurrenceId ?? "")) continue;
                if (!_gates.TryGetValue(s.occurrenceId, out var e)) _gates[s.occurrenceId] = e = new Entry { stop = s, leg = plan.legIndex, state = PlaylistRoundRules.Round.Current };
                if (e.root == null) e.root = stage.StopRoot(runner, s);
                if (e.root == null) continue;
                // Every tick, not once (Oct 19 cut: measured mid-arrival, a gate's field sat far below the ground): the
                // field follows its attraction's seated floor.
                Measure(e);
                if (e.field != null && !e.field.Dissolving)
                    e.field.transform.SetPositionAndRotation(e.centre, e.rot);
                var needs = PlaylistRoundRules.GateNeeds(leg, s, occ => runner.Progress.TryGetValue(occ, out var p) ? p : null);
                if (runLock != null)
                {
                    // run.lock(stopId, {needs, sign}): the playlist Lua holds it until run.unlock. A need naming a stop of this
                    // round checks off as that stop finishes; free text (or the sign) stays until the Lua unlocks.
                    var rl = runLock.Value;
                    foreach (var n in rl.needs)
                    {
                        var sib = leg.stops.Find(x => x != null && (x.occurrenceId == n || x.stopId == n || PlaylistAutoRules.Leaf(x.attraction) == n));
                        // One lock, one list (record v2 § 8.6): a need the stop's lockedUntil already names is not listed twice.
                        if (sib != null && s.lockedUntil != null && s.lockedUntil.Contains(sib.occurrenceId)) continue;
                        needs.Add(sib != null ? (PlaylistObjectiveText.Name(sib), PlaylistRunRules.StopOver(runner.Progress.TryGetValue(sib.occurrenceId, out var sp) ? sp : null)) : (n, false));
                    }
                    if (rl.needs.Count == 0) needs.Add((string.IsNullOrEmpty(rl.sign) ? "Not yet" : rl.sign, false));
                }
                e.remaining = needs.Where(n => !n.done).Select(n => n.name).ToList();
                bool locked = e.remaining.Count > 0 || runLock != null;
                if (locked && e.remaining.Count == 0) e.remaining.Add(runLock?.sign ?? "Not yet");
                // The sign: the stop's authored lockSign while its lockedUntil holds it, else the Lua lock's sign, else the
                // platform's "Finish … to unlock".
                string sign = PlaylistRoundRules.LockSignFor(s, needs, runLock?.sign);
                if (locked)
                {
                    if (!e.held) { GamePause.HoldScope(e.root); e.held = true; }
                    if (e.field == null || e.field.Dissolving) e.field = PlaylistLockField.Locked(transform, e.centre, e.rot, e.size, e.height, needs, sign);
                    else e.field.SetNeeds(needs, sign);
                }
                else if (e.held || e.field != null)
                {
                    if (e.field != null) { e.field.SetNeeds(needs, sign); if (!e.field.Dissolving) e.field.Dissolve(); e.field = null; }
                    if (e.held)
                    {
                        GamePause.ReleaseScope(e.root); e.held = false;
                        // Dreamie says the way is open (the round's boss line waits for this, never before it).
                        var j = new JSONObject();
                        j.AddField("stopId", s.occurrenceId ?? "");
                        j.AddField("headline", PlaylistObjectiveText.Name(s));
                        j.AddField("boss", s.role == "boss" || s.role == "miniboss");
                        j.AddField("line", PlaylistRoundRules.UnlockedSayLine(PlaylistObjectiveText.Name(s), s.role == "boss" || s.role == "miniboss"));
                        runner.Cue("stop_unlocked", s, j);
                    }
                    Debug.Log($"[Playlist] {s.attraction} unlocked (its requirements are done)");
                }
            }
            TickGateNear(runner);
        }

        /// Oct 19 (round-diorama, video-director's run): while the round-change diorama owns Dreamie (the objective
        /// intro's HoldOff), a walk-up cue would be dropped and its cooldown spent; it waits instead and is raised the
        /// first tick after, if the player is still at the lock.
        static bool CuesHeld()
        {
            try { return PlaylistObjectiveCallout.HoldOff != null && PlaylistObjectiveCallout.HoldOff(); } catch { return false; }
        }

        void TickGateNear(PlaylistRunner runner)
        {
            if (CuesHeld()) return;
            var head = PlaylistObjectiveCallout.Head();
            if (head == null) return;
            double now = Time.timeAsDouble;
            foreach (var e in _gates.Values)
            {
                if (!e.held || e.root == null || e.remaining == null || e.remaining.Count == 0) continue;
                if (!PlaylistRoundRules.Near(e.centre, e.rot, e.size + Vector2.one * (2f * PlaylistLockField.Pad), head.position)) continue;
                string key = string.Join("|", e.remaining);
                bool changed = _gateCueKey.TryGetValue(e.stop.occurrenceId, out var last) && last != key;
                if (!changed && _gateCueKey.ContainsKey(e.stop.occurrenceId) && !PlaylistRoundRules.CueDue(e.lastCue, now)) continue;
                if (!changed && _gateCueKey.ContainsKey(e.stop.occurrenceId)) { e.lastCue = now; _gateCueKey[e.stop.occurrenceId] = key; }
                else { e.lastCue = now; _gateCueKey[e.stop.occurrenceId] = key; }
                var j = new JSONObject();
                j.AddField("playerId", PlayerPresence.MyId ?? "");
                j.AddField("stopId", e.stop.occurrenceId ?? "");
                j.AddField("stopName", PlaylistObjectiveText.Name(e.stop));
                var rem = new JSONObject(JSONObject.Type.Array); foreach (var n in e.remaining) rem.Add(n);
                j.AddField("remaining", rem);
                j.AddField("line", PlaylistRoundRules.LockedSayLine(e.remaining));
                j.AddField("personal", true);
                runner.Cue("stop_locked", e.stop, j);
            }
        }

        void ReleaseGates()
        {
            foreach (var e in _gates.Values)
            {
                if (e.held && e.root != null) GamePause.ReleaseScope(e.root);
                if (e.field != null) Destroy(e.field.gameObject);
            }
            _gates.Clear();
            _gateCueKey.Clear();
        }

        void Apply(PlaylistRunner runner, IPlaylistStage stage)
        {
            var plan = runner.Plan;
            string run = (plan.runId ?? plan.playlistId) + "/" + plan.instanceId;
            if (_run != run) { ReleaseAll(); _run = run; }
            _runner = runner;
            bool roundChanged = _leg != plan.legIndex;
            bool open = PlaylistRoundRules.Open(plan);   // a club: no holds, no fields
            if (roundChanged || Time.time >= _nextScan)
            {
                _nextScan = Time.time + 0.5f;
                foreach (var (s, leg, state) in PlaylistRoundRules.Stops(plan))
                {
                    if (!_stops.TryGetValue(s.occurrenceId, out var e)) _stops[s.occurrenceId] = e = new Entry { stop = s, leg = leg, state = state };
                    e.stop = s; e.leg = leg;
                    if (e.root == null)
                    {
                        e.root = stage.StopRoot(runner, s);
                        e.held = false;
                        if (e.root != null) Measure(e);
                    }
                    if (e.root == null) continue;
                    bool awaken = e.state != state && state == PlaylistRoundRules.Round.Current && e.state == PlaylistRoundRules.Round.Future;
                    e.state = state;
                    if (open) Open(e); else Show(e, awaken);
                }
                if (roundChanged && open) ClubRound(runner, stage, plan);
                if (roundChanged)
                {
                    _leg = plan.legIndex;
                    Debug.Log($"[Playlist] rounds: round {_leg + 1}/{plan.legs.Count} plays; {HeldCount} stops held, {FieldCount} locked");
                }
            }
            TickNear(runner);
        }

        // ── A CLUB (locks "open", playlist-packer Oct 19) ──────────────────
        // Courts stay idle and open: never held, never fenced. A stop with no court of its own (standing:false)
        // stands FOR ITS ROUND: on free floor, or on another court's floor (`displaces`: those courts hide). The
        // next round puts the courts back. The gate never yields (the packer never names it).
        readonly List<GameObject> _hiddenCourts = new List<GameObject>();
        readonly Dictionary<string, GameObject> _visitors = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        readonly Dictionary<string, GameObject> _parked = new Dictionary<string, GameObject>(StringComparer.Ordinal);   // visitors off duty (hidden)

        /// A court yields its floor / a visitor leaves (core: bounce-out + its row leaves the saved layout, so
        /// headsets following the Location see the same), and comes back. Defaults: plain hide / show.
        public static Action<GameObject> Yield = go => { if (go != null) go.SetActive(false); };
        public static Action<GameObject> Return = go => { if (go != null) go.SetActive(true); };

        void Open(Entry e)
        {
            if (e.held) { GamePause.ReleaseScope(e.root); e.held = false; }
            if (e.field != null) { Destroy(e.field.gameObject); e.field = null; }
        }

        void ClubRound(PlaylistRunner runner, IPlaylistStage stage, PlaylistRunPlan plan)
        {
            var current = plan.Current?.stops ?? new List<PlaylistRunStop>();
            // Last round's visitors leave (kept hidden: the stage shows them again if a later round needs them).
            var leaving = new List<string>();
            foreach (var kv in _visitors)
                if (!current.Exists(s => s != null && !s.standing && s.occurrenceId == kv.Key)) leaving.Add(kv.Key);
            foreach (string occ in leaving)
            {
                var v = _visitors[occ];
                _visitors.Remove(occ);
                if (v != null) { Yield(v); _parked[occ] = v; }
            }
            foreach (var court in _hiddenCourts) if (court != null) Return(court);
            _hiddenCourts.Clear();
            foreach (var s in current)
            {
                if (s == null || s.standing || string.IsNullOrEmpty(s.occurrenceId)) continue;
                if (!string.IsNullOrEmpty(s.displaces))
                    foreach (string id in s.displaces.Split(','))
                    {
                        var court = stage.StopRoot(runner, new PlaylistRunStop { occurrenceId = id.Trim() });
                        if (court != null) { Yield(court); _hiddenCourts.Add(court); }
                    }
                var here = stage.StopRoot(runner, s);
                if (here == null && _parked.TryGetValue(s.occurrenceId, out var back) && back != null) { Return(back); here = back; }
                _parked.Remove(s.occurrenceId);
                if (here != null) { _visitors[s.occurrenceId] = here; continue; }
                var stop = s;
                _visitors[s.occurrenceId] = null;
                stage.StreamIn(runner, null, stop, root =>
                {
                    if (this == null) return;
                    if (_visitors.ContainsKey(stop.occurrenceId)) _visitors[stop.occurrenceId] = root;
                    else if (root != null) { Yield(root); _parked[stop.occurrenceId] = root; }   // its round already ended
                    Debug.Log($"[Playlist] club: {stop.attraction} {(root != null ? "stands" : "could not stand")} for this round"
                        + (string.IsNullOrEmpty(stop.displaces) ? "" : $" on {stop.displaces}'s floor"));
                });
            }
        }

        void Show(Entry e, bool awaken)
        {
            bool hold = PlaylistRoundRules.Held(e.state);
            if (hold && !e.held) { GamePause.HoldScope(e.root); e.held = true; }
            else if (!hold && e.held) { GamePause.ReleaseScope(e.root); e.held = false; }
            switch (e.state)
            {
                case PlaylistRoundRules.Round.Future:
                    if (e.field == null || e.field.Cleared || e.field.Dissolving)
                    {
                        if (e.field != null && !e.field.Dissolving) Destroy(e.field.gameObject);
                        e.field = PlaylistLockField.Locked(transform, e.centre, e.rot, e.size, e.height);
                    }
                    break;
                case PlaylistRoundRules.Round.Current:
                    if (e.field != null && !e.field.Dissolving)
                    {
                        if (awaken || !e.field.Cleared) e.field.Dissolve(); else Destroy(e.field.gameObject);
                        e.field = null;
                    }
                    break;
                case PlaylistRoundRules.Round.Done:
                    if (e.field == null || !e.field.Cleared)
                    {
                        if (e.field != null && !e.field.Dissolving) e.field.Dissolve();
                        e.field = PlaylistLockField.Done(transform, e.centre, e.rot, e.size, e.height);
                    }
                    break;
            }
        }

        // Walk up to a locked stop → `stop_locked` (this device's player; once per stop per cooldown).
        void TickNear(PlaylistRunner runner)
        {
            if (PlaylistRoundRules.Open(runner.Plan)) return;   // a club: nothing is for later
            if (CuesHeld()) return;
            var head = PlaylistObjectiveCallout.Head();
            if (head == null) return;
            double now = Time.timeAsDouble;
            foreach (var e in _stops.Values)
            {
                if (e.state != PlaylistRoundRules.Round.Future || e.root == null) continue;
                if (!PlaylistRoundRules.Near(e.centre, e.rot, e.size + Vector2.one * (2f * PlaylistLockField.Pad), head.position)) continue;
                if (!PlaylistRoundRules.CueDue(e.lastCue, now)) continue;
                e.lastCue = now;
                var plan = runner.Plan;
                var j = new JSONObject();
                j.AddField("playerId", PlayerPresence.MyId ?? "");
                j.AddField("stopId", e.stop.occurrenceId ?? "");
                j.AddField("stopName", PlaylistObjectiveText.Name(e.stop));
                j.AddField("round", e.leg + 1);
                var rw = PlaylistRunRules.RoundWord(plan);
                j.AddField("roundWord", rw.one);
                var part = plan.Leg(e.leg);
                string partName = !string.IsNullOrWhiteSpace(part?.name) ? part.name
                    : part?.stops != null && part.stops.Count > 0 && part.stops[0] != null ? PlaylistObjectiveText.Name(part.stops[0]) : "";
                j.AddField("roundLabel", PlaylistRoundRules.Marker(e.leg, rw.one, partName));   // "Race 3", else the part's name
                j.AddField("roundName", plan.Leg(e.leg)?.name ?? "");
                j.AddField("currentRound", plan.legIndex + 1);
                j.AddField("currentRoundName", plan.Current?.name ?? "");
                j.AddField("roundsUntil", PlaylistRoundRules.RoundsUntil(e.leg, plan.legIndex));
                j.AddField("line", PlaylistRoundRules.LockedLine(plan));
                j.AddField("personal", true);
                runner.Cue("stop_locked", e.stop, j);
            }
        }

        // The attraction's footprint: its template's runtime footprint, else its renderers' bounds.
        static void Measure(Entry e)
        {
            var at = e.root.GetComponentInChildren<AttractionTemplate>(true);
            Bounds b = default; bool any = false;
            foreach (var r in e.root.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (at != null)
            {
                e.centre = at.transform.position;
                e.rot = Quaternion.Euler(0f, at.transform.eulerAngles.y, 0f);
                e.size = at.RuntimeFootprintMeters;
            }
            else
            {
                e.centre = any ? new Vector3(b.center.x, b.min.y, b.center.z) : e.root.transform.position;
                e.rot = Quaternion.identity;
                e.size = any ? new Vector2(b.size.x, b.size.z) : Vector2.one;
            }
            // The field stands ON the attraction's own floor (its template / root): never at its lowest renderer — mole
            // holes, pits and below-floor content (or an attraction still rising in) sank the whole field underground.
            e.centre.y = at != null ? at.transform.position.y : e.root.transform.position.y;
            e.height = any ? Mathf.Max(0.5f, b.max.y - e.centre.y) : 1.5f;
        }

        void ReleaseAll(bool teardown = false)
        {
            foreach (var e in _stops.Values)
            {
                if (e.held && e.root != null) GamePause.ReleaseScope(e.root);
                if (e.field != null) Destroy(e.field.gameObject);
            }
            _stops.Clear();
            _leg = -1;
            // A new run on the same club (Play again / another playlist): the courts come back, visitors leave.
            // Not on teardown (the package is going; its rows go with it).
            if (!teardown)
            {
                foreach (var kv in _visitors) if (kv.Value != null) { Yield(kv.Value); _parked[kv.Key] = kv.Value; }
                foreach (var court in _hiddenCourts) if (court != null) Return(court);
            }
            _visitors.Clear();
            _hiddenCourts.Clear();
        }

        void OnDestroy() { ReleaseAll(true); ReleaseGates(); }
    }
}
