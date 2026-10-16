// ─────────────────────────────────────────────────────────────────────
//  ParkSimPeerScenario.cs — scripted multiplayer scenarios over simulated peers
//
//  A scenario is JSON: params + a list of steps. Steps either DO something
//  (add a peer, make it send, crash it, run Lua on the local game) or EXPECT
//  something (Lua state, what a peer received, who leads, the send budget),
//  optionally polling `within` seconds. The run writes a pass/fail log to
//  Logs/DreamParkPeers/<time>_<name>/ (result.json + log.txt).
//
//    {
//      "name": "late_join",
//      "params": { "target": "", "settle": 3 },          // defaults; callers override
//      "steps": [
//        { "do": "peer_add", "name": "B" },
//        { "expect": "local_peers", "count": 1, "within": 8 },
//        { "do": "local_lua", "target": "${target}", "code": "${start}", "if": "${start}" },
//        { "do": "wait", "seconds": "${settle}" },
//        { "do": "mark", "name": "join" },
//        { "do": "peer_add", "name": "C" },
//        { "expect": "recv", "peer": "C", "target": "${target}", "since": "join", "within": 3, "if": "${target}" }
//      ]
//    }
//
//  "${p}" is replaced from params (a whole-string "${p}" keeps the param's
//  JSON type). A step with "if" is skipped when that resolves empty/false/0.
//
//  DO steps
//    peer_add      name, rank high|low, id, device headset|phone, game <gameId>, brain platform|wire, leader_capable,
//                  near "local"|path, at [x,y,z] park-local, yaw, sway
//    misalign      yaw (35), offset [x,y,z]          make the Editor's world ≠ the park frame, as on every headset
//    peer_remove   name, mode quit|crash
//    peer_link     name, up true|false              Wi-Fi drop: nothing in or out while down
//    peer_move     name, near|at, yaw
//    peer_walk     name, near|at, speed             (non-blocking; follow with wait)
//    peer_hand     name, side left|right, near|at|null   (null = back to the body)
//    peer_send     name, target path|netId, kind, body {..}|raw "json"   = net_send from that player ("@me" → its id)
//    peer_state    name, state {..}                 = dp.set_state on that player
//    peer_request  name, state, kind, body           s.request(kind, body) from that player's replica (leader applies)
//    peer_set      name, state, key, value           s.set(key, value) from that player's replica
//    local_rejoin  seconds (0.5)                     the Editor's session leaves and rejoins as the room's newest member
//    peer_push     name, target (a dp.net_body prop), by [x,y,z] (attraction frame), seconds   = that player's hand pushing it
//    local_lua     target path (optional), script, code           runs in that script's scope (global env without target)
//    wait          seconds
//    mark          name                             a time stamp for "since"
//    together      steps [..]                       several DO steps in the same frame
//    repeat        times, every, steps [..]         DO steps on a cadence (non-blocking)
//    log           msg
//
//  EXPECT steps (all take "within": seconds to poll, default 0 = now; "hold": seconds it must stay true)
//    lua           target, script, code, equals|not_equals|gte|lte|truthy|falsy|contains
//    recv          peer, kind ("*" any but presence), target, since, count (min), max,
//                  near "local"|peer|path + radius: the last message's position must be there (park-local)
//                  With kind "*" (or "__ss") a target's dp.shared_state traffic counts too: "__ss"
//                  snapshots/merges/writes/requests on the target's NetId or its state bus
//                  (SharedState.BusKeysFor), so a game fully on dp.shared_state passes.
//    no_recv       peer, kind, target, since
//    sees          peer, who "local"|peerName        the peer received that player's presence
//    local_peers   count                             PlayerPresence on the Editor sees exactly N peers
//    leader        is "local"|peerName, target (title-scoped dp.leader() in that script)   and every platform peer agrees
//    state_agrees  peer, name                        every dp.shared_state the Editor holds is synced + identical on the peers
//    budget        since, max_rate (60), max_drops (0)
//    net_body      target, owner "any"|"none"|"local"|peerName, max_speed (3)   a dp.net_body prop's owner and calm
//    no_errors     since, ignore [..substrings]
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using XLua;

namespace DreamPark.ParkSim
{
    public sealed class SimPeerScenario
    {
        public const string LogRoot = "Logs/DreamParkPeers";

        public string Name;
        public string Description;
        public JObject Params = new JObject();
        public JArray Steps = new JArray();

        // ── Run state ────────────────────────────────────────────────
        public static SimPeerScenario Current { get; private set; }
        public enum State { Running, Passed, Failed, Aborted }
        public State Status;
        public string RunDir;
        public double StartedAt;
        public double FinishedAt;
        readonly List<JObject> _results = new List<JObject>();
        readonly List<string> _log = new List<string>();
        readonly Dictionary<string, double> _marks = new Dictionary<string, double>();
        readonly Dictionary<string, int> _dropMarks = new Dictionary<string, int>();
        readonly List<string> _errors = new List<string>();
        readonly List<double> _errorTimes = new List<double>();
        int _pc;
        double _waitUntil = -1;
        double _expectDeadline = -1;
        double _holdSince = -1;
        string _lastExpectDetail;
        readonly List<Repeater> _repeaters = new List<Repeater>();
        Action<string> _onFinished;

        sealed class Repeater { public JArray steps; public int left; public double every; public double next; }
        readonly List<KeyValuePair<double, Action>> _later = new List<KeyValuePair<double, Action>>();
        bool _resumeArenaAuto;

        public int Passed => _results.Count(r => (string)r["result"] == "pass");
        public int FailedCount => _results.Count(r => (string)r["result"] == "fail");
        public int Skipped => _results.Count(r => (string)r["result"] == "skip");

