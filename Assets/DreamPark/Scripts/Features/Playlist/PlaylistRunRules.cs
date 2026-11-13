// ─────────────────────────────────────────────────────────────────────
//  PlaylistRunRules.cs — the playlist RUNNER's data + rules (pure, no
//  UnityEngine). Docs/PLAYLISTS.md § 7 "Running" is the contract.
//
//  Playlists are programs for the GameMaster QUEUE: each leg is one queue
//  item {contentId, arrangement {playlistId, legId}, durationMinutes,
//  advance}. The phone packs the item's arrangement; the runner plays THAT
//  leg: ready → playing → cleared (3 s hold) → leg_complete (the phone's
//  queue advances, packing the next leg's arrangement with the approved
//  bounce-out/in) or, on the last leg, complete ("Adventure complete!").
//  Titles report what happened at a stop as SIGNALS ("boss_defeated",
//  "coins" 12, "complete", a mini-game's result); the rules here turn them
//  into leg completion, score, lives and stop timers. Deterministic from
//  (plan, run row, stop rows, clock): the leader evaluates, others render.
//
//  PlaylistRunner (MonoBehaviour) is the glue: SharedState, clock, swaps,
//  the phone wire and the Lua surface (DreamParkLuaPlaylist).
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Defective.JSON;

namespace DreamPark
{
    [Serializable]
    public sealed class PlaylistRunSeat
    {
        public float x, y, z, yaw;
        public string frameId;
        public float sx = 1f, sz = 1f;
    }

    [Serializable]
    public sealed class PlaylistRunStop
    {
        public string occurrenceId;
        public string attraction;
        public string resourceAddress;
        public string role;
        public string rectId;
        public string roomId;
        public string roomLabel;
        /// The title this stop belongs to (null = the plan's contentId). v1 runs one title per leg.
        public string contentId;
        public bool standing = true;
        public PlaylistRunSeat seat;
        /// Challenge time for this stop (0 = the policy's timerSec). Bosses, minibosses and bookends are never timed.
        public float seconds;
        /// A bookend (the playlist's intro / outro stop): never cut by a timer.
        public bool bookend;
        /// Streamed within the leg (§ 6.1): this stop is not standing at the leg's start; it streams in on its seat
        /// when the stop with this occurrence id completes (that one bounces out, this one in).
        public string streamAfter;
        /// Opaque per-stop options for the title's Lua (mode, difficulty, holes …): a JSON object, passed as-is.
        public string options;
        /// Player-facing name (the tour's / Dreamie's); empty = the attraction leaf, prettified.
        public string displayName;
        /// Author override of the stop's one-line goal ("Find the golden key!"); empty = the phrasing rule.
        public string objectiveText;
        /// A boss's weak point / tip for its reveal ("Hit the glowing belly!"), from the author or the title.
        public string introHint;
        /// The challenge kind (objective catalog, PLAYLISTS.md § 12: collect, reach_end, boss, clear, survive, score,
        /// race, versus, catch, find, solve, protect, moment); empty = derived from the role + the leg objective.
        public string challenge;
        /// Bonus modifiers (no_hit, combo, clock): a bonus line on the card, reported as cues, never required.
        public List<string> bonus = new List<string>();
        /// The Web catalog id (slug of the resourceName), for analytics.
        public string attractionId;
        /// The designer's estimate (catalog expectedSec), for analytics' "expected vs played"; 0 = unknown.
        public float expectedSec;
        /// Club mode (§ 6.0a, playlist-packer): the court(s) this stop takes the floor of FOR ITS ROUND (comma-separated
        /// court occurrence ids). It is not standing: it spawns when its round starts, those courts hide meanwhile.
        public string displaces;
        /// AUTHORED LOCK (§ 6.0a, playlist-packer Oct 19): this stop stays locked until these same-round stops
        /// (occurrence ids) are done; lockedNeeds = their player-facing names (fallback). Empty = never locked.
        public List<string> lockedUntil = new List<string>();
        public List<string> lockedNeeds = new List<string>();
        /// The authored stop `id` (stable across packs; run.lock / goal blocks address it). Null = none authored.
        public string stopId;
        /// The stop's goal (repair-oct20 CONTRACT § 2): "" / "default" = the title's library block for its kind, else the
        /// inline goal-block Lua (`goal: {lua}`).
        public string goal;
        /// Record v2 § 8.6: the force field's sign while `lockedUntil` holds this stop ("" = "Finish … to unlock").
        public string lockSign;
        /// Record v2 § 8.5: the stop's Dreamie guidance {guide, intro, react[]} as JSON text ("" = none).
        public string dreamieJson;
        /// Sets (§ 3.1): the placement wish the playlist Lua gave this attraction — biggest | entrance | outdoor |
        /// far_from:<ref>; "" = none. The packer honours it; the space only caps the room count.
        public string place;
    }

    [Serializable]
    public sealed class PlaylistRunObjective
    {
        public string type = PlaylistRunRules.ReachExit;
        public string target;
        public int count;
        public float seconds;
    }

    [Serializable]
    public sealed class PlaylistRunLeg
    {
        public string id;
        public string name;
        public string beat;
        public float minutes;
        public string next = PlaylistRunRules.WalkOn;
        public PlaylistRunObjective objective = new PlaylistRunObjective();
        /// Author override of the leg's one-line goal; empty = the phrasing rule.
        public string objectiveText;
        public List<PlaylistRunStop> stops = new List<PlaylistRunStop>();
        /// The round's AUTHORED bonus levels, best first (§ 6.0; empty = automatic picks only). The bonus actually out
        /// is park content (the packed plan's `bonus`, `bonusAuthored`), never a runner stop.
        public List<string> bonusLevels = new List<string>();
        /// Why this round puts out no bonus ("" = it may): authored noBonus, or the planner's automatic default
        /// (pvp, race, short scored round); "authored only" = only its bonusLevels.
        public string noBonus = "";
        /// Record v2 § 8.5: the round's Dreamie guidance {guide, react[]} as JSON text ("" = none).
        public string dreamieJson;
    }

    /// The playlist's `rules` block (PLAYLISTS.md § 2 schema, § 7.8 meaning): the fusion layer's meta-state.
    [Serializable]
    public sealed class PlaylistRunPolicy
    {
        /// Per-stop time limit in seconds (0 = none): a stop not done in time counts as done (timed out).
        public float timerSec;
        /// "" (none) | sum (points add up across legs and titles) | best (best single leg) |
        /// elimination (reserved in v1: team play, scored like sum) | placings (points per place, summed; ties by
        /// most 1st / 2nd … places, then the best finish; DNF = 0 and last; CPUs ranked with players).
        public string scoring = "";
        /// Team lives (0 = unlimited). Each failed result costs one; 0 left ends the run (result "out").
        public int lives;
        /// on_complete (the leg's objective) | on_timer (the leg clears when every stop is done or timed out).
        public string advance = PlaylistRunRules.AdvanceComplete;
        /// Reserved: the playlist-level Lua script (§ 7.9).
        public string script;
        /// Runtime only: auto-start once leg 1 stands (false = wait for dp.playlist.begin()).
        public bool autoStart = true;
        /// repair-oct20: who owns level / hub navigation. "" = the playlist (the central guard refuses dp.load_level);
        /// "title" = the title's own navigation keeps working (FREEPLAY, the title's default playlist).
        public string navigation = "";
        public const string NavigationTitle = "title";
    }

    public sealed class PlaylistRunPlan
    {
        public string playlistId, contentId, instanceId, name, kindWord;
        public List<string> badges = new List<string>();
        public PlaylistRunPolicy rules = new PlaylistRunPolicy();
        /// Reserved: the playlist-level Lua script ref (§ 7.9; design only in v1).
        public string script;
        /// Either every leg of the playlist, or (a published package, PLAYLISTS.md § 5a) only the item's leg.
        public List<PlaylistRunLeg> legs = new List<PlaylistRunLeg>();
        /// The queue item's arrangement: the leg it plays (0-based, in the whole playlist), the playlist's leg
        /// count (0 = legs.Count), and the load it belongs to.
        public int legIndex;
        public int legCount;
        public string runId;
        public string owner = "title";
        /// The playlist's authored `sets` {flags, badges} as JSON text ("" = none, § 15.3a): recorded on a WON run.
        public string setsJson = "";
        /// Multi-room spaces (RELEASE PUSH, Oct 18): the packer placed EVERY round's stops up front and a round change
        /// does not re-pack. Stops of other rounds stand HELD (PlaylistRoundLocks); one-room / streaming = false.
        public bool allStanding;
        /// § 6.0a who is locked while another round plays: "rounds" (force fields on future rounds), "open" (a club: every
        /// court idle and walk-up-able, no fields — only the current objective highlighted), "none". Null/empty = derived:
        /// "rounds" when allStanding, else "none" (plans from before this field).
        public string locks;
        public string Locks => !string.IsNullOrEmpty(locks) ? locks : allStanding ? "rounds" : "none";
        /// The playlist record's revision (rev-pinned run, CONTRACT § 3), "" = the compiled manifest copy.
        public string rev;
        /// The playlist's own Lua (`lua`: on_start / on_stop_result / choose_next / on_finale), "" = none.
        public string lua;
        /// The playlist's Dreamie script (`dreamie` {story, beats, react, avoid}) and timing caps (`timing`
        /// {totalMin, rounds{legId: min}, mode}) as JSON text ("" = none).
        public string dreamieJson, timingJson;
        /// The playlist's word for its rounds (Race / Races); null = Round / Rounds (PlaylistRunRules.RoundWord).
        public PlaylistRoundWord roundWord;
        /// Record v2 § 8.2: the minimum title release this playlist needs (0 = any); PlaylistRun refuses an older install.
        public int requiresRelease;
        /// Record v2 § 8.3: how the group plays it (Results + Dreamie's context): mode solo|coop|versus|party, players [min, max].
        public PlaylistPlay play;
        public string PlayMode => play?.mode ?? "";
        /// SETS (PLAYLIST-PLAN § 3.1, pl-sets): the record is an attraction POOL + Lua; what is out comes from the playlist
        /// Lua's run.show / run.add / run.remove at run time. Each set the Lua puts out is one leg (id "set<n>", legIndex
        /// n-1), packed by the run's leader; the run ends only on run.finish (no last leg). False = an arc[] record
        /// (its rounds are legs, the converted default Lua shows them in order).
        public bool sets;
        /// The attraction pool (sets mode): each stop = an attraction with the challenge it offers and its options.
        public List<PlaylistRunStop> pool = new List<PlaylistRunStop>();
        /// § 3.4 `requires.badges`: the group may play when ANY present account holds every one of them.
        public List<string> requiresBadges = new List<string>();

