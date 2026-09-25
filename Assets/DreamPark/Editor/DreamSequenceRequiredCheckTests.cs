#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark;
using DreamPark.Editor;
using DreamPark.PreUploadChecks;
using DreamPark.PreUploadChecks.Checks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class DreamSequenceRequiredCheckTests
{
    private const string ContentId = "__DreamSequenceRequiredCheckTests";
    private const string Root = "Assets/Content/" + ContentId;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", ContentId);
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Root);

    [Test]
    public void EmptySequenceIsBlockingWithoutGeneratedPrefab()
    {
        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);

        Assert.That(Run().findings, Has.Some.Matches<Finding>(finding =>
            finding.severity == CheckSeverity.Blocking
            && finding.title == "Sequence has no compatible attractions"));
        Assert.That(definition, Is.Not.Null);
        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.SequencePrefabPath(ContentId)), Is.Null);
    }

    [Test]
    public void CompiledRoomLimitsIncludeEverySpecialScreenAndIncludedAttraction()
    {
        string first = CreateAttraction("A_First", 8f, 10f);
        string second = CreateAttraction("A_Second", 11f, 15f);
        var layout = new ContentSequenceStore.Data { hasExplicitEndpoints = true };
        layout.items.Add(new ContentSequenceStore.Entry
        {
            kind = "attraction", id = "first", attractionGuid = first,
        });
        layout.items.Add(new ContentSequenceStore.Entry
        {
            kind = "attraction", id = "second", attractionGuid = second,
        });
        ContentSequenceStore.Save(ContentId, layout, true);

        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);
        var included = new[]
        {
            definition.startLevelPrefab.GetComponent<AttractionTemplate>(),
            definition.overlayLevelPrefab.GetComponent<AttractionTemplate>(),
            definition.gameOverLevelPrefab.GetComponent<AttractionTemplate>(),
            AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(first))
                .GetComponent<AttractionTemplate>(),
            AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(second))
                .GetComponent<AttractionTemplate>(),
        };
        Vector2 expectedMinimum = Vector2.zero;
        Vector2 expectedMaximum = new Vector2(DreamSequenceTemplate.StandardWidthFeet,
            DreamSequenceTemplate.StandardLengthFeet) * 0.3048f;
        foreach (AttractionTemplate level in included)
        {
            Vector2 shrink = level.PackingBake.ShrinkFootprintMeters;
            Vector2 grow = level.PackingBake.GrowFootprintMeters;
            expectedMinimum = Vector2.Max(expectedMinimum, shrink);
            expectedMaximum = Vector2.Max(expectedMaximum, grow);
        }
        Assert.That(definition.minimumRoomMeters.x, Is.EqualTo(expectedMinimum.x).Within(0.001f));
        Assert.That(definition.minimumRoomMeters.y, Is.EqualTo(expectedMinimum.y).Within(0.001f));
        Assert.That(definition.maximumRoomMeters.x, Is.EqualTo(expectedMaximum.x).Within(0.001f));
        Assert.That(definition.maximumRoomMeters.y, Is.EqualTo(expectedMaximum.y).Within(0.001f));
    }

    [Test]
    public void MissingGameManager_IsBlocking()
    {
        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);
        definition.gameManagerPrefab = null;
        EditorUtility.SetDirty(definition);

        CheckResult result = Run();

        Assert.That(result.findings, Has.Some.Matches<Finding>(finding =>
            finding.severity == CheckSeverity.Blocking
            && finding.title == "Sequence package parts are missing"));
    }

    [Test]
    public void StalePackageOrder_IsBlocking()
    {
        DreamSequencePackageCompiler.Compile(ContentId);
        string guid = CreateAttraction("A_Fits", 10f, 10f);
        SaveSequencePlacement(guid);

        CheckResult result = Run();

        Assert.That(result.findings, Has.Some.Matches<Finding>(finding =>
            finding.title == "Sequence package definition is out of date"));
    }

    [Test]
    public void AttractionThatNoLongerFits_IsBlocking()
    {
        DreamSequencePackageCompiler.Compile(ContentId);
        string guid = CreateAttraction("A_TooLarge", 30f, 40f);
        string path = AssetDatabase.GUIDToAssetPath(guid);
        using (var scope = new UnityEditor.PrefabUtility.EditPrefabContentsScope(path))
            scope.prefabContentsRoot.GetComponent<AttractionTemplate>().gameRequiresAttraction = true;
        SaveSequencePlacement(guid);

        CheckResult result = Run();

        Assert.That(result.findings, Has.Some.Matches<Finding>(finding =>
            finding.severity == CheckSeverity.Blocking
            && finding.title == "Sequence attraction no longer fits"));
    }

    [Test]
    public void OptionalIncompatibleAttractionIsExcludedWithoutBlocking()
    {
        string incompatible = CreateAttraction("A_TooLarge", 30f, 40f);
        string compatible = CreateAttraction("A_Fits", 8f, 10f);
        var layout = new ContentSequenceStore.Data { hasExplicitEndpoints = true };
        layout.items.Add(new ContentSequenceStore.Entry
            { kind = "attraction", id = "optional", attractionGuid = incompatible });
        layout.items.Add(new ContentSequenceStore.Entry
            { kind = "attraction", id = "included", attractionGuid = compatible });
        ContentSequenceStore.Save(ContentId, layout, true);
        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);

        Assert.That(definition.levels, Has.Count.EqualTo(1));
        Assert.That(definition.levels[0].sourceGuid, Is.EqualTo(compatible));
        CheckResult result = Run();
        Assert.That(result.outcome, Is.EqualTo(CheckOutcome.Clean),
            string.Join("; ", result.findings.ConvertAll(finding => finding.title + ": " + finding.detail)));
    }

    private static string CreateAttraction(string name, float width, float length)
    {
        string path = Root + "/" + name + ".prefab";
        var root = new GameObject(name);
        var attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(width, length);
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return AssetDatabase.AssetPathToGUID(path);
    }

    private static void SaveSequencePlacement(string guid)
    {
        var data = new ContentSequenceStore.Data();
        data.items.Add(new ContentSequenceStore.Entry
        {
            kind = "attraction",
            id = "placement-1",
            attractionGuid = guid,
        });
        ContentSequenceStore.Save(ContentId, data, true);
    }

    private static CheckResult Run() => new DreamSequenceRequiredCheck().Run(
        new PreUploadCheckContext
        {
            contentId = ContentId,
            contentRoot = Root,
            roots = new List<ContentRootInfo>(),
        });
}
#endif