        // ── Loading ──────────────────────────────────────────────────
        public static SimPeerScenario Parse(string json, JObject overrides = null)
        {
            var o = JObject.Parse(json);
            var s = new SimPeerScenario
            {
                Name = (string)o["name"] ?? "scenario",
                Description = (string)o["description"] ?? "",
                Params = o["params"] as JObject ?? new JObject(),
                Steps = o["steps"] as JArray ?? new JArray(),
            };
            if (overrides != null)
                foreach (var kv in overrides) s.Params[kv.Key] = kv.Value;
            // "bus": where the game's own messages travel. Usually the manager itself (target);
            // games with a separate bus object (e.g. a Player.prefab rig bus) set it explicitly.
            if (string.IsNullOrEmpty((string)s.Params["bus"])) s.Params["bus"] = s.Params["target"] ?? "";
            return s;
        }

        /// A built-in scenario name, a .json path, or inline JSON.
        public static SimPeerScenario Load(string nameOrPathOrJson, JObject overrides, out string error)
        {
            error = null;
            try
            {
                string src = nameOrPathOrJson?.Trim() ?? "";
                if (src.StartsWith("{")) return Parse(src, overrides);
                if (SimPeerBuiltins.All.TryGetValue(src, out var builtin)) return Parse(builtin, overrides);
                if (File.Exists(src)) return Parse(File.ReadAllText(src), overrides);
                error = $"Unknown scenario '{src}'. Built in: {string.Join(", ", SimPeerBuiltins.All.Keys)}; or pass a .json path or inline JSON.";
            }
            catch (Exception e) { error = "Scenario JSON: " + e.Message; }
            return null;
        }

        // ── Lifecycle ────────────────────────────────────────────────
        public static string Run(SimPeerScenario s, Action<string> onFinished = null)
        {
            if (!EditorApplication.isPlaying) return "Not in Play mode.";
            if (Current != null && Current.Status == State.Running) return $"Scenario '{Current.Name}' is still running.";
            string err = SimPeers.Start();
            if (err != null) return err;
            s._onFinished = onFinished;
            s.Status = State.Running;
            s.StartedAt = SimPeers.Now;
            s._marks["start"] = s.StartedAt;
            s._dropMarks["start"] = SimPeers.Relay?.RateLimitedMessageCount ?? 0;
            s.RunDir = Path.Combine(LogRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Sanitize(s.Name));
            Application.logMessageReceived += s.OnLog;
            Current = s;
#if !DREAMPARKCORE
            // An Arena package preview regenerates the park on a timer (cycling bucket sizes),
            // which would replace the game mid-scenario. Hold the current size while it runs.
            if (ParkSimPackageTest.IsArenaPreview && ParkSimPackageTest.ArenaAutoPlaying)
            {
                ParkSimPackageTest.ToggleArenaAuto();
                s._resumeArenaAuto = true;
            }
#endif
            s.Note($"▶ {s.Name}: {s.Description}");
            s.Note("params " + s.Params.ToString(Newtonsoft.Json.Formatting.None));
            return null;
        }

        /// Run one DO step immediately (CLI / window). Returns an error or null; note = what happened.
        public static string DoOnce(JObject step, out string note)
        {
            note = null;
            if (!EditorApplication.isPlaying) return "Not in Play mode.";
            string err = SimPeers.Start();
            if (err != null) return err;
            var s = new SimPeerScenario { Name = "act", StartedAt = SimPeers.Now, Status = State.Running };
            if (Current != null) foreach (var kv in Current._marks) s._marks[kv.Key] = kv.Value;
            s.Do((JObject)s.Resolve(step));
            var fail = s._results.FirstOrDefault(r => (string)r["result"] == "fail");
            note = string.Join(" | ", s._log);
            return fail != null ? (string)fail["detail"] : null;
        }

        public static void AbortRunning(string why)
        {
            var c = Current;
            if (c != null && c.Status == State.Running) c.Finish(State.Aborted, why);
        }

