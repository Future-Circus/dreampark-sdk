// Playlist Studio offline harness, part 2: chapters & unlocks, the challenge catalog, and real titles on disk
// (a file-based stand-in for the Unity library builder, used by the live generation run and the migration check).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Defective.JSON;
using DreamPark;
using DreamPark.Editor;
using DreamPark.Editor.PlaylistStudio;
using UnityEngine;

static partial class Program
{
    // ───────────────────────── challenge catalog ─────────────────────────

    static void CatalogTests()
    {
        // § 12.2 title file shape.
        var rows = PlaylistChallenges.ReadCatalog("{\"schemaVersion\":1,\"attractions\":{\"A_CoinGarden\":{\"default\":\"collect\",\"supports\":[\"collect\",\"clock\",\"no_hit\",\"bogus\"],\"unit\":\"coins\",\"expectedSec\":60}}}");
        Check(rows.TryGetValue("A_CoinGarden", out var r) && r.defaultKind == "collect" && r.Supported.SequenceEqual(new[] { "collect", "clock", "no_hit" }) && r.expectedSec == 60,
            "§ 12.2 .dreampark-challenges.json shape");
        // The first-party catalog (whole file + titleId).
        string catalog = Path.Combine(CoreDir, "Tools/Dreamie/objectives/catalog.json");
        if (File.Exists(catalog))
        {
            var al = PlaylistChallenges.ReadCatalog(File.ReadAllText(catalog), "ArcadeLand");
            Check(al.TryGetValue("A_BrickBlitz", out var bb) && bb.defaultKind == "clear" && bb.kinds["combo"] == "build" && !bb.Supported.Contains("combo") && bb.Supported.Contains("score"),
                "first-party catalog: ArcadeLand Brick Blitz (default clear, combo is build-only)");
            var item = Item("A_BrickBlitz", 7.3f, 11f, 2.1f, 2.7f);
            PlaylistChallenges.Apply(item, bb);
            Check(item.objectivesSource == "catalog" && item.objectives.Contains("score") && item.objectives.Contains("reach_exit") && item.objectives.Contains("time"),
                "catalog kinds → leg objectives: " + string.Join(",", item.objectives));
            string file = PlaylistChallenges.WriteTitleFile(al);
            var back = PlaylistChallenges.ReadCatalog(file);
            Check(back.Count == al.Count && back["A_BrickBlitz"].Supported.SequenceEqual(bb.Supported), "import → .dreampark-challenges.json → read back (" + back.Count + " attractions)");
            File.WriteAllText(Path.Combine(OutDir, "ArcadeLand.dreampark-challenges.json"), file);
        }
        else Console.WriteLine("  (first-party catalog not found; skipped)");
        Check(PlaylistChallenges.Derive("boss", "reach_exit") == "boss" && PlaylistChallenges.Derive("stage", "time") == "moment" && PlaylistChallenges.Derive("stage", "reach_exit") == "reach_end",
            "§ 12.1 derived challenge");
        var p = GoodPlaylist();
        p.arc[0].stops[1].challenge = "race"; p.arc[0].stops[1].bonus = new List<string> { "no_hit", "boss" };
        var lib = TestLibrary(); lib.First(i => i.leaf == "A_CoinGarden").challenges = new List<string> { "collect", "clock" };
        var w = PlaylistChallenges.Validate(p, a => lib.FirstOrDefault(i => i.leaf == a));
        Check(w.Count == 2 && w.All(x => !x.isError), "challenge warnings: a kind the catalog lacks + a bonus that is not a modifier");
        var json = PlaylistStudioJson.WritePlaylist(p, "T");
        var again = PlaylistStudioJson.ReadPlaylist(json);
        Check(again.arc[0].stops[1].challenge == "race" && again.arc[0].stops[1].bonus.Count == 2, "challenge + bonus round-trip");
    }

    // ───────────────────────── chapters & unlocks ─────────────────────────

