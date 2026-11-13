// ─────────────────────────────────────────────────────────────────────
//  RunnerDirector.cs — one WAY TO PLAY running on one Location's board
//  (ways-to-play contract §2 / §4, Oct 25 2026).
//
//  The game is the director, the platform is the stage crew:
//    • The director Lua runs on the session LEADER only (the writer of the
//      SharedState "dp.board/{locationId}" — SessionEngine's leader whenever it
//      holds the table). Every dp.flow call is applied by the platform into
//      that table; EVERY headset renders the board from it.
//    • The table holds the whole board, so a new leader resumes from it
//      (onResume(state)) and a late / rejoining headset rebuilds from it.
//      Script state that matters lives in dp.flow.state (synced as "rs").
//
//  Table keys:
//    b    {cid, run, sid, n, w, d}  what this board is      st   1 once onSessionStart ran
//    ld   standing loads [{i, lv, t:[tiles], dn, rs, at}]   nid  next load id
//    pq   pending swaps / transforms [{k, lv, t, nt, ns}]   rs   dp.flow.state (JSON)
//    nu   the Next-up beat {ti, su, at, un, res}            sc   this stretch's scores {player: n}
//    vt   the vote {id, tg, op, zn, en, df, w, wl}          eg   director edge looks {"t:side": {lk, lo}}
//    el   the smallest session time-left an ending hook saw (no refire after a hand-over)
//    vq   votes waiting behind the open one [{id, tg, op, zn, df, sec}] (one vote at a time)
//    lk   edge links on seams {"t:side": {n, la, lb}} (t:side = level A's side; la / lb = the two loads)
//    cs   1 once onClockStart fired (the shared clock started; no refire after a hand-over)
//    zs   zones {id: {nm, cap, lk}} (dp.zones; a load's "zn" names its zone)   zm  zone of each player {player: zone}
//    zd   zone doors {id: {z, fr, t | x, zz, r, dw}} (stand in it dw seconds → sent to zone z)
//  Cells (every device its own): at = the tile its player stands in (-1 none), vz = vote n·1000 + zone + 1 (VoteCell),
//    ps = [x, z] its player's board-frame position (metres, 0.1 m; for dp.zones.players / doors).
//  Requests (to the leader): lc {i, rs, sc} = a level reported done (dp.level.complete on any headset).
//
//  Engine-free: the world hooks (clock, SharedState, Lua env, placement, panels, local pose, session clock) are a
//  RunnerWorld, so Tools/runner-offline plays 3 fake headsets with their own tables and XLua VMs.
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
    public sealed class RunnerNextUp
    {
        public string title, subtitle, resultsJson;
        public double at, until;
        public string Key => $"{at:0.###}|{title}";
    }

    public sealed class RunnerVote
    {
        public string id, tag;
        public List<string> options = new List<string>();
        /// A zone is a tile (tile ≥ 0), a circle {x, z, r} or a rectangle {x, z, w, d} (w, d > 0; board metres, its centre at x, z).
        public List<(int tile, float x, float z, float r, float w, float d)> zones = new List<(int, float, float, float, float, float)>();
        public double endsAt, at;
        public int def, winner = -1;
        /// The winning option's id (what onChoice gets); WinnerText is what a panel shows.
        public string winnerLabel;
        /// What the panels and the phone show for each option (dp.flow.choose {id, label} / labels); "" = the id.
        public List<string> labels = new List<string>();
        /// panel = false: the title draws its own vote UI (no platform card, no floor outlines). outline = false: no outlines.
        public bool panel = true, outline = true;
        /// What the group is choosing ("Pick the next game"; dp.flow.choose{prompt}); null = the panel's own "Pick one".
        public string prompt;
        /// One line per option under its label ("Jump the pits before the floor drops"); "" = none.
        public List<string> descriptions = new List<string>();
        public string DescriptionOf(int i) => i >= 0 && i < descriptions.Count ? descriptions[i] ?? "" : "";
        public string Key => id + "|" + winner;
        public string LabelOf(int i) => i < 0 || i >= options.Count ? "" : i < labels.Count && !string.IsNullOrEmpty(labels[i]) ? labels[i] : options[i];
        public string WinnerText => winner >= 0 ? LabelOf(winner) : null;
        public List<string> DisplayLabels() { var l = new List<string>(); for (int i = 0; i < options.Count; i++) l.Add(LabelOf(i)); return l; }
        public bool HasZones => zones.Any(z => z.tile >= 0 || !float.IsNaN(z.x));
    }

    /// The hooks of one device's directors (Unity: Main, wired by RunnerHost; the offline sim: one per fake headset).
    public sealed class RunnerWorld
    {
        public static readonly RunnerWorld Main = new RunnerWorld();

        public Action<string> Log = m => Debug.Log(m);
        /// The SHARED clock (seconds; Unity: DreamParkSession.Now) — every headset reads the same deadline.
        public Func<double> Clock = () => DreamParkSession.Now;
        public Func<string> DeviceId = () => PlayerPresence.MyId ?? "";
        public Func<string, SharedState> OpenState = name => SharedState.Open(name);
        public Func<LuaEnv> Lua = () => LuaBehaviour.GetLuaEnv();
        /// This device's player in the board frame (metres; +X right, +Z into the park). ok=false = unknown.
        public Func<RunnerDirector, (bool ok, float x, float z)> LocalPos = d => (false, 0, 0);
        /// Other players this device can see (id, board-frame x/z): headsets that hold no board table (an older build, a
        /// simulated peer) still count for presence and votes. Their own "at" / "vz" cells win when they have them.
        public Func<RunnerDirector, List<(string id, float x, float z)>> RemotePositions = d => null;
        /// Render: a level is on the board now (place it in the load's frame), or left it.
        public Action<RunnerDirector, RunnerLoad, RunnerLevel> Place = (d, l, lv) => { };
        public Action<RunnerDirector, RunnerLoad> Remove = (d, l) => { };
        /// Render: a tile edge's look ("closed" | "open" | "doorway" | "gate" | ...) and lock.
        public Action<RunnerDirector, int, string, string, bool> Edge = (d, tile, side, look, locked) => { };
        /// Render: an Edge Link prefab on the seam of (tile, side) — on = spawn `link`, off = remove what stands there.
        public Action<RunnerDirector, int, string, string, bool> Link = (d, tile, side, link, on) => { };
        /// Panels: the Next-up beat (null = hide) and the vote (null = hide). seconds-left is computed from Clock.
        public Action<RunnerDirector, RunnerNextUp> NextUp = (d, n) => { };
        public Action<RunnerDirector, RunnerVote> Vote = (d, v) => { };
        /// The play session's seconds left (-1 unknown) and its length in minutes (0 unknown).
        public Func<double> SessionTimeLeft = () => -1;
        public Func<double> SessionMinutes = () => 0;
        /// Seconds until the shared session clock starts (the 30 s pile-in): > 0 counting down, 0 = started,
        /// -1 = unknown / no play session (counts as started: onClockStart fires at once).
        public Func<double> SessionStartsIn = () => -1;
        /// The wait set for this session, in seconds (dp.lobby.total; 0 = none).
        public Func<double> SessionWaitTotal = () => 0;
        public Func<string, string> PlayerName = id => id;
    }

    public sealed class RunnerDirector : IRunnerDirectorHost
    {
        public const string StatePrefix = "dp.board/";
        const double RetrySeconds = 2.0;
        const int RetryMax = 12;
        const double VoteWinnerHoldSeconds = 2.5;

        readonly RunnerWorld _w;
        public string LocationId { get; }
        public string ContentId { get; }
        public string SessionId { get; }
        public RunnerRecord Runner { get; }
        public IReadOnlyList<RunnerLevel> Levels => _levels;
        public RunnerBoardGeometry Board { get; }
        public SharedState State { get; private set; }
        public bool Disposed { get; private set; }
        readonly List<RunnerLevel> _levels;

        public bool IsLeader => State == null || State.IsLeader;
        public bool Synced => State == null || State.Synced;
        /// The director script runs here now.
        public bool Directing => _script != null && _script.Loaded;
        public string LastError { get; private set; }
        public int LuaLoads { get; private set; }
        public int Resumes { get; private set; }
        public int Starts { get; private set; }

        // leader-local
        RunnerLua.Script _script;
        static int s_gen;
        Dictionary<string, int> _presence;   // device → tile
        readonly Queue<(string kind, string json, string from)> _requests = new Queue<(string, string, string)>();
        string _lastRs;

        // render-local
        readonly Dictionary<int, RunnerLoad> _rendered = new Dictionary<int, RunnerLoad>();
        readonly Dictionary<string, (string look, bool locked)> _edges = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        string _nuShown, _voteShown, _zoneShown;
        readonly Dictionary<string, string> _links = new Dictionary<string, string>(StringComparer.Ordinal);   // "t:side" → link
        int _atMine = int.MinValue;
        float _psX = float.MaxValue, _psZ = float.MaxValue;
        static string Fmt(double v) => Math.Round(v, 1).ToString("0.0", CultureInfo.InvariantCulture);
        string _vzMine;
        readonly List<(int load, string json, double next, int left)> _retries = new List<(int, string, double, int)>();
        bool _warnedOther;

        public RunnerDirector(RunnerWorld w, string locationId, string contentId, RunnerRecord runner, IList<RunnerLevel> levels,
            RunnerBoardGeometry board, string sessionId = null)
        {
            _w = w ?? RunnerWorld.Main;
            LocationId = locationId ?? "";
            ContentId = contentId ?? "";
            SessionId = sessionId ?? "";
            Runner = runner ?? RunnerDefaults.BuiltIn();
            _levels = levels != null ? levels.Where(l => l != null).ToList() : new List<RunnerLevel>();
            Board = board ?? RunnerBoardGeometry.FromFeet(12, 18);
            try
            {
                State = _w.OpenState(StatePrefix + LocationId);
                if (State != null) { State.Requested += OnRequested; State.MarkGameplay(); }
            }
            catch (Exception e) { State = null; Log("no shared state (solo): " + e.Message); }
            Log($"board {Board.Count} tile(s) {Board.widthFt:0.#}x{Board.depthFt:0.#} ft · {ContentId} · way to play {Runner} · {_levels.Count} level(s)");
        }

        void Log(string m) => _w.Log?.Invoke($"[runner] {LocationId}: {m}");

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            DropScript();
            foreach (var l in _rendered.Values.ToList()) Unrender(l);
            _rendered.Clear();
            foreach (var kv in _links.ToList()) LinkOut(kv.Key, kv.Value, false);
            _links.Clear();
            if (_nuShown != null) { _nuShown = null; Safe(() => _w.NextUp?.Invoke(this, null)); }
            if (_voteShown != null) { _voteShown = null; Safe(() => _w.Vote?.Invoke(this, null)); }
            if (State != null) { State.Requested -= OnRequested; try { State.Dispose(); } catch (Exception) { } State = null; }
        }

        void Safe(Action a) { try { a(); } catch (Exception e) { Log("hook threw: " + e.Message); } }

        // ── Reads ──────────────────────────────────────────────────────

        /// The standing loads: the table, or with no table (the portal phone's local preview) this device's own list.
        public List<RunnerLoad> Loads => State != null ? RunnerBoardRules.DecodeLoads(Raw("ld")) : _solo ?? new List<RunnerLoad>();
        /// The main board's loads (every load not in a zone) — what the tile rules, swaps, transforms and edges act on.
        public List<RunnerLoad> MainLoads => RunnerBoardRules.InZone(Loads, null);
        public List<RunnerPending> Pending => RunnerBoardRules.DecodePending(Raw("pq"));
        string Raw(string k) => State?.GetJson(k);
        public bool Started => State != null ? State.GetNumber("st") >= 1 : _soloStarted;
        bool _soloStarted;

        JSONObject BoardMeta() => RunnerBoardRules.Parse(Raw("b"));
        bool TableIsMine()
        {
            var b = BoardMeta();
            if (b == null) return false;
            return RunnerBoardRules.Str(b, "cid") == ContentId && RunnerBoardRules.Str(b, "run") == Runner.runnerId
                && (int)RunnerBoardRules.Num(b, "n", Board.Count) == Board.Count
                && (string.IsNullOrEmpty(SessionId) || string.IsNullOrEmpty(RunnerBoardRules.Str(b, "sid")) || RunnerBoardRules.Str(b, "sid") == SessionId);
        }

        public RunnerNextUp NextUpNow()
        {
            var j = RunnerBoardRules.Parse(Raw("nu"));
            if (j == null) return null;
            var n = new RunnerNextUp { title = RunnerBoardRules.Str(j, "ti"), subtitle = RunnerBoardRules.Str(j, "su"), at = RunnerBoardRules.Num(j, "at"),
                until = RunnerBoardRules.Num(j, "un"), resultsJson = j.GetField("res")?.Print() };
            return _w.Clock() < n.until ? n : null;
        }

        public RunnerVote VoteNow()
        {
            var j = RunnerBoardRules.Parse(Raw("vt"));
            if (j == null) return null;
            var v = new RunnerVote { id = RunnerBoardRules.Str(j, "id"), tag = RunnerBoardRules.Str(j, "tg"), endsAt = RunnerBoardRules.Num(j, "en"),
                def = (int)RunnerBoardRules.Num(j, "df"), winner = (int)RunnerBoardRules.Num(j, "w", -1), winnerLabel = RunnerBoardRules.Str(j, "wl"), at = RunnerBoardRules.Num(j, "at") };
            var op = j.GetField("op"); if (op?.list != null) foreach (var o in op.list) v.options.Add(o?.stringValue ?? "");
            var lb = j.GetField("lb"); if (lb?.list != null) foreach (var o in lb.list) v.labels.Add(o?.stringValue ?? "");
            v.panel = RunnerBoardRules.Num(j, "np", 0) < 1;
            v.prompt = RunnerBoardRules.Str(j, "pr");
            var ds = j.GetField("ds"); if (ds?.list != null) foreach (var o in ds.list) v.descriptions.Add(o?.stringValue ?? "");
            v.outline = RunnerBoardRules.Num(j, "no", 0) < 1;
            var zn = j.GetField("zn");
            if (zn?.list != null)
                foreach (var z in zn.list)
                    v.zones.Add(((int)RunnerBoardRules.Num(z, "t", -1), (float)RunnerBoardRules.Num(z, "x", float.NaN), (float)RunnerBoardRules.Num(z, "z", float.NaN), (float)RunnerBoardRules.Num(z, "r", 0),
                        (float)RunnerBoardRules.Num(z, "w", 0), (float)RunnerBoardRules.Num(z, "d", 0)));
            return v;
        }

        /// The tiles each live device ON THE MAIN BOARD stands in (cell "at", departed devices dropped). A player inside
        /// a zone (dp.zones) sees another level, so it never holds a main tile's swap or counts for main-board events.
        public Dictionary<string, int> Presence()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (State == null) { if (_atMine != int.MinValue) d[_w.DeviceId()] = _atMine; return d; }
            var zm = RunnerBoardRules.Parse(Raw("zm"));
            List<RunnerLoad> all = null; JSONObject zs = null;
            var zoneTiles = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            bool SeesMain(string id, int tile)
            {
                string z = RunnerBoardRules.Str(zm, id);
                if (string.IsNullOrEmpty(z)) return true;
                if (!zoneTiles.TryGetValue(z, out var set)) { all = all ?? Loads; zs = zs ?? RunnerBoardRules.Parse(Raw("zs")); zoneTiles[z] = set = ZoneTiles(all, z, zs); }
                return tile >= 0 && !set.Contains(tile);
            }
            var live = new HashSet<string>(State.Members(), StringComparer.Ordinal) { _w.DeviceId() };
            foreach (var id in live)
            {
                string j = State.MineOf("at", id);
                if (j != null && int.TryParse(j, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)) d[id] = SeesMain(id, t) ? t : -1;
            }
            foreach (var r in Remotes())
                if (!string.IsNullOrEmpty(r.id) && !d.ContainsKey(r.id)) { int t = Board.TileAt(r.x, r.z); d[r.id] = SeesMain(r.id, t) ? t : -1; }
            return d;
        }

        List<(string id, float x, float z)> Remotes()
        {
            try { return _w.RemotePositions?.Invoke(this) ?? new List<(string, float, float)>(); }
            catch (Exception) { return new List<(string, float, float)>(); }
        }

        public int PlayersIn(int tile, Dictionary<string, int> presence = null)
        {
            int n = 0;
            foreach (var kv in presence ?? Presence()) if (kv.Value == tile) n++;
            return n;
        }

        /// A level by id / name / address. A full address no level of the release names ("Title/Runners/Levels/A_Map")
        /// still loads: it becomes a level of its own (id = the address, 1 tile until the prefab says otherwise), the same
        /// on every headset, because the load carries the address.
        RunnerLevel LevelById(string id)
        {
            var lv = RunnerBoardRules.MatchLevel(_levels, id);
            if (lv != null || string.IsNullOrWhiteSpace(id) || id.IndexOf('/') < 0) return lv;
            string a = id.Trim();
            if (!_adhoc.TryGetValue(a, out lv))
            {
                string leaf = RunnerBoardRules.Leaf(a);
                _adhoc[a] = lv = new RunnerLevel { id = a, name = RunnerLevelsFromManifest.Pretty(leaf), address = a, widthFt = 12, depthFt = 18 };
                Log($"level '{a}' is not in the release's level list — loading it by its address (1 tile)");
            }
            return lv;
        }
        readonly Dictionary<string, RunnerLevel> _adhoc = new Dictionary<string, RunnerLevel>(StringComparer.OrdinalIgnoreCase);

        // ── The device's own inputs ────────────────────────────────────

        /// Gameplay on THIS device says a level is done (dp.level.complete). Sent to the leader (retried until the
        /// board shows it done — a request to a leader that just left is never lost).
        public void ReportComplete(int loadId, string result, string scoresJson = null, string dataJson = null)
        {
            if (Disposed) return;
            var o = new JSONObject();
            o.AddField("i", loadId);
            string rs = string.IsNullOrEmpty(result) ? "done" : result;
            if (rs.Length > 200) rs = rs.Substring(0, 200);
            o.AddField("rs", rs);
            var sc = RunnerBoardRules.Parse(scoresJson);
            if (sc != null && sc.type == JSONObject.Type.Object) o.AddField("sc", sc);
            var dt = RunnerBoardRules.Parse(dataJson);
            if (dt != null && dt.type == JSONObject.Type.Object && dataJson.Length <= 1024) o.AddField("dt", dt);
            string json = o.Print();
            _retries.RemoveAll(r => r.load == loadId);
            _retries.Add((loadId, json, _w.Clock() + RetrySeconds, RetryMax));
            if (State != null) State.Request("lc", json); else _requests.Enqueue(("lc", json, _w.DeviceId()));
        }

        /// The load a rendered level root belongs to: by load id (the host keeps root → load).
        public RunnerLoad RenderedLoad(int id) => _rendered.TryGetValue(id, out var l) ? l : null;
        public IEnumerable<RunnerLoad> Rendered => _rendered.Values;

        void OnRequested(string kind, string json, string from) => _requests.Enqueue((kind, json, from));

        // ── Tick ───────────────────────────────────────────────────────

        public void Tick()
        {
            if (Disposed) return;
            double now = _w.Clock();
            WriteMyCells();
            if (!Synced) return;
            if (IsLeader) { if (KnowsItLeads(now)) LeaderTick(now); }
            else
            {
                _leaderSince = -1;
                if (_script != null) { Log("no longer the leader: the director stops here (the new leader resumes from the board)"); DropScript(); }
            }
            RetryReports(now);
            Render(now);
        }

        /// Lead only once this device KNOWS it leads (runtime-fixes, Oct 26 2026). A late joiner's table can sync from a
        /// snapshot before its SessionEngine has heard the session: every other holder is then "not live" to it, so
        /// SharedState names it the leader for a moment and it used to RESUME the director, apply a swap and step down.
        /// So: (1) the session engine must be synced (solo headsets get there too, after its solo grace); (2) taking over
        /// a board someone else started also waits LeaderSettleSeconds of being the leader without a break.
        bool KnowsItLeads(double now)
        {
            if (State == null || _script != null) return true;
            var eng = State.Engine;
            if (eng != null && !eng.Synced)
            {
                if (!_warnedWaiting) { _warnedWaiting = true; Log("waiting for the session to sync before directing (who leads is not known yet)"); }
                _leaderSince = -1;
                return false;
            }
            double t = eng != null ? eng.LocalTime : now;
            if (_leaderSince < 0) _leaderSince = t;
            if (Started && t - _leaderSince < LeaderSettleSeconds) return false;
            _warnedWaiting = false;
            return true;
        }
        /// How long a device must lead without a break before it takes over a board somebody else started.
        public static double LeaderSettleSeconds = 1.0;
        double _leaderSince = -1;
        bool _warnedWaiting;

        void WriteMyCells()
        {
            var p = _w.LocalPos?.Invoke(this) ?? (false, 0, 0);
            int tile = p.ok ? Board.TileAt(p.x, p.z) : -1;
            if (tile != _atMine)
            {
                _atMine = tile;
                State?.SetMine("at", tile.ToString(CultureInfo.InvariantCulture));
            }
            if (p.ok && (Math.Abs(p.x - _psX) >= 0.2f || Math.Abs(p.z - _psZ) >= 0.2f))
            {
                _psX = p.x; _psZ = p.z;
                State?.SetMine("ps", "[" + Fmt(p.x) + "," + Fmt(p.z) + "]");
            }
            var v = State != null ? VoteNow() : null;
            if (v != null && v.winner < 0)
            {
                int zone = ZoneOf(v, p.ok, p.x, p.z, tile);
                string vz = VoteCell(v.id, zone).ToString(CultureInfo.InvariantCulture);
                if (vz != _vzMine) { _vzMine = vz; State?.SetMine("vz", vz); }
            }
        }

        /// The vote cell (a number, so every device's value merges as a plain owner cell): vote n, zone z → n·1000 + z + 1.
        public static long VoteCell(string voteId, int zone)
        {
            long n = long.TryParse((voteId ?? "").TrimStart('v'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long x) ? x : 0;
            return n * 1000 + Math.Max(-1, Math.Min(998, zone)) + 1;
        }

        public static (string voteId, int zone) FromVoteCell(long cell) => ("v" + (cell / 1000).ToString(CultureInfo.InvariantCulture), (int)(cell % 1000) - 1);

        public static int ZoneOf(RunnerVote v, bool ok, float x, float z, int tile)
        {
            if (v == null) return -1;
            for (int i = 0; i < v.zones.Count; i++)
            {
                var zn = v.zones[i];
                if (zn.tile >= 0) { if (tile == zn.tile) return i; continue; }
                if (!ok || float.IsNaN(zn.x) || float.IsNaN(zn.z)) continue;
                if (zn.w > 0 && zn.d > 0) { if (Math.Abs(x - zn.x) <= zn.w / 2 && Math.Abs(z - zn.z) <= zn.d / 2) return i; continue; }
                float dx = x - zn.x, dz = z - zn.z, r = zn.r > 0 ? zn.r : 1f;
                if (dx * dx + dz * dz <= r * r) return i;
            }
            return -1;
        }

        void RetryReports(double now)
        {
            if (_retries.Count == 0) return;
            var loads = Loads;
            for (int i = _retries.Count - 1; i >= 0; i--)
            {
                var r = _retries[i];
                var l = loads.FirstOrDefault(x => x.id == r.load);
                if (l == null || l.done || r.left <= 0) { _retries.RemoveAt(i); continue; }
                if (now < r.next) continue;
                _retries[i] = (r.load, r.json, now + RetrySeconds, r.left - 1);
                State?.Request("lc", r.json);
            }
        }

        // ── Leader ─────────────────────────────────────────────────────

        void LeaderTick(double now)
        {
            if (_script == null) BecomeDirector();
            if (_script == null) return;
            while (_requests.Count > 0)
            {
                var r = _requests.Dequeue();
                if (r.kind == "lc") HandleComplete(r.json, r.from);
                else if (r.kind == "vp") ApplyOverride(r.json);
                else if (r.kind == "rq") HandleRequest(r.json, r.from);
            }
            PresenceEvents();
            PlayerEvents();
            ZoneTick(now);
            ApplyPending(now);
            CloseVote(now);
            ClockStartHook();
            EndingHooks();
            var nu = RunnerBoardRules.Parse(Raw("nu"));
            if (nu != null && now >= RunnerBoardRules.Num(nu, "un")) State?.Delete("nu");
            PersistState();
        }

        void BecomeDirector()
        {
            if (State != null && !State.Synced) return;
            string rid = LocationId + "#" + (++s_gen);
            var rt = RunnerLua.For(_w.Lua?.Invoke());
            if (rt == null) { if (LastError == null) { LastError = "no Lua env"; Log(LastError); } return; }
            _script = rt.Load(rid, RunnerDefaults.LuaFor(Runner), this);
            LuaLoads++;
            if (!_script.Loaded)
            {
                LastError = _script.Error;
                Log($"director '{Runner.runnerId}' failed ({_script.Error}) — the built-in default runner plays instead");
                _script.Dispose();
                _script = rt.Load(rid + "d", RunnerDefaults.BuiltInLua, this);
                if (!_script.Loaded) { _script.Dispose(); _script = null; return; }
            }
            _presence = Presence();   // silently: a hand-over does not replay who is where
            _players = new HashSet<string>(LivePlayers(), StringComparer.Ordinal);
            if (Started && TableIsMine())
            {
                Resumes++;
                _script.SetState(Raw("rs") ?? "{}");
                _lastRs = Raw("rs");
                double el = State != null ? State.GetNumber("el", -1) : -1;
                if (el >= 0) _script.PrimeEnding(el);
                if (State != null ? State.GetNumber("cs") >= 1 : _soloClock) _script.ClockPast();
                var st = _script.TableOf(BoardJson(true));
                Log($"leader {_w.DeviceId()}: RESUMES the director from the board ({Loads.Count} load(s), state {Trunc(_lastRs)})");
                _script.Call("onResume", st);
                return;
            }
            Starts++;
            if (State != null)
            {
                foreach (var k in State.Keys.ToList()) if (!State.IsCell(k)) State.Delete(k);
                State.Clear("vz");
                var b = new JSONObject();
                b.AddField("cid", ContentId); b.AddField("run", Runner.runnerId ?? ""); b.AddField("sid", SessionId);
                b.AddField("n", Board.Count); b.AddField("w", Board.widthFt); b.AddField("d", Board.depthFt);
                State.Set("b", b.Print());
                State.SetNumber("nid", 1);
            }
            Log($"leader {_w.DeviceId()}: onSessionStart ({Runner.runnerId})");
            _script.Call("onSessionStart", _script.TableOf(BoardJson(false)));
            if (State != null) State.SetNumber("st", 1); else _soloStarted = true;
            if (Loads.Count == 0 && _levels.Count > 0)
            {
                Log("the director loaded nothing at the start: its levels in order, one per tile (no empty room)");
                for (int i = 0; i < Math.Min(Board.Count, _levels.Count); i++) DoLoad(_levels[i], new[] { i }, null, null);
            }
            PersistState();
        }

        void DropScript()
        {
            _script?.Dispose();
            _script = null;
            _presence = null;
            _players = null;
        }

        HashSet<string> _players;

        /// dp.flow.onPlayerJoin / onPlayerLeave: the session's players came / went (late joiners land on the main board).
        void PlayerEvents()
        {
            if (_script == null) return;
            var now = new HashSet<string>(LivePlayers(), StringComparer.Ordinal);
            var prev = _players ?? now;
            _players = now;
            foreach (var p in now) if (!prev.Contains(p)) _script.Fire("join", p);
            foreach (var p in prev) if (!now.Contains(p)) _script.Fire("leave", p);
        }

        /// Gameplay on any headset asks the director something (dp.board.request(kind, data)) → onRequest(player, kind, data).
        public void Request(string kind, string dataJson)
        {
            if (Disposed || string.IsNullOrEmpty(kind)) return;
            var o = new JSONObject();
            o.AddField("k", kind.Length > 40 ? kind.Substring(0, 40) : kind);
            var d = RunnerBoardRules.Parse(dataJson);
            if (d != null && dataJson.Length <= 1024) o.AddField("d", d);
            o.AddField("p", _w.DeviceId());
            if (State != null) State.Request("rq", o.Print()); else _requests.Enqueue(("rq", o.Print(), _w.DeviceId()));
        }

        void HandleRequest(string json, string from)
        {
            var j = RunnerBoardRules.Parse(json);
            if (j == null || _script == null) return;
            string player = RunnerBoardRules.Str(j, "p", from);
            var d = j.GetField("d");
            _script.Fire("request", player, RunnerBoardRules.Str(j, "k", ""), d != null ? (object)_script.TableOf(d.Print()) ?? d.Print() : null);
        }

        void PersistState()
        {
            if (_script == null || State == null) return;
            string rs = _script.StateJson();
            if (rs == _lastRs) return;
            if (rs.Length > RunnerBoardRules.MaxStateChars) { if (_lastRs != "!big") Log($"dp.flow.state is {rs.Length} chars (max {RunnerBoardRules.MaxStateChars}) — not synced"); _lastRs = "!big"; return; }
            _lastRs = rs;
            State.Set("rs", rs);
        }

        void EndingHooks()
        {
            double left = _w.SessionTimeLeft?.Invoke() ?? -1;
            if (left < 0 || _script == null) return;
            _script.Ending(left);
            if (State != null)
            {
                double el = State.GetNumber("el", double.MaxValue);
                if (left < el) State.SetNumber("el", Math.Round(left, 1));
            }
        }

        void HandleComplete(string json, string from)
        {
            var j = RunnerBoardRules.Parse(json);
            if (j == null) return;
            int id = (int)RunnerBoardRules.Num(j, "i", -1);
            string result = RunnerBoardRules.Str(j, "rs", "done");
            var sc = j.GetField("sc");
            Complete(id, result, sc, from, j.GetField("dt"));
        }

        bool Complete(int loadId, string result, JSONObject scores, string from, JSONObject data = null)
        {
            var loads = Loads;
            var l = loads.FirstOrDefault(x => x.id == loadId);
            if (l == null) { Log($"level complete for load {loadId} (from {from}) — not on the board any more, ignored"); return false; }
            if (l.done) return false;   // every headset reports it; the first counts
            l.done = true; l.result = result;
            State?.Set("ld", RunnerBoardRules.EncodeLoads(loads));
            if (scores != null && scores.type == JSONObject.Type.Object && scores.keys != null && State != null)
            {
                var sc = RunnerBoardRules.Parse(Raw("sc")) ?? new JSONObject();
                for (int i = 0; i < scores.keys.Count; i++)
                {
                    double add = scores.list[i] != null && scores.list[i].isNumber ? scores.list[i].doubleValue : 0;
                    double cur = RunnerBoardRules.Num(sc, scores.keys[i]);
                    sc.SetField(scores.keys[i], cur + add);
                }
                State.Set("sc", sc.Print());
            }
            Log($"level done: {l.level} on tile(s) {Tiles1(l.tiles)} → {result} (from {from})");
            if (_script != null)
            {
                var info = new JSONObject();
                info.AddField("tile", l.tiles.Length > 0 ? l.tiles[0] + 1 : 0);
                var ts = new JSONObject(JSONObject.Type.Array); foreach (int t in l.tiles) ts.Add(t + 1);
                info.AddField("tiles", ts);
                info.AddField("result", result ?? "done");
                info.AddField("load", l.id);
                info.AddField("level", l.level ?? "");
                if (!l.InMain) info.AddField("zone", l.zone);
                if (scores != null) info.AddField("scores", scores);
                if (data != null && data.type == JSONObject.Type.Object) info.AddField("data", data);
                _script.Fire("complete", l.level, _script.TableOf(info.Print()));
            }
            return true;
        }

        void PresenceEvents()
        {
            if (_script == null) return;
            var now = Presence();
            var prev = _presence ?? new Dictionary<string, int>();
            _presence = now;
            var countPrev = new Dictionary<int, int>(); var countNow = new Dictionary<int, int>();
            foreach (var kv in prev) if (kv.Value >= 0) countPrev[kv.Value] = (countPrev.TryGetValue(kv.Value, out int a) ? a : 0) + 1;
            foreach (var kv in now) if (kv.Value >= 0) countNow[kv.Value] = (countNow.TryGetValue(kv.Value, out int a) ? a : 0) + 1;
            foreach (var kv in now)
            {
                if (kv.Value < 0) continue;
                if (prev.TryGetValue(kv.Key, out int was) && was == kv.Value) continue;
                _script.Fire("enter", kv.Value + 1, kv.Key);
            }
            foreach (var kv in countPrev)
                if (!countNow.ContainsKey(kv.Key)) _script.Fire("empty", kv.Key + 1);
        }

        void ApplyPending(double now)
        {
            var pend = Pending;
            if (pend.Count == 0) return;
            var keep = new List<RunnerPending>();
            bool changed = false;
            var presence = Presence();
            var covered = new HashSet<int>();   // tiles a load in this pass took: a swap still waiting there is stale
            foreach (var p in pend)
            {
                var lv = LevelById(p.level);
                if (lv == null) { changed = true; Log($"pending {p.kind}: unknown level '{p.level}' — dropped"); continue; }
                var loads = MainLoads;
                if (p.kind == RunnerPending.Swap)
                {
                    var standing = RunnerBoardRules.At(loads, p.tile);
                    int inTile = PlayersIn(p.tile, presence);
                    if (!RunnerBoardRules.SwapReady(standing, inTile)) { keep.Add(p); continue; }
                    int[] tiles = RunnerBoardRules.Place(Board, lv.Footprint, p.tile, false);
                    // a swap stays quiet: never take over a tile somebody stands in
                    if (tiles.Any(t => t != p.tile && (PlayersIn(t, presence) > 0 || (RunnerBoardRules.At(loads, t) is RunnerLoad o && !o.done)))) tiles = new[] { p.tile };
                    Log($"swap on tile {p.tile + 1}: {(standing != null ? standing.level + " done" : "empty")}, nobody in it → {lv.id}");
                    DoLoad(lv, tiles, null, null);
                    foreach (int t in tiles) covered.Add(t);
                    changed = true;
                }
                else if (p.kind == RunnerPending.Transform)
                {
                    if (!RunnerBoardRules.TransformReady(loads)) { keep.Add(p); continue; }
                    Log($"transform: every tile is clear → {lv.id} across the board (Next up)");
                    DoLoad(lv, RunnerBoardRules.All(Board.Count), p.nuTitle ?? lv.name ?? lv.id, p.nuSub ?? "", force: true);
                    foreach (int t in RunnerBoardRules.All(Board.Count)) covered.Add(t);
                    changed = true;
                }
                else changed = true;
            }
            // a swap waiting on a tile a load of this pass took is stale (WW proof: it fired after the boss and replaced it)
            int before = keep.Count;
            keep.RemoveAll(p => p.kind == RunnerPending.Swap && covered.Contains(p.tile));
            if (keep.Count != before) { changed = true; Log("pending swap(s) on tiles a new load took — dropped"); }
            if (changed && State != null) State.Set("pq", RunnerBoardRules.EncodePending(keep));
        }

        /// Most votes that may wait behind an open one (dp.flow.choose queues; see Op "choose").
        public const int MaxQueuedVotes = 4;
        /// The phone shows at most 60 characters of a label (DreamPark-Web normalizeVoteReport).
        public const int MaxLabelChars = 60, MaxDescriptionChars = 120;

        /// n floor zones (board frame, metres) that split the whole board into a grid, 1 or 2 or 20 (Aidan, Oct 28 2026:
        /// "split up the play area into a grid of options… scale to fit 1 or 2 or 20"): the layout whose cells are the
        /// most square (the biggest short side; a row when that is nearly as big), front row (the portal) first, left to
        /// right; a short last row's cells widen to fill it. Rectangles {x, z, w, d}: the cell drawn is the cell that counts.
        public static List<(float x, float z, float w, float d)> GridZones(RunnerBoardGeometry g, int n)
        {
            var list = new List<(float, float, float, float)>();
            if (n <= 0) return list;
            float W = g.WidthM, D = g.DepthM;
            float Cell(int c) => Math.Min(W / c, D / ((n + c - 1) / c));
            float best = 0;
            for (int c = 1; c <= n; c++) best = Math.Max(best, Cell(c));
            int cols = 1;   // among layouts whose cells are within 15 % of the biggest: the fewest rows, then the biggest cells
            for (int c = 1; c <= n; c++)
            {
                if (Cell(c) < best * 0.85f) continue;
                int r = (n + c - 1) / c, rc = (n + cols - 1) / cols;
                if (Cell(cols) < best * 0.85f || r < rc || (r == rc && Cell(c) > Cell(cols))) cols = c;
            }
            int rows = (n + cols - 1) / cols;
            float cd = D / rows;
            for (int i = 0; i < n; i++)
            {
                int row = i / cols, col = i % cols;
                int inRow = row == rows - 1 ? n - row * cols : cols;
                float cw = W / inRow;
                list.Add((-W / 2 + cw * (col + 0.5f), -D / 2 + cd * (row + 0.5f), cw, cd));
            }
            return list;
        }

        /// An option shown on the card as its raw id ("pitrun": lower case, no spaces, no label) — warned once per runner.
        public static bool LooksLikeId(string s) => !string.IsNullOrEmpty(s) && s.IndexOf(' ') < 0 && s.ToLowerInvariant() == s && s.Any(char.IsLetter);
        static string Humanize(string id) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase((id ?? "").Replace('_', ' ').Replace('-', ' ').Replace('.', ' '));
        readonly HashSet<string> _labelWarned = new HashSet<string>(StringComparer.Ordinal);
        void WarnIdLabels(JSONObject opts, JSONObject lb)
        {
            var bare = new List<string>();
            for (int i = 0; i < opts.list.Count; i++)
            {
                string o = opts.list[i]?.stringValue ?? "";
                if (string.IsNullOrEmpty(lb.list[i]?.stringValue) && LooksLikeId(o) && _labelWarned.Add(o)) bare.Add(o);
            }
            if (bare.Count == 0) return;
            Log($"WARNING dp.flow.choose: option(s) {string.Join(", ", bare.Select(b => "'" + b + "'"))} have no label, so the vote card and the phone show the id as-is. " +
                $"Give each one: options = {{ {{id = \"{bare[0]}\", label = \"{Humanize(bare[0])}\"}}, … }} (or labels = {{ {bare[0]} = \"…\" }}); onChoice still gets the id.");
        }

        void OpenVote(JSONObject vt, double sec)
        {
            vt.RemoveField("sec");
            if (RunnerBoardRules.Num(vt, "dw", 0) > 0)
            {
                // a DWELL vote: the countdown starts once someone has stood in a zone dw seconds (Aidan's floor-zone model)
                vt.SetField("ln", Math.Round(sec, 3));
                vt.SetField("ar", 0);
                sec = DwellIdleSeconds;
                _dwellSince = -1;
            }
            vt.SetField("en", Math.Round(_w.Clock() + sec, 3));
            vt.SetField("at", Math.Round(_w.Clock(), 3));
            vt.SetField("w", -1);
            State?.Clear("vz");
            State?.Set("vt", vt.Print());
            Log($"choose {RunnerBoardRules.Str(vt, "id")}: {vt.GetField("op")?.Print()} for {sec:0.#} s{(RunnerBoardRules.Str(vt, "tg") is string tg ? " tag " + tg : "")}");
        }

        /// The open vote is gone: the first queued one opens now, with its own full countdown.
        void OpenQueuedVote()
        {
            var q = RunnerBoardRules.Parse(Raw("vq"));
            if (q?.list == null || q.list.Count == 0) return;
            var next = q.list[0];
            q.list.RemoveAt(0);
            if (State != null) { if (q.list.Count == 0) State.Delete("vq"); else State.Set("vq", q.Print()); }
            if (next == null || next.type != JSONObject.Type.Object) return;
            double sec = RunnerBoardRules.Num(next, "sec", RunnerBoardRules.VoteSecondsDefault);
            OpenVote(next, sec > 0 ? sec : RunnerBoardRules.VoteSecondsDefault);
        }

        /// An unarmed dwell vote waits this long for anyone to step into a zone, then closes on its default.
        public static double DwellIdleSeconds = 600;
        double _dwellSince = -1;

        /// Who stands in which zone of the open vote right now (live players, by position / vote cell).
        List<int> ZoneVotesNow(RunnerVote v, out int present)
        {
            var votes = new List<int>();
            present = 0;
            if (State == null) return votes;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in LivePlayers())
            {
                if (!counted.Add(id)) continue;
                present++;
                var p = PositionOf(id);
                int z = p.ok ? ZoneOf(v, true, p.x, p.z, Board.TileAt(p.x, p.z)) : -1;
                if (z < 0)
                {
                    string j = State.MineOf("vz", id);
                    if (j != null && long.TryParse(j, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cell)) { var (vid, zz) = FromVoteCell(cell); if (vid == v.id) z = zz; }
                }
                if (z >= 0) votes.Add(z);
            }
            return votes;
        }

        /// Dwell votes: arm the countdown after dw seconds in a zone, re-arm when everyone steps out, close early when
        /// every present player stands in a zone.
        void DwellTick(RunnerVote v, double now)
        {
            var vt = RunnerBoardRules.Parse(Raw("vt"));
            double dw = RunnerBoardRules.Num(vt, "dw", 0);
            if (dw <= 0 || v.winner >= 0) return;
            bool armed = RunnerBoardRules.Num(vt, "ar", 0) >= 1;
            var inZones = ZoneVotesNow(v, out int present);
            if (!armed)
            {
                if (inZones.Count == 0) { _dwellSince = -1; return; }
                if (_dwellSince < 0) _dwellSince = now;
                if (now - _dwellSince < dw) return;
                double len = RunnerBoardRules.Num(vt, "ln", RunnerBoardRules.VoteSecondsDefault);
                vt.SetField("ar", 1); vt.SetField("en", Math.Round(now + len, 3));
                State?.Set("vt", vt.Print());
                Log($"vote {v.id}: someone stood in a zone {dw:0.#} s → the {len:0.#} s countdown starts");
                return;
            }
            if (inZones.Count == 0)
            {
                _dwellSince = -1;
                vt.SetField("ar", 0); vt.SetField("en", Math.Round(now + DwellIdleSeconds, 3));
                State?.Set("vt", vt.Print());
                Log($"vote {v.id}: everyone stepped out of the zones → the countdown waits again");
                return;
            }
            if (present > 0 && inZones.Count >= present && v.endsAt - now > 1.0)
            {
                vt.SetField("en", Math.Round(now + 1.0, 3));   // everyone has picked: close in a beat
                State?.Set("vt", vt.Print());
                Log($"vote {v.id}: every player stands in a zone → it closes now");
            }
        }

        /// The phone's pick (or any override) closes the open vote with that option. Leader applies it; any device may ask.
        public bool OverrideVote(string option, string voteId = null)
        {
            var v = VoteNow();
            if (v == null || v.winner >= 0 || (!string.IsNullOrEmpty(voteId) && voteId != v.id)) return false;
            int i = v.options.IndexOf(option ?? "");
            if (i < 0) return false;
            var o = new JSONObject(); o.AddField("id", v.id); o.AddField("w", i);
            if (IsLeader && _script != null) ApplyOverride(o.Print());
            else State?.Request("vp", o.Print());
            return true;
        }

        void ApplyOverride(string json)
        {
            var j = RunnerBoardRules.Parse(json);
            var v = VoteNow();
            if (j == null || v == null || v.winner >= 0 || RunnerBoardRules.Str(j, "id") != v.id) return;
            _override = ((int)RunnerBoardRules.Num(j, "w", -1), v.id);
            var vt = RunnerBoardRules.Parse(Raw("vt"));
            vt.SetField("en", Math.Round(_w.Clock(), 3));
            State?.Set("vt", vt.Print());
            Log($"vote {v.id}: overridden → '{(_override.w >= 0 && _override.w < v.options.Count ? v.options[_override.w] : "?")}'");
        }
        (int w, string id) _override = (-1, null);

        void CloseVote(double now)
        {
            var v = VoteNow();
            if (v == null) { OpenQueuedVote(); return; }
            DwellTick(v, now);
            v = VoteNow();
            if (v.winner >= 0)
            {
                if (now >= v.endsAt + VoteWinnerHoldSeconds) { State?.Delete("vt"); OpenQueuedVote(); }
                return;
            }
            if (now < v.endsAt) return;
            var votes = new List<int>();
            if (State != null)
            {
                var live = new HashSet<string>(State.Members(), StringComparer.Ordinal) { _w.DeviceId() };
                var voted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in live)
                {
                    string j = State.MineOf("vz", id);
                    if (j == null || !long.TryParse(j, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cell)) continue;
                    var (vid, z) = FromVoteCell(cell);
                    if (vid == v.id) { votes.Add(z); voted.Add(id); }
                }
                // players without a vote cell for this vote (no director on their device: an older build, a simulated
                // peer): the zone they stand in
                foreach (var r in Remotes())
                    if (!string.IsNullOrEmpty(r.id) && voted.Add(r.id)) votes.Add(ZoneOf(v, true, r.x, r.z, Board.TileAt(r.x, r.z)));
            }
            int w = RunnerBoardRules.Tally(votes, v.options.Count, v.def);
            var counts = RunnerBoardRules.Counts(votes, v.options.Count);
            var tied = RunnerBoardRules.Tied(counts);
            var vt = RunnerBoardRules.Parse(Raw("vt"));
            string tb = RunnerBoardRules.Str(vt, "tb");
            bool overridden = _override.id == v.id && _override.w >= 0 && _override.w < v.options.Count;
            if (overridden) { w = _override.w; tied.Clear(); }
            _override = (-1, null);
            bool tie = tied.Count > 1;
            if (tie)
            {
                // the game's tiebreak: a Lua function on the leader that opened the vote (lost on a hand-over → 'default'),
                // or 'random' / 'first' / 'last', the same on every device
                int pick = -1;
                if (tb == "fn" && _script != null)
                {
                    var tl = new JSONObject(JSONObject.Type.Array); foreach (int i in tied) tl.Add(v.options[i]);
                    string got = _script.TieBreak(v.id, tl.Print());
                    if (got != null) pick = v.options.IndexOf(got);
                    if (pick >= 0 && !tied.Contains(pick)) { Log($"vote {v.id}: tiebreak returned '{got}', not one of the tied options — ignored"); pick = -1; }
                }
                w = pick >= 0 ? pick : RunnerBoardRules.TieBreak(tb, v.id, tied, w);
            }
            string label = w >= 0 && w < v.options.Count ? v.options[w] : "";
            vt.SetField("w", w); vt.SetField("wl", label);
            State?.Set("vt", vt.Print());
            State?.Clear("vz");
            Log($"vote {v.id}{(v.tag != null ? " (" + v.tag + ")" : "")}: {votes.Count} vote(s) [{string.Join(",", votes)}] → '{label}'{(votes.Count == 0 ? " (nobody voted: the default)" : tie ? $" (tie, broken by {(string.IsNullOrEmpty(tb) ? "default" : tb)})" : "")}");
            var info = new JSONObject();
            var cj = new JSONObject();
            for (int i = 0; i < v.options.Count; i++) cj.SetField(v.options[i], counts[i]);
            info.AddField("counts", cj); info.AddField("votes", votes.Count); info.AddField("tied", tie);
            if (overridden) info.AddField("override", true);
            _script?.Vote(v.id, label, v.tag ?? "", info.Print());
        }

        // ── Ops from the director script (IRunnerDirectorHost) ─────────

        public string Op(string op, string a, string json)
        {
            if (op == "log") { Log("lua: " + a); return ""; }
            if (!IsLeader) { Log($"{op} ignored: not the leader"); return "false"; }
            var j = RunnerBoardRules.Parse(json) ?? new JSONObject();
            switch (op)
            {
                case "load":
                {
                    var lv = LevelById(a);
                    if (lv == null) { Log($"dp.flow.load('{a}'): no such level in {ContentId} (have: {string.Join(", ", _levels.Select(x => x.id))})"); return "false"; }
                    int tile = (int)RunnerBoardRules.Num(j, "tile", 0) - 1;
                    bool all = string.Equals(RunnerBoardRules.Str(j, "span"), "all", StringComparison.OrdinalIgnoreCase);
                    var occupied = new HashSet<int>(); foreach (var l in MainLoads) foreach (int t in l.tiles) occupied.Add(t);
                    int[] tiles = RunnerBoardRules.Place(Board, lv.Footprint, tile, all, occupied);
                    var nu = j.GetField("nextUp");
                    string nt = nu != null && nu.type == JSONObject.Type.Object ? RunnerBoardRules.Str(nu, "title", lv.name ?? lv.id) : null;
                    string ns = nu != null && nu.type == JSONObject.Type.Object ? RunnerBoardRules.Str(nu, "subtitle", "") : null;
                    DoLoad(lv, tiles, nt, ns, options: j.GetField("options"));
                    return "true";
                }
                case "swap":
                {
                    int tile = (int)RunnerBoardRules.Num(j, "tile", 0) - 1;
                    if (tile < 0 || tile >= Board.Count) { Log($"swapWhenClear: tile {tile + 1} is not on this {Board.Count}-tile board"); return "false"; }
                    if (LevelById(a) == null) { Log($"swapWhenClear: no such level '{a}'"); return "false"; }
                    var pend = Pending.Where(p => !(p.kind == RunnerPending.Swap && p.tile == tile)).ToList();
                    pend.Add(new RunnerPending { kind = RunnerPending.Swap, tile = tile, level = a, at = _w.Clock() });
                    State?.Set("pq", RunnerBoardRules.EncodePending(pend));
                    Log($"swapWhenClear(tile {tile + 1}, {a}) — waits for the tile's level done + nobody in it");
                    return "true";
                }
                case "transform":
                {
                    var lv = LevelById(a);
                    if (lv == null) { Log($"transform: no such level '{a}'"); return "false"; }
                    var nu = j.GetField("nextUp");
                    var pend = Pending.Where(p => p.kind != RunnerPending.Transform).ToList();
                    pend.Add(new RunnerPending { kind = RunnerPending.Transform, level = a, at = _w.Clock(),
                        nuTitle = nu != null && nu.type == JSONObject.Type.Object ? RunnerBoardRules.Str(nu, "title") : null,
                        nuSub = nu != null && nu.type == JSONObject.Type.Object ? RunnerBoardRules.Str(nu, "subtitle") : null });
                    State?.Set("pq", RunnerBoardRules.EncodePending(pend));
                    Log($"transform({a}) — waits for every tile's level to be done");
                    return "true";
                }
                case "end":
                {
                    var loads = Loads;
                    RunnerLoad l = null;
                    if (a.StartsWith("#", StringComparison.Ordinal) && int.TryParse(a.Substring(1), out int t1)) l = RunnerBoardRules.At(RunnerBoardRules.InZone(loads, null), t1 - 1);
                    else
                    {
                        var lv = LevelById(a);
                        l = loads.LastOrDefault(x => !x.done && (lv != null ? RunnerBoardRules.MatchLevel(new[] { lv }, x.level) != null : x.level == a));
                    }
                    if (l == null) { Log($"endLevel('{a}'): nothing like that stands"); return "false"; }
                    return Complete(l.id, RunnerBoardRules.Str(j, "result", "done"), null, "director") ? "true" : "false";
                }
                case "choose":
                {
                    var opts = j.GetField("options");
                    if (opts?.list == null || opts.list.Count == 0) { Log("choose: no options"); return ""; }
                    string id = "v" + ((int)(State?.GetNumber("vseq") ?? 0) + 1);
                    State?.SetNumber("vseq", (State?.GetNumber("vseq") ?? 0) + 1);
                    double sec = RunnerBoardRules.Num(j, "seconds", RunnerBoardRules.VoteSecondsDefault);
                    if (sec <= 0) sec = RunnerBoardRules.VoteSecondsDefault;
                    var vt = new JSONObject();
                    vt.AddField("id", id);
                    string tag = RunnerBoardRules.Str(j, "tag");
                    if (tag != null) vt.AddField("tg", tag);
                    string tbm = RunnerBoardRules.Str(j, "tiebreak");
                    if (!string.IsNullOrEmpty(tbm)) vt.AddField("tb", tbm);
                    double dwell = RunnerBoardRules.Num(j, "dwell", 0);
                    if (dwell > 0) vt.AddField("dw", Math.Min(10, dwell));
                    vt.AddField("op", opts);
                    // display labels (GridRunner beta v1, Oct 27 2026: ids like "pitrun · crusher" were shown verbatim)
                    var lbs = j.GetField("labels");
                    var lb = new JSONObject(JSONObject.Type.Array);
                    bool anyLabel = false;
                    for (int i = 0; i < opts.list.Count; i++)
                    {
                        string s = lbs?.list != null && i < lbs.list.Count ? lbs.list[i]?.stringValue ?? "" : "";
                        if (s.Length > MaxLabelChars) s = s.Substring(0, MaxLabelChars);
                        anyLabel |= s.Length > 0;
                        lb.Add(s);
                    }
                    if (anyLabel) vt.AddField("lb", lb);
                    var dss = j.GetField("descriptions");
                    var ds = new JSONObject(JSONObject.Type.Array);
                    bool anyDesc = false;
                    for (int i = 0; i < opts.list.Count; i++)
                    {
                        string s = dss?.list != null && i < dss.list.Count ? dss.list[i]?.stringValue ?? "" : "";
                        if (s.Length > MaxDescriptionChars) s = s.Substring(0, MaxDescriptionChars);
                        anyDesc |= s.Length > 0;
                        ds.Add(s);
                    }
                    if (anyDesc) vt.AddField("ds", ds);
                    string prompt = RunnerBoardRules.Str(j, "prompt");
                    if (!string.IsNullOrWhiteSpace(prompt)) vt.AddField("pr", prompt.Length > MaxLabelChars ? prompt.Substring(0, MaxLabelChars) : prompt);
                    bool panel = j.GetField("panel")?.type != JSONObject.Type.Bool || j.GetField("panel").boolValue;
                    if (!panel) vt.AddField("np", 1);
                    if (j.GetField("outline")?.type == JSONObject.Type.Bool && !j.GetField("outline").boolValue) vt.AddField("no", 1);
                    if (panel) WarnIdLabels(opts, lb);
                    var zn = new JSONObject(JSONObject.Type.Array);
                    var zones = j.GetField("zones");
                    // no zones: the platform splits the board into a grid of floor zones, one per option (Aidan, Oct 28 2026:
                    // "a default spatialized grid that shows the options"; it used to be option i = tile i, and options past
                    // the last tile had no zone at all)
                    // (as many options as tiles: the grid IS the tiles, so they stay tile zones, as before)
                    var grid = (zones?.list == null || zones.list.Count == 0) && opts.list.Count != Board.Count ? GridZones(Board, opts.list.Count) : null;
                    for (int i = 0; i < opts.list.Count; i++)
                    {
                        if (grid != null)
                        {
                            var gz = new JSONObject();
                            gz.AddField("x", Math.Round(grid[i].x, 3)); gz.AddField("z", Math.Round(grid[i].z, 3)); gz.AddField("w", Math.Round(grid[i].w, 3)); gz.AddField("d", Math.Round(grid[i].d, 3));
                            zn.Add(gz); continue;
                        }
                        var z = zones?.list != null && i < zones.list.Count ? zones.list[i] : null;
                        var o = new JSONObject();
                        double tile = RunnerBoardRules.Num(z, "tile", 0);
                        if (tile >= 1) o.AddField("t", (int)tile - 1);
                        else if (z?.GetField("x") != null)
                        {
                            o.AddField("x", RunnerBoardRules.Num(z, "x")); o.AddField("z", RunnerBoardRules.Num(z, "z"));
                            double zw = RunnerBoardRules.Num(z, "w", 0), zd = RunnerBoardRules.Num(z, "d", 0);
                            if (zw > 0 && zd > 0) { o.AddField("w", zw); o.AddField("d", zd); } else o.AddField("r", RunnerBoardRules.Num(z, "r", 1));
                        }
                        else if (z == null && i < Board.Count) o.AddField("t", i);   // no zones: option i = tile i
                        zn.Add(o);
                    }
                    vt.AddField("zn", zn);
                    vt.AddField("df", (int)RunnerBoardRules.Num(j, "default", 1) - 1);
                    // ONE vote at a time (runtime-fixes, Oct 26 2026): a choose while another vote is still open used to
                    // replace it silently, and the first vote's onChoice never fired. Now it QUEUES (in the shared table,
                    // so a new leader opens it too): it opens with its own full countdown once the open vote has closed
                    // and its winner has been shown. More than MaxQueuedVotes waiting → refused (returns nil, logged).
                    var open = VoteNow();
                    // queue behind an OPEN vote, and behind a closed one whose winner is still being shown (a choose from
                    // inside onChoice used to replace the winner's beat at once)
                    if (open != null && (open.winner < 0 || _w.Clock() < open.endsAt + VoteWinnerHoldSeconds))
                    {
                        var q = RunnerBoardRules.Parse(Raw("vq"));
                        if (q == null || q.type != JSONObject.Type.Array) q = new JSONObject(JSONObject.Type.Array);
                        int waiting = q.list?.Count ?? 0;
                        if (waiting >= MaxQueuedVotes)
                        {
                            Log($"ERROR dp.flow.choose: vote {open.id} is open and {waiting} more are waiting — this one ({opts.Print()}) is refused (one vote at a time)");
                            return "";
                        }
                        vt.AddField("sec", sec);
                        q.Add(vt);
                        State?.Set("vq", q.Print());
                        Log($"choose {id}: {opts.Print()} queued behind vote {open.id} ({waiting + 1} waiting){(tag != null ? " tag " + tag : "")}");
                        return id;
                    }
                    OpenVote(vt, sec);
                    return id;
                }
                case "edge_link":
                case "edge_unlink":
                    return LinkOp(op == "edge_link", j) ? "true" : "false";
                case "vote_close":
                {
                    // dp.flow.closeVote([option]): close the open vote now — on that option, or on the zones as they stand
                    var v = VoteNow();
                    if (v == null || v.winner >= 0) return "false";
                    if (!string.IsNullOrEmpty(a))
                    {
                        int i = v.options.IndexOf(a);
                        if (i < 0) { Log($"closeVote('{a}'): not an option of vote {v.id}"); return "false"; }
                        var o = new JSONObject(); o.AddField("id", v.id); o.AddField("w", i);
                        ApplyOverride(o.Print());
                        return "true";
                    }
                    var vt = RunnerBoardRules.Parse(Raw("vt"));
                    vt.SetField("en", Math.Round(_w.Clock(), 3));
                    State?.Set("vt", vt.Print());
                    Log($"vote {v.id}: closed by the director");
                    return "true";
                }
                case "zone_open":
                case "zone_load":
                case "zone_close":
                case "zone_send":
                case "zone_lock":
                case "zone_door":
                case "zone_undoor":
                    return ZoneOp(op, a, j);
                case "edge_open":
                case "edge_lock":
                {
                    int tile = (int)RunnerBoardRules.Num(j, "tile", 0) - 1;
                    string side = RunnerBoardRules.NormSide(RunnerBoardRules.Str(j, "side"));
                    if (Board.Tile(tile) == null || side == "") { Log($"dp.edges: tile {tile + 1} side '{RunnerBoardRules.Str(j, "side")}' is not on this board"); return "false"; }
                    var eg = RunnerBoardRules.Parse(Raw("eg")) ?? new JSONObject();
                    void SetEdge(int t, string s)
                    {
                        string key = t + ":" + s;
                        var e = eg.GetField(key) ?? new JSONObject();
                        if (op == "edge_open") { e.SetField("lk", RunnerBoardRules.Str(j, "look", "open")); e.RemoveField("ln"); }
                        else e.SetField("lo", RunnerBoardRules.Bool(j, "on"));
                        eg.SetField(key, e);
                    }
                    SetEdge(tile, side);
                    int other = Board.Neighbour(tile, side);
                    if (other >= 0) SetEdge(other, Opposite(side));
                    State?.Set("eg", eg.Print());
                    return "true";
                }
            }
            Log("unknown op " + op);
            return "false";
        }

        string IRunnerDirectorHost.Get(string what)
        {
            switch (what)
            {
                case "levels":
                {
                    var arr = new JSONObject(JSONObject.Type.Array);
                    foreach (var l in _levels)
                    {
                        var o = new JSONObject();
                        o.AddField("id", l.id ?? ""); o.AddField("name", l.name ?? l.id ?? ""); o.AddField("tiles", l.Footprint);
                        arr.Add(o);
                    }
                    return arr.Print();
                }
                case "board": return BoardJson(false);
                case "time_left": return (_w.SessionTimeLeft?.Invoke() ?? -1).ToString(CultureInfo.InvariantCulture);
                case "starts_in": return StartsIn().ToString(CultureInfo.InvariantCulture);
                case "wait_total": return (_w.SessionWaitTotal?.Invoke() ?? 0).ToString(CultureInfo.InvariantCulture);
                case "zones": return ZonesJson();
                case "players": return PlayersJson();
            }
            if (what.StartsWith("zone_of:", StringComparison.Ordinal)) return ZoneOf(what.Substring(8));
            if (what.StartsWith("zone_members:", StringComparison.Ordinal))
            {
                var arr = new JSONObject(JSONObject.Type.Array);
                foreach (var m in ZoneMembers(what.Substring(13))) arr.Add(m);
                return arr.Print();
            }
            return null;
        }

        /// Board for Lua: {tiles, widthFt, depthFt, cols, rows, minutes, players, list = {{tile, level, done, players, load}}}
        /// (+ state = dp.flow.state for onResume).
        public string BoardJson(bool withState)
        {
            var o = new JSONObject();
            o.AddField("tiles", Board.Count);
            o.AddField("widthFt", Board.widthFt); o.AddField("depthFt", Board.depthFt);
            o.AddField("cols", Board.cols); o.AddField("rows", Board.rows);
            o.AddField("minutes", _w.SessionMinutes?.Invoke() ?? 0);
            o.AddField("startsInSec", Math.Round(StartsIn(), 1));
            var loads = MainLoads; var pres = Presence();
            o.AddField("players", pres.Count(kv => kv.Value >= 0));
            var list = new JSONObject(JSONObject.Type.Array);
            for (int i = 0; i < Board.Count; i++)
            {
                var t = new JSONObject();
                t.AddField("tile", i + 1);
                var l = RunnerBoardRules.At(loads, i);
                if (l != null) { t.AddField("level", l.level ?? ""); t.AddField("done", l.done); t.AddField("load", l.id); }
                t.AddField("players", PlayersIn(i, pres));
                list.Add(t);
            }
            o.AddField("list", list);
            var zs = RunnerBoardRules.Parse(Raw("zs"));
            if (zs?.keys != null && zs.keys.Count > 0) o.AddField("zones", RunnerBoardRules.Parse(ZonesJson()));
            if (withState) { var st = RunnerBoardRules.Parse(Raw("rs")); o.AddField("state", st ?? new JSONObject()); }
            return o.Print();
        }

        void DoLoad(RunnerLevel lv, int[] tiles, string nuTitle, string nuSub, bool force = false, string zone = null, JSONObject options = null)
        {
            int id = State != null ? (int)State.GetNumber("nid", 1) : (_soloNid++);
            var load = new RunnerLoad { id = id, level = lv.id, tiles = tiles, at = _w.Clock(), zone = string.IsNullOrEmpty(zone) ? null : zone,
                options = options != null && options.type == JSONObject.Type.Object && options.Print().Length <= 1024 ? options.Print() : null };
            var loads = RunnerBoardRules.Apply(Loads, load);
            if (State != null)
            {
                State.SetNumber("nid", id + 1);
                State.Set("ld", RunnerBoardRules.EncodeLoads(loads));
                if (load.InMain)
                {
                    // The director's edge looks belong to the SEAM and survive a level change on either side (WW proof, Oct 26:
                    // a doorway the director opened closed again after both sides swapped). A look leaves only when this load
                    // spans BOTH tiles of the seam (the seam is inside one level now) — or when the director closes it.
                    var eg = RunnerBoardRules.Parse(Raw("eg"));
                    if (eg?.keys != null)
                    {
                        bool dirty = false;
                        foreach (string k in eg.keys.ToList())
                        {
                            var (t, sd) = SplitEdgeKey(k);
                            int nb = t >= 0 ? Board.Neighbour(t, sd) : -1;
                            if (tiles.Contains(t) && nb >= 0 && tiles.Contains(nb)) { eg.RemoveField(k); dirty = true; }
                        }
                        if (dirty) State.Set("eg", eg.Print());
                    }
                    // …an Edge Link on a seam of those tiles leaves with the level it joined (and the open look it gave the seam)
                    var lk = RunnerBoardRules.Parse(Raw("lk"));
                    if (lk?.keys != null)
                    {
                        bool dirty = false;
                        foreach (string k in lk.keys.ToList())
                        {
                            var (t, sd) = SplitEdgeKey(k);
                            int nb = Board.Neighbour(t, sd);
                            if (tiles.Contains(t) || (nb >= 0 && tiles.Contains(nb))) { lk.RemoveField(k); dirty = true; DropLinkLooks(t, sd); }
                        }
                        if (dirty) { if (lk.keys.Count == 0) State.Delete("lk"); else State.Set("lk", lk.Print()); }
                    }
                    // …and a swap still waiting on a tile this load took is stale (WW proof: a pending swap survived a
                    // span = 'all' load and later replaced the boss)
                    var pend = Pending;
                    if (pend.RemoveAll(p => p.kind == RunnerPending.Swap && tiles.Contains(p.tile)) > 0)
                    {
                        State.Set("pq", RunnerBoardRules.EncodePending(pend));
                        Log($"load {lv.id}: a pending swap on tile(s) {Tiles1(tiles)} is dropped (this load took the tile)");
                    }
                }
            }
            else _solo = loads;
            Log($"load {lv.id} → {(load.InMain ? "" : "zone '" + load.zone + "' ")}tile(s) {Tiles1(tiles)} (footprint {lv.Footprint}){(nuTitle != null ? " with Next up '" + nuTitle + "'" : " (seamless)")}");
            double left = _w.SessionTimeLeft?.Invoke() ?? -1;
            if (nuTitle != null && State != null && load.InMain && (left < 0 || left > 1))
            {
                var nu = new JSONObject();
                nu.AddField("ti", nuTitle); nu.AddField("su", nuSub ?? "");
                double now = _w.Clock();
                nu.AddField("at", Math.Round(now, 3));
                nu.AddField("un", Math.Round(now + (force ? RunnerBoardRules.TransformRevealSeconds : RunnerBoardRules.NextUpSeconds), 3));
                var sc = RunnerBoardRules.Parse(Raw("sc"));
                if (sc != null) nu.AddField("res", sc);
                State.Set("nu", nu.Print());
                State.Delete("sc");   // the next stretch starts
            }
        }
        int _soloNid = 1;
        List<RunnerLoad> _solo;

        // ── The shared clock (dp.session.onClockStart) ─────────────────

        /// Seconds until the shared clock starts (> 0 the pile-in counts down, 0 started, -1 unknown = started).
        public double StartsIn()
        {
            double v = -1;
            try { v = _w.SessionStartsIn?.Invoke() ?? -1; } catch (Exception) { }
            return double.IsNaN(v) ? -1 : v;
        }

        /// onClockStart fires ONCE per board ("cs"): when the pile-in ends, or at once when the clock is already running /
        /// unknown. A new leader never refires it.
        void ClockStartHook()
        {
            if (_script == null || !Started) return;
            if (State != null ? State.GetNumber("cs") >= 1 : _soloClock) return;
            double left = StartsIn();
            if (left > 0) return;
            if (State != null) State.SetNumber("cs", 1); else _soloClock = true;
            Log($"the shared clock started{(left < 0 ? " (no pile-in known)" : "")}: onClockStart");
            _script.ClockStart();
        }
        bool _soloClock;

        // ── Zones (dp.zones, Oct 26 2026): players in different levels at once ──
        //
        // A zone is a group of players who see their own level(s) on the same physical tiles while everyone else stays on
        // the main board. Loads carry their zone ("zn"); every headset renders only its own zone's loads, so a level
        // (and its NetIds: every load is its own NetScope) exists only for that zone's players — their state sync,
        // trades and battles stay inside it. The map / hub level on the main board reads dp.zones.players().

        /// The zone this device's player is in (null = the main board).
        public string MyZone() => ZoneOfOrNull(_w.DeviceId());

        /// What a player of `zone` sees: the zone's own loads, plus the main board's levels in the tiles the zone has
        /// nothing in (so the hub / map stays in view) — unless the zone was opened alone = true.
        public List<RunnerLoad> SeenLoads(IList<RunnerLoad> all, string zone)
        {
            if (string.IsNullOrEmpty(zone)) return RunnerBoardRules.InZone(all, null);
            var own = RunnerBoardRules.InZone(all, zone);
            var zs = State != null ? RunnerBoardRules.Parse(Raw("zs")) : _soloZones;
            if (RunnerBoardRules.Bool(zs?.GetField(zone), "al")) return own;
            var taken = new HashSet<int>(); foreach (var l in own) foreach (int t in l.tiles) taken.Add(t);
            var seen = new List<RunnerLoad>(own);
            foreach (var l in RunnerBoardRules.InZone(all, null)) if (!l.tiles.Any(taken.Contains)) seen.Add(l);
            seen.Sort((a, b) => a.id.CompareTo(b.id));
            return seen;
        }

        /// The tiles a zone's own loads cover (a zone player standing outside them sees — and counts for — the main board).
        HashSet<int> ZoneTiles(List<RunnerLoad> all, string zone, JSONObject zs)
        {
            var set = new HashSet<int>();
            if (RunnerBoardRules.Bool(zs?.GetField(zone), "al")) { for (int i = 0; i < Board.Count; i++) set.Add(i); return set; }
            foreach (var l in all) if (RunnerBoardRules.SameZone(l.zone, zone)) foreach (int t in l.tiles) set.Add(t);
            return set;
        }

        string ZoneOfOrNull(string player)
        {
            if (State == null) return _soloZone;
            string z = RunnerBoardRules.Str(RunnerBoardRules.Parse(Raw("zm")), player ?? "");
            return string.IsNullOrEmpty(z) ? null : z;
        }
        string _soloZone;

        /// What gameplay Lua sees of the way to play for one load (dp.level.runner(go)): {id, name, content, tiles, level,
        /// load, span (1-based tiles), zone, done, options, state (a copy of dp.flow.state)}. loadId -1 = this headset's own
        /// standing load (else the only open one); null when there is none.
        public string RunnerInfoJson(int loadId)
        {
            var all = Loads;
            RunnerLoad l = loadId >= 0 ? all.FirstOrDefault(x => x.id == loadId) : null;
            if (l == null && loadId < 0)
            {
                var seen = SeenLoads(all, MyZone());
                var p = _w.LocalPos?.Invoke(this) ?? (false, 0, 0);
                int tile = p.ok ? Board.TileAt(p.x, p.z) : -1;
                l = seen.LastOrDefault(x => x.Covers(tile)) ?? (seen.Count(x => !x.done) == 1 ? seen.First(x => !x.done) : null);
            }
            var o = new JSONObject();
            o.AddField("id", Runner.runnerId ?? "");
            o.AddField("name", Runner.name ?? Runner.runnerId ?? "");
            o.AddField("content", ContentId);
            o.AddField("tiles", Board.Count);
            if (l != null)
            {
                o.AddField("level", l.level ?? "");
                o.AddField("load", l.id);
                var sp = new JSONObject(JSONObject.Type.Array); foreach (int t in l.tiles) sp.Add(t + 1);
                o.AddField("span", sp);
                o.AddField("zone", l.InMain ? "main" : l.zone);
                o.AddField("done", l.done);
                var op = RunnerBoardRules.Parse(l.options);
                o.AddField("options", op != null && op.type == JSONObject.Type.Object ? op : new JSONObject());
            }
            else if (loadId >= 0) return null;   // that object is not on this board
            var st = RunnerBoardRules.Parse(Raw("rs"));
            o.AddField("state", st != null && st.type == JSONObject.Type.Object ? st : new JSONObject());
            return o.Print();
        }

        /// The open / just-closed vote for gameplay (dp.board.vote()): {id, tag, options, zones = {{tile} | {x, z, r}},
        /// secondsLeft, winner (option or nil)}; null when there is none.
        public string VoteJson()
        {
            var v = VoteNow();
            if (v == null) return null;
            var o = new JSONObject();
            o.AddField("id", v.id ?? "");
            if (v.tag != null) o.AddField("tag", v.tag);
            var op = new JSONObject(JSONObject.Type.Array); foreach (var s in v.options) op.Add(s);
            o.AddField("options", op);
            var lb = new JSONObject(JSONObject.Type.Array); foreach (var s in v.DisplayLabels()) lb.Add(s);
            o.AddField("labels", lb);
            o.AddField("panel", v.panel);
            if (!string.IsNullOrEmpty(v.prompt)) o.AddField("prompt", v.prompt);
            var dsj = new JSONObject(JSONObject.Type.Array); for (int i = 0; i < v.options.Count; i++) dsj.Add(v.DescriptionOf(i));
            o.AddField("descriptions", dsj);
            var zn = new JSONObject(JSONObject.Type.Array);
            foreach (var z in v.zones)
            {
                var zo = new JSONObject();
                if (z.tile >= 0)
                {
                    zo.AddField("tile", z.tile + 1);
                    var t = Board.Tile(z.tile);
                    if (t != null) { zo.AddField("x", Math.Round(t.cx, 2)); zo.AddField("z", Math.Round(t.cz, 2)); zo.AddField("w", Math.Round(t.w, 2)); zo.AddField("d", Math.Round(t.d, 2)); }
                }
                else if (!float.IsNaN(z.x) && z.w > 0 && z.d > 0) { zo.AddField("x", z.x); zo.AddField("z", z.z); zo.AddField("w", z.w); zo.AddField("d", z.d); }
                else if (!float.IsNaN(z.x)) { zo.AddField("x", z.x); zo.AddField("z", z.z); zo.AddField("r", z.r); }
                zn.Add(zo);
            }
            o.AddField("zones", zn);
            o.AddField("secondsLeft", Math.Round(Math.Max(0, v.endsAt - _w.Clock()), 1));
            if (v.winner >= 0) { o.AddField("winner", v.winnerLabel ?? ""); o.AddField("winnerLabel", v.WinnerText ?? ""); }
            return o.Print();
        }

        /// This device's player id.
        public string DeviceId => _w.DeviceId();

        /// The player who runs a zone's own things: the lowest live player id in it (the same answer on every headset).
        public string ZoneLeader(string zone)
        {
            var m = ZoneMembers(zone);
            var live = new HashSet<string>(LivePlayers(), StringComparer.Ordinal);
            return m.Where(live.Contains).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        }

        /// A player's zone id, "main" on the main board.
        public string ZoneOf(string player) => ZoneOfOrNull(player) ?? "main";

        public List<string> ZoneMembers(string zone)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(zone) || zone == "main")
            {
                foreach (var id in LivePlayers()) if (ZoneOfOrNull(id) == null) l.Add(id);
                return l;
            }
            if (State == null) { if (_soloZone == zone) l.Add(_w.DeviceId()); return l; }
            var zm = RunnerBoardRules.Parse(Raw("zm"));
            if (zm?.keys != null) for (int i = 0; i < zm.keys.Count; i++) if (zm.list[i]?.stringValue == zone) l.Add(zm.keys[i]);
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        /// Every live player id (this device, the session's members, simulated / older peers seen by position).
        public List<string> LivePlayers()
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { _w.DeviceId() };
            if (State != null) foreach (var m in State.Members()) if (!string.IsNullOrEmpty(m)) set.Add(m);
            foreach (var r in Remotes()) if (!string.IsNullOrEmpty(r.id)) set.Add(r.id);
            var l = set.ToList(); l.Sort(StringComparer.Ordinal); return l;
        }

        /// A player's board-frame position: its own "ps" cell, else a remote position, else this device's pose.
        public (bool ok, float x, float z) PositionOf(string player)
        {
            if (player == _w.DeviceId()) { var p = _w.LocalPos?.Invoke(this) ?? (false, 0, 0); if (p.ok) return p; }
            var c = RunnerBoardRules.Parse(State?.MineOf("ps", player));
            if (c?.list != null && c.list.Count >= 2 && c.list[0] != null && c.list[0].isNumber && c.list[1] != null && c.list[1].isNumber)
                return (true, (float)c.list[0].doubleValue, (float)c.list[1].doubleValue);
            foreach (var r in Remotes()) if (r.id == player) return (true, r.x, r.z);
            return (false, 0, 0);
        }

        /// [{id, name, zone, x, z, tile}] every live player (x/z/tile absent when unknown) — the map level's markers.
        public string PlayersJson()
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            foreach (var id in LivePlayers())
            {
                var o = new JSONObject();
                o.AddField("id", id);
                string nm = id; try { nm = _w.PlayerName?.Invoke(id) ?? id; } catch (Exception) { }
                o.AddField("name", nm ?? id);
                o.AddField("zone", ZoneOf(id));
                var p = PositionOf(id);
                if (p.ok) { o.AddField("x", Math.Round(p.x, 2)); o.AddField("z", Math.Round(p.z, 2)); o.AddField("tile", Board.TileAt(p.x, p.z) + 1); }
                arr.Add(o);
            }
            return arr.Print();
        }

        /// [{id, name, level, capacity, count, locked, full}] the open zones.
        public string ZonesJson()
        {
            var arr = new JSONObject(JSONObject.Type.Array);
            var zs = RunnerBoardRules.Parse(Raw("zs"));
            if (zs?.keys == null) return arr.Print();
            var loads = Loads;
            for (int i = 0; i < zs.keys.Count; i++)
            {
                string id = zs.keys[i]; var z = zs.list[i];
                var o = new JSONObject();
                o.AddField("id", id);
                o.AddField("name", RunnerBoardRules.Str(z, "nm", id));
                var zl = RunnerBoardRules.InZone(loads, id);
                o.AddField("level", zl.Count > 0 ? zl[zl.Count - 1].level : "");
                int cap = (int)RunnerBoardRules.Num(z, "cap", 0), n = ZoneMembers(id).Count;
                o.AddField("capacity", cap);
                o.AddField("count", n);
                bool full = cap > 0 && n >= cap;
                o.AddField("full", full);
                o.AddField("locked", RunnerBoardRules.Bool(z, "lk") || full);
                arr.Add(o);
            }
            return arr.Print();
        }

        /// Why a player may not enter a zone now ("" = it may): unknown | locked | full.
        string RefuseWhy(JSONObject zs, string zone, string player)
        {
            var z = zs?.GetField(zone);
            if (z == null) return "unknown";
            if (RunnerBoardRules.Bool(z, "lk")) return "locked";
            int cap = (int)RunnerBoardRules.Num(z, "cap", 0);
            if (cap > 0 && ZoneMembers(zone).Count(m => m != player) >= cap) return "full";
            return "";
        }

        void SetZone(string player, string zone, string why)
        {
            string was = ZoneOfOrNull(player);
            string to = string.IsNullOrEmpty(zone) || zone == "main" ? null : zone;
            if (was == to) return;
            if (State == null) _soloZone = to;
            else
            {
                var zm = RunnerBoardRules.Parse(Raw("zm")) ?? new JSONObject();
                if (to == null) zm.RemoveField(player); else zm.SetField(player, to);
                if (zm.keys == null || zm.keys.Count == 0) State.Delete("zm"); else State.Set("zm", zm.Print());
            }
            Log($"zone: {player} {(was ?? "main")} → {(to ?? "main")} ({why})");
            if (was != null) { _script?.Fire("zleave", was, player, why); if (ZoneMembers(was).Count == 0) _script?.Fire("zempty", was); }
            _script?.Fire("zenter", to ?? "main", player);
        }

        string ZoneOp(string op, string a, JSONObject j)
        {
            var zs = RunnerBoardRules.Parse(Raw("zs")) ?? new JSONObject();
            string id = (a ?? "").Trim();
            switch (op)
            {
                case "zone_open":
                case "zone_load":
                {
                    if (id.Length == 0 || id == "main") { Log($"dp.zones.{(op == "zone_open" ? "open" : "load")}: a zone needs an id (not 'main')"); return "false"; }
                    if (op == "zone_load" && zs.GetField(id) == null) { Log($"dp.zones.load: no zone '{id}' (open it first)"); return "false"; }
                    string level = RunnerBoardRules.Str(j, "level");
                    var lv = string.IsNullOrEmpty(level) ? null : LevelById(level);
                    if (!string.IsNullOrEmpty(level) && lv == null) { Log($"dp.zones: no such level '{level}' in {ContentId}"); return "false"; }
                    if (op == "zone_open")
                    {
                        var z = zs.GetField(id) ?? new JSONObject();
                        z.SetField("nm", RunnerBoardRules.Str(j, "name", RunnerBoardRules.Str(z, "nm", id)));
                        if (j.GetField("capacity") != null) z.SetField("cap", (int)RunnerBoardRules.Num(j, "capacity", 0));
                        if (j.GetField("locked") != null) z.SetField("lk", RunnerBoardRules.Bool(j, "locked"));
                        if (j.GetField("alone") != null) z.SetField("al", RunnerBoardRules.Bool(j, "alone"));
                        zs.SetField(id, z);
                        State?.Set("zs", zs.Print());
                        if (State == null) _soloZones = zs;
                        Log($"zone '{id}' open (capacity {(int)RunnerBoardRules.Num(z, "cap", 0)})");
                    }
                    if (lv != null)
                    {
                        int tile = (int)RunnerBoardRules.Num(j, "tile", 0) - 1;
                        bool all = string.Equals(RunnerBoardRules.Str(j, "span"), "all", StringComparison.OrdinalIgnoreCase);
                        int[] tiles = RunnerBoardRules.Place(Board, lv.Footprint, tile, all);
                        DoLoad(lv, tiles, null, null, zone: id, options: j.GetField("options"));
                    }
                    return "true";
                }
                case "zone_close":
                {
                    if (zs.GetField(id) == null) return "false";
                    foreach (var m in ZoneMembers(id)) SetZone(m, null, "closed");
                    zs.RemoveField(id);
                    if (zs.keys == null || zs.keys.Count == 0) State?.Delete("zs"); else State?.Set("zs", zs.Print());
                    var rest = Loads.Where(l => !RunnerBoardRules.SameZone(l.zone, id)).ToList();
                    if (State != null) State.Set("ld", RunnerBoardRules.EncodeLoads(rest)); else _solo = rest;
                    var zd = RunnerBoardRules.Parse(Raw("zd"));
                    if (zd?.keys != null)
                    {
                        foreach (var k in zd.keys.ToList()) if (RunnerBoardRules.Str(zd.GetField(k), "z") == id || RunnerBoardRules.Str(zd.GetField(k), "fr") == id) zd.RemoveField(k);
                        if (zd.keys.Count == 0) State?.Delete("zd"); else State?.Set("zd", zd.Print());
                    }
                    Log($"zone '{id}' closed");
                    return "true";
                }
                case "zone_lock":
                {
                    var z = zs.GetField(id);
                    if (z == null) return "false";
                    z.SetField("lk", RunnerBoardRules.Bool(j, "on"));
                    State?.Set("zs", zs.Print());
                    return "true";
                }
                case "zone_send":
                {
                    string to = id.Length == 0 ? "main" : id;
                    var who = new List<string>();
                    var pl = j.GetField("players");
                    if (pl?.list != null) foreach (var x in pl.list) { string s = x?.type == JSONObject.Type.String ? x.stringValue : x?.Print(); if (!string.IsNullOrEmpty(s)) who.Add(s); }
                    int sent = 0;
                    foreach (var p in who)
                    {
                        if (to != "main")
                        {
                            if (ZoneOfOrNull(p) == to) { sent++; continue; }
                            string why = RefuseWhy(zs, to, p);
                            if (why.Length > 0) { Log($"zone '{to}': {p} refused ({why})"); _script?.Fire("zrefused", to, p, why); continue; }
                        }
                        SetZone(p, to, "sent");
                        sent++;
                    }
                    return sent.ToString(CultureInfo.InvariantCulture);
                }
                case "zone_door":
                {
                    string to = RunnerBoardRules.Str(j, "zone", "");
                    if (id.Length == 0 || to.Length == 0) { Log("dp.zones.door: needs an id and a zone"); return "false"; }
                    var zd = RunnerBoardRules.Parse(Raw("zd")) ?? new JSONObject();
                    var d = new JSONObject();
                    d.AddField("z", to);
                    d.AddField("fr", RunnerBoardRules.Str(j, "from", "main"));
                    double tile = RunnerBoardRules.Num(j, "tile", 0);
                    if (tile >= 1) d.AddField("t", (int)tile - 1);
                    else { d.AddField("x", RunnerBoardRules.Num(j, "x")); d.AddField("zz", RunnerBoardRules.Num(j, "z")); d.AddField("r", RunnerBoardRules.Num(j, "r", 1)); }
                    d.AddField("dw", Math.Max(0, RunnerBoardRules.Num(j, "dwell", 2)));
                    zd.SetField(id, d);
                    State?.Set("zd", zd.Print());
                    return "true";
                }
                case "zone_undoor":
                {
                    var zd = RunnerBoardRules.Parse(Raw("zd"));
                    if (zd?.GetField(id) == null) return "false";
                    zd.RemoveField(id);
                    if (zd.keys.Count == 0) State?.Delete("zd"); else State?.Set("zd", zd.Print());
                    return "true";
                }
            }
            return "false";
        }
        JSONObject _soloZones;

        /// Leader: players who left the session leave their zone; doors send whoever stood in them long enough.
        void ZoneTick(double now)
        {
            if (State == null) return;
            var zm = RunnerBoardRules.Parse(Raw("zm"));
            if (zm?.keys != null && zm.keys.Count > 0)
            {
                var live = new HashSet<string>(LivePlayers(), StringComparer.Ordinal);
                foreach (var p in zm.keys.ToList()) if (!live.Contains(p)) SetZone(p, null, "left");
            }
            var zd = RunnerBoardRules.Parse(Raw("zd"));
            if (zd?.keys == null || zd.keys.Count == 0) { _doorSeen.Clear(); return; }
            var zs = RunnerBoardRules.Parse(Raw("zs"));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in LivePlayers())
            {
                var pos = PositionOf(p);
                if (!pos.ok) continue;
                string from = ZoneOf(p);
                for (int i = 0; i < zd.keys.Count; i++)
                {
                    var d = zd.list[i];
                    string to = RunnerBoardRules.Str(d, "z", "");
                    if (to == from || RunnerBoardRules.Str(d, "fr", "main") != from) continue;
                    bool inside;
                    if (d.GetField("t") != null) inside = Board.TileAt(pos.x, pos.z) == (int)RunnerBoardRules.Num(d, "t", -1);
                    else
                    {
                        float dx = pos.x - (float)RunnerBoardRules.Num(d, "x"), dz = pos.z - (float)RunnerBoardRules.Num(d, "zz"), r = (float)RunnerBoardRules.Num(d, "r", 1);
                        inside = dx * dx + dz * dz <= r * r;
                    }
                    if (!inside) continue;
                    string key = p + "|" + zd.keys[i];
                    seen.Add(key);
                    if (!_doorSeen.TryGetValue(key, out double since)) { _doorSeen[key] = now; since = now; }
                    if (now - since < RunnerBoardRules.Num(d, "dw", 2)) continue;
                    if (to != "main")
                    {
                        string why = RefuseWhy(zs, to, p);
                        if (why.Length > 0)
                        {
                            if (!_doorRefused.Contains(key)) { _doorRefused.Add(key); Log($"door {zd.keys[i]}: {p} refused ({why})"); _script?.Fire("zrefused", to, p, why); }
                            continue;
                        }
                    }
                    _doorRefused.Remove(key);
                    SetZone(p, to, "door " + zd.keys[i]);
                    break;
                }
            }
            foreach (var k in _doorSeen.Keys.ToList()) if (!seen.Contains(k)) { _doorSeen.Remove(k); _doorRefused.Remove(k); }
        }
        readonly Dictionary<string, double> _doorSeen = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly HashSet<string> _doorRefused = new HashSet<string>(StringComparer.Ordinal);

        // ── Render (every device) ──────────────────────────────────────

        void Render(double now)
        {
            if (State != null && !TableIsMine() && BoardMeta() != null)
            {
                if (!_warnedOther) { _warnedOther = true; Log($"the board belongs to {Raw("b")} — not rendered here (this headset runs {ContentId}/{Runner.runnerId})"); }
                return;
            }
            _warnedOther = false;
            // this device renders ITS zone's loads (the main board unless the director sent its player into a zone)
            string mine = MyZone();
            var loads = SeenLoads(Loads, mine);
            if (mine != _zoneShown) { if (_zoneShown != null || mine != null) Log($"this headset is in {(mine == null ? "the main board" : "zone '" + mine + "'")} now"); _zoneShown = mine; }
            var want = new Dictionary<int, RunnerLoad>();
            foreach (var l in loads) want[l.id] = l;
            foreach (var id in _rendered.Keys.ToList())
                if (!want.ContainsKey(id)) { Unrender(_rendered[id]); _rendered.Remove(id); }
            foreach (var l in loads)
            {
                if (_rendered.TryGetValue(l.id, out var have)) { have.done = l.done; have.result = l.result; continue; }
                var lv = LevelById(l.level);
                _rendered[l.id] = l.Clone();
                Safe(() => _w.Place?.Invoke(this, l, lv));
            }
            RenderEdges(loads, mine == null);
            RenderLinks(mine == null ? loads : new List<RunnerLoad>());
            var nu = NextUpNow();
            string nk = nu?.Key;
            if (nk != _nuShown) { _nuShown = nk; Safe(() => _w.NextUp?.Invoke(this, nu)); }
            var v = VoteNow();
            string vk = v?.Key;
            if (vk != _voteShown) { _voteShown = vk; Safe(() => _w.Vote?.Invoke(this, v)); }
        }

        void Unrender(RunnerLoad l) => Safe(() => _w.Remove?.Invoke(this, l));

        void RenderEdges(List<RunnerLoad> loads, bool main = true)
        {
            var eg = main ? RunnerBoardRules.Parse(Raw("eg")) : null;   // director looks dress the main board only
            foreach (var (a, sa, b, sb) in Board.InnerEdges())
            {
                var ea = eg?.GetField(a + ":" + sa); var eb = eg?.GetField(b + ":" + sb);
                string dl = RunnerBoardRules.Str(ea, "lk") ?? RunnerBoardRules.Str(eb, "lk");
                bool locked = RunnerBoardRules.Bool(ea, "lo") || RunnerBoardRules.Bool(eb, "lo");
                string look = RunnerBoardRules.EdgeLook(loads, a, b, dl);
                EdgeOut(loads, a, sa, look, locked);
                EdgeOut(loads, b, sb, look, locked);
            }
        }

        void EdgeOut(List<RunnerLoad> loads, int tile, string side, string look, bool locked)
        {
            var l = RunnerBoardRules.At(loads, tile);
            string key = tile + ":" + side;
            string sig = (l?.id ?? -1) + "|" + look;
            if (_edges.TryGetValue(key, out var have) && have.look == sig && have.locked == locked) return;
            _edges[key] = (sig, locked);
            if (l == null) return;
            Safe(() => _w.Edge?.Invoke(this, tile, side, look, locked));
        }

        /// The edge looks rendered here ("tile:side" → look/locked), for checks.
        public Dictionary<string, (string look, bool locked)> EdgeLooks()
        {
            var d = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            foreach (var kv in _edges) d[kv.Key] = (kv.Value.look.Substring(kv.Value.look.IndexOf('|') + 1), kv.Value.locked);
            return d;
        }

        // ── Edge Links (dp.edges.link) ─────────────────────────────────

        static (int tile, string side) SplitEdgeKey(string k)
        {
            int c = k?.IndexOf(':') ?? -1;
            if (c <= 0 || !int.TryParse(k.Substring(0, c), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)) return (-1, "");
            return (t, RunnerBoardRules.NormSide(k.Substring(c + 1)));
        }

        bool LevelIs(RunnerLoad l, string name)
        {
            if (l == null || string.IsNullOrEmpty(name)) return false;
            var lv = LevelById(name);
            return lv != null ? string.Equals(lv.id, l.level, StringComparison.Ordinal) : string.Equals(l.level, name, StringComparison.OrdinalIgnoreCase);
        }

        /// The seam (level A's tile + side) between two levels standing in neighbouring tiles, else (-1, "").
        public (int tile, string side) SeamBetween(string levelA, string levelB)
        {
            var loads = MainLoads;
            foreach (var (a, sa, b, sb) in Board.InnerEdges())
            {
                var la = RunnerBoardRules.At(loads, a); var lb = RunnerBoardRules.At(loads, b);
                if (la == null || lb == null || la == lb) continue;
                if (LevelIs(la, levelA) && LevelIs(lb, levelB)) return (a, sa);
                if (LevelIs(la, levelB) && LevelIs(lb, levelA)) return (b, sb);
            }
            return (-1, "");
        }

        /// dp.edges.link(tile, side, link) / dp.edges.link{a=, b=, link=} / dp.edges.unlink(tile, side): an Edge Link
        /// prefab spawned on the seam on every headset; both sides of the seam open (unless the director gave them a
        /// look already). It leaves with either level (a board change on one of its tiles).
        bool LinkOp(bool on, JSONObject j)
        {
            string a = RunnerBoardRules.Str(j, "a"), b = RunnerBoardRules.Str(j, "b"), name = RunnerBoardRules.Str(j, "link", "");
            int tile; string side;
            if (on && !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
            {
                (tile, side) = SeamBetween(a, b);
                if (tile < 0) { Log($"dp.edges.link: {a} and {b} do not stand in neighbouring tiles ({Describe()})"); return false; }
            }
            else
            {
                tile = (int)RunnerBoardRules.Num(j, "tile", 0) - 1;
                side = RunnerBoardRules.NormSide(RunnerBoardRules.Str(j, "side"));
            }
            int other = Board.Neighbour(tile, side);
            if (Board.Tile(tile) == null || side == "" || other < 0) { Log($"dp.edges.{(on ? "link" : "unlink")}: tile {tile + 1} side '{side}' is not a seam on this board"); return false; }
            var lk = RunnerBoardRules.Parse(Raw("lk")) ?? new JSONObject();
            string key = tile + ":" + side, mirror = other + ":" + Opposite(side);
            bool had = lk.GetField(key) != null || lk.GetField(mirror) != null;
            lk.RemoveField(key); lk.RemoveField(mirror);
            if (on)
            {
                if (string.IsNullOrWhiteSpace(name)) { Log("dp.edges.link: no link prefab named"); return false; }
                var loads = MainLoads;
                var la = RunnerBoardRules.At(loads, tile); var lb = RunnerBoardRules.At(loads, other);
                if (la == null || lb == null || la == lb) { Log($"dp.edges.link: the seam tile {tile + 1} {side} needs a level on each side ({Describe()})"); return false; }
                var e = new JSONObject();
                e.AddField("n", name.Trim()); e.AddField("la", la.id); e.AddField("lb", lb.id);
                lk.SetField(key, e);
                // the link is the doorway: both sockets open (keep a look the director already chose)
                var eg = RunnerBoardRules.Parse(Raw("eg")) ?? new JSONObject();
                foreach (var k in new[] { key, mirror })
                {
                    var x = eg.GetField(k) ?? new JSONObject();
                    if (RunnerBoardRules.Str(x, "lk") == null) { x.SetField("lk", "open"); x.SetField("ln", true); }   // the link's own look
                    eg.SetField(k, x);
                }
                State?.Set("eg", eg.Print());
                Log($"edge link '{name}' on the seam tile {tile + 1} {side} ⇄ tile {other + 1} ({la.level} ⇄ {lb.level})");
            }
            else if (!had) return false;
            else DropLinkLooks(tile, side);
            if (State != null) { if (lk.keys == null || lk.keys.Count == 0) State.Delete("lk"); else State.Set("lk", lk.Print()); }
            return true;
        }

        /// The open looks a link gave both sides of its seam leave with it (a look the director set itself stays).
        void DropLinkLooks(int tile, string side)
        {
            var eg = RunnerBoardRules.Parse(Raw("eg"));
            if (eg?.keys == null) return;
            int other = Board.Neighbour(tile, side);
            bool dirty = false;
            foreach (var k in new[] { tile + ":" + side, other + ":" + Opposite(side) })
                if (RunnerBoardRules.Bool(eg.GetField(k), "ln")) { eg.RemoveField(k); dirty = true; }
            if (dirty) State?.Set("eg", eg.Print());
        }

        void RenderLinks(List<RunnerLoad> loads)
        {
            var lk = RunnerBoardRules.Parse(Raw("lk"));
            var want = new Dictionary<string, string>(StringComparer.Ordinal);
            if (lk?.keys != null)
                for (int i = 0; i < lk.keys.Count; i++)
                {
                    var (t, sd) = SplitEdgeKey(lk.keys[i]);
                    int nb = Board.Neighbour(t, sd);
                    var e = lk.list[i];
                    var la = RunnerBoardRules.At(loads, t); var lb = nb >= 0 ? RunnerBoardRules.At(loads, nb) : null;
                    // only while BOTH levels it joined still stand there
                    if (la == null || lb == null || la.id != (int)RunnerBoardRules.Num(e, "la", -1) || lb.id != (int)RunnerBoardRules.Num(e, "lb", -1)) continue;
                    string n = RunnerBoardRules.Str(e, "n", "");
                    if (n.Length > 0) want[lk.keys[i]] = n;
                }
            foreach (var kv in _links.ToList())
                if (!want.TryGetValue(kv.Key, out var n) || n != kv.Value) { _links.Remove(kv.Key); LinkOut(kv.Key, kv.Value, false); }
            foreach (var kv in want)
                if (!_links.ContainsKey(kv.Key)) { _links[kv.Key] = kv.Value; LinkOut(kv.Key, kv.Value, true); }
        }

        void LinkOut(string key, string link, bool on)
        {
            var (t, sd) = SplitEdgeKey(key);
            Safe(() => _w.Link?.Invoke(this, t, sd, link, on));
        }

        /// The links rendered here ("t:side" → link), for checks.
        public Dictionary<string, string> LinksShown() => new Dictionary<string, string>(_links, StringComparer.Ordinal);

        public static string Opposite(string side)
        {
            switch (RunnerBoardRules.NormSide(side)) { case "left": return "right"; case "right": return "left"; case "front": return "back"; case "back": return "front"; }
            return "";
        }

        static string Tiles1(int[] t) => string.Join("+", t.Select(x => (x + 1).ToString(CultureInfo.InvariantCulture)));
        static string Trunc(string s) => s == null ? "{}" : s.Length > 80 ? s.Substring(0, 80) + "…" : s;

        /// A short human line of the board ("1:A_Arena(done) 2:A_Lobby | nu 'World 2' | vote v1"), for logs and checks.
        public string Describe()
        {
            var all = Loads;
            var loads = RunnerBoardRules.InZone(all, null);
            var b = new StringBuilder();
            for (int i = 0; i < Board.Count; i++)
            {
                var l = RunnerBoardRules.At(loads, i);
                b.Append(i + 1).Append(':').Append(l == null ? "-" : l.level + (l.done ? "(done)" : "")).Append(' ');
            }
            var nu = NextUpNow(); if (nu != null) b.Append("| nu '").Append(nu.title).Append("' ");
            var v = VoteNow(); if (v != null) b.Append("| vote ").Append(v.id).Append(v.winner >= 0 ? " → " + v.winnerLabel : " open");
            foreach (var zl in all.Where(l => !l.InMain).GroupBy(l => l.zone))
                b.Append("| zone ").Append(zl.Key).Append(": ").Append(string.Join(" ", zl.Select(l => l.level + "@" + Tiles1(l.tiles) + (l.done ? "(done)" : ""))))
                 .Append(" [").Append(string.Join(",", ZoneMembers(zl.Key))).Append("] ");
            return b.ToString().Trim();
        }
    }
}
