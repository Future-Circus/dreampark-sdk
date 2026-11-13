// Ways to play (sdk-tools, Oct 2026) — headless checks of the SDK side: header reading, the upload export, the
// pre-upload checks and the Park Simulator's engine (WaysToPlaySim = the REAL RunnerDirector on fake headsets).
// Run: <clone>/Tools/agent/u.sh run_script --file Tools/Builders/WaysToPlaySimCheck.cs --entry WaysToPlaySimCheck.Run --timeout_ms 240000
// Writes the full report to <project>/Temp/ways-to-play-sim-check.txt and returns ALL PASS / N FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DreamPark;
using DreamPark.WaysToPlay;

public static class WaysToPlaySimCheck
{
    static StringBuilder sb;
    static int fails;

    public static string SampleRoot = "Assets/DreamPark/Samples/WaysToPlay";
    static string RunnersDir => SampleRoot + "/Runners";

    static void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static List<RunnerLevel> Levels() => new List<RunnerLevel>
    {
        new RunnerLevel { id = "Garden", name = "Garden", address = "S/Levels/Garden", widthFt = 12, depthFt = 18 },
        new RunnerLevel { id = "Workshop", name = "Workshop", address = "S/Levels/Workshop", widthFt = 12, depthFt = 18 },
        new RunnerLevel { id = "Library", name = "Library", address = "S/Levels/Library", widthFt = 12, depthFt = 18 },
        new RunnerLevel { id = "Courtyard", name = "Courtyard", address = "S/Levels/Courtyard", widthFt = 24, depthFt = 18 },
        new RunnerLevel { id = "GreatHall", name = "Great Hall", address = "S/Levels/GreatHall", widthFt = 24, depthFt = 36 },
    };

    static WaysToPlaySim NewSim(int tiles, string lua, string id, float minutes = 6f)
    {
        return new WaysToPlaySim
        {
            tiles = tiles, minutes = minutes, autoFinishSeconds = 15f, seed = 7, contentId = "WaysToPlaySample",
            runner = lua == null ? null : RunnerRecord.FromLua(id, lua), levels = Levels(),
        };
    }

    static void Dump(WaysToPlaySim sim, string title)
    {
        sb.AppendLine("---- " + title + " ----");
        foreach (var l in sim.log) sb.AppendLine("   " + l);
    }

    static bool Log(WaysToPlaySim sim, string needle) => sim.log.Any(l => l.Contains(needle));

