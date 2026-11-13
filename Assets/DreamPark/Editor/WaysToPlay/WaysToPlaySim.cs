// ─────────────────────────────────────────────────────────────────────
//  WaysToPlaySim.cs — the Park Simulator's "Ways to play" engine.
//
//  Runs a title's way to play on the REAL director runtime
//  (Assets/DreamPark/Scripts/Features/Runners: RunnerDirector + RunnerLua,
//  runtime agent) with N simulated headsets, in the editor, without a Quest:
//  each fake headset has its own SessionEngine + the real SharedState over a
//  simulated relay, its own RunnerWorld and its own XLua VM — the same set-up
//  as Tools/runner-offline. So the director runs on the elected LEADER only,
//  every headset renders the board from the shared table, a late joiner
//  rebuilds from it, and when the leader leaves the next one resumes
//  (onResume) — exactly what the headsets do.
//
//  What the simulator adds: fake players that wander between tiles and walk
//  into vote zones, a level "finishing" (auto after N seconds, or a button:
//  dp.level.complete via RunnerDirector.ReportComplete), the session clock,
//  and one OBSERVER headset whose render hooks (loads, edges, Next up, vote)
//  the panel and the scene stage draw.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DreamPark;
using XLua;

namespace DreamPark.WaysToPlay
{
    public sealed class SimHeadset : ISessionLink
    {
        public WaysToPlaySim sim;
        public string id;
        public bool gone;
        public SessionEngine engine;
        public LuaEnv lua;
        public RunnerWorld world;
        public RunnerDirector dir;
        public float x, z;                           // the player, board frame (metres; +X right, +Z into the park)
        public double nextMove;
        public double startDirectorAt = -1;          // a late joiner joins the session first, then starts its title
        public readonly Dictionary<string, (string look, bool locked)> edges = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);

