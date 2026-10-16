#if UNITY_EDITOR && (DREAMPARK_SDK_PACKAGES_READY || DREAMPARKCORE)
using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    /// <summary>
    /// Unity CLI surface for simulated multiplayer peers (ParkSimPeers.cs), so an
    /// agent can test late join, simultaneous starts, leader loss, shared props and
    /// the send budget without a headset. Through the agent kit, under the play lock:
    ///
    ///   unity command --project-path . editor_play
    ///   unity command --project-path . --timeout 120 dreampark_peers_scenario --name late_join --params '{"target":"A_MyGame/Manager","start":"start_round()"}' --wait true
    ///   unity command --project-path . dreampark_peers_add --name B
    ///   unity command --project-path . dreampark_peers_act --step '{"do":"peer_send","name":"B","target":"A_MyGame/Manager","kind":"hit_rq","body":{"n":1}}'
    ///   unity command --project-path . dreampark_peers_status
    ///   unity command --project-path . dreampark_peers_remove --name B --mode crash
    /// </summary>
    public static class SimPeerCommands
    {
        const string Tags0 = "dreampark";
        const string Tags1 = "dreampark/multiplayer";

        [CliCommand("dreampark_peers_status", "Simulated multiplayer peers: the loopback relay, the Editor's link, who leads, every peer (id, pose, traffic) and the running scenario.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Status() => SimPeers.Status();

        [CliCommand("dreampark_peers_add", "Add a simulated player to the running game (Play mode). It joins the Editor's session over the real relay (no echo, 60 msg/s per peer) and streams real presence. Starts the session if needed.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Add(
            [CliArg("name", "Peer name (B, C, …). Default: next free letter.")] string name = "",
            [CliArg("rank", "high = sorts above the Editor's id (the Editor stays leader); low = below (this peer leads).")] string rank = "high",
            [CliArg("device", "headset | phone")] string device = "headset",
            [CliArg("near", "\"local\" or an object path to stand next to.")] string near = "local",
            [CliArg("id", "Explicit 8-hex presence id (overrides rank).")] string id = "")
        {
            var p = SimPeers.Add(new SimPeerOptions
            {
                name = name, id = string.IsNullOrEmpty(id) ? null : id, device = device, near = near,
                rank = rank == "low" ? SimPeerRank.Low : SimPeerRank.High,
            }, out string err);
            return p == null ? (object)new { success = false, error = err } : new { success = true, name = p.Name, id = p.Id, park = SimPeers.Fmt(p.HeadPos) };
        }

        [CliCommand("dreampark_peers_remove", "Remove a simulated player: quit (clean goodbye) or crash (silence; peers find out from timeouts).", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Remove(
            [CliArg("name", "Peer name.", Required = true)] string name,
            [CliArg("mode", "quit | crash")] string mode = "quit")
        {
            bool ok = SimPeers.Remove(name, mode, out string err);
            return new { success = ok, error = err };
        }

        [CliCommand("dreampark_peers_act", "Run one scenario DO step now, e.g. '{\"do\":\"peer_send\",\"name\":\"B\",\"target\":\"A_Game/Boss\",\"kind\":\"hit_rq\",\"body\":{\"n\":1}}'. Steps: peer_add, peer_remove, peer_link, peer_move, peer_walk, peer_hand, peer_send, peer_state, local_lua.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Act([CliArg("step", "The step as a JSON object.", Required = true)] string step)
        {
            JObject o;
            try { o = JObject.Parse(step); } catch (Exception e) { return new { success = false, error = "step is not JSON: " + e.Message }; }
            string err = SimPeerScenario.DoOnce(o, out string note);
            return new { success = err == null, error = err, note };
        }

        [CliCommand("dreampark_peers_scenario", "Run a multiplayer scenario over simulated peers: a built-in (late_join, simultaneous_start, leader_leaves, shared_prop, budget, or 'all'), a .json path, or inline JSON. Params JSON fills the scenario's ${…} hooks. With --wait true it returns the pass/fail log.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static async Task<object> Scenario(
            [CliArg("name", "Built-in name, 'all', a .json path or inline JSON.", Required = true)] string name,
            [CliArg("params", "JSON object of params, e.g. '{\"target\":\"A_Game/Manager\",\"start\":\"start()\"}'.")] string paramsJson = "{}",
            [CliArg("wait", "Block until it finishes (pass --timeout to unity command).")] bool wait = false,
            [CliArg("fresh", "Remove all peers before starting (default true).")] bool fresh = true,
            [CliArg("reset_park", "Regenerate the Park Simulator's park before each scenario, so no game state carries over from the previous one.")] bool resetPark = false)
        {
            if (!EditorApplication.isPlaying) return new { success = false, error = "Not in Play mode. Run editor_play first." };
            JObject overrides;
            try { overrides = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception e) { return new { success = false, error = "params is not JSON: " + e.Message }; }

            var names = name == "all" ? SimPeerBuiltins.Order : new[] { name };
            var results = new JArray();
            foreach (var n in names)
            {
                var s = SimPeerScenario.Load(n, overrides, out string err);
                if (s == null) return new { success = false, error = err };
                if (fresh) SimPeers.RemoveAll();
                if (fresh && names.Length > 1) await Task.Delay(1500);   // goodbyes land; crashed peers were reaped inside their scenario
                if (resetPark)
                {
                    ParkSimulator.Regenerate();
                    double until = EditorApplication.timeSinceStartup + 90;
                    await Task.Delay(500);
                    while ((ParkSimulator.IsGenerating || !global::DreamPark.ParkBuilder.LevelObjectManager.objectsEnabled) && EditorApplication.timeSinceStartup < until)
                        await Task.Delay(250);
                    if (SimPeers.Misaligned) SimPeers.Misalign(0, Vector3.zero);   // the new park joins the turned frame
                    await Task.Delay(3000);   // scripts boot, zones settle
                    // The rebuilt park's Game Manager is exempt from the optimizer's
                    // distance pass, so nothing but a park release ever un-parks it:
                    // wake it (ArcadeLand: every Lua step failed after a reset).
                    Debug.Log("[SimPeers] reset_park: " + SimPeers.WakeParkServices());
                    // Never run a scenario against a target whose script is not running:
                    // say why instead of failing every step with "has not booted".
                    string target = s.Params["target"]?.ToString();
                    if (!string.IsNullOrEmpty(target) && !uint.TryParse(target, out _))
                    {
                        double bootBy = EditorApplication.timeSinceStartup + 10;
                        string bootErr;
                        while (true)
                        {
                            SimPeerScenario.LuaEval(target, (string)s.Params["script"], "true", out bootErr);
                            if (bootErr == null || EditorApplication.timeSinceStartup > bootBy) break;
                            SimPeers.WakeParkServices();
                            await Task.Delay(250);
                        }
                        if (bootErr != null)
                            return new { success = false, error = $"reset_park: '{target}' is not running after the park rebuild ({bootErr}): " +
                                                                  SimPeers.DescribeParking(SimPeers.FindObject(target)) };
                    }
                }
                err = SimPeerScenario.Run(s);
                if (err != null) return new { success = false, error = err };
                if (!wait && names.Length == 1) return new { success = true, running = s.Name, poll = "dreampark_peers_scenario_status" };
                while (s.Status == SimPeerScenario.State.Running && EditorApplication.isPlaying) await Task.Delay(250);
                results.Add(s.Summary());
            }
            bool allPassed = results.All(r => (string)r["status"] == "Passed");
            return new { success = allPassed, results = results.ToString() };
        }

        [CliCommand("dreampark_peers_scenario_status", "The running (or last) scenario: status, step, what it is waiting on, every result so far and the log path.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object ScenarioStatus()
        {
            var s = SimPeerScenario.Current;
            return s == null ? (object)new { status = "none" } : s.Summary().ToString();
        }

        [CliCommand("dreampark_peers_inbox", "What a simulated peer received (newest last): kind, NetId and the wire JSON.", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Inbox(
            [CliArg("name", "Peer name.", Required = true)] string name,
            [CliArg("kind", "Only this message kind ('*' = all except presence).")] string kind = "*",
            [CliArg("tail", "How many.")] int tail = 20)
        {
            var p = SimPeers.FindAny(name);
            if (p == null) return new { success = false, error = "no peer " + name };
            var msgs = p.Inbox.Where(m => kind == "*" ? (m.type != "pp" && m.type != "pp_bye") : m.type == kind)
                              .Reverse().Take(tail).Reverse()
                              .Select(m => new { t = Math.Round(m.t, 2), m.type, netId = m.hasNetId ? (long)m.netId : -1, json = m.json.Length > 400 ? m.json.Substring(0, 400) + "…" : m.json })
                              .ToArray();
            return new { success = true, received = p.ReceivedCount, messages = msgs };
        }

        [CliCommand("dreampark_peers_stop", "Remove every simulated peer and stop the loopback relay (the Editor's NetSessionArbiter is re-enabled).", MainThreadRequired = true, Tags = new[] { Tags0, Tags1 })]
        public static object Stop()
        {
            SimPeers.Stop();
            return new { success = true };
        }
    }
}
#endif
