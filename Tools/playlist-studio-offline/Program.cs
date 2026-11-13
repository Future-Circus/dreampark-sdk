// Editor-free tests of the SDK PLAYLIST STUDIO (Oct 18 2026, sdk-playlist-studio). Compiles the REAL SDK
// sources (PlaylistStudio/Core, the shared core planner, ContentPlaylistStore's validator, the manifest
// types) with the Unity facade DLLs and runs them without Unity:
//   • authoring JSON round trip over every hand-drafted playlist (dreampark-core Tools/Dreamie/playlists-draft);
//   • the fit matrix, incl. byte-identical plan signatures vs core's own offline packer run;
//   • generator post-processing: repairs, de-dupe, validation, fit filtering (fixtures/generator-response.json);
//   • Adventure / Sequence / Arena → playlist migration (synthetic + real title organizers on disk);
//   • chapters & unlocks (continueWith / requires / sets), the objective catalog, library hints.
// Extra commands (used for the live generation run; never part of the pass/fail):
//   run.sh context  <titleRoot> <titleId> <out.json>      title context for Web, built from the project on disk
//   run.sh evaluate <titleRoot> <titleId> <response.json> post-process a live generator response
//   run.sh migrate  <titleRoot> <titleId>                 convert a real title's organizers
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Defective.JSON;
using DreamPark;
using DreamPark.Editor;
using DreamPark.Editor.PlaylistStudio;
using DreamPark.SpaceMap;
using UnityEngine;

