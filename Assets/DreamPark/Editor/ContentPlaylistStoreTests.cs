#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using DreamPark;
using DreamPark.Editor;
using NUnit.Framework;

/// <summary>Docs/PLAYLISTS.md § 3: what makes a playlist COMPLETE.</summary>
public sealed class ContentPlaylistStoreTests
{
    const string Title = "TestTitle";

    static readonly System.Func<string, IReadOnlyList<PlaylistAttractionRef>> Resolve =
        ContentPlaylistValidator.Resolver(new[] { "A_Start", "A_Mid", "A_Small", "A_Boss", "A_Arena" }
            .Select(leaf => new PlaylistAttractionRef
            {
                guid = "guid-" + leaf, leaf = leaf, size = "Custom", hasPackingBake = true,
                resourceName = "Content/TestTitle/Levels/" + leaf,
                assetPath = "Assets/Content/TestTitle/Levels/" + leaf + ".prefab",
            }).Concat(new[]
            {
                new PlaylistAttractionRef { guid = "guid-unbaked", leaf = "A_Unbaked", size = "Custom",
                    resourceName = "Content/TestTitle/A_Unbaked", hasPackingBake = false },
            }));

    static PlaylistStop Stop(string attraction, string role, params string[] alternates) => new PlaylistStop
    {
        attraction = attraction, role = role, alternates = alternates.ToList(), placement = new PlaylistPlacement(),
    };

    static PlaylistLeg Leg(string name, string beat, string objective, params PlaylistStop[] stops) => new PlaylistLeg
    {
        name = name, beat = beat, minutes = 5, next = "walk_on",
        objective = new PlaylistObjective { type = objective }, stops = stops.ToList(),
    };

    static PackagePlaylist Good() => new PackagePlaylist
    {
        id = "boss-quest", kind = "adventure", name = "Boss Quest", pitch = "Beat the boss!",
        tags = new List<string> { "boss" }, badges = new List<string> { "test_champion" },
        fit = new PlaylistFit { minRooms = 2, minAreaM2 = 20 },
        arc = new List<PlaylistLeg>
        {
            Leg("Warm Up", "opening", "reach_exit", Stop("A_Start", "start"), Stop("A_Mid", "stage", "A_Small")),
            Leg("Showdown", "climax", "defeat_boss", Stop("A_Boss", "boss")),
        },
    };

    static List<PlaylistIssue> Validate(params PackagePlaylist[] playlists)
    {
        var data = new ContentPlaylistStore.Data { playlists = playlists.ToList() };
        foreach (PackagePlaylist p in data.playlists) ContentPlaylistStore.Normalize(p);
        return ContentPlaylistValidator.Validate(data, Title, Resolve);
    }

    static List<string> Errors(List<PlaylistIssue> issues) => issues.Where(i => i.isError).Select(i => i.message).ToList();

    [Test]
    public void CompleteArcIsValidWithNoWarnings()
    {
        List<PlaylistIssue> issues = Validate(Good());
        Assert.That(issues, Is.Empty, string.Join("\n", issues));
        Assert.That(ContentPlaylistValidator.IsValid("boss-quest", issues), Is.True);
    }

    /// § 6.0 AUTHORED BONUS (Oct 19): a round's bonusLevels name the title's own attractions; a boss only with
    /// bonusBosses; a round's own stop is only a warning.
    [Test]
    public void BonusLevelsAreTheTitlesOwnNonBossAttractions()
    {
        PackagePlaylist p = Good();
        p.arc[0].bonusLevels = new List<string> { "A_Arena", " A_Small ", "A_Arena" };
        List<PlaylistIssue> ok = Validate(p);
        Assert.That(Errors(ok), Is.Empty, string.Join("\n", ok));
        Assert.That(p.arc[0].bonusLevels, Is.EqualTo(new[] { "A_Arena", "A_Small" }), "trimmed, no repeats");

        p.arc[0].bonusLevels = new List<string> { "A_Nope" };
        Assert.That(Errors(Validate(p)), Has.Some.Contains("bonusLevels 'A_Nope' is not an attraction of this title"));

        p.arc[0].bonusLevels = new List<string> { "A_Boss" };
        Assert.That(Errors(Validate(p)), Has.Some.Contains("is a boss"));
        p.arc[0].bonusBosses = true;
        Assert.That(Errors(Validate(p)), Is.Empty);

        p.arc[0].bonusLevels = new List<string> { "A_Mid" };
        List<PlaylistIssue> own = Validate(p);
        Assert.That(Errors(own), Is.Empty);
        Assert.That(own.Where(i => !i.isError).Select(i => i.message), Has.Some.Contains("already a stop of this round"));

        // noBonus wins over bonusLevels: a warning, never an error.
        p.arc[0].bonusLevels = new List<string> { "A_Arena" };
        p.arc[0].noBonus = true;
        List<PlaylistIssue> off = Validate(p);
        Assert.That(Errors(off), Is.Empty);
        Assert.That(off.Where(i => !i.isError).Select(i => i.message), Has.Some.Contains("noBonus is set"));
    }