        public bool Present => true;
        public bool Connected => !gone;
        public int PingMs => 5;
        public void Publish(string type, string payloadJson) { if (!gone) sim.Enqueue(this, type, payloadJson); }
        public int Tile => sim.Geometry.TileAt(x, z);
    }

    public sealed class WaysToPlaySim
    {
        // ── settings ───────────────────────────────────────────────
        public int tiles = 1;
        public float minutes = 10f;
        public float autoFinishSeconds = 25f;        // 0 = levels only finish from the panel
        public int seed = 1;
        public string contentId = "Title";
        public RunnerRecord runner;                  // null = the built-in default
        public List<RunnerLevel> levels = new List<RunnerLevel>();
        public string locationId = "loc_sim";
        /// The play session's pile-in before the shared clock starts (dp.session.onClockStart), seconds. 0 = none.
        public float pileInSeconds = 30f;

        // ── state ──────────────────────────────────────────────────
        public double t = 1000;
        public double timeLeft;
        public bool running, ended;
        public RunnerBoardGeometry Geometry { get; private set; }
        public readonly List<SimHeadset> headsets = new List<SimHeadset>();
        public readonly List<string> log = new List<string>();
        public int leaderChanges, lateJoins;
        public int Players => headsets.Count(h => !h.gone);
        public float WidthFt => tiles >= 2 ? 24f : 12f;
        public float DepthFt => tiles >= 4 ? 36f : 18f;

        readonly List<(double at, SimHeadset from, string type, string json)> queue = new List<(double, SimHeadset, string, string)>();
        readonly Dictionary<int, double> playedSince = new Dictionary<int, double>();   // load id → seconds played
        readonly HashSet<int> reported = new HashSet<int>();
        System.Random rng;
        int seq;
        double startT;
        const double Latency = 0.03, Step = 0.05;

        public IEnumerable<SimHeadset> Live => headsets.Where(h => !h.gone);
        public SimHeadset Leader => Live.FirstOrDefault(h => h.dir != null && h.dir.Directing);
        /// The headset the panel and the scene draw (any live one: they all render the same board).
        public SimHeadset Observer => Live.FirstOrDefault(h => h.dir != null);
        public string LeaderId => Leader?.id ?? "—";
        public double Elapsed => t - startT;

        // ── lifecycle ──────────────────────────────────────────────
        public bool Start(int playerCount)
        {
            Stop();
            rng = new System.Random(seed);
            t = 1000; startT = t; timeLeft = minutes * 60f; ended = false; leaderChanges = 0; lateJoins = 0; seq = 0;
            log.Clear(); queue.Clear(); playedSince.Clear(); reported.Clear();
            Geometry = RunnerBoardGeometry.FromFeet(WidthFt, DepthFt);
            for (int i = 0; i < Math.Max(1, playerCount); i++) AddHeadset(false);
            Note($"session · {Geometry.Count} tile(s) {WidthFt:0}×{DepthFt:0} ft · {Players} headsets · {minutes:0} min · way to play {(runner != null ? runner.ToString() : "built-in default")}");
            Run(1.0);                                 // let the session elect a leader
            foreach (var h in headsets) StartDirector(h);
            running = true;
            Run(3.0, () => Live.All(h => h.dir.Synced) && Leader != null);
            return Leader != null;
        }

        public void Stop()
        {
            running = false;
            foreach (var h in headsets) DisposeHeadset(h);
            headsets.Clear();
            DreamParkLobby.TotalSeconds = () => 0;
            DreamParkLobby.LeftSeconds = () => 0;
        }

        SimHeadset AddHeadset(bool late)
        {
            int n = seq++;
            var h = new SimHeadset { sim = this, id = "hs" + (char)('a' + n % 26) + (n >= 26 ? (n / 26).ToString(CultureInfo.InvariantCulture) : "") };
            h.engine = new SessionEngine(h.id, h, () => t, () => false);
            h.lua = NewLua(h);
            h.world = new RunnerWorld
            {
                Log = m => OnDeviceLog(h, m),
                Clock = () => h.engine.Now,
                DeviceId = () => h.id,
                OpenState = name => SharedState.CreateDetached(h.engine, "g:session", name),
                Lua = () => h.lua,
                LocalPos = d => (true, h.x, h.z),
                Edge = (d, tile, side, look, locked) => h.edges[tile + ":" + side] = (look, locked),
                SessionTimeLeft = () => Math.Max(0, timeLeft),
                SessionMinutes = () => minutes,
                SessionStartsIn = () => pileInSeconds <= 0 ? -1 : Math.Max(0, startT + pileInSeconds - t),
                SessionWaitTotal = () => Math.Max(0, pileInSeconds),
                PlayerName = id => id,
            };
            // gameplay Lua's dp.lobby reads the same pile-in
            DreamParkLobby.TotalSeconds = () => Math.Max(0, pileInSeconds);
            DreamParkLobby.LeftSeconds = () => pileInSeconds <= 0 ? 0 : Math.Max(0, startT + pileInSeconds - t);
            StandIn(h, 0);
            h.nextMove = t + 3 + rng.NextDouble() * 5;
            headsets.Add(h);
            if (late) Note($"{h.id} joins late — it rebuilds the board from the shared table");
            return h;
        }

        void StartDirector(SimHeadset h)
        {
            h.dir = new RunnerDirector(h.world, locationId, contentId, runner, levels, Geometry, "ps_sim");
        }

        void DisposeHeadset(SimHeadset h)
        {
            try { h.dir?.Dispose(); } catch { }
            h.dir = null;
            // XLua refuses to dispose a VM while Lua→C# delegates are alive; drop it either way.
            try { h.lua?.Dispose(); } catch { }
            h.lua = null;
        }

        static LuaEnv NewLua(SimHeadset h)
        {
            var env = new LuaEnv();
            env.Global.Set("__cprint", new Action<string>(s => h.sim.Note($"{h.id} lua: {s}")));
            env.DoString("print = function(...) local t={} for i=1,select('#',...) do t[i]=tostring((select(i,...))) end __cprint(table.concat(t,'\\t')) end");
            // The shipping app's sandbox (LuaSecuritySandbox): no load / loadstring / dofile / loadfile / debug.
            env.DoString("load = nil; loadstring = nil; dofile = nil; loadfile = nil; debug = nil");
            return env;
        }

        // ── the panel's buttons ────────────────────────────────────
        public SimHeadset AddPlayer()
        {
            if (!running) return null;
            lateJoins++;
            var h = AddHeadset(true);
            h.startDirectorAt = t + 1.0;          // like a headset: the session syncs, then the title (and its director) starts
            return h;
        }

        /// The leader takes the headset off and leaves mid-level: its director is gone; the session elects the next
        /// headset, which starts a FRESH director and resumes from the shared table (onResume).
        public void LeaderLeaves()
        {
            var lead = Leader;
            if (lead == null) return;
            leaderChanges++;
            Note($"{lead.id} (leader) leaves mid-level — the next headset becomes leader and resumes");
            lead.gone = true;
            DisposeHeadset(lead);
        }

        /// Gameplay says the level in this tile is done (dp.level.complete), from a follower when there is one.
        /// zone null = the main board's level there.
        public bool FinishTile(int tile1, string result = "won", string zone = null)
        {
            var obs = Observer;
            if (obs == null) return false;
            RunnerLoad load = obs.dir.Loads.FirstOrDefault(l => l.Covers(tile1 - 1) && !l.done && RunnerBoardRules.SameZone(l.zone, zone));
            if (load == null) return false;
            return FinishLoad(load, result);
        }

        public bool FinishLoad(RunnerLoad load, string result = "won")
        {
            var obs = Observer;
            if (obs == null || load == null) return false;
            SimHeadset reporter = Live.FirstOrDefault(h => h.dir != null && !h.dir.Directing) ?? obs;
            var scores = Live.Where(h => load.Covers(h.Tile)).Select(h => $"\"{h.id}\":{rng.Next(100, 999)}");
            reporter.dir.ReportComplete(load.id, result, "{" + string.Join(",", scores) + "}");
            reported.Add(load.id);
            Note($"{reporter.id}: dp.level.complete{{result='{result}'}} for {load.level} (tile {string.Join("+", load.tiles.Select(x => x + 1))})");
            return true;
        }

        /// Everybody walks into the zone of option `index` of the open vote.
        public void EveryoneVote(int index)
        {
            var v = Observer?.dir.VoteNow();
            if (v == null || index < 0 || index >= v.zones.Count) return;
            foreach (var h in Live) GoToZone(h, v.zones[index]);
            Note($"everyone walks to vote zone {index + 1} ({v.options.ElementAtOrDefault(index)})");
        }

        // ── tick ───────────────────────────────────────────────────
        public void Tick(double dt) { if (running && !ended) Run(dt); }

        public void Run(double seconds, Func<bool> until = null)
        {
            double end = t + seconds;
            while (t < end - 1e-9)
            {
                t += Step;
                if (running) timeLeft = Math.Max(0, timeLeft - Step);
                for (int i = 0; i < queue.Count; i++)
                {
                    var m = queue[i];
                    if (m.at > t) continue;
                    foreach (var h in headsets) if (h != m.from && !h.gone) h.engine.HandleGlobal(m.type, m.json);
                }
                queue.RemoveAll(m => m.at <= t);
                foreach (var h in headsets) if (!h.gone) h.engine.Tick();
                foreach (var h in headsets) if (!h.gone && h.dir == null && h.startDirectorAt > 0 && t >= h.startDirectorAt) { h.startDirectorAt = -1; StartDirector(h); }
                foreach (var h in headsets) if (!h.gone) { try { h.dir?.Tick(); } catch (Exception e) { Note($"{h.id} tick threw: {e.Message}"); } }
                if (running) { MovePlayers(); AutoFinish(); }
                if (running && timeLeft <= 0 && !ended)
                {
                    ended = true;
                    Note("time is up — final results → Thanks for playing (the play session's, not the director's)");
                    return;
                }
                if (until != null && until()) return;
            }
        }

        internal void Enqueue(SimHeadset from, string type, string payload) =>
            queue.Add((t + Latency, from, type, "{\"type\":\"" + type + "\",\"payload\":" + payload + "}"));

        void MovePlayers()
        {
            var obs = Observer;
            if (obs == null) return;
            List<RunnerLoad> loads = obs.dir.Loads;
            RunnerVote vote = obs.dir.VoteNow();
            foreach (var h in Live)
            {
                if (t < h.nextMove) continue;
                h.nextMove = t + 4 + rng.NextDouble() * 6;
                if (vote != null && vote.winner < 0 && vote.zones.Count > 0 && rng.NextDouble() < 0.8)
                { GoToZone(h, vote.zones[rng.Next(vote.zones.Count)]); continue; }
                var withLevel = Enumerable.Range(0, Geometry.Count).Where(i => loads.Any(l => l.Covers(i))).ToList();
                if (withLevel.Count == 0 || rng.NextDouble() < 0.4) continue;
                StandIn(h, withLevel[rng.Next(withLevel.Count)]);
            }
        }

        void GoToZone(SimHeadset h, (int tile, float x, float z, float r, float w, float d) zone)
        {
            if (zone.tile >= 0) StandIn(h, zone.tile);
            else if (!float.IsNaN(zone.x) && zone.w > 0 && zone.d > 0) { h.x = zone.x + (float)(rng.NextDouble() - 0.5) * zone.w * 0.6f; h.z = zone.z + (float)(rng.NextDouble() - 0.5) * zone.d * 0.6f; }
            else if (!float.IsNaN(zone.x)) { h.x = zone.x + (float)(rng.NextDouble() - 0.5) * zone.r; h.z = zone.z + (float)(rng.NextDouble() - 0.5) * zone.r; }
        }

        void StandIn(SimHeadset h, int tile0)
        {
            RunnerTile tile = Geometry.Tile(tile0) ?? Geometry.Tile(0);
            h.x = tile.cx + (float)(rng.NextDouble() - 0.5) * tile.w * 0.6f;
            h.z = tile.cz + (float)(rng.NextDouble() - 0.5) * tile.d * 0.6f;
        }

        void AutoFinish()
        {
            if (autoFinishSeconds <= 0) return;
            var obs = Observer;
            if (obs == null) return;
            foreach (var l in obs.dir.Loads)
            {
                if (l.done || reported.Contains(l.id)) continue;
                bool someone = Live.Any(h => l.Covers(h.Tile) && RunnerBoardRules.SameZone(h.dir?.ZoneOf(h.id) is string z && z != "main" ? z : null, l.zone));
                playedSince.TryGetValue(l.id, out double played);
                if (someone) played += Step;
                playedSince[l.id] = played;
                if (played >= autoFinishSeconds) FinishLoad(l, "won");
            }
        }

        // ── reads for the panel / stage ────────────────────────────
        public List<RunnerLoad> Loads => Observer?.dir.Loads ?? new List<RunnerLoad>();
        /// What the observer headset (the first live one) sees: its zone's levels + the main board where its zone has none.
        public List<RunnerLoad> SeenLoads => Observer?.dir.SeenLoads(Observer.dir.Loads, Observer.dir.MyZone()) ?? new List<RunnerLoad>();
        public string ObserverZone => Observer?.dir.MyZone() ?? "main";
        public string ZoneOf(SimHeadset h) => h?.dir?.ZoneOf(h.id) ?? "main";
        public List<RunnerPending> Pending => Observer?.dir.Pending ?? new List<RunnerPending>();
        public RunnerNextUp NextUp => Observer?.dir.NextUpNow();
        public RunnerVote Vote => Observer?.dir.VoteNow();
        public string ScriptState => Observer?.dir.State?.GetJson("rs") ?? "{}";
        public string LastError => Live.Select(h => h.dir?.LastError).FirstOrDefault(e => !string.IsNullOrEmpty(e));
        public double PlayedSeconds(int loadId) => playedSince.TryGetValue(loadId, out double s) ? s : 0;

        /// The observer's look for a tile edge (tile 0-based, side "front"/"back"/"left"/"right"), the runtime's rules:
        /// outside the board = closed; inside one level's span = open; else the director's look; else closed.
        public (string look, bool locked) EdgeLook(int tile0, string side)
        {
            int nb = Geometry.Neighbour(tile0, side);
            if (nb < 0) return ("closed", false);
            var loads = Loads;
            RunnerLoad mine = loads.FirstOrDefault(l => l.Covers(tile0));
            if (mine != null && mine.Covers(nb)) return ("open", false);
            var obs = Observer;
            if (obs != null && obs.edges.TryGetValue(tile0 + ":" + side, out var e)) return e;
            return ("closed", false);
        }

        void OnDeviceLog(SimHeadset h, string m)
        {
            bool leader = h.dir != null && h.dir.Directing;
            bool important = m.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || m.Contains("threw") || m.Contains("resum");
            if (leader || important || h == Observer) Note($"{h.id}{(leader ? "★" : "")} {StripPrefix(m)}");
        }

        static string StripPrefix(string m)
        {
            int i = m.IndexOf(": ", StringComparison.Ordinal);
            return m.StartsWith("[runner]", StringComparison.Ordinal) && i > 0 ? m.Substring(i + 2) : m;
        }

        public void Note(string m)
        {
            if (log.Count > 0 && log[log.Count - 1].EndsWith(m, StringComparison.Ordinal)) return;   // two headsets saying the same thing
            log.Add($"{FormatTime(Elapsed)}  {m}");
            if (log.Count > 600) log.RemoveRange(0, log.Count - 600);
        }

        public static string FormatTime(double s) => s < 0 ? "0:00" : $"{(int)(s / 60)}:{(int)(s % 60):00}";
    }
}