static partial class Program
{
    static int failed, passed;
    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else { failed++; Console.WriteLine("  FAIL " + what); }
    }

    static string SdkDir, FixturesDir, OutDir, CoreDir;

    static int Main(string[] args)
    {
        SdkDir = args[0]; FixturesDir = args[1]; OutDir = args[2];
        Directory.CreateDirectory(OutDir);
        CoreDir = Environment.GetEnvironmentVariable("DREAMPARK_CORE") ?? Path.Combine(Path.GetDirectoryName(SdkDir.TrimEnd('/')), "dreampark-core");
        string cmd = args.Length > 3 ? args[3] : "test";
        switch (cmd)
        {
            case "context": return WriteContext(args[4], args[5], args[6]);
            case "evaluate": return Evaluate(args[4], args[5], args[6]);
            case "migrate": return MigrateReal(args[4], args[5], true);
        }

        Console.WriteLine("══ library hints ══"); LibraryTests();
        Console.WriteLine("══ authoring JSON round trip (hand drafts) ══"); RoundTripTests();
        Console.WriteLine("══ fit matrix (shared planner) ══"); FitTests();
        Console.WriteLine("══ generator post-processing ══"); GeneratorTests();
        Console.WriteLine("══ migration ══"); MigrationTests();
        Console.WriteLine("══ chapters & unlocks ══"); GateTests();
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ───────────────────────── fixtures ─────────────────────────

    static PlaylistLibraryItem Item(string leaf, float w, float d, float minW = 0, float minD = 0, string role = null, params string[] objectives)
    {
        var item = new PlaylistLibraryItem
        {
            guid = "guid-" + leaf, leaf = leaf, resourceName = "Content/T/Levels/" + leaf, assetPath = "Assets/Content/T/Levels/" + leaf + ".prefab",
            size = "Custom", hasPackingBake = true, authored = new Vector2(w, d),
        };
        item.ladder = PlaylistLibrary.Ladder(item.authored, minW > 0 ? new[] { new Vector2(minW, minD) } : null);
        item.roleHint = role ?? PlaylistLibrary.RoleHint(leaf, null);
        item.objectives = objectives.Length > 0 ? objectives.ToList() : PlaylistLibrary.InferObjectives(item.roleHint, null, out item.objectivesSource);
        return item;
    }

    static List<PlaylistLibraryItem> TestLibrary() => new List<PlaylistLibraryItem>
    {
        Item("A_Gate", 2.5f, 3f, role: "start"),
        Item("A_CoinGarden", 6f, 8f, 3f, 4f, null, "collect", "time"),
        Item("A_IcyAmbush", 5f, 6f, 3f, 3.5f),
        Item("A_JumpTower", 4f, 5f, 2.5f, 3f),
        Item("A_KingJubbo", 7f, 9f, 3.4f, 5f, "boss"),
        Item("A_Giant", 14f, 20f),                         // never fits a 12×18 room, no smaller frame
        Item("A_Trophy", 3f, 3f, role: "end"),
    };

    // ───────────────────────── library ─────────────────────────

    static void LibraryTests()
    {
        Check(PlaylistLibrary.Pretty("A_KingJubbo") == "King Jubbo", "Pretty A_KingJubbo");
        Check(PlaylistLibrary.Pretty("Content/X/Levels/A_LT_RW_Lobby") == "LT RW Lobby", "Pretty keeps acronyms");
        Check(PlaylistLibrary.RoleHint("A_KingJubbo", null) == "boss", "King → boss");
        Check(PlaylistLibrary.RoleHint("A_MiniBossCave", null) == "miniboss", "MiniBoss → miniboss");
        Check(PlaylistLibrary.RoleHint("A_ArcadeEntrance", null) == "start", "Entrance → start");
        Check(PlaylistLibrary.RoleHint("A_TrophyRoom", null) == "end", "Trophy → end");
        Check(PlaylistLibrary.RoleHint("P_Bush", null) == "prop", "P_ → prop");
        Check(PlaylistLibrary.RoleHint("A_Legend", null) == "stage", "'Legend' is not 'end'");
        Check(PlaylistLibrary.RoleHint("A_Cave", null, new[] { "dp.playlist.signal('boss_defeated')" }) == "boss", "Lua boss signal → boss");
        var objs = PlaylistLibrary.InferObjectives("stage", new[] { "local coins = 0\nfunction on_win() dp.playlist.signal('stage_won') end" }, out string src);
        Check(src == "lua" && objs.Contains("collect") && objs.Contains("reach_exit") && objs.Contains("time") && !objs.Contains("defeat_boss"),
            "objectives from Lua: " + string.Join(",", objs));
        Check(PlaylistLibrary.InferObjectives("boss", null, out _).SequenceEqual(new[] { "defeat_boss", "time" }), "boss with no Lua → defeat_boss, time");
        var sig = PlaylistLibrary.LuaSignals(new[] { "dp.playlist.signal('wish_made')\nif x then dp.playlist.signal(\"stage_won\", 3) end -- boss_defeated" });
        Check(sig.SequenceEqual(new[] { "boss_defeated", "stage_won", "wish_made" }), "Lua signals: " + string.Join(",", sig));
        var ladder = PlaylistLibrary.Ladder(new Vector2(7, 10), new[] { new Vector2(3, 4), new Vector2(7, 10), new Vector2(8, 11), new Vector2(5, 6) });
        Check(ladder.Count == 3 && ladder[0] == new Vector2(7, 10) && ladder[2] == new Vector2(3, 4), "ladder: authored first, larger dropped, sorted");
        var lib = TestLibrary();
        Check(lib.First(i => i.leaf == "A_CoinGarden").FitsSmallRoom && !lib.First(i => i.leaf == "A_Giant").FitsSmallRoom, "FitsSmallRoom via the ladder");

        // The generator context is deterministic (Web caches it byte for byte).
        string a = PlaylistLibrary.GeneratorContext("T", "Test", "d", new[] { PlaylistKind.Default() }, lib, null, "docs", null).Print();
        var shuffled = lib.AsEnumerable().Reverse().ToList();
        string b = PlaylistLibrary.GeneratorContext("T", "Test", "d", new[] { PlaylistKind.Default() }, shuffled, null, "docs", null).Print();
        Check(a == b, "generator context is order-independent and byte-stable");

        CatalogTests();
    }

    // ───────────────────────── round trip ─────────────────────────

    static Func<string, IReadOnlyList<PlaylistAttractionRef>> DraftResolver(string titleId, JSONObject footprints)
    {
        var refs = new List<PlaylistAttractionRef>();
        JSONObject t = footprints.GetField(titleId);
        if (t?.keys != null)
            foreach (string leaf in t.keys)
                refs.Add(new PlaylistAttractionRef { guid = "g-" + leaf, leaf = leaf, resourceName = "Content/" + titleId + "/" + leaf, size = "Custom", hasPackingBake = true });
        return ContentPlaylistValidator.Resolver(refs);
    }

    static void RoundTripTests()
    {
        string drafts = Path.Combine(CoreDir, "Tools/Dreamie/playlists-draft");
        string fpPath = Path.Combine(CoreDir, "Tools/playlist-pack-offline/fixtures/footprints.json");
        if (!Directory.Exists(drafts) || !File.Exists(fpPath)) { Console.WriteLine("  (skipped: no dreampark-core next to the SDK)"); return; }
        var footprints = new JSONObject(File.ReadAllText(fpPath));
        int playlists = 0, chained = 0;
        foreach (string f in Directory.GetFiles(drafts, "*.json").OrderBy(x => x))
        {
            string titleId = Path.GetFileNameWithoutExtension(f);
            ContentPlaylistStore.Data data = PlaylistStudioJson.ReadData(File.ReadAllText(f));
            string once = PlaylistStudioJson.Serialize(data, titleId);
            ContentPlaylistStore.Data again = PlaylistStudioJson.ReadData(once);
            string twice = PlaylistStudioJson.Serialize(again, titleId);
            Check(once == twice, titleId + ": serialize is idempotent");
            Check(data.playlists.Count == again.playlists.Count && data.playlists.Select(PlaylistProposals.Signature).SequenceEqual(again.playlists.Select(PlaylistProposals.Signature)),
                titleId + ": same playlists and stops after a round trip");
            var resolve = DraftResolver(titleId, footprints);
            var before = ContentPlaylistValidator.Validate(data, titleId, resolve).Select(i => i.ToString()).ToList();
            var after = ContentPlaylistValidator.Validate(again, titleId, resolve).Select(i => i.ToString()).ToList();
            Check(before.SequenceEqual(after), titleId + ": the validator says the same before and after (" + before.Count(i => i.StartsWith("error")) + " errors)");
            var opts = data.playlists.SelectMany(p => p.arc).SelectMany(l => l.stops).Where(s => !string.IsNullOrEmpty(s.options)).ToList();
            var opts2 = again.playlists.SelectMany(p => p.arc).SelectMany(l => l.stops).Where(s => !string.IsNullOrEmpty(s.options)).ToList();
            Check(opts.Count == opts2.Count && opts.Zip(opts2, (x, y) => new JSONObject(x.options).Print() == new JSONObject(y.options).Print()).All(v => v),
                titleId + $": {opts.Count} stop options survive as objects");
            // Chapter fields the drafts carry (continueWith) survive too.
            var gatesBefore = data.playlists.Select(p => string.Join(",", p.continueWith)).ToList();
            var gatesAfter = again.playlists.Select(p => string.Join(",", p.continueWith)).ToList();
            Check(gatesBefore.SequenceEqual(gatesAfter), titleId + ": continueWith survives the round trip");
            chained += data.playlists.Count(p => p.continueWith.Count > 0);
            playlists += data.playlists.Count;
            if (titleId == "SuperAdventureLand") File.WriteAllText(Path.Combine(OutDir, "roundtrip.SuperAdventureLand.json"), once);
        }
        Console.WriteLine($"  {playlists} hand-drafted playlists round-tripped ({chained} with continueWith)");
    }

    // ───────────────────────── fit ─────────────────────────

    static PlaylistFootprintBook CoreHarnessBook(JSONObject footprints, params string[] titles)
    {
        var book = new PlaylistFootprintBook();
        foreach (string cid in titles)
        {
            JSONObject t = footprints.GetField(cid);
            if (t?.keys == null) continue;
            for (int j = 0; j < t.keys.Count; j++)
            {
                JSONObject e = t.list[j];
                var ladder = new List<Vector2>();
                foreach (JSONObject v in e.GetField("ladder").list) ladder.Add(new Vector2(v.list[0].floatValue, v.list[1].floatValue));
                book.AddLadder(cid, new Vector2(e.GetField("w").floatValue, e.GetField("d").floatValue), ladder, t.keys[j]);
            }
        }
        return book;
    }

    static void FitTests()
    {
        var matrix = PlaylistFitCheck.Matrix;
        Check(matrix.Count == 6 && matrix[0].id == "small-room" && matrix[5].id == "park", "six spaces, small room first, park last");
        Check(matrix.First(s => s.id == "house-backyard").outdoor && !matrix.First(s => s.id == "house").outdoor, "the backyard is outdoors, the house is not");

        // Equivalence: the SDK copy of the planner gives core's byte-identical plans.
        string drafts = Path.Combine(CoreDir, "Tools/Dreamie/playlists-draft/SuperAdventureLand.json");
        string fpPath = Path.Combine(CoreDir, "Tools/playlist-pack-offline/fixtures/footprints.json");
        string coreOut = Path.Combine(CoreDir, "Tools/playlist-pack-offline/out");
        if (File.Exists(drafts) && File.Exists(fpPath))
        {
            var footprints = new JSONObject(File.ReadAllText(fpPath));
            PlaylistFootprintBook book = CoreHarnessBook(footprints, "SuperAdventureLand", "Zombiez", "LaserTag");
            JSONObject sunHeist = new JSONObject(File.ReadAllText(drafts)).GetField("playlists").list.First(p => p.GetField("id").stringValue == "great-sun-heist");
            PlaylistSpec spec = PlaylistSpec.FromJson(sunHeist, "SuperAdventureLand");
            int compared = 0;
            foreach (PlaylistFitSpace space in matrix)
            {
                string corePlan = Path.Combine(coreOut, $"{space.id}.great-sun-heist.plan.json");
                if (!File.Exists(corePlan)) continue;
                string want = new JSONObject(File.ReadAllText(corePlan)).GetField("signature")?.stringValue;
                PlaylistPlan plan = PlaylistPacker.Pack(spec, space.map, book);
                Check(PlaylistPlan.Hash(plan.signature) == want, $"{space.id}: SDK plan signature {PlaylistPlan.Hash(plan.signature)} == core's {want}");
                compared++;
            }
            Console.WriteLine($"  compared {compared} plans with core's own run");
            PackagePlaylist studio = PlaylistStudioJson.ReadPlaylist(sunHeist);
            PlaylistPlan viaStudio = PlaylistPacker.Pack(PlaylistFitCheck.SpecFor(studio, "SuperAdventureLand"), matrix[0].map, book);
            PlaylistPlan direct = PlaylistPacker.Pack(spec, matrix[0].map, book);
            Check(viaStudio.signature == direct.signature, "Studio's spec plans exactly like the raw draft");
        }
        else Console.WriteLine("  (core equivalence skipped: no dreampark-core next to the SDK)");

        var lib = TestLibrary();
        PlaylistFootprintBook tb = PlaylistLibrary.Book("T", lib);
        PackagePlaylist good = GoodPlaylist();
        var rows = PlaylistFitCheck.Run(good, "T", tb);
        foreach (var r in rows) Console.WriteLine($"  jubbo-run @ {r.space.id,-15} {r.verdict,-8} {r.layoutMode,-7} streamed={r.streamed} {r.reason}");
        Check(rows.Count == 6, "one row per space");
        Check(PlaylistFitCheck.Passes(rows, out string why), "a small-frame playlist passes: " + why);
        Check(PlaylistFitCheck.FitsSmallRoom(rows), "…and plays in the 12×18 room");
        Check(rows.First(r => r.space.id == "small-room").verdict == PlaylistFitCheck.Streams, "small room: it streams (one stop after another)");
        Check(rows.First(r => r.space.id == "park").verdict == PlaylistFitCheck.Fits, "park: everything stands");

        PackagePlaylist giant = GoodPlaylist();
        giant.arc[1].stops.Insert(0, Stop("A_Giant", "stage"));
        rows = PlaylistFitCheck.Run(giant, "T", tb);
        Check(!PlaylistFitCheck.Passes(rows, out why) && rows.First(r => r.space.id == "small-room").verdict == PlaylistFitCheck.NoFit,
            "a 14×20 m stop with no smaller frame: no-fit in the small room → blocked (" + why + ")");
        giant.fit = new PlaylistFit { minRooms = 1, minAreaM2 = 300 };
        rows = PlaylistFitCheck.Run(giant, "T", tb);
        Check(rows.First(r => r.space.id == "small-room").verdict == PlaylistFitCheck.Skipped && rows.First(r => r.space.id == "park").verdict != PlaylistFitCheck.NoFit,
            "…declared minAreaM2 300: small spaces are skipped, the park plays it");
        Check(PlaylistFitCheck.Passes(rows, out why), "…and then it may be saved: " + why);
    }

    static PlaylistStop Stop(string a, string role, string prefer = "any", params string[] alts) => new PlaylistStop
    { attraction = a, role = role, alternates = alts.ToList(), placement = new PlaylistPlacement { prefer = prefer } };

    static PackagePlaylist GoodPlaylist() => new PackagePlaylist
    {
        id = "jubbo-run", owner = "title", titleId = "T", kind = "adventure", name = "Jubbo Run", pitch = "Collect the coins and beat King Jubbo!",
        tags = new List<string> { "boss" }, badges = new List<string> { "t_champ" }, fit = new PlaylistFit { minRooms = 1, minAreaM2 = 12 },
        arc = new List<PlaylistLeg>
        {
            new PlaylistLeg { id = "l1", name = "Coin Garden", beat = "opening", minutes = 4, next = "walk_on",
                objective = new PlaylistObjective { type = "collect", count = 7 }, stops = new List<PlaylistStop> { Stop("A_Gate", "start", "entrance"), Stop("A_CoinGarden", "stage") } },
            new PlaylistLeg { id = "l2", name = "Icy Ambush", beat = "rising", minutes = 4, next = "walk_on",
                objective = new PlaylistObjective { type = "reach_exit" }, stops = new List<PlaylistStop> { Stop("A_IcyAmbush", "stage") } },
            new PlaylistLeg { id = "l3", name = "FINAL BOSS: King Jubbo", beat = "climax", minutes = 6, next = "walk_on",
                objective = new PlaylistObjective { type = "defeat_boss", target = "A_KingJubbo" }, stops = new List<PlaylistStop> { Stop("A_KingJubbo", "boss", "biggest") } },
        },
    };

    // ───────────────────────── generator ─────────────────────────

    static void GeneratorTests()
    {
        var lib = TestLibrary();
        var existing = new ContentPlaylistStore.Data { kinds = new List<PlaylistKind> { PlaylistKind.Default() }, playlists = new List<PackagePlaylist> { GoodPlaylist() } };
        foreach (var p in existing.playlists) ContentPlaylistStore.Normalize(p);
        GeneratorResponse resp = GeneratorResponse.Parse(File.ReadAllText(Path.Combine(FixturesDir, "generator-response.json")));
        Check(resp.success && resp.playlists.Count == 10 && resp.kinds.Any(k => k.id == "quest"), "response parsed: 10 playlists, kinds incl. quest");
        Check(resp.cacheReadTokens == 12000 && resp.warnings.Count == 1, "usage + Web warnings parsed");
        var ctx = new PlaylistProposals.Context
        {
            titleId = "T", existing = existing, library = lib, badgeIds = new HashSet<string> { "t_champ", "t_coins" },
        };
        var proposals = PlaylistProposals.Process(resp, ctx, out var dropped);
        foreach (var p in proposals)
            Console.WriteLine($"  {(p.CanSave ? "SAVE " : "BLOCK")} {p.playlist.id,-30} errors={p.issues.Count(i => i.isError)} fit={p.fitProblem ?? "ok"} repairs={p.repairs.Count}");
        foreach (string d in dropped) Console.WriteLine("  dropped: " + d);

        PlaylistProposal P(string id) => proposals.FirstOrDefault(x => x.playlist.id == id);
        Check(dropped.Count(d => d.Contains("repeats 'Jubbo Run'")) == 1, "de-dupe vs the title's existing playlist");
        Check(dropped.Count(d => d.Contains("repeats 'Frost Quest'")) == 1, "de-dupe within the batch");
        Check(P("frost-quest")?.CanSave == true, "a good quest is saveable");
        Check(P("frost-quest").playlist.kind == "quest", "…keeping its new kind word");
        Check(P("jubbo-run-2") != null && P("jubbo-run-2").repairs.Any(r => r.Contains("taken")), "an id clash is renamed jubbo-run-2");
        var invented = P("dragon-den");
        Check(invented != null && !invented.CanSave && invented.issues.Any(i => i.message.Contains("A_DragonDen")), "an invented attraction blocks the save");
        var longOne = P("the-very-long-named-playlist");
        Check(longOne != null && longOne.playlist.name.Length <= 60 && longOne.repairs.Any(r => r.StartsWith("name shortened")), "an over-long name is shortened, with a note");
        Check(longOne != null && longOne.playlist.kind == "adventure" && longOne.repairs.Any(r => r.Contains("kind 'saga'")), "an undeclared kind falls back to a declared one");
        var party = P("party-night");
        Check(party != null && party.CanSave, "bookends: a lobby/results playlist is saveable");
        Check(party != null && party.playlist.arc[0].stops[0].contentId == PlaylistBookends.ContentId && party.playlist.arc[0].titles.SequenceEqual(new[] { PlaylistBookends.ContentId }),
            "…its bare 'start-lobby' stop became a platform stop on a bookend leg");
        Check(party != null && party.playlist.badges.SequenceEqual(new[] { "t_coins" }) && party.repairs.Any(r => r.Contains("unknown badge")), "unknown badge ids are dropped");
        var alts = P("ice-sprint");
        Check(alts != null && alts.playlist.arc[0].stops[1].alternates.SequenceEqual(new[] { "A_JumpTower" }) && alts.repairs.Any(r => r.Contains("A_Nope")),
            "alternates that are not the title's are dropped, real ones kept");
        Check(alts != null && alts.playlist.arc[0].stops[0].role == "start", "first stop role stage → start");
        var big = P("giant-stomp");
        Check(big != null && !big.CanSave && big.fitProblem != null && big.fitProblem.Contains("12×18"), "the fit matrix blocks a playlist that cannot play where it says it can");
        var bigDeclared = P("giant-park");
        Check(bigDeclared != null && bigDeclared.CanSave && !bigDeclared.FitsSmallRoom, "…but one that declares a big space and fits it is saveable (not a small-room one)");
        Check(proposals.All(p => p.playlist.titleId == "T" && p.playlist.owner == "title"), "owner/title forced to this title");

        var accepted = proposals.Where(p => p.CanSave).Select(p => p.playlist).ToList();
        var kinds = PlaylistProposals.KindsToSave(existing, resp.kinds, existing.playlists.Concat(accepted));
        var saved = new ContentPlaylistStore.Data { kinds = kinds, playlists = existing.playlists.Concat(accepted).ToList() };
        string json = PlaylistStudioJson.Serialize(saved, "T");
        var reread = PlaylistStudioJson.ReadData(json);
        var issues = ContentPlaylistValidator.Validate(reread, "T", PlaylistLibrary.Resolver(lib));
        Check(!issues.Any(i => i.isError), "the saved file (existing + accepted) has no validator error: " + string.Join("; ", issues.Where(i => i.isError)));
        Check(reread.kinds.Any(k => k.id == "quest" && k.plural == "Quests"), "the new kind word is declared in the saved file");
        File.WriteAllText(Path.Combine(OutDir, "generator.saved.json"), json);

        // Realistic targets (catalog unit / par / max): "Ponk to 15" cannot survive post-processing.
        var tlib = TestLibrary();
        var pong = Item("A_Ponk", 4, 5, 2.4f, 3f, null, "score", "time");
        pong.targets["score"] = new PlaylistChallenges.Target { unit = "party points", par = 500, max = 1000 };
        var lava = Item("A_LavaHold", 4, 5, 2.4f, 3f, null, "survive", "time");
        lava.targets["survive"] = new PlaylistChallenges.Target { unit = "seconds", par = 90, max = 120 };
        tlib.Add(pong); tlib.Add(lava);
        var tp = GoodPlaylist();
        tp.arc.Insert(2, new PlaylistLeg { id = "pong", name = "Ponk", beat = "rising", minutes = 2, objective = new PlaylistObjective { type = "score", count = 15 }, stops = new List<PlaylistStop> { Stop("A_Ponk", "stage") } });
        tp.arc.Insert(3, new PlaylistLeg { id = "pong2", name = "Ponk again", beat = "rising", minutes = 2, objective = new PlaylistObjective { type = "score", count = 3000 }, stops = new List<PlaylistStop> { Stop("A_Ponk", "stage") } });
        tp.arc.Insert(4, new PlaylistLeg { id = "lava", name = "Hold", beat = "rising", minutes = 2, objective = new PlaylistObjective { type = "survive" }, stops = new List<PlaylistStop> { Stop("A_LavaHold", "stage") } });
        ContentPlaylistStore.Normalize(tp);
        var tfix = new List<string>();
        PlaylistProposals.ClampTargets(tp, PlaylistLibrary.Resolver(tlib), tlib, tfix);
        foreach (string f in tfix) Console.WriteLine("  target: " + f);
        Check(tp.arc[2].objective.count == 500 && tp.arc[3].objective.count == 1000 && tp.arc[4].objective.seconds == 90 && tp.arc[0].objective.count == 7 && tfix.Count == 3,
            "targets: 15 (another unit) → par 500, 3000 → max 1000, survive 0 → par 90 s; a coin leg without targets is left alone");

        var remix = GeneratorResponse.Parse("{\"success\":true,\"kinds\":[],\"playlists\":[" + PlaylistStudioJson.WritePlaylist(GoodPlaylist(), "T").Print() + "]}");
        var rp = PlaylistProposals.Process(remix, new PlaylistProposals.Context { titleId = "T", existing = existing, library = lib, source = "remix" }, out var rd);
        Check(rp.Count == 0 && rd.Count == 1, "a remix identical to its base is dropped as a duplicate");
        Check(!GeneratorResponse.Parse("not json").success && !GeneratorResponse.Parse("{\"success\":false,\"code\":\"RATE_LIMITED\",\"error\":\"slow down\"}").success,
            "errors parse into a failed response");
        Check(GeneratorResponse.Parse("   \n {\"success\":true,\"playlists\":[]}").success, "Web's keep-alive whitespace before the body is fine");
    }

    // ───────────────────────── migration ─────────────────────────

    static void MigrationTests()
    {
        var lib = TestLibrary();
        lib.Add(Item("A_Snake", 4, 5, 2.4f, 3f, null, "score", "time"));
        lib.Add(Item("A_Pong", 4, 5, 2.4f, 3f, null, "score", "time"));
        var input = new PlaylistMigration.Input
        {
            titleId = "T", titleName = "Test Land",
            adventureStartGuid = "guid-A_Gate", adventureEndGuid = "guid-A_Trophy",
            adventure = new List<PlaylistMigration.Entry>
            {
                new PlaylistMigration.Entry { guids = { "guid-A_CoinGarden" } },
                new PlaylistMigration.Entry { isGroup = true, name = "Frost Land", guids = { "guid-A_IcyAmbush", "guid-A_JumpTower" } },
                new PlaylistMigration.Entry { guids = { "guid-A_KingJubbo" } },
                new PlaylistMigration.Entry { guids = { "guid-A_Missing" } },
                new PlaylistMigration.Entry { hidden = true, guids = { "guid-A_Giant" } },
            },
            sequence = new List<PlaylistMigration.Entry>
            {
                new PlaylistMigration.Entry { isGroup = true, name = "Frost Land", guids = { "guid-A_IcyAmbush", "guid-A_JumpTower", "guid-A_KingJubbo" } },
                new PlaylistMigration.Entry { isGroup = true, name = "Snake", guids = { "guid-A_Snake" } },
                new PlaylistMigration.Entry { isGroup = true, name = "Pong", guids = { "guid-A_Pong" } },
            },
            arena = new List<string> { "guid-A_Snake", "guid-A_Pong", "guid-A_Snake", "guid-A_KingJubbo" },
        };
        foreach (var i in lib) input.byGuid[i.guid] = i;
        var result = PlaylistMigration.Convert(input);
        foreach (var p in result.playlists) Console.WriteLine("  " + p.id + ": " + PlaylistArcModel.OneLine(p));
        Check(result.playlists.Count == 4, "adventure + 1 world + 1 gauntlet + 1 arena showdown (got " + result.playlists.Count + ")");
        PackagePlaylist adv = result.playlists[0];
        Check(adv.arc.First().beat == "opening" && adv.arc.First().stops[0].attraction == "A_Gate" && adv.arc.First().stops[0].role == "start", "adventure starts at its Start Point");
        PlaylistLeg climax = adv.Climax();
        Check(climax != null && climax.stops.Last().attraction == "A_KingJubbo" && climax.objective.type == "defeat_boss", "adventure climax = the boss, defeat_boss");
        Check(adv.arc.Last().beat == "finale" && adv.arc.Last().stops.Last().attraction == "A_Trophy" && adv.arc.Last().stops.Last().role == "end", "…then the End Point as the finale");
        Check(adv.arc.Any(l => l.name == "Frost Land" && l.stops.Count == 2), "a group becomes one leg named after it");
        Check(!adv.arc.SelectMany(l => l.stops).Any(s => s.attraction == "A_Giant"), "hidden rows stay out");
        var world = result.playlists.First(p => p.kind == "world");
        Check(world.name == "Frost Land" && world.arc.Count == 3 && world.arc.All(l => l.stops.Count == 1) && world.arc[0].next == "swap_in_place", "a Sequence group → a level-by-level world, swapped in place");
        Check(world.arc.Last().beat == "climax" && world.arc.Last().objective.type == "defeat_boss", "…ending on its boss");
        var gauntlet = result.playlists.First(p => p.id == "gauntlet");
        Check(gauntlet.rules.advance == "on_timer" && gauntlet.arc.Count == 2 && gauntlet.arc[0].objective.type == "time", "single-game groups → one timed gauntlet");
        var arena = result.playlists.First(p => p.id == "arena-showdown");
        Check(arena.arc.Count == 1 && arena.arc[0].beat == "climax" && arena.arc[0].stops.Count == 3 && arena.arc[0].stops.Last().attraction == "A_KingJubbo",
            "arena → one showdown leg, de-duplicated, the boss last");
        Check(result.kinds.Select(k => k.id).SequenceEqual(new[] { "adventure", "world", "challenge" }), "kinds declared for what it made");
        Check(adv.continueWith.Count == 0, "migration invents no chain");

        var data = new ContentPlaylistStore.Data { kinds = result.kinds, playlists = result.playlists };
        var issues = ContentPlaylistValidator.Validate(data, "T", PlaylistLibrary.Resolver(lib));
        var errors = issues.Where(i => i.isError).ToList();
        foreach (var e in errors) Console.WriteLine("  expected-to-fix: " + e);
        Check(!issues.Any(i => i.isError && i.playlistId == adv.id) && !issues.Any(i => i.isError && i.playlistId == world.id),
            "boss-ending drafts validate with no error");
        Check(errors.Count == 1 && errors.All(e => e.message.Contains("needs count > 0")) && errors[0].playlistId == "gauntlet",
            "a draft without a boss carries one honest gap: the score target");

        var proposals = PlaylistProposals.Process(result.playlists.Select(p => PlaylistStudioJson.WritePlaylist(p, "T")), result.kinds,
            new PlaylistProposals.Context { titleId = "T", library = lib, source = "migrate",
                existing = new ContentPlaylistStore.Data { playlists = new List<PackagePlaylist> { world } } }, out var dropped);
        Check(proposals.Count == 3 && dropped.Count == 1, "re-running the migration does not duplicate a playlist the title already has");

        foreach (var (root, id) in RealTitles())
            if (Directory.Exists(root)) MigrateReal(root, id, false);
    }

    static IEnumerable<(string, string)> RealTitles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return (Path.Combine(home, "Dropbox/2026/arcadeland/Assets/Content/ArcadeLand"), "ArcadeLand");
        yield return (Path.Combine(home, "Dropbox/2025/superadventureland/Assets/Content/SuperAdventureLand"), "SuperAdventureLand");
        yield return (Path.Combine(home, "Dropbox/2026/creaturez/Assets/Content/Zombiez"), "Zombiez");
    }
}
