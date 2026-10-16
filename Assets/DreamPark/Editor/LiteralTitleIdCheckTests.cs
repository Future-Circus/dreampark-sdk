#if UNITY_EDITOR && !DREAMPARKCORE
using System.Linq;
using DreamPark.PreUploadChecks;
using DreamPark.PreUploadChecks.Checks;
using NUnit.Framework;
using UnityEditor;

public sealed class LiteralTitleIdCheckTests
{
    private const string Id = "__LiteralTitleIdCheckTests";
    private const string Root = "Assets/Content/" + Id;

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
    }

    private static int[] Lines(string src) =>
        LiteralTitleIdCheck.FindLiteralIds(src, Id).Select(h => h.line).ToArray();

    [Test]
    public void FlagsExactIdAndSlashPrefixedValuesInBothQuoteStyles()
    {
        string src =
            "local a = \"" + Id + "\"\n" +                       // 1: exact, double quotes
            "local b = '" + Id + "/FX_Boom'\n" +                // 2: "<id>/" prefix, single quotes
            "local c = [[" + Id + "]]\n" +                      // 3: long string
            "if gid == \"" + Id + "\" then end\n";              // 4: comparison
        Assert.That(Lines(src), Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void IgnoresCommentsNamespacesLookalikesAndOptOutLines()
    {
        string src =
            "-- storage key was \"" + Id + "\" before Beta\n" +              // 1: line comment
            "--[[ old: local id = '" + Id + "'\n" +                         // 2: block comment …
            "   still '" + Id + "/x' ]]\n" +                                // 3: … spanning lines
            "local T = CS." + Id + ".SomeType\n" +                          // 4: CS.<ns>. reference
            "local ns = CS[\"" + Id + "\"]\n" +                             // 5: CS["<ns>"] lookup
            "local t = \"" + Id + ".SomeType\"\n" +                         // 6: dotted type name
            "local s = \"" + Id + "Extra\"\n" +                             // 7: longer id
            "local l = \"Play " + Id + "\"\n" +                             // 8: merely contains it
            "local old = \"" + Id + "\" -- " + LiteralTitleIdCheck.AllowMarker + "\n" + // 9: opt-out
            "local ok = \"" + Id + "\"\n";                                  // 10: still flagged
        Assert.That(Lines(src), Is.EqualTo(new[] { 10 }));
    }

    [Test]
    public void RunReportsOneWarningPerScriptAndSkipsEditorOnlyScripts()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        AssetDatabase.CreateFolder("Assets/Content", Id);
        AssetDatabase.CreateFolder(Root, "Editor");
        System.IO.File.WriteAllText(Root + "/game.lua.txt", "local a = '" + Id + "'\nlocal b = '" + Id + "/x'\n");
        System.IO.File.WriteAllText(Root + "/clean.lua.txt", "local id = storage.gameId\n");
        System.IO.File.WriteAllText(Root + "/Editor/tool.lua.txt", "local a = '" + Id + "'\n");
        System.IO.File.WriteAllText(Root + "/baker.lua.txt", "-- @editor-only\nlocal a = '" + Id + "'\n");
        AssetDatabase.Refresh();

        CheckResult result = new LiteralTitleIdCheck().Run(new PreUploadCheckContext
        {
            contentId = Id,
            contentRoot = Root,
        });

        Assert.That(result.outcome, Is.EqualTo(CheckOutcome.HasFindings));
        Assert.That(result.findings.Count, Is.EqualTo(1));
        Finding finding = result.findings[0];
        Assert.That(finding.severity, Is.EqualTo(CheckSeverity.Warning));
        Assert.That(finding.assetPath, Is.EqualTo(Root + "/game.lua.txt"));
        Assert.That(finding.detail, Does.Contain("dp.game_id").And.Contain("dp_title_of"));
    }
}
#endif
