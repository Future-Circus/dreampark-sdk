#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using DreamPark;
using DreamPark.PreUploadChecks;
using DreamPark.PreUploadChecks.Checks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class PreUploadBlockingFixTests
{
    private const string ContentId = "__PreUploadBlockingFixTests";
    private const string Root = "Assets/Content/" + ContentId;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content"))
            AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root))
            AssetDatabase.CreateFolder("Assets/Content", ContentId);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
    }

    [Test]
    public void MissingSequence_BuildFixCreatesRuntimeDefinitionWithoutPrefab()
    {
        var context = new PreUploadCheckContext
        {
            contentId = ContentId,
            contentRoot = Root,
            roots = new List<ContentRootInfo>(),
        };

        CheckResult result = new DreamSequenceRequiredCheck().Run(context);

        Assert.That(result.findings, Has.Count.EqualTo(1));
        Finding finding = result.findings.Single();
        Assert.That(finding.fixes.Select(f => f.label),
            Is.EqualTo(new[] { "Build Sequence Package" }));
        Assert.That(finding.fixes[0].run(), Is.True);

        var definition = AssetDatabase.LoadAssetAtPath<DreamSequencePackageDefinition>(
            DreamPark.Editor.DreamSequencePackageCompiler.DefinitionPath(ContentId));
        Assert.That(definition, Is.Not.Null);
        Assert.That(definition.startLevelPrefab, Is.Not.Null);
        Assert.That(definition.overlayLevelPrefab, Is.Not.Null);
        Assert.That(definition.gameOverLevelPrefab, Is.Not.Null);
        Assert.That(definition.gameManagerPrefab, Is.Not.Null);
        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamPark.Editor.DreamSequenceGenerator.SequencePrefabPath(ContentId)), Is.Null);
    }

    [Test]
    public void ExactDuplicateGroup_ReportsOnlyDeterministicRenameTargets()
    {
        CheckResult result = RunDuplicates(
            RootInfo("Assets/Content/Test/Z/A_Boss.prefab", "A_Boss"),
            RootInfo("Assets/Content/Test/A/A_Boss.prefab", "A_Boss"),
            RootInfo("Assets/Content/Test/M/A_Boss.prefab", "A_Boss"));

        Assert.That(result.findings, Has.Count.EqualTo(2));
        Assert.That(result.findings.Select(f => f.assetPath), Is.EqualTo(new[]
        {
            "Assets/Content/Test/M/A_Boss.prefab",
            "Assets/Content/Test/Z/A_Boss.prefab",
        }));
        Assert.That(result.findings.All(f => f.severity == CheckSeverity.Blocking), Is.True);
        Assert.That(result.findings.All(f => f.fixes[0].bulkKey == "duplicate-names/auto-rename"), Is.True);
        Assert.That(result.findings.All(f => f.fixes[0].bulkLabel == "Auto-rename duplicates"), Is.True);
        Assert.That(result.findings.All(f => f.fixes[0].canBulk), Is.True);
    }

    [Test]
    public void MixedExactAndCaseCollision_ReportsNMinusOneWithoutDuplicateAssets()
    {
        CheckResult result = RunDuplicates(
            RootInfo("Assets/Content/Test/A/A_Boss.prefab", "A_Boss"),
            RootInfo("Assets/Content/Test/B/A_Boss.prefab", "A_Boss"),
            RootInfo("Assets/Content/Test/C/A_boss.prefab", "A_boss"));

        Assert.That(result.findings, Has.Count.EqualTo(2));
        Assert.That(result.findings.Select(f => f.assetPath).Distinct(), Has.Count.EqualTo(2));
        Assert.That(result.findings.Count(f => f.severity == CheckSeverity.Blocking), Is.EqualTo(1));
        Assert.That(result.findings.Count(f => f.severity == CheckSeverity.Warning), Is.EqualTo(1));
    }

    [Test]
    public void SdkNetworkBudget_IsWithinPeerRelayCap()
    {
        int budget, cap;
        Assert.That(NetBudgetInvariant.IsSatisfied(out budget, out cap), Is.True,
            NetBudgetInvariant.FailureMessage(budget, cap));
    }

    private static CheckResult RunDuplicates(params ContentRootInfo[] roots)
    {
        return new DuplicateNamesCheck().Run(new PreUploadCheckContext
        {
            contentId = "Test",
            contentRoot = "Assets/Content/Test",
            roots = roots,
        });
    }

    private static ContentRootInfo RootInfo(string path, string name)
    {
        return new ContentRootInfo
        {
            assetPath = path,
            name = name,
            guid = path,
            kind = ContentRootKindPublic.Attraction,
        };
    }
}
#endif