        /// The leg this item plays.
        public PlaylistRunLeg Current => legs != null && legs.Count == 1 ? legs[0] : Leg(legIndex);
        /// The current leg's POSITION in `legs` (a set's plan holds only its own leg: position 0, legIndex = set - 1).
        public int CurrentPosition => legs != null && legs.Count == 1 ? 0 : legIndex;
        /// Sets mode never has a last leg: only run.finish ends the run.
        public bool IsLastLeg => !sets && legIndex >= LegCount - 1;

        public int LegCount => legCount > 0 ? legCount : (legs?.Count ?? 0);
        public PlaylistRunLeg Leg(int i) => legs != null && i >= 0 && i < legs.Count ? legs[i] : null;

        public PlaylistRunStop FindStop(string occurrenceId, out int legIndex)
        {
            legIndex = -1;
            if (string.IsNullOrEmpty(occurrenceId) || legs == null) return null;
            for (int l = 0; l < legs.Count; l++)
                foreach (var s in legs[l].stops ?? new List<PlaylistRunStop>())
                    if (s != null && s.occurrenceId == occurrenceId) { legIndex = l; return s; }
            return null;
        }

        public string ContentOf(PlaylistRunStop s) => !string.IsNullOrEmpty(s?.contentId) ? s.contentId : contentId;

        // ── JSON ─────────────────────────────────────────────────────
        public string ToJson()
        {
            var o = new JSONObject();
            o.AddField("playlistId", playlistId ?? "");
            o.AddField("contentId", contentId ?? "");
            o.AddField("instanceId", instanceId ?? "");
            o.AddField("name", name ?? "");
            o.AddField("kindWord", kindWord ?? "Adventure");
            o.AddField("legIndex", legIndex);
            if (legCount > 0) o.AddField("legCount", legCount);
            if (!string.IsNullOrEmpty(runId)) o.AddField("runId", runId);
            o.AddField("owner", owner ?? "title");
            if (allStanding) o.AddField("allStanding", true);
            if (!string.IsNullOrEmpty(locks)) o.AddField("locks", locks);
            var b = new JSONObject(JSONObject.Type.Array);
            foreach (var x in badges ?? new List<string>()) b.Add(x ?? "");
            o.AddField("badges", b);
            if (!string.IsNullOrEmpty(script)) o.AddField("script", script);
            if (!string.IsNullOrEmpty(rev)) o.AddField("rev", rev);
            if (roundWord != null && !roundWord.IsDefault)
            {
                var rw = new JSONObject(); var (one, many) = PlaylistRoundWord.Of(roundWord);
                rw.AddField("one", one); rw.AddField("many", many);
                o.AddField("roundWord", rw);
            }
            if (!string.IsNullOrEmpty(lua)) o.AddField("lua", lua);
            if (sets) o.AddField("sets", true);
            if (requiresBadges != null && requiresBadges.Count > 0)
            {
                var rq = new JSONObject(); var rb = new JSONObject(JSONObject.Type.Array);
                foreach (var x in requiresBadges) rb.Add(x ?? "");
                rq.AddField("badges", rb); o.AddField("requires", rq);
            }
            if (pool != null && pool.Count > 0)
            {
                var pj = new JSONObject(JSONObject.Type.Array);
                foreach (var s in pool) if (s != null) pj.Add(StopJson(s));
                o.AddField("pool", pj);
            }
            if (requiresRelease > 0) o.AddField("requiresRelease", requiresRelease);
            if (play != null && !play.IsEmpty)
            {
                var pj = new JSONObject();
                if (!string.IsNullOrEmpty(play.mode)) pj.AddField("mode", play.mode);
                if (play.players != null && play.players.Count > 0) { var pa = new JSONObject(JSONObject.Type.Array); foreach (int n in play.players) pa.Add(n); pj.AddField("players", pa); }
                o.AddField("play", pj);
            }
            var dj = string.IsNullOrEmpty(dreamieJson) ? null : SharedState.ParseFragment(dreamieJson);
            if (dj != null && dj.type == JSONObject.Type.Object) o.AddField("dreamie", dj);
            var tj = string.IsNullOrEmpty(timingJson) ? null : SharedState.ParseFragment(timingJson);
            if (tj != null && tj.type == JSONObject.Type.Object) o.AddField("timing", tj);
            var setsJ = PlaylistRunRules.AuthoredSets(setsJson);
            if (setsJ != null) o.AddField("authoredSets", setsJ);
            var r = rules ?? new PlaylistRunPolicy();
            var rj = new JSONObject();
            rj.AddField("timerSec", r.timerSec);
            rj.AddField("scoring", r.scoring ?? "");
            rj.AddField("lives", r.lives);
            rj.AddField("advance", r.advance ?? PlaylistRunRules.AdvanceComplete);
            if (!string.IsNullOrEmpty(r.script)) rj.AddField("script", r.script);
            rj.AddField("autoStart", r.autoStart);
            if (!string.IsNullOrEmpty(r.navigation)) rj.AddField("navigation", r.navigation);
            o.AddField("rules", rj);
            var legsJ = new JSONObject(JSONObject.Type.Array);
            foreach (var leg in legs ?? new List<PlaylistRunLeg>())
            {
                if (leg == null) continue;
                var lj = new JSONObject();
                lj.AddField("id", leg.id ?? "");
                lj.AddField("name", leg.name ?? "");
                lj.AddField("beat", leg.beat ?? "");
                lj.AddField("minutes", leg.minutes);
                lj.AddField("next", leg.next ?? PlaylistRunRules.WalkOn);
                if (!string.IsNullOrEmpty(leg.objectiveText)) lj.AddField("objectiveText", leg.objectiveText);
                if (leg.bonusLevels != null && leg.bonusLevels.Count > 0)
                {
                    var blj = new JSONObject(JSONObject.Type.Array); foreach (var x in leg.bonusLevels) blj.Add(x ?? "");
                    lj.AddField("bonusLevels", blj);
                }
                if (!string.IsNullOrEmpty(leg.noBonus)) lj.AddField("noBonus", leg.noBonus);
                var ldj = string.IsNullOrEmpty(leg.dreamieJson) ? null : SharedState.ParseFragment(leg.dreamieJson);
                if (ldj != null && ldj.type == JSONObject.Type.Object) lj.AddField("dreamie", ldj);
                var ob = leg.objective ?? new PlaylistRunObjective();
                var oj = new JSONObject();
                oj.AddField("type", ob.type ?? PlaylistRunRules.ReachExit);
                oj.AddField("target", ob.target ?? "");
                oj.AddField("count", ob.count);
                oj.AddField("seconds", ob.seconds);
                lj.AddField("objective", oj);
                var sj = new JSONObject(JSONObject.Type.Array);
                foreach (var s in leg.stops ?? new List<PlaylistRunStop>()) if (s != null) sj.Add(StopJson(s));
                lj.AddField("stops", sj);
                legsJ.Add(lj);
            }
            o.AddField("legs", legsJ);
            return o.Print();
        }

        /// One stop as plan JSON (legs[].stops[] and pool[]).
        public static JSONObject StopJson(PlaylistRunStop s)
        {
            var st = new JSONObject();
            st.AddField("occurrenceId", s.occurrenceId ?? "");
            st.AddField("attraction", s.attraction ?? "");
            st.AddField("resourceAddress", s.resourceAddress ?? "");
            st.AddField("role", s.role ?? "stage");
            st.AddField("rectId", s.rectId ?? "");
            st.AddField("roomId", s.roomId ?? "");
            st.AddField("roomLabel", s.roomLabel ?? "");
            if (!string.IsNullOrEmpty(s.contentId)) st.AddField("contentId", s.contentId);
            st.AddField("standing", s.standing);
            if (s.seconds > 0f) st.AddField("seconds", s.seconds);
            if (s.bookend) st.AddField("bookend", true);
            if (!string.IsNullOrEmpty(s.displayName)) st.AddField("displayName", s.displayName);
            if (!string.IsNullOrEmpty(s.objectiveText)) st.AddField("objectiveText", s.objectiveText);
            if (!string.IsNullOrEmpty(s.introHint)) st.AddField("introHint", s.introHint);
            if (!string.IsNullOrEmpty(s.challenge)) st.AddField("challenge", s.challenge);
            if (s.bonus != null && s.bonus.Count > 0)
            {
                var bj = new JSONObject(JSONObject.Type.Array);
                foreach (var x in s.bonus) bj.Add(x ?? "");
                st.AddField("bonus", bj);
            }
            if (!string.IsNullOrEmpty(s.attractionId)) st.AddField("attractionId", s.attractionId);
            if (s.expectedSec > 0) st.AddField("expectedSec", s.expectedSec);
            if (!string.IsNullOrEmpty(s.displaces)) st.AddField("displaces", s.displaces);
            if (!string.IsNullOrEmpty(s.stopId)) st.AddField("id", s.stopId);
            if (!string.IsNullOrEmpty(s.goal) && s.goal != "default")
            {
                var gj = new JSONObject(); gj.AddField("lua", s.goal);
                st.AddField("goal", gj);
            }
            if (s.lockedUntil != null && s.lockedUntil.Count > 0)
            {
                var lu = new JSONObject(JSONObject.Type.Array); foreach (var x in s.lockedUntil) lu.Add(x ?? "");
                st.AddField("lockedUntil", lu);
                var ln = new JSONObject(JSONObject.Type.Array); foreach (var x in s.lockedNeeds ?? new List<string>()) ln.Add(x ?? "");
                st.AddField("lockedNeeds", ln);
            }
            if (!string.IsNullOrEmpty(s.lockSign)) st.AddField("lockSign", s.lockSign);
            if (!string.IsNullOrEmpty(s.place)) st.AddField("place", s.place);
            var sdj = string.IsNullOrEmpty(s.dreamieJson) ? null : SharedState.ParseFragment(s.dreamieJson);
            if (sdj != null && sdj.type == JSONObject.Type.Object) st.AddField("dreamie", sdj);
            if (!string.IsNullOrEmpty(s.streamAfter))
            {
                var si = new JSONObject();
                si.AddField("after", s.streamAfter);
                st.AddField("streamIn", si);
            }
            if (!string.IsNullOrEmpty(s.options))
            {
                var oj2 = SharedState.ParseFragment(s.options);
                if (oj2 != null && oj2.type == JSONObject.Type.Object) st.AddField("options", oj2);
            }
            if (s.seat != null)
            {
                var se = new JSONObject();
                se.AddField("x", s.seat.x); se.AddField("y", s.seat.y); se.AddField("z", s.seat.z);
                se.AddField("yaw", s.seat.yaw); se.AddField("frameId", s.seat.frameId ?? "base");
                se.AddField("sx", s.seat.sx); se.AddField("sz", s.seat.sz);
                st.AddField("seat", se);
            }
            return st;
        }

