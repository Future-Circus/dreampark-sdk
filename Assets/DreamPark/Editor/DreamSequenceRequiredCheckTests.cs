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
    public void EmptyTitleHasNoPackageAndBlocks()
    {
        Assert.That(DreamSequencePackageCompiler.IsAuthored(ContentId), Is.False);
        Assert.That(Run().findings, Has.Some.Matches<Finding>(finding =>
            finding.severity == CheckSeverity.Blocking
            && finding.title == "Title has no playable package"));
        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.SequencePrefabPath(ContentId)), Is.Null);
    }

    [Test]
    public void UnfinishedSequenceIsLeftOutWithoutSequenceFindings()
    {
        // Only an attraction too big for the 12 × 18 ft room: the Sequence is
        // optional, so it is dropped from the upload instead of blocking it.
        SaveSequencePlacement(CreateAttraction("A_TooLarge", 30f, 40f));

        Assert.That(DreamSequencePackageCompiler.IsStarted(ContentId), Is.True);
        Assert.That(DreamSequencePackageCompiler.IsAuthored(ContentId), Is.False);
        Assert.That(Run().findings, Has.None.Matches<Finding>(finding =>
            finding.title.StartsWith("Sequence")));
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
            // Oct 11 2026: the Overlay and Game Over count toward the maximum
            // only, never the minimum room.
            if (!IsOverlayOrGameOver(definition, level))
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
        SaveSequencePlacement(CreateAttraction("A_Fits", 10f, 10f));
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
        // A playable stage keeps the Sequence authored, so its requirement is enforced.
        string fits = CreateAttraction("A_Fits", 10f, 10f);
        var data = new ContentSequenceStore.Data();
        data.items.Add(new ContentSequenceStore.Entry
            { kind = "attraction", id = "placement-1", attractionGuid = fits });
        data.items.Add(new ContentSequenceStore.Entry
            { kind = "attraction", id = "placement-2", attractionGuid = guid });
        ContentSequenceStore.Save(ContentId, data, true);

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

    [Test]
    public void MinimumRoomShrinksToALevelsSmallMinimumFrame()
    {
        string guid = CreateFramedAttraction("A_Framed", 11f, 17f, new Vector2(11f, 17f));
        SaveStagePlacement(guid);
        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);
        float lengthWithoutFrames = definition.minimumRoomMeters.y;
        Assert.That(lengthWithoutFrames, Is.EqualTo(17f * Ft).Within(0.001f),
            "Without frames the 17 ft Base cannot shrink at all.");

        string path = AssetDatabase.GUIDToAssetPath(guid);
        using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
            scope.prefabContentsRoot.GetComponent<AttractionTemplate>().SetLayoutFrames(
                new List<AttractionLayoutFrame> { SyntheticFrame(AttractionTemplate.MinimumFrameId,
                    AttractionLayoutFrameKind.Minimum, 4f, 4f, new Vector2(3f, 3f)) });
        definition = DreamSequencePackageCompiler.Compile(ContentId);

        AttractionTemplate attraction = AssetDatabase.LoadAssetAtPath<GameObject>(path)
            .GetComponent<AttractionTemplate>();
        Assert.That(attraction.MinimumFrame, Is.Not.Null);
        Assert.That(attraction.FrameHasValidBake(attraction.MinimumFrame), Is.True);
        Vector2 expectedMinimum = attraction.MinimumFrame.Bake.ShrinkFootprintMeters;
        Vector2 expectedMaximum = Vector2.Max(DreamSequenceCompatibility.StandardRoomMeters,
            attraction.PackingBake.GrowFootprintMeters);
        foreach (AttractionTemplate special in SpecialLevels(definition))
        {
            if (!IsOverlayOrGameOver(definition, special))
                expectedMinimum = Vector2.Max(expectedMinimum, special.PackingBake.ShrinkFootprintMeters);
            expectedMaximum = Vector2.Max(expectedMaximum, special.PackingBake.GrowFootprintMeters);
        }
        Assert.That(definition.levels, Has.Count.EqualTo(1));
        Assert.That(definition.minimumRoomMeters.x, Is.EqualTo(expectedMinimum.x).Within(0.001f));
        Assert.That(definition.minimumRoomMeters.y, Is.EqualTo(expectedMinimum.y).Within(0.001f));
        Assert.That(definition.minimumRoomMeters.y, Is.LessThan(lengthWithoutFrames - 0.5f));
        Assert.That(definition.maximumRoomMeters.x, Is.EqualTo(expectedMaximum.x).Within(0.001f));
        Assert.That(definition.maximumRoomMeters.y, Is.EqualTo(expectedMaximum.y).Within(0.001f));
    }

    [Test]
    public void RequiredAttractionThatOnlyFitsThroughAFrameIsCompiledAndNotBlocked()
    {
        string guid = CreateFramedAttraction("A_Huge", 30f, 40f, new Vector2(20f, 30f),
            SyntheticFrame(AttractionTemplate.MinimumFrameId, AttractionLayoutFrameKind.Minimum,
                8f, 8f, new Vector2(6f, 6f)));
        string path = AssetDatabase.GUIDToAssetPath(guid);
        using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
            scope.prefabContentsRoot.GetComponent<AttractionTemplate>().gameRequiresAttraction = true;
        SaveStagePlacement(guid);

        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);

        AttractionTemplate attraction = AssetDatabase.LoadAssetAtPath<GameObject>(path)
            .GetComponent<AttractionTemplate>();
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.True);
        Assert.That(definition.levels, Has.Count.EqualTo(1));
        Assert.That(definition.levels[0].sourceGuid, Is.EqualTo(guid));
        // The Base's 20 × 30 ft shrink never enters the minimum room.
        Vector2 expectedMinimum = attraction.MinimumFrame.Bake.ShrinkFootprintMeters;
        foreach (AttractionTemplate special in SpecialLevels(definition))
            if (!IsOverlayOrGameOver(definition, special))
                expectedMinimum = Vector2.Max(expectedMinimum, special.PackingBake.ShrinkFootprintMeters);
        Assert.That(definition.minimumRoomMeters.x, Is.EqualTo(expectedMinimum.x).Within(0.001f));
        Assert.That(definition.minimumRoomMeters.y, Is.EqualTo(expectedMinimum.y).Within(0.001f));
        // The largest frame (Base) cannot fit 12 × 18 ft, so its grow counts unrotated.
        Vector2 baseGrow = attraction.PackingBake.GrowFootprintMeters;
        Assert.That(definition.maximumRoomMeters.x, Is.EqualTo(baseGrow.x).Within(0.001f));
        Assert.That(definition.maximumRoomMeters.y, Is.EqualTo(baseGrow.y).Within(0.001f));

        CheckResult result = Run();
        Assert.That(result.findings, Has.None.Matches<Finding>(finding =>
            finding.title == "Sequence attraction no longer fits"
            || finding.title == "Sequence has no compatible attractions"
            || finding.title == "Sequence package definition is out of date"));
    }

    [Test]
    public void CompileRebakesStaleFramesButNeverChasesAFrameThatCannotBeBaked()
    {
        var root = new GameObject("A_Unbakeable");
        try
        {
            var attraction = root.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(10f, 10f);
            attraction.SetPackingBake(SyntheticBake(new Vector2(10f, 10f) * Ft, new Vector2(8f, 8f) * Ft));
            var needsBake = typeof(DreamSequencePackageCompiler).GetMethod("NeedsBake",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(needsBake, Is.Not.Null);

            // BakeFrame returns null for a sizeless frame, so it can never become
            // valid; it must not force a prefab rebake on every compile.
            var sizeless = new AttractionLayoutFrame
            {
                id = "fzero0001", name = "Zero", kind = AttractionLayoutFrameKind.Custom,
                sizeFeet = Vector2.zero,
            };
            attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { sizeless });
            Assert.That((bool)needsBake.Invoke(null, new object[] { attraction }), Is.False);

            // A frame resized after its bake is stale and still triggers a rebake.
            AttractionLayoutFrame stale = SyntheticFrame("fstale001", AttractionLayoutFrameKind.Custom,
                6f, 6f, new Vector2(5f, 5f));
            stale.sizeFeet = new Vector2(7f, 7f);
            attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { sizeless, stale });
            Assert.That((bool)needsBake.Invoke(null, new object[] { attraction }), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private const float Ft = 0.3048f;

    // Explicit endpoints keep a lone attraction a Sequence stage instead of
    // migrating it into the Start slot.
    private static void SaveStagePlacement(string guid)
    {
        var layout = new ContentSequenceStore.Data { hasExplicitEndpoints = true };
        layout.items.Add(new ContentSequenceStore.Entry
            { kind = "attraction", id = "stage", attractionGuid = guid });
        ContentSequenceStore.Save(ContentId, layout, true);
    }

    private static IEnumerable<AttractionTemplate> SpecialLevels(DreamSequencePackageDefinition definition)
    {
        yield return definition.startLevelPrefab.GetComponent<AttractionTemplate>();
        yield return definition.overlayLevelPrefab.GetComponent<AttractionTemplate>();
        yield return definition.gameOverLevelPrefab.GetComponent<AttractionTemplate>();
    }

    // Oct 11 2026: the Overlay and Game Over never set the minimum room.
    private static bool IsOverlayOrGameOver(DreamSequencePackageDefinition definition, AttractionTemplate level)
        => level != null
            && (level.gameObject == definition.overlayLevelPrefab
                || level.gameObject == definition.gameOverLevelPrefab);

    // A prefab with a pre-made (synthetic) Base bake and frames, grown 1.25× like
    // the default maxGrowthScale so the compiler treats every bake as current.
    private static string CreateFramedAttraction(string name, float width, float length,
        Vector2 shrinkFeet, params AttractionLayoutFrame[] frames)
    {
        string path = Root + "/" + name + ".prefab";
        var root = new GameObject(name);
        var attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(width, length);
        attraction.SetPackingBake(SyntheticBake(new Vector2(width, length) * Ft, shrinkFeet * Ft));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>(frames));
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return AssetDatabase.AssetPathToGUID(path);
    }

    private static AttractionLayoutFrame SyntheticFrame(string id, AttractionLayoutFrameKind kind,
        float width, float length, Vector2 shrinkFeet)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = id,
            kind = kind,
            sizeFeet = new Vector2(width, length),
        };
        frame.SetBake(SyntheticBake(frame.SizeMeters, shrinkFeet * Ft));
        return frame;
    }

    private static AttractionPackingBake SyntheticBake(Vector2 authored, Vector2 shrink) =>
        new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink,
            new List<AttractionPropPackingPose>());

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