    /// Record v2 (repair-oct20 CONTRACT § 8): a v1 file is upgraded on read; a v2 file reads the same model.
    [Test]
    public void V1AndV2RecordsReadTheSameModel()
    {
        string v1 = "{\"schemaVersion\":1,\"playlists\":[{\"id\":\"q\",\"kind\":\"adventure\",\"name\":\"Q\",\"pitch\":\"P\",\"fit\":{\"minRooms\":1,\"minPlayers\":2},"
            + "\"arc\":[{\"id\":\"a\",\"name\":\"A\",\"beat\":\"climax\",\"minutes\":4,\"objective\":{\"type\":\"defeat_boss\",\"target\":\"A_Boss\"},"
            + "\"stops\":[{\"id\":\"m\",\"attraction\":\"A_Mid\",\"role\":\"stage\"},{\"id\":\"b\",\"attraction\":\"A_Boss\",\"role\":\"boss\",\"lockedUntil\":[\"m\"]}]}],"
            + "\"dreamie\":{\"beats\":{\"layout.a\":{\"guide\":\"G\"}},\"react\":[{\"on\":\"stop_intro\",\"at\":\"b\",\"guide\":\"I\"}]}}]}";
        string v2 = "{\"schemaVersion\":1,\"playlists\":[{\"id\":\"q\",\"schema\":\"dreampark.playlist/2\",\"kind\":\"adventure\",\"name\":\"Q\",\"pitch\":\"P\",\"fit\":{\"minRooms\":1},"
            + "\"play\":{\"mode\":\"coop\",\"players\":[2,16]},\"requiresRelease\":23,"
            + "\"arc\":[{\"id\":\"a\",\"name\":\"A\",\"beat\":\"climax\",\"estMinutes\":4,\"dreamie\":{\"guide\":\"G\"},"
            + "\"stops\":[{\"id\":\"m\",\"attraction\":\"A_Mid\",\"role\":\"stage\"},{\"id\":\"b\",\"attraction\":\"A_Boss\",\"role\":\"boss\",\"lockedUntil\":[\"m\"],\"lockSign\":\"Later!\",\"dreamie\":{\"react\":[{\"on\":\"stop_intro\",\"guide\":\"I\"}]}}]}]}]}";
        PackagePlaylist a = ContentPlaylistStore.Deserialize(v1).playlists[0], b = ContentPlaylistStore.Deserialize(v2).playlists[0];
        foreach (PackagePlaylist p in new[] { a, b })
        {
            Assert.That(p.schema, Is.EqualTo(PlaylistRecordV2.Schema));
            Assert.That(p.arc[0].minutes, Is.EqualTo(4));
            Assert.That(p.arc[0].objective.type, Is.EqualTo("defeat_boss"));
            Assert.That(p.arc[0].objective.target, Is.EqualTo("A_Boss"));
            Assert.That(p.fit.minPlayers, Is.EqualTo(2));
            Assert.That(p.play.Min, Is.EqualTo(2));
            Assert.That(p.arc[0].dreamieJson, Does.Contain("\"G\""));
            Assert.That(p.arc[0].stops[1].dreamieJson, Does.Contain("stop_intro"));
        }
        Assert.That(b.requiresRelease, Is.EqualTo(23));
        Assert.That(b.play.mode, Is.EqualTo("coop"));
        Assert.That(b.arc[0].stops[1].lockSign, Is.EqualTo("Later!"));
    }