        /// One plan-JSON stop → the model (legs[].stops[] and pool[]).
        public static PlaylistRunStop StopFromJson(JSONObject st)
        {
            if (st == null || st.type != JSONObject.Type.Object) return null;
            var s = new PlaylistRunStop
            {
                occurrenceId = Str(st, "occurrenceId"), attraction = Str(st, "attraction"),
                resourceAddress = Str(st, "resourceAddress"), role = Str(st, "role", "stage"),
                rectId = Str(st, "rectId"), roomId = Str(st, "roomId"), roomLabel = Str(st, "roomLabel"),
                contentId = Str(st, "contentId", null), standing = Bool(st, "standing", true),
                seconds = (float)Num(st, "seconds"), bookend = Bool(st, "bookend", false),
                displayName = Str(st, "displayName", null), objectiveText = Str(st, "objectiveText", null), introHint = Str(st, "introHint", null),
                challenge = Str(st, "challenge", null), attractionId = Str(st, "attractionId", null),
                expectedSec = (float)Num(st, "expectedSec"),
                displaces = Str(st, "displaces", null),
                stopId = Str(st, "id", null),
                place = Str(st, "place", null),
            };
            s.lockSign = Str(st, "lockSign", null);
            var sdm = st.GetField("dreamie");
            if (sdm != null && sdm.type == JSONObject.Type.Object) s.dreamieJson = sdm.Print();
            var gl = st.GetField("goal");
            if (gl != null && gl.type == JSONObject.Type.Object) s.goal = Str(gl, "lua", null);
            else if (gl != null && gl.type == JSONObject.Type.String && gl.stringValue != "default") s.goal = gl.stringValue;
            var si = st.GetField("streamIn");
            if (si != null && si.type == JSONObject.Type.Object) s.streamAfter = Str(si, "after", null);
            else if (si != null && si.type == JSONObject.Type.String) s.streamAfter = si.stringValue;
            var bo = st.GetField("bonus");
            if (bo?.list != null) foreach (var x in bo.list) if (x != null && x.type == JSONObject.Type.String) s.bonus.Add(x.stringValue);
            var lk = st.GetField("lockedUntil");
            if (lk?.list != null) foreach (var x in lk.list) if (x != null && x.type == JSONObject.Type.String) s.lockedUntil.Add(x.stringValue);
            var lnn = st.GetField("lockedNeeds");
            if (lnn?.list != null) foreach (var x in lnn.list) if (x != null && x.type == JSONObject.Type.String) s.lockedNeeds.Add(x.stringValue);
            var op = st.GetField("options");
            if (op != null && op.type == JSONObject.Type.Object) s.options = op.Print();
            var se = st.GetField("seat");
            if (se != null && se.type == JSONObject.Type.Object)
                s.seat = new PlaylistRunSeat
                {
                    x = (float)Num(se, "x"), y = (float)Num(se, "y"), z = (float)Num(se, "z"),
                    yaw = (float)Num(se, "yaw"), frameId = Str(se, "frameId", "base"),
                    sx = (float)Num(se, "sx", 1), sz = (float)Num(se, "sz", 1),
                };
            return s;
        }

        public static PlaylistRunPlan FromJson(string json)
        {
            JSONObject o;
            try { o = new JSONObject(json); } catch (Exception) { return null; }
            if (o == null || o.type != JSONObject.Type.Object) return null;
            var p = new PlaylistRunPlan
            {
                playlistId = Str(o, "playlistId"), contentId = Str(o, "contentId"),
                instanceId = Str(o, "instanceId"), name = Str(o, "name"),
                kindWord = Str(o, "kindWord", "Adventure"), script = Str(o, "script", null),
                legIndex = (int)Num(o, "legIndex"), legCount = (int)Num(o, "legCount"),
                runId = Str(o, "runId", null), owner = Str(o, "owner", "title"),
                allStanding = o.GetField("allStanding") is JSONObject asj && asj.type == JSONObject.Type.Bool && asj.boolValue,
                locks = Str(o, "locks", null),
                rev = Str(o, "rev", null), lua = Str(o, "lua", null),
            };
            p.roundWord = PlaylistRunRules.ParseRoundWord(o.GetField("roundWord"));
            p.requiresRelease = (int)Num(o, "requiresRelease");
            p.play = PlaylistRunRules.ParsePlay(o.GetField("play"));
            var dmj = o.GetField("dreamie");
            if (dmj != null && dmj.type == JSONObject.Type.Object) p.dreamieJson = dmj.Print();
            var tmj = o.GetField("timing");
            if (tmj != null && tmj.type == JSONObject.Type.Object) p.timingJson = tmj.Print();
            var b = o.GetField("badges");
            if (b != null && b.list != null) foreach (var x in b.list) if (x != null && x.type == JSONObject.Type.String) p.badges.Add(x.stringValue);
            // "sets" is the sets-mode flag (bool); the authored {flags, badges} ride "authoredSets" (older plans: an object "sets").
            var setsJ = o.GetField("authoredSets");
            var setsF = o.GetField("sets");
            if (setsF != null && setsF.type == JSONObject.Type.Bool) p.sets = setsF.boolValue;
            else if (setsJ == null && setsF != null) setsJ = setsF;
            if (setsJ != null) p.setsJson = setsJ.Print();
            var poolJ = o.GetField("pool");
            if (poolJ?.list != null) foreach (var x in poolJ.list) { var ps = StopFromJson(x); if (ps != null) p.pool.Add(ps); }
            var rqJ = o.GetField("requires");
            var rbJ = rqJ != null && rqJ.type == JSONObject.Type.Object ? rqJ.GetField("badges") : null;
            if (rbJ?.list != null) foreach (var x in rbJ.list) if (x != null && x.type == JSONObject.Type.String && !string.IsNullOrEmpty(x.stringValue)) p.requiresBadges.Add(x.stringValue);
            var rj = o.GetField("rules");
            if (rj != null && rj.type == JSONObject.Type.Object)
            {
                p.rules.timerSec = (float)Num(rj, "timerSec");
                p.rules.scoring = Str(rj, "scoring", "");
                p.rules.lives = (int)Num(rj, "lives");
                p.rules.advance = Str(rj, "advance", AdvanceDefault);
                if (string.IsNullOrEmpty(p.rules.advance)) p.rules.advance = AdvanceDefault;
                p.rules.script = Str(rj, "script", null);
                p.rules.autoStart = Bool(rj, "autoStart", true);
                p.rules.navigation = Str(rj, "navigation", "");
            }
            var legs = o.GetField("legs");
            if (legs != null && legs.list != null)
                foreach (var lj in legs.list)
                {
                    if (lj == null || lj.type != JSONObject.Type.Object) continue;
                    var leg = new PlaylistRunLeg
                    {
                        id = Str(lj, "id"), name = Str(lj, "name"), beat = Str(lj, "beat"),
                        minutes = (float)Num(lj, "minutes"), next = Str(lj, "next", PlaylistRunRules.WalkOn),
                        objectiveText = Str(lj, "objectiveText", null),
                    };
                    var blj = lj.GetField("bonusLevels");
                    if (blj?.list != null) foreach (var x in blj.list) if (x != null && x.type == JSONObject.Type.String) leg.bonusLevels.Add(x.stringValue);
                    leg.noBonus = Str(lj, "noBonus", "");
                    var ldm = lj.GetField("dreamie");
                    if (ldm != null && ldm.type == JSONObject.Type.Object) leg.dreamieJson = ldm.Print();
                    var oj = lj.GetField("objective");
                    if (oj != null && oj.type == JSONObject.Type.Object)
                    {
                        leg.objective.type = Str(oj, "type", PlaylistRunRules.ReachExit);
                        leg.objective.target = Str(oj, "target", null);
                        if (leg.objective.target == "") leg.objective.target = null;
                        leg.objective.count = (int)Num(oj, "count");
                        leg.objective.seconds = (float)Num(oj, "seconds");
                    }
                    var sj = lj.GetField("stops");
                    if (sj != null && sj.list != null)
                        foreach (var st in sj.list) { var s = StopFromJson(st); if (s != null) leg.stops.Add(s); }
                    p.legs.Add(leg);
                }
            return p;
        }

        const string AdvanceDefault = PlaylistRunRules.AdvanceComplete;

        internal static string Str(JSONObject o, string k, string fallback = "")
        {
            var f = o?.GetField(k);
            return f != null && f.type == JSONObject.Type.String ? f.stringValue : fallback;
        }
        internal static double Num(JSONObject o, string k, double fallback = 0)
        {
            var f = o?.GetField(k);
            return f != null && f.type == JSONObject.Type.Number ? f.doubleValue : fallback;
        }
        internal static bool Bool(JSONObject o, string k, bool fallback)
        {
            var f = o?.GetField(k);
            return f != null && f.type == JSONObject.Type.Bool ? f.boolValue : fallback;
        }
    }

    /// One stop's shared progress (SharedState key "s.{occurrenceId}").
    public sealed class PlaylistStopProgress
    {
        public bool done;          // the stop is beaten / its challenge is over
        public double n;           // absolute progress (score / collect), max-merged
        public int failures;       // failed results (each costs a life when the policy has lives)
        public double timeSec;     // the challenge's own time, when it reported one
        public bool timedOut;      // the stop timer ran out (counts as done; result timed_out)
        public double ts;          // shared clock: the stop's challenge started (0 = not yet: streamed, or before the leg)
        public bool started;       // the title said so itself (its own 3-2-1 countdown ended): ts is that moment
        public bool resultFailed;  // its challenge result said "not completed" (a lost round), so its result is failed
        public double firstAt;     // shared clock: first progress or signal at this stop (analytics: clarity), 0 = none yet
        public double goal;        // the stop's own target the title reported (e.g. 7 coins), 0 = none
        public string unit;        // what n counts ("coins", "points") — the title's word
        public double limit;       // the title's own time limit for this stop (seconds), 0 = none (e.g. SAL's stage timer)

