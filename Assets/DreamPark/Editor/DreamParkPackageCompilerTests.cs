#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using DreamPark;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class DreamParkPackageCompilerTests
{
    private const string SourceId = "__DreamParkPackageCompilerTests";
    private const string TargetId = "__DreamParkPackageCompilerTests_beta";
    private const string Root = "Assets/Content/" + SourceId;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", SourceId);
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Root);

    [Test]
    public void CompilesRepeatedGroupedOccurrencesAndBetaAddresses()
    {
        Assert.That(ContentUploaderPanel.PublishedPackageSchemaVersion, Is.EqualTo(2),
            "Web package JSON must not inherit the runtime manifest schema version");
        string first = CreateAttraction("A_First");
        string second = CreateAttraction("A_Second");
        var optionalProp = new GameObject("P_Optional");
        optionalProp.AddComponent<PropTemplate>();
        string optionalPropPath = Root + "/P_Optional.prefab";
        PrefabUtility.SaveAsPrefabAsset(optionalProp, optionalPropPath);
        Object.DestroyImmediate(optionalProp);
        string propGuid = AssetDatabase.AssetPathToGUID(optionalPropPath);
        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);

        var adventure = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = first,
            endGuid = second,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry
                {
                    kind = "world", id = "group-one", name = "DesertLand",
                    attractionGuids = new List<string> { first, first },
                    attractionIds = new List<string> { "repeated-one", "repeated-two" },
                },
            },
        };
        ContentSequenceStore.Save(SourceId, adventure);
        var sequence = new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "sequence-one", attractionGuid = first },
                new ContentSequenceStore.Entry { id = "sequence-prop", attractionGuid = propGuid },
                new ContentSequenceStore.Entry { id = "sequence-two", attractionGuid = first },
            },
        };
        ContentSequenceStore.Save(SourceId, sequence, true);
        DreamSequencePackageCompiler.Compile(SourceId);

        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(manifest.schemaVersion, Is.EqualTo(DreamParkPackageManifest.CurrentSchemaVersion));
        Assert.That(manifest.packageRevision, Has.Length.EqualTo(64));
        Assert.That(manifest.contentId, Is.EqualTo(TargetId));
        Assert.That(manifest.adventure.playerAddress, Is.EqualTo(TargetId + "/Player"));
        Assert.That(manifest.adventure.gameManagerAddress, Is.EqualTo(TargetId + "/Game Container"));
        Assert.That(manifest.sequence.sequenceDefinitionAddress,
            Is.EqualTo(DreamSequencePackageDefinition.AddressFor(TargetId)));
        Assert.That(manifest.sequence.hasPackingMetadata, Is.True);
        Assert.That(manifest.sequence.authoredFootprintMeters.x,
            Is.EqualTo(DreamSequenceTemplate.StandardWidthFeet * 0.3048f).Within(0.001f));
        Assert.That(manifest.sequence.shrinkFootprintMeters.x, Is.GreaterThan(0f));
        Assert.That(manifest.adventure.groups.Select(g => g.occurrenceId),
            Is.EqualTo(new[] { "group-one" }));
        Assert.That(manifest.adventure.occurrences.Select(o => o.occurrenceId),
            Is.EqualTo(new[] { "start", "repeated-one", "repeated-two", "end" }));
        Assert.That(manifest.adventure.occurrences.Select(o => o.role),
            Is.EqualTo(new[] { "start", "stage", "stage", "end" }));
        Assert.That(manifest.adventure.occurrences.Select(o => o.order),
            Is.EqualTo(new[] { 0, 1, 2, 3 }));
        Assert.That(manifest.adventure.occurrences[1].resourceAddress,
            Is.EqualTo(manifest.adventure.occurrences[2].resourceAddress));
        Assert.That(manifest.adventure.occurrences.Skip(1).Take(2)
            .Select(o => o.groupOccurrenceId), Is.EqualTo(new[] { "group-one", "group-one" }));
        Assert.That(manifest.sequence.occurrences.Select(o => o.occurrenceId),
            Is.EqualTo(new[] { "sequence-one", "sequence-two" }));
        Assert.That(manifest.sequence.occurrences.All(o => o.resourceAddress.StartsWith(TargetId + "/")),
            Is.True);
        Assert.That(AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(
            DreamParkPackageCompiler.ManifestPath(SourceId)), Is.SameAs(manifest));

        JSONObject web = ContentUploaderPanel.BuildPublishedPackageJson(manifest.adventure, SourceId);
        Assert.That(web.GetField("groups").list.Count, Is.EqualTo(1));
        Assert.That(web.GetField("attractions").list.Count, Is.EqualTo(4));
        Assert.That(web.GetField("attractions").list[1].GetField("resourceName").stringValue,
            Is.EqualTo("Content/" + SourceId + "/A_First"));
        Assert.That(web.GetField("attractions").list[1].GetField("groupIndex").longValue, Is.EqualTo(0));
        Assert.That(manifest.adventure.occurrences[1].resourceAddress,
            Is.Not.EqualTo(web.GetField("attractions").list[1].GetField("resourceName").stringValue));
    }

    [Test]
    public void BetaSequenceDefinitionRetargetsEveryLazyLevelAddress()
    {
        string first = CreateAttraction("A_First");
        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "level-one", attractionGuid = first },
            },
        }, true);

        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(SourceId, TargetId);
        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(definition.levels.Select(level => level.address),
            Is.EqualTo(manifest.sequence.occurrences.Select(level => level.resourceAddress)));
        Assert.That(definition.levels.All(level => level.address.StartsWith(TargetId + "/")), Is.True);
    }

    [Test]
    public void DeletedAuthoredOccurrenceCannotSilentlyDisappearDuringReconciliation()
    {
        string first = CreateAttraction("A_First");
        string missing = CreateAttraction("A_Deleted");
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = first,
            endGuid = first,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "deleted-stage", attractionGuid = missing },
            },
        });
        AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(missing));

        Assert.Throws<System.InvalidOperationException>(() =>
            DreamParkPackageCompiler.CompileRecipe(SourceId, SourceId, false,
                SourceId + "/Player", SourceId + "/Game Container"));
    }

    [Test]
    public void ContentSweepRegistersManifestAtStableBetaAddress()
    {
        string first = CreateAttraction("A_First");
        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry { id = "level-one", attractionGuid = first },
            },
        }, true);
        DreamSequencePackageCompiler.Compile(SourceId);
        DreamParkPackageCompiler.Compile(SourceId);
        // The content sweep also renders previews; batch-mode -nographics
        // cannot allocate RenderTextures. The assertions below target catalog
        // registration, not preview rendering.
        bool priorIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            ContentProcessor.ForceUpdateContent(SourceId, TargetId);
            string path = DreamParkPackageCompiler.ManifestPath(SourceId);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var entry = AddressableAssetSettingsDefaultObject.Settings.FindAssetEntry(guid);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry.address, Is.EqualTo(DreamParkPackageManifest.AddressFor(TargetId)));
            var definition = AssetDatabase.LoadAssetAtPath<DreamSequencePackageDefinition>(
                DreamSequencePackageCompiler.DefinitionPath(SourceId));
            Assert.That(definition.levels.Single().address, Does.StartWith(TargetId + "/"));
        }
        finally
        {
            ContentProcessor.RestoreContentAfterTargetBuild(SourceId, TargetId);
            LogAssert.ignoreFailingMessages = priorIgnore;
        }
    }

    [Test]
    public void NewGameManagerIsOptimizerExempt()
    {
        string path = DreamSequenceGenerator.EnsureContainer(SourceId);
        GameObject manager = AssetDatabase.LoadAssetAtPath<GameObject>(path);

        Assert.That(manager.GetComponent<OptimizedAFIgnore>(), Is.Not.Null);
    }

    [Test]
    public void CompilesArenaBucketsAndPublishedPriorityOrder()
    {
        string attractionGuid = CreateAttraction("A_ArenaChoice");
        string attractionPath = AssetDatabase.GUIDToAssetPath(attractionGuid);
        var attraction = AssetDatabase.LoadAssetAtPath<GameObject>(attractionPath)
            .GetComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = Vector2.one;
        EditorUtility.SetDirty(attraction);
        PrefabUtility.SavePrefabAsset(attraction.gameObject);
        Assert.That(AttractionPackingBaker.BakeIntoAsset(attraction), Is.True);

        var propObject = new GameObject("P_ArenaChoice");
        PropTemplate prop = propObject.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one * 0.3048f;
        string propPath = Root + "/P_ArenaChoice.prefab";
        PrefabUtility.SaveAsPrefabAsset(propObject, propPath);
        Object.DestroyImmediate(propObject);
        string propGuid = AssetDatabase.AssetPathToGUID(propPath);

        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);
        ArenaPackageStore.Save(SourceId, new ArenaPackageStore.Data
        {
            buckets = new List<ArenaPackageStore.Bucket>
            {
                new ArenaPackageStore.Bucket
                {
                    widthFeet = 1,
                    lengthFeet = 1,
                    guids = new List<string> { propGuid, attractionGuid },
                },
            },
        });

        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(manifest.arena, Is.Not.Null);
        Assert.That(manifest.arena.kind, Is.EqualTo("arena"));
        Assert.That(manifest.arena.arenaBuckets.Single().occurrenceIds,
            Is.EqualTo(new[] { "arena-" + propGuid, "arena-" + attractionGuid }));
        Assert.That(manifest.arena.occurrences.Select(item => item.widthFeet),
            Is.EqualTo(new[] { 1, 1 }));
        Assert.That(manifest.arena.occurrences.All(item => !item.required), Is.True,
            "Arena entries are mutually exclusive candidates, never independently required.");
        Assert.That(manifest.RecipeFor("ARENA"), Is.SameAs(manifest.arena));

        JSONObject web = ContentUploaderPanel.BuildPublishedPackageJson(manifest.arena, SourceId);
        JSONObject bucket = web.GetField("buckets").list.Single();
        Assert.That(bucket.GetField("widthFeet").longValue, Is.EqualTo(1));
        Assert.That(bucket.GetField("lengthFeet").longValue, Is.EqualTo(1));
        Assert.That(bucket.GetField("attractions").list.Select(item =>
                item.GetField("resourceName").stringValue),
            Is.EqualTo(new[]
            {
                "Content/" + SourceId + "/P_ArenaChoice",
                "Content/" + SourceId + "/A_ArenaChoice",
            }));
    }

    private static string CreateAttraction(string name)
    {
        string path = Root + "/" + name + ".prefab";
        var root = new GameObject(name);
        var attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(8f, 10f);
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        Assert.That(AttractionPackingBaker.BakeIntoAsset(
            AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<AttractionTemplate>()), Is.True);
        return AssetDatabase.AssetPathToGUID(path);
    }
}
#endif
