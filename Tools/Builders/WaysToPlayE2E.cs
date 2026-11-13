// Ways to play END TO END, the headset half (release round Oct 26 2026, platform-core). Your editor plays the headset:
// it reads the park web setup made (Tools/ways-to-play-e2e/setup-park.js wrote ~/.dreampark/ways-e2e/<content>.json),
// asks the LOCAL emulator server for this headset's /identity (nowPlaying: title + way to play + board), and runs that
// way to play on the real RunnerHost / RunnerDirector with your title's level prefabs, Game Manager and Player rig.
//
//   1. node <core>/Tools/ways-to-play-e2e/setup-park.js --base http://127.0.0.1:3350 --content <TitleBeta> [--runner id] [--preset single|double|quad]
//   2. Tools/agent/playlock.sh acquire <you> && Tools/agent/u.sh editor_play
//   3. Tools/agent/u.sh run_script --file <abs path>/Tools/Builders/WaysToPlayE2E.cs --entry WaysToPlayE2E.Headset --args '["<TitleBeta>"]'
//   4. ... WaysToPlayE2E.Status / Finish '["1|won"]' / Stand '["2"]' ;  Tools/agent/capture.sh game <png>
//   5. WaysToPlayE2E.Stop ; u.sh editor_stop ; playlock.sh release <you>
// Refuses any server that is not 127.0.0.1 / localhost. Never writes anything to the server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Defective.JSON;
using DreamPark;
using DreamPark.WaysToPlay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public static class WaysToPlayE2E
{
    const string Key = "dp.ways-e2e";
    static object Get(string k) => AppDomain.CurrentDomain.GetData(Key + "." + k);
    static void Set(string k, object v) => AppDomain.CurrentDomain.SetData(Key + "." + k, v);

    static string JsonPath(string arg)
    {
        if (!string.IsNullOrEmpty(arg) && arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return arg;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dreampark", "ways-e2e", (arg ?? "") + ".json");
    }

    /// args: "<ContentId>" or a path to the setup-park JSON, optionally "|<content root>" to play another folder's
    /// levels + runners (e.g. the SDK sample, Assets/DreamPark/Samples/WaysToPlay). Play mode.
    public static string Headset(string arg)
    {
        if (!Application.isPlaying) return "[e2e] needs Play mode (u.sh editor_play under the play lock)";
        string rootOverride = null;
        if (arg != null && arg.Contains("|")) { rootOverride = arg.Substring(arg.IndexOf('|') + 1); arg = arg.Substring(0, arg.IndexOf('|')); }
        string path = JsonPath(arg);
        if (!File.Exists(path)) return "[e2e] no " + path + " — run Tools/ways-to-play-e2e/setup-park.js first";
        var park = new JSONObject(File.ReadAllText(path));
        string baseUrl = park.GetField("base")?.stringValue ?? "";
        var u = new Uri(baseUrl);
        if (u.Host != "127.0.0.1" && u.Host != "localhost") return "[e2e] refusing a non-local server: " + baseUrl;
        string headsetId = park.GetField("headsetId")?.stringValue, key = park.GetField("key")?.stringValue;

        // 1. what this headset is told to play — /identity, like a Quest
        string body = HttpGet(baseUrl + "/app/headset/" + Uri.EscapeDataString(headsetId) + "/identity", "ApiKey " + key, out long code);
        if (code != 200) return $"[e2e] /identity answered {code}: {Trunc(body)}";
        var np = new JSONObject(body).GetField("nowPlaying");
        if (np == null || np.type != JSONObject.Type.Object) return "[e2e] /identity has no nowPlaying (is the title picked in web setup?) " + Trunc(body);
        string contentId = np.GetField("contentId")?.stringValue, runnerId = np.GetField("runnerId")?.stringValue ?? "default";
        string locationId = np.GetField("locationId")?.stringValue ?? "loc_e2e";
        var (w, d) = RunnerBoardRules.BoardFeetFrom(np.GetField("board"));
        if (w <= 0) { w = 12; d = 18; }

        // 2. the title in THIS project (a beta target's levels live under its main title's folder)
        string src = contentId;
        if (!AssetDatabase.IsValidFolder("Assets/Content/" + src) && src.EndsWith("Beta", StringComparison.Ordinal)) src = src.Substring(0, src.Length - 4);
        string root = rootOverride ?? "Assets/Content/" + src;
        if (!AssetDatabase.IsValidFolder(root)) return "[e2e] this project has no " + root + " (open the title's editor)";
        var entries = RunnerCatalog.Discover(root).Where(e => e.header.Ok).ToList();
        var entry = runnerId == RunnerDefaults.BuiltInId ? entries.FirstOrDefault(e => e.header.isDefault) ?? entries.FirstOrDefault()
            : entries.FirstOrDefault(e => string.Equals(e.runnerId, runnerId, StringComparison.OrdinalIgnoreCase));
        RunnerRecord runner = entry != null
            ? new RunnerRecord { runnerId = entry.runnerId, name = entry.header.name, blurb = entry.header.blurb, cover = entry.header.cover, isDefault = entry.header.isDefault, lua = entry.source }
            : RunnerDefaults.BuiltIn(src);
        var levels = TitleLevels.Discover(root);
        var byKey = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        var runnerLevels = new List<RunnerLevel>();
        foreach (var l in levels)
        {
            string address = RunnerIds.IsRunnerOnlyAsset(l.assetPath) ? RunnerIds.LevelAddress(contentId, l.name) : contentId + "/Levels/" + l.name;
            runnerLevels.Add(new RunnerLevel { id = l.name, name = RunnerLevelsFromManifest.Pretty(l.name), address = address, widthFt = l.sizeFeet.x, depthFt = l.sizeFeet.y });
            byKey[address] = l.prefab; byKey[l.name] = l.prefab;
        }
        RunnerHost.LevelProvider = a => Task.FromResult(byKey.TryGetValue(a ?? "", out var g) ? g : (byKey.TryGetValue(RunnerBoardRules.Leaf(a), out g) ? g : null));
        RunnerHost.Placeholders = true;
        RunnerHost.ShowBoard = true;

        // 3. the title's Game Manager + Player rig (core does this through its package runtime; here: as the stage does)
        bool core = Type.GetType("DreamPark.Runners.RunnerCoreBridge, Assembly-CSharp") != null;
        if (!core)
        {
            RunnerHost.OnRun = h => SpawnServices(src, contentId);
            RunnerHost.OnStop = (h, why) => DropServices();
            RunnerHost.OnLevelPlaced = r =>
            {
                if (Get("host") is DreamParkPackageHost ph && ph != null)
                {
                    var m = r.gameObject.GetComponent<DreamParkPackageMembership>() ?? r.gameObject.AddComponent<DreamParkPackageMembership>();
                    m.host = ph; m.packageInstanceId = contentId + ":runner"; m.occurrenceId = "runner:" + r.loadId;
                }
            };
        }

        var board = Get("board") as GameObject;
        if (board == null) { board = new GameObject("[E2E] Board (the web-setup play rect)"); Set("board", board); }
        var host = RunnerHost.Ensure();
        host.Run(locationId, contentId, runner, runnerLevels, w, d, "e2e-" + headsetId, board.transform);
        Set("file", path);
        return $"[e2e] {headsetId}: /identity says {contentId} · way to play '{np.GetField("runnerName")?.stringValue}' ({runnerId}) on a {w:0}×{d:0} ft board → " +
               $"running {(entry != null ? "the local Runners/" + entry.runnerId + ".lua.txt" : "the built-in default")} with {runnerLevels.Count} level(s)" +
               (core ? " (core project: title services via RunnerCoreBridge)" : $"; Game Manager {(Get("gm") != null ? "spawned" : "MISSING")}, Player {(Get("player") != null ? "spawned" : "none")}");
    }

    static void SpawnServices(string src, string contentId)
    {
        DropServices();
        var go = new GameObject("[E2E] Title services (Game Manager + Player)");
        var host = go.AddComponent<DreamParkPackageHost>();
        host.kind = DreamParkPackageKind.Arena; host.contentId = contentId;
        string root = "Assets/Content/" + src;
        GameObject gmPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Game Container.prefab")
            ?? AssetDatabase.FindAssets("\"Game Container\" t:Prefab", new[] { root }).Select(AssetDatabase.GUIDToAssetPath).Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p)).FirstOrDefault(g => g != null);
        if (gmPrefab != null)
        {
            var gm = UnityEngine.Object.Instantiate(gmPrefab, go.transform);
            gm.name = "Game Manager";
            host.container = gm;
            if (gm.GetComponent<NetId>() == null) gm.AddComponent<NetId>();
            var m = gm.GetComponent<DreamParkPackageMembership>() ?? gm.AddComponent<DreamParkPackageMembership>();
            m.host = host; m.packageInstanceId = contentId + ":runner";
            Set("gm", gm);
        }
        var playerPrefab = AssetDatabase.FindAssets("t:Prefab", new[] { root }).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0)
            .OrderBy(p => string.Equals(Path.GetFileNameWithoutExtension(p), "Player", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p))
            .FirstOrDefault(g => g != null && g.GetComponent<PlayerRig>() != null && !g.name.StartsWith("Phone", StringComparison.OrdinalIgnoreCase));
        if (playerPrefab != null && UnityEngine.Object.FindAnyObjectByType<PlayerRig>() == null)
        {
            var p = UnityEngine.Object.Instantiate(playerPrefab, go.transform);
            p.name = contentId + "/Player";
            Set("player", p);
        }
        Set("host", host); Set("services", go);
    }

    static void DropServices()
    {
        if (Get("services") is GameObject g && g != null) UnityEngine.Object.Destroy(g);
        Set("services", null); Set("host", null); Set("gm", null); Set("player", null);
    }

    public static string Status()
    {
        var h = RunnerHost.Instance;
        var d = h != null ? h.Director : null;
        if (d == null) return "[e2e] nothing runs";
        var roots = UnityEngine.Object.FindObjectsByType<RunnerLevelRoot>(FindObjectsSortMode.None);
        return $"[e2e] {d.ContentId}/{d.Runner?.runnerId} · {d.Board.Count} tile(s) · leader={d.IsLeader} directing={d.Directing} · {d.Describe()} · " +
               $"placed: {string.Join(", ", roots.Select(r => r.level + "@" + string.Join("+", r.tiles.Select(t => t + 1)) + (r.ready ? "" : "(loading)")))} · " +
               $"state {d.State?.GetJson("rs") ?? "{}"}{(d.LastError != null ? " · ERROR " + d.LastError : "")}";
    }

    /// args: "<tile>|<result>" — gameplay says the level on that tile is done (as dp.level.complete would).
    public static string Finish(string arg)
    {
        var d = RunnerHost.Instance?.Director;
        if (d == null) return "[e2e] nothing runs";
        var a = (arg ?? "1").Split('|');
        int tile = int.TryParse(a[0], out int t) ? t - 1 : 0;
        var l = d.SeenLoads(d.Loads, d.MyZone()).FirstOrDefault(x => x.Covers(tile) && !x.done);
        if (l == null) return "[e2e] no open level on tile " + (tile + 1);
        d.ReportComplete(l.id, a.Length > 1 ? a[1] : "won");
        return $"[e2e] reported {l.level} (load {l.id}) {(a.Length > 1 ? a[1] : "won")}";
    }

    /// args: "<tile>" — move the editor player (the camera) to the centre of that tile, eye height.
    public static string Stand(string arg)
    {
        var d = RunnerHost.Instance?.Director; var board = Get("board") as GameObject; var cam = Camera.main;
        if (d == null || board == null || cam == null) return "[e2e] nothing runs / no camera";
        var t = d.Board.Tile((int.TryParse(arg, out int n) ? n : 1) - 1);
        if (t == null) return "[e2e] no such tile";
        cam.transform.position = board.transform.TransformPoint(new Vector3(t.cx, 1.6f, t.cz - t.d * 0.45f));
        cam.transform.rotation = Quaternion.LookRotation(board.transform.TransformDirection(Vector3.forward) + Vector3.down * 0.25f);
        return $"[e2e] standing in tile {arg}";
    }

    public static string Stop()
    {
        RunnerHost.Instance?.Stop("e2e harness stop");
        DropServices();
        if (Get("board") is GameObject b && b != null) UnityEngine.Object.Destroy(b);
        Set("board", null);
        RunnerHost.LevelProvider = null; RunnerHost.OnRun = null; RunnerHost.OnStop = null; RunnerHost.OnLevelPlaced = null;
        return "[e2e] stopped";
    }

    static string HttpGet(string url, string auth, out long code)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Authorization", auth);
            req.timeout = 20;
            var op = req.SendWebRequest();
            var t0 = DateTime.UtcNow;
            while (!op.isDone && (DateTime.UtcNow - t0).TotalSeconds < 22) System.Threading.Thread.Sleep(20);
            code = req.responseCode;
            return req.downloadHandler?.text ?? "";
        }
    }

    static string Trunc(string s) => s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
}
