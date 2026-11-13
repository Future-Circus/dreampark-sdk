// Screenshots of the Playlist Studio (sdk-playlist-studio, Oct 18 2026). Not shipped. Loads a REAL title from disk
// WITHOUT importing it into this project (ArcadeLand: prefab bakes, previews, badges, the first-party challenge
// catalog, the hand-drafted playlists and the live generator run), opens the Studio on it and grabs the window.
//   u.sh run_script --file Tools/Builders/PlaylistStudioShots.cs --entry PlaylistStudioShots.Show0   (… Show3)
//   u.sh run_script --file Tools/Builders/PlaylistStudioShots.cs --entry PlaylistStudioShots.Capture
// Env-free: paths are below. Output: dreampark-core/_screenshots/dreamie-everywhere/playlist-studio/.
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

public static class PlaylistStudioShots
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Projects => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    static string Core => Path.Combine(Projects, "dreampark-core");
    static string TitleRoot => Path.Combine(Home, "Dropbox/2026/arcadeland/Assets/Content/ArcadeLand");
    static string OutDir => Path.Combine(Core, "_screenshots/dreamie-everywhere/playlist-studio");
    static string Live => Path.Combine(Application.dataPath, "..", "Tools/playlist-studio-offline/out/live-arcadeland.response.json");

    const string Fold = "DreamPark.PlaylistStudio.fold.";
    static readonly string[] Sections = { "bookends", "rules", "fit", "gates", "details", "fitplans", "warnings" };

    public static string Show0() => Show(PlaylistStudioWindow.HomeTab, "01-simple-playlist-cards", 1, tip: true);
    public static string Show1() => Show(PlaylistStudioWindow.EditTab, "02-edit-simple", 1);
    public static string Show2() => Show(PlaylistStudioWindow.EditTab, "03-edit-advanced-open", 1, advanced: true);
    public static string Show3() => Show(PlaylistStudioWindow.CampaignTab, "04-campaign-chapters-unlocks", 0);
    public static string Show4() => Show(PlaylistStudioWindow.GenerateTab, "05-generate-live-arcadeland", 0);
    public static string Show5() => Show(PlaylistStudioWindow.ScriptTab, "06-script-placeholder", 1);
    public static string Show6() => Show(PlaylistStudioWindow.EditTab, "07-edit-fit-plans", 0, advanced: true, scrollFit: true);

    /// Puts the shared editor back: tip pref and fold states as they were before the shots (defaults).
    public static string Cleanup()
    {
        EditorPrefs.DeleteKey("DreamPark.PlaylistStudio.TipDismissed");
        foreach (string k in Sections) SessionState.EraseBool(Fold + k);
        var w = Resources.FindObjectsOfTypeAll<PlaylistStudioWindow>().FirstOrDefault();
        if (w != null) w.Close();
        return "cleaned up";
    }

    static string Show(int tab, string name, int select, bool scrollFit = false, bool tip = false, bool advanced = false)
    {
        PlaylistStudioSession s = Session();
        if (tab == PlaylistStudioWindow.GenerateTab && File.Exists(Live))
        {
            JSONObject file = new JSONObject(File.ReadAllText(Live));
            s.TakeResponse(GeneratorResponse.Parse((file.GetField("response") ?? file).Print()), "generate");
            s.messages.Clear();
            s.messages.Add("Live run: claude-opus-5-5 via DreamPark-Web, 10 proposals in 247 s; every one repaired, validated and fit-checked here.");
        }
        EditorPrefs.SetBool("DreamPark.PlaylistStudio.TipDismissed", !tip);
        foreach (string k in Sections) SessionState.SetBool(Fold + k, advanced && k != "warnings");
        var shown = s.data.playlists.Count > select ? s.data.playlists[select] : null;
        if (shown != null)
            for (int i = 0; i < shown.arc.Count; i++)
            {
                SessionState.SetBool(Fold + $"leg.{shown.id}.{i}", advanced && i == 1);
                for (int j = 0; j < shown.arc[i].stops.Count; j++) SessionState.SetBool(Fold + $"stop.{shown.arc[i].id}.{j}", advanced && i == 1);
            }
        var w = PlaylistStudioWindow.OpenSession(s, tab, select);
        w.position = new Rect(60, 60, 1500, 940);
        EditorPrefs.SetString("PlaylistStudioShots.next", name);
        if (scrollFit) w.ScrollEditorTo(100000f);
        w.Repaint();
        return "shown " + name + " (" + s.library.Count + " attractions, " + s.data.playlists.Count + " playlists); now run Capture";
    }

    public static string Capture()
    {
        var w = Resources.FindObjectsOfTypeAll<PlaylistStudioWindow>().FirstOrDefault();
        if (w == null) return "no Studio window";
        Directory.CreateDirectory(OutDir);
        string path = Path.Combine(OutDir, EditorPrefs.GetString("PlaylistStudioShots.next", "shot") + ".png");
        int width = (int)w.position.width, height = (int)w.position.height;
        Texture2D tex = null;
        try
        {
            // The window's own back buffer (GUIView.GrabPixels): no OS screen-recording permission needed.
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
                // The back buffer is bottom-up: flip rows so the PNG reads top-down.
                Color32[] px = tex.GetPixels32(), flipped = new Color32[px.Length];
                for (int y = 0; y < rt.height; y++) Array.Copy(px, y * rt.width, flipped, (rt.height - 1 - y) * rt.width, rt.width);
                tex.SetPixels32(flipped);
                tex.Apply();
                RenderTexture.active = null;
                rt.Release();
            }
        }
        catch (Exception e) { Debug.LogWarning("[playlist-studio-shots] GrabPixels failed: " + e.Message); }
        if (tex == null)
        {
            Color[] px = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(w.position.position, width, height);
            tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.SetPixels(px); tex.Apply();
        }
        File.WriteAllBytes(path, tex.EncodeToPNG());
        return "wrote " + path + " " + tex.width + "x" + tex.height;
    }

    // ── a title from disk (mirror of Tools/playlist-studio-offline Program.Title.cs ScanTitle) ──

    static readonly Regex V2 = new Regex(@"\{x: ([-\d.e]+), y: ([-\d.e]+)\}");
    static float F(string x) => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture);

    static PlaylistStudioSession Session()
    {
        var s = new PlaylistStudioSession { titleId = "ArcadeLand", titleName = "ArcadeLand" };
        var luaByGuid = new Dictionary<string, string>();
        foreach (string lua in Directory.GetFiles(TitleRoot, "*.lua.txt", SearchOption.AllDirectories).Where(p => !p.Contains("~/")))
        { string g = Guid(lua); if (g != null) luaByGuid[g] = File.ReadAllText(lua); }
        foreach (JSONObject e in new JSONObject(File.ReadAllText(Path.Combine(TitleRoot, ".badges.json"))).GetField("entries").list)
            s.badges.Add(new PlaylistLibrary.BadgeInfo { id = e.GetField("badgeId").stringValue, name = e.GetField("name")?.stringValue, description = e.GetField("description")?.stringValue,
                earnAt = (e.GetField("earnAt")?.list ?? new List<JSONObject>()).Select(x => x.stringValue).ToList() });
        var catalog = PlaylistChallenges.ReadCatalog(File.ReadAllText(Path.Combine(Core, "Tools/Dreamie/objectives/catalog.json")), "ArcadeLand");
        s.hasCatalog = catalog.Count > 0;
        string assets = TitleRoot.Substring(0, TitleRoot.IndexOf("/Assets/") + 8);
        foreach (string prefab in Directory.GetFiles(TitleRoot, "*.prefab", SearchOption.AllDirectories).Where(p => !p.Contains("~/") && !p.Contains("/DreamSequence/")))
        {
            string text = File.ReadAllText(prefab);
            Vector2? authored = null, shrink = null; var frames = new List<Vector2>(); bool inFrames = false; string display = null;
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("authoredFootprintMeters:") && authored == null) { var m = V2.Match(t); if (m.Success) authored = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (t.StartsWith("shrinkFootprintMeters:") && shrink == null) { var m = V2.Match(t); if (m.Success) shrink = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (line.StartsWith("  layoutFrames:")) inFrames = true;
                else if (inFrames && line.StartsWith("  ") && !line.StartsWith("   ") && !line.StartsWith("  -")) inFrames = false;
                else if (inFrames && t.StartsWith("sizeFeet:")) { var m = V2.Match(t); if (m.Success) frames.Add(new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)) * 0.3048f); }
                else if (display == null && line.StartsWith("  displayName: ")) display = line.Substring(15).Trim().Trim('\'', '"');
            }
            if (authored == null || authored.Value.x <= 0f) continue;
            string leaf = Path.GetFileNameWithoutExtension(prefab);
            var luaTexts = Regex.Matches(text, @"guid: ([0-9a-f]{32})").Select(m => m.Groups[1].Value).Distinct().Where(luaByGuid.ContainsKey).Select(g => luaByGuid[g]).ToList();
            var smaller = new List<Vector2>(); if (shrink.HasValue) smaller.Add(shrink.Value); smaller.AddRange(frames);
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
            s.library.Add(item);
        }
        s.library.Sort((a, b) => string.CompareOrdinal(a.leaf, b.leaf));
        s.data = PlaylistStudioJson.ReadData(File.ReadAllText(Path.Combine(Core, "Tools/Dreamie/playlists-draft/ArcadeLand.json")));
        // Demo unlocks for the campaign picture (in memory only): the marathon after Insert Coin, the Boss Rush with a badge.
        var marathon = s.data.playlists.FirstOrDefault(p => p.id == "high-score-marathon");
        if (marathon != null) { marathon.requires = new List<string> { "playlist:insert-coin" }; marathon.chapter = "After your first credit"; }
        var bossRush = s.data.playlists.FirstOrDefault(p => p.id == "boss-rush");
        if (bossRush != null) bossRush.requires = new List<string> { "badge:al_dungeon_deep5" };
        var insert = s.data.playlists.FirstOrDefault(p => p.id == "insert-coin");
        if (insert != null) { insert.sets = new List<string> { "first-credit" }; insert.chapter = "Start here"; }
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
