#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using DreamPark.Editor.GameGraph;
using NUnit.Framework;

/// <summary>Docs/GAME-GRAPH.md: the Lua index and the deterministic writer.</summary>
public sealed class GameGraphTests
{
    const string Src = @"-- a comment: dp.playlist.signal(""not_this"")
local hp = 30
GAME_STATE = 'idle'
_G.boss_hp = hp

function start()
  dp.profile.awardBadge(""demo_boss"")
end

local function die()
  if hp <= 0 then
    dp.playlist.signal(""boss_defeated"")
    send(""wave_cleared"", 3)
  end
end

local api = {}
api.award = function(id) dp.profile.awardBadge(id) end

function start_challenge(params)
  if player.position.y < -2 then respawn_player() end
  return [[ long ""string"" ]]
end
";

    static JObj Index() => GameGraphLuaIndex.Index(Src, new HashSet<string> { "demo_boss" }, new HashSet<string> { "boss_defeated" });

    [Test]
    public void Functions_HaveScopesAndLines()
    {
        var fns = ((List<object>)Index()["functions"]).Cast<JObj>().ToDictionary(f => (string)f["name"], f => f);
        Assert.AreEqual("global", fns["start"]["scope"]);
        Assert.AreEqual("local", fns["die"]["scope"]);
        Assert.AreEqual("member", fns["api.award"]["scope"]);
        Assert.AreEqual(10, fns["die"]["line"]);
    }

    [Test]
    public void Comments_AreIgnored_AndLiteralSignalsFound()
    {
        var signals = (JObj)Index()["signals"];
        Assert.IsFalse(signals.Has("not_this"));
        Assert.IsTrue(signals.Has("boss_defeated"));
        Assert.IsTrue(signals.Has("wave_cleared"));
    }

    [Test]
    public void Dp_Calls_Badges_Hooks_Fall()
    {
        JObj ix = Index();
        var dp = (JObj)ix["dp"];
        Assert.IsTrue(dp.Has("dp.playlist.signal"));
        CollectionAssert.Contains((List<string>)((JObj)dp["dp.profile.awardBadge"])["args"], "demo_boss");
        var badges = (JObj)ix["badges"];
        Assert.IsTrue(badges.Has("demo_boss"));
        Assert.IsTrue(badges.Has("(dynamic)"));
        CollectionAssert.AreEquivalent(new[] { "start", "start_challenge" }, (List<string>)ix["hooks"]);
        CollectionAssert.Contains((List<string>)ix["globals"], "GAME_STATE");
        CollectionAssert.Contains((List<string>)ix["globals"], "boss_hp");
        Assert.IsTrue(((List<object>)ix["fall"]).Any(f => (int)((List<object>)f)[0] == 21));
    }

    [Test]
    public void Listeners_Compares_Dialogue()
    {
        JObj ix = GameGraphLuaIndex.Index("dp.on_global(\"sal_damage\", f)\nfunction onnet(t) if t == \"ping\" then toast(\"Grab every coin now!\") end end\nlocal card = { say = \"Follow the glowing path home\" }\nfunction ontriggerenter(o) end\n");
        Assert.IsTrue(((JObj)ix["listens"]).Has("sal_damage"));
        Assert.IsTrue(((JObj)ix["compares"]).Has("ping"));
        var lines = ((List<object>)ix["dialogue"]).Select(d => (string)((List<object>)d)[1]).ToList();
        CollectionAssert.Contains(lines, "Grab every coin now!");
        CollectionAssert.Contains(lines, "Follow the glowing path home");
        Assert.IsFalse(((JObj)ix["signals"]).Has("ontriggerenter"));
    }

    [Test]
    public void Writer_IsDeterministic_AndInvariant()
    {
        string a = GameGraphJson.Write(Index());
        string b = GameGraphJson.Write(Index());
        Assert.AreEqual(a, b);
        Assert.AreEqual("{\"x\":0.5,\"y\":0,\"s\":\"a\\\"b\"}", GameGraphJson.Write(new JObj().Add("x", 0.5).Add("y", -0.0001 * 0).Add("s", "a\"b")));
        Assert.AreEqual(0.0, GameGraphJson.Round(-0.0004, 3));
    }