    static PackagePlaylist Named(string id, params string[] requires)
    {
        var p = GoodPlaylist();
        p.id = id; p.name = PlaylistLibrary.Pretty(id);
        ContentPlaylistStore.Normalize(p);
        PlaylistGates.SetRequires(p, requires.Select(PlaylistGates.Parse));
        return p;
    }

    static void GateTests()
    {
        var meadow = Named("meadow"); meadow.continueWith = new List<string> { "caves" }; PlaylistGates.SetSets(meadow, new[] { "met-maple" }, new string[0]);
        meadow.chapter = "Chapter 1";
        var caves = Named("caves", "playlist:meadow"); caves.continueWith = new List<string> { "finale" };
        var bonus = Named("bonus-round", "badge:t_champ", "flag:met-maple");
        var finale = Named("finale", "playlist:caves");
        var data = new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { meadow, caves, bonus, finale } };
        var issues = PlaylistGates.Validate(data, new HashSet<string> { "t_champ" });
        Check(issues.Count == 0, "a clean campaign has no gate issues: " + string.Join("; ", issues));
        var nodes = PlaylistGates.Map(data, out var edges);
        string Col(string id) => nodes.First(n => n.playlist.id == id).column.ToString();
        Check(Col("meadow") == "0" && Col("caves") == "1" && Col("finale") == "2" && Col("bonus-round") == "0", "map columns follow the chain: " + string.Join(" ", nodes.Select(n => n.playlist.id + "@" + n.column)));
        Check(nodes.First(n => n.playlist.id == "meadow").starter && !nodes.First(n => n.playlist.id == "caves").starter, "starters = no requires");
        Check(edges.Count(e => e.unlock) == 2 && edges.Count(e => !e.unlock) == 2, $"edges: 2 unlocks, 2 continueWith (got {edges.Count(e => e.unlock)}/{edges.Count(e => !e.unlock)})");
        Check(PlaylistGates.Describe(bonus, null, b => b == "t_champ" ? "Champion" : b) == "with badge Champion, when met-maple", "gate in plain words");