    [Test]
    public void ABunchOfStuffWithNoClimaxIsRejected()
    {
        PackagePlaylist p = Good();
        p.arc[1].beat = "rising";
        p.arc[1].objective.type = "reach_exit";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("no climax"));
        Assert.That(Errors(Validate(p)), Has.Some.Contains("must end with its climax or a finale"));
    }

    [Test]
    public void ClimaxNeedsABossOrAShowdown()
    {
        PackagePlaylist p = Good();
        p.arc[1].stops = new List<PlaylistStop> { Stop("A_Arena", "stage") };
        p.arc[1].objective.type = "reach_exit";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("climax needs a boss"));

        p.arc[1].objective = new PlaylistObjective { type = "score", count = 10 };
        Assert.That(Errors(Validate(p)), Is.Empty);
    }

    [Test]
    public void ArcCannotGoBackwardsOrHaveTwoClimaxes()
    {
        PackagePlaylist p = Good();
        p.arc.Add(Leg("Again", "climax", "defeat_boss", Stop("A_Boss", "boss")));
        Assert.That(Errors(Validate(p)), Has.Some.Contains("more than one climax"));
        p = Good();
        p.arc.Insert(1, Leg("Late Opening", "opening", "reach_exit", Stop("A_Mid", "stage")));
        p.arc[0].beat = "rising";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("goes backwards"));
    }

    [Test]
    public void EveryStopAndAlternateMustBeABakedAttractionOfTheTitle()
    {
        PackagePlaylist p = Good();
        p.arc[0].stops[1].alternates.Add("A_Missing");
        p.arc[0].stops.Add(Stop("A_Unbaked", "stage"));
        List<string> errors = Errors(Validate(p));
        Assert.That(errors, Has.Some.Contains("'A_Missing' is not an attraction"));
        Assert.That(errors, Has.Some.Contains("no packing bake"));
    }

    [Test]
    public void ObjectivesNeedTheirNumbersAndTargets()
    {
        PackagePlaylist p = Good();
        p.arc[0].objective = new PlaylistObjective { type = "collect" };
        p.arc[1].objective.target = "A_Start";
        List<string> errors = Errors(Validate(p));
        Assert.That(errors, Has.Some.Contains("needs count > 0"));
        Assert.That(errors, Has.Some.Contains("is not a stop of this leg"));
    }

    [Test]
    public void TimeIsOnlyAnEndingForAFinaleAfterTheClimax()
    {
        PackagePlaylist p = Good();
        p.arc.Add(Leg("Victory Lap", "finale", "time", Stop("A_Mid", "end")));
        p.arc[2].objective.seconds = 60;
        Assert.That(Errors(Validate(p)), Is.Empty);
    }

    [Test]
    public void TitleFilesShipOnlyTheirOwnTitleAndOneTitlePerLeg()
    {
        PackagePlaylist p = Good();
        p.owner = "platform";
        p.arc[0].stops[0].contentId = "OtherTitle";
        p.arc[1].mixed = true;
        List<string> errors = Errors(Validate(p));
        Assert.That(errors, Has.Some.Contains("cannot ship in a title package"));
        Assert.That(errors, Has.Some.Contains("one title per leg"));
        Assert.That(errors, Has.Some.Contains("mixed"));
    }

    [Test]
    public void RulesAreCheckedButOptional()
    {
        PackagePlaylist p = Good();
        p.rules = new PlaylistRules { advance = "on_timer", scoring = "highest" };
        List<string> errors = Errors(Validate(p));
        Assert.That(errors, Has.Some.Contains("rules.scoring"));
        Assert.That(errors, Has.Some.Contains("on_timer needs rules.timerSec"));
    }

    [Test]
    public void UndeclaredKindAndDuplicateIdsAreErrors()
    {
        PackagePlaylist a = Good(), b = Good();
        b.kind = "world";
        List<string> errors = Errors(Validate(a, b));
        Assert.That(errors, Has.Some.Contains("id is used by another playlist"));
        Assert.That(errors, Has.Some.Contains("kind 'world' is not one of the title's kinds"));
    }

    [Test]
    public void JsonRoundTripKeepsDefaultsAndRejectsNewerSchema()
    {
        ContentPlaylistStore.Data data = ContentPlaylistStore.Deserialize(
            "{\"schemaVersion\":1,\"kinds\":[{\"id\":\"world\",\"singular\":\"World\",\"plural\":\"Worlds\"}],"
            + "\"playlists\":[{\"id\":\"w1\",\"kind\":\"world\",\"name\":\"W\",\"pitch\":\"P\",\"arc\":[{\"name\":\"L\","
            + "\"beat\":\"climax\",\"minutes\":3,\"objective\":{\"type\":\"defeat_boss\"},\"stops\":[{\"attraction\":\"A_Boss\",\"role\":\"boss\"}]}]}]}");
        PackagePlaylist p = data.playlists.Single();
        Assert.That(p.fit.indoor && p.fit.outdoor, Is.True);
        Assert.That(p.owner, Is.EqualTo("title"));
        Assert.That(p.arc[0].id, Is.EqualTo("leg1"));
        Assert.That(p.arc[0].next, Is.EqualTo("walk_on"));
        Assert.That(p.arc[0].stops[0].placement.prefer, Is.EqualTo("any"));
        Assert.That(ContentPlaylistStore.KindsOrDefault(data).Single().plural, Is.EqualTo("Worlds"));
        Assert.Throws<System.InvalidOperationException>(() =>
            ContentPlaylistStore.Deserialize("{\"schemaVersion\":99,\"playlists\":[]}"));
    }

    // ── Bookends (PLAYLISTS.md § 11) ──────────────────────────────────

    static PlaylistStop Bookend(string id, string role) => new PlaylistStop
    {
        contentId = PlaylistBookends.ContentId, attraction = id, role = role, alternates = new List<string>(),
        placement = new PlaylistPlacement { prefer = PlaylistBookends.PreferPlayers },
    };

    static PackagePlaylist Bookended()
    {
        PackagePlaylist p = Good();
        PlaylistLeg lobby = Leg("Start Lobby", "opening", "reach_exit", Bookend("start-lobby", "start"));
        lobby.titles = new List<string> { PlaylistBookends.ContentId };
        lobby.objective.target = "start-lobby";
        PlaylistLeg results = Leg("Results Room", "finale", "time", Bookend("results-room", "end"));
        results.titles = new List<string> { PlaylistBookends.ContentId };
        results.objective.seconds = 90;
        p.arc.Insert(0, lobby);
        p.arc.Add(results);
        p.arc[0].id = "lobby"; p.arc[p.arc.Count - 1].id = "results";
        return p;
    }

    [Test]
    public void BookendLegsAreValidInATitleFile()
    {
        List<PlaylistIssue> issues = Validate(Bookended());
        Assert.That(Errors(issues), Is.Empty, string.Join("\n", issues));
    }

    [Test]
    public void BookendMustBeAKnownPlatformAttractionWithoutAlternates()
    {
        PackagePlaylist p = Bookended();
        p.arc[0].stops[0].attraction = "trophy-room";
        p.arc[p.arc.Count - 1].stops[0].alternates = new List<string> { "A_Start" };
        List<string> errors = Errors(Validate(p));
        Assert.That(errors, Has.Some.Contains("is not a platform bookend"));
        Assert.That(errors, Has.Some.Contains("takes no alternates"));
    }

    [Test]
    public void ABookendCannotBeTheClimax()
    {
        PackagePlaylist p = Good();
        PlaylistLeg climax = Leg("Results", "climax", "score", Bookend("results-room", "boss"));
        climax.objective.count = 5;
        p.arc[1] = climax;
        Assert.That(Errors(Validate(p)), Has.Some.Contains("not a bookend"));
    }

    [Test]
    public void PlayersPlacementIsAccepted()
    {
        PackagePlaylist p = Good();
        p.arc[0].stops[0].placement.prefer = "players";
        Assert.That(Errors(Validate(p)), Is.Empty);
        p.arc[0].stops[0].placement.prefer = "everywhere";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("players"));
    }

    [Test]
    public void StopOptionsAreOpaqueJsonText()
    {
        const string json = "{\"schemaVersion\":1,\"playlists\":[{\"id\":\"golf\",\"kind\":\"adventure\",\"name\":\"Back Nine\","
            + "\"pitch\":\"p\",\"arc\":[{\"name\":\"Holes\",\"beat\":\"climax\",\"minutes\":9,\"objective\":{\"type\":\"score\",\"count\":1},"
            + "\"stops\":[{\"attraction\":\"A_Arena\",\"role\":\"stage\",\"options\":{\"holes\":[10,18],\"difficulty\":\"hard\"}},"
            + "{\"attraction\":\"A_Mid\",\"role\":\"stage\"}]}]}]}";
        ContentPlaylistStore.Data data = ContentPlaylistStore.Deserialize(json);
        PlaylistStop golf = data.playlists[0].arc[0].stops[0];
        Assert.That(golf.options, Does.Contain("\"holes\"").And.Contain("\"hard\"").And.StartWith("{"));
        Assert.That(data.playlists[0].arc[0].stops[1].options, Is.Empty);
        Assert.That(Errors(ContentPlaylistValidator.Validate(data, Title, Resolve)), Is.Empty);
    }

    [Test]
    public void StopOptionsMustBeASmallObject()
    {
        PackagePlaylist p = Good();
        p.arc[0].stops[0].options = "[1,2]";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("JSON object"));
        p = Good();
        p.arc[0].stops[0].options = "{\"blob\":\"" + new string('x', 3000) + "\"}";
        Assert.That(Errors(Validate(p)), Has.Some.Contains("larger than"));
    }

    [Test]
    public void ContinueWithKeepsKnownSiblingsAndDropsTheRestWithWarnings()
    {
        PackagePlaylist a = Good(), b = Good();
        b.id = "next-quest";
        a.continueWith = new List<string> { "next-quest", "boss-quest", "nope", "title:TestTitle:next-quest", "TestTitle/next-quest" };
        List<PlaylistIssue> issues = Validate(a, b);
        Assert.That(Errors(issues), Is.Empty);
        Assert.That(a.continueWith, Is.EqualTo(new List<string> { "next-quest" }));
        Assert.That(issues.Where(i => !i.isError && i.message.StartsWith("continueWith")).Count(), Is.EqualTo(4));
    }

    [Test]
    public void ChallengeBonusAndIntroHintAreOptionalWarningsOnly()
    {
        PackagePlaylist p = Good();
        PlaylistStop boss = p.arc[1].stops[0];
        boss.challenge = "boss"; boss.bonus = new List<string> { "no_hit", "combo" }; boss.introHint = "Hit the glowing spot!";
        Assert.That(Validate(p).Where(i => !i.isError && (i.message.Contains("challenge") || i.message.Contains("bonus"))), Is.Empty);
        boss.challenge = "dance"; boss.bonus = new List<string> { "no_hit", "fly", "combo", "clock" };
        List<PlaylistIssue> issues = Validate(p);
        Assert.That(Errors(issues), Is.Empty);
        Assert.That(boss.challenge, Is.Empty);
        Assert.That(boss.bonus, Is.EqualTo(new List<string> { "no_hit", "combo" }));
        boss.introHint = new string('x', 121);
        Assert.That(Errors(Validate(p)), Has.Some.Contains("introHint"));
    }

    [Test]
    public void PlacingsScoringIsAccepted()
    {
        PackagePlaylist p = Good();
        p.rules = new PlaylistRules { scoring = "placings" };
        Assert.That(Errors(Validate(p)), Is.Empty);
    }

    [Test]
    public void GatesAreCarriedAsTextAndUnknownShapesDropWithWarnings()
    {
        const string json = "{\"schemaVersion\":1,\"playlists\":[{\"id\":\"a\",\"kind\":\"adventure\",\"name\":\"A\",\"pitch\":\"p\","
            + "\"requires\":[\"meadow\",{\"badge\":\"sal_star\"},{\"flag\":\"cr.partner\",\"is\":\"maple\"},{\"min\":1,\"of\":[\"x\",\"y\"]},{\"weird\":1}],"
            + "\"sets\":{\"flags\":{\"cr.ch1\":true},\"badges\":[\"cr_rookie\"]},\"window\":{\"from\":\"2026-10-25\",\"until\":\"2026-11-02\"},"
            + "\"personal\":[{\"flag\":\"cr.partner\"}],\"arc\":[]}]}";
        ContentPlaylistStore.Data data = ContentPlaylistStore.Deserialize(json);
        PackagePlaylist p = data.playlists[0];
        Assert.That(p.requires, Is.EqualTo(new List<string> { "playlist:meadow", "badge:sal_star" }));
        Assert.That(p.sets, Is.EqualTo(new List<string> { "cr.ch1" }));
        var issues = new List<PlaylistIssue>();
        ContentPlaylistValidator.CleanGates(p, issues);
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(p.requiresJson, Does.Contain("\"min\"").And.Not.Contain("weird"));
        Assert.That(p.requiresJson, Does.StartWith("[\"meadow\",{\"badge\":\"sal_star\"}"));   // canonical § 2.1 form

        // A Studio-only file (string refs) gets canonical text built from its lists.
        PackagePlaylist studio = Good();
        studio.requires = new List<string> { "playlist:boss-quest", "badge:test_champion", "flag:ch1" };
        studio.sets = new List<string> { "ch2" };
        ContentPlaylistValidator.CleanGates(studio, new List<PlaylistIssue>());
        Assert.That(studio.requiresJson, Is.EqualTo("[\"boss-quest\",{\"badge\":\"test_champion\"},{\"flag\":\"ch1\"}]"));
        Assert.That(studio.setsJson, Is.EqualTo("{\"flags\":{\"ch2\":true}}"));

        // Empty containers (an authored `"requires": []`, `"sets": {}`) read as none, never throw.
        PackagePlaylist empty = Good();
        empty.requiresJson = "[]"; empty.setsJson = "{}"; empty.windowJson = "{}"; empty.personalJson = " [ ] ";
        var none = new List<PlaylistIssue>();
        ContentPlaylistValidator.CleanGates(empty, none);
        Assert.That(empty.requiresJson + empty.setsJson + empty.windowJson, Is.Empty);
        Assert.That(p.windowJson, Does.Contain("2026-11-02"));
        Assert.That(p.personalJson, Does.Contain("cr.partner"));
    }

    /// json-escape (repair Oct 20 2026): authored objects become JSON TEXT (dreamieJson, goal Lua …) and the file is
    /// printed again, so every escape must survive two levels. A SAL Dreamie guide `End with \"Follow me!\"` used to
    /// break the whole file and the upload shipped NO playlists.
    [Test]
    public void EscapedStringsSurviveTheTextPass()
    {
        const string guide = "End with \"Follow me, friend!\" Then: {wave}.";
        const string lua = "return function(stop) print(\"a\\nb\") end";
        var file = new Defective.JSON.JSONObject("{\"schemaVersion\":1,\"playlists\":[{\"id\":\"q\",\"kind\":\"adventure\",\"name\":\"Q\","
            + "\"pitch\":\"p\",\"arc\":[{\"name\":\"R\",\"beat\":\"climax\",\"minutes\":3,\"stops\":[{\"attraction\":\"A_Arena\",\"role\":\"stage\"}]}]}]}");
        Defective.JSON.JSONObject p = file.GetField("playlists").list[0], leg = p.GetField("arc").list[0], stop = leg.GetField("stops").list[0];
        var beats = new Defective.JSON.JSONObject(); var intro = new Defective.JSON.JSONObject(); intro.AddField("guide", guide); beats.AddField("playlist_intro", intro);
        var pd = new Defective.JSON.JSONObject(); pd.AddField("beats", beats); p.AddField("dreamie", pd);
        var ld = new Defective.JSON.JSONObject(); ld.AddField("guide", guide); leg.AddField("dreamie", ld);
        var goal = new Defective.JSON.JSONObject(); goal.AddField("lua", lua); stop.AddField("goal", goal);
        var options = new Defective.JSON.JSONObject(); options.AddField("path", "C:\\x\\\"y\""); stop.AddField("options", options);

        ContentPlaylistStore.Data data = ContentPlaylistStore.Deserialize(file.Print(true));
        Assert.That(data.playlists.Count, Is.EqualTo(1));
        PackagePlaylist read = data.playlists[0];
        Assert.That(new Defective.JSON.JSONObject(read.dreamieJson).GetField("beats").GetField("playlist_intro").GetField("guide").stringValue, Is.EqualTo(guide));
        Assert.That(new Defective.JSON.JSONObject(read.arc[0].dreamieJson).GetField("guide").stringValue, Is.EqualTo(guide));
        Assert.That(read.arc[0].stops[0].goal, Is.EqualTo(lua));
        Assert.That(new Defective.JSON.JSONObject(read.arc[0].stops[0].options).GetField("path").stringValue, Is.EqualTo("C:\\x\\\"y\""));
    }

    [Test]
    public void ObjectiveTextAndDisplayNameAreOptionalAndShort()
    {
        PackagePlaylist p = Good();
        p.arc[1].objectiveText = "  Beat the boss!  ";
        p.arc[1].stops[0].objectiveText = "Defeat King Jubbo!";
        p.arc[1].stops[0].displayName = "King Jubbo";
        Assert.That(Errors(Validate(p)), Is.Empty);
        Assert.That(p.arc[1].objectiveText, Is.EqualTo("Beat the boss!"));
        p.arc[1].stops[0].displayName = new string('x', 61);
        p.arc[1].objectiveText = new string('x', 121);
        Assert.That(Errors(Validate(p)), Has.Some.Contains("displayName").And.Some.Contains("objectiveText"));
    }
}
#endif
