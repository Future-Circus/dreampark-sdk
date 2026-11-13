// Playlist Studio SCRIPTING check + screenshots (repair Oct 20 2026, pl-generate). Not shipped.
// Loads SuperAdventureLand from disk WITHOUT importing it (library from prefab YAML, its .dreampark-goals blocks, the
// playlists file at snapshot/oct20-pre-repair), runs goal blocks through the Studio's Test (PlaylistGoalTest: a fresh
// XLua LuaEnv in this editor), and shows/captures the Library goal panel, the Script tab and the Campaign tab.
//   u.sh run_script --file Tools/Builders/PlaylistScriptingShots.cs --entry PlaylistScriptingShots.TestBlocks
//   u.sh run_script --file Tools/Builders/PlaylistScriptingShots.cs --entry PlaylistScriptingShots.ShowLibrary   (then Capture)
//   … ShowScript, ShowCampaign; Capture; Cleanup
// Output: dreampark-core/_screenshots/repair-oct20/pl-generate/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Defective.JSON;
using DreamPark;
using DreamPark.Editor;
using DreamPark.Editor.PlaylistStudio;
using UnityEditor;
using UnityEngine;

public static class PlaylistScriptingShots
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Projects => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    static string Core => Path.Combine(Projects, "dreampark-core");
    static string Web => Path.Combine(Projects, "DreamPark-Web");
    const string Title = "SuperAdventureLand";
    static string TitleRoot => Path.Combine(Home, "Dropbox/2025/superadventureland/Assets/Content/SuperAdventureLand");
    static string PlaylistsFile => Path.Combine(Home, "Library/Caches/dreampark-playlist-studio/roundtrip/out/SuperAdventureLand.orig.json");
    static string Scripted => Path.Combine(Web, ".emulator-data/pl-generate/SuperAdventureLand.meadow.scripted.json");
    static string GeneratedGoals => Path.Combine(Web, ".emulator-data/pl-generate/SuperAdventureLand.goals.json");
    static string OutDir => Path.Combine(Core, "_screenshots/repair-oct20/pl-generate");

    /// Every SAL goal block (the title's hand-written .dreampark-goals + the generated library) through the Studio Test.
    public static string TestBlocks()
    {
        var rows = new List<string>();
        int ok = 0, n = 0;
        var blocks = new SortedDictionary<string, string>(PlaylistGoalStore.Load(Path.Combine(TitleRoot, PlaylistGoalStore.Folder)), StringComparer.Ordinal);
        var gen = File.Exists(GeneratedGoals) ? new JSONObject(File.ReadAllText(GeneratedGoals)).GetField("blocks") : null;
        foreach (string key in new[] { "A_CoinGarden.collect", "A_DefensibleBlock.clear", "A_LavaPits.jump", "A_MoleHillCannon.boss", "A_KingJubbo.boss", "A_IceTraversal.jump", "A_GreenCoinHunt.find" })
        {
            foreach (var (src, lua) in new[] { ("title", blocks.TryGetValue(key, out string t) ? t : null), ("generated", gen?.GetField(key)?.GetField("lua")?.stringValue) })
            {
                if (lua == null) continue;
                n++;
                string kind = key.Substring(key.LastIndexOf('.') + 1);
                PlaylistGoalTest.Result r = PlaylistGoalTest.Run(lua, kind, null, null, key.Substring(0, key.LastIndexOf('.')));
                if (r.settled) ok++;
                rows.Add($"{key,-26} {src,-9} {r.Summary}");
            }
        }
        string compileErr = PlaylistGoalTest.Compile("return function(stop) stop.timer(1, function() end end");
        rows.Add("compile check of a broken block → " + (compileErr ?? "(compiled?!)"));
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "studio-test-xlua.txt"), string.Join("\n", rows) + "\n");
        Debug.Log("[pl-generate] Studio Test (XLua):\n" + string.Join("\n", rows));
        return $"{ok}/{n} blocks settled in the editor's XLua VM; written {Path.Combine(OutDir, "studio-test-xlua.txt")}\n" + string.Join("\n", rows);
    }

    public static string ShowLibrary()
    {
        var s = Session();
        var w = Open(s, PlaylistStudioWindow.EditTab, 0, "01-library-goal-blocks");
        Set(w, "libSelected", "A_LavaPits");
        string lua = s.goals.TryGetValue("A_LavaPits.jump", out string l) ? l : null;
        if (lua != null)
        {
            Set(w, "goalTestKey", "A_LavaPits.jump");
            Set(w, "goalTest", PlaylistGoalTest.Run(lua, "jump", null, null, "A_LavaPits"));
        }
        w.Repaint();
        return "library shown (A_LavaPits selected, its jump block tested); now Capture";
    }

    public static string ShowScript()
    {
        var s = Session();
        int meadow = s.data.playlists.FindIndex(p => p.id == "meadow");
        // The Script tab after "Generate scripting": the Playlist API's answer for meadow (local emulator run) applied as the
        // window does (stop ids + goals + Lua + Dreamie), in memory only.
        if (meadow >= 0 && File.Exists(Scripted)) ApplyScripted(s.data.playlists[meadow], new JSONObject(File.ReadAllText(Scripted)));
        var w = Open(s, PlaylistStudioWindow.ScriptTab, Math.Max(0, meadow), "02-script-tab-meadow");
        var p = s.data.playlists[Math.Max(0, meadow)];
        var stop = p.arc.SelectMany(x => x.stops).FirstOrDefault(x => x.attraction == "A_DefensibleBlock");
        if (stop != null && s.goals.TryGetValue("A_DefensibleBlock.clear", out string lua))
        {
            Set(w, "goalTestKey", "stop:" + stop.id);
            Set(w, "goalTest", PlaylistGoalTest.Run(lua, "clear", null, null, "A_DefensibleBlock"));
        }
        w.Repaint();
        return "script tab shown for meadow; now Capture";
    }

    public static string ShowCampaign()
    {
        var s = Session();
        foreach (PackagePlaylist p in s.data.playlists)
            if (p.id == "meadow" && File.Exists(Scripted)) ApplyScripted(p, new JSONObject(File.ReadAllText(Scripted)));
        Open(s, PlaylistStudioWindow.CampaignTab, 0, "03-campaign-lua-flags-locks");
        return "campaign shown; now Capture";
    }

    static void ApplyScripted(PackagePlaylist p, JSONObject g)
    {
        var legs = g.GetField("arc")?.list ?? new List<JSONObject>();
        for (int i = 0; i < p.arc.Count && i < legs.Count; i++)
        {
            var stops = legs[i].GetField("stops")?.list ?? new List<JSONObject>();
            for (int j = 0; j < p.arc[i].stops.Count && j < stops.Count; j++)
            {
                if (string.IsNullOrEmpty(p.arc[i].stops[j].id)) p.arc[i].stops[j].id = stops[j].GetField("id")?.stringValue;
                if (stops[j].GetField("goal") != null) PlaylistStudioJson.SetExtra(p.arc[i].stops[j], "goal", stops[j].GetField("goal"));
            }
        }
        foreach (string k in new[] { "lua", "dreamie" }) if (g.GetField(k) != null) PlaylistStudioJson.SetExtra(p, k, g.GetField(k));
    }

    static PlaylistStudioWindow Open(PlaylistStudioSession s, int tab, int select, string shot)
    {
        EditorPrefs.SetBool("DreamPark.PlaylistStudio.TipDismissed", true);
        var w = PlaylistStudioWindow.OpenSession(s, tab, select);
        w.position = new Rect(60, 60, 1500, 940);
        EditorPrefs.SetString("PlaylistScriptingShots.next", shot);
        w.Repaint();
        return w;
    }

    static void Set(object o, string field, object value)
        => o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)?.SetValue(o, value);

    public static string Cleanup()
    {
        EditorPrefs.DeleteKey("DreamPark.PlaylistStudio.TipDismissed");
        var w = Resources.FindObjectsOfTypeAll<PlaylistStudioWindow>().FirstOrDefault();
        if (w != null) w.Close();
        return "cleaned up";
    }

    public static string Capture()
    {
        var w = Resources.FindObjectsOfTypeAll<PlaylistStudioWindow>().FirstOrDefault();
        if (w == null) return "no Studio window";
        Directory.CreateDirectory(OutDir);
        string path = Path.Combine(OutDir, EditorPrefs.GetString("PlaylistScriptingShots.next", "shot") + ".png");
        int width = (int)w.position.width, height = (int)w.position.height;
        Texture2D tex = null;
        try
        {
            object parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w);
            MethodInfo grab = parent?.GetType().GetMethod("GrabPixels", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (grab != null)
            {
                float ppp = EditorGUIUtility.pixelsPerPoint;
                var rt = new RenderTexture((int)(width * ppp), (int)(height * ppp), 0, RenderTextureFormat.ARGB32);
                grab.Invoke(parent, new object[] { rt, new Rect(0, 0, width * ppp, height * ppp) });
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                Color32[] px = tex.GetPixels32(), flipped = new Color32[px.Length];
                for (int y = 0; y < rt.height; y++) Array.Copy(px, y * rt.width, flipped, (rt.height - 1 - y) * rt.width, rt.width);
                tex.SetPixels32(flipped);
                tex.Apply();
                RenderTexture.active = null;
                rt.Release();
            }
        }
        catch (Exception e) { Debug.LogWarning("[pl-generate] GrabPixels failed: " + e.Message); }
        if (tex == null) return "could not grab the window";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        return "wrote " + path + " " + tex.width + "x" + tex.height;
    }

    // ── SuperAdventureLand from disk (same scan as PlaylistStudioShots.Session) ──

    static readonly Regex V2 = new Regex(@"\{x: ([-\d.e]+), y: ([-\d.e]+)\}");
    static float F(string x) => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture);

    static PlaylistStudioSession Session()
    {
        var s = new PlaylistStudioSession { titleId = Title, titleName = "Super Adventure Land" };
        var luaByGuid = new Dictionary<string, string>();
        foreach (string lua in Directory.GetFiles(TitleRoot, "*.lua.txt", SearchOption.AllDirectories).Where(p => !p.Contains("~/")))
        { string g = Guid(lua); if (g != null) luaByGuid[g] = File.ReadAllText(lua); }
        string badges = Path.Combine(TitleRoot, ".badges.json");
        if (File.Exists(badges))
            foreach (JSONObject e in new JSONObject(File.ReadAllText(badges)).GetField("entries")?.list ?? new List<JSONObject>())
                s.badges.Add(new PlaylistLibrary.BadgeInfo { id = e.GetField("badgeId")?.stringValue, name = e.GetField("name")?.stringValue, description = e.GetField("description")?.stringValue,
                    earnAt = (e.GetField("earnAt")?.list ?? new List<JSONObject>()).Select(x => x.stringValue).ToList() });
        var catalog = PlaylistChallenges.ReadCatalog(File.ReadAllText(Path.Combine(Core, "Tools/Dreamie/objectives/catalog.json")), Title);
        s.hasCatalog = catalog.Count > 0;
        string assets = TitleRoot.Substring(0, TitleRoot.IndexOf("/Assets/") + 8);
        foreach (string prefab in Directory.GetFiles(TitleRoot, "A_*.prefab", SearchOption.AllDirectories).Where(p => !p.Contains("~/") && !p.Contains("/DreamSequence/")))
        {
            string text = File.ReadAllText(prefab);
            Vector2? authored = null, shrink = null; string display = null;
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("authoredFootprintMeters:") && authored == null) { var m = V2.Match(t); if (m.Success) authored = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (t.StartsWith("shrinkFootprintMeters:") && shrink == null) { var m = V2.Match(t); if (m.Success) shrink = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (display == null && line.StartsWith("  displayName: ")) display = line.Substring(15).Trim().Trim('\'', '"');
            }
            if (authored == null || authored.Value.x <= 0f) continue;
            string leaf = Path.GetFileNameWithoutExtension(prefab);
            var luaTexts = Regex.Matches(text, @"guid: ([0-9a-f]{32})").Select(m => m.Groups[1].Value).Distinct().Where(luaByGuid.ContainsKey).Select(g => luaByGuid[g]).ToList();
            var smaller = new List<Vector2>(); if (shrink.HasValue) smaller.Add(shrink.Value);
            string preview = Path.Combine(TitleRoot, "Previews", leaf + ".png");
            var item = new PlaylistLibraryItem
            {
                guid = Guid(prefab), leaf = leaf, assetPath = null, resourceName = prefab.Substring(assets.Length, prefab.Length - assets.Length - 7),
                displayName = display ?? "", size = "Custom", hasPackingBake = true, authored = authored.Value, previewPath = File.Exists(preview) ? preview : null,
            };
            item.ladder = PlaylistLibrary.Ladder(item.authored, smaller);
            item.roleHint = PlaylistLibrary.RoleHint(leaf, display, luaTexts);
            item.objectives = PlaylistLibrary.InferObjectives(item.roleHint, luaTexts, out item.objectivesSource);
            item.luaSignals = PlaylistLibrary.LuaSignals(luaTexts);
            item.badges = s.badges.Where(b => b.earnAt.Contains(leaf)).Select(b => b.id).ToList();
            if (catalog.TryGetValue(leaf, out var row)) PlaylistChallenges.Apply(item, row);
            if (s.library.All(x => x.leaf != leaf)) s.library.Add(item);
        }
        s.library.Sort((a, b) => string.CompareOrdinal(a.leaf, b.leaf));
        s.data = PlaylistStudioJson.ReadData(File.ReadAllText(PlaylistsFile));
        s.goals = PlaylistGoalStore.Load(Path.Combine(TitleRoot, PlaylistGoalStore.Folder));
        return s;
    }

    static string Guid(string path)
    {
        string meta = path + ".meta";
        if (!File.Exists(meta)) return null;
        var m = Regex.Match(File.ReadAllText(meta), @"guid: ([0-9a-f]{32})");
        return m.Success ? m.Groups[1].Value : null;
    }
}