        // Round trip, object-form refs.
        var json = PlaylistStudioJson.WritePlaylist(bonus, "T");
        var back = PlaylistStudioJson.ReadPlaylist(json);
        Check(back.requires.SequenceEqual(bonus.requires) && back.requiresJson == bonus.requiresJson, "requires round-trips (projection + canonical)");
        Check(json.GetField("requires").Print() == "[{\"badge\":\"t_champ\"},{\"flag\":\"met-maple\"}]", "requires is written in the canonical § 15.1 shape: " + json.GetField("requires").Print());
        var canon = new JSONObject("{\"id\":\"z\",\"kind\":\"adventure\",\"name\":\"Z\",\"pitch\":\"p\",\"arc\":[],"
            + "\"requires\":[\"lava-caves\",\"Zombiez/the-mall\",{\"badge\":\"x\"},{\"flag\":\"cr.partner\",\"is\":\"fire\"},{\"min\":2,\"of\":[\"a\",\"b\",{\"badge\":\"y\"}]}],"
            + "\"sets\":{\"flags\":{\"lava-cleared\":true,\"cr.partner\":\"fire\"},\"badges\":[\"sal_champion\"]},"
            + "\"window\":{\"from\":\"2026-12-01\",\"until\":\"2026-12-31\",\"label\":\"Winter\"},\"personal\":[{\"flag\":\"cr.partner\"}]}");
        var cp = PlaylistStudioJson.ReadPlaylist(canon);
        var again = PlaylistStudioJson.WritePlaylist(cp, "T");
        Check(again.GetField("requires").Print() == canon.GetField("requires").Print(), "canonical requires (ids, T/id, {badge}, {flag,is}, {min,of}) survive: " + again.GetField("requires").Print());
        Check(again.GetField("sets").Print() == canon.GetField("sets").Print(), "canonical sets {flags, badges} survive: " + again.GetField("sets").Print());
        Check(again.GetField("window") != null && again.GetField("personal") != null, "window and personal are carried untouched");
        Check(cp.requires.SequenceEqual(new[] { "playlist:lava-caves", "playlist:Zombiez/the-mall", "badge:x", "flag:cr.partner" }) && cp.sets.SequenceEqual(new[] { "lava-cleared", "cr.partner" }),
            "the projection matches playlist-model's (prefixed refs, flag ids): " + string.Join(",", cp.requires) + " | " + string.Join(",", cp.sets));
        Check(PlaylistGates.Describe(cp) == "after lava-caves, after Zombiez the-mall, with badge x, when cr.partner is fire, 2 of (after a / after b / with badge y)", "words: " + PlaylistGates.Describe(cp));
        var legacy = new JSONObject("{\"requires\":{\"all\":[{\"playlist\":\"meadow\"},{\"type\":\"badge\",\"id\":\"x\"},\"flag:y\"]},\"sets\":[\"flag:z\",\"w\"]}");
        Check(PlaylistGates.ReadRequires(legacy.GetField("requires")).SequenceEqual(new[] { "meadow", "{\"badge\":\"x\"}", "{\"flag\":\"y\"}" })
            && PlaylistGates.ReadSets(legacy.GetField("sets")).SequenceEqual(new[] { "flag:z", "flag:w" }), "the first proposal's shapes are still read");
        // Empty / garbage containers never throw (Defective.JSON throws on bare "[]" / "{}"): they read as "none".
        var empty = PlaylistStudioJson.ReadPlaylist(new JSONObject("{\"id\":\"e\",\"kind\":\"adventure\",\"name\":\"E\",\"pitch\":\"p\",\"arc\":[],\"requires\":[],\"sets\":{}}"));
        var junk = Named("j"); junk.requiresJson = "[]"; junk.setsJson = "{}"; junk.windowJson = "{"; junk.personalJson = "garbage";
        bool noThrow = true;
        try { PlaylistGates.Requires(junk); PlaylistGates.Sets(junk); PlaylistStudioJson.WritePlaylist(junk, "T"); PlaylistGates.Requires(empty); PlaylistStudioJson.WritePlaylist(empty, "T"); }
        catch (Exception ex) { noThrow = false; Console.WriteLine("  threw: " + ex.Message); }
        Check(noThrow && PlaylistGates.IsStarter(empty) && PlaylistGates.IsStarter(junk) && PlaylistGates.Sets(junk).flags.Count == 0, "empty or garbage gate text reads as none, never throws");
        var minBad = Named("m", "x"); PlaylistGates.SetRequires(minBad, new[] { PlaylistGates.Parse("{\"min\":3,\"of\":[\"a\",\"b\"]}") });
        Check(PlaylistGates.Validate(new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { Named("a"), Named("b"), minBad } }, null)
            .Any(x => x.isError && x.message.Contains("can never hold")), "an impossible {min, of} is an error");

        // Errors.
        var loopA = Named("a", "playlist:b"); var loopB = Named("b", "playlist:a");
        var bad = Named("bad", "playlist:nope", "level:3"); bad.continueWith = new List<string> { "ghost" };
        var e = PlaylistGates.Validate(new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { Named("open"), loopA, loopB, bad } }, null);
        Check(e.Count(x => x.isError && x.message.Contains("loop")) == 2, "a requires loop is an error on both");
        Check(e.Any(x => x.isError && x.message.Contains("'nope'")) && e.Any(x => x.isError && x.message.Contains("level:3")) && e.Any(x => x.isError && x.message.Contains("ghost")),
            "unknown playlist, bad ref kind and unknown continueWith are errors");
        var allLocked = PlaylistGates.Validate(new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { Named("x", "badge:t"), Named("y", "x") } }, null);
        Check(allLocked.Any(x => x.isError && x.playlistId == null && x.message.Contains("every playlist is locked")), "every playlist locked → error");
        var warn = PlaylistGates.Validate(new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { Named("o"), Named("p", "flag:never-set", "badge:nope") } }, new HashSet<string> { "t_champ" });
        Check(warn.Count == 2 && warn.All(x => !x.isError), "an unset flag and an unknown badge are warnings");

        // The SAL drafts' real chain.
        string sal = Path.Combine(CoreDir, "Tools/Dreamie/playlists-draft/SuperAdventureLand.json");
        if (File.Exists(sal))
        {
            var d = PlaylistStudioJson.ReadData(File.ReadAllText(sal));
            var gi = PlaylistGates.Validate(d, null);
            Check(!gi.Any(x => x.isError), "SAL drafts: their continueWith chain is valid " + string.Join("; ", gi.Where(x => x.isError)));
            var m = PlaylistGates.Map(d, out var se);
            Console.WriteLine("  SAL campaign: " + string.Join("  ", m.OrderBy(n => n.column).ThenBy(n => n.row).Select(n => $"{n.column}:{n.playlist.id}")));
            Check(m.First(n => n.playlist.id == "great-sun-heist").column > m.First(n => n.playlist.id == "meadow").column, "SAL: the Sun Heist sits after the Meadow on the map");
        }
    }

    // ───────────────────────── a real title on disk (approximates the Unity library builder) ─────────────────────────

    static readonly Regex V2 = new Regex(@"\{x: ([-\d.e]+), y: ([-\d.e]+)\}");
    static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-f]{32})");

    static string MetaGuid(string path)
    {
        string meta = path + ".meta";
        if (!File.Exists(meta)) return null;
        var m = Regex.Match(File.ReadAllText(meta), @"guid: ([0-9a-f]{32})");
        return m.Success ? m.Groups[1].Value : null;
    }

    static bool Skip(string path) => path.Contains("~/") || path.Contains("/ThirdPartyLocal/") || path.Contains("/DreamSequence/");

    static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    static List<PlaylistLibraryItem> ScanTitle(string root, string titleId, out List<PlaylistLibrary.BadgeInfo> badges, out string docs)
    {
        var luaByGuid = new Dictionary<string, string>();
        foreach (string lua in Directory.GetFiles(root, "*.lua.txt", SearchOption.AllDirectories).Where(p => !Skip(p)))
        {
            string g = MetaGuid(lua);
            if (g != null) luaByGuid[g] = File.ReadAllText(lua);
        }
        badges = new List<PlaylistLibrary.BadgeInfo>();
        string badgesPath = Path.Combine(root, ".badges.json");
        if (File.Exists(badgesPath))
            foreach (JSONObject e in Items(new JSONObject(File.ReadAllText(badgesPath)).GetField("entries")))
                badges.Add(new PlaylistLibrary.BadgeInfo
                {
                    id = e.GetField("badgeId")?.stringValue, name = e.GetField("name")?.stringValue, description = e.GetField("description")?.stringValue,
                    earnAt = Items(e.GetField("earnAt")).Select(x => x.stringValue).ToList(),
                });
        // The challenge catalog: the title's own § 12.2 file, else its section of the first-party catalog.
        string own = Path.Combine(root, ".dreampark-challenges.json"), firstParty = Path.Combine(CoreDir, "Tools/Dreamie/objectives/catalog.json");
        var catalog = File.Exists(own) ? PlaylistChallenges.ReadCatalog(File.ReadAllText(own))
            : File.Exists(firstParty) ? PlaylistChallenges.ReadCatalog(File.ReadAllText(firstParty), titleId) : new Dictionary<string, PlaylistChallenges.Row>();
        var items = new List<PlaylistLibraryItem>();
        string assets = root.Substring(0, root.IndexOf("/Assets/", StringComparison.Ordinal) + "/Assets/".Length);
        foreach (string prefab in Directory.GetFiles(root, "*.prefab", SearchOption.AllDirectories).Where(p => !Skip(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(prefab);
            Vector2? authored = null, shrink = null; var frames = new List<Vector2>(); bool inFrames = false; string display = null;
            foreach (string line in text.Split('\n'))
            {
                string s = line.Trim();
                if (s.StartsWith("authoredFootprintMeters:") && authored == null) { var m = V2.Match(s); if (m.Success) authored = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (s.StartsWith("shrinkFootprintMeters:") && shrink == null) { var m = V2.Match(s); if (m.Success) shrink = new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)); }
                else if (line.StartsWith("  layoutFrames:")) inFrames = true;
                else if (inFrames && line.StartsWith("  ") && !line.StartsWith("   ") && !line.StartsWith("  -")) inFrames = false;
                else if (inFrames && s.StartsWith("sizeFeet:")) { var m = V2.Match(s); if (m.Success) frames.Add(new Vector2(F(m.Groups[1].Value), F(m.Groups[2].Value)) * 0.3048f); }
                else if (display == null && line.StartsWith("  displayName: ")) display = line.Substring("  displayName: ".Length).Trim().Trim('\'', '"');
            }
            if (authored == null || authored.Value.x <= 0f) continue;
            string leaf = Path.GetFileNameWithoutExtension(prefab);
            var luaTexts = GuidRef.Matches(text).Select(m => m.Groups[1].Value).Distinct().Where(luaByGuid.ContainsKey).Select(g => luaByGuid[g]).ToList();
            var smaller = new List<Vector2>(); if (shrink.HasValue) smaller.Add(shrink.Value); smaller.AddRange(frames);
            var item = new PlaylistLibraryItem
            {
                guid = MetaGuid(prefab), leaf = leaf, assetPath = "Assets/" + prefab.Substring(assets.Length),
                resourceName = prefab.Substring(assets.Length, prefab.Length - assets.Length - ".prefab".Length),
                displayName = display ?? "", size = "Custom", hasPackingBake = true, authored = authored.Value,
            };
            item.ladder = PlaylistLibrary.Ladder(item.authored, smaller);
            item.roleHint = PlaylistLibrary.RoleHint(leaf, display, luaTexts);
            item.objectives = PlaylistLibrary.InferObjectives(item.roleHint, luaTexts, out item.objectivesSource);
            item.luaSignals = PlaylistLibrary.LuaSignals(luaTexts);
            item.badges = badges.Where(b => b.earnAt.Contains(leaf)).Select(b => b.id).ToList();
            item.notes = string.Join("; ", luaTexts.Select(LuaSummary).Where(x => x.Length > 0)).Trim();
            if (item.notes.Length > 600) item.notes = item.notes.Substring(0, 600);
            if (catalog.TryGetValue(leaf, out var row)) PlaylistChallenges.Apply(item, row);
            items.Add(item);
        }
        var parts = new List<string>();
        foreach (string md in Directory.GetFiles(root, "README.md", SearchOption.AllDirectories).Where(p => !Skip(p)).OrderBy(p => p, StringComparer.Ordinal))
            parts.Add("## " + md.Substring(root.Length + 1) + "\n" + File.ReadAllText(md));
        docs = string.Join("\n\n", parts);
        if (docs.Length > 40000) docs = docs.Substring(0, 40000);
        return items;
    }

    /// The first comment block of a Lua file: authors describe the mechanic there.
    static string LuaSummary(string lua)
    {
        var lines = lua.Split('\n').Select(l => l.Trim()).TakeWhile(l => l.StartsWith("--") || l.Length == 0).Where(l => l.StartsWith("--"))
            .Select(l => l.TrimStart('-').Trim()).Where(l => l.Length > 0).Take(4);
        return string.Join(" ", lines);
    }

    static IEnumerable<JSONObject> Items(JSONObject a) => a?.list ?? new List<JSONObject>();

    static int WriteContext(string root, string titleId, string outPath)
    {
        var items = ScanTitle(root, titleId, out var badges, out string docs);
        string playlistsPath = Path.Combine(root, ".dreampark-playlists.json");
        ContentPlaylistStore.Data existing = File.Exists(playlistsPath) ? PlaylistStudioJson.ReadData(File.ReadAllText(playlistsPath)) : null;
        JSONObject ctx = PlaylistLibrary.GeneratorContext(titleId, PlaylistLibrary.Pretty(titleId), "", existing?.kinds ?? new List<PlaylistKind>(), items, badges, docs, existing);
        File.WriteAllText(outPath, ctx.Print(true));
        Console.WriteLine($"{titleId}: {items.Count} attractions ({items.Count(i => i.FitsSmallRoom)} fit 12×18), {badges.Count} badges, docs {docs.Length} chars → {outPath}");
        foreach (var i in items) Console.WriteLine($"  {i.leaf,-24} {i.roleHint,-8} {string.Join(",", i.objectives),-34} {i.objectivesSource,-7} challenges {string.Join(",", i.challenges)} min {i.Minimum.x:F1}×{i.Minimum.y:F1}");
        return 0;
    }

    static int Evaluate(string root, string titleId, string responsePath)
    {
        var items = ScanTitle(root, titleId, out var badges, out _);
        JSONObject file = new JSONObject(File.ReadAllText(responsePath));
        JSONObject body = file.GetField("response") ?? file;
        GeneratorResponse resp = GeneratorResponse.Parse(body.Print());
        if (!resp.success) { Console.WriteLine("generator failed: " + resp.code + " " + resp.error); return 1; }
        var ctx = new PlaylistProposals.Context { titleId = titleId, library = items, badgeIds = new HashSet<string>(badges.Select(b => b.id)) };
        var proposals = PlaylistProposals.Process(resp, ctx, out var dropped);
        Console.WriteLine($"{resp.playlists.Count} generated → {proposals.Count} proposals, {proposals.Count(p => p.CanSave)} saveable, {proposals.Count(p => p.FitsSmallRoom)} play in a 12×18 room; dropped {dropped.Count}");
        foreach (string d in dropped) Console.WriteLine("  dropped: " + d);
        foreach (string w in resp.warnings) Console.WriteLine("  web warning: " + w);
        var report = new JSONObject(JSONObject.Type.Array);
        foreach (var p in proposals)
        {
            int min = p.playlist.arc.Sum(l => l.minutes);
            Console.WriteLine($"\n{(p.CanSave ? "SAVE " : "BLOCK")} {p.playlist.id} [{p.playlist.kind}] \"{p.playlist.name}\" {min} min, {p.playlist.arc.Count} legs");
            Console.WriteLine("  " + p.playlist.pitch);
            Console.WriteLine("  arc: " + PlaylistArcModel.OneLine(p.playlist));
            Console.WriteLine("  objectives: " + string.Join(", ", p.playlist.arc.Select(l => l.beat + ":" + l.objective.type + (l.objective.count > 0 ? " " + l.objective.count : "") + (l.objective.seconds > 0 ? " " + l.objective.seconds + "s" : ""))));
            Console.WriteLine("  challenges: " + string.Join(", ", p.playlist.arc.SelectMany(l => l.stops).Select(s => (string.IsNullOrEmpty(s.challenge) ? "·" : s.challenge) + (s.bonus.Count > 0 ? "+" + string.Join("+", s.bonus) : ""))));
            if (!p.playlist.rules.IsDefault) Console.WriteLine($"  rules: advance {p.playlist.rules.advance} timer {p.playlist.rules.timerSec} scoring {p.playlist.rules.scoring} lives {p.playlist.rules.lives}");
            Console.WriteLine("  fit: " + string.Join("  ", p.fit.Select(r => r.space.id + "=" + r.verdict)));
            foreach (var i in p.issues) Console.WriteLine("  " + i);
            foreach (var r in p.repairs) Console.WriteLine("  repaired: " + r);
            if (p.fitProblem != null) Console.WriteLine("  fit problem: " + p.fitProblem);
            var row = PlaylistStudioJson.WritePlaylist(p.playlist, titleId);
            row.AddField("_canSave", p.CanSave);
            row.AddField("_fit", StrArray(p.fit.Select(r => r.space.id + "=" + r.verdict)));
            row.AddField("_issues", StrArray(p.issues.Select(i => i.ToString())));
            row.AddField("_repairs", StrArray(p.repairs));
            report.Add(row);
        }
        File.WriteAllText(responsePath.Replace(".json", ".proposals.json"), report.Print(true));
        return 0;
    }

    static JSONObject StrArray(IEnumerable<string> xs)
    {
        var a = new JSONObject(JSONObject.Type.Array);
        foreach (string x in xs) a.Add(x);
        return a;
    }

    static int MigrateReal(string root, string titleId, bool verbose)
    {
        var items = ScanTitle(root, titleId, out _, out _);
        var byGuid = items.Where(i => i.guid != null).GroupBy(i => i.guid).ToDictionary(g => g.Key, g => g.First());
        var input = new PlaylistMigration.Input { titleId = titleId, titleName = PlaylistLibrary.Pretty(titleId), byGuid = byGuid };
        List<PlaylistMigration.Entry> Entries(JSONObject data) => Items(data?.GetField("items")).Select(e => new PlaylistMigration.Entry
        {
            name = e.GetField("name")?.stringValue, isGroup = e.GetField("kind")?.stringValue == "world", hidden = e.GetField("hidden")?.boolValue ?? false,
            guids = e.GetField("kind")?.stringValue == "world" ? Items(e.GetField("attractionGuids")).Select(x => x.stringValue).ToList()
                : new List<string> { e.GetField("attractionGuid")?.stringValue }.Where(g => !string.IsNullOrEmpty(g)).ToList(),
        }).ToList();
        string adv = Path.Combine(root, ".dreampark-sequence.json"), seq = Path.Combine(root, ".dreampark-dream-sequence.json"), arena = Path.Combine(root, ".dreampark-arena.json");
        if (File.Exists(adv))
        {
            var d = new JSONObject(File.ReadAllText(adv));
            if (!string.IsNullOrEmpty(d.GetField("startGuid")?.stringValue) && !string.IsNullOrEmpty(d.GetField("endGuid")?.stringValue))
            { input.adventure = Entries(d); input.adventureStartGuid = d.GetField("startGuid").stringValue; input.adventureEndGuid = d.GetField("endGuid").stringValue; }
        }
        if (File.Exists(seq)) input.sequence = Entries(new JSONObject(File.ReadAllText(seq)));
        if (File.Exists(arena))
        {
            var d = new JSONObject(File.ReadAllText(arena));
            var excluded = new HashSet<string>(Items(d.GetField("excludedGuids")).Select(x => x.stringValue));
            input.arena = Items(d.GetField("buckets"))
                .OrderBy(b => b.GetField("widthFeet").intValue * b.GetField("lengthFeet").intValue)
                .SelectMany(b => Items(b.GetField("guids")).Select(x => x.stringValue))
                .Where(k => !excluded.Contains(k) && !k.Contains(':')).ToList();
        }
        var result = PlaylistMigration.Convert(input);
        var proposals = PlaylistProposals.Process(result.playlists.Select(p => PlaylistStudioJson.WritePlaylist(p, titleId)), result.kinds,
            new PlaylistProposals.Context { titleId = titleId, library = items, source = "migrate" }, out var dropped);
        Console.WriteLine($"  {titleId}: {result.playlists.Count} drafts ({proposals.Count(p => p.CanSave)} saveable as-is, {proposals.Count(p => p.FitsSmallRoom)} play in 12×18)");
        Check(result.playlists.Count > 0, titleId + ": its organizers convert to at least one draft");
        Check(proposals.All(p => !p.issues.Any(i => i.isError && i.message.Contains("is not an attraction"))), titleId + ": every migrated stop resolves");
        foreach (var p in proposals)
        {
            Console.WriteLine($"    {(p.CanSave ? "SAVE " : "FIX  ")} {p.playlist.id}: {PlaylistArcModel.OneLine(p.playlist)}");
            if (verbose) { foreach (var i in p.issues.Where(i => i.isError)) Console.WriteLine("      " + i); if (p.fitProblem != null) Console.WriteLine("      fit: " + p.fitProblem); }
        }
        if (verbose) foreach (string n in result.notes) Console.WriteLine("    note: " + n);
        return 0;
    }
}