    /// PLAYLIST-PLAN § 4.2: dp.announce and its pcall / alias / chain forms; kits (mirrors luaIndex.js / test.js).
    [Test]
    public void Announces_PcallAliasChain_AndKits()
    {
        JObj ix = GameGraphLuaIndex.Index(@"local d = dp
function Creature.announce(c)
    local t = { item = c:script_id() }
    if alive(c.coinPrefab) then t.holds = { collect = 1 } end
    c.announced = pcall(d.announce, ""defeat"", t)
end
pcall(dp.announce, ""collect"", { item = ""coin"", from = source })
dp.announce('break', { item = 'brick_block' })
pcall(c.dp.announce, ""catch"", { item = ""scarab"" })
r.announce()
ui.announce(""Round two!"")
-- dp.announce('find') in a comment
local s = ""dp.announce('target')""
local kit = rawget(_G, ""jubbo_kit"")
rawset(_G, ""my_kit"", {})
");
        var an = (JObj)ix["announces"];
        CollectionAssert.AreEqual(new[] { "break", "catch", "collect", "defeat" }, an.Select(kv => kv.Key).ToList());
        var defeat = (JObj)an["defeat"];
        CollectionAssert.AreEqual(new[] { "pcall d.announce" }, (List<string>)defeat["via"]);
        CollectionAssert.AreEqual(new[] { "collect" }, (List<string>)defeat["holds"]);
        var collect = (JObj)an["collect"];
        CollectionAssert.AreEqual(new[] { "coin" }, (List<string>)collect["items"]);
        Assert.AreEqual(true, collect["from"]);
        CollectionAssert.AreEqual(new[] { "pcall c.dp.announce" }, (List<string>)((JObj)an["catch"])["via"]);
        CollectionAssert.AreEqual(new[] { "brick_block" }, (List<string>)((JObj)an["break"])["items"]);
        var kits = (JObj)ix["kits"];
        CollectionAssert.AreEqual(new[] { "my_kit" }, (List<string>)kits["publishes"]);
        CollectionAssert.AreEqual(new[] { "jubbo_kit" }, (List<string>)kits["uses"]);
    }

    /// PLAYLIST-PLAN § 3: a sets playlist's pool / requires.badges / finishBadge — the exact bytes core build.js writes.
    [Test]
    public void PlaylistSets_MatchCoreExporterBytes()
    {
        const string Rec = @"{""id"":""world"",""kind"":""world"",""requires"":{""badges"":[""b_prev""]},""pool"":[{""id"":""crates"",""attraction"":""A_Crates"",""challenge"":""break"",""options"":{""time"":45,""slot"":1},""role"":""stage"",""place"":{""far_from"":""x""},""lock"":{""needs"":[""y""],""sign"":""Win first!""},""displayName"":""Crates""},{""id"":""gone"",""attraction"":""A_Missing"",""contentId"":""Other"",""challenge"":""coins"",""options"":{},""role"":""stage"",""place"":null}],""lua"":""-- badge = 'b_fake'\nreturn run.batches{ boss = 'crates', badge = 'b_world' }\n""}";
        var rec = (JObj)GameGraphJsonReader.Read(Rec);
        JObj f = GameGraphPlaylistSets.Fields(rec, rec["lua"] as string, (a, cid) => cid != null ? cid + "/" + a : "R:" + a);
        Assert.AreEqual(@"{""pool"":[{""id"":""crates"",""attraction"":""R:A_Crates"",""challenge"":""break"",""options"":{""time"":45,""slot"":1},""role"":""stage"",""place"":{""far_from"":""x""},""lock"":{""needs"":[""y""],""sign"":""Win first!""},""displayName"":""Crates""},{""id"":""gone"",""attraction"":""Other/A_Missing"",""challenge"":""coins"",""role"":""stage""}],""requires"":{""badges"":[""b_prev""]},""finishBadge"":""b_world""}", GameGraphJson.Write(f));
        Assert.AreEqual(0, GameGraphPlaylistSets.Fields(new JObj().Add("arc", new List<object>()), null, (a, c) => a).Count);
    }

    [Test]
    public void Slug_MatchesWebCatalog()
    {
        Assert.AreEqual("content__superadventureland__2__features__3__level__sequence__a__coinrush",
            GameGraphExporter.Slug("Content/SuperAdventureLand/2. Features/3. Level/Sequence/A_CoinRush"));
        Assert.AreEqual("Coin Rush", GameGraphExporter.Humanize("A_CoinRush"));
    }
}
#endif