        /// done | timed_out | failed | "" (not over) — the reel's objective result.
        /// A boss beaten after a team wipe is DONE (failures are reported separately); failed = a lost challenge round.
        public string Result => timedOut && !done ? "timed_out" : done ? (resultFailed ? "failed" : "done") : "";
        public readonly SortedSet<string> kinds = new SortedSet<string>(StringComparer.Ordinal);

        public bool Hit(string type) => done || (type != null && kinds.Contains(type));

        public string ToJson()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"d\":").Append(done ? "1" : "0");
            sb.Append(",\"n\":").Append(PlaylistRunRules.Num(n));
            if (failures > 0) sb.Append(",\"f\":").Append(failures.ToString(CultureInfo.InvariantCulture));
            if (timeSec > 0) sb.Append(",\"t\":").Append(PlaylistRunRules.Num(timeSec));
            if (timedOut) sb.Append(",\"to\":1");
            if (ts > 0) sb.Append(",\"ts\":").Append(PlaylistRunRules.Num(ts));
            if (started) sb.Append(",\"st\":1");
            if (resultFailed) sb.Append(",\"rf\":1");
            if (firstAt > 0) sb.Append(",\"fa\":").Append(PlaylistRunRules.Num(firstAt));
            if (goal > 0) sb.Append(",\"g\":").Append(PlaylistRunRules.Num(goal));
            if (!string.IsNullOrEmpty(unit)) sb.Append(",\"u\":\"").Append(PlaylistRunRules.Esc(unit)).Append('"');
            if (limit > 0) sb.Append(",\"l\":").Append(PlaylistRunRules.Num(limit));
            sb.Append(",\"k\":\"").Append(string.Join(",", kinds)).Append("\"}");
            return sb.ToString();
        }

        public static PlaylistStopProgress FromJson(JSONObject j)
        {
            var p = new PlaylistStopProgress();
            if (j == null || j.type != JSONObject.Type.Object) return p;
            p.done = PlaylistRunPlan.Num(j, "d") > 0;
            p.n = PlaylistRunPlan.Num(j, "n");
            p.failures = (int)PlaylistRunPlan.Num(j, "f");
            p.timeSec = PlaylistRunPlan.Num(j, "t");
            p.timedOut = PlaylistRunPlan.Num(j, "to") > 0;
            p.ts = PlaylistRunPlan.Num(j, "ts");
            p.started = PlaylistRunPlan.Num(j, "st") > 0;
            p.resultFailed = PlaylistRunPlan.Num(j, "rf") > 0;
            p.firstAt = PlaylistRunPlan.Num(j, "fa");
            p.goal = PlaylistRunPlan.Num(j, "g");
            p.unit = PlaylistRunPlan.Str(j, "u", null);
            p.limit = PlaylistRunPlan.Num(j, "l");
            foreach (var k in PlaylistRunPlan.Str(j, "k").Split(','))
                if (k.Length > 0) p.kinds.Add(k);
            return p;
        }

        public PlaylistStopProgress Clone()
        {
            var c = new PlaylistStopProgress { done = done, n = n, failures = failures, timeSec = timeSec, timedOut = timedOut, ts = ts, started = started, goal = goal, unit = unit, limit = limit, resultFailed = resultFailed, firstAt = firstAt };
            foreach (var k in kinds) c.kinds.Add(k);
            return c;
        }
    }

    /// The leader-owned run row (SharedState key "run").
    public sealed class PlaylistRunRow
    {
        public int leg;               // 0-based (= plan.legIndex: one queue item plays one leg)
        public string phase = PlaylistRunRules.Ready;
        public double t0;             // shared clock: the current leg started playing
        public double tp;             // shared clock: the current phase started
        public int rev;               // bumps on every row change
        public string result = "";    // cleared / leg_complete: objective | timer | skip ; complete: won | out

        public PlaylistRunRow Clone() => (PlaylistRunRow)MemberwiseClone();

        public bool SameAs(PlaylistRunRow o) =>
            o != null && o.leg == leg && o.phase == phase && o.result == result;

        public string ToJson() =>
            "{\"leg\":" + leg.ToString(CultureInfo.InvariantCulture)
            + ",\"ph\":\"" + phase + "\""
            + ",\"t0\":" + PlaylistRunRules.Num(t0)
            + ",\"tp\":" + PlaylistRunRules.Num(tp)
            + ",\"rev\":" + rev.ToString(CultureInfo.InvariantCulture)
            + ",\"res\":\"" + (result ?? "") + "\"}";

        public static PlaylistRunRow FromJson(JSONObject j)
        {
            if (j == null || j.type != JSONObject.Type.Object) return null;
            return new PlaylistRunRow
            {
                leg = (int)PlaylistRunPlan.Num(j, "leg"),
                phase = PlaylistRunPlan.Str(j, "ph", PlaylistRunRules.Ready),
                t0 = PlaylistRunPlan.Num(j, "t0"),
                tp = PlaylistRunPlan.Num(j, "tp"),
                rev = (int)PlaylistRunPlan.Num(j, "rev"),
                result = PlaylistRunPlan.Str(j, "res"),
            };
        }
    }

    /// What the leader needs to know about THIS device right now (inputs to <see cref="PlaylistRunRules.Step"/>).
    public struct PlaylistRunInputs
    {
        public double now;
        /// The current leg's stops stand on the leader (its local swap finished).
        public bool legStanding;
        /// dp.playlist.begin() was called (ready → playing without autoStart).
        public bool beginRequested;
        /// dp.playlist.skip_leg() was called (the current leg's objective is met).
        public bool skipRequested;
        /// run.finish{result} (§ 3.3): the playlist's Lua ended the run (null = not asked).
        public string finishResult;
    }

    public static class PlaylistRunRules
    {
        // Phases. leg_complete = this queue item's leg is done (the phone advances the queue); complete = the
        // playlist's last leg is done ("Adventure complete!") or the team ran out of lives.
        public const string Ready = "ready", Playing = "playing", Cleared = "cleared",
                            LegDone = "leg_complete", Complete = "complete";
        // Objective types (PLAYLISTS.md § 2)
        public const string ReachExit = "reach_exit", DefeatBoss = "defeat_boss", Score = "score",
                            Collect = "collect", Survive = "survive", Time = "time";
        // Signal kinds beyond the objective types
        public const string SignalComplete = "complete", SignalFailed = "failed";
        /// The title's own start (its 3-2-1 countdown ended): the stop's timer starts now (applied by the leader).
        public const string SignalStarted = "started";
        /// Bonus modifiers (§ 12): recorded on the stop (bonusHit) and forwarded as cues; never gate anything.
        public const string BonusNoHit = "no_hit", BonusCombo = "combo", BonusClock = "clock";
        public static bool IsBonus(string kind) => kind == BonusNoHit || kind == BonusCombo || kind == BonusClock;
        public const string WalkOn = "walk_on", SwapInPlace = "swap_in_place";
        public const string AdvanceComplete = "on_complete", AdvanceTimer = "on_timer";
        public const string ScoringNone = "", ScoringSum = "sum", ScoringBest = "best", ScoringElimination = "elimination",
                            ScoringPlacings = "placings";

        /// A `roundWord` JSON value ({one, many} or a bare "Race") → the model, null when absent / empty.
        public static PlaylistRoundWord ParseRoundWord(JSONObject j)
        {
            if (j == null) return null;
            if (j.type == JSONObject.Type.String) return string.IsNullOrWhiteSpace(j.stringValue) ? null : new PlaylistRoundWord { one = j.stringValue.Trim() };
            if (j.type != JSONObject.Type.Object) return null;
            string one = PlaylistRunPlan.Str(j, "one", null), many = PlaylistRunPlan.Str(j, "many", null);
            return string.IsNullOrWhiteSpace(one) ? null : new PlaylistRoundWord { one = one.Trim(), many = string.IsNullOrWhiteSpace(many) ? null : many.Trim() };
        }

        /// A `play` JSON value {mode, players: [min, max]} → the model, null when absent / empty (record v2 § 8.3).
        public static PlaylistPlay ParsePlay(JSONObject j)
        {
            if (j == null || j.type != JSONObject.Type.Object) return null;
            var p = new PlaylistPlay { mode = (PlaylistRunPlan.Str(j, "mode", "") ?? "").Trim().ToLowerInvariant() };
            var pl = j.GetField("players");
            if (pl?.list != null) foreach (var x in pl.list) if (x != null && x.type == JSONObject.Type.Number && p.players.Count < 2) p.players.Add((int)Math.Round(x.doubleValue));
            return p.IsEmpty ? null : p;
        }

        /// Record v2 § 8.2: why this device must not run the plan (its `requiresRelease` is newer than the installed
        /// release of its title), or null. installed ≤ 0 = unknown (an editor / local build): allowed.
        public static string ReleaseRefusal(PlaylistRunPlan plan, int installed)
        {
            int need = plan?.requiresRelease ?? 0;
            if (need <= 0 || installed <= 0 || installed >= need) return null;
            return $"'{plan.name ?? plan.playlistId}' needs {plan.contentId} release {need}; this device has release {installed} — update the title to play it";
        }

        /// The colocated Dreamie guidance (record v2 § 8.5) for a moment: the stop's, else its round's, else null
        /// (the playlist-level beats come after, in PlaylistRun's composer).
        public static string ColocatedGuide(PlaylistRunPlan plan, string moment, string stopOcc)
        {
            if (plan == null || string.IsNullOrEmpty(moment)) return null;
            PlaylistRunLeg leg = null;
            PlaylistRunStop stop = null;
            if (!string.IsNullOrEmpty(stopOcc))
            {
                stop = plan.FindStop(stopOcc, out int li);
                if (stop == null)
                    foreach (var l in plan.legs ?? new List<PlaylistRunLeg>())
                        foreach (var s in l?.stops ?? new List<PlaylistRunStop>())
                            if (stop == null && s != null && s.stopId == stopOcc) { stop = s; li = plan.legs.IndexOf(l); }
                if (stop != null && li >= 0) leg = plan.Leg(li);
            }
            leg = leg ?? plan.Current;
            string g = stop != null ? PlaylistRecordV2.GuideFor(stop.dreamieJson, moment, false) : null;
            return g ?? PlaylistRecordV2.GuideFor(leg?.dreamieJson, moment, true);
        }

        /// Record v2: what Dreamie's §6 `context` gets from the run's plan (merge these keys into it):
        ///   play {mode, players [min, max]}, roundWord {one, many} (only when the playlist names one),
        ///   dreamie {round: the current round's {guide, react[]…}, stop: the stop's {guide, intro, react[]…}}.
        /// JSON object text ("{}" = nothing to add). stopOcc = the stop in play (occurrence or authored id), may be null.
        public static string DreamieContextJson(PlaylistRunPlan plan, string stopOcc)
        {
            var c = new JSONObject();
            if (plan == null) return "{}";
            if (plan.play != null && !plan.play.IsEmpty)
            {
                var pj = new JSONObject();
                if (!string.IsNullOrEmpty(plan.play.mode)) pj.AddField("mode", plan.play.mode);
                if (plan.play.players != null && plan.play.players.Count > 0) { var pa = new JSONObject(JSONObject.Type.Array); foreach (int n in plan.play.players) pa.Add(n); pj.AddField("players", pa); }
                c.AddField("play", pj);
            }
            if (plan.roundWord != null && !plan.roundWord.IsDefault)
            {
                var (one, many) = RoundWord(plan);
                var rw = new JSONObject(); rw.AddField("one", one); rw.AddField("many", many);
                c.AddField("roundWord", rw);
            }
            var d = new JSONObject();
            PlaylistRunStop stop = null; int li = -1;
            if (!string.IsNullOrEmpty(stopOcc))
            {
                stop = plan.FindStop(stopOcc, out li);
                if (stop == null)
                    for (int l = 0; l < (plan.legs?.Count ?? 0) && stop == null; l++)
                        foreach (var x in plan.legs[l]?.stops ?? new List<PlaylistRunStop>())
                            if (x != null && x.stopId == stopOcc) { stop = x; li = l; break; }
            }
            var leg = li >= 0 ? plan.Leg(li) : plan.Current;
            var lj = string.IsNullOrEmpty(leg?.dreamieJson) ? null : SharedState.ParseFragment(leg.dreamieJson);
            if (lj != null && lj.type == JSONObject.Type.Object && lj.count > 0) d.AddField("round", lj);
            var sj = string.IsNullOrEmpty(stop?.dreamieJson) ? null : SharedState.ParseFragment(stop.dreamieJson);
            if (sj != null && sj.type == JSONObject.Type.Object && sj.count > 0) d.AddField("stop", sj);
            if (d.count > 0) c.AddField("dreamie", d);
            return c.Print();
        }

        /// The plan's round word (one, many): "Race" / "Races"; none → ("", "").
        public static (string one, string many) RoundWord(PlaylistRunPlan plan) => PlaylistRoundWord.Of(plan?.roundWord);

        /// "Race 2 of 5" (the part now playing) — only when the playlist authored its word; "" otherwise, for a one-part playlist
        /// and in sets mode (§ 2.3: never "round" / "x of y" that players did not ask for).
        public static string RoundLabel(PlaylistRunPlan plan)
        {
            if (plan == null || plan.sets || plan.LegCount <= 1) return "";
            string one = RoundWord(plan).one;
            if (string.IsNullOrWhiteSpace(one)) return "";
            return one + " " + (Math.Max(0, plan.legIndex) + 1) + " of " + plan.LegCount;
        }

        /// The run's scoring (DECISIONS "Scoring defaults", CONTRACT § 3): `rules.scoring` when authored, else one title →
        /// `sum`, several titles (a crossover) → `placings` (15·12·10·8·6·4·2·1).
        public static string DefaultScoring(PlaylistRunPlan plan)
        {
            string authored = plan?.rules?.scoring;
            if (!string.IsNullOrEmpty(authored)) return authored;
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(plan?.contentId)) titles.Add(plan.contentId);
            foreach (var leg in plan?.legs ?? new List<PlaylistRunLeg>())
                foreach (var st in leg?.stops ?? new List<PlaylistRunStop>())
                    if (st != null && !st.bookend) { string c = plan.ContentOf(st); if (!string.IsNullOrEmpty(c)) titles.Add(c); }
            // A Beta upload and its base id are one title.
            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in titles) bases.Add(t.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) ? t.Substring(0, t.Length - 4) : t);
            return bases.Count > 1 ? ScoringPlacings : ScoringSum;
        }

        /// The "leg complete" beat: the title's own victory fanfare plays before anything moves.
        public const double ClearHoldSeconds = 3.0;
        /// Nobody gets stuck: a non-boss stop with no timer of its own times out after this (3.5 min).
        public const double DefaultStopSeconds = 210.0;
        /// A boss is never timed, but after this long unbeaten the state carries a hint (and a boss_hint cue).
        public const double BossHintSeconds = 180.0;

        static readonly Dictionary<string, string> BuiltInAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { ReachExit, ReachExit }, { "stage_won", ReachExit }, { "exit_reached", ReachExit }, { "exit", ReachExit },
            { DefeatBoss, DefeatBoss }, { "boss_defeated", DefeatBoss }, { "boss", DefeatBoss },
            { Score, Score }, { "points", Score },
            { Collect, Collect }, { "coins", Collect }, { "collected", Collect },
            { Survive, Survive }, { "survived", Survive },
            { SignalComplete, SignalComplete }, { "won", SignalComplete }, { "beaten", SignalComplete },
            { SignalFailed, SignalFailed }, { "lost", SignalFailed },
            { SignalStarted, SignalStarted }, { "countdown_done", SignalStarted }, { "go", SignalStarted },
            { BonusNoHit, BonusNoHit }, { "untouchable", BonusNoHit }, { BonusCombo, BonusCombo }, { BonusClock, BonusClock },
        };

        /// A signal name → its kind (objective type, complete, failed), title declarations first. Null = unknown.
        public static string Normalize(string signal, IReadOnlyDictionary<string, string> declared)
        {
            if (string.IsNullOrEmpty(signal)) return null;
            string s = signal.Trim().ToLowerInvariant();
            if (declared != null && declared.TryGetValue(s, out string d) && !string.IsNullOrEmpty(d))
            {
                d = d.Trim().ToLowerInvariant();
                return BuiltInAliases.TryGetValue(d, out string dd) ? dd : null;
            }
            return BuiltInAliases.TryGetValue(s, out string k) ? k : null;
        }

        /// Merge one signal into a stop's progress. Idempotent for one-shot kinds; n is max-merged.
        /// Returns true when something changed.
        public static bool ApplySignal(PlaylistStopProgress p, string kind, double? n)
        {
            if (p == null || string.IsNullOrEmpty(kind)) return false;
            bool changed = false;
            if (kind == SignalStarted) return false;   // the leader stamps ts with its clock (PlaylistRunner)
            if (IsBonus(kind)) return p.kinds.Add(kind);   // a bonus hit: no progress, no completion
            if (kind == SignalComplete) { if (!p.done) { p.done = true; changed = true; } }
            else if (kind == SignalFailed) { p.failures++; changed = true; }
            else if (kind == Score || kind == Collect)
            {
                if (p.kinds.Add(kind)) changed = true;
            }
            else if (p.kinds.Add(kind)) changed = true;
            if (n.HasValue && !double.IsNaN(n.Value) && n.Value > p.n) { p.n = n.Value; changed = true; }
            return changed;
        }

        /// A challenge result (`report_result{score, completed, timeSec}`): the stop's challenge is over.
        public static bool ApplyResult(PlaylistStopProgress p, double score, bool completed, double timeSec)
        {
            if (p == null) return false;
            bool changed = false;
            if (score > p.n) { p.n = score; changed = true; }
            if (timeSec > 0 && Math.Abs(timeSec - p.timeSec) > 1e-6) { p.timeSec = timeSec; changed = true; }
            if (!p.done) { p.done = true; changed = true; }
            if (!completed) { p.failures++; p.resultFailed = true; changed = true; }
            return changed;
        }

        static string Leaf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOfAny(new[] { '/', '\\' });
            return i >= 0 ? s.Substring(i + 1) : s;
        }

        static bool Matches(PlaylistRunStop s, string target) =>
            s != null && !string.IsNullOrEmpty(target)
            && (s.occurrenceId == target || Leaf(s.attraction) == Leaf(target) || Leaf(s.resourceAddress) == Leaf(target));

        /// The stop an objective is about: its target, else (defeat_boss) the boss, else the miniboss. Null = all stops.
        public static PlaylistRunStop TargetStop(PlaylistRunLeg leg)
        {
            if (leg?.stops == null) return null;
            var ob = leg.objective ?? new PlaylistRunObjective();
            if (!string.IsNullOrEmpty(ob.target))
                foreach (var s in leg.stops) if (Matches(s, ob.target)) return s;
            if (ob.type == DefeatBoss)
            {
                foreach (var s in leg.stops) if (s != null && s.role == "boss") return s;
                foreach (var s in leg.stops) if (s != null && s.role == "miniboss") return s;
            }
            return null;
        }

        static PlaylistStopProgress Of(IReadOnlyDictionary<string, PlaylistStopProgress> prog, PlaylistRunStop s)
            => s != null && prog != null && prog.TryGetValue(s.occurrenceId ?? "", out var p) && p != null ? p : null;

        static bool Done(IReadOnlyDictionary<string, PlaylistStopProgress> prog, PlaylistRunStop s)
        {
            var p = Of(prog, s);
            return p != null && (p.done || p.timedOut);
        }

        static bool AllDone(PlaylistRunLeg leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog, string orKind)
        {
            if (leg?.stops == null || leg.stops.Count == 0) return false;
            foreach (var s in leg.stops)
            {
                if (s == null) continue;
                var p = Of(prog, s);
                if (p == null) return false;
                if (!(p.done || p.timedOut || (orKind != null && p.kinds.Contains(orKind)))) return false;
            }
            return true;
        }

        public static double LegSum(PlaylistRunLeg leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog)
        {
            double sum = 0;
            if (leg?.stops == null) return 0;
            foreach (var s in leg.stops) { var p = Of(prog, s); if (p != null) sum += Math.Max(0, p.n); }
            return sum;
        }

        /// Is the leg's OBJECTIVE met (timers of the policy are handled by <see cref="Step"/>)?
        public static bool LegComplete(PlaylistRunLeg leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog,
            double legElapsed, out string why)
        {
            why = null;
            if (leg == null) return false;
            var ob = leg.objective ?? new PlaylistRunObjective();
            if (AllDone(leg, prog, null)) { why = "all stops complete"; return true; }
            switch (ob.type)
            {
                case ReachExit:
                {
                    var t = TargetStop(leg);
                    if (t != null) { var p = Of(prog, t); if (p != null && (p.Hit(ReachExit) || p.timedOut)) { why = "exit reached at " + t.attraction; return true; } return false; }
                    if (AllDone(leg, prog, ReachExit)) { why = "every exit reached"; return true; }
                    return false;
                }
                case DefeatBoss:
                {
                    var t = TargetStop(leg);
                    if (t == null) return false;
                    var p = Of(prog, t);
                    if (p != null && p.Hit(DefeatBoss)) { why = "boss defeated: " + t.attraction; return true; }
                    return false;
                }
                case Score:
                case Collect:
                    if (ob.count > 0 && LegSum(leg, prog) >= ob.count) { why = ob.type + " " + ob.count + " reached"; return true; }
                    return false;
                case Survive:
                    if (ob.seconds > 0 && legElapsed >= ob.seconds) { why = "survived " + ob.seconds + " s"; return true; }
                    foreach (var s in leg.stops) { var p = Of(prog, s); if (p != null && p.kinds.Contains(Survive)) { why = "survived"; return true; } }
                    return false;
                case Time:
                    if (ob.seconds > 0 && legElapsed >= ob.seconds) { why = "time " + ob.seconds + " s"; return true; }
                    return false;
            }
            return false;
        }

        /// When the leg's `survive` / `time` clock started: the moment its title signalled `started` (its own 3-2-1
        /// ended; the earliest stop that did), else the leg start (row.t0). A game that starts late (an idle start
        /// after 25 s) still gets its full seconds — on the HUD countdown and on the leg's own clock (§ 7.3).
        public static double LegClockStart(PlaylistRunLeg leg, PlaylistRunRow row, IReadOnlyDictionary<string, PlaylistStopProgress> prog)
        {
            double t = 0;
            foreach (var s in leg?.stops ?? new List<PlaylistRunStop>())
            {
                var p = Of(prog, s);
                if (p != null && p.started && p.ts > 0 && (t <= 0 || p.ts < t)) t = p.ts;
            }
            return t > 0 ? t : row?.t0 ?? 0;
        }

        /// Seconds on the leg's `survive` / `time` clock (0 unless playing), from <see cref="LegClockStart"/>.
        public static double LegElapsed(PlaylistRunLeg leg, PlaylistRunRow row, IReadOnlyDictionary<string, PlaylistStopProgress> prog, double now)
            => row != null && row.phase == Playing ? Math.Max(0, now - LegClockStart(leg, row, prog)) : 0;

        /// (progress, goal) of the current leg's objective, for the HUD / phone / Dreamie.
        public static void Progress(PlaylistRunLeg leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog,
            double legElapsed, out double progress, out double goal)
        {
            progress = 0; goal = 1;
            if (leg == null) return;
            var ob = leg.objective ?? new PlaylistRunObjective();
            switch (ob.type)
            {
                case Score:
                case Collect:
                    if (ob.count > 0) { progress = LegSum(leg, prog); goal = ob.count; return; }
                    break;
                case Survive:
                case Time:
                    if (ob.seconds > 0) { progress = Math.Min(ob.seconds, legElapsed); goal = ob.seconds; return; }
                    break;
                case DefeatBoss:
                {
                    var t = TargetStop(leg);
                    progress = t != null && (Of(prog, t)?.Hit(DefeatBoss) ?? false) ? 1 : 0;
                    goal = 1;
                    return;
                }
                case ReachExit:
                {
                    var t = TargetStop(leg);
                    if (t != null) { progress = (Of(prog, t)?.Hit(ReachExit) ?? false) ? 1 : 0; goal = 1; return; }
                    break;
                }
            }
            int n = 0, done = 0;
            foreach (var s in leg.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                n++;
                var p = Of(prog, s);
                if (p != null && (p.done || p.timedOut || p.kinds.Contains(ob.type))) done++;
            }
            progress = done; goal = Math.Max(1, n);
        }

        /// This leg's points (every stop's progress). Scoring ACROSS legs (`rules.scoring` sum / best /
        /// elimination) belongs to the queue's session scoreboard (§ 7.8): each leg reports its score.
        public static double RunScore(PlaylistRunPlan plan, int leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog)
            => LegSum(plan?.Current, prog);

        /// Team lives left (-1 = unlimited).
        public static int LivesLeft(PlaylistRunPlan plan, PlaylistRunRow row, IReadOnlyDictionary<string, PlaylistStopProgress> prog)
        {
            int lives = plan?.rules?.lives ?? 0;
            if (lives <= 0) return -1;
            int lost = 0;   // a failed result, or a challenge whose timer ran out with no result, costs one
            if (prog != null)
                foreach (var kv in prog)
                {
                    var p = kv.Value;
                    if (p == null) continue;
                    lost += p.failures;
                    if (p.timedOut && !p.done && !TimeoutIsDone(plan, kv.Key)) lost++;   // a moment's timeout never costs one
                }
            return Math.Max(0, lives - lost);
        }

        /// The stop has no fail condition on the clock (<see cref="PlaylistChallenge.TimeoutIsDone"/>).
        public static bool TimeoutIsDone(PlaylistRunPlan plan, string occurrenceId)
        {
            if (plan == null) return false;
            var s = plan.FindStop(occurrenceId, out int li);
            if (s == null) return false;
            var leg = plan.legs != null && li >= 0 && li < plan.legs.Count ? plan.legs[li] : plan.Current;
            return PlaylistChallenge.TimeoutIsDone(leg, s);
        }

        /// The leader's write when a stop's timer runs out: a moment (no fail condition) ends `done`; any other stop
        /// is `timed_out` (no clear credit; with `rules.lives` it costs one).
        public static void TimeOut(PlaylistRunPlan plan, string occurrenceId, PlaylistStopProgress p)
        {
            if (p == null) return;
            if (TimeoutIsDone(plan, occurrenceId)) { p.done = true; p.timedOut = false; }
            else p.timedOut = true;
        }

        /// Per-stop challenge timer in seconds (0 = none). Bosses, minibosses and bookends are never cut by a timer.
        /// Order: the stop's authored `seconds` → the title's own limit (`dp.playlist.time_limit`, e.g. SAL's stage
        /// timer) → a `time` leg's authored `objective.seconds` (the card's "30 seconds — go!"; a gauntlet of several
        /// stops caps each at `rules.timerSec`) → `rules.timerSec` → the kind's default (<see cref="DefaultStopSeconds"/>:
        /// nobody gets stuck on the 7th coin).
        public static double StopTimer(PlaylistRunPlan plan, PlaylistRunStop s, PlaylistStopProgress p = null)
        {
            if (s == null || IsUntimed(s)) return 0;
            if (s.seconds > 0) return s.seconds;
            if (p != null && p.limit > 0) return p.limit;
            var tob = plan?.Current?.objective;
            if (tob != null && tob.type == Time && tob.seconds > 0)   // not the 60 s moment default
                return (plan.rules?.timerSec ?? 0) > 0 ? Math.Min(tob.seconds, plan.rules.timerSec) : tob.seconds;
            string kind = PlaylistChallenge.KindOf(plan?.Current, s);
            if (kind == PlaylistChallenge.Boss) return 0;
            if ((plan?.rules?.timerSec ?? 0) > 0 && kind != PlaylistChallenge.Moment) return plan.rules.timerSec;
            return PlaylistChallenge.DefaultSeconds(kind, plan?.Current);
        }

        public static bool IsUntimed(PlaylistRunStop s) => s == null || s.bookend || s.role == "boss" || s.role == "miniboss"
            || s.challenge == PlaylistChallenge.Boss;

        /// The title reported a stop's own progress target (`dp.playlist.progress(n, of, unit)`).
        public static bool ApplyProgress(PlaylistStopProgress p, double n, double goal, string unit)
        {
            if (p == null) return false;
            bool changed = false;
            if (n > p.n) { p.n = n; changed = true; }
            if (goal > 0 && Math.Abs(goal - p.goal) > 1e-6) { p.goal = goal; changed = true; }
            if (!string.IsNullOrEmpty(unit) && unit != p.unit) { p.unit = unit; changed = true; }
            return changed;
        }

        /// A streamed stop's turn has come: the stop it follows is over (done or timed out).
        public static bool StreamDue(PlaylistRunStop s, IReadOnlyDictionary<string, PlaylistStopProgress> prog, PlaylistRunPlan plan = null)
        {
            if (s != null && !s.standing && s.streamAfter == PlaylistSets.AddNow) return true;   // run.add into the standing set
            if (s == null || s.standing || string.IsNullOrEmpty(s.streamAfter)) return false;
            // Whole-arc playlists (§ 7.10) stream across rounds: the stop it follows may be an EARLIER round's, whose
            // progress lives in that round's table (each round = its own runner / row). An earlier round is over.
            if (plan != null && plan.FindStop(s.streamAfter, out int afterLeg) != null && afterLeg >= 0 && afterLeg < plan.CurrentPosition) return true;
            return prog != null && prog.TryGetValue(s.streamAfter, out var p) && StopOver(p);
        }

        /// A stop's own part is over: beaten (complete / its exit / its boss), its challenge reported, or timed out.
        public static bool StopOver(PlaylistStopProgress p) =>
            p != null && (p.done || p.timedOut || p.kinds.Contains(ReachExit) || p.kinds.Contains(DefeatBoss));

        /// The longest stop timer of a leg (the leg's clock under on_timer), 0 = none.
        public static double LegTimer(PlaylistRunPlan plan, PlaylistRunLeg leg)
        {
            double t = 0;
            foreach (var s in leg?.stops ?? new List<PlaylistRunStop>()) t = Math.Max(t, StopTimer(plan, s));
            return t;
        }

        /// Stops of the current leg whose challenge timer ran out (the leader marks them timedOut).
        public static List<string> ExpiredStops(PlaylistRunPlan plan, PlaylistRunRow row,
            IReadOnlyDictionary<string, PlaylistStopProgress> prog, double now, Func<string, bool> goalDriven = null)
        {
            var list = new List<string>();
            if (plan == null || row == null || row.phase != Playing) return list;
            var leg = plan.Current;
            if (leg?.stops == null) return list;
            foreach (var s in leg.stops)
            {
                var p = Of(prog, s);
                double t = StopTimer(plan, s, p);
                if (s == null || t <= 0) continue;
                if (p == null || p.ts <= 0 || StopOver(p)) continue;   // not started yet, or over
                // A stop a goal block drives keeps the block's own timers (its safety net, CONTRACT § 2).
                if (goalDriven != null && goalDriven(s.occurrenceId ?? "")) continue;
                if (now - p.ts >= t) list.Add(s.occurrenceId);
            }
            return list;
        }

        /// THE state machine (leader only). Returns the next row, or null when nothing changes.
        public static PlaylistRunRow Step(PlaylistRunPlan plan, PlaylistRunRow row,
            IReadOnlyDictionary<string, PlaylistStopProgress> prog, PlaylistRunInputs input, out string why)
        {
            why = null;
            if (plan == null || plan.LegCount == 0) return null;
            row = row ?? new PlaylistRunRow { tp = input.now };
            if (row.phase == Complete) return null;
            var r = plan.rules ?? new PlaylistRunPolicy();
            var next = row.Clone();
            double now = input.now;
            // run.finish (§ 3.3) ends the run wherever it is: the end screens follow `complete`.
            if (input.finishResult != null && row.phase != Ready)
                return Go(next, Complete, now, input.finishResult.Length > 0 ? input.finishResult : "won", ref why, "run.finish");
            if (row.phase == LegDone) return null;

            // Lives: out of lives ends the playlist wherever it is.
            if (row.phase != Ready && LivesLeft(plan, row, prog) == 0)
                return Go(next, Complete, now, "out", ref why, "out of lives");

            switch (row.phase)
            {
                case Ready:
                    if (!input.legStanding) return null;
                    if (!r.autoStart && !input.beginRequested) return null;
                    next.leg = Math.Max(0, plan.legIndex);
                    next.t0 = now;
                    return Go(next, Playing, now, "", ref why, "start: " + (plan.Current?.name ?? "leg"));

                case Playing:
                {
                    var leg = plan.Current;
                    double el = LegElapsed(leg, row, prog, now);   // from the title's `started`, else the leg start
                    if (input.skipRequested) return Go(next, Cleared, now, "skip", ref why, "skipped");
                    // Sets mode: the playlist's Lua decides what is out next (run.show → the set changes); a set never clears
                    // itself — every challenge stands, replayable, until the Lua puts out the next set or finishes.
                    if (plan.sets) return null;
                    if ((r.advance ?? AdvanceComplete) == AdvanceTimer)
                    {
                        // The mini-game gauntlet: every stop's challenge is over (its result, or its own timer).
                        if (AllDone(leg, prog, null)) return Go(next, Cleared, now, "timer", ref why, "every challenge over");
                        return null;
                    }
                    if (LegComplete(leg, prog, el, out string w)) return Go(next, Cleared, now, "objective", ref why, w);
                    return null;
                }

                case Cleared:
                    if (now - row.tp < ClearHoldSeconds) return null;
                    if (plan.IsLastLeg)
                        return Go(next, Complete, now, "won", ref why, "adventure complete");
                    // The queue advances (the phone packs the next leg's arrangement).
                    return Go(next, LegDone, now, row.result, ref why, "leg complete → queue advances");
            }
            return null;
        }

        /// The stop players should head to next: the first stop of the leg (authored order) that is not beaten
        /// (-1 = all are). Guidance for Dreamie / the HUD; every stop stands and counts at any time.
        public static int CurrentStop(PlaylistRunLeg leg, IReadOnlyDictionary<string, PlaylistStopProgress> prog)
        {
            if (leg?.stops == null) return -1;
            for (int i = 0; i < leg.stops.Count; i++)
            {
                var p = Of(prog, leg.stops[i]);
                if (!StopOver(p)) return i;
            }
            return -1;
        }

        static PlaylistRunRow Go(PlaylistRunRow next, string phase, double now, string result, ref string why, string reason)
        {
            next.phase = phase;
            next.tp = now;
            next.result = result ?? "";
            next.rev++;
            why = reason;
            return next;
        }

        internal static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ── Story flags + badges this run records (§ 15.3a) ─────────────────────────────────────────────
        // PLAYLIST_STATE.sets {flags {name: true|"value"|n}, badges [ids]}: what the phone's `complete {result.sets}`
        // records on every present player's playlist_progress. The playlist's AUTHORED sets join only on a WON run
        // (the last leg complete, not out); flags the title sets at runtime (dp.playlist.set_flag) ride every
        // leg's complete (the title decided them: the chosen starter stays chosen). A runtime flag beats an authored one.

        public const int MaxFlagName = 64, MaxFlagText = 64, MaxFlags = 32;

        /// Flag names: 1–64 of A-Z a-z 0-9 . _ : - (e.g. "al.dungeon_lord", "cr.partner").
        public static bool ValidFlag(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxFlagName) return false;
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '.' && c != '_' && c != ':' && c != '-') return false;
            return true;
        }

        /// A flag value as canonical JSON (true / false / a number / a short string); null when not a flag value.
        public static string FlagValue(JSONObject v)
        {
            if (v == null) return "true";
            switch (v.type)
            {
                case JSONObject.Type.Bool: return v.boolValue ? "true" : "false";
                case JSONObject.Type.Number: return Num(v.doubleValue);
                case JSONObject.Type.String:
                    string t = v.stringValue ?? "";
                    if (t.Length > MaxFlagText) t = t.Substring(0, MaxFlagText);
                    return "\"" + Esc(t) + "\"";
                case JSONObject.Type.Null: return "true";
                default: return null;
            }
        }

        /// The authored `sets` normalized to {flags {}, badges []}; also accepts the shorthand list of flag names.
        /// Null when it sets nothing.
        public static JSONObject AuthoredSets(string setsJson)
        {
            if (string.IsNullOrEmpty(setsJson)) return null;
            JSONObject j;
            try { j = new JSONObject(setsJson); } catch (Exception) { return null; }
            var flags = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var badges = new List<string>();
            if (j.type == JSONObject.Type.Array && j.list != null)
            {
                foreach (var x in j.list) if (x != null && x.type == JSONObject.Type.String && ValidFlag(Strip(x.stringValue))) flags[Strip(x.stringValue)] = "true";
            }
            else if (j.type == JSONObject.Type.Object)
            {
                var f = j.GetField("flags");
                if (f != null && f.type == JSONObject.Type.Object && f.keys != null)
                    for (int i = 0; i < f.keys.Count; i++)
                    { string v = FlagValue(f.list[i]); if (v != null && ValidFlag(f.keys[i])) flags[f.keys[i]] = v; }
                else if (f != null && f.type == JSONObject.Type.Array && f.list != null)
                    foreach (var x in f.list) if (x != null && x.type == JSONObject.Type.String && ValidFlag(Strip(x.stringValue))) flags[Strip(x.stringValue)] = "true";
                var b = j.GetField("badges");
                if (b?.list != null) foreach (var x in b.list) if (x != null && x.type == JSONObject.Type.String && !string.IsNullOrEmpty(x.stringValue) && !badges.Contains(x.stringValue)) badges.Add(x.stringValue);
            }
            if (flags.Count == 0 && badges.Count == 0) return null;
            return SharedState.ParseFragment(SetsText(flags, badges));
        }

        static string Strip(string ref0) => ref0 != null && ref0.StartsWith("flag:", StringComparison.Ordinal) ? ref0.Substring(5) : ref0;

        static string SetsText(IDictionary<string, string> flags, IList<string> badges)
        {
            var sb = new StringBuilder("{\"flags\":{");
            bool first = true;
            foreach (var kv in flags) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(Esc(kv.Key)).Append("\":").Append(kv.Value); }
            sb.Append("},\"badges\":[");
            for (int i = 0; i < badges.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(badges[i])).Append('"'); }
            return sb.Append("]}").ToString();
        }

        /// The run is WON: its last leg completed and not out of lives.
        public static bool Won(PlaylistRunPlan plan, PlaylistRunRow row) =>
            row != null && row.phase == Complete && row.result != "out";

        /// PLAYLIST_STATE.sets: runtime flags (every leg) + the authored sets on a WON run; null when nothing.
        public static string SetsJson(PlaylistRunPlan plan, PlaylistRunRow row, IReadOnlyDictionary<string, string> flags)
        {
            var f = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var badges = new List<string>();
            if (Won(plan, row))
            {
                var a = AuthoredSets(plan?.setsJson);
                var af = a?.GetField("flags");
                if (af?.keys != null) for (int i = 0; i < af.keys.Count; i++) f[af.keys[i]] = FlagValue(af.list[i]) ?? "true";
                var ab = a?.GetField("badges");
                if (ab?.list != null) foreach (var x in ab.list) if (x != null && x.type == JSONObject.Type.String) badges.Add(x.stringValue);
            }
            if (flags != null) foreach (var kv in flags) if (ValidFlag(kv.Key) && !string.IsNullOrEmpty(kv.Value)) f[kv.Key] = kv.Value;
            return f.Count == 0 && badges.Count == 0 ? null : SetsText(f, badges);
        }

        internal static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// Seconds left on a live, timed stop (-1 = untimed, not started, or over).
        public static double StopLeft(PlaylistRunPlan plan, PlaylistRunStop s, PlaylistStopProgress p, double now)
        {
            double tl = StopTimer(plan, s, p);
            if (tl <= 0 || p == null || p.ts <= 0 || StopOver(p)) return -1;
            return Math.Max(0, tl - (now - p.ts));
        }

        /// A boss stop live and unbeaten for <see cref="BossHintSeconds"/>.
        public static bool BossNeedsHint(PlaylistRunStop s, PlaylistStopProgress p, double now) =>
            NeedsHint(null, s, p, now);

        /// A stop unbeaten past its kind's hint time (boss 180 s, solve 120 s; catalog § 12).
        public static bool NeedsHint(PlaylistRunLeg leg, PlaylistRunStop s, PlaylistStopProgress p, double now)
        {
            if (s == null || p == null || p.ts <= 0 || StopOver(p)) return false;
            double h = PlaylistChallenge.HintSeconds(PlaylistChallenge.KindOf(leg, s));
            return h > 0 && now - p.ts >= h;
        }

        /// What players are told right now: the current stop's goal line (else the leg's), its wrist chip, a hint.
        public static void Tell(PlaylistRunPlan plan, PlaylistRunRow row, IReadOnlyDictionary<string, PlaylistStopProgress> prog,
            double now, out string line, out string chip, out string hint)
        {
            line = chip = hint = "";
            var leg = plan?.Current;
            if (leg == null) return;
            double el = LegElapsed(leg, row, prog, now);
            Progress(leg, prog, el, out double pr, out double goal);
            int cur = CurrentStop(leg, prog);
            var s = cur >= 0 ? leg.stops[cur] : null;
            var p = Of(prog, s);
            line = s != null ? PlaylistObjectiveText.StopGoal(leg, s, p) : PlaylistObjectiveText.LegGoal(leg);
            chip = s != null ? PlaylistObjectiveText.Chip(leg, s, p, StopLeft(plan, s, p, now), pr, goal) : "";
            foreach (var b in leg.stops)
                if (NeedsHint(leg, b, Of(prog, b), now)) { hint = PlaylistObjectiveText.Hint(leg, b); break; }
        }

        /// PLAYLIST_STATE body (§ 7.6). <paramref name="scoreboardJson"/> = the run's per-player board (a JSON array) or null.
        public static string StateJson(PlaylistRunPlan plan, PlaylistRunRow row,
            IReadOnlyDictionary<string, PlaylistStopProgress> prog, double now, string scoreboardJson = null, int players = 0,
            IReadOnlyDictionary<string, string> flags = null, bool advanceReady = true, string nextPlaylist = null, string nextState = null, string waypointsJson = null, string brokenJson = null,
            Func<string, (string state, int inside, bool retrying)?> stopStatus = null, Func<string, bool> stopLocked = null)
        {
            row = row ?? new PlaylistRunRow();
            var leg = plan?.Current;
            var ob = leg?.objective ?? new PlaylistRunObjective();
            double el = LegElapsed(leg, row, prog, now);   // the HUD countdown runs from the title's `started`, else the leg start
            Progress(leg, prog, el, out double pr, out double goal);
            int cur = CurrentStop(leg, prog);
            var curStop = cur >= 0 && leg != null ? leg.stops[cur] : null;
            double left = ob.seconds > 0 && (ob.type == Survive || ob.type == Time) ? Math.Max(0, ob.seconds - el)
                        : curStop != null ? StopLeft(plan, curStop, Of(prog, curStop), now) : -1;
            Tell(plan, row, prog, now, out string line, out string chip, out string hint);
            var sb = new StringBuilder("{");
            void S(string k, string v, bool comma = true) { if (comma) sb.Append(','); sb.Append('"').Append(k).Append("\":\"").Append(Esc(v)).Append('"'); }
            void N(string k, double v) { sb.Append(",\"").Append(k).Append("\":").Append(Num(v)); }
            S("playlistId", plan?.playlistId, false);
            S("contentId", plan?.contentId);
            S("instanceId", plan?.instanceId);
            S("runId", plan?.runId);
            S("owner", plan?.owner ?? "title");
            S("name", plan?.name);
            S("kindWord", plan?.kindWord ?? "Adventure");
            S("phase", row.phase);
            S("result", row.result);
            // Sets mode: the phone's queue item is the WHOLE playlist (legIndex 0, one item) — the state names that item (leg 1
            // of 1, no legId: the phone matches by legIndex) so its final `complete` finds it; the set rides setId / set.
            bool setsMode = plan?.sets ?? false;
            N("leg", setsMode ? 1 : (plan?.legIndex ?? row.leg) + 1);
            N("legs", setsMode ? 1 : plan?.LegCount ?? 0);
            S("legId", setsMode ? "" : leg?.id);
            if (setsMode) { S("setId", leg?.id); N("set", (plan.legIndex) + 1); }
            S("legName", leg?.name);
            N("currentStop", cur + 1);
            S("objectiveText", line);
            S("legGoal", PlaylistObjectiveText.LegGoal(leg));
            // The DreamBand / progress chip leads with the round in the playlist's OWN word ("Race 2 of 5 · Coins 3/7") —
            // only when the playlist names one (no generic "round" language for titles that did not ask for it).
            string roundLabel = RoundLabel(plan);
            bool authoredWord = plan?.roundWord != null && !plan.roundWord.IsDefault;
            if (roundLabel.Length > 0 && authoredWord) chip = string.IsNullOrEmpty(chip) ? roundLabel : roundLabel + " · " + chip;
            S("chip", chip);
            if (roundLabel.Length > 0) S("roundLabel", roundLabel);   // only an authored word (never "Round 2 of 5")
            var rwd = RoundWord(plan);
            sb.Append(",\"roundWord\":{\"one\":\"").Append(Esc(rwd.one)).Append("\",\"many\":\"").Append(Esc(rwd.many)).Append("\"}");
            S("hint", hint);
            S("beat", leg?.beat);
            S("next", leg?.next);
            sb.Append(",\"objective\":{");
            S("type", ob.type, false); S("target", ob.target); N("count", ob.count); N("seconds", ob.seconds);
            N("progress", pr); N("goal", goal); N("left", left);
            sb.Append('}');
            N("score", RunScore(plan, row.leg, prog));
            N("lives", LivesLeft(plan, row, prog));
            if (players > 0) N("players", players);
            sb.Append(",\"stops\":[");
            bool first = true;
            foreach (var s in leg?.stops ?? new List<PlaylistRunStop>())
            {
                if (s == null) continue;
                var p = Of(prog, s);
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                S("occurrenceId", s.occurrenceId, false); S("attraction", s.attraction); S("name", PlaylistObjectiveText.Name(s));
                S("role", s.role); S("roomLabel", s.roomLabel);
                S("goalText", PlaylistObjectiveText.StopGoal(leg, s, p));
                S("attractionId", s.attractionId ?? "");
                S("challenge", PlaylistChallenge.KindOf(leg, s));
                sb.Append(",\"bonus\":[");
                for (int bi = 0; bi < (s.bonus?.Count ?? 0); bi++) { if (bi > 0) sb.Append(','); sb.Append('"').Append(Esc(s.bonus[bi])).Append('"'); }
                sb.Append("],\"bonusHit\":[");
                bool bf = true;
                foreach (var k in p?.kinds ?? new SortedSet<string>()) if (IsBonus(k)) { if (!bf) sb.Append(','); bf = false; sb.Append('"').Append(k).Append('"'); }
                sb.Append(']');
                // done = really beaten (a timed-out stop is `result: timed_out`, not done); failures separate.
                sb.Append(",\"done\":").Append(p != null && p.done ? "true" : "false");
                sb.Append(",\"over\":").Append(StopOver(p) ? "true" : "false");
                N("failures", p?.failures ?? 0);
                double tl0 = StopTimer(plan, s, p);
                double exp = tl0 > 0 ? tl0 : s.expectedSec;
                if (exp > 0) N("expectedMs", exp * 1000.0);
                if (p != null && p.firstAt > 0 && p.ts > 0) N("firstProgressMs", Math.Max(0, (p.firstAt - p.ts) * 1000.0));
                N("n", p?.n ?? 0);
                N("of", p?.goal ?? 0);
                S("unit", p?.unit ?? "");
                // the phone's per-stop state (ios-sets): state won | skipped | open (a broken one is skipped, and listed in
                // `broken`), players inside now, retrying while a reset after a loss runs. `result` keeps done / timed_out /
                // failed (the phone's play-insights stop_result), except a skipped stop says "skipped" (+ skipped: true).
                (string state, int inside, bool retrying)? ss = null;
                try { ss = stopStatus?.Invoke(s.occurrenceId); } catch (Exception) { }
                string state = ss?.state ?? (p != null && p.done && !p.resultFailed && !p.timedOut ? "won" : "open");
                if (state == "open" && p != null && p.done && !p.resultFailed && !p.timedOut) state = "won";   // a native stop beaten
                S("result", state == "skipped" ? "skipped" : p?.Result ?? "");
                S("state", state);
                if (state == "skipped") sb.Append(",\"skipped\":true");
                N("inside", ss?.inside ?? 0);
                sb.Append(",\"retrying\":").Append(ss?.retrying == true ? "true" : "false");
                // locked (run.lock / the set's lock / a native lockedUntil) while it is open: the phone draws a lock on it
                bool locked = false;
                if (state == "open") { try { locked = stopLocked?.Invoke(s.occurrenceId) ?? false; } catch (Exception) { } }
                if (locked) sb.Append(",\"locked\":true");
                sb.Append(",\"streamed\":").Append(!s.standing && !string.IsNullOrEmpty(s.streamAfter) ? "true" : "false");
                double sl = StopLeft(plan, s, p, now);
                if (sl >= 0) N("left", sl);
                sb.Append('}');
            }
            sb.Append(']');
            if (!string.IsNullOrEmpty(scoreboardJson)) sb.Append(",\"scoreboard\":").Append(scoreboardJson);
            string sets = SetsJson(plan, row, flags);
            if (sets != null) sb.Append(",\"sets\":").Append(sets);
            N("rev", row.rev);
            // The phone's cue to advance the queue waits for the run's readiness gate (PlaylistRun: every device has the
            // next round downloaded + preloaded, or 20 s grace) — the whole group moves on together.
            // Sets mode: the run's leader packs every set itself, the queue item is the whole playlist — only `complete` advances it.
            sb.Append(",\"legComplete\":").Append((row.phase == LegDone && advanceReady && !(plan?.sets ?? false)) || row.phase == Complete ? "true" : "false");
            if (plan != null && plan.sets) sb.Append(",\"setsMode\":true");
            sb.Append(",\"complete\":").Append(row.phase == Complete ? "true" : "false");
            // run.finish{next} (pl-sets): the playlist to start after this one, and what became of it on the device:
            // "" (deciding) | started (the device started it: no phone queue) | phone (the phone's queue should load it) |
            // locked (requires.badges: nobody here holds them — do NOT start it) | unknown (no such playlist here).
            if (!string.IsNullOrEmpty(nextPlaylist)) { S("nextPlaylist", nextPlaylist); S("nextState", nextState ?? ""); }
            // § 6.7 waypoints (pl-sets): the ones the playlist made count — [{ref, attraction, occurrence, done}]; absent = none.
            if (!string.IsNullOrEmpty(waypointsJson)) sb.Append(",\"waypoints\":").Append(waypointsJson);
            // a challenge that could not be won (challenge_broken → skipped): [{occurrence, attraction, why}] — a content bug.
            if (!string.IsNullOrEmpty(brokenJson)) sb.Append(",\"broken\":").Append(brokenJson);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
