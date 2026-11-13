// ─────────────────────────────────────────────────────────────────────
//  PlaylistRun.cs — ONE playlist run across all its rounds (repair-oct20 CONTRACT § 1–§ 4).
//
//  A DETACHED SharedState "dp.playlist.run/{runId}" (like PlaylistScoreboard: the session bus,
//  no package — it outlives every round's package), grown from the per-round PlaylistRunner
//  (PlaylistAuthority / PlaylistPlanBridge attach those from the plan). It holds:
//    rec      the rev-pinned record {id, rev, title, name, t0}
//    leg      the round as the leader sees it {i, id, ph}
//    rs       run.state (the playlist Lua's)
//    g.{occ}  a stop's goal block {k: key, s: source, st: stop.state, tm: {id: [due, every]}, r: result}
//    res.{occ} a stop's result fields     mk.{occ} its markers     jp.{occ} its jump     jr.{occ} a jump reset
//    lk.{occ} a playlist lock {needs, sign}     f.{name} run flags     bd badges {player|all: [ids]}
//    sub      the event sources goal blocks listen to (followers forward only those)
//    rd.{dev} per-device readiness {r, t, dl, pre}     adv  the round allowed to advance {i}
//    nx       choose_next's pick     cap  {over, why}     log  the run log (last 24)
//    ul.{occ} a stop's unlock was announced (stop_unlocked, once per stop: native lockedUntil or Lua)
//  Record v2 (CONTRACT § 8): the run PINS the bundle it started with (record + goal-block revs, logged in
//  rec.revs) and never refetches mid-run; `lockedUntil` is enforced natively (any result of a needed stop
//  opens it, `stop_unlocked` fires here); a plan whose `requiresRelease` is newer than the install is refused.
//  Leader only (SessionEngine.PickLeader via SharedState): goal blocks + playlist Lua run here
//  (PlaylistLuaRuntime), events from every device arrive as "ev" requests, Dreamie moments as "dm".
//  Everything the leader decides is written here and rendered on every device, so a hand-over
//  resumes mid-goal (stop.state + absolute timer deadlines).
//
//  Engine-free core: a PlaylistRunWorld carries the hooks (clock, device, SharedState factory,
//  Lua env, census / edge probes …) so Tools/playlist-run-offline plays 2–3 fake devices, each
//  with its own world, SharedState engine and XLua VM. Unity's world is PlaylistRunWorld.Main
//  (PlaylistRunHost ticks it).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Defective.JSON;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// One round of the run on this device (PlaylistRunner implements it; the offline sim fakes it).
    public interface IPlaylistLeg
    {
        PlaylistRunPlan Plan { get; }
        PlaylistRunRow Row { get; }
        IReadOnlyDictionary<string, PlaylistStopProgress> Progress { get; }
        /// The stop is live here (standing / streamed in) — its goal block may start.
        bool IsLive(PlaylistRunStop s);
        GameObject StopRoot(PlaylistRunStop s);
        void ApplyGoalProgress(string occ, double n, double of, string unit);
        /// done | failed | timed_out | skip
        void ApplyGoalOutcome(string occ, string kind);
        void SkipLeg();
        /// run.finish{result} (§ 3.3): the run ends now (the leg → complete; the end screens follow).
        void Finish(string result);
        /// run.add / run.remove (§ 3.1) into the STANDING set: these stops join it (each streams in on its own seat) / leave it
        /// (retired with the stage's transition). Every other stop keeps its rect, its state and keeps playing.
        void SetStops(List<PlaylistRunStop> add, List<string> remove);
        bool SetFlag(string name, JSONObject value);
        bool Alive { get; }
    }

    /// The hooks of one device's runs (Unity: Main; the offline sim: one per fake device).
    /// One assigned waypoint (PLAYLIST-PLAN § 6.7): the map's done / not-done mark and Dreamie's guidance set.
    public sealed class PlaylistWaypoint
    {
        /// The ref run.waypoint named (a pool id, else the attraction's leaf name).
        public string reference;
        /// The attraction's leaf name (A_CoinGarden).
        public string attraction;
        /// Its standing stop on this device (the map pin's anchor), null when it is not out now.
        public string occurrenceId;
        public bool done;
        /// won | skipped | announced | map (a dp.map / run.map mark set done) — null while not done.
        public string how;
    }

    public sealed partial class PlaylistRunWorld
    {
        public static readonly PlaylistRunWorld Main = new PlaylistRunWorld();

        public Action<string> Log = m => Debug.Log(m);
        public Func<double> Clock = () => PlaylistRunner.Clock();
        public Func<string> DeviceId = () => PlayerPresence.MyId ?? "";
        public Func<string, SharedState> OpenState = name => SharedState.Open(name);
        public Func<LuaEnv> Lua = () => PlaylistLuaRuntime.DefaultEnv();
        /// census on the leader: the live matching objects under the stop root.
        public Func<PlaylistRunStop, GameObject, PlaylistCensusFilter, int> Census = (s, root, f) => PlaylistCensus.Count(root, f);
        /// stop.count{kind}: the announce list's {total, remaining, done, by} under the stop root (JSON) — pl-announce.
        /// A broken challenge's report (play_events body {events:[stop_result broken + why]}) → the outbox (Web play_events).
        public Action<string> ReportBroken = body => PlaylistOutbox.Enqueue("play_events", body);
        /// stop.jump{} with no pads: the pads the attraction authored for a chain ("" = its default). Core: PlaylistJumpPads.
        public Func<PlaylistRunStop, GameObject, string, int> JumpAuthored = (s, root, chain) => root != null ? PlaylistJumpPads.AuthoredCount(root, chain) : 0;
        /// stop.jump{}'s live chain (sal-jumppads): (stop, root, chain, the jump spec) → (pads, fellBack, why). Null = the default:
        /// the given pads, else the attraction's authored chain (JumpAuthored); fewer than 2 → it runs as a crossing.
        /// pl-announce's start-of-jump validation (PlaylistJumpTracker JumpChain) plugs in here.
        public Func<PlaylistRunStop, GameObject, string, string, (int pads, bool fellBack, string why)> JumpChain;
        public Func<PlaylistRunStop, GameObject, string, string> Count = (s, root, kind) => PlaylistAnnounce.CountJson(root, string.IsNullOrEmpty(kind) ? null : kind);
        /// stop.edge: the stop-local position of its start / far edge (null = unknown).
        public Func<PlaylistRunStop, GameObject, string, Vector3?> Edge = (s, root, which) => PlaylistGoalSpace.Edge(root, which);
        /// stop.request: a title's existing SharedState request on the stop's bus.
        public Action<PlaylistRunStop, GameObject, string, string, string> Request = (s, root, table, key, json) =>
        {
            var ss = SharedState.Open(table, null, root);
            ss?.Request(key, string.IsNullOrEmpty(json) ? "null" : json);
        };
        /// The director accepts a moment (Unity: DreamieMoments.Accept).
        public Action<DreamieMoment> MomentSink = m => DreamieMoments.Accept(m);
        public Func<List<(string id, string name)>> Players = PlaylistRunDefaults.Players;
        public Func<bool> ServerReachable = () => Application.internetReachability != NetworkReachability.NotReachable;
        public Func<bool> PhonePresent = PlaylistRunDefaults.PhonePresent;
        /// This device's readiness for a round: (downloaded, preloaded). Default: ready.
        public Func<PlaylistRunPlan, int, (bool downloaded, bool preloaded)> RoundReady = (p, i) => (true, true);
        public Action<PlaylistRunPlan, int> PreloadRound = (p, i) => { };
        public Action<PlaylistRunPlan, int> UnloadRound = (p, i) => { };
        /// LAN: the leader starts the next round itself (core: PlaylistAuthority packs it). (plan, next leg index, its id).
        public Action<PlaylistRunPlan, int, string> AdvanceLan = (p, i, id) => { };
        /// The queue item's own minutes for this round (venue override), 0 = none.
        public Func<double> QueueRoundMinutes = () => 0;
        /// Award a badge to THIS device's player (core: the profile API, else the outbox).
        public Action<string, string> AwardBadge = PlaylistRunDefaults.AwardBadge;
        /// The backend's copy of the playlist for a round that just attached (online wins; cached for offline).
        /// Default: PlaylistBundle (cache first, then an ETag fetch); null = the manifest's copy only (the offline sim).
        public Action<PlaylistRun, IPlaylistLeg> Bundle = PlaylistRunDefaults.Bundle;
        /// Save the run's offline cache (null = PlaylistRunCache's file).
        public Action<string, string> CacheWrite = (runId, json) => PlaylistRunCache.Write(json);
        /// Record v2 § 8.2: the installed release (version number) of a title on this device; ≤ 0 = unknown (allowed).
        /// Core: ContentPanel's installed version.
        public Func<string, int> InstalledRelease = id => -1;
        /// § 3.2a: a runtime → game challenge event delivered to the attraction's own objects on THIS device
        /// (stop, its live root or null, name, data JSON). Core: DreamParkLuaPlaylist (onevent / dp.on).
        public Action<PlaylistRunStop, GameObject, string, string> Deliver = (s, root, name, json) => PlaylistRunDefaults.Deliver(s, root, name, json);
        /// § 3.1: the rooms this space has (run.space().rooms; the batches helper's N). Core: the packer's room graph.
        public Func<int> SpaceRooms = () => 1;
        /// § 3.1 run.add into the standing set (leader): seat each added stop in a room the set does not use (an unchanged
        /// rect keeps its attraction) — rectId / roomId / seat / resourceAddress filled. Returns the stops that got a place
        /// (the rest are refused). Core: PlaylistPlanBridge (PlaylistPacker.PackSet with the set's rooms excluded).
        public Func<PlaylistRunPlan, List<PlaylistRunStop>, List<PlaylistRunStop>> PlaceAdded = (plan, add) => add;
        /// run.finish{next} (pl-sets): the record of another playlist of a title, from what this device has OFFLINE — the cached
        /// backend bundle, else (core) the installed title manifest. Null = unknown here.
        public Func<string, string, PackagePlaylist> FindPlaylist = PlaylistRunDefaults.FindPlaylist;
        /// run.finish{next}: start that playlist now on this device (the leader, no phone queue): (the run that ended, the next
        /// record, the new run's id). Core: PlaylistAuthority.StartPlaylist (the PACK_PARK path of LAN advance / offline resume).
        public Action<PlaylistRunPlan, PackagePlaylist, string> StartPlaylist = (from, rec, runId) => { };
        /// run.finish{next} started here: the new run's id (ended run id, next playlist id). Core (PlaylistNextQueue, Oct 23
        /// pl-sync-next): a `pl-…` id the location's queue can carry, and the queue told. null = the run id + "&gt;" + the next's id.
        public Func<string, string, string> NextRunId;
        /// § 3.4: does THIS device's account hold a badge (requires.badges)? Core: ProfileAPI.HasBadge.
        public Func<string, bool> HoldsBadge = PlaylistRunDefaults.HoldsBadge;
        /// Every badge THIS device's account holds (its "bh.{device}" row: run.can_play answers from the group's rows at once).
        /// Core: ProfileAPI.Badges.
        public Func<IEnumerable<string>> HeldBadges = PlaylistRunDefaults.HeldBadges;
        /// A plan this device refused to run (record v2 § 8.2): (plan, reason).
        public event Action<PlaylistRunPlan, string> Refused;
        public string LastRefusal { get; private set; }

        /// Record + announce a refusal (the log line says why; Refused listeners show it).
        public void Refuse(PlaylistRunPlan plan, string why)
        {
            LastRefusal = why;
            Log?.Invoke("[pl-runtime] REFUSED " + PlaylistRun.RunKey(plan) + ": " + why);
            try { Refused?.Invoke(plan, why); } catch (Exception e) { Log?.Invoke("[pl-runtime] refused listener threw: " + e.Message); }
        }

        /// Why this device must not run the plan (requiresRelease newer than the install), or null.
        public string RefusalFor(PlaylistRunPlan plan)
        {
            if (plan == null) return null;
            int installed = -1;
            try { installed = InstalledRelease?.Invoke(plan.contentId) ?? -1; } catch (Exception) { }
            string why = PlaylistRunRules.ReleaseRefusal(plan, installed);
            if (why != null) return why;
            // the backend said so (bundle 409 RELEASE_TOO_OLD for this install): its goal blocks need newer content
            int need = PlaylistBundle.TooOld(plan.contentId, plan.playlistId, installed);
            return need > 0 ? PlaylistRunRules.ReleaseRefusal(new PlaylistRunPlan { playlistId = plan.playlistId, name = plan.name, contentId = plan.contentId, requiresRelease = need }, installed) : null;
        }

        internal readonly Dictionary<string, PlaylistRun> runs = new Dictionary<string, PlaylistRun>(StringComparer.Ordinal);
        public IEnumerable<PlaylistRun> Runs => runs.Values;

        public PlaylistRun Find(string runId) => runId != null && runs.TryGetValue(runId, out var r) ? r : null;

        /// A run this device has no round of yet (a LATE JOINER, pl-sets): open its shared table without a leg, so the run's
        /// `lan` (the set / round the leader is on) and `set` sync here and the device can attach the round that stands.
        /// The first Attach of that run reuses it. Null runId = null.
        public PlaylistRun Watch(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return null;
            if (runs.TryGetValue(runId, out var run) && !run.Disposed) return run;
            run = new PlaylistRun(this, runId);
            runs[runId] = run;
            Log?.Invoke($"[pl-runtime] {runId}: watching the run (no round here yet) — its current set / round syncs from the leader");
            return run;
        }

        /// The run a leg belongs to (created on first attach).
        public PlaylistRun Attach(IPlaylistLeg leg)
        {
            if (leg?.Plan == null) return null;
            string why = RefusalFor(leg.Plan);
            if (why != null) { Refuse(leg.Plan, why); return null; }
            string id = PlaylistRun.RunKey(leg.Plan);
            if (!runs.TryGetValue(id, out var run) || run.Disposed)
            {
                run = new PlaylistRun(this, id);
                runs[id] = run;
            }
            run.AddLeg(leg);
            return run;
        }

        public void Detach(IPlaylistLeg leg)
        {
            foreach (var r in runs.Values) r.RemoveLeg(leg);
        }

        /// An event seen on this device → every run it may belong to.
        public void Ingest(PlaylistEvent e)
        {
            if (e == null) return;
            foreach (var r in runs.Values) if (!r.Disposed) r.Enqueue(e);
        }

        /// Any run here listens to this source (cheap check before a tap builds an event).
        public bool Wants(string source)
        {
            foreach (var r in runs.Values) if (!r.Disposed && r.Wants(source)) return true;
            return false;
        }

        public void Tick()
        {
            double now = Clock();
            List<string> dead = null;
            // A snapshot: a run's tick may start another run (run.finish{next} → Attach).
            foreach (var kv in new List<KeyValuePair<string, PlaylistRun>>(runs))
            {
                if (kv.Value.Disposed) { (dead ??= new List<string>()).Add(kv.Key); continue; }
                try { kv.Value.Tick(now); }
                catch (Exception e) { Log?.Invoke("[pl-runtime] run tick threw: " + e); }
            }
            if (dead != null) foreach (var k in dead) runs.Remove(k);
        }

        /// The pack of a set found no room for some of its stops (PlaylistPlanBridge): (occurrence, attraction, why); nothingPlaced
        /// = none of the set placed (no round will attach). The run's leader skips them instead of waiting (PlaylistRun).
        public void SetUnplaced(string runId, string setId, List<(string occ, string attraction, string why)> stops, bool nothingPlaced)
        {
            var r = Find(runId);
            if (r == null || r.Disposed) { Log?.Invoke($"[pl-runtime] {setId}: unplaced stops for an unknown run '{runId}' — ignored"); return; }
            r.OnSetUnplaced(setId, stops, nothingPlaced);
        }

        /// § 6.7 waypoints of every live run on this device (the map: only these get done / not-done marks).
        public List<PlaylistWaypoint> Waypoints()
        {
            var l = new List<PlaylistWaypoint>();
            foreach (var r in runs.Values) if (r != null && !r.Disposed) l.AddRange(r.Waypoints);
            return l;
        }

        /// The run whose current round contains this stop (or the only run).
        public PlaylistRun ForStop(string occ)
        {
            PlaylistRun only = null; int n = 0;
            foreach (var r in runs.Values)
            {
                if (r.Disposed) continue;
                n++; only = r;
                if (occ != null && r.HasStop(occ)) return r;
            }
            return n == 1 ? only : null;
        }
    }

    public sealed partial class PlaylistRun : IPlaylistGoalHost
    {
        public const string StateName = "dp.playlist.run/";
        public const double ReadyGraceSeconds = 20.0;
        public const int MaxLog = 24;
        public const double ForwardPerSecond = 30;
        /// Seconds without any progress anywhere in a playing round before Dreamie's `idle` moment.
        public const double IdleSeconds = 90.0;

        /// The SharedState's leader runs Lua; keep the same key the scoreboard uses.
        public static string RunKey(PlaylistRunPlan p) => !string.IsNullOrEmpty(p?.runId) ? p.runId : "pl-" + (p?.playlistId ?? "");

        // World-replicated sources: every device sees them, so the leader uses its own observation (never forwarded).
        static readonly HashSet<string> LocalOnly = new HashSet<string>(StringComparer.Ordinal)
            { PlaylistEvent.State, PlaylistEvent.Gone, PlaylistEvent.Enemy };

        readonly PlaylistRunWorld _w;
        public string RunId { get; }
        public SharedState State { get; private set; }
        public bool Disposed { get; private set; }
        public int Version { get; private set; }

        readonly List<IPlaylistLeg> _legs = new List<IPlaylistLeg>();
        readonly Queue<PlaylistEvent> _pending = new Queue<PlaylistEvent>();
        readonly PlaylistEventDedupe _dedupe = new PlaylistEventDedupe();
        int _ssVersion = -1;

        // Local mirrors of the shared table (every device).
        readonly HashSet<string> _subs = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _goalDriven = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _marks = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _jumps = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _jumpResets = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _locks = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _badgesAwarded = new HashSet<string>(StringComparer.Ordinal);
        int _advance = -1;
        bool _over;

        // Leader-local.
        PlaylistLuaRuntime _rt;
        sealed class Block { public string key, source; public int timerCount; public double startedAt; public bool platformTimers; }
        readonly Dictionary<string, Block> _blocks = new Dictionary<string, Block>(StringComparer.Ordinal);
        readonly HashSet<string> _noBlock = new HashSet<string>(StringComparer.Ordinal);
        bool _wasLeader;
        int _legSeen = -1;
        string _phaseSeen;
        double _legDoneAt = -1, _lastProgressAt = -1, _nextReady, _nextCache;
        int _forwardBudget; double _forwardWindow;
        bool _runLuaTried;
        readonly HashSet<string> _onceLocal = new HashSet<string>(StringComparer.Ordinal);

        internal PlaylistRun(PlaylistRunWorld w, string runId)
        {
            _w = w;
            RunId = runId;
            try
            {
                State = w.OpenState(StateName + runId);
                if (State != null) State.Requested += OnRequested;
                // The rig is the gate (Oct 23 2026, platform-rig): a spectator phone never leads the run or forwards its
                // events; it still sends Dreamie moments.
                if (State != null) { State.MarkGameplay(); State.AllowSpectator("dm"); }
            }
            catch (Exception e) { State = null; Log("no shared state (solo): " + e.Message); }
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _rt?.EndAll(RunId);
            _rt?.EndRun(RunId);
            if (State != null) { State.Requested -= OnRequested; try { State.Dispose(); } catch (Exception) { } State = null; }
        }

        public bool IsLeader => State == null || State.IsLeader;
        public bool Synced => State == null || State.Synced;
        public string LeaderId => State?.LeaderId ?? _w.DeviceId();
        public IReadOnlyList<IPlaylistLeg> Legs => _legs;

        internal void AddLeg(IPlaylistLeg leg)
        {
            if (_legs.Contains(leg)) return;
            _legs.Add(leg);
            _played = true;
            Version++;
            // A run that started keeps its bundle: a later round gets the PINNED record, no refetch (§ 8.9).
            if (Started)
            {
                if (_bundle != null && PlaylistBundle.Overlay(leg.Plan, _bundle)) Log($"round {leg.Plan.legIndex + 1} plays the pinned record rev {leg.Plan.rev} (no refetch mid-run)");
                return;
            }
            try { _w.Bundle?.Invoke(this, leg); } catch (Exception e) { Log("bundle hook threw: " + e.Message); }
        }

        // ── The pinned bundle (record v2 § 8.9) ────────────────────────
        JSONObject _bundle;
        Dictionary<string, (string lua, string rev)> _bundleGoals;
        string _bundleTitle;
        readonly Dictionary<string, Dictionary<string, (string lua, string source)>> _librarySnap = new Dictionary<string, Dictionary<string, (string, string)>>(StringComparer.OrdinalIgnoreCase);

        /// The backend bundle this run plays (before it starts: the newest applied; after: pinned for the whole run).
        public JSONObject Bundle => _bundle;

        /// A bundle for this run (cached or fresh). Before the run starts it overlays every round attached here and
        /// becomes the run's copy; once the run started a different bundle is never applied ("library edits apply from
        /// the next run"). True when applied.
        public bool ApplyBundle(IPlaylistLeg leg, JSONObject bundle, string why)
        {
            if (bundle == null) return false;
            if (Started)
            {
                if (bundle != _bundle)
                    Log($"bundle rev {PlaylistBundle.Rev(bundle)} ({why}) arrived after the run started on rev {PinnedRev ?? "manifest"}: not applied (it plays from the next run)");
                return false;
            }
            // A record that needs a newer release than this install (§ 8.2) never replaces the installed copy.
            var rec = bundle.GetField("playlist");
            var probe = new PlaylistRunPlan { playlistId = leg?.Plan?.playlistId, name = leg?.Plan?.name, contentId = leg?.Plan?.contentId,
                requiresRelease = (int)PlaylistRunPlan.Num(rec, "requiresRelease") };
            string refusal = _w.RefusalFor(probe);
            if (refusal != null) { Log($"bundle rev {PlaylistBundle.Rev(bundle)} ({why}) not applied: {refusal} (playing the installed copy)"); AddLog("bundle_refused", null, refusal); return false; }
            bool any = false;
            foreach (var l in _legs.Count > 0 ? _legs : new List<IPlaylistLeg> { leg })
                if (l?.Plan != null && PlaylistBundle.Overlay(l.Plan, bundle)) any = true;
            if (!any) return false;
            _bundle = bundle;
            _bundleTitle = PlaylistGoalLibrary.BaseTitle(PlaylistRunPlan.Str(bundle, "titleId", leg?.Plan?.contentId));
            _bundleGoals = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            var goals = bundle.GetField("goals");
            if (goals?.keys != null)
                for (int i = 0; i < goals.keys.Count; i++)
                {
                    var v = goals.list[i];
                    string lua = v == null ? null : v.type == JSONObject.Type.String ? v.stringValue : PlaylistRunPlan.Str(v, "lua", null);
                    var rv = v?.GetField("rev");
                    string rev = rv == null ? "" : rv.type == JSONObject.Type.String ? rv.stringValue : rv.Print();
                    if (!string.IsNullOrWhiteSpace(lua)) _bundleGoals[goals.keys[i]] = (lua, rev);
                }
            Log($"bundle rev {PlaylistBundle.Rev(bundle)} ({why}): {_bundleGoals.Count} goal block(s) — pinned once the run starts");
            return true;
        }

        /// The pinned goal block for a key (the run's bundle, else the title library frozen at its first use in this run).
        (string lua, string source)? PinnedGoal(string title, string key)
        {
            string t = PlaylistGoalLibrary.BaseTitle(title);
            if (_bundleGoals != null && string.Equals(t, _bundleTitle, StringComparison.OrdinalIgnoreCase) && _bundleGoals.TryGetValue(key, out var b))
                return (b.lua, PlaylistGoalLibrary.SourceBackend);
            if (!_librarySnap.TryGetValue(t, out var snap)) _librarySnap[t] = snap = PlaylistGoalLibrary.Snapshot(t);
            return snap.TryGetValue(key, out var x) ? x : ((string, string)?)null;
        }

        /// rec.revs: the bundle's revs {playlist, block, guide, goals{key: rev}} (or the manifest copy's).
        JSONObject RevsJson(PlaylistRunPlan p)
        {
            var revs = _bundle?.GetField("revs");
            if (revs != null && revs.type == JSONObject.Type.Object) return new JSONObject(revs.Print());
            var j = new JSONObject();
            j.AddField("playlist", !string.IsNullOrEmpty(p?.rev) ? p.rev : "manifest");
            if (_bundleGoals != null && _bundleGoals.Count > 0)
            {
                var g = new JSONObject();
                foreach (var kv in _bundleGoals.OrderBy(x => x.Key, StringComparer.Ordinal)) g.AddField(kv.Key, kv.Value.rev ?? "");
                j.AddField("goals", g);
            }
            j.AddField("source", _bundle != null ? "backend" : "manifest");
            return j;
        }

        /// The run's pinned record revision (rec.rev, written when it started), or null before it started / unknown.
        public string PinnedRev
        {
            get
            {
                var rec = SharedState.ParseFragment(State?.GetJson("rec"));
                string r = rec != null ? PlaylistRunPlan.Str(rec, "rev", null) : null;
                return string.IsNullOrEmpty(r) ? null : r;
            }
        }
        public bool Started => State != null && State.Has("rec");
        internal void RemoveLeg(IPlaylistLeg leg) { if (_legs.Remove(leg)) Version++; }

        /// The round this device plays now: the newest live leg.
        public IPlaylistLeg Current
        {
            get
            {
                IPlaylistLeg best = null;
                for (int i = _legs.Count - 1; i >= 0; i--)
                {
                    var l = _legs[i];
                    if (l == null || !l.Alive || l.Plan == null) { _legs.RemoveAt(i); continue; }
                    if (best == null || l.Plan.legIndex > best.Plan.legIndex) best = l;
                }
                return best;
            }
        }

        public PlaylistRunPlan Plan => Current?.Plan;

        internal bool HasStop(string occ) => Current?.Plan?.Current?.stops?.Exists(s => s?.occurrenceId == occ) == true;

        // ── What every device reads ───────────────────────────────────

        /// A goal block owns this stop: the title's dp.playlist calls and the auto signals are events only.
        public bool GoalDriven(string occ) { Read(); return occ != null && _goalDriven.Contains(occ); }
        /// The run's playlist lock on a stop (run.lock), or null.
        public (List<string> needs, string sign)? LockOf(string occ)
        {
            Read();
            if (occ == null) return null;
            if (!_locks.TryGetValue(occ, out var j))
            {
                // A lock by ref (run.lock before its set stood, or the set's own `lock`): the stop's id or attraction.
                var st = Current?.Plan?.FindStop(occ, out _);
                if (st == null || (!(!string.IsNullOrEmpty(st.stopId) && _locks.TryGetValue(st.stopId, out j))
                    && !_locks.TryGetValue(PlaylistAutoRules.Leaf(st.attraction) ?? "", out j))) return null;
            }
            var o = SharedState.ParseFragment(j);
            var needs = new List<string>();
            var nj = o?.GetField("needs");
            if (nj?.list != null) foreach (var x in nj.list) if (x != null && x.type == JSONObject.Type.String) needs.Add(x.stringValue);
            return (needs, PlaylistRunPlan.Str(o, "sign", null));
        }
        public bool HasLocks { get { Read(); return _locks.Count > 0; } }
        /// The round the leader advanced to on LAN (the queue did not): (legIndex, legId, playlistId, legCount, owner), or null.
        public (int legIndex, string legId, string playlistId, int legCount, string owner)? LanRound
        {
            get
            {
                var j = SharedState.ParseFragment(State?.GetJson("lan"));
                if (j == null) return null;
                return ((int)PlaylistRunPlan.Num(j, "i"), PlaylistRunPlan.Str(j, "id", null), PlaylistRunPlan.Str(j, "pl", null),
                    (int)PlaylistRunPlan.Num(j, "n"), PlaylistRunPlan.Str(j, "owner", "title"));
            }
        }

        /// Exact resume (PlaylistResumeRules.Exact): the cached round's finished stops come back as results, run.state too.
        /// Applied once, by the leader, to the round it matches.
        public JSONObject ResumeFrom;
        bool _resumed;
        void ApplyResume(IPlaylistLeg leg)
        {
            if (_resumed || ResumeFrom == null || leg?.Plan == null) return;
            if ((int)PlaylistRunPlan.Num(ResumeFrom, "leg", -1) != leg.Plan.legIndex) return;
            _resumed = true;
            var res = ResumeFrom.GetField("results");
            int n = 0;
            if (res?.keys != null)
                for (int i = 0; i < res.keys.Count; i++)
                {
                    string occ = res.keys[i];
                    if (leg.Plan.FindStop(occ, out _) == null) continue;
                    string kind = PlaylistRunPlan.Str(res.list[i], "result", "done");
                    State?.Set("res." + occ, res.list[i].Print());
                    State?.Set("g." + occ, "{\"r\":\"" + SharedState.JsonEscape(kind) + "\",\"k\":\"resumed\"}");
                    leg.ApplyGoalOutcome(occ, kind);
                    n++;
                }
            var rs = ResumeFrom.GetField("runState");
            if (rs != null && rs.type == JSONObject.Type.Object) SaveRunState(rs.Print());
            Log($"resumed exactly where it was: round {leg.Plan.legIndex + 1}, {n} finished stop(s) restored");
            AddLog("resume", null, n + " stops");
        }
        public string MarkSpec(string occ) { Read(); return occ != null && _marks.TryGetValue(occ, out var j) ? j : null; }
        public string JumpSpec(string occ) { Read(); return occ != null && _jumps.TryGetValue(occ, out var j) ? j : null; }
        public string JumpResetSpec(string occ) { Read(); return occ != null && _jumpResets.TryGetValue(occ, out var j) ? j : null; }
        public IEnumerable<string> MarkedStops() { Read(); return new List<string>(_marks.Keys); }
        public IEnumerable<string> JumpStops() { Read(); return new List<string>(_jumps.Keys); }
        public bool Over { get { Read(); return _over; } }
        /// May round `legIndex` hand over to the next (the readiness gate)?
        public bool MayAdvance(int legIndex) { Read(); return _advance >= legIndex; }
        public string ResultJson(string occ) => State?.GetJson("res." + occ);
        public string StopStateJson(string occ) => SharedState.ParseFragment(State?.GetJson("g." + occ))?.GetField("st")?.Print();
        public string RunStateJson => State?.GetJson("rs");
        public string LogJson => State?.GetJson("log");
        public IEnumerable<string> Keys => State != null ? State.Keys : Array.Empty<string>();

        void Read()
        {
            if (State == null || State.Version == _ssVersion) return;
            _ssVersion = State.Version;
            _subs.Clear(); _goalDriven.Clear(); _marks.Clear(); _jumps.Clear(); _jumpResets.Clear(); _locks.Clear();
            foreach (var k in State.Keys)
            {
                if (k.StartsWith("g.", StringComparison.Ordinal))
                {
                    var g = SharedState.ParseFragment(State.GetJson(k));
                    if (g != null && string.IsNullOrEmpty(PlaylistRunPlan.Str(g, "r", null))) _goalDriven.Add(k.Substring(2));
                    else if (g != null) _goalDriven.Add(k.Substring(2));   // a finished block still owns its stop
                }
                else if (k.StartsWith("mk.", StringComparison.Ordinal)) _marks[k.Substring(3)] = State.GetJson(k);
                else if (k.StartsWith("jp.", StringComparison.Ordinal)) _jumps[k.Substring(3)] = State.GetJson(k);
                else if (k.StartsWith("jr.", StringComparison.Ordinal)) _jumpResets[k.Substring(3)] = State.GetJson(k);
                else if (k.StartsWith("lk.", StringComparison.Ordinal)) _locks[k.Substring(3)] = State.GetJson(k);
            }
            var sub = SharedState.ParseFragment(State.GetJson("sub"));
            if (sub?.list != null) foreach (var x in sub.list) if (x != null && x.type == JSONObject.Type.String) _subs.Add(x.stringValue);
            var adv = SharedState.ParseFragment(State.GetJson("adv"));
            _advance = adv != null ? (int)PlaylistRunPlan.Num(adv, "i", -1) : -1;
            var cap = SharedState.ParseFragment(State.GetJson("cap"));
            _over = cap != null && PlaylistRunPlan.Bool(cap, "over", false);
            // Sets: the set the playlist Lua put out (every device registers it: packs and follower plans resolve "set<n>").
            var set = SharedState.ParseFragment(State.GetJson("set"));
            if (set != null && set.type == JSONObject.Type.Object) PlaylistSets.Register(Plan?.playlistId ?? PlaylistRunPlan.Str(SharedState.ParseFragment(State.GetJson("rec")), "id", null), set);
            Version++;
        }

        // ── Events ────────────────────────────────────────────────────

        internal void Enqueue(PlaylistEvent e)
        {
            if (_pending.Count > 512) _pending.Dequeue();
            _pending.Enqueue(e);
        }

        /// Does any goal block of this run listen to `source` (any name)? Taps skip building events nobody wants.
        public bool Wants(string source)
        {
            Read();
            if (_subs.Contains("*") || _subs.Contains(source)) return true;
            foreach (var x in _subs) if (x.StartsWith(source + ":", StringComparison.Ordinal)) return true;
            return false;
        }

        bool Subscribed(PlaylistEvent e)
        {
            Read();
            return _subs.Contains(e.source) || _subs.Contains(e.source + ":" + e.name) || _subs.Contains("*") || IsBroken(e);
        }

        void Pump(double now, bool leader)
        {
            if (!leader && !Synced) return;   // not synced yet: who leads is unknown — keep the events until it is
            if (now - _forwardWindow >= 1.0) { _forwardWindow = now; _forwardBudget = (int)ForwardPerSecond; }
            int n = _pending.Count;
            while (n-- > 0 && _pending.Count > 0)
            {
                var e = _pending.Dequeue();
                if (leader) { Dispatch(e, now); continue; }
                if (LocalOnly.Contains(e.source) || !Subscribed(e) || State == null) continue;
                if (_forwardBudget-- <= 0) { Log($"forward budget spent: dropped {e}"); continue; }
                State.Request("ev", e.ToJson());
            }
        }

        // The leader's recent events per stop: a block that starts a moment after its title's first report (SAL's
        // progress(0, 7) at stage start) still hears it (replayed at start, ≤ ReplaySeconds old).
        public const double ReplaySeconds = 15.0;
        readonly Dictionary<string, List<PlaylistEvent>> _recent = new Dictionary<string, List<PlaylistEvent>>(StringComparer.Ordinal);

        void Remember(PlaylistEvent e, double now)
        {
            if (e.stop == null) return;
            if (!_recent.TryGetValue(e.stop, out var l)) _recent[e.stop] = l = new List<PlaylistEvent>();
            l.Add(e);
            l.RemoveAll(x => now - x.at > ReplaySeconds);
            if (l.Count > 32) l.RemoveRange(0, l.Count - 32);
        }

        void Replay(string occ, double now)
        {
            if (!_recent.TryGetValue(occ, out var l) || l.Count == 0) return;
            int n = 0;
            foreach (var e in l.ToArray())
                if (now - e.at <= ReplaySeconds && _blocks.ContainsKey(occ)) { Deliver(occ, e); n++; }
            if (n > 0) Log($"{occ}: replayed {n} event(s) from before the block started");
        }

        void Dispatch(PlaylistEvent e, double now)
        {
            if (!_dedupe.First(e)) return;
            Remember(e, now);
            if (IsBroken(e)) ChallengeBroken(e);
            AttemptEvent(e, now);
            if (_blocks.Count == 0 || _rt == null) return;
            var leg = Current;
            if (e.stop != null)
            {
                if (_blocks.ContainsKey(e.stop)) Deliver(e.stop, e);
                return;
            }
            foreach (var occ in new List<string>(_blocks.Keys)) Deliver(occ, e);
        }

        void Deliver(string occ, PlaylistEvent e)
        {
            string st = _rt.Dispatch(RunId, occ, e);
            if (st != null) SaveStopState(occ, st);
        }

        // Leader only (SharedState raises it on the leader).
        void OnRequested(string kind, string json, string from)
        {
            switch (kind)
            {
                case "ev":
                {
                    var e = PlaylistEvent.FromJson(json);
                    if (e != null) Enqueue(e);
                    return;
                }
                case "map": MapApply(json); return;   // a non-leader's dp.map call (PlaylistMap.cs)
                case "dm":
                {
                    var m = DreamieMoment.FromJson(json);
                    if (m != null) { try { _w.MomentSink?.Invoke(m); } catch (Exception ex) { Log("moment sink threw: " + ex.Message); } }
                    return;
                }
            }
        }

        /// A Dreamie moment from this device: the director's if we lead, else forwarded (CONTRACT § 4).
        public void SubmitMoment(DreamieMoment m)
        {
            if (m == null) return;
            if (m.name == "stop_unlocked" && !FirstUnlock(m)) return;
            if (string.IsNullOrEmpty(m.runId)) m.runId = RunId;
            if (string.IsNullOrEmpty(m.playlistId)) m.playlistId = Plan?.playlistId;
            if (string.IsNullOrEmpty(m.roundOne)) { var w = PlaylistRunRules.RoundWord(Plan); m.roundOne = w.one; m.roundMany = w.many; }
            if (IsLeader || State == null) { try { _w.MomentSink?.Invoke(m); } catch (Exception ex) { Log("moment sink threw: " + ex.Message); } }
            else State.Request("dm", m.ToJson());
        }

        /// One `stop_unlocked` per stop per run (the native lock and a playlist Lua that still raises it never both speak).
        bool FirstUnlock(DreamieMoment m)
        {
            var d = SharedState.ParseFragment(m.dataJson);
            string id = PlaylistRunPlan.Str(d, "stop", null) ?? PlaylistRunPlan.Str(d, "id", null) ?? m.stop;
            string occ = ResolveStopId(id);
            if (string.IsNullOrEmpty(occ) || State == null) return true;
            if (State.Has("ul." + occ)) { Log($"{occ}: stop_unlocked already said — dropped the duplicate"); return false; }
            State.Set("ul." + occ, "{\"t\":" + Num(_w.Clock()) + "}");
            return true;
        }

        void Moment(string name, string dataJson, string stop = null, string priority = null, bool once = false, string guide = null)
        {
            if (!DreamieMoments.ValidName(name)) return;
            string g = guide ?? BeatGuide(name, stop);
            SubmitMoment(new DreamieMoment
            {
                name = name, dataJson = PlaylistEvents.Clamp(dataJson, DreamieMoments.MaxDataBytes), stop = stop, once = once,
                priority = priority ?? DreamieMoment.Normal, guide = g, from = _w.DeviceId(), at = _w.Clock(),
            });
        }

        /// The playlist's Dreamie script beat for a moment (`dreamie.beats[name]` or `beats[layout.<legId>]`), if any.
        string BeatGuide(string name, string stop)
        {
            var p = Plan;
            if (p == null) return null;
            // Record v2 § 8.5: the stop's guidance, then its round's, then the playlist's beats.
            string colocated = PlaylistRunRules.ColocatedGuide(p, name, stop);
            if (colocated != null) return colocated;
            if (string.IsNullOrEmpty(p.dreamieJson)) return null;
            var d = SharedState.ParseFragment(p.dreamieJson);
            var beats = d?.GetField("beats");
            if (beats == null) return null;
            var b = beats.GetField(name + "." + (p.Current?.id ?? "")) ?? beats.GetField(name);
            return b != null ? PlaylistRunPlan.Str(b, "guide", null) : null;
        }

        // ── Tick ──────────────────────────────────────────────────────

        internal void Tick(double now)
        {
            Read();
            bool leader = Synced && IsLeader;
            if (_wasLeader && !leader)
            {
                // Lost the lead: the new leader resumes every block from the shared table.
                _rt?.EndAll(RunId); _rt?.EndRun(RunId);
                _blocks.Clear(); _runLuaTried = false; _att.Clear();
                Log("lead handed over — blocks stop here, the new leader resumes them");
            }
            if (!_wasLeader && leader) _att.Clear();   // a new leader reads every attempt from the shared table
            _wasLeader = leader;
            var leg = Current;
            Pump(now, leader);
            WriteReady(now, leg);
            ApplyBadges();
            if (!_nextNoted && (State?.Has("nxs") ?? false)) NoteNext();
            if (leg != null) { ApplySetChanges(leg); LocalRound(now, leg); DeliverChallengeEvents(leg); WriteBadgeHolds(now, leg); }
            MapPoll(now, leader, leg);   // dp.map show / hide on every device (PlaylistMap.cs)
            if (!leader || leg == null) { SaveCache(now, leg); return; }

            // A refusal that arrived after the attach (the bundle's 409 RELEASE_TOO_OLD) and before anyone started: no start,
            // no goal block (no 0/0 run).
            if (!(State?.Has("st") ?? false) && leg.Plan.legIndex == 0)
            {
                if (!_releaseRefused) { string why = _w.RefusalFor(leg.Plan); if (why != null) { _releaseRefused = true; _w.Refuse(leg.Plan, why); AddLog("refused", null, why); } }
                if (_releaseRefused) return;   // no cache either: nothing to resume
            }

            EnsureRecord(now, leg);
            if (leg.Plan.legIndex != _legSeen) OnLegChanged(leg, now);
            ApplyResume(leg);
            EnsureRunLua(leg);
            var row = leg.Row;
            if (row.phase != _phaseSeen) OnPhase(leg, row, now);
            if (row.phase == PlaylistRunRules.Playing)
            {
                if (_over) { CapRound(leg, "the run's time is up"); }
                if (_startPending) TryStartRun(leg, now);
                NativeUnlocks(leg);
                StartBlocks(leg, now);
                SendChallenges(leg);
                EndFinishedBlocks(leg);
                FireTimers(now);
                AttemptTick(leg, now);
                ApplyUnplaced(leg);
                SetTick(leg, now);
                WaypointTick(leg, now);
                TimingCaps(leg, now);
                Idle(leg, now);
            }
            if (row.phase == PlaylistRunRules.Complete) NextTick(leg, now);
            Readiness(leg, now);
            SaveCache(now, leg);
        }

        void EnsureRecord(double now, IPlaylistLeg leg)
        {
            if (State == null || State.Has("rec")) return;
            var p = leg.Plan;
            var j = new JSONObject();
            j.AddField("id", p.playlistId ?? ""); j.AddField("rev", p.rev ?? ""); j.AddField("title", p.contentId ?? "");
            j.AddField("name", p.name ?? ""); j.AddField("t0", now); j.AddField("legs", p.LegCount);
            var revs = RevsJson(p);
            j.AddField("revs", revs);
            string rj = j.Print();
            if (rj.Length > SharedState.MaxValueChars) { j.RemoveField("revs"); rj = j.Print(); }
            State.Set("rec", rj);
            int goals = revs.GetField("goals")?.count ?? 0;
            Log($"run starts PINNED to record rev {(string.IsNullOrEmpty(p.rev) ? "manifest" : p.rev)}, {goals} goal rev(s): {PlaylistEvents.Clamp(revs.Print(), 400)}");
            AddLog("revs", null, revs.Print());
        }

        void OnLegChanged(IPlaylistLeg leg, double now)
        {
            int prev = _legSeen;
            _legSeen = leg.Plan.legIndex;
            _phaseSeen = null;
            _legDoneAt = -1;
            _lastProgressAt = now;
            // The previous round's blocks end; their per-stop rows leave the table (the snapshot stays small).
            _rt?.EndAll(RunId);
            _blocks.Clear(); _noBlock.Clear();
            // Oct 23 (pl-sync-fix, leader hand-off D3): a NEW LEADER meets the round the table is already on — that is no round
            // change: the table keeps `nx` (the set the old leader asked for), `sd` and the stop rows, so the set change goes on
            // (before: the new leader deleted `nx` and the run stalled on a finished set).
            var tableLeg = SharedState.ParseFragment(State?.GetJson("leg"));
            bool handOff = tableLeg != null && (int)PlaylistRunPlan.Num(tableLeg, "i", -1) == leg.Plan.legIndex
                && PlaylistRunPlan.Str(tableLeg, "id", "") == (leg.Plan.Current?.id ?? "");
            if (handOff) Log($"round {leg.Plan.legIndex + 1} '{leg.Plan.Current?.id}' is the table's round already (a new leader): its rows and next set stay");
            if (State != null && !handOff)
            {
                var keep = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>()) if (s != null) keep.Add(s.occurrenceId ?? "");
                foreach (var k in new List<string>(State.Keys))
                {
                    int dot = k.IndexOf('.');
                    if (dot < 0) continue;
                    string pre = k.Substring(0, dot), occ = k.Substring(dot + 1);
                    if ((pre == "g" || pre == "mk" || pre == "jp" || pre == "jr" || pre == "ul" || pre == "at" || pre == "ch" || pre == "ce") && !keep.Contains(occ)) State.Delete(k);
                    else if (pre == "lk" && leg.Plan.sets && occ.StartsWith("pl-", StringComparison.Ordinal) && !keep.Contains(occ)) State.Delete(k);   // an old set's lock
                }
                var lj = new JSONObject(); lj.AddField("i", leg.Plan.legIndex); lj.AddField("id", leg.Plan.Current?.id ?? "");
                State.Set("leg", lj.Print());
                if (State.Has("nx")) State.Delete("nx");
                if (State.Has("sd")) State.Delete("sd");
            }
            if (State != null)
            {
                _att.Clear();
                RebuildSubs();
                if (leg.Plan.sets) AdoptSet(leg);
            }
            AddLog("round", null, $"round {leg.Plan.legIndex + 1}/{leg.Plan.LegCount} '{leg.Plan.Current?.name}'" + (prev >= 0 ? $" (after {prev + 1})" : ""));
            if (_over) Log("the run is over (time cap): this round is skipped");
        }

        void OnPhase(IPlaylistLeg leg, PlaylistRunRow row, double now)
        {
            string was = _phaseSeen;
            _phaseSeen = row.phase;
            if (row.phase == PlaylistRunRules.Playing && was != PlaylistRunRules.Playing)
            {
                _lastProgressAt = now;
                // The start circle ran (the leg reached `playing`): the playlist's Lua starts — once the badge gate (§ 3.4) says
                // this group may play it.
                if (leg.Plan.legIndex == 0 && !(State?.Has("st") ?? false)) { _startPending = true; _gateSince = now; TryStartRun(leg, now); }
            }
            if (row.phase == PlaylistRunRules.Cleared || row.phase == PlaylistRunRules.LegDone)
            {
                if (_legDoneAt < 0) _legDoneAt = now;
                EndAllBlocks("round over");
                if (!leg.Plan.IsLastLeg && !leg.Plan.sets && !(State?.Has("nx") ?? false))
                {
                    // An arc[] record's set is over: the (converted) Lua's on_set_done shows the next round (run.show), else
                    // choose_next names it (records whose Lua predates sets), else the queue's order.
                    var (st, ret) = CallRun("on_set_done", SetSummary(leg));
                    if (st != null) SaveRunState(st);
                    if (!(State?.Has("nx") ?? false))
                    {
                        (st, ret) = CallRun("choose_next", LegJson(leg.Plan.Current));
                        if (st != null) SaveRunState(st);
                    }
                    if (!string.IsNullOrEmpty(ret) && ret != "nil" && !(State?.Has("nx") ?? false)) { State?.Set("nx", "\"" + SharedState.JsonEscape(ret) + "\""); AddLog("next", null, "choose_next → " + ret); }
                }
            }
            if (row.phase == PlaylistRunRules.Complete && !string.IsNullOrEmpty(NextPlaylist) && !(State?.Has("nxs") ?? false)) { _nextPending = true; _nextSince = now; }
            if (row.phase == PlaylistRunRules.Complete && !(State?.Has("fin") ?? false))
            {
                State?.Set("fin", "{\"t\":" + Num(now) + ",\"r\":\"" + SharedState.JsonEscape(row.result ?? "") + "\"}");
                EndAllBlocks("run complete");
                var (st, _) = CallRun("on_finale");
                if (st != null) SaveRunState(st);
                Moment("complete", "{\"result\":\"" + SharedState.JsonEscape(row.result ?? "") + "\",\"playlist\":\"" + SharedState.JsonEscape(leg.Plan.name ?? "") + "\"}", null, DreamieMoment.High, true);
                AddLog("complete", null, "playlist complete: " + row.result);
            }
        }

        // ── Goal blocks ───────────────────────────────────────────────

        PlaylistLuaRuntime Runtime()
        {
            if (_rt != null) return _rt;
            LuaEnv env = null;
            try { env = _w.Lua?.Invoke(); } catch (Exception e) { Log("no Lua env: " + e.Message); }
            _rt = env != null ? PlaylistLuaRuntime.For(env) : null;
            return _rt;
        }

        void StartBlocks(IPlaylistLeg leg, double now)
        {
            var plan = leg.Plan;
            foreach (var s in plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || string.IsNullOrEmpty(s.occurrenceId)) continue;
                string occ = s.occurrenceId;
                if (_blocks.ContainsKey(occ) || _noBlock.Contains(occ)) continue;
                if (!leg.IsLive(s)) continue;
                var p = leg.Progress.TryGetValue(occ, out var pp) ? pp : null;
                var stored = SharedState.ParseFragment(State?.GetJson("g." + occ));
                if (stored != null && !string.IsNullOrEmpty(PlaylistRunPlan.Str(stored, "r", null))) { _noBlock.Add(occ); continue; }   // finished
                if (PlaylistRunRules.StopOver(p) && stored == null) { _noBlock.Add(occ); continue; }
                string src = PlaylistGoalLibrary.Resolve(plan, s, out string key, out string source, PinnedGoal);
                if (src == null) { _noBlock.Add(occ); continue; }
                var rt = Runtime();
                if (rt == null) { _noBlock.Add(occ); Log($"{occ}: goal block {key} needs a Lua VM — none here"); continue; }
                string kind = PlaylistChallenge.KindOf(plan.Current, s);
                var ctx = new JSONObject();
                ctx.AddField("id", !string.IsNullOrEmpty(s.stopId) ? s.stopId : occ);
                ctx.AddField("occurrence", occ);
                ctx.AddField("attraction", PlaylistAutoRules.Leaf(s.attraction) ?? "");
                ctx.AddField("kind", kind);
                ctx.AddField("role", s.role ?? "stage");
                ctx.AddField("key", key ?? "");
                var opt = string.IsNullOrEmpty(s.options) ? null : SharedState.ParseFragment(s.options);
                ctx.AddField("options", opt != null && opt.type == JSONObject.Type.Object ? opt : new JSONObject());
                var b = new Block { key = key, source = source, startedAt = now };
                _blocks[occ] = b;
                // § 3.2: the runtime tracks each player's attempt; presence + resets + skip requests reach the leader.
                foreach (var ev in AttemptEvents) GoalSubscribe(occ, PlaylistEvent.Event, ev);
                var att = Att(occ);
                if (att.startedAt <= 0) { att.startedAt = now; SaveAtt(occ, att); }
                // Record ownership FIRST: from now on the title's calls at this stop are events only, on every device.
                var g = stored ?? new JSONObject();
                g.SetField("k", key ?? ""); g.SetField("s", source ?? "");
                if (g.GetField("t0") == null) g.SetField("t0", now);
                State?.Set("g." + occ, g.Print());
                string stateJson = g.GetField("st")?.Print();
                string err = rt.StartBlock(this, RunId, occ, src, ctx.Print(), stateJson, out string stOut);
                if (err != null)
                {
                    _blocks.Remove(occ); _noBlock.Add(occ);
                    State?.Delete("g." + occ);
                    Log($"{occ}: goal block {key} ({source}) failed — falling back to the title's calls / auto rules: {err}");
                    AddLog("goal_error", occ, err);
                    continue;
                }
                if (stOut != null) SaveStopState(occ, stOut);
                bool resumed = stored != null && stored.GetField("st") != null;
                Log($"{occ} ({s.attraction}): goal block {key} [{source}] {(resumed ? "RESUMED from the shared state" : "started")}");
                AddLog(resumed ? "goal_resume" : "goal_start", occ, key);
                // The safety net (CONTRACT § 2): a block that set no timer gets the challenge default.
                if (b.timerCount == 0) AddSafetyNet(occ, s, kind, now, g);
                ArmAttemptLimit(occ, now);
                if (!resumed) Replay(occ, now);
                if (kind == PlaylistChallenge.Boss && !resumed)
                    Moment("boss_intro", "{\"stop\":\"" + SharedState.JsonEscape(occ) + "\",\"name\":\"" + SharedState.JsonEscape(PlaylistObjectiveText.Name(s)) + "\"}", occ, DreamieMoment.High, true);
            }
        }

        // ── Native locks (record v2 § 8.6) ───────────────────────────
        // `lockedUntil` is data: every device holds the stop behind its force field (PlaylistRoundLocks) until each
        // needed stop has ANY result (done / failed / timed out / skipped — a lock never stalls). The leader announces
        // the opening once: `stop_unlocked` {stop, id, name, by, result}. A playlist Lua lock on the same stop keeps
        // it shut (dynamic logic) until run.unlock.
        readonly HashSet<string> _unlockSeen = new HashSet<string>(StringComparer.Ordinal);
        void NativeUnlocks(IPlaylistLeg leg)
        {
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (!PlaylistRoundRules.Gated(s) || _unlockSeen.Contains(s.occurrenceId ?? "")) continue;
                PlaylistRunStop last = null; string lastResult = null; double lastAt = -1;
                bool open = true;
                foreach (string need in s.lockedUntil)
                {
                    var p = leg.Progress.TryGetValue(need ?? "", out var pp) ? pp : null;
                    if (!PlaylistRunRules.StopOver(p)) { open = false; break; }
                    double at = Math.Max(p.ts, p.firstAt);
                    if (at >= lastAt) { lastAt = at; last = leg.Plan.FindStop(need, out _); lastResult = PlaylistRunPlan.Str(SharedState.ParseFragment(ResultJson(need)), "result", p.Result); }
                }
                if (!open || (State != null && State.Has("lk." + s.occurrenceId))) continue;
                _unlockSeen.Add(s.occurrenceId);
                if (State != null && State.Has("ul." + s.occurrenceId)) continue;   // said already (resume / a new leader)
                string id = !string.IsNullOrEmpty(s.stopId) ? s.stopId : s.occurrenceId;
                string by = last != null ? (!string.IsNullOrEmpty(last.stopId) ? last.stopId : last.occurrenceId) : "";
                Log($"{s.occurrenceId} ({s.attraction}): lockedUntil met (last: {by} {lastResult}) — unlocked natively");
                AddLog("unlock", s.occurrenceId, "native: " + by);
                Moment("stop_unlocked", "{\"stop\":\"" + SharedState.JsonEscape(id) + "\",\"id\":\"" + SharedState.JsonEscape(id) + "\",\"name\":\""
                    + SharedState.JsonEscape(PlaylistObjectiveText.Name(s)) + "\",\"by\":\"" + SharedState.JsonEscape(by) + "\",\"result\":\"" + SharedState.JsonEscape(lastResult ?? "") + "\""
                    + (s.role == "boss" || s.role == "miniboss" ? ",\"boss\":true" : "") + "}", s.occurrenceId, DreamieMoment.High, true);
            }
        }

        /// Locked now: a playlist lock (run.lock / the set's lock) or its native lockedUntil (PLAYLIST_STATE stops[].locked).
        public bool Locked(string occ) => occ != null && (LockOf(occ) != null || NativeLocked(occ));

        /// The stop is held by its native lock right now (some lockedUntil need has no result yet).
        public bool NativeLocked(string occ)
        {
            var leg = Current;
            var s = leg?.Plan?.FindStop(occ, out _);
            if (!PlaylistRoundRules.Gated(s)) return false;
            foreach (string need in s.lockedUntil)
                if (!PlaylistRunRules.StopOver(leg.Progress.TryGetValue(need ?? "", out var p) ? p : null)) return true;
            return false;
        }

        void AddSafetyNet(string occ, PlaylistRunStop s, string kind, double now, JSONObject g)
        {
            var b = _blocks[occ];
            if (AttemptLimit(occ) > 0) return;   // the block set its own per-attempt time (stop.time)
            b.platformTimers = true;
            var tm = TimersOf(occ);
            double sec;
            if (kind == PlaylistChallenge.Boss)
            {
                if (!tm.ContainsKey(-1)) tm[-1] = (now + PlaylistRunRules.BossHintSeconds, 0);
                sec = PlaylistChallenge.BossSkipSeconds;
            }
            else sec = s.seconds > 0 ? s.seconds : PlaylistChallenge.DefaultSeconds(kind, Plan?.Current);
            SaveTimers(occ, tm);
            if (sec > 0) SetAttemptLimit(occ, sec);
            Log($"{occ}: block set no timer — platform safety net added (each attempt ends lost{{why='time'}} after {sec:0} s{(kind == PlaylistChallenge.Boss ? ", boss hint at 180 s" : "")})");
        }

        void EndFinishedBlocks(IPlaylistLeg leg)
        {
            foreach (var occ in new List<string>(_blocks.Keys))
            {
                var p = leg.Progress.TryGetValue(occ, out var pp) ? pp : null;
                if (!PlaylistRunRules.StopOver(p)) continue;
                if (_att.TryGetValue(occ, out var open) && open.won && !open.closed && !p.timedOut) continue;   // others still inside
                var g = SharedState.ParseFragment(State?.GetJson("g." + occ));
                if (g != null && string.IsNullOrEmpty(PlaylistRunPlan.Str(g, "r", null)))
                {
                    // Over by other means (the runner's skip / a result): the block is done too.
                    g.SetField("r", p.timedOut ? "timed_out" : "done");
                    State?.Set("g." + occ, g.Print());
                }
                EndBlock(occ);
            }
        }

        void EndBlock(string occ)
        {
            _rt?.EndBlock(RunId, occ);
            _blocks.Remove(occ);
            _noBlock.Add(occ);
            if (State != null)
            {
                if (State.Has("mk." + occ)) State.Delete("mk." + occ);
                if (State.Has("jp." + occ)) State.Delete("jp." + occ);
            }
            RebuildSubs();
        }

        void EndAllBlocks(string why)
        {
            if (_blocks.Count == 0) return;
            foreach (var occ in new List<string>(_blocks.Keys)) EndBlock(occ);
            Log("goal blocks ended: " + why);
        }

        // Timers: absolute deadlines in g.{occ}.tm so a new leader keeps them.
        Dictionary<int, (double due, double every)> TimersOf(string occ)
        {
            var d = new Dictionary<int, (double, double)>();
            var g = SharedState.ParseFragment(State?.GetJson("g." + occ));
            var tm = g?.GetField("tm");
            if (tm?.keys == null) return d;
            for (int i = 0; i < tm.keys.Count; i++)
            {
                var v = tm.list[i];
                if (v?.list == null || v.list.Count < 2) continue;
                if (int.TryParse(tm.keys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    d[id] = (v.list[0].doubleValue, v.list[1].doubleValue);
            }
            return d;
        }

        void SaveTimers(string occ, Dictionary<int, (double due, double every)> tm)
        {
            var g = SharedState.ParseFragment(State?.GetJson("g." + occ)) ?? new JSONObject();
            var tj = new JSONObject();
            foreach (var kv in tm.OrderBy(x => x.Key))
            {
                var a = new JSONObject(JSONObject.Type.Array); a.Add(Math.Round(kv.Value.due, 3)); a.Add(kv.Value.every);
                tj.AddField(kv.Key.ToString(CultureInfo.InvariantCulture), a);
            }
            g.SetField("tm", tj);
            if (State != null) State.Set("g." + occ, g.Print());
            else _soloG[occ] = g.Print();
        }
        readonly Dictionary<string, string> _soloG = new Dictionary<string, string>(StringComparer.Ordinal);

        void FireTimers(double now)
        {
            foreach (var occ in new List<string>(_blocks.Keys))
            {
                var tm = TimersOf(occ);
                bool changed = false;
                foreach (var id in tm.Keys.OrderBy(x => x).ToList())
                {
                    if (!_blocks.ContainsKey(occ)) break;
                    var (due, every) = tm[id];
                    if (due > now) continue;
                    if (every > 0) { double next = due; while (next <= now) next += every; tm[id] = (next, every); }
                    else tm.Remove(id);
                    changed = true;
                    SaveTimers(occ, tm);
                    if (id < 0) { PlatformTimer(occ, id); continue; }
                    string st = _rt?.FireTimer(RunId, occ, id);
                    if (st != null) SaveStopState(occ, st);
                    tm = TimersOf(occ);   // the callback may have added timers
                }
                if (changed && _blocks.ContainsKey(occ)) { }
            }
        }

        void PlatformTimer(string occ, int id)
        {
            var s = Plan?.FindStop(occ, out _);
            switch (id)
            {
                case -1:   // boss hint
                    GoalHint(occ, "");
                    break;
                case -2:   // (older stop states) the boss's 600 s net, the kind's default: the attempt's time is up
                case -3:
                case -4:   // the per-attempt limit (stop.time / the platform safety net)
                    Log($"{occ}: the attempt's time ran out → lost{{why='time'}}");
                    AttemptLost(occ, "time", null, "{\"why\":\"time\",\"reason\":\"attempt_limit\"}");
                    break;
            }
        }

        void SaveStopState(string occ, string stateJson)
        {
            if (State == null || stateJson == null) return;
            var g = SharedState.ParseFragment(State.GetJson("g." + occ)) ?? new JSONObject();
            var st = SharedState.ParseFragment(stateJson);
            if (st == null || st.type != JSONObject.Type.Object) st = new JSONObject();
            string before = g.GetField("st")?.Print();
            string after = st.Print();
            if (before == after) return;
            g.SetField("st", st);
            string all = g.Print();
            if (all.Length > SharedState.MaxValueChars) { Log($"{occ}: stop.state too large ({all.Length} chars) — not synced"); return; }
            State.Set("g." + occ, all);
        }

        void SaveRunState(string stateJson)
        {
            if (State == null || stateJson == null) return;
            if (State.GetJson("rs") == stateJson) return;
            if (stateJson.Length > SharedState.MaxValueChars) { Log("run.state too large — not synced"); return; }
            State.Set("rs", stateJson);
        }

        void RebuildSubs()
        {
            if (State == null) return;
            // Subscriptions of running blocks (each block re-subscribes on start; a finished block drops its own).
            var all = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var kv in _subsByBlock) if (_blocks.ContainsKey(kv.Key)) foreach (var s in kv.Value) all.Add(s);
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var s in all) arr.Add(s);
            string j = arr.Print();
            if (State.GetJson("sub") != j) State.Set("sub", j);
        }
        readonly Dictionary<string, HashSet<string>> _subsByBlock = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // ── Round-level rules: caps, readiness, idle, LAN advance ─────

        void TimingCaps(IPlaylistLeg leg, double now)
        {
            var plan = leg.Plan;
            var row = leg.Row;
            double capMin = 0, totalMin = 0;
            var t = string.IsNullOrEmpty(plan.timingJson) ? null : SharedState.ParseFragment(plan.timingJson);
            if (t != null)
            {
                totalMin = PlaylistRunPlan.Num(t, "totalMin");
                var rounds = t.GetField("rounds");
                if (rounds != null && plan.Current?.id != null) capMin = PlaylistRunPlan.Num(rounds, plan.Current.id);
            }
            double q = 0;
            try { q = _w.QueueRoundMinutes?.Invoke() ?? 0; } catch (Exception) { }
            if (q > 0) capMin = q;   // the venue's queue item overrides the round's cap
            if (capMin > 0 && row.t0 > 0 && now - row.t0 >= capMin * 60.0) { CapRound(leg, $"round cap {capMin:0.#} min"); return; }
            if (totalMin > 0 && !_over)
            {
                var rec = SharedState.ParseFragment(State?.GetJson("rec"));
                double t0 = rec != null ? PlaylistRunPlan.Num(rec, "t0") : 0;
                if (t0 > 0 && now - t0 >= totalMin * 60.0)
                {
                    State?.Set("cap", "{\"over\":true,\"why\":\"total " + Num(totalMin) + " min\"}");
                    _over = true;
                    CapRound(leg, $"total cap {totalMin:0.#} min");
                }
            }
        }

        readonly HashSet<int> _capped = new HashSet<int>();
        void CapRound(IPlaylistLeg leg, string why)
        {
            if (!_capped.Add(leg.Plan.legIndex)) return;
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                var p = leg.Progress.TryGetValue(s.occurrenceId ?? "", out var pp) ? pp : null;
                if (PlaylistRunRules.StopOver(p)) continue;
                if (_blocks.ContainsKey(s.occurrenceId)) { EndStop(s.occurrenceId, "timed_out", "{\"reason\":\"time_cap\"}"); EndBlock(s.occurrenceId); }
                else leg.ApplyGoalOutcome(s.occurrenceId, "timed_out");
            }
            if (leg.Plan.sets)
            {
                // Sets: the time cap ends the set; the Lua puts out the next (on_set_done) or the run ends.
                if (_over) { Log("the run's time is up: run.finish{result='timer'}"); State?.Set("rf", "{\"r\":\"timer\"}"); leg.Finish("timer"); }
                else
                {
                    var (st0, _) = CallRun("on_set_done", SetSummary(leg));
                    if (st0 != null) SaveRunState(st0);
                    if (!(State?.Has("nx") ?? false) && !(State?.Has("rf") ?? false)) { State?.Set("rf", "{\"r\":\"timer\"}"); leg.Finish("timer"); }
                    else if (State?.Has("nx") ?? false) leg.SkipLeg();
                }
            }
            else leg.SkipLeg();
            Log($"round {leg.Plan.legIndex + 1} ends: {why} (unfinished stops timed out)");
            AddLog("cap", null, why);
            var rw = PlaylistRunRules.RoundWord(leg.Plan);
            string label = PlaylistRunRules.RoundLabel(leg.Plan);
            string text = "Time's up" + (label.Length > 0 ? " — " + rw.one + " " + (leg.Plan.legIndex + 1) + " is over!" : "!");
            Moment("time_up", "{\"why\":\"" + SharedState.JsonEscape(why) + "\",\"round\":\"" + SharedState.JsonEscape(label) + "\",\"roundWord\":\""
                + SharedState.JsonEscape(rw.one) + "\",\"caption\":\"" + SharedState.JsonEscape(text) + "\"}", null, DreamieMoment.High);
        }

        void Idle(IPlaylistLeg leg, double now)
        {
            foreach (var kv in leg.Progress)
            {
                var p = kv.Value;
                if (p == null) continue;
                double t = Math.Max(p.firstAt, p.ts);
                if (t > _lastProgressAt && t <= now) _lastProgressAt = t;
            }
            if (_lastProgressAt > 0 && now - _lastProgressAt >= IdleSeconds)
            {
                _lastProgressAt = now;
                Moment("idle", "{\"round\":\"" + SharedState.JsonEscape(leg.Plan.Current?.name ?? "") + "\"}", null, DreamieMoment.Low);
            }
        }

        void WriteReady(double now, IPlaylistLeg leg)
        {
            if (leg == null || State == null || now < _nextReady || !State.Synced) return;
            _nextReady = now + 1.0;
            int next = leg.Plan.legIndex + 1;
            bool dl = true, pre = true;
            try { var r = _w.RoundReady(leg.Plan, next); dl = r.downloaded; pre = r.preloaded; } catch (Exception) { }
            string dev = _w.DeviceId();
            string j = "{\"r\":" + next + ",\"dl\":" + (dl ? "true" : "false") + ",\"pre\":" + (pre ? "true" : "false") + "}";
            if (State.GetJson("rd." + dev) != j) State.Set("rd." + dev, j);
        }

        void Readiness(IPlaylistLeg leg, double now)
        {
            var row = leg.Row;
            int li = leg.Plan.legIndex;
            if (row.phase != PlaylistRunRules.Cleared && row.phase != PlaylistRunRules.LegDone) return;
            if (_advance >= li) { MaybeLan(leg); return; }
            if (_legDoneAt < 0) _legDoneAt = now;
            int next = li + 1;
            var members = State?.Members() ?? new List<string> { _w.DeviceId() };
            if (!members.Contains(_w.DeviceId())) members.Add(_w.DeviceId());
            var waiting = new List<string>();
            foreach (var m in members)
            {
                var r = SharedState.ParseFragment(State?.GetJson("rd." + m));
                bool ok = r != null && PlaylistRunPlan.Num(r, "r") >= next && PlaylistRunPlan.Bool(r, "dl", false) && PlaylistRunPlan.Bool(r, "pre", false);
                if (!ok) waiting.Add(m);
            }
            bool grace = now - _legDoneAt >= ReadyGraceSeconds;
            if (leg.Plan.IsLastLeg || waiting.Count == 0 || grace)
            {
                State?.Set("adv", "{\"i\":" + li + "}");
                _advance = li;
                Log($"round {li + 1} → {(leg.Plan.IsLastLeg ? "finale" : "round " + (next + 1))}: "
                    + (waiting.Count == 0 ? "every device is ready" : leg.Plan.IsLastLeg ? "last round" : $"grace {ReadyGraceSeconds:0} s passed (waiting: {string.Join(",", waiting)})"));
                AddLog("advance", null, waiting.Count == 0 ? "all ready" : "grace");
                MaybeLan(leg);
            }
        }

        readonly HashSet<int> _lanAsked = new HashSet<int>();
        void MaybeLan(IPlaylistLeg leg)
        {
            if (leg.Plan.IsLastLeg || leg.Row.phase != PlaylistRunRules.LegDone || _lanAsked.Contains(leg.Plan.legIndex)) return;
            bool server = true, phone = true;
            try { server = _w.ServerReachable(); } catch (Exception) { }
            try { phone = _w.PhonePresent(); } catch (Exception) { }
            string nx = null;
            var nxj = SharedState.ParseFragment(State?.GetJson("nx"));
            if (nxj != null && nxj.type == JSONObject.Type.String) nx = nxj.stringValue;
            // Sets (§ 3.1): the run's leader packs every set itself (the queue item is the whole playlist) — online or not.
            bool sets = leg.Plan.sets;
            if (sets && PlaylistSets.SetNumber(nx) <= 0) return;   // no next set asked for yet (the Lua decides)
            if (server && phone && !sets) return;   // the phone's queue advances (legComplete → complete {expectItemId})
            _lanAsked.Add(leg.Plan.legIndex);
            int next = leg.Plan.legIndex + 1;
            if (sets) next = PlaylistSets.SetNumber(nx) - 1;
            else if (nx != null)
                for (int i = 0; i < leg.Plan.legs.Count; i++) if (leg.Plan.legs[i]?.id == nx) next = i;
            string nextId = nx ?? leg.Plan.Leg(next)?.id;
            Log(sets ? $"set change: the leader packs set {next + 1} '{nextId}' (all at once)"
                : $"LAN advance ({(server ? "" : "no server")}{(!server && !phone ? ", " : "")}{(phone ? "" : "no phone")}): the leader starts round {next + 1} '{nextId}' itself");
            AddLog("lan_advance", null, nextId);
            // Every device follows the LAN round (core PlaylistAuthority attaches followers from it while the queue is behind).
            var lj = new JSONObject();
            lj.AddField("i", next); lj.AddField("id", nextId ?? ""); lj.AddField("pl", leg.Plan.playlistId ?? ""); lj.AddField("n", sets ? next + 1 : leg.Plan.LegCount);
            lj.AddField("owner", leg.Plan.owner ?? "title"); lj.AddField("title", leg.Plan.contentId ?? "");
            State?.Set("lan", lj.Print());
            try { _w.AdvanceLan(leg.Plan, next, nextId); } catch (Exception e) { Log("LAN advance threw: " + e.Message); }
            if (sets) return;   // a set is not a queue item: only the run's `complete` advances the queue
            // The queue's `complete` for this round, delivered by the headset queue route when the server answers (in order).
            PlaylistOutbox.Enqueue("queue_complete", "{\"runId\":\"" + SharedState.JsonEscape(RunId) + "\",\"playlistId\":\"" + SharedState.JsonEscape(leg.Plan.playlistId ?? "")
                + "\",\"legId\":\"" + SharedState.JsonEscape(leg.Plan.Current?.id ?? "") + "\",\"legIndex\":" + leg.Plan.legIndex
                + ",\"result\":\"" + SharedState.JsonEscape(string.IsNullOrEmpty(leg.Row.result) ? "objective" : leg.Row.result) + "\"}");
        }

        // Every device: preload the next round from this round's halfway point; unload rounds left behind.
        readonly HashSet<int> _preloaded = new HashSet<int>(), _unloaded = new HashSet<int>();
        void LocalRound(double now, IPlaylistLeg leg)
        {
            var plan = leg.Plan;
            int li = plan.legIndex;
            foreach (var l in _legs)
                if (l != leg && l?.Plan != null && l.Plan.legIndex < li && _unloaded.Add(l.Plan.legIndex))
                    try { _w.UnloadRound(l.Plan, l.Plan.legIndex); } catch (Exception) { }
            if (plan.IsLastLeg || _preloaded.Contains(li + 1) || leg.Row.phase != PlaylistRunRules.Playing) return;
            var stops = plan.Current?.stops ?? new List<PlaylistRunStop>();
            int over = 0;
            foreach (var s in stops) if (s != null && PlaylistRunRules.StopOver(leg.Progress.TryGetValue(s.occurrenceId ?? "", out var p) ? p : null)) over++;
            double minutes = plan.Current?.minutes ?? 0;
            bool half = (stops.Count > 0 && over * 2 >= stops.Count) || (minutes > 0 && leg.Row.t0 > 0 && now - leg.Row.t0 >= minutes * 30.0);
            if (!half) return;
            _preloaded.Add(li + 1);
            Log($"round {li + 1} is halfway: preloading round {li + 2}");
            try { _w.PreloadRound(plan, li + 1); } catch (Exception e) { Log("preload threw: " + e.Message); }
        }

        bool _played;
        void ApplyBadges()
        {
            if (State == null || !_played) return;   // a device only WATCHING the run (no round here yet) earns nothing
            var bd = SharedState.ParseFragment(State.GetJson("bd"));
            if (bd?.keys == null) return;
            string me = _w.DeviceId();
            for (int i = 0; i < bd.keys.Count; i++)
            {
                string who = bd.keys[i];
                if (who != "all" && who != me) continue;
                foreach (var x in bd.list[i]?.list ?? new List<JSONObject>())
                {
                    if (x == null || x.type != JSONObject.Type.String) continue;
                    if (!_badgesAwarded.Add(who + "|" + x.stringValue)) continue;
                    Log($"badge {x.stringValue} → this player ({who})");
                    var cp = Current?.Plan;   // the Results badge montage lists the playlist's badges: a run.badge joins them
                    if (cp != null && !cp.badges.Contains(x.stringValue)) cp.badges.Add(x.stringValue);
                    try { _w.AwardBadge(RunId, x.stringValue); } catch (Exception e) { Log("award threw: " + e.Message); }
                }
            }
        }

        // ── Offline cache ─────────────────────────────────────────────
        void SaveCache(double now, IPlaylistLeg leg)
        {
            if (leg == null || now < _nextCache) return;
            _nextCache = now + 2.0;
            try { _w.CacheWrite?.Invoke(RunId, PlaylistRunCache.Snapshot(this, leg, _w.DeviceId())); }
            catch (Exception e) { Log("cache write threw: " + e.Message); }
        }

        // ── The run log ───────────────────────────────────────────────
        void AddLog(string kind, string occ, string msg)
        {
            if (State == null || !IsLeader) return;
            var arr = SharedState.ParseFragment(State.GetJson("log"));
            if (arr == null || arr.type != JSONObject.Type.Array) arr = new JSONObject(JSONObject.Type.Array);
            var e = new JSONObject();
            e.AddField("t", Math.Round(_w.Clock(), 1)); e.AddField("k", kind ?? "");
            if (!string.IsNullOrEmpty(occ)) e.AddField("o", occ);
            if (!string.IsNullOrEmpty(msg)) e.AddField("m", msg.Length > 120 ? msg.Substring(0, 120) : msg);
            arr.Add(e);
            while (arr.count > MaxLog) arr.list.RemoveAt(0);
            State.Set("log", arr.Print());
        }

        void Log(string m) => _w.Log?.Invoke("[pl-runtime] " + RunId + ": " + m);

        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static string LegJson(PlaylistRunLeg l)
        {
            var j = new JSONObject();
            j.AddField("id", l?.id ?? ""); j.AddField("name", l?.name ?? ""); j.AddField("beat", l?.beat ?? "");
            return j.Print();
        }


        // ══ pl-sets (PLAYLIST-PLAN § 3): sets, challenge results + attempts, the ending, badges ═══════════════════

        /// The events every goal block's stop listens to for the attempt rules (forwarded to the leader).
        static readonly string[] AttemptEvents = { "challenge_enter", "challenge_leave", "reset_done", "challenge_skip_request" };
        /// A retry with no reset_done from the game after this long starts the next attempt anyway.
        public const double ResetFallbackSeconds = 6.0;
        bool _startPending;
        double _gateSince = -1;

        /// The set the playlist Lua put out last (the run's "set"), or null.
        public JSONObject SetJson => SharedState.ParseFragment(State?.GetJson("set"));
        /// The current set number (1-based; 0 = none / an arc[] record).
        public int SetNumber => PlaylistSets.SetNumber(Current?.Plan?.Current?.id);

        // ── attempts (leader) ─────────────────────────────────────────
        readonly Dictionary<string, PlaylistAttempt> _att = new Dictionary<string, PlaylistAttempt>(StringComparer.Ordinal);

        PlaylistAttempt Att(string occ)
        {
            if (_att.TryGetValue(occ ?? "", out var a)) return a;
            a = PlaylistAttempt.FromJson(SharedState.ParseFragment(State?.GetJson("at." + occ)));
            _att[occ ?? ""] = a;
            return a;
        }

        void SaveAtt(string occ, PlaylistAttempt a)
        {
            if (State == null || a == null) return;
            string j = a.ToJson();
            if (State.GetJson("at." + occ) != j) State.Set("at." + occ, j);
        }

        /// A stop's attempts as every device sees them ({n, e, l, w, sk, so, p{player: in|failed|finished}, in[]}), or null.
        public string AttemptJson(string occ) => State?.GetJson("at." + occ);
        /// § 3.2: the skip is on offer at this stop (10+ lost attempts and the last unfinished challenge of the set).
        public bool SkipAvailable(string occ) => PlaylistAttempt.FromJson(SharedState.ParseFragment(AttemptJson(occ))).skipOffered;

        double AttemptLimit(string occ) => PlaylistRunPlan.Num(SharedState.ParseFragment(State?.GetJson("g." + occ)), "al");

        void SetAttemptLimit(string occ, double sec)
        {
            if (State == null) return;
            var g = SharedState.ParseFragment(State.GetJson("g." + occ)) ?? new JSONObject();
            if (sec > 0) g.SetField("al", sec); else if (g.HasField("al")) g.RemoveField("al");
            State.Set("g." + occ, g.Print());
            var tm = TimersOf(occ);
            if (tm.Remove(-4)) SaveTimers(occ, tm);
        }

        /// The attempt's own clock (stop.time / the safety net): armed when an attempt starts.
        void ArmAttemptLimit(string occ, double now)
        {
            double al = AttemptLimit(occ);
            var tm = TimersOf(occ);
            if (al <= 0 || tm.ContainsKey(-4) || Att(occ).resetting || Att(occ).closed) return;
            tm[-4] = (now + al, 0);
            SaveTimers(occ, tm);
        }

        void DisarmAttemptLimit(string occ)
        {
            var tm = TimersOf(occ);
            if (tm.Remove(-4)) SaveTimers(occ, tm);
        }

        /// The leader's attempt events (presence, the game's reset, a skip request). True = consumed (still delivered).
        void AttemptEvent(PlaylistEvent e, double now)
        {
            if (e == null || e.source != PlaylistEvent.Event || string.IsNullOrEmpty(e.stop)) return;
            if (Array.IndexOf(AttemptEvents, e.name) < 0) return;
            var leg = Current;
            if (leg?.Plan?.Current?.stops?.Exists(x => x?.occurrenceId == e.stop) != true) return;
            string who = !string.IsNullOrEmpty(e.player) ? e.player : e.from;
            var a = Att(e.stop);
            switch (e.name)
            {
                case "challenge_enter": a.Enter(who); SaveAtt(e.stop, a); break;
                case "challenge_leave": { bool closed = a.Leave(who); SaveAtt(e.stop, a); if (closed) CloseAttempt(e.stop, "won, and the last player still playing walked out"); break; }
                case "reset_done":
                    if (a.ResetDone((int)PlaylistRunPlan.Num(e.Data, "epoch", a.epoch), now)) { SaveAtt(e.stop, a); AttemptStarted(e.stop, a, now, "reset_done"); }
                    break;
                case "challenge_skip_request": TrySkip(e.stop, who, "request"); break;
            }
        }

        void AttemptStarted(string occ, PlaylistAttempt a, double now, string why)
        {
            Log($"{occ}: attempt {a.n} starts (epoch {a.epoch}, {why}; inside: {string.Join(",", a.inside)})");
            AddLog("attempt", occ, a.n + "/" + a.epoch);
            ArmAttemptLimit(occ, now);
            if (_blocks.ContainsKey(occ) && _rt != null)
                Deliver(occ, new PlaylistEvent { source = "attempt", name = "start", dataJson = "{\"n\":" + a.n + ",\"epoch\":" + a.epoch + ",\"lost\":" + a.lost + "}", stop = occ, at = now });
        }

        /// Every tick (leader, playing): a retry whose game never answered reset_done starts anyway.
        void AttemptTick(IPlaylistLeg leg, double now)
        {
            // A player who left the session is out of every attempt (a won attempt may close: nobody is still playing).
            if (State != null && State.Synced)
            {
                var members = new HashSet<string>(State.Members() ?? new List<string>(), StringComparer.Ordinal) { _w.DeviceId() };
                foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
                {
                    if (s == null || !State.Has("at." + s.occurrenceId)) continue;
                    var a = Att(s.occurrenceId);
                    var gone = new List<string>();
                    foreach (var x in a.inside) if (!members.Contains(x)) gone.Add(x);
                    foreach (var kv in a.p) if (kv.Value == PlaylistAttempt.In && !members.Contains(kv.Key) && !gone.Contains(kv.Key)) gone.Add(kv.Key);
                    if (gone.Count == 0) continue;
                    bool closed = false;
                    foreach (var x in gone) closed |= a.Leave(x);
                    SaveAtt(s.occurrenceId, a);
                    Log($"{s.occurrenceId}: {string.Join(", ", gone)} left the session — out of the attempt");
                    if (closed) CloseAttempt(s.occurrenceId, "won, and the players still playing left");
                }
            }
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || !_att.TryGetValue(s.occurrenceId ?? "", out var a) || !a.resetting) continue;
                if (now - a.resetAt < ResetFallbackSeconds) continue;
                Log($"{s.occurrenceId}: no reset_done from the game in {ResetFallbackSeconds:0} s — the next attempt starts anyway");
                if (a.ResetDone(a.epoch, now)) { SaveAtt(s.occurrenceId, a); AttemptStarted(s.occurrenceId, a, now, "fallback"); }
            }
        }

        void AttemptWon(string occ, string by, JSONObject f)
        {
            if (Current?.Plan?.FindStop(occ, out _) == null) return;
            var a = Att(occ);
            string r = a.Won(by);
            if (r == "ignored") { Log($"{occ}: won{(by != null ? " by " + by : "")} ignored ({(a.resetting ? "resetting" : "closed")})"); return; }
            SaveAtt(occ, a);
            var d = new JSONObject(); d.AddField("occurrence", occ); d.AddField("by", by ?? "");
            SendChallengeEvent(occ, "challenge_won", d);
            if (r.StartsWith("first", StringComparison.Ordinal))
            {
                f.SetField("won", true);
                if (!string.IsNullOrEmpty(by)) f.SetField("by", by);
                f.SetField("attempts", a.n);
                EndStop(occ, "done", f.Print());
            }
            else Log($"{occ}: {by} finished too ({r})");
            if (a.closed) CloseAttempt(occ, "everyone inside is done");
        }

        void CloseAttempt(string occ, string why)
        {
            DisarmAttemptLimit(occ);
            Log($"{occ}: the attempt closes ({why})");
            if (_blocks.ContainsKey(occ)) EndBlock(occ);
        }

        void AttemptLost(string occ, string why, string player, string fieldsJson)
        {
            if (Current?.Plan?.FindStop(occ, out _) == null) return;
            var a = Att(occ);
            if (why == "time" && a.p.Count == 0 && a.inside.Count == 0 && !a.won && !a.resetting && !a.closed)
            {
                // Nobody was playing it: the clock simply starts again (an empty attraction never loses).
                DisarmAttemptLimit(occ); ArmAttemptLimit(occ, _w.Clock());
                return;
            }
            string r = a.Lost(why, player);
            switch (r)
            {
                case "ignored": Log($"{occ}: lost{{why='{why}'}} ignored ({(a.resetting ? "the attraction is resetting" : "closed")})"); return;
                case "out":
                    SaveAtt(occ, a);
                    Log($"{occ}: {player} is out of attempt {a.n} ({why}) — {a.Count(PlaylistAttempt.In)} still in");
                    AddLog("out", occ, player + ":" + why);
                    return;
                case "closed":
                    SaveAtt(occ, a);
                    CloseAttempt(occ, "won earlier, now everyone inside is done");
                    return;
            }
            // The attempt is lost: the game shows its fail beat and resets in place (§ 5.2); players retry.
            double now = _w.Clock();
            a.BeginRetry(now);
            SaveAtt(occ, a);
            DisarmAttemptLimit(occ);
            var d = new JSONObject(); d.AddField("occurrence", occ); d.AddField("why", why ?? "lost");
            if (!string.IsNullOrEmpty(player)) d.AddField("player", player);
            SendChallengeEvent(occ, "challenge_lost", d);
            var rd = new JSONObject(); rd.AddField("occurrence", occ); rd.AddField("epoch", a.epoch);
            SendChallengeEvent(occ, "challenge_retry", rd);
            var s = StopFor(occ);
            Log($"{occ} ({s?.attraction}): attempt {a.n} LOST ({why}{(player != null ? ", " + player : "")}) — retry, epoch {a.epoch}, {a.lost} lost so far");
            AddLog("lost", occ, why + " #" + a.lost);
            Moment("challenge_lost", "{\"stop\":\"" + SharedState.JsonEscape(occ) + "\",\"name\":\"" + SharedState.JsonEscape(s != null ? PlaylistObjectiveText.Name(s) : "")
                + "\",\"why\":\"" + SharedState.JsonEscape(why ?? "") + "\",\"lost\":" + a.lost + "}", occ, DreamieMoment.Low);
            if (_blocks.ContainsKey(occ) && _rt != null)
                Deliver(occ, new PlaylistEvent { source = "attempt", name = "lost", dataJson = "{\"why\":\"" + SharedState.JsonEscape(why ?? "") + "\",\"lost\":" + a.lost + ",\"epoch\":" + a.epoch + "}", stop = occ, at = now });
            CheckSkip(occ);
        }

        /// The other playable challenges of the set that are not over (locked ones wait on this one: they don't count).
        List<PlaylistRunStop> OpenOthers(string occ)
        {
            var l = new List<PlaylistRunStop>();
            var leg = Current;
            foreach (var x in leg?.Plan?.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (x == null || x.occurrenceId == occ || x.bookend) continue;
                if (PlaylistRunRules.StopOver(leg.Progress.TryGetValue(x.occurrenceId ?? "", out var p) ? p : null)) continue;
                if (LockOf(x.occurrenceId) != null || NativeLocked(x.occurrenceId)) continue;
                l.Add(x);
            }
            return l;
        }

        /// § 3.2 skip: 10+ lost attempts AND the last unfinished challenge of the set → on offer; otherwise Dreamie points at the
        /// other open challenges (once per 10 losses).
        void CheckSkip(string occ)
        {
            var a = Att(occ);
            if (a.won || a.skipOffered || a.lost < PlaylistSets.SkipAfterLosses) return;
            var others = OpenOthers(occ);
            var s = StopFor(occ);
            string name = s != null ? PlaylistObjectiveText.Name(s) : "";
            if (others.Count == 0)
            {
                a.skipOffered = true;
                SaveAtt(occ, a);
                var ch = SharedState.ParseFragment(State?.GetJson("ch." + occ)) ?? new JSONObject();
                ch.SetField("skip", true);
                State?.Set("ch." + occ, ch.Print());
                Log($"{occ}: {a.lost} lost attempts and the last open challenge of the set — the skip is on offer");
                AddLog("skip_offer", occ, a.lost.ToString(CultureInfo.InvariantCulture));
                Moment("skip_available", "{\"stop\":\"" + SharedState.JsonEscape(occ) + "\",\"name\":\"" + SharedState.JsonEscape(name) + "\",\"lost\":" + a.lost + "}", occ, DreamieMoment.High);
                if (_blocks.ContainsKey(occ) && _rt != null)
                    Deliver(occ, new PlaylistEvent { source = "attempt", name = "skip_available", dataJson = "{\"lost\":" + a.lost + "}", stop = occ, at = _w.Clock() });
            }
            else if (a.lost % PlaylistSets.SkipAfterLosses == 0 && _onceLocal.Add(occ + "|others|" + a.lost))
            {
                var arr = new JSONObject(JSONObject.Type.Array);
                foreach (var o in others) arr.Add(PlaylistObjectiveText.Name(o));
                Moment("stuck", "{\"stop\":\"" + SharedState.JsonEscape(occ) + "\",\"name\":\"" + SharedState.JsonEscape(name) + "\",\"lost\":" + a.lost + ",\"others\":" + arr.Print() + "}", occ, DreamieMoment.Normal);
                Log($"{occ}: {a.lost} lost attempts — not the last open challenge: Dreamie points at {string.Join(", ", others.ConvertAll(o => o.attraction))}");
            }
        }

        /// stop.skip() / run.skip(ref) / a player's challenge_skip_request: counts as WON — only when on offer.
        bool TrySkip(string occ, string by, string from)
        {
            var leg = Current;
            if (leg?.Plan?.FindStop(occ, out _) == null) return false;
            var a = Att(occ);
            if (!a.skipOffered) CheckSkip(occ);
            if (!a.skipOffered || a.won)
            {
                Log($"{occ}: skip ({from}) refused — {(a.won ? "already won" : a.lost + " lost attempts" + (a.lost >= PlaylistSets.SkipAfterLosses ? ", other challenges are still open" : ""))}");
                return false;
            }
            SkipNow(occ, a, by, from, null);
            return true;
        }

        /// The skip itself (stop.skip / run.skip / a broken challenge): won, skipped; the game hears challenge_skip.
        void SkipNow(string occ, PlaylistAttempt a, string by, string from, JSONObject extra)
        {
            a.skipped = true; a.won = true; a.closed = true; a.resetting = false;
            SaveAtt(occ, a);
            var d = new JSONObject(); d.AddField("occurrence", occ);
            SendChallengeEvent(occ, "challenge_skip", d);
            var f = new JSONObject(); f.AddField("won", true); f.AddField("skipped", true); f.AddField("attempts", a.n); f.AddField("lost", a.lost);
            if (!string.IsNullOrEmpty(by)) f.AddField("by", by);
            if (extra?.keys != null) for (int i = 0; i < extra.keys.Count; i++) f.SetField(extra.keys[i], extra.list[i]);
            Log($"{occ}: SKIPPED ({from}) after {a.lost} lost attempts — counts as won");
            EndStop(occ, "done", f.Print());
            CloseAttempt(occ, "skipped");
        }

        // ── a challenge that cannot be won (pl-sets, lead decision Oct 22) ─────────────────────────
        // A goal block (or the game) says `challenge_broken {occurrence, why}` — e.g. it counts announced objects and the
        // content announces none (old content, 0/0). The leader skips it at once, exactly like stop.skip: its waypoint and the
        // overworld count it done (skipped), the run moves on. The why is logged, kept in the run's table ("brk") for
        // PLAYLIST_STATE, and reported through the outbox (play_events stop_result, result "broken") as a content bug.
        public const int MaxBroken = 8;

        static bool IsBroken(PlaylistEvent e) => e != null && e.source == PlaylistEvent.Event && e.name == "challenge_broken";

        void ChallengeBroken(PlaylistEvent e)
        {
            var data = SharedState.ParseFragment(e.dataJson) ?? new JSONObject();
            string occ = PlaylistRunPlan.Str(data, "occurrence", null) ?? e.stop;
            var leg = Current;
            var s = occ != null ? StopFor(occ) : null;
            if (leg == null || s == null || State == null) { Log($"challenge_broken for '{occ}': no such stop in the set that stands — ignored"); return; }
            var a = Att(occ);
            if (a.won || a.skipped) return;   // already over (a repeat of the same report)
            string why = PlaylistRunPlan.Str(data, "why", null) ?? "unknown";
            if (why.Length > 160) why = why.Substring(0, 160);
            Log($"ERROR {occ} ({PlaylistAutoRules.Leaf(s.attraction)}): challenge_broken — {why} — skipped (a content bug: reported)");
            ReportBroken(s, occ, why, leg.Plan, leg.Plan.Current?.id, leg.Plan.legIndex);
            var extra = new JSONObject(); extra.AddField("broken", true); extra.AddField("why", why);
            SkipNow(occ, a, null, "challenge_broken", extra);
        }

        /// A stop that cannot be played (challenge_broken, or its set could not be placed): logged, kept in the run's table
        /// ("brk" → PLAYLIST_STATE broken) and reported through the outbox (play_events stop_result "broken" + why).
        void ReportBroken(PlaylistRunStop s, string occ, string why, PlaylistRunPlan plan, string legId, int legIndex)
        {
            string att = PlaylistAutoRules.Leaf(s?.attraction) ?? "";
            AddLog("broken", occ, why);
            // the run's table: PLAYLIST_STATE "broken"
            var arr = SharedState.ParseFragment(State.GetJson("brk"));
            if (arr == null || arr.type != JSONObject.Type.Array) arr = new JSONObject(JSONObject.Type.Array);
            var bj = new JSONObject(); bj.AddField("occurrence", occ); bj.AddField("attraction", att); bj.AddField("why", why);
            arr.Add(bj);
            while (arr.count > MaxBroken) arr.list.RemoveAt(0);
            State.Set("brk", arr.Print());
            // the run's report (Web play_events, headset source): a stop_result whose result is "broken", with the why
            var ev = new JSONObject();
            ev.AddField("type", "stop_result"); ev.AddField("runId", RunId ?? ""); ev.AddField("playlistId", plan?.playlistId ?? "");
            ev.AddField("owner", plan?.owner ?? "title"); ev.AddField("titleId", plan?.contentId ?? "");
            ev.AddField("legId", legId ?? ""); ev.AddField("legIndex", legIndex);
            ev.AddField("occurrenceId", occ); ev.AddField("stop", s?.stopId ?? occ); ev.AddField("attraction", att);
            ev.AddField("result", "broken"); ev.AddField("why", why); ev.AddField("source", "headset");
            ev.AddField("atMs", (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds);
            var evs = new JSONObject(JSONObject.Type.Array); evs.Add(ev);
            var body = new JSONObject(); body.AddField("events", evs);
            try { _w.ReportBroken?.Invoke(body.Print()); } catch (Exception ex) { Log("broken report threw: " + ex.Message); }
        }

        // ── a set that cannot be placed here (pl-sets, Oct 22: Meadow set 4 in one room) ───────────────────────────────
        // The pack reports the stops it found no room for (PlaylistRunWorld.SetUnplaced). The leader never waits on them:
        // a stop that did not fit is skipped like challenge_broken (logged + reported, its waypoint done as skipped) — once
        // its set stands when the rest of the set placed; when NOTHING placed, the set passes at once (its on_set_done runs,
        // the Lua puts out the next one, numbered after it so every device packs it fresh).
        readonly Dictionary<string, string> _unplaced = new Dictionary<string, string>(StringComparer.Ordinal);
        int _setPassed;

        internal void OnSetUnplaced(string setId, List<(string occ, string attraction, string why)> stops, bool nothingPlaced)
        {
            int n = PlaylistSets.SetNumber(setId);
            if (State == null || !IsLeader || n <= 0 || stops == null || stops.Count == 0) return;   // the leader decides
            if (!nothingPlaced)
            {
                foreach (var x in stops) if (!string.IsNullOrEmpty(x.occ) && !_unplaced.ContainsKey(x.occ)) _unplaced[x.occ] = x.why;
                Log($"{setId}: {stops.Count} attraction(s) did not fit here — skipped once the set stands: {string.Join(", ", stops.ConvertAll(x => x.attraction))}");
                return;
            }
            if (_setPassed >= n) return;   // once per set
            _setPassed = n;
            var plan = Plan;
            var set = PlaylistSets.Find(plan?.playlistId, setId);
            var leg = set != null ? PlaylistSets.Leg(plan?.playlistId, set) : null;
            var refs = new JSONObject(JSONObject.Type.Array);
            foreach (var x in stops)
            {
                var st = leg?.stops.Find(y => y != null && y.occurrenceId == x.occ) ?? new PlaylistRunStop { occurrenceId = x.occ, attraction = x.attraction };
                string why = string.IsNullOrEmpty(x.why) ? "no room it fits in here" : x.why;
                Log($"ERROR {x.occ} ({PlaylistAutoRules.Leaf(st.attraction)}): could not be placed — {why} — skipped");
                ReportBroken(st, x.occ, why, plan, setId, n - 1);
                var r = new JSONObject(); r.AddField("result", "done"); r.AddField("skipped", true); r.AddField("broken", true); r.AddField("why", why);
                State.Set("res." + x.occ, r.Print());
                MarkWaypoint(st, "skipped");
                refs.Add(PlaylistSets.RefOf(st));
            }
            Log($"{setId}: nothing in it could be placed here — it passes (skipped), the playlist puts out the next");
            AddLog("set_unplaced", null, setId);
            var sum = new JSONObject();
            sum.AddField("id", setId); sum.AddField("name", leg?.name ?? ""); sum.AddField("beat", leg?.beat ?? ""); sum.AddField("n", n);
            sum.AddField("attractions", refs); sum.AddField("won", new JSONObject(JSONObject.Type.Array)); sum.AddField("open", new JSONObject(JSONObject.Type.Array));
            sum.AddField("skipped", refs); sum.AddField("lost", new JSONObject());
            var cur = Current;
            if (cur != null) _lanAsked.Remove(cur.Plan.legIndex);   // ask again for whatever the Lua puts out next
            var (st0, _) = CallRun("on_set_done", sum.Print());
            if (st0 != null) SaveRunState(st0);
        }

        /// Leader: stops of the standing set that did not fit → skipped like challenge_broken.
        void ApplyUnplaced(IPlaylistLeg leg)
        {
            if (_unplaced.Count == 0) return;
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || !_unplaced.TryGetValue(s.occurrenceId ?? "", out string why)) continue;
                _unplaced.Remove(s.occurrenceId);
                var a = Att(s.occurrenceId);
                if (a.won || a.skipped) continue;
                why = string.IsNullOrEmpty(why) ? "no room it fits in here" : why;
                Log($"ERROR {s.occurrenceId} ({PlaylistAutoRules.Leaf(s.attraction)}): could not be placed — {why} — skipped");
                ReportBroken(s, s.occurrenceId, why, leg.Plan, leg.Plan.Current?.id, leg.Plan.legIndex);
                var extra = new JSONObject(); extra.AddField("broken", true); extra.AddField("why", why);
                SkipNow(s.occurrenceId, a, null, "unplaced", extra);
            }
        }

        /// PLAYLIST_STATE stops[] (ios-sets): (state won | skipped | open, players inside, retrying) of a stop, null = unknown
        /// here (no attempt yet: StateJson falls back to the stop's progress). Any device (the attempt is in the run's table).
        public (string state, int inside, bool retrying)? StopStatus(string occ)
        {
            if (string.IsNullOrEmpty(occ)) return null;
            PlaylistAttempt a = null;
            if (IsLeader && _att.TryGetValue(occ, out var mine)) a = mine;
            else if (State != null && State.Has("at." + occ)) a = PlaylistAttempt.FromJson(SharedState.ParseFragment(State.GetJson("at." + occ)));
            var res = SharedState.ParseFragment(State?.GetJson("res." + occ));
            bool resSkipped = res?.GetField("skipped")?.boolValue == true;
            bool resWon = PlaylistRunPlan.Str(res, "result", "") == "done";
            if (a == null && res == null) return null;
            string state = (a != null && a.skipped) || resSkipped ? "skipped" : (a != null && a.won) || resWon ? "won" : "open";
            bool over = state != "open";
            return (state, over ? 0 : a?.inside.Count ?? 0, !over && (a?.resetting ?? false));
        }

        /// What the run reported broken (PLAYLIST_STATE "broken"): [{occurrence, attraction, why}], null when none.
        public string BrokenJson => State?.GetJson("brk");

        // ── challenge events (§ 3.2a): runtime → game, on EVERY device ──
        public const int MaxChallengeEvents = 8;
        readonly Dictionary<string, int> _ceSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        IPlaylistLeg _ceLeg;

        void SendChallengeEvent(string occ, string name, JSONObject data)
        {
            if (State == null) { var st = StopFor(occ); try { _w.Deliver?.Invoke(st, st != null ? Current?.StopRoot(st) : null, name, data.Print()); } catch (Exception) { } return; }
            var arr = SharedState.ParseFragment(State.GetJson("ce." + occ));
            if (arr == null || arr.type != JSONObject.Type.Array) arr = new JSONObject(JSONObject.Type.Array);
            int q = 0;
            foreach (var x in arr.list ?? new List<JSONObject>()) q = Math.Max(q, (int)PlaylistRunPlan.Num(x, "q"));
            var e = new JSONObject(); e.AddField("q", q + 1); e.AddField("n", name); e.AddField("d", data);
            arr.Add(e);
            while (arr.count > MaxChallengeEvents) arr.list.RemoveAt(0);
            State.Set("ce." + occ, arr.Print());
        }

        // ── waypoints (PLAYLIST-PLAN § 6.7, pl-sets) ───────────────────────────
        // Every attraction broadcasts itself as a waypoint; the playlist decides which COUNT: run.waypoint(ref, true|false).
        // Nothing is assigned by default. The assigned set lives in the run's table ("wp": [{ref, done, how}] in assignment
        // order) — shared, late-join safe, a new leader keeps it. Done (sticky for the run): its challenge won / skipped, a
        // native stop beaten, or the game announced it done (every waypoint announced under the attraction is done).

        /// run.waypoint(ref, on): ref = a pool id or an attraction. Unknown → false.
        string WaypointOp(string reference, string json)
        {
            if (State == null || string.IsNullOrEmpty(reference)) return "false";
            var o = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json);
            bool on = o?.GetField("on") == null || o.GetField("on").type != JSONObject.Type.Bool || o.GetField("on").boolValue;
            string key = WaypointKey(reference);
            if (key == null) { Log($"run.waypoint('{reference}'): not in this playlist (no pool entry / stop / attraction of that name) — ignored"); return "false"; }
            var arr = WaypointRows();
            int at = arr.FindIndex(x => string.Equals(PlaylistRunPlan.Str(x, "ref", ""), key, StringComparison.OrdinalIgnoreCase));
            if (on && at < 0)
            {
                var row = new JSONObject(); row.AddField("ref", key); row.AddField("done", false);
                arr.Add(row);
                // a stop already beaten counts at once
                foreach (var st in Current?.Plan?.Current?.stops ?? new List<PlaylistRunStop>())
                    if (PlaylistSets.Matches(st, key) && StopBeaten(st)) { row.SetField("done", true); row.SetField("how", "won"); }
            }
            else if (!on && at >= 0) arr.RemoveAt(at);
            else return "true";
            SaveWaypoints(arr);
            Log($"run.waypoint('{key}', {(on ? "true" : "false")}): {arr.Count} waypoint(s) count");
            return "true";
        }

        /// A ref → its canonical key: a pool id / stop id as written there (an attraction naming exactly one entry → that id),
        /// else the attraction's leaf name. Null = unknown.
        string WaypointKey(string reference)
        {
            var plan = Plan;
            var all = new List<PlaylistRunStop>(plan?.pool ?? new List<PlaylistRunStop>());
            foreach (var l in plan?.legs ?? new List<PlaylistRunLeg>()) all.AddRange(l?.stops ?? new List<PlaylistRunStop>());
            foreach (var st in all) if (st != null && string.Equals(st.stopId, reference, StringComparison.OrdinalIgnoreCase)) return st.stopId;
            // an attraction name: the pool id when it names exactly one entry (so 'A_WetFloor' and 'wet-floor' are one waypoint)
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool any = false;
            foreach (var st in all) if (PlaylistSets.Matches(st, reference)) { any = true; ids.Add(st.stopId ?? ""); }
            if (!any) return null;
            if (ids.Count == 1) { foreach (var id in ids) if (!string.IsNullOrEmpty(id)) return id; }
            return PlaylistAutoRules.Leaf(reference);
        }

        List<JSONObject> WaypointRows()
        {
            var j = SharedState.ParseFragment(State?.GetJson("wp"));
            var l = new List<JSONObject>();
            foreach (var x in j?.list ?? new List<JSONObject>()) if (x != null && x.type == JSONObject.Type.Object) l.Add(x);
            return l;
        }

        void SaveWaypoints(List<JSONObject> rows)
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var r in rows) arr.Add(r);
            string j = arr.Print();
            if (State != null && State.GetJson("wp") != j) State.Set("wp", j);
        }

        /// The leader: an assigned waypoint matching this stop is done (won / skipped / native / announced).
        void MarkWaypoint(PlaylistRunStop s, string how)
        {
            if (s == null || State == null || !State.Has("wp")) return;
            var rows = WaypointRows();
            bool changed = false;
            foreach (var r in rows)
            {
                if (r.GetField("done")?.boolValue == true || !PlaylistSets.Matches(s, PlaylistRunPlan.Str(r, "ref", ""))) continue;
                r.SetField("done", true); r.SetField("how", how); changed = true;
                Log($"waypoint '{PlaylistRunPlan.Str(r, "ref", "")}' done ({how})");
                AddLog("waypoint_done", s.occurrenceId, how);
            }
            if (changed) SaveWaypoints(rows);
        }

        bool StopBeaten(PlaylistRunStop s)
        {
            var leg = Current;
            if (s == null || leg == null) return false;
            if (PlaylistRunPlan.Str(SharedState.ParseFragment(State?.GetJson("res." + s.occurrenceId)), "result", "") == "done") return true;
            return leg.Progress.TryGetValue(s.occurrenceId ?? "", out var p) && p != null && p.done && !p.timedOut && !p.resultFailed;
        }

        double _nextWp;
        /// The leader, playing: native stops beaten, and the game's own "done" (pl-announce: the waypoints announced under the
        /// attraction, all done).
        void WaypointTick(IPlaylistLeg leg, double now)
        {
            if (State == null || !State.Has("wp") || now < _nextWp) return;
            _nextWp = now + 0.5;
            var rows = WaypointRows();
            foreach (var st in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (st == null) continue;
                bool open = false;
                foreach (var r in rows) if (r.GetField("done")?.boolValue != true && PlaylistSets.Matches(st, PlaylistRunPlan.Str(r, "ref", ""))) open = true;
                if (!open) continue;
                if (StopBeaten(st)) { MarkWaypoint(st, "won"); rows = WaypointRows(); continue; }
                if (AnnouncedDone(leg, st)) { MarkWaypoint(st, "announced"); rows = WaypointRows(); }
            }
        }

        bool AnnouncedDone(IPlaylistLeg leg, PlaylistRunStop st)
        {
            try
            {
                var c = SharedState.ParseFragment(_w.Count?.Invoke(st, leg.StopRoot(st), "waypoint"));
                return PlaylistRunPlan.Num(c, "total") > 0 && PlaylistRunPlan.Num(c, "remaining") <= 0;
            }
            catch (Exception) { return false; }
        }

        /// The assigned waypoints (the map, Dreamie's guidance), any device: in assignment order, each with its standing
        /// occurrence on THIS device (null when its stop is not out now) and attraction.
        public List<PlaylistWaypoint> Waypoints
        {
            get
            {
                var l = new List<PlaylistWaypoint>();
                if (State == null) return l;
                var plan = Plan;
                foreach (var r in WaypointRows())
                {
                    string key = PlaylistRunPlan.Str(r, "ref", "");
                    var w = new PlaylistWaypoint { reference = key, done = r.GetField("done")?.boolValue == true, how = PlaylistRunPlan.Str(r, "how", null) };
                    foreach (var st in plan?.Current?.stops ?? new List<PlaylistRunStop>())
                        if (PlaylistSets.Matches(st, key)) { w.occurrenceId = st.occurrenceId; w.attraction = PlaylistAutoRules.Leaf(st.attraction); break; }
                    if (w.attraction == null)
                        foreach (var st in plan?.pool ?? new List<PlaylistRunStop>())
                            if (PlaylistSets.Matches(st, key)) { w.attraction = PlaylistAutoRules.Leaf(st.attraction); break; }
                    l.Add(w);
                }
                // dp.map marks on attractions are waypoints too (the map's done / not-done attraction marks)
                foreach (var m in MapWaypoints())
                {
                    var same = l.Find(x => string.Equals(x.reference, m.reference, StringComparison.OrdinalIgnoreCase));
                    if (same == null) l.Add(m);
                    else if (m.done && !same.done) { same.done = true; same.how = m.how; }
                }
                return l;
            }
        }

        /// The assigned waypoint this standing stop is, or null (not assigned: the map leaves it out).
        public PlaylistWaypoint WaypointFor(string occurrenceId)
        {
            foreach (var w in Waypoints) if (w.occurrenceId == occurrenceId && !string.IsNullOrEmpty(occurrenceId)) return w;
            return null;
        }

        /// PLAYLIST_STATE "waypoints": [{ref, attraction, occurrence, done}] — null when none are assigned.
        public string WaypointsJson()
        {
            var ws = Waypoints;
            if (ws.Count == 0) return null;
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var w in ws)
            {
                var j = new JSONObject(); j.AddField("ref", w.reference ?? ""); j.AddField("attraction", w.attraction ?? "");
                j.AddField("occurrence", w.occurrenceId ?? ""); j.AddField("done", w.done);
                arr.Add(j);
            }
            return arr.Print();
        }

        /// stop.send(name, data) (pl-sets): a goal block's own event to its attraction's objects (onevent / dp.on) on EVERY device,
        /// through the challenge-event channel — in order, once per device. Names: letters / digits / _ / . / -, ≤ 40, not
        /// `challenge*` (the platform's own). data ≤ 1 KB (bigger keeps its scalar fields); `occurrence` is filled in.
        string SendOp(string occ, string json)
        {
            var o = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json);
            string name = PlaylistRunPlan.Str(o, "name", "");
            if (string.IsNullOrEmpty(occ) || StopFor(occ) == null) { Log($"stop.send('{name}'): no such stop here"); return "false"; }
            bool okName = name.Length > 0 && name.Length <= 40 && !name.StartsWith("challenge", StringComparison.Ordinal);
            foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) okName = false;
            if (!okName) { Log($"{occ}: stop.send('{name}') refused — a name is letters / digits / _ . -, ≤ 40, not challenge*"); return "false"; }
            var dj = o?.GetField("data");
            // an object only (Lua's empty table arrives as [])
            var d = SharedState.ParseFragment(PlaylistEvents.Clamp(dj != null && dj.type == JSONObject.Type.Object ? dj.Print() : "{}", 1024)) ?? new JSONObject();
            if (d.GetField("occurrence") == null) d.AddField("occurrence", occ);
            SendChallengeEvent(occ, name, d);
            AddLog("send", occ, name);
            Log($"{occ}: stop.send('{name}') → its attraction on every headset");
            return "true";
        }

        /// Every device: deliver the leader's challenge events to this device's copy of each attraction (in order, once).
        /// A device that arrives late (or resumes) gets the challenge as it stands now — never old wins / losses.
        void DeliverChallengeEvents(IPlaylistLeg leg)
        {
            if (State == null || !State.Synced) return;
            bool fresh = _ceLeg != leg;
            if (fresh) { _ceLeg = leg; _ceSeen.Clear(); }
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || string.IsNullOrEmpty(s.occurrenceId)) continue;
                string occ = s.occurrenceId;
                var arr = SharedState.ParseFragment(State.GetJson("ce." + occ));
                int max = 0;
                foreach (var x in arr?.list ?? new List<JSONObject>()) max = Math.Max(max, (int)PlaylistRunPlan.Num(x, "q"));
                if (!_ceSeen.TryGetValue(occ, out int seen))
                {
                    if (fresh && max > 0)
                    {
                        // Late / resumed: the challenge as it is now (kind, options, its current epoch), not the history.
                        var ch = SharedState.ParseFragment(State.GetJson("ch." + occ));
                        if (ch != null)
                        {
                            var at = PlaylistAttempt.FromJson(SharedState.ParseFragment(State.GetJson("at." + occ)));
                            var d = new JSONObject(); d.AddField("occurrence", occ); d.AddField("kind", PlaylistRunPlan.Str(ch, "kind", ""));
                            d.AddField("options", ch.GetField("options") ?? new JSONObject()); d.AddField("epoch", at.epoch); d.AddField("late", true);
                            if (at.won) d.AddField("won", true);
                            DeliverLocal(leg, s, "challenge", d.Print());
                        }
                        _ceSeen[occ] = max;
                        continue;
                    }
                    seen = 0;
                }
                foreach (var x in arr?.list ?? new List<JSONObject>())
                {
                    int q = (int)PlaylistRunPlan.Num(x, "q");
                    if (q <= seen) continue;
                    seen = q;
                    DeliverLocal(leg, s, PlaylistRunPlan.Str(x, "n", ""), x.GetField("d")?.Print() ?? "{}");
                }
                _ceSeen[occ] = seen;
            }
        }

        /// § 3.2a on THIS device, after the attraction got it: (run, stop, name, data JSON) — challenge, challenge_won {by},
        /// challenge_lost {why}, challenge_retry {epoch}, challenge_skip. Platform listeners (the round diorama's map pop-up
        /// after any win, a friend's included) hook here; games use onevent / dp.on.
        public static event Action<PlaylistRun, PlaylistRunStop, string, string> ChallengeDelivered;

        void DeliverLocal(IPlaylistLeg leg, PlaylistRunStop s, string name, string json)
        {
            GameObject root = null;
            try { root = leg.StopRoot(s); } catch (Exception) { }
            try { _w.Deliver?.Invoke(s, root, name, json); }
            catch (Exception e) { Log($"{s.occurrenceId}: delivering {name} threw: {e.Message}"); }
            try { ChallengeDelivered?.Invoke(this, s, name, json); }
            catch (Exception e) { Log($"{s.occurrenceId}: a ChallengeDelivered listener threw: {e.Message}"); }
        }

        /// Is this player inside an OPEN attempt at some stop of the current set (playing a challenge right now)? The map
        /// pop-up queues until it is false (§ 2). Every device reads the shared attempts.
        public bool InAttempt(string player)
        {
            if (string.IsNullOrEmpty(player)) return false;
            foreach (var st in Current?.Plan?.Current?.stops ?? new List<PlaylistRunStop>())
            {
                var a = PlaylistAttempt.FromJson(SharedState.ParseFragment(AttemptJson(st?.occurrenceId)));
                if (a.closed || a.resetting) continue;
                if (a.p.TryGetValue(player, out var v) && v == PlaylistAttempt.In) return true;
            }
            return false;
        }

        /// Leader: each live stop of the set gets its challenge once ("ch.{occ}" + the `challenge` event).
        void SendChallenges(IPlaylistLeg leg)
        {
            if (State == null) return;
            foreach (var s in leg.Plan.Current?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null || string.IsNullOrEmpty(s.occurrenceId) || s.bookend || State.Has("ch." + s.occurrenceId)) continue;
                if (!leg.IsLive(s)) continue;
                string kind = !string.IsNullOrEmpty(s.challenge) ? s.challenge : PlaylistChallenge.KindOf(leg.Plan.Current, s);
                var opt = string.IsNullOrEmpty(s.options) ? null : SharedState.ParseFragment(s.options);
                var ch = new JSONObject(); ch.AddField("kind", kind ?? "");
                ch.AddField("options", opt != null && opt.type == JSONObject.Type.Object ? opt : new JSONObject());
                State.Set("ch." + s.occurrenceId, ch.Print());
                var d = new JSONObject(); d.AddField("occurrence", s.occurrenceId); d.AddField("kind", kind ?? "");
                d.AddField("options", opt != null && opt.type == JSONObject.Type.Object ? new JSONObject(opt.Print()) : new JSONObject()); d.AddField("epoch", 0);
                SendChallengeEvent(s.occurrenceId, "challenge", d);
                Log($"{s.occurrenceId} ({s.attraction}): challenge '{kind}' {(opt != null ? opt.Print() : "{}")}");
            }
        }

        // ── sets (§ 3.1) ─────────────────────────────────────────────

        static string SetSummaryOf(IPlaylistLeg leg, PlaylistRun run)
        {
            var j = new JSONObject();
            var l = leg?.Plan?.Current;
            j.AddField("id", l?.id ?? ""); j.AddField("name", l?.name ?? ""); j.AddField("beat", l?.beat ?? "");
            j.AddField("n", PlaylistSets.SetNumber(l?.id));
            var all = new JSONObject(JSONObject.Type.Array); var won = new JSONObject(JSONObject.Type.Array); var open = new JSONObject(JSONObject.Type.Array);
            var lost = new JSONObject();
            foreach (var s in l?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                string r = PlaylistSets.RefOf(s);
                all.Add(r);
                var p = leg.Progress.TryGetValue(s.occurrenceId ?? "", out var pp) ? pp : null;
                if (p != null && p.done) won.Add(r); else if (!PlaylistRunRules.StopOver(p)) open.Add(r);
                var a = PlaylistAttempt.FromJson(SharedState.ParseFragment(run?.AttemptJson(s.occurrenceId)));
                if (a.lost > 0) lost.AddField(r, a.lost);
            }
            j.AddField("attractions", all); j.AddField("won", won); j.AddField("open", open); j.AddField("lost", lost);
            return j.Print();
        }
        string SetSummary(IPlaylistLeg leg) => SetSummaryOf(leg, this);

        /// Leader, sets mode, playing: every challenge of the set is over → on_set_done once (the Lua shows the next set or
        /// finishes); offers the skip where a challenge became the last open one.
        void SetTick(IPlaylistLeg leg, double now)
        {
            if (!leg.Plan.sets || State == null) return;
            var stops = leg.Plan.Current?.stops ?? new List<PlaylistRunStop>();
            bool all = stops.Count > 0;
            foreach (var s in stops)
            {
                if (s == null) continue;
                var p = leg.Progress.TryGetValue(s.occurrenceId ?? "", out var pp) ? pp : null;
                if (!PlaylistRunRules.StopOver(p)) all = false;
                else
                {
                    // Won, but others are still inside its attempt: the set waits for them (§ 3.2 — one winner never ends it).
                    var open = PlaylistAttempt.FromJson(SharedState.ParseFragment(AttemptJson(s.occurrenceId)));
                    if (open.won && !open.closed && !p.timedOut) all = false;
                    continue;
                }
                if (_att.TryGetValue(s.occurrenceId ?? "", out var a) && a.lost >= PlaylistSets.SkipAfterLosses && !a.skipOffered) CheckSkip(s.occurrenceId);
            }
            int n = PlaylistSets.SetNumber(leg.Plan.Current?.id);
            if (!all || (int)PlaylistRunPlan.Num(SharedState.ParseFragment(State.GetJson("sd")), "n") == n || State.Has("rf")) return;
            State.Set("sd", "{\"n\":" + n + "}");
            Log($"set {n} done: every challenge is over — on_set_done");
            AddLog("set_done", null, "set" + n);
            var (st, _) = CallRun("on_set_done", SetSummary(leg));
            if (st != null) SaveRunState(st);
        }

        /// Leader, a sets-mode leg just became current: "set" = this set (a preview / resume / follower pack), its locks.
        void AdoptSet(IPlaylistLeg leg)
        {
            int n = PlaylistSets.SetNumber(leg.Plan.Current?.id);
            if (n <= 0 || State == null) return;
            var set = SharedState.ParseFragment(State.GetJson("set"));
            if (set == null || (int)PlaylistRunPlan.Num(set, "n") < n)
            {
                set = PlaylistSets.Find(leg.Plan.playlistId, leg.Plan.Current.id);
                if (set == null)
                {
                    // Rebuilt from the standing leg (a resume): its refs, as they stand.
                    var req = new JSONObject(); var arr = new JSONObject(JSONObject.Type.Array);
                    foreach (var s in leg.Plan.Current.stops) if (s != null) arr.Add(PlaylistSets.RefOf(s));
                    req.AddField("attractions", arr);
                    set = PlaylistSets.BuildSet(n, leg.Plan.pool.Count > 0 ? leg.Plan.pool : leg.Plan.Current.stops, req, null);
                }
                if (!ShareSet(set, "the set that stands")) return;
                PlaylistSets.Register(leg.Plan.playlistId, set);
            }
            ApplySetLocks(leg, set);
        }

        void ApplySetLocks(IPlaylistLeg leg, JSONObject set)
        {
            if (State == null || set == null || (int)PlaylistRunPlan.Num(set, "n") != PlaylistSets.SetNumber(leg.Plan.Current?.id)) return;
            var refs = PlaylistSets.Refs(set);
            int i = 0, n = (int)PlaylistRunPlan.Num(set, "n");
            foreach (var sj in set.GetField("stops")?.list ?? new List<JSONObject>())
            {
                var lk = sj?.GetField("lock");
                string occ = PlaylistSets.OccurrenceId(leg.Plan.playlistId, n, PlaylistSets.IndexOf(sj, i));
                var st = leg.Plan.Current.stops.Find(x => x != null && x.occurrenceId == occ);
                i++;
                if (lk == null || st == null || State.Has("lk." + st.occurrenceId)) continue;
                var needs = new JSONObject(JSONObject.Type.Array);
                var nj = lk.GetField("needs");
                if (nj?.list != null && nj.list.Count > 0) { foreach (var x in nj.list) if (x != null && x.type == JSONObject.Type.String) needs.Add(x.stringValue); }
                else foreach (var r in refs) if (r != PlaylistRunPlan.Str(sj, "ref", "")) needs.Add(r);
                var lj = new JSONObject(); lj.AddField("needs", needs);
                string sign = PlaylistRunPlan.Str(lk, "sign", null) ?? st.lockSign;
                if (!string.IsNullOrEmpty(sign)) lj.AddField("sign", sign);
                State.Set("lk." + st.occurrenceId, lj.Print());
                Log($"{st.occurrenceId} ({st.attraction}): locked by its set until {needs.Print()}");
                AddLog("lock", st.occurrenceId, "set");
            }
        }

        /// run.show / run.add / run.remove (leader). Sets mode: the next set, all at once (the current leg clears; the
        /// leader packs the set through the round-swap seam). An arc[] record: `round = <id>` names the next round.
        string ShowOp(string op, string json)
        {
            var leg = Current;
            if (leg == null || State == null) return "false";
            var req = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json) ?? new JSONObject();
            var plan = leg.Plan;
            if (!plan.sets)
            {
                string round = PlaylistRunPlan.Str(req, "round", null);
                if (round == null) { var a0 = req.GetField("attractions")?.list; if (a0 != null && a0.Count > 0 && a0[0]?.type == JSONObject.Type.String) round = a0[0].stringValue; }
                int li = -1;
                for (int i = 0; i < plan.legs.Count; i++)
                    if (plan.legs[i] != null && (plan.legs[i].id == round || plan.legs[i].stops.Exists(x => PlaylistSets.Matches(x, round)))) { li = i; break; }
                if (li < 0) { Log($"run.{op}: no round '{round}' in this playlist"); return "false"; }
                if (li == plan.legIndex) return "true";   // already out
                State.Set("nx", "\"" + SharedState.JsonEscape(plan.legs[li].id) + "\"");
                AddLog("show", null, "round " + plan.legs[li].id);
                return "true";
            }
            int cur = PlaylistSets.SetNumber(plan.Current?.id);
            var last = SharedState.ParseFragment(State.GetJson("set"));
            int lastN = last != null ? (int)PlaylistRunPlan.Num(last, "n") : 0;
            // add / remove into the STANDING set (no next set asked for): in place — only the added / removed attractions
            // transition, every other one keeps its rect, its block and its attempt (an unchanged rect keeps its attraction).
            if (op != "show" && last != null && lastN == cur && cur > 0) return ChangeStandingSet(op, req, last, leg);
            // add / remove on a set asked for but not out yet: that set ± attractions (it changes at once when it comes out).
            if (op != "show")
            {
                var baseRefs = PlaylistSets.Refs(last != null && lastN >= cur ? last : null);
                if (baseRefs.Count == 0) foreach (var s in plan.Current?.stops ?? new List<PlaylistRunStop>()) baseRefs.Add(PlaylistSets.RefOf(s));
                var change = new List<string>();
                foreach (var x in req.GetField("attractions")?.list ?? new List<JSONObject>())
                    change.Add(x.type == JSONObject.Type.String ? x.stringValue : PlaylistRunPlan.Str(x, "ref", PlaylistRunPlan.Str(x, "attraction", "")));
                var arr = new JSONObject(JSONObject.Type.Array);
                foreach (var r in baseRefs) if (op == "add" || !change.Exists(c => string.Equals(c, r, StringComparison.OrdinalIgnoreCase))) arr.Add(r);
                if (op == "add") foreach (var x in req.GetField("attractions")?.list ?? new List<JSONObject>()) arr.Add(x);
                var nreq = new JSONObject();
                nreq.AddField("attractions", arr);
                int rooms = (int)PlaylistRunPlan.Num(req, "rooms", last != null ? PlaylistRunPlan.Num(last, "rooms") : 0);
                if (rooms > 0) nreq.AddField("rooms", rooms);
                var place = req.GetField("place") ?? new JSONObject();
                // the kept attractions keep their wishes and locks
                foreach (var sj in last?.GetField("stops")?.list ?? new List<JSONObject>())
                {
                    string r = PlaylistRunPlan.Str(sj, "ref", "");
                    if (!string.IsNullOrEmpty(PlaylistRunPlan.Str(sj, "place", null)) && place.GetField(r) == null) place.AddField(r, PlaylistRunPlan.Str(sj, "place", ""));
                }
                if (place.count > 0) nreq.AddField("place", place);
                req = nreq;
            }
            var missing = new List<string>();
            var pool = plan.pool;
            // Showing what already stands is no change (the run's first show: set 1 was packed from the preview of this very
            // on_start; a resume re-running a show) — adopt it as the current set.
            if (lastN <= cur && cur > 0)
            {
                var same = PlaylistSets.BuildSet(cur, pool, req, missing);
                var have = new List<string>(); foreach (var s in plan.Current.stops) have.Add(PlaylistSets.RefOf(s));
                // Oct 23 (pl-loading): set 1 was packed WITH the rooms on_start's run.add puts beside it (the preview folds them
                // in, flagged "added"): this show is that set minus its added rooms — adopt the packed set whole; the run.add
                // that follows finds them out already.
                // (the run's shared set as the pack put it out — never changed in place yet, no `v` — else this device's registry)
                JSONObject packed = last != null ? (lastN == cur && last.GetField("v") == null ? last : null) : PlaylistSets.Find(plan.playlistId, PlaylistSets.SetId(cur));
                var addedRefs = PlaylistSets.AddedRefs(packed);
                if (addedRefs.Count > 0 && string.Join("|", PlaylistSets.Refs(packed)) == string.Join("|", have)
                    && string.Join("|", PlaylistSets.Refs(same)) == string.Join("|", have.FindAll(r => !addedRefs.Contains(r))))
                    same = packed;
                if (string.Join("|", PlaylistSets.Refs(same)) == string.Join("|", have))
                {
                    if (!ShareSet(same, $"run.{op}")) return "false";
                    PlaylistSets.Register(plan.playlistId, same);
                    ApplySetLocks(leg, same);
                    Log($"run.{op}: set {cur} [{string.Join(", ", have)}] is the set standing" + (cur == 1 ? " (packed from the preview)" : "") + " — no change");
                    AddLog("show", null, PlaylistSets.SetId(cur) + " (standing)");
                    return "true";
                }
                missing.Clear();
            }
            int n = lastN > cur ? lastN : cur + 1;   // a set asked for but not out yet is replaced, not queued
            if (_setPassed >= n) n = _setPassed + 1;   // after a set that could not be placed: the next number (every device packs it)
            var set = PlaylistSets.BuildSet(n, pool, req, missing);
            if (missing.Count > 0) Log($"run.{op}: not in the pool: {string.Join(", ", missing)}");
            if ((set.GetField("stops")?.count ?? 0) == 0) { Log($"run.{op}: nothing to put out"); return "false"; }
            if (!ShareSet(set, $"run.{op}")) return "false";
            PlaylistSets.Register(plan.playlistId, set);
            State.Set("nx", "\"" + PlaylistSets.SetId(n) + "\"");
            Log($"run.{op}: set {n} [{string.Join(", ", PlaylistSets.Refs(set))}]" + (PlaylistRunPlan.Num(set, "rooms") > 0 ? $" in ≤ {PlaylistRunPlan.Num(set, "rooms")} rooms" : "") + " — the whole set changes at once");
            AddLog("show", null, PlaylistSets.SetId(n));
            // All at once: the standing set clears (its unfinished challenges simply go), the leader packs the next.
            if (leg.Row.phase == PlaylistRunRules.Playing) leg.SkipLeg();
            return "true";
        }


        /// run.add / run.remove on the set that stands (leader): the set JSON gains / loses stops (each keeps its occurrence
        /// index), its version `v` bumps; every device applies the difference to its leg (ApplySetChanges).
        string ChangeStandingSet(string op, JSONObject req, JSONObject set, IPlaylistLeg leg)
        {
            var plan = leg.Plan;
            int n = (int)PlaylistRunPlan.Num(set, "n");
            var stops = set.GetField("stops");
            if (stops == null || stops.type != JSONObject.Type.Array) { stops = new JSONObject(JSONObject.Type.Array); set.SetField("stops", stops); }
            int next = (int)PlaylistRunPlan.Num(set, "next", stops.count);
            var names = new List<string>();
            foreach (var x in req.GetField("attractions")?.list ?? new List<JSONObject>())
                names.Add(x.type == JSONObject.Type.String ? x.stringValue : PlaylistRunPlan.Str(x, "ref", PlaylistRunPlan.Str(x, "attraction", "")));
            var changed = new List<string>();
            if (op == "remove")
            {
                for (int i = stops.count - 1; i >= 0; i--)
                {
                    var sj = stops.list[i];
                    string r = PlaylistRunPlan.Str(sj, "ref", "");
                    var st = PlaylistRunPlan.StopFromJson(sj);
                    if (!names.Exists(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase) || PlaylistSets.Matches(st, x))) continue;
                    stops.list.RemoveAt(i);
                    changed.Add(r);
                }
                if (changed.Count == 0) { Log($"run.remove: none of [{string.Join(", ", names)}] is out"); return "false"; }
            }
            else
            {
                var missing = new List<string>();
                var addSet = PlaylistSets.BuildSet(n, plan.pool, req, missing);
                if (missing.Count > 0) Log($"run.add: not in the pool: {string.Join(", ", missing)}");
                var adds = new List<(JSONObject sj, PlaylistRunStop stop)>();
                int alreadyOut = 0;
                foreach (var sj in addSet.GetField("stops")?.list ?? new List<JSONObject>())
                {
                    string r = PlaylistRunPlan.Str(sj, "ref", "");
                    bool already = false;
                    foreach (var x in stops.list ?? new List<JSONObject>()) if (string.Equals(PlaylistRunPlan.Str(x, "ref", ""), r, StringComparison.OrdinalIgnoreCase)) already = true;
                    if (already) { Log($"run.add: '{r}' is already out"); alreadyOut++; continue; }
                    var st = PlaylistRunPlan.StopFromJson(sj);
                    sj.SetField("i", next);
                    st.occurrenceId = PlaylistSets.OccurrenceId(plan.playlistId, n, next);
                    next++;
                    adds.Add((sj, st));
                }
                // Every one asked for is out already (set 1 packed them with the set, pl-loading): what was asked holds.
                if (adds.Count == 0) return alreadyOut > 0 && missing.Count == 0 ? "true" : "false";
                List<PlaylistRunStop> placed = null;
                try { placed = _w.PlaceAdded?.Invoke(plan, adds.ConvertAll(a => a.stop)); } catch (Exception e) { Log("run.add: placing threw: " + e.Message); }
                foreach (var (sj, st) in adds)
                {
                    var p = placed?.Find(x => x != null && x.occurrenceId == st.occurrenceId);
                    if (p == null) { Log($"run.add: no free room for '{PlaylistRunPlan.Str(sj, "ref", "")}' beside the set that stands (an unchanged rect keeps its attraction) — not added"); continue; }
                    var pj = PlaylistRunPlan.StopJson(p);
                    foreach (var k in new[] { "rectId", "roomId", "roomLabel", "resourceAddress", "seat" })
                        if (pj.GetField(k) != null) sj.SetField(k, pj.GetField(k));
                    sj.SetField("added", true);
                    stops.Add(sj);
                    changed.Add(PlaylistRunPlan.Str(sj, "ref", ""));
                }
                if (changed.Count == 0) return "false";
            }
            set.SetField("next", next);
            set.SetField("v", (int)PlaylistRunPlan.Num(set, "v") + 1);
            if (!ShareSet(set, $"run.{op}")) return "false";
            PlaylistSets.Register(plan.playlistId, set);
            Log($"run.{op}: [{string.Join(", ", changed)}] {(op == "add" ? "join" : "leave")} set {n} in place — every other attraction keeps its rect and keeps playing");
            AddLog(op, null, string.Join(",", changed));
            ApplySetChanges(leg);
            return "true";
        }

        /// The run's "set" = this set, compact (no goal Lua: every device puts it back from the pool). False (logged) when the
        /// shared table refuses it — the caller must not go on as if the set changed.
        bool ShareSet(JSONObject set, string what)
        {
            string j = PlaylistSets.Compact(set);
            if (State != null && j != null && State.Set("set", j)) return true;
            Log($"{what}: the set could not be shared ({j?.Length ?? 0} chars, the shared table holds ≤ 4096 per value) — nothing changes");
            AddLog("set_refused", null, (j?.Length ?? 0) + " chars");
            return false;
        }

        string _setApplied;
        /// Every device: the standing set's stops = the run's "set" (same number). Added stops join the leg (they stream in on
        /// their seats), removed ones leave it; nothing else moves. Idempotent (a fresh leg built from the set has no diff).
        void ApplySetChanges(IPlaylistLeg leg)
        {
            if (State == null || !leg.Plan.sets) return;
            string json = State.GetJson("set");
            if (json == null || json == _setApplied) return;
            var set = PlaylistSets.Hydrate(leg.Plan.playlistId, SharedState.ParseFragment(json), leg.Plan.pool);
            int n = (int)PlaylistRunPlan.Num(set, "n");
            if (set == null || n != PlaylistSets.SetNumber(leg.Plan.Current?.id)) { _setApplied = json; return; }
            var want = new Dictionary<string, JSONObject>(StringComparer.Ordinal);
            int i = 0;
            foreach (var sj in set.GetField("stops")?.list ?? new List<JSONObject>())
                want[PlaylistSets.OccurrenceId(leg.Plan.playlistId, n, PlaylistSets.IndexOf(sj, i++))] = sj;
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in leg.Plan.Current.stops) if (s != null) have.Add(s.occurrenceId ?? "");
            var add = new List<PlaylistRunStop>(); var remove = new List<string>();
            foreach (var kv in want)
                if (!have.Contains(kv.Key))
                {
                    var st = PlaylistRunPlan.StopFromJson(kv.Value);
                    st.occurrenceId = kv.Key; st.standing = false; st.streamAfter = PlaylistSets.AddNow;
                    add.Add(st);
                }
            foreach (var occ in have) if (!want.ContainsKey(occ)) remove.Add(occ);
            _setApplied = json;
            if (add.Count == 0 && remove.Count == 0) return;
            Log($"set {n} changes in place: +[{string.Join(", ", add.ConvertAll(x => x.occurrenceId))}] −[{string.Join(", ", remove)}]");
            leg.SetStops(add, remove);
            if (IsLeader) foreach (var occ in remove) { DisarmAttemptLimit(occ); if (_blocks.ContainsKey(occ)) EndBlock(occ); }
            ApplySetLocks(leg, set);
            Version++;
        }

        // ── the ending + the badge gate (§ 3.3 / § 3.4) ─────────────────

        string FinishOp(string json)
        {
            var leg = Current;
            if (leg == null) return "false";
            var fj = SharedState.ParseFragment(json);
            string r = PlaylistRunPlan.Str(fj, "result", "won");
            if (string.IsNullOrEmpty(r)) r = "won";
            string next = PlaylistRunPlan.Str(fj, "next", null);
            if (State != null)
            {
                if (State.Has("rf")) return "true";
                var rf = new JSONObject(); rf.AddField("r", r);
                if (!string.IsNullOrEmpty(next)) rf.AddField("next", next);
                State.Set("rf", rf.Print());
            }
            Log($"run.finish{{result='{r}'{(string.IsNullOrEmpty(next) ? "" : $", next='{next}'")}}} — the run ends (Results → badges → rating → What's Next follow)");
            AddLog("finish", null, r);
            leg.Finish(r);
            return "true";
        }

        /// run.finish{next}: the playlist the run asked to start next (null = none).
        public string NextPlaylist => PlaylistRunPlan.Str(SharedState.ParseFragment(State?.GetJson("rf")), "next", null);
        /// What became of it: "" (pending / none), "started", "phone" (the phone's queue follows), "locked", "unknown".
        public string NextState => PlaylistRunPlan.Str(SharedState.ParseFragment(State?.GetJson("nxs")), "s", "");
        /// The started next run {pl, run, title, id} for followers (PlaylistAuthority attaches from it), or null.
        public JSONObject NextStarted { get { var j = SharedState.ParseFragment(State?.GetJson("nxs")); return PlaylistRunPlan.Str(j, "s", "") == "started" ? j : null; } }

        bool _nextPending, _nextHere; double _nextSince;

        /// OFFLINE NEXT UP (offline-next, Oct 23 2026; Aidan: "offline … on the next up screen it'd immediately have the next
        /// playlist centered with a 3 2 1 countdown and it jumps to the next playlist"): the leader starts `next` itself once the
        /// Results' 3-2-1 ends — the run.finish{next} path (the group's badge gate, StartPlaylist, `nxs` for every other
        /// headset and late joiners, NextRunId → the location queue's outbox), always HERE (never left to a phone's queue).
        /// Only on the leader, the run complete, and only when the run named no `next` of its own. True = taken.
        public bool StartNext(string next)
        {
            var leg = Current;
            if (string.IsNullOrEmpty(next) || State == null || !IsLeader || leg?.Row == null || leg.Row.phase != PlaylistRunRules.Complete) return false;
            if (State.Has("nxs") || !string.IsNullOrEmpty(NextPlaylist)) return false;
            var was = SharedState.ParseFragment(State.GetJson("rf"));
            var rf = new JSONObject();
            rf.AddField("r", PlaylistRunPlan.Str(was, "r", string.IsNullOrEmpty(leg.Row.result) ? "won" : leg.Row.result));
            rf.AddField("next", next);
            State.Set("rf", rf.Print());
            _nextHere = true; _nextPending = true; _nextSince = _w.Clock();
            Log($"offline Next up: '{next}' starts here after the 3-2-1 (the run.finish next path, no phone queue)");
            AddLog("next_offline", null, next);
            return true;
        }

        /// Leader, the run complete with a `next`: gate its requires.badges on the group, then start it here (no phone queue) or
        /// leave it to the phone (PLAYLIST_STATE nextPlaylist). Once.
        void NextTick(IPlaylistLeg leg, double now)
        {
            if (!_nextPending || State == null) return;
            string next = NextPlaylist;
            if (string.IsNullOrEmpty(next) || State.Has("nxs")) { _nextPending = false; return; }
            PackagePlaylist rec = null;
            try { rec = _w.FindPlaylist?.Invoke(leg.Plan.contentId, next); } catch (Exception e) { Log("next: lookup threw: " + e.Message); }
            if (rec == null)
            {
                if (now - _nextSince < 5.0) return;   // a manifest still loading (core)
                _nextPending = false;
                SetNext("unknown", next, null, null);
                Log($"run.finish next='{next}': no such playlist here (no cached bundle, not in the installed title) — not started");
                return;
            }
            var req = PlaylistSets.RequiredBadges(SharedState.ParseFragment(string.IsNullOrWhiteSpace(rec.requiresJson) ? null : rec.requiresJson), rec.requires);
            if (req.Count > 0)
            {
                string want = JsonArr(req);
                if (State.GetJson("nrq") != want) { State.Set("nrq", want); return; }   // every device reports its holdings
                var (decided, open) = BadgeGateFor(req, now, _nextSince);
                if (!decided) return;
                if (!open)
                {
                    _nextPending = false;
                    SetNext("locked", next, null, null);
                    Log($"run.finish next='{next}': requires.badges [{string.Join(", ", req)}] — nobody here holds them: not started (the group stays)");
                    AddLog("next_locked", null, next);
                    Moment("playlist_locked", "{\"badges\":" + want + ",\"playlist\":\"" + SharedState.JsonEscape(next) + "\",\"name\":\"" + SharedState.JsonEscape(rec.name ?? next) + "\"}", null, DreamieMoment.High, true);
                    return;
                }
            }
            _nextPending = false;
            bool server = true, phone = true;
            try { server = _w.ServerReachable(); } catch (Exception) { }
            try { phone = _w.PhonePresent(); } catch (Exception) { }
            if (server && phone && !_nextHere)
            {
                SetNext("phone", next, null, null);
                Log($"run.finish next='{next}': the phone's queue starts it (PLAYLIST_STATE nextPlaylist)");
                return;
            }
            string runId = _w.NextRunId?.Invoke(RunId, next) ?? RunId + ">" + next;
            SetNext("started", next, runId, PlaylistRunPlan.Str(SharedState.ParseFragment(State.GetJson("rec")), "title", leg.Plan.contentId));
            Log($"run.finish next='{next}': starting '{rec.name ?? next}' here ({(server ? "" : "no server")}{(!server && !phone ? ", " : "")}{(phone ? "" : "no phone")}) as run {runId}");
            AddLog("next", null, next);
            Moment("next_playlist", "{\"playlist\":\"" + SharedState.JsonEscape(next) + "\",\"name\":\"" + SharedState.JsonEscape(rec.name ?? next) + "\"}", null, DreamieMoment.High, true);
            try { _w.StartPlaylist?.Invoke(leg.Plan, rec, runId); } catch (Exception e) { Log("next: start threw: " + e.Message); }
        }

        void SetNext(string state, string next, string runId, string title)
        {
            var j = new JSONObject(); j.AddField("s", state); j.AddField("pl", next ?? "");
            if (!string.IsNullOrEmpty(runId)) j.AddField("run", runId);
            if (!string.IsNullOrEmpty(title)) j.AddField("title", title);
            State?.Set("nxs", j.Print());
            NoteNext();
        }

        bool _nextNoted, _releaseRefused;
        /// Every device, once: the ended run's `next` started (or went to the phone's queue) → note where it came from, so the
        /// new run's run.started_from and its set-1 preview both see this playlist (PlaylistSets.NoteStartedFrom).
        void NoteNext()
        {
            if (_nextNoted || State == null) return;
            var j = SharedState.ParseFragment(State.GetJson("nxs"));
            if (j == null) return;
            _nextNoted = true;
            string st = PlaylistRunPlan.Str(j, "s", "");
            if (st != "started" && st != "phone") return;
            string from = Plan?.playlistId ?? PlaylistRunPlan.Str(SharedState.ParseFragment(State.GetJson("rec")), "id", null);
            PlaylistSets.NoteStartedFrom(PlaylistRunPlan.Str(j, "pl", null), from, st == "started" ? PlaylistRunPlan.Str(j, "run", null) : null);
        }

        /// run.started_from: the playlist whose run.finish{next} started this run, or null. The leader keeps it in the run's
        /// table ("sf") once its Lua loads, so a new leader / late joiner reads the same.
        public string StartedFrom
        {
            get
            {
                string s = PlaylistRunPlan.Str(SharedState.ParseFragment(State?.GetJson("sf")), "pl", null);
                return !string.IsNullOrEmpty(s) ? s : PlaylistSets.StartedFrom(Plan?.playlistId, RunId);
            }
        }

        double _nextBh;
        /// Every device: the badges this account holds ("bh.{device}") — all of them (run.can_play asks about any playlist), plus
        /// any required one HoldsBadge confirms (the playlist's requires.badges, the leader's "nrq" for run.finish{next}).
        void WriteBadgeHolds(double now, IPlaylistLeg leg)
        {
            if (State == null || !State.Synced || now < _nextBh) return;
            _nextBh = now + 1.0;
            var req = new List<string>(leg?.Plan?.requiresBadges ?? new List<string>());
            foreach (var x in SharedState.ParseFragment(State.GetJson("nrq"))?.list ?? new List<JSONObject>())
                if (x?.type == JSONObject.Type.String && !req.Contains(x.stringValue)) req.Add(x.stringValue);
            string dev = _w.DeviceId(), j = JsonArr(MyBadges(req));
            if (State.GetJson("bh." + dev) != j) State.Set("bh." + dev, j);
        }

        /// This device's account: every badge it holds, plus those of `req` HoldsBadge confirms. Sorted (a stable row).
        List<string> MyBadges(IEnumerable<string> req)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try { foreach (var b in _w.HeldBadges?.Invoke() ?? Array.Empty<string>()) if (!string.IsNullOrEmpty(b)) set.Add(b); } catch (Exception) { }
            foreach (var b in req ?? Array.Empty<string>()) { try { if (!string.IsNullOrEmpty(b) && !set.Contains(b) && (_w.HoldsBadge?.Invoke(b) ?? false)) set.Add(b); } catch (Exception) { } }
            var l = new List<string>(set); l.Sort(StringComparer.Ordinal);
            return l;
        }

        /// § 3.4: (decided, open). Any present account holding every required badge opens it; until every device reported
        /// (≤ 5 s) it is undecided.
        public (bool decided, bool open) BadgeGate(IPlaylistLeg leg, double now) => BadgeGateFor(leg?.Plan?.requiresBadges, now, _gateSince);

        (bool decided, bool open) BadgeGateFor(List<string> req, double now, double since)
        {
            if (req == null || req.Count == 0) return (true, true);
            var holders = GroupBadges(req, out int reported, out int members);
            bool open = PlaylistSets.MayPlay(req, holders);
            return (open || reported >= members || now - since >= 5.0, open);
        }

        /// The badges of each present account: this device's asked directly (its row may not have gone out yet), the others'
        /// from their "bh.{device}" rows (a device with no row yet is not counted in `reported`).
        List<ICollection<string>> GroupBadges(List<string> req, out int reported, out int members)
        {
            string me = _w.DeviceId();
            var ms = new List<string>(State?.Members() ?? new List<string>());
            if (!ms.Contains(me)) ms.Add(me);
            members = ms.Count;
            var holders = new List<ICollection<string>>();
            reported = 0;
            foreach (var m in ms)
            {
                if (m == me) { holders.Add(MyBadges(req)); reported++; continue; }
                var j = SharedState.ParseFragment(State?.GetJson("bh." + m));
                if (j == null || j.type != JSONObject.Type.Array) continue;
                reported++;
                var h = new List<string>(); foreach (var x in j.list ?? new List<JSONObject>()) if (x?.type == JSONObject.Type.String) h.Add(x.stringValue);
                holders.Add(h);
            }
            return holders;
        }

        /// run.can_play(playlistId) / stop.can_play: may THIS group play that playlist of the title now — the requires.badges
        /// rule of the start gate and run.finish{next}, answered at once from the group's badge rows. {ok, need = [badges no
        /// present account holds]}; a playlist this device cannot find → {ok = false, need = [], unknown = true}.
        string CanPlayJson(string pid)
        {
            var (found, ok, missing) = CanPlay(pid);
            var j = new JSONObject();
            var need = new JSONObject(JSONObject.Type.Array);
            foreach (var r in missing) need.Add(r);
            j.AddField("ok", ok); j.AddField("need", need);
            if (!found) j.AddField("unknown", true);
            return j.Print();
        }

        /// run.can_play for C# (offline-next): (found here — the cached bundle or the installed title; ok — the group may play
        /// it; the required badges nobody present holds).
        public (bool found, bool ok, List<string> need) CanPlay(string pid)
        {
            string title = Plan?.contentId ?? PlaylistRunPlan.Str(SharedState.ParseFragment(State?.GetJson("rec")), "title", null);
            PackagePlaylist rec = null;
            if (!string.IsNullOrEmpty(pid)) { try { rec = _w.FindPlaylist?.Invoke(title, pid); } catch (Exception e) { Log("can_play: lookup threw: " + e.Message); } }
            if (rec == null)
            {
                Log($"run.can_play('{pid}'): no such playlist here (no cached bundle, not in the installed title)");
                return (false, false, new List<string>());
            }
            var req = PlaylistSets.RequiredBadges(SharedState.ParseFragment(string.IsNullOrWhiteSpace(rec.requiresJson) ? null : rec.requiresJson), rec.requires);
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in GroupBadges(req, out _, out _)) foreach (var b in h) all.Add(b);
            var missing = new List<string>();
            foreach (var r in req) if (!all.Contains(r)) missing.Add(r);
            Log($"run.can_play('{pid}'): " + (missing.Count == 0 ? (req.Count == 0 ? "open (no badges required)" : $"open (the group holds {string.Join(", ", req)})") : $"locked — nobody here holds {string.Join(", ", missing)}"));
            return (true, missing.Count == 0, missing);
        }

        void TryStartRun(IPlaylistLeg leg, double now)
        {
            if (!_startPending) return;
            var (decided, open) = BadgeGate(leg, now);
            if (!decided) return;
            _startPending = false;
            if (State?.Has("st") ?? false) return;
            State?.Set("st", "{\"t\":" + Num(now) + "}");
            if (!open)
            {
                Log($"requires.badges [{string.Join(", ", leg.Plan.requiresBadges)}]: nobody here holds them — the playlist stays locked");
                AddLog("locked", null, string.Join(",", leg.Plan.requiresBadges));
                Moment("playlist_locked", "{\"badges\":" + JsonArr(leg.Plan.requiresBadges) + "}", null, DreamieMoment.High, true);
                State?.Set("rf", "{\"r\":\"locked\"}");
                leg.Finish("locked");
                return;
            }
            if (leg.Plan.requiresBadges.Count > 0) Log($"requires.badges [{string.Join(", ", leg.Plan.requiresBadges)}]: held in the group — open");
            var (st, _) = CallRun("on_start");
            if (st != null) SaveRunState(st);
        }

        static string JsonArr(IEnumerable<string> xs)
        {
            var a = new JSONObject(JSONObject.Type.Array);
            foreach (var x in xs ?? Array.Empty<string>()) a.Add(x ?? "");
            return a.Print();
        }

        string PoolJson()
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            var plan = Plan;
            foreach (var s in plan?.pool ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                var j = new JSONObject();
                j.AddField("id", PlaylistSets.RefOf(s)); j.AddField("attraction", s.attraction ?? "");
                j.AddField("challenge", s.challenge ?? ""); j.AddField("role", s.role ?? "stage");
                if (!string.IsNullOrEmpty(s.displayName)) j.AddField("name", s.displayName);
                arr.Add(j);
            }
            return arr.Print();
        }

        // ── IPlaylistGoalHost (leader; called from Lua) ────────────────

        IPlaylistLeg LegFor(string occ) => Current;
        PlaylistRunStop StopFor(string occ) => Current?.Plan?.FindStop(occ, out _);

        public void GoalProgress(string occ, double n, double of, string unit)
        {
            var leg = LegFor(occ);
            if (leg == null) return;
            leg.ApplyGoalProgress(occ, n, of, unit);
            _lastProgressAt = _w.Clock();
            if (of >= 2)
            {
                if (n >= of / 2.0 && n < of && _onceLocal.Add(occ + "|half"))
                    Moment("progress_half", Prog(n, of, unit, occ), occ, DreamieMoment.Low, true);
                if (Math.Abs(of - n - 1) < 1e-6 && _onceLocal.Add(occ + "|last"))
                    Moment("progress_last", Prog(n, of, unit, occ), occ, DreamieMoment.Normal, true);
            }
        }

        static string Prog(double n, double of, string unit, string occ) =>
            "{\"n\":" + Num(n) + ",\"of\":" + Num(of) + ",\"unit\":\"" + SharedState.JsonEscape(unit ?? "") + "\",\"stop\":\"" + SharedState.JsonEscape(occ ?? "") + "\"}";

        /// stop.won / stop.lost / stop.skip (and their aliases done / failed / timed_out) → the attempt rules (§ 3.2).
        public void GoalOutcome(string occ, string kind, string fieldsJson)
        {
            var f = SharedState.ParseFragment(string.IsNullOrEmpty(fieldsJson) ? "{}" : fieldsJson);
            if (f == null || f.type != JSONObject.Type.Object) f = new JSONObject();
            switch (kind)
            {
                case "lost": case "failed": case "timed_out":
                {
                    string why = PlaylistRunPlan.Str(f, "why", null) ?? (kind == "timed_out" ? "time" : kind == "failed" ? "failed" : "lost");
                    f.SetField("why", why);
                    AttemptLost(occ, why, PlaylistRunPlan.Str(f, "player", null) ?? PlaylistRunPlan.Str(f, "by", null), f.Print());
                    return;
                }
                case "skip": TrySkip(occ, PlaylistRunPlan.Str(f, "by", null), "block"); return;
                default: AttemptWon(occ, PlaylistRunPlan.Str(f, "by", null) ?? PlaylistRunPlan.Str(f, "player", null), f); return;
            }
        }

        /// The stop's challenge is over for the run (its result, the round's progress, on_stop_result) — the block is ended
        /// by the caller. kind: done (won / skipped) | timed_out (a time cap) | failed.
        void EndStop(string occ, string kind, string fieldsJson)
        {
            var leg = LegFor(occ);
            if (leg == null || State == null && _blocks.Count == 0) return;
            if (kind != "done" && kind != "failed" && kind != "timed_out" && kind != "skip") kind = "done";
            var f = SharedState.ParseFragment(string.IsNullOrEmpty(fieldsJson) ? "{}" : fieldsJson);
            if (f == null || f.type != JSONObject.Type.Object) f = new JSONObject();
            f.SetField("result", kind);
            f.SetField("status", kind);            // generated playlist Lua reads r.status / r.done (pl-generate)
            f.SetField("done", kind == "done");
            f.SetField("t", Math.Round(_w.Clock(), 1));
            State?.Set("res." + occ, PlaylistEvents.Clamp(f.Print(), 1024));
            var g = SharedState.ParseFragment(State?.GetJson("g." + occ)) ?? new JSONObject();
            g.SetField("r", kind);
            State?.Set("g." + occ, g.Print());
            leg.ApplyGoalOutcome(occ, kind);
            var s = StopFor(occ);
            if (kind == "done") MarkWaypoint(s, f.GetField("skipped")?.boolValue == true ? "skipped" : "won");
            Log($"{occ} ({s?.attraction}): {kind} {f.Print()}");
            AddLog("stop_" + kind, occ, f.Print());
            if (kind == "done")
                Moment("stop_done", "{\"stop\":\"" + SharedState.JsonEscape(occ) + "\",\"name\":\"" + SharedState.JsonEscape(s != null ? PlaylistObjectiveText.Name(s) : "") + "\",\"result\":" + f.Print() + "}", occ, DreamieMoment.Normal, true);
            // The playlist's own reaction (on_stop_result), then the block ends.
            var stopJ = new JSONObject();
            stopJ.AddField("id", s != null && !string.IsNullOrEmpty(s.stopId) ? s.stopId : occ);
            stopJ.AddField("occurrence", occ);
            stopJ.AddField("ref", PlaylistSets.RefOf(s));
            stopJ.AddField("attraction", PlaylistAutoRules.Leaf(s?.attraction) ?? "");
            stopJ.AddField("kind", s != null ? PlaylistChallenge.KindOf(leg.Plan.Current, s) : "");
            var (st, _) = CallRun("on_stop_result", stopJ.Print(), f.Print());
            if (st != null) SaveRunState(st);
        }

        public int GoalTimer(string occ, double seconds, bool every)
        {
            if (!_blocks.TryGetValue(occ, out var b)) return 0;
            int id = ++b.timerCount;
            var tm = TimersOf(occ);
            if (!tm.ContainsKey(id))
            {
                double now = _w.Clock();
                tm[id] = (now + Math.Max(0, seconds), every ? Math.Max(0.05, seconds) : 0);
                SaveTimers(occ, tm);
            }
            // A block that sets its own timer replaces the platform's safety net (and its per-attempt limit).
            if (b.platformTimers) { tm = TimersOf(occ); foreach (var k in tm.Keys.Where(k => k < 0).ToList()) tm.Remove(k); b.platformTimers = false; SaveTimers(occ, tm); SetAttemptLimit(occ, 0); }
            return id;
        }

        public double GoalCensus(string occ, string filterJson)
        {
            var leg = LegFor(occ);
            var s = StopFor(occ);
            if (leg == null || s == null) return 0;
            var f = PlaylistCensusFilter.FromJson(filterJson);
            try { return _w.Census(s, leg.StopRoot(s), f); }
            catch (Exception e) { Log($"{occ}: census threw: {e.Message}"); return 0; }
        }

        /// stop.jump{}'s return: {pads, fell_back, why} — the live chain (pads = 0 + fell_back = it runs as a crossing).
        string JumpChainJson(string occ, string specJson)
        {
            var leg = LegFor(occ);
            var s = StopFor(occ);
            var spec = SharedState.ParseFragment(string.IsNullOrEmpty(specJson) ? "{}" : specJson) ?? new JSONObject();
            string chain = PlaylistRunPlan.Str(spec, "chain", null) ?? PlaylistRunPlan.Str(spec, "id", null);
            if (string.IsNullOrEmpty(chain)) chain = null;
            (int pads, bool fellBack, string why) r = (0, true, "no such stop here");
            if (leg != null && s != null)
            {
                try
                {
                    if (_w.JumpChain != null) r = _w.JumpChain(s, leg.StopRoot(s), chain, spec.Print());
                    else
                    {
                        int given = spec.GetField("pads")?.list?.Count ?? 0;
                        int n = given >= 2 ? given : (_w.JumpAuthored?.Invoke(s, leg.StopRoot(s), chain) ?? 0);
                        r = n >= 2 ? (n, false, null) : (0, true, chain != null ? $"no pads given or authored for chain '{chain}'" : "no pads given or authored on the attraction");
                    }
                }
                catch (Exception e) { r = (0, true, "jump chain threw: " + e.Message); }
            }
            if (r.pads < 2) r = (0, true, string.IsNullOrEmpty(r.why) ? "fewer than 2 pads" : r.why);
            var j = new JSONObject(); j.AddField("pads", r.pads); j.AddField("fell_back", r.fellBack);
            if (!string.IsNullOrEmpty(r.why)) j.AddField("why", r.why);
            return j.Print();
        }

        public int GoalJumpAuthored(string occ, string chain)
        {
            var leg = LegFor(occ);
            var s = StopFor(occ);
            if (leg == null || s == null) return 0;
            try { return _w.JumpAuthored?.Invoke(s, leg.StopRoot(s), string.IsNullOrEmpty(chain) ? null : chain) ?? 0; }
            catch (Exception e) { Log($"{occ}: authored jump pads threw: {e.Message}"); return 0; }
        }

        public string GoalCount(string occ, string kind)
        {
            var leg = LegFor(occ);
            var s = StopFor(occ);
            if (leg == null || s == null) return null;
            try { return _w.Count(s, leg.StopRoot(s), kind); }
            catch (Exception e) { Log($"{occ}: count threw: {e.Message}"); return null; }
        }

        /// stop.mark{…} adds one marker group to the stop (an identical group is not added twice); stop.unmark() clears them.
        /// mk.{occ} = [spec, …] — one batch per stop on every device.
        public void GoalMark(string occ, string specJson)
        {
            if (State == null) return;
            if (string.IsNullOrEmpty(specJson) || specJson == "{}") { if (State.Has("mk." + occ)) State.Delete("mk." + occ); return; }
            var spec = SharedState.ParseFragment(PlaylistEvents.Clamp(specJson, 1024));
            if (spec == null || spec.type != JSONObject.Type.Object) return;
            var cur = SharedState.ParseFragment(State.GetJson("mk." + occ));
            var arr = cur != null && cur.type == JSONObject.Type.Array ? cur : new JSONObject(JSONObject.Type.Array);
            if (cur != null && cur.type == JSONObject.Type.Object) arr.Add(cur);
            string sp = spec.Print();
            foreach (var x in arr.list ?? new List<JSONObject>()) if (x != null && x.Print() == sp) return;
            arr.Add(spec);
            while (arr.count > 6) arr.list.RemoveAt(0);
            string j = arr.Print();
            if (j.Length <= SharedState.MaxValueChars && State.GetJson("mk." + occ) != j) State.Set("mk." + occ, j);
        }

        public bool GoalEdge(string occ, string which, out Vector3 local)
        {
            local = default;
            var leg = LegFor(occ);
            var s = StopFor(occ);
            if (leg == null || s == null) return false;
            Vector3? v = null;
            try { v = _w.Edge(s, leg.StopRoot(s), which); } catch (Exception e) { Log($"{occ}: edge threw: {e.Message}"); }
            if (!v.HasValue) return false;
            local = v.Value;
            return true;
        }

        public void GoalJump(string occ, string specJson)
        {
            if (State == null) return;
            State.Set("jp." + occ, PlaylistEvents.Clamp(specJson, 1024));
        }

        public void GoalJumpReset(string occ, string player, string why, string extraJson)
        {
            if (State == null) return;
            var cur = SharedState.ParseFragment(State.GetJson("jr." + occ));
            int n = cur != null ? (int)PlaylistRunPlan.Num(cur, "n") + 1 : 1;
            var j = SharedState.ParseFragment(string.IsNullOrEmpty(extraJson) ? "{}" : extraJson);
            if (j == null || j.type != JSONObject.Type.Object) j = new JSONObject();
            j.SetField("p", player ?? ""); j.SetField("n", n); j.SetField("why", string.IsNullOrEmpty(why) ? "forced" : why);
            State.Set("jr." + occ, j.Print());
        }

        public void GoalRequest(string occ, string table, string key, string valueJson)
        {
            var leg = LegFor(occ);
            var s = StopFor(occ);
            if (leg == null || s == null || string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key)) return;
            try { _w.Request(s, leg.StopRoot(s), table, key, valueJson); Log($"{occ}: request {table}.{key} {valueJson}"); }
            catch (Exception e) { Log($"{occ}: request threw: {e.Message}"); }
        }

        public void GoalHint(string occ, string text)
        {
            var s = StopFor(occ);
            Moment("stuck", "{\"stop\":\"" + SharedState.JsonEscape(occ ?? "") + "\",\"name\":\"" + SharedState.JsonEscape(s != null ? PlaylistObjectiveText.Name(s) : "")
                + "\",\"hint\":\"" + SharedState.JsonEscape(text ?? "") + "\"}", occ, DreamieMoment.Normal);
        }

        public void GoalSubscribe(string occ, string source, string name)
        {
            if (string.IsNullOrEmpty(source)) return;
            if (!_subsByBlock.TryGetValue(occ ?? "", out var set)) _subsByBlock[occ ?? ""] = set = new HashSet<string>(StringComparer.Ordinal);
            // Forward the whole source when the filter has no name (a name filter narrows what followers send).
            if (set.Add(string.IsNullOrEmpty(name) ? source : source + ":" + name)) RebuildSubs();
        }

        public void GoalEmit(string occ, string name, string dataJson)
        {
            var e = new PlaylistEvent { source = PlaylistEvent.Event, name = name, dataJson = PlaylistEvents.Clamp(dataJson, PlaylistEvents.MaxEventDataBytes), stop = occ, from = _w.DeviceId(), at = Math.Round(_w.Clock(), 3) };
            _w.Ingest(e);
        }

        public void GoalLog(string occ, string line) => Log((occ != null ? occ + ": " : "") + line);

        (string state, string ret) CallRun(string hook, string a1 = null, string a2 = null)
        {
            if (_rt == null || !_rt.RunLoaded(RunId)) return (null, null);
            return _rt.CallRun(RunId, hook, a1, a2);
        }

        void EnsureRunLua(IPlaylistLeg leg)
        {
            if (_runLuaTried) return;
            _runLuaTried = true;
            string lua = leg.Plan.lua;
            // An arc[] record keeps playing (pl-sets): its Lua becomes the default driver — run.show per old round, in order.
            string converted = PlaylistSets.ConvertArc(leg.Plan);
            if (converted != null) { lua = converted; Log($"arc[] record: playlist Lua converted on load (run.show per round, {leg.Plan.legs.Count} rounds)"); }
            if (string.IsNullOrWhiteSpace(lua)) return;
            var rt = Runtime();
            if (rt == null) { Log("playlist Lua needs a Lua VM — none here"); return; }
            var ctx = new JSONObject(); ctx.AddField("runId", RunId); ctx.AddField("playlistId", leg.Plan.playlistId ?? "");
            string sf = StartedFrom;
            if (!string.IsNullOrEmpty(sf))
            {
                ctx.AddField("startedFrom", sf);
                if (State != null && !State.Has("sf")) State.Set("sf", "{\"pl\":\"" + SharedState.JsonEscape(sf) + "\"}");
                Log($"started from '{sf}' (its run.finish{{next}}): run.started_from = '{sf}'");
            }
            bool resumed = State?.Has("rs") == true;
            string err = rt.StartRun(this, RunId, lua, State?.GetJson("rs"), ctx.Print(), out string st);
            if (err != null) { Log("playlist Lua failed to load: " + err); AddLog("lua_error", null, err); return; }
            if (st != null) SaveRunState(st);
            Log("playlist Lua loaded" + (resumed ? " (run.state resumed)" : ""));
        }

        string ResolveStopId(string stopId)
        {
            if (string.IsNullOrEmpty(stopId)) return null;
            var plan = Plan;
            if (plan == null) return stopId;
            foreach (var l in plan.legs ?? new List<PlaylistRunLeg>())
                foreach (var s in l?.stops ?? new List<PlaylistRunStop>())
                    if (s != null && (s.occurrenceId == stopId || s.stopId == stopId || PlaylistAutoRules.Leaf(s.attraction) == stopId)) return s.occurrenceId;
            return stopId;
        }

        public string RunOp(string op, string a, string json)
        {
            var leg = Current;
            switch (op)
            {
                case "lock":
                {
                    string occ = ResolveStopId(a);
                    if (occ == null || State == null) return "false";
                    var o = SharedState.ParseFragment(string.IsNullOrEmpty(json) ? "{}" : json) ?? new JSONObject();
                    var lj = new JSONObject();
                    var needs = new JSONObject(JSONObject.Type.Array);
                    var nj = o.GetField("needs");
                    if (nj?.list != null) foreach (var x in nj.list) if (x != null && x.type == JSONObject.Type.String) needs.Add(x.stringValue);
                    // § 8.6 no double lock: a Lua lock that only repeats the stop's lockedUntil is the native lock already.
                    var gated = Plan?.FindStop(occ, out _);
                    if (PlaylistRoundRules.Gated(gated) && (needs.list ?? new List<JSONObject>()).All(x => gated.lockedUntil.Contains(ResolveStopId(x.stringValue))))
                    {
                        Log($"{occ}: run.lock repeats its lockedUntil — the native lock holds it (no second lock)");
                        AddLog("lock_native", occ, "lockedUntil covers it");
                        return "true";
                    }
                    lj.AddField("needs", needs);
                    string sign = PlaylistRunPlan.Str(o, "sign", null);
                    if (!string.IsNullOrEmpty(sign)) lj.AddField("sign", sign);
                    State.Set("lk." + occ, lj.Print());
                    AddLog("lock", occ, sign);
                    return "true";
                }
                case "unlock":
                {
                    string occ = ResolveStopId(a);
                    if (occ != null && State != null && State.Has("lk." + occ)) { State.Delete("lk." + occ); AddLog("unlock", occ, null); }
                    // a lock set by ref (before its set stood) goes too
                    var st = occ != null ? Plan?.FindStop(occ, out _) : null;
                    foreach (var k in new[] { a, st?.stopId, PlaylistAutoRules.Leaf(st?.attraction) })
                        if (!string.IsNullOrEmpty(k) && State != null && State.Has("lk." + k)) State.Delete("lk." + k);
                    return "true";
                }
                case "locked":
                {
                    string occ = ResolveStopId(a);
                    return occ != null && ((State != null && State.Has("lk." + occ)) || NativeLocked(occ)) ? "true" : "false";
                }
                case "flag":
                {
                    var o = SharedState.ParseFragment(json);
                    var v = o?.GetField("v");
                    bool ok = leg != null && leg.SetFlag(a, v);
                    if (ok && State != null) State.Set("f." + a, "{\"v\":" + (v != null ? v.Print() : "true") + "}");
                    return ok ? "true" : "false";
                }
                case "badge":
                {
                    // run.badge(id) → every player present (they earned it); run.badge(player, id) → that player.
                    var o = SharedState.ParseFragment(json);
                    string id = PlaylistRunPlan.Str(o, "id", null);
                    if (string.IsNullOrEmpty(id) || State == null) return "false";
                    string who = string.IsNullOrEmpty(a) ? "all" : a;
                    var bd = SharedState.ParseFragment(State.GetJson("bd"));
                    if (bd == null || bd.type != JSONObject.Type.Object) bd = new JSONObject();
                    var list = bd.GetField(who);
                    if (list == null) { list = new JSONObject(JSONObject.Type.Array); bd.AddField(who, list); }
                    foreach (var x in list.list ?? new List<JSONObject>()) if (x != null && x.stringValue == id) return "true";
                    list.Add(id);
                    State.Set("bd", bd.Print());
                    AddLog("badge", null, who + ":" + id);
                    return "true";
                }
                case "result":
                {
                    string occ = ResolveStopId(a);
                    return occ != null ? ResultJson(occ) : null;
                }
                case "players":
                {
                    var arr = new JSONObject(JSONObject.Type.Array);
                    try
                    {
                        foreach (var (id, name) in _w.Players())
                        {
                            var pj = new JSONObject(); pj.AddField("id", id ?? ""); pj.AddField("name", name ?? id ?? "");
                            arr.Add(pj);
                        }
                    }
                    catch (Exception) { }
                    return arr.Print();
                }
                case "leader": return "\"" + SharedState.JsonEscape(LeaderId ?? "") + "\"";
                case "show": case "add": case "remove": { string shown = ShowOp(op, json); if (shown == "true") MapAfterField(json); return shown; }
                case "map": return MapOp(json);
                case "placed": return PlacedJson();
                case "finish": return FinishOp(json);
                case "can_play": return CanPlayJson(a);
                case "send": return SendOp(a, json);
                case "waypoint": return WaypointOp(a, json);
                case "jump_chain": return JumpChainJson(a, json);
                case "space": { int n = 1; try { n = Math.Max(1, _w.SpaceRooms?.Invoke() ?? 1); } catch (Exception) { } return "{\"rooms\":" + n + "}"; }
                case "pool": return PoolJson();
                case "set": return leg != null ? SetSummary(leg) : null;
                case "skip": { string occ = ResolveStopId(a); return occ != null && TrySkip(occ, null, "run.skip") ? "true" : "false"; }
                case "attempt": { string occ = ResolveStopId(a); return occ != null ? State?.GetJson("at." + occ) ?? Att(occ).ToJson() : null; }
                case "attempt_time":
                {
                    string occ = ResolveStopId(a);
                    double sec = PlaylistRunPlan.Num(SharedState.ParseFragment(json), "sec");
                    if (occ == null) return "false";
                    if (_blocks.TryGetValue(occ, out var bk)) bk.platformTimers = false;
                    SetAttemptLimit(occ, sec);
                    ArmAttemptLimit(occ, _w.Clock());
                    return "true";
                }
                case "skip_round":
                    leg?.SkipLeg();
                    AddLog("skip_round", null, null);
                    return "true";
                case "next_round":
                    if (string.IsNullOrEmpty(a) || State == null) return "false";
                    State.Set("nx", "\"" + SharedState.JsonEscape(a) + "\"");
                    AddLog("next", null, "next_round → " + a);
                    return "true";
            }
            return null;
        }
    }

    /// Which goal block a stop runs (CONTRACT § 2 precedence, steps 1–2) — the library's registry is PlaylistGoalLibrary.cs.
    public static partial class PlaylistGoalLibrary
    {
        /// The goal block a stop runs (steps 1–2 of the precedence), or null (steps 3–4: no block).
        public static string Resolve(PlaylistRunPlan plan, PlaylistRunStop s, out string key, out string source)
            => Resolve(plan, s, out key, out source, null);

        /// <param name="pinned">the run's pinned copy (title, key) → (lua, source) (record v2 § 8.9), else the live library.</param>
        public static string Resolve(PlaylistRunPlan plan, PlaylistRunStop s, out string key, out string source,
            Func<string, string, (string lua, string source)?> pinned)
        {
            key = null; source = null;
            if (s == null || plan == null) return null;
            if (!string.IsNullOrWhiteSpace(s.goal) && s.goal.Trim() != "default")
            {
                key = (PlaylistAutoRules.Leaf(s.attraction) ?? "") + "." + PlaylistChallenge.KindOf(plan.Current, s) + "#inline";
                source = SourceInline;
                return s.goal;
            }
            if (s.bookend) return null;
            var leg = LegOf(plan, s);
            // The authored challenge id first (a pool's coins / bad_guys / break / the attraction's unique one), then — when that
            // id is not a catalog kind — the derived kind (role / objective), so a record keyed by the derived kind still resolves.
            string kind = PlaylistChallenge.KindOf(leg, s);
            string derived = PlaylistChallenge.DerivedKind(leg, s);
            foreach (string kd in kind == derived || PlaylistChallenge.Known(kind) ? new[] { kind } : new[] { kind, derived })
            {
                if (string.IsNullOrEmpty(kd)) continue;
                if (pinned == null)
                {
                    string src = Find(plan.ContentOf(s), s.attraction, s.attractionId, kd, out key, out source);
                    if (src != null) return src;
                    continue;
                }
                foreach (string k in Keys(s.attraction, s.attractionId, kd))
                {
                    var hit = pinned(plan.ContentOf(s), k);
                    if (hit.HasValue && !string.IsNullOrWhiteSpace(hit.Value.lua)) { key = k; source = hit.Value.source; return hit.Value.lua; }
                }
            }
            key = null; source = null;
            return null;
        }

        static PlaylistRunLeg LegOf(PlaylistRunPlan plan, PlaylistRunStop s)
        {
            foreach (var l in plan.legs ?? new List<PlaylistRunLeg>()) if (l?.stops != null && l.stops.Contains(s)) return l;
            return plan.Current;
        }
    }

    /// Unity defaults for the world's hooks.
    public static class PlaylistRunDefaults
    {
        /// Cache first (offline), then ONE device fetch before the run starts; a run that started keeps its pinned
        /// bundle (record v2 § 8.9: PlaylistRun.ApplyBundle refuses a newer one — it plays from the next run).
        public static void Bundle(PlaylistRun run, IPlaylistLeg leg)
        {
            var plan = leg?.Plan;
            if (plan == null || string.IsNullOrEmpty(plan.playlistId) || string.IsNullOrEmpty(plan.contentId)) return;
            var cached = PlaylistBundle.Cached(plan.contentId, plan.playlistId);
            if (cached != null && run.ApplyBundle(leg, cached, "cached"))
                Debug.Log($"[pl-runtime] {run.RunId}: round {plan.legIndex + 1} plays the backend record rev {plan.rev} (cached bundle)");
            if (run.Started) return;   // never refetch mid-run
            PlaylistBundle.Ensure(plan.contentId, plan.playlistId, fresh =>
            {
                if (fresh == null || fresh == cached || leg.Plan == null) return;
                if (run.ApplyBundle(leg, fresh, "fetched"))
                    Debug.Log($"[pl-runtime] {run.RunId}: round {plan.legIndex + 1} plays the backend record rev {leg.Plan.rev}");
            });
        }

        public static List<(string id, string name)> Players()
        {
            var l = new List<(string, string)>();
            string me = PlayerPresence.MyId;
            if (!string.IsNullOrEmpty(me))
            {
                string name = PlayerPresence.LocalPlayerName();   // a guest: the headset's name (web-setup)
                l.Add((me, name ?? me));
            }
            var pp = PlayerPresence.Instance;
            if (pp != null)
                foreach (var kv in pp.Peers)
                    if (kv.Value != null && DreamParkLuaAPI.DeviceOf(kv.Value.id) != "phone")
                        l.Add((kv.Value.id, RemoteNameTag.NameFor(kv.Value)));
            return l;
        }

        /// § 3.2a: a challenge event to the attraction's own objects (onevent(name, data) / dp.on handlers) — DreamParkLuaPlaylist.
        public static void Deliver(PlaylistRunStop stop, GameObject root, string name, string json)
        {
            if (root == null) return;
            try { DreamParkLuaPlaylist.DeliverChallengeEvent(root, name, json); }
            catch (Exception e) { Debug.LogWarning($"[pl-runtime] {stop?.occurrenceId}: {name} → the attraction threw: {e.Message}"); }
        }

        /// run.finish{next}: the cached backend bundle's record of that playlist (offline: the last fetched copy), or null.
        public static PackagePlaylist FindPlaylist(string titleId, string playlistId)
        {
            try { return PlaylistRunPlanFromManifest.ParseInline(PlaylistBundle.Cached(titleId, playlistId)?.GetField("playlist")); }
            catch (Exception) { return null; }
        }

        /// § 3.4: this device's account holds the badge (the profile's badge list).
        public static bool HoldsBadge(string badgeId)
        {
            try { return API.ProfileAPI.HasBadge(badgeId); } catch (Exception) { return false; }
        }

        /// Every badge this device's account holds (the profile's badge list).
        public static IEnumerable<string> HeldBadges()
        {
            var l = new List<string>();
            try { foreach (var b in API.ProfileAPI.Badges ?? Array.Empty<API.ProfileBadge>()) if (!string.IsNullOrEmpty(b?.badgeId)) l.Add(b.badgeId); }
            catch (Exception) { }
            return l;
        }

        /// run.badge → this device's player: the profile API now, the outbox when it fails (offline).
        public static void AwardBadge(string runId, string badgeId)
        {
            string body = "{\"runId\":\"" + SharedState.JsonEscape(runId ?? "") + "\",\"badge\":\"" + SharedState.JsonEscape(badgeId ?? "") + "\"}";
            try { API.ProfileAPI.AwardBadge(badgeId, (ok, b) => { if (!ok) PlaylistOutbox.Enqueue("badge", body); }); }
            catch (Exception) { PlaylistOutbox.Enqueue("badge", body); }
        }

        public static bool PhonePresent()
        {
            if (DreamParkDevice.IsPhone) return true;
            var pp = PlayerPresence.Instance;
            if (pp == null) return false;
            foreach (var kv in pp.Peers) if (kv.Value != null && DreamParkLuaAPI.DeviceOf(kv.Value.id) == "phone") return true;
            return false;
        }
    }

    /// Game-Graph-like space of a stop at runtime: start / far edge from the live footprint + entry portal.
    public static class PlaylistGoalSpace
    {
        /// Stop-local (the AttractionTemplate's frame) position of an edge: "start" = the entry portal's side, "far" = across.
        public static Vector3? Edge(GameObject root, string which)
        {
            if (root == null) return null;
            var tpl = root.GetComponentInChildren<AttractionTemplate>(true);
            if (tpl == null) return null;
            Vector2 fp = tpl.RuntimeFootprintMeters;
            Vector3 start = new Vector3(0, 0, -fp.y * 0.5f);   // the default entry: the middle of −Z
            try
            {
                foreach (var portal in tpl.CurrentPortals)
                {
                    if (portal == null || !portal.IsEntry) continue;
                    tpl.GetPortalWorldPose(portal, out Vector3 pos, out Vector3 walk);
                    Vector3 l = tpl.transform.InverseTransformPoint(pos);
                    start = new Vector3(l.x, 0, l.z);
                    break;
                }
            }
            catch (Exception) { }
            if (which == "far") return new Vector3(-start.x, 0, -start.z);
            if (which == "center" || which == "centre") return Vector3.zero;
            return start;
        }
    }
}