        void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception
                && !(msg.Contains("[LuaBehaviour]") && msg.Contains("threw"))
                && !msg.Contains("LuaException")) return;
            if (msg.StartsWith(SimPeers.Tag)) return;
            _errors.Add(msg.Length > 400 ? msg.Substring(0, 400) : msg);
            _errorTimes.Add(SimPeers.Now);
        }

        internal static void Tick()
        {
            var s = Current;
            if (s == null || s.Status != State.Running) return;
            try { s.Step(); }
            catch (Exception e) { s.Finish(State.Failed, "runner exception: " + e.Message); }
        }

        void Step()
        {
            double now = SimPeers.Now;
            for (int i = _later.Count - 1; i >= 0; i--)
                if (now >= _later[i].Key) { var a = _later[i].Value; _later.RemoveAt(i); try { a(); } catch (Exception e) { Note("delayed action: " + e.Message); } }
            foreach (var r in _repeaters.ToArray())
            {
                if (now < r.next) continue;
                r.next = now + r.every;
                foreach (var st in r.steps) Do((JObject)Resolve(st));
                if (--r.left <= 0) _repeaters.Remove(r);
            }

            for (int guard = 0; guard < 64; guard++)
            {
                if (_waitUntil > 0) { if (now < _waitUntil) return; _waitUntil = -1; _pc++; }
                if (_pc >= Steps.Count) { Finish(FailedCount > 0 ? State.Failed : State.Passed, null); return; }

                var step = (JObject)Resolve(Steps[_pc]);
                if (step.ContainsKey("if") && !Truthy(step["if"]))
                {
                    Record(step, "skip", "condition empty");
                    _pc++; continue;
                }

                if (step.ContainsKey("expect"))
                {
                    if (!Expect(step, now)) return;   // still polling
                    _pc++; continue;
                }

                string op = (string)step["do"] ?? "";
                if (op == "wait") { _waitUntil = now + Num(step["seconds"], 1); Note($"wait {Num(step["seconds"], 1):0.##} s"); return; }
                Do(step);
                _pc++;
            }
        }

        // ── DO ───────────────────────────────────────────────────────
        void Do(JObject st)
        {
            // "if" is honoured here too, so steps nested in together/repeat can be conditional.
            if (st.ContainsKey("if") && !Truthy(st["if"])) { Record(st, "skip", "condition empty"); return; }
            string op = (string)st["do"] ?? "";
            string name = (string)st["name"];
            string err = null;
            switch (op)
            {
                case "peer_add":
                {
                    var o = new SimPeerOptions
                    {
                        name = name, id = (string)st["id"], device = (string)st["device"] ?? "headset",
                        rank = string.Equals((string)st["rank"], "low", StringComparison.OrdinalIgnoreCase) ? SimPeerRank.Low : SimPeerRank.High,
                        near = (string)st["near"], at = Vec(st["at"]), yaw = st["yaw"] != null ? (float?)Num(st["yaw"], 0) : null,
                        sway = st["sway"] == null || Truthy(st["sway"]),
                        gameId = (string)st["game"],
                        platform = st["brain"] == null || (string)st["brain"] != "wire",
                    };
                    // A wire-only peer cannot do a leader's job: only "rank":"low" (or an explicit
                    // "leader_capable": true) makes one that can win the election.
                    o.leaderCapable = st["leader_capable"] != null ? Truthy(st["leader_capable"]) : o.rank == SimPeerRank.Low;
                    var p = SimPeers.Add(o, out err);
                    if (p != null) Note($"peer_add {p.Name} id {p.Id}");
                    break;
                }
                case "peer_remove":
                    if (SimPeers.Remove(name, (string)st["mode"] ?? "quit", out err)) Note($"peer_remove {name} {(string)st["mode"] ?? "quit"}");
                    break;
                case "peer_link":
                {
                    var p = Peer(name, ref err);
                    if (p != null) { p.LinkUp = Truthy(st["up"]); Note($"peer_link {name} {(p.LinkUp ? "UP" : "DOWN")}"); }
                    break;
                }
                case "peer_move":
                case "peer_walk":
                {
                    var p = Peer(name, ref err);
                    if (p == null) break;
                    var spot = Spot(st, p);
                    if (op == "peer_move") { p.StopWalking(); p.HeadPos = spot; if (st["yaw"] != null) p.Yaw = Num(st["yaw"], 0); }
                    else p.WalkTo(spot, Num(st["speed"], 1.2f));
                    Note($"{op} {name} → park {SimPeers.Fmt(spot)}");
                    break;
                }
                case "peer_hand":
                {
                    var p = Peer(name, ref err);
                    if (p == null) break;
                    bool left = string.Equals((string)st["side"], "left", StringComparison.OrdinalIgnoreCase);
                    Vector3? pos = (st["near"] == null && st["at"] == null) ? (Vector3?)null : Spot(st, p, keepHeight: false);
                    if (left) p.LeftHand = pos; else p.RightHand = pos;
                    Note($"peer_hand {name} {(left ? "left" : "right")} → {(pos.HasValue ? SimPeers.Fmt(pos.Value) : "body")}");
                    break;
                }
                case "peer_send":
                {
                    var p = Peer(name, ref err);
                    if (p == null) break;
                    string kind = (string)st["kind"] ?? "msg";
                    string body = st["raw"] != null ? (string)st["raw"] : (st["body"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
                    body = body.Replace("\"@me\"", "\"" + p.Id + "\"");   // "@me" = that player's presence id
                    string target = st["target"]?.ToString();
                    if (string.IsNullOrEmpty(target))
                    {
                        if (!p.RawSend(kind, body)) err = $"{name} is not connected";
                        else Note($"peer_send {name} '{kind}' (global) {Trim(body)}");
                    }
                    else if (SimPeers.ResolveNetId(target, out uint id, out err))
                    {
                        if (!p.SendToNetId(id, kind, body)) err = $"{name} is not connected (or its link is down)";
                        else Note($"peer_send {name} '{kind}' → {target} (NetId {id}) {Trim(body)}");
                    }
                    break;
                }
                case "peer_request":
                case "peer_set":
                {
                    // Through the peer's own SharedState replica, in the real "__ss" wire format:
                    // what that player's copy of the game does with s.request(kind, payload) / s.set(k, v).
                    var p = Peer(name, ref err);
                    if (p == null) break;
                    string stateName = (string)st["state"];
                    var r = p.Replicas.FirstOrDefault(x => stateName == null || x.Name == stateName);
                    if (r == null) { err = $"{name} holds no shared state{(stateName != null ? " '" + stateName + "'" : "")} (does the game call dp.shared_state? is {name} a platform peer?)"; break; }
                    if (op == "peer_request")
                    {
                        string kind = (string)st["kind"] ?? "request";
                        string body = (st["body"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
                        r.Request(kind, body);
                        Note($"peer_request {name} → state '{r.Name}' '{kind}' {Trim(body)}");
                    }
                    else
                    {
                        string key = (string)st["key"];
                        string json = (st["value"] ?? JValue.CreateNull()).ToString(Newtonsoft.Json.Formatting.None);
                        r.Set(key, json == "null" ? null : json);
                        Note($"peer_set {name} → state '{r.Name}' {key} = {json}");
                    }
                    break;
                }
                case "peer_push":
                {
                    var p = Peer(name, ref err);
                    if (p == null) break;
                    var go = SimPeers.FindObject((string)st["target"]);
                    var body = SimPeers.NetBodyOf(go);
                    if (body == null) { err = $"'{(string)st["target"]}' is not a dp.net_body prop (or this SDK has no NetBodies)"; break; }
                    if (body.netId == 0) { err = "the body's NetId has not registered yet"; break; }
                    var by = Vec(st["by"]) ?? new Vector3(0.3f, 0, 0);
                    p.PushBody(body.netId, body.rev, body.local, body.localRot, by, Num(st["seconds"], 1f));
                    Note($"peer_push {name} → {(string)st["target"]} by {SimPeers.Fmt(by)} (claims rev {body.rev + 1})");
                    break;
                }
                case "peer_state":
                {
                    var p = Peer(name, ref err);
                    if (p != null) { p.SetState((st["state"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None)); Note($"peer_state {name}"); }
                    break;
                }
                case "local_lua":
                {
                    string code = (string)st["code"] ?? "";
                    if (string.IsNullOrWhiteSpace(code)) break;
                    var r = LuaEval((string)st["target"], (string)st["script"], code, out err);
                    if (err == null) Note($"local_lua {(string)st["target"] ?? "(global)"}: {code} → {Show(r)}");
                    break;
                }
                case "misalign":
                {
                    float yaw = Num(st["yaw"], 35f);
                    if (Math.Abs(yaw) < 0.01f && Vec(st["offset"]) == null) { Note("misalign: off"); break; }
                    err = SimPeers.Misalign(yaw, Vec(st["offset"]) ?? new Vector3(1.5f, 0, -2f));
                    if (err == null) Note($"misalign: world ≠ park (yaw {yaw:0}°)");
                    break;
                }
                case "local_rejoin":
                {
                    // The Editor's own session leaves (as a doffed headset does) and comes back as the
                    // room's NEWEST member: its shared states must resync from the peers' replicas.
                    double away = Num(st["seconds"], 0.5f);
                    DreamParkSession.Engine.Pause(true);
                    _later.Add(new KeyValuePair<double, Action>(SimPeers.Now + away, () => DreamParkSession.Engine.Pause(false)));
                    Note($"local_rejoin: the Editor's session left; back as the newest member in {away:0.##} s");
                    break;
                }
                case "mark":
                    _marks[name ?? "mark"] = SimPeers.Now;
                    _dropMarks[name ?? "mark"] = SimPeers.Relay?.RateLimitedMessageCount ?? 0;
                    Note($"mark '{name}'");
                    break;
                case "together":
                    foreach (var sub in (st["steps"] as JArray) ?? new JArray()) Do((JObject)sub);
                    break;
                case "repeat":
                    _repeaters.Add(new Repeater { steps = (st["steps"] as JArray) ?? new JArray(), left = (int)Num(st["times"], 1), every = Num(st["every"], 1), next = SimPeers.Now });
                    Note($"repeat ×{(int)Num(st["times"], 1)} every {Num(st["every"], 1):0.##} s");
                    break;
                case "log":
                    Note("· " + (string)st["msg"]);
                    break;
                default:
                    err = $"unknown step '{op}'";
                    break;
            }
            if (err != null) Record(st, "fail", err);
        }

        // ── EXPECT ───────────────────────────────────────────────────
        /// Returns true when the step is finished (passed, failed or skipped).
        bool Expect(JObject st, double now)
        {
            if (_expectDeadline < 0) _expectDeadline = now + Num(st["within"], 0);
            double hold = Num(st["hold"], 0);
            string kind = (string)st["expect"];
            bool ok;
            string detail;
            try { ok = Check(kind, st, out detail); }
            catch (Exception e) { ok = false; detail = "error: " + e.Message; }

            if (detail != null && detail.StartsWith("SKIP:"))
            {
                Record(st, "skip", detail.Substring(5).Trim());
                _expectDeadline = -1; _holdSince = -1;
                return true;
            }
            _lastExpectDetail = detail;

            if (ok)
            {
                if (_holdSince < 0) _holdSince = now;
                if (now - _holdSince >= hold)
                {
                    Record(st, "pass", detail);
                    _expectDeadline = -1; _holdSince = -1;
                    return true;
                }
                if (now <= _expectDeadline + hold) return false;
            }
            else
            {
                if (hold > 0 && _holdSince >= 0)   // it was true and flipped while holding
                {
                    Record(st, "fail", "did not hold: " + detail);
                    _expectDeadline = -1; _holdSince = -1;
                    return true;
                }
                _holdSince = -1;
                if (now < _expectDeadline) return false;
            }
            Record(st, "fail", detail);
            _expectDeadline = -1; _holdSince = -1;
            return true;
        }

        /// <summary>
        /// What a "recv" step counts: the messages <paramref name="p"/> received of
        /// <paramref name="type"/> ("*" = anything but presence) on
        /// <paramref name="netId"/> (null = any) since <paramref name="since"/>.
        /// For a target, its shared-state traffic counts as "its state on the bus"
        /// too when the kind is "*" or "__ss": "__ss" messages that carry state
        /// (sn snapshot, mg merge bundle, ws write, rk request — never the hb/rq/lv
        /// housekeeping) on "o:{netId}" or on <paramref name="stateBuses"/>.
        /// A game fully on dp.shared_state never net_sends, and used to fail here
        /// by design.
        /// </summary>
        public static List<SimPeerMessage> ReceivedOn(SimPeer p, string type, uint? netId, ICollection<string> stateBuses, double since)
        {
            bool any = type == "*" || string.IsNullOrEmpty(type);
            var direct = p.Received(any ? null : type, netId, since)
                    .Where(m => !any || (m.type != "pp" && m.type != "pp_bye")).ToList();
            if (!netId.HasValue || !(any || type == SessionEngine.TypeState)) return direct;

            var buses = new HashSet<string>(StringComparer.Ordinal) { "o:" + netId.Value.ToString(CultureInfo.InvariantCulture) };
            if (stateBuses != null) foreach (var b in stateBuses) if (!string.IsNullOrEmpty(b)) buses.Add(b);
            var state = p.Received(SessionEngine.TypeState, null, since).Where(m =>
            {
                string k = SimPeer.ExtractString(m.json, "k");
                if (k != "sn" && k != "mg" && k != "ws" && k != "rk") return false;
                string bus = SimPeer.ExtractString(m.json, "b");
                return bus != null && buses.Contains(bus);
            });
            return direct.Concat(state).OrderBy(m => m.t).ToList();
        }

        bool Check(string kind, JObject st, out string detail)
        {
            switch (kind)
            {
                case "lua":
                {
                    string err;
                    var v = LuaEval((string)st["target"], (string)st["script"], (string)st["code"] ?? "nil", out err);
                    if (err != null) { detail = err; return false; }
                    return Compare(v, st, out detail);
                }
                case "recv":
                case "no_recv":
                {
                    string err = null;
                    var p = SimPeers.FindAny((string)st["peer"]);
                    if (p == null) { detail = $"no peer '{(string)st["peer"]}'"; return false; }
                    string type = (string)st["kind"];
                    uint? netId = null;
                    string target = st["target"]?.ToString();
                    if (!string.IsNullOrEmpty(target))
                    {
                        if (!SimPeers.ResolveNetId(target, out uint id, out err)) { detail = err; return false; }
                        netId = id;
                    }
                    double since = Since(st);
                    List<string> stateBuses = null;
                    if (netId.HasValue && !uint.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        stateBuses = SharedState.BusKeysFor(SimPeers.FindObject(target));
                    var msgs = ReceivedOn(p, type, netId, stateBuses, since);
                    int n = msgs.Count;
                    string what = $"{p.Name} got {n} × '{type ?? "*"}'{(netId.HasValue ? $" on {target}" : "")}";
                    if (n == 0 && netId.HasValue)
                    {
                        // Say where the state traffic WAS, so a bus mismatch is visible in the log.
                        var seen = p.Received(SessionEngine.TypeState, null, since)
                            .Select(m => SimPeer.ExtractString(m.json, "b") + "/" + SimPeer.ExtractString(m.json, "k"))
                            .Distinct().Take(8).ToList();
                        what += $" (looked on o:{netId.Value}" + (stateBuses != null && stateBuses.Count > 0 ? ", " + string.Join(", ", stateBuses) : "") +
                                $"; __ss since then: {(seen.Count > 0 ? string.Join(", ", seen) : "none")})";
                    }
                    if (n > 0) what += " — last: " + Trim(msgs[n - 1].json, 160);
                    detail = what;
                    if (kind == "no_recv") return n == 0;
                    if (n > 0 && st["near"] != null)
                    {
                        // Where the message says something is, vs where it really is (park-local).
                        // A world-space leak passes on an identity frame and fails here after "misalign".
                        if (!SimPeers.TryExtractPosition(msgs[n - 1].json, out var got)) { detail = what + " — no position in it"; return false; }
                        var want = NearPoint((string)st["near"], p);
                        float dist = Vector3.Distance(got, want), radius = Num(st["radius"], 1f);
                        detail = $"{what} — position {SimPeers.Fmt(got)} is {dist:0.00} m from {(string)st["near"]} {SimPeers.Fmt(want)} (max {radius:0.##})";
                        if (dist > radius) return false;
                    }
                    int min = (int)Num(st["count"], 1);
                    int max = st["max"] != null ? (int)Num(st["max"], int.MaxValue) : int.MaxValue;
                    return n >= min && n <= max;
                }
                case "sees":
                {
                    var p = SimPeers.FindAny((string)st["peer"]);
                    if (p == null) { detail = $"no peer '{(string)st["peer"]}'"; return false; }
                    string who = (string)st["who"] ?? "local";
                    string id = who == "local" ? PlayerPresence.MyId : SimPeers.FindAny(who)?.Id;
                    bool ok = id != null && p.Sees(id, Since(st));
                    detail = $"{p.Name} {(ok ? "sees" : "does not see")} {who} ({id})";
                    return ok;
                }
                case "net_body":
                {
                    var go = SimPeers.FindObject((string)st["target"]);
                    var b = SimPeers.NetBodyOf(go);
                    if (b == null) { detail = $"SKIP: '{(string)st["target"]}' is not a dp.net_body prop"; return false; }
                    string want = (string)st["owner"] ?? "any";
                    string wantId = want == "local" ? PlayerPresence.MyId : want == "any" || want == "none" ? null : SimPeers.FindAny(want)?.Id;
                    float maxSpeed = Num(st["max_speed"], 3f);
                    bool ownerOk = want == "any" ? !string.IsNullOrEmpty(b.owner) : want == "none" ? string.IsNullOrEmpty(b.owner) : b.owner == wantId;
                    bool calm = b.speed <= maxSpeed;
                    detail = $"owner {b.owner ?? "none"} ({NameOf(b.owner)}), rev {b.rev}, {(b.following ? "following" : "simulating")}, speed {b.speed:0.00} m/s (max {maxSpeed:0.#}), at {SimPeers.Fmt(b.local)}";
                    return ownerOk && calm;
                }
                case "local_peers":
                {
                    var pp = PlayerPresence.Instance;
                    int n = pp != null ? pp.PeerCount : 0;
                    int want = (int)Num(st["count"], 1);
                    detail = $"the Editor's PlayerPresence sees {n} peer(s), want {want}";
                    return n == want;
                }
                case "leader":
                {
                    // The Editor's view: title-scoped dp.leader() in the target's scope, else session-wide.
                    string target = (string)st["target"];
                    string leader = null, title = null;
                    if (!string.IsNullOrEmpty(target))
                    {
                        leader = LuaEval(target, (string)st["script"], "dp.leader()", out string lerr) as string;
                        title = LuaEval(target, (string)st["script"], "dp_title_of ~= nil and dp_title_of(self.gameObject) or ''", out _) as string;
                        if (lerr != null) { detail = lerr; return false; }
                    }
                    if (leader == null) leader = LeaderId();
                    if (leader == null) { detail = "SKIP: no DreamParkSession / dp.leader() in this SDK"; return false; }
                    string who = (string)st["is"] ?? "local";
                    string want = who == "local" ? PlayerPresence.MyId : SimPeers.FindAny(who)?.Id;
                    // Every platform peer must compute the SAME leader (identical on every device).
                    var disagree = new List<string>();
                    foreach (var p in SimPeers.Live)
                    {
                        if (p.Engine == null || !p.Engine.Synced) continue;
                        if (!string.IsNullOrEmpty(title) && !p.SameTitle && p.GameId != title) continue;
                        string theirs = string.IsNullOrEmpty(title) ? p.Engine.LeaderId : p.Engine.LeaderFor(title);
                        if (theirs != leader) disagree.Add($"{p.Name} says {theirs} ({NameOf(theirs)})");
                    }
                    detail = $"leader {leader} ({NameOf(leader)}), want {who} ({want})" + (disagree.Count > 0 ? "; DISAGREE: " + string.Join(", ", disagree) : "; peers agree");
                    return leader == want && disagree.Count == 0;
                }
                case "state_agrees":
                {
                    // Every shared state the Editor's game holds is mirrored, synced and identical (rev +
                    // values) on every live platform peer of that title: the late-join snapshot arrived.
                    var editor = DreamParkSession.Engine.SharedStates.Where(x => x != null && !x.Disposed && !string.IsNullOrEmpty(x.BusKey)
                        && (st["name"] == null || x.Name == (string)st["name"])).ToList();
                    if (editor.Count == 0) { detail = "SKIP: the game opened no dp.shared_state"; return false; }
                    string only = (string)st["peer"];
                    var issues = new List<string>();
                    int checkedN = 0;
                    foreach (var p in SimPeers.Live)
                    {
                        if (p.Engine == null || (only != null && !string.Equals(p.Name, only, StringComparison.OrdinalIgnoreCase))) continue;
                        foreach (var e in editor)
                        {
                            if (!p.SameTitle && (string.IsNullOrEmpty(e.Title) || e.Title != p.GameId)) continue;
                            checkedN++;
                            var r = p.Replica(e.BusKey, e.Name);
                            if (r == null) { issues.Add($"{p.Name}: no '{e.Name}' yet"); continue; }
                            if (!r.Synced) { issues.Add($"{p.Name}: '{e.Name}' unsynced"); continue; }
                            if (r.Rev != e.Rev) { issues.Add($"{p.Name}: '{e.Name}' rev {r.Rev} vs Editor {e.Rev}"); continue; }
                            foreach (var k in e.Keys)
                                if (r.GetJson(k) != e.GetJson(k)) { issues.Add($"{p.Name}: '{e.Name}.{k}' = {r.GetJson(k)} vs Editor {e.GetJson(k)}"); break; }
                        }
                    }
                    detail = issues.Count == 0
                        ? $"{checkedN} replica(s) match the Editor: " + string.Join(", ", editor.Select(e => $"{e.Name} rev {e.Rev} writer {NameOf(e.LeaderId)}"))
                        : string.Join("; ", issues.Take(4));
                    return issues.Count == 0 && checkedN > 0;
                }
                case "budget":
                {
                    double since = Since(st);
                    int peak = SimPeers.LocalPeakRate(since);
                    int maxRate = (int)Num(st["max_rate"], DreamBoxClient.DesignBudget);
                    int drops = SimPeers.Relay != null ? SimPeers.Relay.RateLimitedMessageCount - (_dropMarks.TryGetValue((string)st["since"] ?? "start", out int d0) ? d0 : 0) : 0;
                    int maxDrops = (int)Num(st["max_drops"], 0);
                    var kinds = SimPeers.LocalSentByKind(since).OrderByDescending(k => k.Value).Take(8)
                        .Select(k => $"{k.Key}×{k.Value}");
                    double span = Math.Max(0.001, SimPeers.Now - since);
                    detail = $"Editor peak {peak} msg/s (limit {maxRate}), mean {SimPeers.LocalSentSince(since) / span:0.#}/s, relay drops {drops} (limit {maxDrops}); by kind: {string.Join(", ", kinds)}";
                    return peak <= maxRate && drops <= maxDrops;
                }
                case "no_errors":
                {
                    double since = Since(st);
                    var ignore = ((st["ignore"] as JArray) ?? new JArray()).Select(t => (string)t).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var hits = new List<string>();
                    for (int i = 0; i < _errors.Count; i++)
                        if (_errorTimes[i] >= since && !ignore.Any(x => _errors[i].Contains(x))) hits.Add(_errors[i]);
                    detail = hits.Count == 0 ? "no errors" : $"{hits.Count} error(s): " + string.Join(" | ", hits.Take(3));
                    return hits.Count == 0;
                }
            }
            detail = $"unknown expectation '{kind}'";
            return false;
        }

        bool Compare(object v, JObject st, out string detail)
        {
            string shown = Show(v);
            if (st["equals"] != null) { detail = $"{shown} == {st["equals"]}"; return LuaEquals(v, st["equals"]); }
            if (st["not_equals"] != null) { detail = $"{shown} ~= {st["not_equals"]}"; return !LuaEquals(v, st["not_equals"]); }
            if (st["gte"] != null) { detail = $"{shown} >= {st["gte"]}"; return ToDouble(v) >= Num(st["gte"], 0); }
            if (st["lte"] != null) { detail = $"{shown} <= {st["lte"]}"; return ToDouble(v) <= Num(st["lte"], 0); }
            if (st["contains"] != null) { detail = $"{shown} contains {st["contains"]}"; return shown.Contains((string)st["contains"]); }
            if (st["falsy"] != null && Truthy(st["falsy"])) { detail = $"{shown} is falsy"; return v == null || (v is bool b0 && !b0); }
            detail = $"{shown} is truthy";
            return v != null && !(v is bool b && !b);
        }

        // ── Finish ───────────────────────────────────────────────────
        void Finish(State state, string why)
        {
            if (Status != State.Running) return;
            Status = state;
            FinishedAt = SimPeers.Now;
            Application.logMessageReceived -= OnLog;
            _repeaters.Clear();
            foreach (var l in _later) { try { l.Value(); } catch { } }   // never leave the Editor's session paused
            // The Arena auto-cycle stays held after a scenario: resuming it would regenerate the
            // park between two scenarios of one run. The window's Arena button resumes it.
            if (_resumeArenaAuto) Note("Arena size auto-cycle was paused for this run (resume it in the Park Simulator overlay).");
            _resumeArenaAuto = false;
            _later.Clear();
            if (why != null) Note("■ " + why);
            Note($"■ {Name}: {state.ToString().ToUpperInvariant()} — {Passed} passed, {FailedCount} failed, {Skipped} skipped, {FinishedAt - StartedAt:0.0} s");
            try
            {
                Directory.CreateDirectory(RunDir);
                File.WriteAllText(Path.Combine(RunDir, "log.txt"), string.Join("\n", _log) + "\n");
                File.WriteAllText(Path.Combine(RunDir, "result.json"), Summary().ToString());
            }
            catch (Exception e) { Debug.LogWarning(SimPeers.Tag + " could not write the run log: " + e.Message); }
            try { _onFinished?.Invoke(state.ToString()); } catch { }
        }

        public JObject Summary() => new JObject
        {
            ["name"] = Name,
            ["status"] = Status.ToString(),
            ["passed"] = Passed,
            ["failed"] = FailedCount,
            ["skipped"] = Skipped,
            ["seconds"] = Math.Round(((Status == State.Running ? SimPeers.Now : FinishedAt) - StartedAt), 1),
            ["step"] = _pc,
            ["steps"] = Steps.Count,
            ["waiting_on"] = Status == State.Running ? _lastExpectDetail : null,
            ["log"] = string.IsNullOrEmpty(RunDir) ? null : Path.GetFullPath(Path.Combine(RunDir, "log.txt")),
            ["results"] = new JArray(_results),
        };

        // ── Helpers ──────────────────────────────────────────────────
        void Record(JObject st, string result, string detail)
        {
            string label = st["expect"] != null ? "expect " + (string)st["expect"] : "do " + (string)st["do"];
            if (st["label"] != null) label = (string)st["label"] + " (" + label + ")";
            _results.Add(new JObject { ["step"] = _pc + 1, ["what"] = label, ["result"] = result, ["detail"] = detail, ["t"] = Math.Round(SimPeers.Now - StartedAt, 2) });
            Note($"{(result == "pass" ? "PASS" : result == "fail" ? "FAIL" : "skip")}  {label}: {detail}");
        }

        void Note(string msg)
        {
            string line = $"[{SimPeers.Now - StartedAt,6:0.00}] {msg}";
            _log.Add(line);
            Debug.Log(SimPeers.Tag + " " + line);
        }

        SimPeer Peer(string name, ref string err)
        {
            var p = SimPeers.Find(name);
            if (p == null) err = $"no live peer '{name}'";
            return p;
        }

        double Since(JObject st)
        {
            string m = (string)st["since"];
            if (string.IsNullOrEmpty(m)) return StartedAt;
            return _marks.TryGetValue(m, out double t) ? t : StartedAt;
        }

        Vector3 Spot(JObject st, SimPeer p, bool keepHeight = true)
        {
            var at = Vec(st["at"]);
            if (at.HasValue) return at.Value;
            string near = (string)st["near"];
            if (string.IsNullOrEmpty(near) || near == "local") return SimPeers.LocalHeadPark();
            var other = SimPeers.Find(near);
            if (other != null) return other.HeadPos;
            var go = SimPeers.FindObject(near);
            if (go == null) return p.HeadPos;
            var v = ParkRoot.ToPark(go.transform.position);
            if (keepHeight) v.y = p.HeadPos.y;
            return v;
        }

        /// Park-local point for "local", a peer name, or an object path.
        static Vector3 NearPoint(string near, SimPeer self)
        {
            if (string.IsNullOrEmpty(near) || near == "local") return SimPeers.LocalHeadPark();
            var other = SimPeers.FindAny(near);
            if (other != null) return other.HeadPos;
            var go = SimPeers.FindObject(near);
            return go != null ? ParkRoot.ToPark(go.transform.position) : self.HeadPos;
        }

        static Vector3? Vec(JToken t)
        {
            if (t is JArray a && a.Count >= 3)
                return new Vector3((float)Num(a[0], 0), (float)Num(a[1], 0), (float)Num(a[2], 0));
            return null;
        }

        static float Num(JToken t, float fallback)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<float>();
            return float.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null: return false;
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.Integer: case JTokenType.Float: return Math.Abs(t.Value<double>()) > 1e-9;
                case JTokenType.String: { string s = t.Value<string>(); return !string.IsNullOrEmpty(s) && s != "false" && s != "0"; }
                default: return t.HasValues;
            }
        }

        JToken Resolve(JToken t)
        {
            switch (t)
            {
                case JObject o:
                {
                    var r = new JObject();
                    foreach (var kv in o)
                        r[kv.Key] = (kv.Key == "steps") ? kv.Value.DeepClone() : Resolve(kv.Value);   // nested steps resolve when they run
                    if (o["steps"] != null) r["steps"] = new JArray(((JArray)o["steps"]).Select(Resolve));
                    return r;
                }
                case JArray a: return new JArray(a.Select(Resolve));
                case JValue v when v.Type == JTokenType.String:
                {
                    string s = (string)v;
                    if (s.StartsWith("${") && s.EndsWith("}") && s.IndexOf("${", 2, StringComparison.Ordinal) < 0)
                    {
                        string key = s.Substring(2, s.Length - 3);
                        return Params.TryGetValue(key, out var pv) ? pv.DeepClone() : JValue.CreateString("");
                    }
                    if (s.Contains("${"))
                        foreach (var kv in Params) s = s.Replace("${" + kv.Key + "}", kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString(Newtonsoft.Json.Formatting.None));
                    return JValue.CreateString(s);
                }
                default: return t.DeepClone();
            }
        }

        // ── Lua bridge (the Editor's one real copy of the game) ──────
        static FieldInfo _scopeField;
        static LuaTable ScopeOf(LuaBehaviour lb)
        {
            _scopeField = _scopeField ?? typeof(LuaBehaviour).GetField("scriptScopeTable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return _scopeField?.GetValue(lb) as LuaTable;
        }

        /// Evaluate in an object's script scope (expression first, then statement), or in the global env.
        public static object LuaEval(string target, string script, string code, out string error)
        {
            error = null;
            var env = LuaBehaviour.GetLuaEnv();
            if (env == null) { error = "no Lua environment (has any LuaBehaviour booted?)"; return null; }
            LuaTable scope = null;
            if (!string.IsNullOrEmpty(target))
            {
                var go = SimPeers.FindObject(target);
                if (go == null) { error = $"no object '{target}'"; return null; }
                var lbs = go.GetComponents<LuaBehaviour>();
                if (lbs.Length == 0) lbs = go.GetComponentsInChildren<LuaBehaviour>(true);
                if (lbs.Length == 0) { error = $"'{target}' has no LuaBehaviour"; return null; }
                var lb = string.IsNullOrEmpty(script) ? lbs[0]
                    : lbs.FirstOrDefault(l => l.luaScript != null && l.luaScript.name.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0) ?? lbs[0];
                scope = ScopeOf(lb);
                if (scope == null) { error = $"'{target}' script has not booted yet (parked or never enabled)"; return null; }
            }
            object[] r;
            try { r = scope != null ? env.DoString("return " + code, "simpeers_eval", scope) : env.DoString("return " + code, "simpeers_eval"); }
            catch (LuaException)
            {
                try { r = scope != null ? env.DoString(code, "simpeers_eval", scope) : env.DoString(code, "simpeers_eval"); }
                catch (Exception e) { error = "lua: " + e.Message; return null; }
            }
            return r != null && r.Length > 0 ? r[0] : null;
        }

        static bool LuaEquals(object v, JToken want)
        {
            if (want.Type == JTokenType.Null) return v == null;
            if (want.Type == JTokenType.Boolean) return v is bool b && b == want.Value<bool>();
            if (want.Type == JTokenType.Integer || want.Type == JTokenType.Float) return v != null && Math.Abs(ToDouble(v) - want.Value<double>()) < 1e-6;
            return v != null && Show(v) == want.ToString();
        }

        static double ToDouble(object v)
        {
            switch (v)
            {
                case null: return double.NaN;
                case bool b: return b ? 1 : 0;
                case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
                default: try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return double.NaN; }
            }
        }

        static string Show(object v)
        {
            switch (v)
            {
                case null: return "nil";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("0.####", CultureInfo.InvariantCulture);
                case float f: return f.ToString("0.####", CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case LuaTable t:
                {
                    var parts = new List<string>();
                    int n = 0;
                    t.ForEach<object, object>((k, val) => { if (n++ < 12) parts.Add($"{k}={(val is LuaTable ? "{…}" : Show(val))}"); });
                    return "{" + string.Join(", ", parts) + (n > 12 ? ", …" : "") + "}";
                }
                default: return v.ToString();
            }
        }

        static string Trim(string s, int n = 120) => string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) + "…" : s);
        static string Sanitize(string s) => new string((s ?? "run").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

        static string NameOf(string id)
        {
            if (id == PlayerPresence.MyId) return "local";
            return SimPeers.All.LastOrDefault(p => p.Id == id)?.Name ?? "?";
        }

        /// The session leader as the Editor's game sees it: DreamParkSession.LeaderId when
        /// that type exists, else dp.leader() from Lua, else null (older SDK).
        public static string LeaderId()
        {
            if (!EditorApplication.isPlaying) return null;
            try { return DreamParkSession.LeaderId; } catch { }
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return null;
                var r = env.DoString("if dp ~= nil and dp.leader ~= nil then return dp.leader() end return nil", "simpeers_leader");
                return r != null && r.Length > 0 ? r[0] as string : null;
            }
            catch { return null; }
        }
    }
}
