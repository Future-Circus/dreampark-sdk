// ─────────────────────────────────────────────────────────────────────
//  PlaylistRunner.cs — plays ONE leg of a playlist: the GameMaster queue
//  item the phone just packed (Docs/PLAYLISTS.md § 7). Rules are pure
//  (PlaylistRunRules); this is the glue:
//    • SharedState "dp.playlist" on the Game Manager's bus: the leader owns
//      the run row, signals are requests the leader merges into per-stop
//      keys (idempotent), late joiners get the snapshot.
//    • the phone wire: PLAYLIST_STATE (progress for Dreamie; legComplete:
//      true is the phone's cue to send the queue's `complete
//      {expectItemId}`) through Announce (core → Swift).
//    • the Lua face (DreamParkLuaPlaylist): dp.playlist.*.
//  Advancing to the next leg is the QUEUE's job (the phone packs the next
//  arrangement with the approved bounce-out/in); the runner never moves
//  attractions. A title that does not opt in never meets this.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    /// Finds a stop's live root on this device, and streams a stop in within a leg. Core sets
    /// <see cref="PlaylistRunner.Stage"/> (bounce-out / bounce-in on the phone); harnesses may replace it.
    public interface IPlaylistStage
    {
        /// The live root of a stop on this device (null = not standing here).
        GameObject StopRoot(PlaylistRunner runner, PlaylistRunStop stop);

        /// A streamed stop's turn (§ 6.1 streamIn): <paramref name="from"/> (the stop it follows, now over) goes,
        /// <paramref name="to"/> comes in on its seat. Call <paramref name="done"/> with its root (or null: gave up).
        void StreamIn(PlaylistRunner runner, PlaylistRunStop from, PlaylistRunStop to, Action<GameObject> done);
    }

    /// Default: the stops are package occurrences the phone's pack placed (DreamParkPackageMembership.occurrenceId);
    /// a streamed stop is shown if it exists hidden, else spawned through <see cref="PlaylistRunner.SpawnStop"/>.
    public class PlaylistDefaultStage : IPlaylistStage
    {
        protected static GameObject Find(PlaylistRunner runner, PlaylistRunStop stop, bool includeInactive)
        {
            if (runner == null || stop == null || string.IsNullOrEmpty(stop.occurrenceId)) return null;
            string instance = runner.Plan?.instanceId;
            foreach (var m in UnityEngine.Object.FindObjectsByType<DreamParkPackageMembership>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (m == null || m.occurrenceId != stop.occurrenceId) continue;
                if (!string.IsNullOrEmpty(instance) && !string.IsNullOrEmpty(m.packageInstanceId)
                    && m.packageInstanceId != instance) continue;
                return m.gameObject;
            }
            return null;
        }

        public virtual GameObject StopRoot(PlaylistRunner runner, PlaylistRunStop stop) => Find(runner, stop, false);

        protected virtual void Hide(PlaylistRunner runner, GameObject root) { if (root != null) root.SetActive(false); }
        /// run.remove (pl-sets): this stop leaves the standing set with the stage's own transition.
        public virtual void Retire(PlaylistRunner runner, GameObject root) => Hide(runner, root);
        protected virtual void Show(PlaylistRunner runner, GameObject root) { if (root != null && !root.activeSelf) root.SetActive(true); }

        public virtual void StreamIn(PlaylistRunner runner, PlaylistRunStop from, PlaylistRunStop to, Action<GameObject> done)
        {
            var old = from != null ? Find(runner, from, false) : null;
            if (old != null) Hide(runner, old);
            var root = Find(runner, to, true);
            if (root != null) { Show(runner, root); done?.Invoke(root); return; }
            if (PlaylistRunner.SpawnStop == null)
            {
                Debug.LogWarning($"[Playlist] {to?.occurrenceId} ({to?.attraction}) streams in but nothing can spawn it");
                done?.Invoke(null);
                return;
            }
            PlaylistRunner.SpawnStop(runner, to, done);
        }
    }

    /// The central navigation guard's bookkeeping (pure): refuse while a playlist owns the package; log each
    /// (package, from, to) once — titles poll a refusal every 1.5–2 s.
    public sealed class PlaylistNavigationGuard
    {
        readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);
        public int Refused { get; private set; }
        /// True = this refusal should be logged (the first of its kind). Not owned = nothing refused, nothing logged.
        public bool Refuse(bool owned, string key)
        {
            if (!owned) return false;
            Refused++;
            return _logged.Add(key ?? "");
        }
        public void Reset() { _logged.Clear(); Refused = 0; }
    }

    [DisallowMultipleComponent]
    public sealed class PlaylistRunner : MonoBehaviour, IPlaylistLeg
    {
        public const string StateName = "dp.playlist";
        public const string WireTag = "PLAYLIST_STATE";
        public const string CueTag = "PLAYLIST_CUE";
        /// The leg starts once all its stops stand here, or after this long anyway.
        public const double StandWaitSeconds = 8.0;

        // ── Hooks (core sets them; SDK defaults are local-only) ──────
        public static IPlaylistStage Stage = new PlaylistDefaultStage();
        /// Spawn a streamed stop from its seat (core: SpaceMapPacker.SpawnAt). Callback with the root or null.
        public static Action<PlaylistRunner, PlaylistRunStop, Action<GameObject>> SpawnStop;
        /// Unity → Swift ("PLAYLIST_STATE: {json}"). Default: the console.
        public static Action<string> Announce = m => Debug.Log("[Playlist] " + m);
        /// Headset goal cards over each stop as it goes live (the phone shows PLAYLIST_STATE.objectiveText instead).
        public static bool ShowCallouts = true;
        /// PLAYLIST-PLAN § 1 / § 6.1.5 (pl-sets): the PLATFORM's instruction cards (the intro / personal / boss cards and the
        /// goal line over each stop, PlaylistObjectiveCallout) are off — each game shows its own instructions. The
        /// `stop_intro` cue (Dreamie) still fires. pl-announce removes the card classes.
        public static bool PlatformCards = false;
        /// Every PLAYLIST_CUE raised on this device (name, JSON body) — for in-Unity listeners such as the Quest's
        /// Dreamie host (stop_intro / stuck / goal_replay carry playerId = this device's player).
        public static event Action<string, string> CueRaised;
        /// "What do I do?" — core sets it to the DreamBand being looked at (a glance at the wrist replays the goal).
        public static Func<bool> WristGlance;
        /// Shared clock (DreamParkSession.Now); tests and harnesses may replace it.
        public static Func<double> Clock = () => DreamParkSession.Now;

        static readonly List<PlaylistRunner> s_all = new List<PlaylistRunner>();
        public static IReadOnlyList<PlaylistRunner> All => s_all;

        // ── The central navigation guard (PLAYLISTS.md § 14.7) ─────────────────────────
        public const string RefusedReason = "playlist-owns-navigation";
        /// A level load refused because a playlist owns navigation: (host, from slot, to slot, the caller's stop
        /// occurrence id or null). auto-objectives counts a refusal from inside a live stop as its reach_exit.
        public static event Action<DreamParkPackageHost, int, int, string> LevelRefused;
        static readonly PlaylistNavigationGuard s_guard = new PlaylistNavigationGuard();
        public static int RefusedCount => s_guard.Refused;

        /// A PlaylistRunner is attached to this package: titles may not move levels (the queue does). FREE PLAY
        /// (rules.navigation "title", PlaylistFreeplay) is the exception: the title's own navigation keeps working.
        public static bool OwnsNavigation(DreamParkPackageHost host) =>
            host != null && host.TryGetComponent(out PlaylistRunner r) && r != null && r.Plan != null && !PlaylistFreeplay.TitleNavigation(r.Plan);

        /// This round is a title's free play: no stop intros, holds, cards, start_challenge or soft stop timers.
        public bool Freeplay => PlaylistFreeplay.Is(Plan);

        /// Record a refusal: the event every time (once per call), the log ONCE per (package, from, to) —
        /// LaserTag retries every 1.5 s, ArcadeLand / Zombiez every 2 s.
        public static void NoteRefused(DreamParkPackageHost host, int from, int to, GameObject caller)
        {
            string stop = null;
            var r = host != null ? host.GetComponent<PlaylistRunner>() : null;
            if (r != null && caller != null) stop = r.StopOf(caller)?.occurrenceId;
            string key = (host != null ? host.GetInstanceID() : 0) + ":" + from + ">" + to;
            if (s_guard.Refuse(true, key))
                Debug.Log($"[Playlist] level {from} → {to} refused: the playlist's queue owns navigation ({RefusedReason})"
                    + (stop != null ? $" — from stop {stop}" : "") + ". Further identical refusals are not logged.");
            try { LevelRefused?.Invoke(host, from, to, stop); }
            catch (Exception e) { Debug.LogWarning("[Playlist] LevelRefused listener threw: " + e.Message); }
        }

        public PlaylistRunPlan Plan { get; private set; }
        public PlaylistRunRow Row => _row;
        public IReadOnlyDictionary<string, PlaylistStopProgress> Progress => _prog;
        /// Bumps on every visible change (row, progress, local standing): Lua on_change polls it.
        public int Version { get; private set; }
        public bool IsLeader => _ss != null ? _ss.IsLeader : true;
        public bool Synced => _ss == null || _ss.Synced;
        public bool LegStanding => _standing;
        public string LastWhy { get; private set; }
        public event Action<PlaylistRunner> Changed;

        // ── The run (PlaylistRun) + its goal blocks (repair-oct20 CONTRACT § 2–§ 3) ─────────
        /// The run this round belongs to (null only when the run could not open).
        public PlaylistRun Run { get; private set; }
        /// A goal block owns this stop: the title's dp.playlist calls there are events only (CONTRACT § 2 precedence).
        public bool GoalDriven(string occ) => Run != null && Run.GoalDriven(occ);
        readonly HashSet<string> _titleDriven = new HashSet<string>(StringComparer.Ordinal);
        /// The title's own dp.playlist calls drive this stop (no goal block): the auto signals step aside for it.
        public bool TitleDriven(string occ) => occ != null && _titleDriven.Contains(occ);
        public void NoteTitleDriven(string occ) { if (!string.IsNullOrEmpty(occ)) _titleDriven.Add(occ); }

        bool IPlaylistLeg.Alive => this != null && Plan != null;
        GameObject IPlaylistLeg.StopRoot(PlaylistRunStop s) => Stage?.StopRoot(this, s);
        /// A goal block may run here: the stop stands (or streamed in), is on the route (its turn, or under way) and unlocked.
        bool IPlaylistLeg.IsLive(PlaylistRunStop s) =>
            s != null && _standing && _live.Contains(s.occurrenceId ?? "") && !OffRoute(s) && !StillLocked(s);
        void IPlaylistLeg.ApplyGoalProgress(string occ, double n, double of, string unit)
        {
            var j = new JSONObject();
            j.AddField("o", occ ?? ""); j.AddField("n", n); j.AddField("g", of);
            if (!string.IsNullOrEmpty(unit)) j.AddField("u", unit);
            Send("pg", j.Print());
        }
        void IPlaylistLeg.ApplyGoalOutcome(string occ, string kind)
        {
            var j = new JSONObject();
            j.AddField("o", occ ?? ""); j.AddField("r", kind ?? "done");
            Send("gb", j.Print());
        }

        SharedState _ss;
        int _ssVersion = -1;
        PlaylistRunRow _row = new PlaylistRunRow();
        readonly Dictionary<string, PlaylistStopProgress> _prog = new Dictionary<string, PlaylistStopProgress>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _declared = new Dictionary<string, string>(StringComparer.Ordinal);
        // Story flags the title set at runtime this leg (name → canonical JSON value), shared as "f.{name}" (§ 15.3a).
        readonly SortedDictionary<string, string> _flags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Flags => _flags;
        bool _standing;
        double _attachedAt = -1;
        readonly HashSet<string> _streamAsked = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _live = new HashSet<string>(StringComparer.Ordinal);       // stops standing here (leg start + streamed in)
        readonly HashSet<string> _challenged = new HashSet<string>(StringComparer.Ordinal);
        bool _beginRequested, _skipRequested;
        string _finishResult;
        double _nextEval, _nextAnnounce;
        string _lastAnnounced;
        readonly List<(GameObject root, string json)> _challenges = new List<(GameObject, string)>();

        // ── Attach / find ─────────────────────────────────────────────

        /// Run <paramref name="plan"/> on the package <paramref name="owner"/> (core: right after the playlist
        /// package commits, and again on restore — the same plan keeps the live runner and the room's state).
        public static PlaylistRunner Attach(GameObject owner, PlaylistRunPlan plan)
        {
            if (owner == null || plan == null || plan.LegCount == 0) return null;
            // Record v2 § 8.2: a playlist that needs a newer release of its title than this install never runs here.
            string refusal = PlaylistRunWorld.Main.RefusalFor(plan);
            if (refusal != null) { PlaylistRunWorld.Main.Refuse(plan, refusal); return null; }
            var r = owner.GetComponent<PlaylistRunner>();
            if (r != null && r.Plan != null && r.Plan.playlistId == plan.playlistId && r.Plan.instanceId == plan.instanceId
                && r.Plan.legIndex == plan.legIndex && (r.Plan.runId ?? "") == (plan.runId ?? ""))   // a club's package outlives its runs
                return r;
            if (r != null) DestroyImmediate(r);
            r = owner.AddComponent<PlaylistRunner>();
            r.Init(plan);
            return r;
        }

        /// The runner a script's object belongs to: its package (membership / owner), else the only runner
        /// of its title, else the only runner at all.
        public static PlaylistRunner For(GameObject go)
        {
            s_all.RemoveAll(x => x == null);
            if (s_all.Count == 0) return null;
            if (go != null)
            {
                var m = go.GetComponentInParent<DreamParkPackageMembership>(true);
                if (m != null && m.host != null)
                {
                    var r = m.host.GetComponent<PlaylistRunner>();
                    if (r != null) return r;
                }
                var direct = go.GetComponentInParent<PlaylistRunner>(true);
                if (direct != null) return direct;
                var host = DreamParkLuaAPI.Package(go);
                if (host != null && host.TryGetComponent(out PlaylistRunner hr)) return hr;
                string title = SharedState.TitleOf(go);
                PlaylistRunner only = null;
                int n = 0;
                foreach (var r in s_all)
                    if (!string.IsNullOrEmpty(title) && (r.Plan.contentId == title || r.UsesTitle(title))) { only = r; n++; }
                if (n == 1) return only;
            }
            return s_all.Count == 1 ? s_all[0] : null;
        }

        bool UsesTitle(string title)
        {
            foreach (var leg in Plan.legs) foreach (var s in leg.stops) if (Plan.ContentOf(s) == title) return true;
            return false;
        }

        void Init(PlaylistRunPlan plan)
        {
            Plan = plan;
            _row = new PlaylistRunRow { leg = Mathf.Max(0, plan.legIndex) };
            if (!s_all.Contains(this)) s_all.Add(this);
            GameObject bus = gameObject;
            var host = GetComponent<DreamParkPackageHost>();
            if (host != null && host.container != null) bus = host.container;
            try
            {
                // One table per queue item (package instance + leg): a later item never reads an earlier row.
                // A club's package stands across runs (Play again): its table is per run too.
                string run = PlaylistRoundRules.Open(plan) && !string.IsNullOrEmpty(plan.runId) ? "/" + plan.runId : "";
                _ss = SharedState.Open(StateName + "/" + (plan.instanceId ?? "") + run + "/" + plan.legIndex, bus, bus);
                if (_ss != null) _ss.Requested += OnRequested;
                // The rig is the gate (Oct 23 2026, platform-rig): a spectator phone's only request here is the start
                // circle's "begin" (SAL's start_game in a playlist = dp.playlist.begin()).
                if (_ss != null) { _ss.MarkGameplay(); _ss.AllowSpectator("begin"); }
            }
            catch (Exception e)
            {
                _ss = null;
                Debug.LogWarning("[Playlist] no shared state (solo): " + e.Message);
            }
            PlaylistScoreboard.Ensure(!string.IsNullOrEmpty(plan.runId) ? plan.runId : "pl-" + plan.playlistId);
            // The run across rounds (repair-oct20): goal blocks, playlist Lua, readiness, Dreamie moments.
            try { Run = PlaylistRunWorld.Main.Attach(this); } catch (Exception e) { Debug.LogWarning("[Playlist] no PlaylistRun: " + e.Message); }
            Log($"attached '{plan.name}' ({plan.playlistId}, run {plan.runId}) leg {plan.legIndex + 1}/{plan.LegCount} '{plan.Current?.name}' on {name}");
            Version++;
        }

        void OnDestroy()
        {
            s_all.Remove(this);
            try { PlaylistRunWorld.Main.Detach(this); } catch (Exception) { }
            foreach (var occ in _held)
            {
                var st = Plan?.FindStop(occ, out _);
                if (st != null) GamePause.ReleaseScope(Stage?.StopRoot(this, st));
            }
            _held.Clear();
            if (_ss != null) { _ss.Requested -= OnRequested; try { _ss.Dispose(); } catch (Exception) { } _ss = null; }
        }

        // ── The hook (any device) ─────────────────────────────────────

        public void Declare(string signal, string kind)
        {
            if (string.IsNullOrEmpty(signal)) return;
            _declared[signal.Trim().ToLowerInvariant()] = kind ?? "";
        }

        /// Which stop: an occurrence id, else this leg's target stop, else its only stop.
        public PlaylistRunStop ResolveStop(string occurrenceId)
        {
            if (!string.IsNullOrEmpty(occurrenceId)) return Plan.FindStop(occurrenceId, out _);
            var leg = Plan.Current;
            var t = PlaylistRunRules.TargetStop(leg);
            if (t != null) return t;
            return leg != null && leg.stops.Count == 1 ? leg.stops[0] : null;
        }

        public PlaylistRunStop StopOf(GameObject go)
        {
            if (go == null) return null;
            var m = go.GetComponentInParent<DreamParkPackageMembership>(true);
            if (m != null && !string.IsNullOrEmpty(m.occurrenceId))
            {
                var s = Plan.FindStop(m.occurrenceId, out _);
                if (s != null) return s;
            }
            // A stage that roots its stops some other way (harness stages): match by root.
            foreach (var leg in Plan.legs)
                foreach (var s in leg.stops)
                {
                    var root = Stage?.StopRoot(this, s);
                    if (root != null && (go == root || go.transform.IsChildOf(root.transform))) return s;
                }
            return null;
        }

        /// "This happened at that stop." False when the name is unknown or no stop resolves.
        public bool Signal(string occurrenceId, string signal, double? n)
        {
            string kind = PlaylistRunRules.Normalize(signal, _declared);
            var stop = ResolveStop(occurrenceId);
            if (kind == null)
            {
                // A title moment (a cue): its number rides along ("boss_phase" 2 → Dreamie's "Two pears! One more…").
                JSONObject cn = null;
                if (n.HasValue) { cn = new JSONObject(); cn.AddField("n", n.Value); }
                return Cue(signal, stop, cn);
            }
            if (stop == null) { Log($"signal '{signal}': no stop (pass the stop's object or occurrence id)"); return false; }
            if (kind == PlaylistRunRules.SignalStarted && OffRoute(stop))
            {
                // Brushing a later stop's start (SAL's spinning StartCoin) on the way to the target starts nothing (meadow F/dry-run 4).
                Log($"signal started at {stop.attraction} ({stop.occurrenceId}) ignored: not the route target, no intro yet");
                return false;
            }
            var j = new JSONObject();
            j.AddField("o", stop.occurrenceId);
            j.AddField("k", kind);
            if (n.HasValue) j.AddField("n", n.Value);
            Log($"signal {signal} → {kind}{(n.HasValue ? " " + n.Value : "")} at {stop.attraction} ({stop.occurrenceId})");
            if (PlaylistRunRules.IsBonus(kind))
            {
                // A bonus hit (§ 12): recorded on the stop (bonusHit) AND forwarded as a cue for Dreamie / the reel.
                var cj = new JSONObject();
                if (n.HasValue) cj.AddField("n", n.Value);
                cj.AddField("playerId", PlayerPresence.MyId ?? "");
                Cue(kind, stop, cj);
            }
            Send("sig", j.Print());
            return true;
        }

        /// A name that is not an objective signal is a CUE (e.g. BirthdayBash's 'wish_made'): no effect on the leg,
        /// forwarded to the phone as PLAYLIST_CUE so Dreamie can speak on the moment. Local to this device.
        public bool Cue(string name, PlaylistRunStop stop) => Cue(name, stop, null);

        /// A cue with extra fields (stop_intro: goalText, headline, variant, personal, playerId …).
        public bool Cue(string name, PlaylistRunStop stop, JSONObject extra)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var j = extra ?? new JSONObject();
            j.AddField("playlistId", Plan.playlistId ?? "");
            j.AddField("runId", Plan.runId ?? "");
            j.AddField("legId", Plan.Current?.id ?? "");
            j.AddField("name", name);
            j.AddField("occurrenceId", stop?.occurrenceId ?? "");
            j.AddField("attraction", stop?.attraction ?? "");
            Log($"cue '{name}'{(stop != null ? " at " + stop.attraction : "")}");
            string body = j.Print();
            try { Announce?.Invoke(CueTag + ": " + body); }
            catch (Exception e) { Debug.LogWarning("[Playlist] cue failed: " + e.Message); }
            try { CueRaised?.Invoke(name, body); }       // in-Unity listeners (the Quest Dreamie host)
            catch (Exception e) { Debug.LogWarning("[Playlist] cue listener threw: " + e.Message); }
            return true;
        }

        /// A challenge result (`report_result{score, completed, timeSec}`): the stop's challenge is over.
        public bool Result(string occurrenceId, double score, bool completed, double timeSec)
        {
            var stop = ResolveStop(occurrenceId);
            if (stop == null) return false;
            var j = new JSONObject();
            j.AddField("o", stop.occurrenceId);
            j.AddField("sc", score);
            j.AddField("c", completed);
            j.AddField("t", timeSec);
            Log($"result at {stop.attraction}: score {score}, completed {completed}, {timeSec:0.#} s");
            Send("res", j.Print());
            return true;
        }

        static bool SetLimit(PlaylistStopProgress p, double seconds)
        {
            if (seconds <= 0 || Math.Abs(seconds - p.limit) < 1e-6) return false;
            p.limit = seconds;
            return true;
        }

        /// The title's own progress at a stop: n of a goal ("coins" 3 of 7). Feeds the goal line and the wrist chip.
        public bool ReportProgress(string occurrenceId, double n, double goal, string unit)
        {
            var stop = ResolveStop(occurrenceId);
            if (stop == null) return false;
            var j = new JSONObject();
            j.AddField("o", stop.occurrenceId); j.AddField("n", n); j.AddField("g", goal);
            if (!string.IsNullOrEmpty(unit)) j.AddField("u", unit);
            Send("pg", j.Print());
            return true;
        }

        /// The title's own time limit for a stop (e.g. SAL's stage timer); the runner times the stop out with it.
        public bool ReportTimeLimit(string occurrenceId, double seconds)
        {
            var stop = ResolveStop(occurrenceId);
            if (stop == null || seconds <= 0) return false;
            var j = new JSONObject();
            j.AddField("o", stop.occurrenceId); j.AddField("s", seconds);
            Send("lim", j.Print());
            return true;
        }

        /// A player's absolute score in THIS leg, for the run's session scoreboard (null player = this device's).
        public void Score(string playerId, double n, string name)
        {
            if (string.IsNullOrEmpty(playerId)) playerId = PlayerPresence.MyId;
            if (string.IsNullOrEmpty(name) && playerId == PlayerPresence.MyId)
                name = PlayerPresence.LocalPlayerName();   // a guest: the headset's name (web-setup)
            PlaylistScoreboard.Report(Plan.legIndex, playerId, n, name);
            Version++;
        }

        /// One entrant's placing in THIS leg (a race / match), players and CPUs alike: place 1 = first, 0 = DNF;
        /// points null = the placings table's points for that place.
        public void Placing(string id, string name, string kind, string avatar, int place, double? points) => Placing(id, name, kind, avatar, place, points, -1);

        /// … with a medal (3 gold · 2 silver · 1 bronze · 0 none; -1 = not given): a medal-scored title's result, players
        /// and CPUs alike (Docs/CPU_PLAYERS.md). place PlaylistScoreRules.NoPlace = no finishing order.
        public void Placing(string id, string name, string kind, string avatar, int place, double? points, int medal)
        {
            if (string.IsNullOrEmpty(id)) id = PlayerPresence.MyId;
            PlaylistScoreboard.ReportPlacing(Plan.legIndex, id, name, kind, avatar, place, points, medal);
            Version++;
        }

        /// A story flag the title decides at runtime (the chosen starter): recorded on every present player's progress
        /// with this leg's `complete` (PLAYLIST_STATE.sets). Any device may set it; the leader keeps it.
        public bool SetFlag(string name, JSONObject value)
        {
            string v = PlaylistRunRules.FlagValue(value);
            if (!PlaylistRunRules.ValidFlag(name) || v == null) return false;
            if (!_flags.ContainsKey(name) && _flags.Count >= PlaylistRunRules.MaxFlags) return false;
            Send("flag", "{\"n\":\"" + name + "\",\"v\":" + v + "}");
            return true;
        }

        public void Begin() { Send("begin", "{}"); }
        public void SkipLeg() { Send("skip", "{}"); }
        /// run.finish{result} (§ 3.3): the run ends here (complete → the end screens).
        /// run.add / run.remove into the standing set (PlaylistRun.ApplySetChanges): added stops stream in on their own seat now;
        /// removed ones go with the stage's transition. Every other stop keeps its rect, its progress and keeps playing.
        public void SetStops(List<PlaylistRunStop> add, List<string> remove)
        {
            var cur = Plan?.Current;
            if (cur == null) return;
            foreach (var occ in remove ?? new List<string>())
            {
                var s = cur.stops.Find(x => x != null && x.occurrenceId == occ);
                if (s == null) continue;
                var root = Stage?.StopRoot(this, s);
                if (_held.Remove(occ)) GamePause.ReleaseScope(root);
                cur.stops.Remove(s);
                _live.Remove(occ); _present.Remove(occ); _inside.Remove(occ);
                if (root != null)
                {
                    if (Stage is PlaylistDefaultStage ds) ds.Retire(this, root);
                    else root.SetActive(false);
                }
                Log($"{s.attraction} ({occ}) leaves the set");
            }
            foreach (var s in add ?? new List<PlaylistRunStop>())
            {
                if (s == null || cur.stops.Exists(x => x != null && x.occurrenceId == s.occurrenceId)) continue;
                cur.stops.Add(s);
                Log($"{s.attraction} ({s.occurrenceId}) joins the set — streams in on its seat {(s.seat != null ? $"({s.seat.x:0.0}, {s.seat.z:0.0})" : "")} in {s.roomId}");
            }
            Version++;
        }

        public void Finish(string result) { Send("finish", "{\"r\":\"" + PlaylistRunRules.Esc(string.IsNullOrEmpty(result) ? "won" : result) + "\"}"); }

        void Send(string kind, string json)
        {
            if (_ss != null) _ss.Request(kind, json);
            else OnRequested(kind, json, "local");
        }

        // Leader only (SharedState raises it on the leader).
        void OnRequested(string kind, string json, string from)
        {
            JSONObject j = SharedState.ParseFragment(json);
            switch (kind)
            {
                case "begin": _beginRequested = true; _nextEval = 0; return;
                case "skip": _skipRequested = true; _nextEval = 0; return;
                case "finish": _finishResult = PlaylistRunPlan.Str(j, "r", "won"); _nextEval = 0; return;
                case "flag":
                {
                    string n = PlaylistRunPlan.Str(j, "n");
                    string v = PlaylistRunRules.FlagValue(j?.GetField("v"));
                    if (!PlaylistRunRules.ValidFlag(n) || v == null) return;
                    if (!_flags.ContainsKey(n) && _flags.Count >= PlaylistRunRules.MaxFlags) return;
                    if (_flags.TryGetValue(n, out var old) && old == v) return;
                    _flags[n] = v;
                    if (_ss != null) _ss.Set("f." + n, "{\"v\":" + v + "}");
                    Version++;
                    Log($"flag {n} = {v}");
                    return;
                }
                case "intro":
                {
                    // An arrival at an IDLE stop: the first one starts its (≤ 3 s) intro; the stop is held only then.
                    string io = PlaylistRunPlan.Str(j, "o");
                    if (Plan.FindStop(io, out _) == null || _intros.ContainsKey(io)) return;
                    var cur = _prog.TryGetValue(io, out var ci) ? ci : null;
                    bool anyProgress = cur != null && (cur.n > 0 || cur.kinds.Count > 0 || cur.done);
                    if (!PlaylistStopIntroRules.CanStartIdleIntro(false, anyProgress, PlaylistRunRules.StopOver(cur))) return;
                    string v = PlaylistRunPlan.Str(j, "v", PlaylistStopIntroRules.Full);
                    double t = Clock();
                    _intros[io] = (t, v);
                    var ij = new JSONObject();
                    ij.AddField("t", t); ij.AddField("v", v); ij.AddField("p", PlaylistRunPlan.Str(j, "p"));
                    if (_ss != null) _ss.Set("i." + io, ij.Print());
                    var q = cur != null ? cur.Clone() : new PlaylistStopProgress();
                    q.ts = t + PlaylistStopIntroRules.HoldSeconds(v);     // the stop's clock starts at GO
                    WriteStop(io, q);
                    Log($"idle intro at {io} ({v}) for {PlaylistRunPlan.Str(j, "p")}");
                    return;
                }
                case "gb":
                {
                    // A goal block's outcome (CONTRACT § 2): done | failed | timed_out | skip (no credit).
                    string go = PlaylistRunPlan.Str(j, "o");
                    if (Plan.FindStop(go, out _) == null) return;
                    var q = _prog.TryGetValue(go, out var gc) && gc != null ? gc.Clone() : new PlaylistStopProgress();
                    if (PlaylistRunRules.StopOver(q)) return;
                    switch (PlaylistRunPlan.Str(j, "r", "done"))
                    {
                        case "failed": PlaylistRunRules.ApplyResult(q, q.n, false, 0); break;
                        case "timed_out": case "skip": q.timedOut = true; break;
                        default: q.done = true; break;
                    }
                    if (q.ts <= 0) q.ts = Clock();
                    if (q.firstAt <= 0) q.firstAt = Clock();
                    WriteStop(go, q);
                    _nextEval = 0;
                    return;
                }
                case "pg":
                case "lim":
                {
                    string occ0 = PlaylistRunPlan.Str(j, "o");
                    if (Plan.FindStop(occ0, out _) == null) return;
                    var q = _prog.TryGetValue(occ0, out var c0) && c0 != null ? c0.Clone() : new PlaylistStopProgress();
                    bool ch = kind == "pg"
                        ? PlaylistRunRules.ApplyProgress(q, PlaylistRunPlan.Num(j, "n"), PlaylistRunPlan.Num(j, "g"), PlaylistRunPlan.Str(j, "u", null))
                        : SetLimit(q, PlaylistRunPlan.Num(j, "s"));
                    if (ch && kind == "pg" && q.firstAt <= 0 && q.n > 0) q.firstAt = Clock();
                    if (ch) { WriteStop(occ0, q); _nextEval = 0; }
                    return;
                }
                case "sig":
                case "res":
                {
                    string occ = PlaylistRunPlan.Str(j, "o");
                    if (Plan.FindStop(occ, out _) == null) return;
                    var p = _prog.TryGetValue(occ, out var cur) && cur != null ? cur.Clone() : new PlaylistStopProgress();
                    if (kind == "sig" && PlaylistRunPlan.Str(j, "k") == PlaylistRunRules.SignalStarted)
                    {
                        if (p.started || p.done || p.timedOut) return;   // the title's own start: once, the timer runs from now
                        p.started = true; p.ts = Clock();
                        WriteStop(occ, p);
                        return;
                    }
                    bool changed = kind == "sig"
                        ? PlaylistRunRules.ApplySignal(p, PlaylistRunPlan.Str(j, "k"),
                            j != null && j.HasField("n") ? (double?)PlaylistRunPlan.Num(j, "n") : null)
                        : PlaylistRunRules.ApplyResult(p, PlaylistRunPlan.Num(j, "sc"),
                            PlaylistRunPlan.Bool(j, "c", true), PlaylistRunPlan.Num(j, "t"));
                    if (!changed) return;
                    if (p.firstAt <= 0) p.firstAt = Clock();      // the first thing that happened here (clarity)
                    WriteStop(occ, p);
                    _nextEval = 0;
                    return;
                }
            }
        }

        void WriteStop(string occ, PlaylistStopProgress p)
        {
            _prog[occ] = p;
            if (_ss != null) _ss.Set("s." + occ, p.ToJson());
            Version++;
        }

        // ── Tick ──────────────────────────────────────────────────────

        void Update()
        {
            if (Plan == null) return;
            ReadShared();
            // Whole-arc playlists (§ 7.10): other rounds' stops stand held — future ones behind a locked field.
            // BEFORE ApplyLocal: on a round change the locks release the new round's stops first, then its intro
            // (TickIntros) holds them for the 3-2-1 — the other way round the release would undo the intro hold.
            try { PlaylistRoundLocks.Sync(this, Stage); }
            catch (Exception e) { Debug.LogWarning("[Playlist] round locks: " + e.Message); }
            ApplyLocal();
            double now = Clock();
            if (Synced && IsLeader && now >= _nextEval)
            {
                _nextEval = now + 0.1;
                Evaluate(now);
            }
            MaybeAnnounce(now);
        }

        void ReadShared()
        {
            if (_ss == null || _ss.Version == _ssVersion) return;
            _ssVersion = _ss.Version;
            var row = PlaylistRunRow.FromJson(SharedState.ParseFragment(_ss.GetJson("run")));
            if (row != null && (row.rev != _row.rev || !row.SameAs(_row))) { _row = row; Version++; Changed?.Invoke(this); }
            foreach (var key in _ss.Keys)
            {
                if (key.StartsWith("i.", StringComparison.Ordinal))
                {
                    var ij = SharedState.ParseFragment(_ss.GetJson(key));
                    string io = key.Substring(2);
                    if (!_intros.ContainsKey(io) && ij != null)
                        _intros[io] = (PlaylistRunPlan.Num(ij, "t"), PlaylistRunPlan.Str(ij, "v", PlaylistStopIntroRules.Full));
                    continue;
                }
                if (key.StartsWith("f.", StringComparison.Ordinal))
                {
                    string fv = PlaylistRunRules.FlagValue(SharedState.ParseFragment(_ss.GetJson(key))?.GetField("v"));
                    string fn = key.Substring(2);
                    if (fv != null && (!_flags.TryGetValue(fn, out var fo) || fo != fv)) { _flags[fn] = fv; Version++; }
                    continue;
                }
                if (!key.StartsWith("s.", StringComparison.Ordinal)) continue;
                var p = PlaylistStopProgress.FromJson(SharedState.ParseFragment(_ss.GetJson(key)));
                string occ = key.Substring(2);
                if (!_prog.TryGetValue(occ, out var old) || old == null || old.ToJson() != p.ToJson())
                {
                    _prog[occ] = p;
                    Version++;
                }
            }
        }

        void Evaluate(double now)
        {
            // A stop's challenge starts when it is live here (leg start, or streamed in) — unless the title starts it.
            if (_row.phase == PlaylistRunRules.Playing)
                foreach (var s in Plan.Current?.stops ?? new List<PlaylistRunStop>())
                {
                    if (s == null || !_live.Contains(s.occurrenceId ?? "")) continue;
                    var cur = _prog.TryGetValue(s.occurrenceId, out var c0) ? c0 : null;
                    if (cur != null && cur.ts > 0) continue;
                    // Clocks follow the route too (meadow-host, Oct 20): a later stop's clock starts when it becomes the
                    // target (or its intro / its own start), not at leg start — the Oct 20 09:22 take timed out Brick Stacks
                    // and Jubbo Crossing (0/5, 0/14) while the player was still on Block Course.
                    if (OffRoute(s)) continue;
                    var p = cur != null ? cur.Clone() : new PlaylistStopProgress();
                    p.ts = now;
                    WriteStop(s.occurrenceId, p);
                }
            // Stop timers (rules.timerSec): a challenge not over in time counts as done (timed out).
            foreach (var occ in Freeplay ? new List<string>() : PlaylistRunRules.ExpiredStops(Plan, _row, _prog, now, GoalDriven))
            {
                var p = _prog.TryGetValue(occ, out var cur) && cur != null ? cur.Clone() : new PlaylistStopProgress();
                PlaylistRunRules.TimeOut(Plan, occ, p);
                Log($"{occ}: challenge timer ran out" + (p.done ? " (a moment: done)" : ""));
                WriteStop(occ, p);
            }
            var input = new PlaylistRunInputs
            {
                now = now, legStanding = LegStanding,
                beginRequested = _beginRequested, skipRequested = _skipRequested, finishResult = _finishResult,
            };
            var next = PlaylistRunRules.Step(Plan, _row, _prog, input, out string why);
            _skipRequested = false;
            if (next != null && next.phase == PlaylistRunRules.Complete) _finishResult = null;
            if (next == null) return;
            if (next.phase != PlaylistRunRules.Ready) _beginRequested = false;
            LastWhy = why;
            Log($"{_row.phase} → {next.phase} (leg {Plan.legIndex + 1}/{Plan.LegCount} '{Plan.Current?.name}'): {why}");
            _row = next;
            if (_ss != null) _ss.Set("run", next.ToJson());
            Version++;
            Changed?.Invoke(this);
        }

        static bool Streamed(PlaylistRunStop s) => s != null && !s.standing && !string.IsNullOrEmpty(s.streamAfter);

        // Every device: is the leg standing here yet; stream stops in as their turn comes; start challenges.
        void ApplyLocal()
        {
            var stage = Stage ?? new PlaylistDefaultStage();
            var stops = Plan.Current?.stops ?? new List<PlaylistRunStop>();
            if (!_standing)
            {
                double now = Clock();
                if (_attachedAt < 0) _attachedAt = now;
                bool all = true;
                foreach (var s in stops)
                    if (s != null && !Streamed(s) && stage.StopRoot(this, s) == null) { all = false; break; }
                if (all || now - _attachedAt >= StandWaitSeconds)
                {
                    _standing = true;
                    foreach (var s in stops) if (s != null && !Streamed(s)) _live.Add(s.occurrenceId ?? "");
                    Version++;
                    Log(all ? $"leg {Plan.legIndex + 1} stands" : $"leg {Plan.legIndex + 1}: not every stop stands after {StandWaitSeconds} s — playing anyway");
                }
            }
            if (_row.phase != PlaylistRunRules.Playing) return;
            foreach (var s in stops)
            {
                if (!Streamed(s) || _streamAsked.Contains(s.occurrenceId) || !PlaylistRunRules.StreamDue(s, _prog, Plan)) continue;
                _streamAsked.Add(s.occurrenceId);
                var from = Plan.FindStop(s.streamAfter, out _);
                Log($"stream in {s.attraction} ({s.occurrenceId}) after {from?.attraction}");
                var to = s;
                try
                {
                    stage.StreamIn(this, from, to, root =>
                    {
                        _live.Add(to.occurrenceId);
                        Version++;
                        Log(root != null ? $"{to.attraction} streamed in" : $"{to.attraction} could not stream in");
                    });
                }
                catch (Exception e) { Debug.LogError("[Playlist] stream-in threw: " + e); _live.Add(to.occurrenceId); }
            }
            foreach (var s in stops)
                if (s != null && _live.Contains(s.occurrenceId ?? "") && _challenged.Add(s.occurrenceId ?? "") && !Freeplay)
                {
                    QueueChallenge(s);
                    Marker(s);
                }
            double tnow = Clock();
            foreach (var s in stops)
                if (s != null && !Freeplay && !_hinted.Contains(s.occurrenceId ?? "")
                    && PlaylistRunRules.NeedsHint(Plan.Current, s, _prog.TryGetValue(s.occurrenceId ?? "", out var hp) ? hp : null, tnow))
                {
                    _hinted.Add(s.occurrenceId ?? "");
                    // Dreamie can give a real hint; the state carries a generic one (boss 180 s, solve 120 s).
                    Cue(PlaylistChallenge.KindOf(Plan.Current, s) == PlaylistChallenge.Solve ? "solve_hint" : "boss_hint", s);
                    Version++;
                }
            TickIntros();
        }

        readonly HashSet<string> _hinted = new HashSet<string>(StringComparer.Ordinal);

        // § 3.2 (pl-sets): this wearer standing in a stop or not → challenge_enter / challenge_leave to the run's leader, who
        // keeps each player's attempt (inside / failed / finished). Raw footprint presence, not the intro's route rules.
        readonly HashSet<string> _present = new HashSet<string>(StringComparer.Ordinal);
        void Presence(PlaylistRunStop s, bool inside)
        {
            string occ = s?.occurrenceId;
            if (string.IsNullOrEmpty(occ) || inside == _present.Contains(occ)) return;
            if (inside) _present.Add(occ); else _present.Remove(occ);
            string me = PlayerPresence.MyId ?? "";
            try
            {
                PlaylistRunWorld.Main.Ingest(new PlaylistEvent { source = PlaylistEvent.Event, name = inside ? "challenge_enter" : "challenge_leave",
                    dataJson = "{}", stop = occ, player = me, from = me, at = Math.Round(Clock(), 3) });
            }
            catch (Exception e) { Debug.LogWarning("[Playlist] presence: " + e.Message); }
        }

        // ── The stop intro (PlaylistStopIntroRules; PLAYLISTS.md § 7.4) ─────────────────
        // PERSONAL by default: this device's player walks into a stop → a card in front of THEM + a
        // `stop_intro` cue (Dreamie in their ear, from their own band). An IDLE stop may hold ≤ 3 s for the
        // first arrival's intro + 3·2·1 (the leader writes "i.{occ}" once); anyone inside that window shares it.
        // A live stop never holds again. Stuck → re-explain; a wrist glance → replay. All local except "i.".

        readonly Dictionary<string, (double t, string v)> _intros = new Dictionary<string, (double, string)>(StringComparer.Ordinal);
        readonly HashSet<string> _arrived = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _inside = new HashSet<string>(StringComparer.Ordinal);
        /// ARRIVALS FOLLOW THE ROUTE (Oct 20, meadow-take; Aidan, take 3: the lines "spam"): a stop arrives (its card, Dreamie's
        /// intro, its clock) only when it is the round's target — the first stop not over yet, the one Dreamie leads to —
        /// or the wearer stays in it <see cref="ArrivalLingerS"/> (they chose to play it there). Walking across a later
        /// stop's footprint on the way to the target says nothing and starts nothing.
        public static bool ArrivalsFollowRoute = true;
        /// Oct 20 (meadow-host): 6 → 15 s. Walking across a later stop (or standing in the living room while Dreamie talks) is
        /// not choosing it; only a real stay is. The target itself arrives at once, as before.
        public const double ArrivalLingerS = 15.0;
        readonly Dictionary<string, double> _lingerSince = new Dictionary<string, double>(StringComparer.Ordinal);

        bool IsRouteTarget(List<PlaylistRunStop> stops, PlaylistRunStop s)
        {
            if (s == null || s.bookend) return true;
            if (Plan != null && Plan.sets) return true;   // § 3.2 sets: every challenge of the set is open at once, in any order
            string occ = s.occurrenceId ?? "";
            if (_intros.ContainsKey(occ)) return true;          // someone's arrival already started its intro (shared)
            var sp = P(s);
            // Already being played: its own start (or intro, above). Progress alone does NOT make a stop the target (Oct 20,
            // meadow-host): a coin grabbed in passing must not pull the route there — but it is still counted (an out-of-order
            // player who finishes a stop before its linger arrival keeps their 3/3, not 0/3).
            if (sp != null && sp.started) return true;
            if (_arrived.Contains(occ)) return true;            // this wearer arrived here (lingered: chose to play it now)
            foreach (var x in stops)
            {
                if (x == null || x.bookend || !_live.Contains(x.occurrenceId ?? "") || StillLocked(x)) continue;
                if (PlaylistRunRules.StopOver(P(x))) continue;
                return x == s;
            }
            return true;
        }

        /// Is this stop the one being played now (`dp.playlist.stop().target`)? The round's target (the first stop not over,
        /// the one Dreamie leads to), or one whose intro already ran / that is already being played, or every stop when
        /// arrivals don't follow the route. False for a later stop the wearer is only passing, and for other rounds' stops.
        internal bool IsTarget(PlaylistRunStop s)
        {
            if (s == null) return false;
            if (!ArrivalsFollowRoute || s.bookend) return true;
            var stops = Plan?.Current?.stops;
            return stops != null && stops.Contains(s) && IsRouteTarget(stops, s);
        }

        /// A current-round stop that is not the target and had no intro: its `started` / progress are not taken (a brush on
        /// the way through must not become the target or start its clock). Other rounds' stops keep the old behaviour.
        bool OffRoute(PlaylistRunStop s)
        {
            if (!ArrivalsFollowRoute || s == null || s.bookend || Freeplay || Plan.sets) return false;
            var stops = Plan?.Current?.stops;
            return stops != null && stops.Contains(s) && !IsRouteTarget(stops, s);
        }
        readonly Dictionary<string, double> _asked = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly HashSet<string> _held = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, PlaylistObjectiveCallout> _markers = new Dictionary<string, PlaylistObjectiveCallout>(StringComparer.Ordinal);
        readonly Dictionary<string, double> _lastChange = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _lastProg = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> s_seenKinds = new HashSet<string>(StringComparer.Ordinal);
        static string s_seenRun;
        double _lastReplay = -99, _nextPresence;
        bool _glanced;

        /// The stop intro a stop got (shared start time, variant), if any.
        public bool TryGetIntro(string occurrenceId, out double startedAt, out string variant)
        {
            startedAt = 0; variant = null;
            if (occurrenceId == null || !_intros.TryGetValue(occurrenceId, out var iv)) return false;
            startedAt = iv.t; variant = iv.v;
            return true;
        }

        /// Harness/tests: forget this device's arrivals (as if another player's headset were looking).
        public void ResetLocalArrivals() { _arrived.Clear(); _inside.Clear(); _asked.Clear(); }

        bool Headset => ShowCallouts && (IsHeadset != null ? IsHeadset() : !DreamParkDevice.IsPhone);
        /// Is this device a headset (its own objective cards, markers, stop intros)? Default: not a phone. A harness that
        /// simulates the phone for the bounce but films the headset's view sets it.
        public static Func<bool> IsHeadset;

        PlaylistStopProgress P(PlaylistRunStop s) => s != null && _prog.TryGetValue(s.occurrenceId ?? "", out var p) ? p : null;

        // "Timed" = a real timed round (the stop's authored seconds, or the playlist's timerSec) — never a soft limit.
        string VariantFor(PlaylistRunStop s) =>
            PlaylistStopIntroRules.Variant(s, s.seconds > 0 ? s.seconds : (Plan.rules?.timerSec ?? 0),
                s_seenKinds.Contains(PlaylistObjectiveText.Name(s)));

        string Goal(PlaylistRunStop s)
        {
            string g = PlaylistObjectiveText.StopGoal(Plan.Current, s, P(s));
            return PlaylistStopIntroRules.IsBoss(s) && !string.IsNullOrEmpty(s.introHint) ? g + "\n" + s.introHint : g;
        }

        static bool Inside(GameObject root, Vector3 pos)
        {
            if (root == null) return false;
            var at = root.GetComponentInChildren<AttractionTemplate>(true);
            if (at != null)
            {
                Vector2 fp = at.RuntimeFootprintMeters;
                Vector3 d = pos - at.transform.position;
                const float margin = 0.25f;
                return Mathf.Abs(Vector3.Dot(d, at.transform.right)) <= fp.x * 0.5f + margin
                    && Mathf.Abs(Vector3.Dot(d, at.transform.forward)) <= fp.y * 0.5f + margin;
            }
            bool any = false; var b = new Bounds();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any && pos.x >= b.min.x && pos.x <= b.max.x && pos.z >= b.min.z && pos.z <= b.max.z;
        }

        void IntroCue(PlaylistRunStop s, string variant, bool shared)
        {
            var j = new JSONObject();
            j.AddField("goalText", Goal(s));
            j.AddField("headline", PlaylistStopIntroRules.Headline(variant, s, shared));
            j.AddField("variant", variant);
            j.AddField("personal", true);
            j.AddField("shared", shared);
            j.AddField("playerId", PlayerPresence.MyId ?? "");
            Cue("stop_intro", s, j);
        }

        void ShowShared(PlaylistRunStop s, double t, string v)
        {
            s_seenKinds.Add(PlaylistObjectiveText.Name(s));
            IntroCue(s, v, true);
            if (Headset && PlatformCards)
                PlaylistObjectiveCallout.Intro(v, t, Clock, () => PlaylistStopIntroRules.Headline(v, s, true), () => Goal(s));
            _lastChange[s.occurrenceId] = t + PlaylistStopIntroRules.HoldSeconds(v);
        }

        void ShowPersonal(PlaylistRunStop s, string v, string cue = "stop_intro")
        {
            s_seenKinds.Add(PlaylistObjectiveText.Name(s));
            if (cue == "stop_intro") IntroCue(s, v, false);
            else
            {
                var j = new JSONObject();
                j.AddField("goalText", Goal(s)); j.AddField("personal", true); j.AddField("playerId", PlayerPresence.MyId ?? "");
                Cue(cue, s, j);
            }
            if (Headset && PlatformCards)
                PlaylistObjectiveCallout.Personal(v, Clock, () => PlaylistStopIntroRules.Headline(v, s, false), () => Goal(s),
                    shown => PlaylistStopIntroRules.PersonalFooter(shown, P(s), Plan.Current?.objective?.type));
            _lastChange[s.occurrenceId] = Clock();
        }

        bool StillLocked(PlaylistRunStop s) =>
            (PlaylistRoundRules.Gated(s) && PlaylistRoundRules.GateNeeds(Plan.Current, s, P2).Exists(n => !n.done))
            || (s != null && Run != null && Run.LockOf(s.occurrenceId) != null);   // the playlist Lua's run.lock
        PlaylistStopProgress P2(string occ) => occ != null && _prog.TryGetValue(occ, out var p) ? p : null;

        static bool SafeHold() { try { return PlaylistObjectiveCallout.HoldOff(); } catch { return false; } }

        // The low-key goal line over a live stop (re-readable any time), until it is over.
        void Marker(PlaylistRunStop s)
        {
            if (!Headset || !PlatformCards || _markers.ContainsKey(s.occurrenceId ?? "")) return;
            var root = Stage?.StopRoot(this, s);
            if (root == null) return;
            var stop = s;
            _markers[s.occurrenceId ?? ""] = PlaylistObjectiveCallout.Marker(root, () => Goal(stop),
                () => this != null && !PlaylistRunRules.StopOver(P(stop)) && _row.phase == PlaylistRunRules.Playing);
        }

        void TickIntros()
        {
            if (Freeplay) return;   // free play: the title runs as it always does
            if (s_seenRun != (Plan.runId ?? Plan.playlistId)) { s_seenRun = Plan.runId ?? Plan.playlistId; s_seenKinds.Clear(); }
            double now = Clock();
            var stage = Stage ?? new PlaylistDefaultStage();
            var stops = Plan.Current?.stops ?? new List<PlaylistRunStop>();
            // Holds: only an idle intro's ≤ 3 s window (every device holds its own copy of the stop).
            foreach (var s in stops)
            {
                if (s == null) continue;
                string occ = s.occurrenceId ?? "";
                bool held = _intros.TryGetValue(occ, out var iv) && PlaylistStopIntroRules.Held(iv.t, iv.v, now);
                if (held && !_held.Contains(occ))
                {
                    var root = stage.StopRoot(this, s);
                    if (root != null) { GamePause.HoldScope(root); _held.Add(occ); }
                }
                else if (!held && _held.Remove(occ)) GamePause.ReleaseScope(stage.StopRoot(this, s));
            }
            if (_row.phase != PlaylistRunRules.Playing) return;

            // Progress per stop (anyone's): the stuck clock restarts on every change.
            foreach (var s in stops)
            {
                string occ = s?.occurrenceId ?? "";
                string pj = P(s)?.ToJson() ?? "";
                if (!_lastProg.TryGetValue(occ, out var old) || old != pj) { _lastProg[occ] = pj; if (_lastChange.ContainsKey(occ)) _lastChange[occ] = now; }
            }

            if (now < _nextPresence) return;
            _nextPresence = now + 0.2;
            var head = PlaylistObjectiveCallout.Head();
            if (head == null) return;
            Vector3 pos = head.position;
            PlaylistRunStop here = null;
            bool heldOff = PlaylistObjectiveCallout.HoldOff != null && SafeHold();
            // ONE arrival per tick (F23: two stop intros fired together → two cards stacked), the route's target first.
            bool arrivedNow = false;
            var order = new List<PlaylistRunStop>(stops);
            int ti = order.FindIndex(x => x != null && !x.bookend && IsRouteTarget(stops, x) && _live.Contains(x.occurrenceId ?? ""));
            if (ti > 0) { var tgt = order[ti]; order.RemoveAt(ti); order.Insert(0, tgt); }
            foreach (var s in order)
            {
                if (s == null || !_live.Contains(s.occurrenceId ?? "")) continue;
                string occ = s.occurrenceId;
                // While another moment holds the stage (the round diorama, PlaylistObjectiveCallout.HoldOff) nobody "arrives":
                // the intro plays once it lets go, for whoever is standing in the stop then.
                // A stop the playlist still locks (lockedUntil) is not entered: its sign + Dreamie's line say what is left.
                bool rawInside = Inside(stage.StopRoot(this, s), pos);
                Presence(s, rawInside && !StillLocked(s));
                bool inside = !heldOff && !StillLocked(s) && rawInside;
                if (inside && ArrivalsFollowRoute && !_arrived.Contains(occ) && !IsRouteTarget(stops, s))
                {
                    // Not the route's target: crossing it is not arriving (it arrives once it is the target, or after a linger,
                    // and never over the card that is already up).
                    if (!_lingerSince.TryGetValue(occ, out double since)) { _lingerSince[occ] = now; inside = false; Log($"pass-through {s.attraction} ({occ}): not the target, no intro"); }
                    else if (now - since < ArrivalLingerS || PlaylistObjectiveCallout.AnyCardUp) inside = false;
                }
                else if (!inside) _lingerSince.Remove(occ);
                // Another stop arrived this tick: this one waits for the next (it is still "outside", so it arrives then).
                if (inside && arrivedNow && !_arrived.Contains(occ) && !_inside.Contains(occ)) inside = false;
                bool was = _inside.Contains(occ);
                if (inside) { _inside.Add(occ); here = here ?? s; } else _inside.Remove(occ);
                bool over = PlaylistRunRules.StopOver(P(s));
                if (inside && !was && !_arrived.Contains(occ) && !over)
                {
                    Log($"arrival {s.attraction} ({occ}){(IsRouteTarget(stops, s) ? " [target]" : " [linger]")}");
                    _arrived.Add(occ); arrivedNow = true;
                    string v = VariantFor(s);
                    if (_intros.TryGetValue(occ, out var iv))
                    {
                        if (PlaylistStopIntroRules.SharesIntro(iv.t, iv.v, now)) ShowShared(s, iv.t, iv.v);
                        else ShowPersonal(s, v);
                    }
                    else
                    {
                        var cur = P(s);
                        bool anyProgress = cur != null && (cur.n > 0 || cur.kinds.Count > 0 || cur.done);
                        if (PlaylistStopIntroRules.CanStartIdleIntro(false, anyProgress, over))
                        {
                            var j = new JSONObject();
                            j.AddField("o", occ); j.AddField("v", v); j.AddField("p", PlayerPresence.MyId ?? "");
                            Send("intro", j.Print());
                            _asked[occ] = now;
                        }
                        else ShowPersonal(s, v);
                    }
                }
                // Stuck: in the stop, nothing changed for a minute → Dreamie re-explains, the card replays
                // (never in a live timed round: the title signalled `started` and its clock still runs).
                var sp = P(s);
                if (inside && _lastChange.TryGetValue(occ, out var lc)
                    && PlaylistStopIntroRules.IsStuck(true, over, now, lc, sp != null && sp.started, PlaylistRunRules.StopLeft(Plan, s, sp, now)))
                    ShowPersonal(s, PlaylistStopIntroRules.Short, "stuck");
            }
            // An arrival waiting for the leader's idle intro.
            foreach (var kv in new List<KeyValuePair<string, double>>(_asked))
            {
                var s = Plan.FindStop(kv.Key, out _);
                if (_intros.TryGetValue(kv.Key, out var iv))
                {
                    _asked.Remove(kv.Key);
                    if (PlaylistStopIntroRules.SharesIntro(iv.t, iv.v, now)) ShowShared(s, iv.t, iv.v);
                    else ShowPersonal(s, VariantFor(s));
                    break;   // one card per tick
                }
                else if (now - kv.Value >= PlaylistStopIntroRules.AskTimeoutSeconds) { _asked.Remove(kv.Key); ShowPersonal(s, VariantFor(s)); break; }
            }
            // "What do I do?" — a glance at the band in a live stop replays its goal.
            bool glance = false;
            try { glance = WristGlance != null && WristGlance(); } catch (Exception) { }
            if (glance && !_glanced && here != null && now - _lastReplay >= PlaylistStopIntroRules.ReplayCooldownSeconds)
            {
                _lastReplay = now;
                ShowPersonal(here, PlaylistStopIntroRules.Short, "goal_replay");
            }
            _glanced = glance;
        }

        void QueueChallenge(PlaylistRunStop s)
        {
            var leg = Plan.Current;
            var root = Stage?.StopRoot(this, s);
            if (root == null) return;
            var j = new JSONObject();
            j.AddField("playlist", Plan.playlistId ?? "");
            j.AddField("runId", Plan.runId ?? "");
            j.AddField("leg", Plan.legIndex + 1);
            j.AddField("stop", s.occurrenceId ?? "");
            j.AddField("attraction", s.attraction ?? "");
            j.AddField("role", s.role ?? "");
            j.AddField("seconds", PlaylistRunRules.StopTimer(Plan, s, _prog.TryGetValue(s.occurrenceId ?? "", out var sp) ? sp : null));
            j.AddField("goalText", PlaylistObjectiveText.StopGoal(leg, s, null));
            j.AddField("objective", leg?.objective?.type ?? "");
            j.AddField("count", leg?.objective?.count ?? 0);
            var opt = string.IsNullOrEmpty(s.options) ? null : SharedState.ParseFragment(s.options);
            if (opt != null && opt.type == JSONObject.Type.Object) j.AddField("options", opt);
            _challenges.Add((root, j.Print()));
        }

        public bool HasChallenges => _challenges.Count > 0;

        /// Lua pump: start_challenge(params) for each stop whose leg just started (drained once).
        public List<(GameObject root, string json)> DrainChallenges()
        {
            var list = new List<(GameObject, string)>(_challenges);
            _challenges.Clear();
            return list;
        }

        public string StateJson() => PlaylistRunRules.StateJson(Plan, _row, _prog, Clock(),
            PlaylistScoreboard.BoardJson(PlaylistRunRules.DefaultScoring(Plan)), Players(), _flags,
            Run == null || Run.MayAdvance(Plan.legIndex), Run?.NextPlaylist, Run?.NextState, Run?.WaypointsJson(), Run?.BrokenJson,
            Run != null ? Run.StopStatus : (Func<string, (string, int, bool)?>)null, Run != null ? Run.Locked : (Func<string, bool>)null);

        /// Players in the session (this device + headset peers; phones watch). Analytics' players / playersEnd.
        public static int Players()
        {
            int n = 1;
            var pp = PlayerPresence.Instance;
            if (pp == null) return n;
            foreach (var kv in pp.Peers)
                if (kv.Value != null && DreamParkLuaAPI.DeviceOf(kv.Value.id) != "phone") n++;
            return n;
        }

        void MaybeAnnounce(double now)
        {
            string body = StateJson();
            // Progress lines change every frame on timed objectives: compare without "left".
            string key = _row.rev + "|" + _row.phase + "|" + Version;
            if (key == _lastAnnounced) return;
            bool rowChange = _lastAnnounced == null || !_lastAnnounced.StartsWith(_row.rev + "|" + _row.phase + "|", StringComparison.Ordinal);
            if (!rowChange && now < _nextAnnounce) return;
            _lastAnnounced = key;
            _nextAnnounce = now + 0.5;
            try { Announce?.Invoke(WireTag + ": " + body); }
            catch (Exception e) { Debug.LogWarning("[Playlist] announce failed: " + e.Message); }
        }

        void Log(string line) => Debug.Log("[Playlist] " + line);
    }
}