    public static string Run()
    {
        sb = new StringBuilder(); fails = 0;

        // 1. Headers (evaluated in a throwaway VM).
        foreach (string f in Directory.GetFiles(RunnersDir, "*.lua.txt"))
        {
            var h = RunnerHeaderReader.Read(File.ReadAllText(f), RunnerIds.FromFileName(f));
            sb.AppendLine("header " + h + (h.warnings.Count > 0 ? " warnings: " + string.Join("; ", h.warnings) : ""));
            Check(h.Ok, "header reads: " + Path.GetFileName(f));
        }
        var bad = RunnerHeaderReader.Read("function onSessionStart() end", "nohdr");
        Check(!bad.Ok && bad.error.Contains("no DreamPark.runner"), "a runner without a header is refused: " + bad.error);
        var syn = RunnerHeaderReader.Read("DreamPark.runner { name = 'x' }\nfunction (", "syntax");
        Check(!syn.Ok, "a syntax error is refused: " + syn.error);
        var noName = RunnerHeaderReader.Read("DreamPark.runner { blurb = 'x' }", "noname");
        Check(!noName.Ok && noName.error.Contains("name"), "a header without a name is refused: " + noName.error);
        var computed = RunnerHeaderReader.Read("local N = 'Co' .. 'op'\nDreamPark.runner { name = N, default = true }\ndp.flow.onLevelComplete(function() end)", "computed");
        Check(computed.Ok && computed.name == "Coop" && computed.isDefault, "computed header values and file-scope dp calls work (name = " + computed.name + ")");
        var two = new List<RunnerHeaderInfo> { new RunnerHeaderInfo { runnerId = "b", isDefault = true }, new RunnerHeaderInfo { runnerId = "a", isDefault = true } };
        RunnerHeaderReader.ResolveDefault(two, out bool twoMarked);
        Check(twoMarked && two[0].isDefault && !two[1].isDefault, "two defaults detected; the first marked is kept");
        var none = new List<RunnerHeaderInfo> { new RunnerHeaderInfo { runnerId = "z" }, new RunnerHeaderInfo { runnerId = "a" } };
        RunnerHeaderReader.ResolveDefault(none, out _);
        Check(none[1].isDefault && !none[0].isDefault, "no default → the first by runnerId");
        Check(RunnerIds.FromFileName("Runners/PvP Only.lua.txt") == "pvp-only" && RunnerIds.FromFileName("_x.lua.txt") == "x", "runnerId from the file name: pvp-only, x");

        // 2. Upload export (dry run) + manifest records.
        var json = RunnerCatalog.BuildExportJson(SampleRoot, out List<string> problems, new Dictionary<string, string> { { "adventure", "https://example.test/cover.png" } });
        sb.AppendLine("export: " + (json != null ? json.Print(true) : "(none)"));
        Check(json != null && json.type == Defective.JSON.JSONObject.Type.Array && json.list.Count == 2 && problems.Count == 0, "export = a top-level array of the 2 sample runners, no problems");
        Check(json != null && json.list.Count(i => i.GetField("default").boolValue) == 1 && json.list[0].GetField("runnerId").stringValue == "adventure"
              && json.list[0].GetField("coverUrl").stringValue == "https://example.test/cover.png" && json.list[1].GetField("coverUrl").type == Defective.JSON.JSONObject.Type.Null
              && json.list[0].GetField("scriptPath").stringValue == "DreamPark/Samples/WaysToPlay/Runners/adventure.lua.txt",
              "export fields: exactly one default, coverUrl filled / null, scriptPath");
        var recs = RunnerCatalog.BuildManifestRunners(SampleRoot);
        Check(recs.Count == 2 && recs.All(r => !string.IsNullOrEmpty(r.lua)) && recs.Count(r => r.isDefault) == 1, "manifest runners carry the Lua + header for the headset");

        // 3. Pre-upload checks on the sample.
        var findings = RunnerChecks.Run(SampleRoot);
        foreach (var f in findings) sb.AppendLine("finding " + f);
        Check(!findings.Any(f => f.severity == RunnerFindingSeverity.Blocking), "the sample has no blocking findings");

        // 3b. Negative checks: a runner that opens an edge with a look no level has, two defaults, a bare .lua,
        //     a runner without a header. Written next to the sample for the check, deleted right after.
        string[] temp = { RunnersDir + "/zz-bad.lua.txt", RunnersDir + "/zz-nohdr.lua.txt", RunnersDir + "/zz-stray.lua" };
        try
        {
            File.WriteAllText(temp[0], "DreamPark.runner { name = 'Bad', default = true }\nfunction onSessionStart(b) dp.flow.load('Garden', { tile = 1 }) dp.edges.open(1, 'right', 'portcullis') dp.flow.load('Nowhere') end");
            File.WriteAllText(temp[1], "function onSessionStart(b) end");
            File.WriteAllText(temp[2], "DreamPark.runner { name = 'Stray' }");
            var neg = RunnerChecks.Run(SampleRoot);
            foreach (var f in neg) sb.AppendLine("neg-finding " + f);
            Check(neg.Any(f => f.severity == RunnerFindingSeverity.Blocking && f.message.Contains("portcullis")), "[checks] an edge opened with a look no level has → blocking");
            Check(neg.Any(f => f.severity == RunnerFindingSeverity.Blocking && f.message.Contains("default = true")), "[checks] two defaults → blocking");
            Check(neg.Any(f => f.severity == RunnerFindingSeverity.Blocking && f.message.Contains("zz-nohdr")), "[checks] a runner without a header → blocking");
            Check(neg.Any(f => f.message.Contains("bare .lua")), "[checks] a bare .lua file → warning (it would not ship)");
            Check(neg.Any(f => f.message.Contains("\"Nowhere\"")), "[checks] a level name the title does not have → warning");
            var negExport = RunnerCatalog.BuildExportJson(SampleRoot, out List<string> negProblems);
            Check(negExport.list.Count == 3 && negExport.list.Count(i => i.GetField("default").boolValue) == 1 && negProblems.Any(p => p.Contains("zz-nohdr")),
                  "[export] a broken runner is left out and reported; still exactly one default (" + string.Join(" | ", negProblems) + ")");
        }
        finally { foreach (string f in temp) { if (File.Exists(f)) File.Delete(f); if (File.Exists(f + ".meta")) File.Delete(f + ".meta"); } }

        string adventure = File.ReadAllText(Path.Combine(RunnersDir, "adventure.lua.txt"));
        string quick = File.ReadAllText(Path.Combine(RunnersDir, "quick.lua.txt"));

        // 4. Adventure on 1, 2 and 4 tiles: 3 headsets, a late join at 25 s, the leader leaves at 40 s (mid-level).
        foreach (int tiles in new[] { 1, 2, 4 })
        {
            var sim = NewSim(tiles, adventure, "adventure");
            bool started = sim.Start(3);
            Check(started && sim.Leader != null, $"[{tiles}] 3 headsets sync and elect a leader; the director runs there");
            bool joined = false, left = false, gate = false, open = false, span = false, nextUp = false, vote = false;
            for (int i = 0; i < 4000 && !sim.ended; i++)
            {
                sim.Tick(0.1);
                if (!joined && sim.Elapsed >= 25) { sim.AddPlayer(); joined = true; }
                if (!left && sim.Elapsed >= 40) { sim.LeaderLeaves(); left = true; }
                for (int t = 0; t < sim.Geometry.Count; t++)
                    foreach (string s in new[] { "front", "back", "left", "right" })
                    {
                        var e = sim.EdgeLook(t, s);
                        if (e.look == "gate") gate = true;
                        if (e.look != "closed" && sim.Geometry.Neighbour(t, s) >= 0) open = true;
                    }
                if (sim.Loads.Any(l => l.tiles.Length > 1)) span = true;
                if (sim.NextUp != null) nextUp = true;
                if (sim.Vote != null) vote = true;
            }
            Check(string.IsNullOrEmpty(sim.LastError), $"[{tiles}] no director error ({sim.LastError})");
            Check(sim.leaderChanges == 1 && Log(sim, "resumed"), $"[{tiles}] the leader left mid-level → the next headset resumed (onResume)");
            Check(sim.lateJoins == 1 && sim.Players == 3, $"[{tiles}] a late joiner rebuilt the board (headsets now {sim.Players})");
            Check(vote, $"[{tiles}] the end-of-world vote appeared");
            Check(nextUp, $"[{tiles}] Next up / the reveal appeared");
            Check(sim.Loads.Any(l => l.level == "GreatHall") || Log(sim, "GreatHall"), $"[{tiles}] reached the boss (GreatHall)");
            Check(sim.ended, $"[{tiles}] the session ran to time up");
            if (tiles > 1) Check(open && gate, $"[{tiles}] a seam between tiles opened (the director's gate look) — open={open} gate={gate}");
            if (tiles > 1) Check(span, $"[{tiles}] a whole-board level spanned the tiles");
            Dump(sim, $"adventure · {tiles} tile(s) · state {sim.ScriptState}");
            sim.Stop();
        }

        // 5. Quick rounds: votes every round; seamless vs Next up.
        {
            var sim = NewSim(1, quick, "quick", 4f);
            sim.Start(2);
            int votes = 0; bool inVote = false, nu = false;
            for (int i = 0; i < 2400 && !sim.ended; i++)
            {
                sim.Tick(0.1);
                bool v = sim.Vote != null;
                if (v && !inVote) votes++;
                inVote = v;
                if (sim.NextUp != null) nu = true;
            }
            Check(string.IsNullOrEmpty(sim.LastError), "[quick] no director error");
            Check(votes >= 3, $"[quick] a vote every round ({votes})");
            Check(nu, "[quick] Next up at the end of a world (every third round)");
            Dump(sim, "quick rounds · 1 tile · state " + sim.ScriptState);
            sim.Stop();
        }

        // 6. Two simultaneous votes; the leader leaving during a vote.
        {
            string twoVotes = @"
DreamPark.runner { name = 'votes' }
function onSessionStart(board)
  dp.flow.load('Garden', { tile = 1 })
  dp.flow.choose({ options = { 'A', 'B' }, zones = { { x = -1, z = 0, r = 1 }, { x = 1, z = 0, r = 1 } }, seconds = 6, default = 'A', tag = 'v1' })
  dp.flow.choose({ options = { 'X', 'Y' }, zones = { { x = -1, z = 0, r = 1 }, { x = 1, z = 0, r = 1 } }, seconds = 6, default = 'Y', tag = 'v2' })
end
function onResume(state) dp.log('resume with ' .. tostring(#state.list) .. ' tile(s)') end
dp.flow.onChoice(function(winner, tag) dp.flow.state[tag] = winner dp.log('choice ' .. tag .. '=' .. winner) end)
";
            var sim = NewSim(1, twoVotes, "votes");
            sim.autoFinishSeconds = 0;
            sim.Start(3);
            sim.Run(20);
            sb.AppendLine("two votes state " + sim.ScriptState);
            Check(sim.ScriptState.Contains("v1") || sim.ScriptState.Contains("v2"), "[votes] two votes asked at once: what the runtime does is in the log (state " + sim.ScriptState + ")");
            Dump(sim, "two simultaneous votes");
            sim.Stop();

            var sim2 = NewSim(1, twoVotes, "votes");
            sim2.autoFinishSeconds = 0;
            sim2.Start(3);
            sim2.Run(2);
            sim2.LeaderLeaves();
            sim2.Run(20);
            Check(Log(sim2, "resum") && (sim2.ScriptState.Contains("v1") || sim2.ScriptState.Contains("v2")), "[votes] the leader left mid-vote → the new leader still got the result through onChoice (state " + sim2.ScriptState + ")");
            Dump(sim2, "leader leaves during a vote");
            sim2.Stop();
        }

        // 7. Free play when the director loads nothing; the built-in default runner.
        {
            var sim = NewSim(1, "DreamPark.runner { name = 'once' }\nfunction onSessionStart(b) dp.flow.load('Garden', { tile = 1 }) end", "once", 2f);
            sim.Start(2);
            sim.Run(40);
            Check(sim.Loads.Count == 1 && sim.Loads[0].done && sim.Loads[0].level == "Garden", "[free play] a finished level with nothing loaded next stays on the board (free play)");
            sim.Stop();
            var def = NewSim(2, null, null, 3f);
            def.Start(2);
            def.Run(90);
            Check(string.IsNullOrEmpty(def.LastError) && Log(def, "Garden") && Log(def, "Workshop"), "[built-in default] its levels in order, one per tile");
            Dump(def, "built-in default · 2 tiles");
            def.Stop();
        }

        // 8. (platform-core) the pile-in clock, zones and Fail in the simulator.
        {
            var z = NewSim(2, "DreamPark.runner { name = 'Z' }\nfunction onSessionStart(b) dp.flow.load('Garden', { tile = 1 }) dp.flow.load('Workshop', { tile = 2 }) dp.zones.open('cave', { level = 'Library', tile = 2, capacity = 1 }) end\n" +
                "dp.session.onClockStart(function() dp.flow.state.clock = true end)\ndp.zones.onRefused(function(zone, p, why) dp.flow.state.refused = why end)\ndp.flow.onLevelComplete(function(level, info) dp.flow.state.last = level .. ':' .. info.result end)", "z", 4f);
            z.pileInSeconds = 10f; z.autoFinishSeconds = 0f;
            z.Start(3);
            z.Run(2);
            Check(!z.ScriptState.Contains("clock"), "[clock] during the 10 s pile-in onClockStart has not fired (" + z.ScriptState + ")");
            z.Run(10);
            Check(z.ScriptState.Contains("\"clock\":true"), "[clock] the pile-in ends → onClockStart fired");
            var obs = z.Observer;
            z.Leader.dir.Op("zone_send", "cave", "{\"players\":[\"" + obs.id + "\"]}");
            z.Leader.dir.Op("zone_send", "cave", "{\"players\":[\"" + z.Live.First(h => h != obs).id + "\"]}");
            z.Run(1);
            Check(z.ObserverZone == "cave" && z.SeenLoads.Any(l => l.level == "Library") && z.SeenLoads.Any(l => l.level == "Garden") && !z.SeenLoads.Any(l => l.level == "Workshop"),
                "[zones] a headset sent into the cave sees the Library on tile 2 and the main Garden on tile 1 (" + string.Join(",", z.SeenLoads.Select(l => l.level)) + ")");
            Check(z.ScriptState.Contains("\"refused\":\"full\""), "[zones] the cave holds 1: the second player is refused (full)");
            var lib = z.Loads.First(l => l.level == "Library");
            z.FinishLoad(lib, "lost");
            z.Run(1.5);
            Check(z.ScriptState.Contains("Library:lost"), "[fail] Fail on a zone's level → onLevelComplete(result = 'lost')");
            Dump(z, "zones + clock + fail");
            z.Stop();
        }

        string head = (fails == 0 ? "ALL PASS" : fails + " FAIL");
        sb.Insert(0, head + "\n");
        string outPath = Path.GetFullPath("Temp/ways-to-play-sim-check.txt");
        File.WriteAllText(outPath, sb.ToString());
        return head + " — full report: " + outPath;
    }
}
