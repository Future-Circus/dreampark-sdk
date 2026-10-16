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
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        RemoveAddressablesLeftovers();
    }

    // ForceUpdateContent leaves per-id state OUTSIDE Root that deleting the folder
    // does not touch: Assets/AddressableAssetsData/<id>-PackingOrder.json, groups
    // named "<id>-…" (e.g. "<id>-Bundle-P_LuaInjections") and the "<id>" label in
    // AddressableAssetSettings.asset. The Beta tests used to leave all three behind
    // in every project they ran in (Oct 13 2026). Everything carrying the fixture's
    // prefix is removed, including leftovers from earlier runs.
    private static void RemoveAddressablesLeftovers()
    {
        foreach (string id in new[] { SourceId, TargetId, SourceId + BetaContentId.Suffix })
        {
            string order = PackingOrderStore.PathForContent(id);
            if (System.IO.File.Exists(order)) AssetDatabase.DeleteAsset(order);
        }

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null) return;
        bool changed = false;
        foreach (var group in settings.groups
                     .Where(g => g != null && g.Name.StartsWith(SourceId, System.StringComparison.Ordinal))
                     .ToList())
        {
            settings.RemoveGroup(group);
            changed = true;
        }
        foreach (string label in settings.GetLabels()
                     .Where(l => l.StartsWith(SourceId, System.StringComparison.Ordinal))
                     .ToList())
        {
            settings.RemoveLabel(label);
            changed = true;
        }
        if (!changed) return;
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    [Test]
    public void CompilesRepeatedGroupedOccurrencesAndBetaAddresses()
    {
        Assert.That(ContentUploaderPanel.PublishedPackageSchemaVersion, Is.EqualTo(3),
            "Web package JSON must not inherit the runtime manifest schema version");
        Assert.That(DreamParkPackageManifest.CurrentSchemaVersion, Is.EqualTo(5));
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

        var reconciled = ContentSequenceStore.LoadAndReconcile(SourceId, new[] { first },
            new[] { first }, false);
        ContentSequenceStore.Save(SourceId, reconciled);

        Assert.Throws<System.InvalidOperationException>(() =>
            DreamParkPackageCompiler.CompileRecipe(SourceId, SourceId, false,
                SourceId + "/Player", SourceId + "/Game Container"));
    }

    [Test]
    public void MissingGroupChildAppearsInAdvisoryFindings()
    {
        string missing = CreateAttraction("A_Deleted");
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            items = new List<ContentSequenceStore.Entry>
            {
                new ContentSequenceStore.Entry
                {
                    kind = "world", id = "group", name = "BlockLand",
                    attractionGuids = new List<string> { missing },
                    attractionIds = new List<string> { "child" },
                },
            },
        });
        AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(missing));

        var result = new DreamPark.PreUploadChecks.Checks.PackageReferencesCheck().Run(
            new DreamPark.PreUploadChecks.PreUploadCheckContext
            {
                contentId = SourceId,
                contentRoot = Root,
            });

        Assert.That(result.findings, Has.Count.EqualTo(1));
        Assert.That(result.findings[0].severity,
            Is.EqualTo(DreamPark.PreUploadChecks.CheckSeverity.Blocking));
        Assert.That(result.findings[0].title, Does.Contain("BlockLand"));
        Assert.That(result.findings[0].detail, Does.Contain(missing));
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

    // Oct 13 2026 (beta-gameid-audit §4 option 1): Beta uploads stamped every
    // `gameId` field but left LuaBehaviour string injections holding the
    // source id, so content compared "<id>" against stamped "<id>Beta" ids on
    // device (LaserTag roster empty, CrashCourse races never started). Exact
    // ids and "<id>/" prefixes are stamped; everything else is untouched; the
    // restore pass puts every value back byte-for-byte.
    [Test]
    public void BetaStampRewritesLuaStringInjectionsAndRestoreFlipsThemBack()
    {
        var (innerPath, outerPath) = CreateLuaInjectionPrefabs();
        bool priorIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;   // preview rendering in batch mode
        try
        {
            ContentProcessor.ForceUpdateContent(SourceId, TargetId);
            Assert.That(InjectionValues(innerPath), Is.EqualTo(ExpectedInjections(TargetId, "start")));
            Assert.That(InjectionValues(outerPath),
                Is.EqualTo(ExpectedInjections(TargetId, TargetId + "/Audio/pop")),
                "inherited values follow the stamped nested prefab; overrides are stamped in place");
        }
        finally
        {
            ContentProcessor.RestoreContentAfterTargetBuild(SourceId, TargetId);
            LogAssert.ignoreFailingMessages = priorIgnore;
        }
        Assert.That(InjectionValues(innerPath), Is.EqualTo(ExpectedInjections(SourceId, "start")));
        Assert.That(InjectionValues(outerPath), Is.EqualTo(ExpectedInjections(SourceId, SourceId + "/Audio/pop")));
    }

    // A Beta publish that never reached RestoreContentAfterTargetBuild (editor
    // crash, domain reload) must not ship "<id>Beta" injections in the next
    // release: any plain ForceUpdateContent(source) flips them back.
    [Test]
    public void PlainContentPassRestoresInjectionsLeftStampedByInterruptedBetaPublish()
    {
        string betaId = SourceId + BetaContentId.Suffix;
        string betaOrder = PackingOrderStore.PathForContent(betaId);
        bool betaOrderExisted = System.IO.File.Exists(betaOrder);
        var (innerPath, outerPath) = CreateLuaInjectionPrefabs();
        bool priorIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;   // preview rendering in batch mode
        try
        {
            ContentProcessor.ForceUpdateContent(SourceId, betaId);
            Assert.That(InjectionValues(innerPath), Is.EqualTo(ExpectedInjections(betaId, "start")));
            ContentProcessor.ForceUpdateContent(SourceId);   // no restore call: the interrupted case
            Assert.That(InjectionValues(innerPath), Is.EqualTo(ExpectedInjections(SourceId, "start")));
            Assert.That(InjectionValues(outerPath), Is.EqualTo(ExpectedInjections(SourceId, SourceId + "/Audio/pop")));
        }
        finally
        {
            ContentProcessor.RestoreContentAfterTargetBuild(SourceId, betaId);
            LogAssert.ignoreFailingMessages = priorIgnore;
            if (!betaOrderExisted && System.IO.File.Exists(betaOrder)) AssetDatabase.DeleteAsset(betaOrder);
        }
    }

    private static string[] ExpectedInjections(string id, string lastValue) => new[]
    {
        id,                           // exact id: stamped
        id + "/FX_Explosion",         // "<id>/" prefix: stamped
        "Play " + SourceId,           // merely contains the id: untouched
        SourceId + "Extra",           // id prefix without '/': untouched
        lastValue,
    };

    private static (string inner, string outer) CreateLuaInjectionPrefabs()
    {
        string innerPath = Root + "/P_LuaInjections.prefab";
        var inner = new GameObject("P_LuaInjections");
        inner.AddComponent<PropTemplate>();
        inner.AddComponent<LuaBehaviour>().stringInjections = new[]
        {
            new StringInjection { name = "gameIdFilter", value = SourceId },
            new StringInjection { name = "explosionName", value = SourceId + "/FX_Explosion" },
            new StringInjection { name = "label", value = "Play " + SourceId },
            new StringInjection { name = "sibling", value = SourceId + "Extra" },
            new StringInjection { name = "action", value = "start" },
        };
        PrefabUtility.SaveAsPrefabAsset(inner, innerPath);
        Object.DestroyImmediate(inner);

        string outerPath = Root + "/P_LuaOuter.prefab";
        var outer = new GameObject("P_LuaOuter");
        outer.AddComponent<PropTemplate>();
        var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(innerPath));
        nested.transform.SetParent(outer.transform, false);
        var nestedLua = nested.GetComponent<LuaBehaviour>();
        nestedLua.stringInjections[4].value = SourceId + "/Audio/pop";
        PrefabUtility.RecordPrefabInstancePropertyModifications(nestedLua);
        PrefabUtility.SaveAsPrefabAsset(outer, outerPath);
        Object.DestroyImmediate(outer);
        return (innerPath, outerPath);
    }

    private static string[] InjectionValues(string prefabPath) =>
        AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath)
            .GetComponentInChildren<LuaBehaviour>(true)
            .stringInjections.Select(i => i.value).ToArray();

    // Oct 9 2026: a Beta grouping pass ran every path-scoped step against
    // Assets/Content/{id}Beta, which never exists, so Beta builds had no
    // Bootstrap, Shared-Foundation or Code group: no delivery-index packages,
    // the SDK shader copied into every material bundle, and every script
    // copied into every bundle that used it. Release ids must stay unchanged.
    [Test]
    public void BetaGroupingPassBuildsFoundationCodeAndBootstrapGroups()
    {
        if (BundlingStrategyPrefs.Current != BundlingStrategy.Smart)
            Assert.Ignore("Smart bundling is not this machine's strategy.");
        string betaId = SourceId + BetaContentId.Suffix;
        string betaOrder = PackingOrderStore.PathForContent(betaId);
        bool betaOrderExisted = System.IO.File.Exists(betaOrder);

        Shader shader = Shader.Find("Shader Graphs/DreamPark-UniversalShader");
        Assert.That(shader, Is.Not.Null, "the SDK lit shader must be loadable");
        string shaderPath = AssetDatabase.GetAssetPath(shader);
        Assert.That(shaderPath, Does.StartWith("Assets/DreamPark/"));
        var material = new Material(shader);
        AssetDatabase.CreateAsset(material, Root + "/M_Foundation.mat");
        var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prop.name = "P_Foundation";
        Object.DestroyImmediate(prop.GetComponent<Collider>());
        prop.AddComponent<PropTemplate>();
        prop.GetComponent<MeshRenderer>().sharedMaterial =
            AssetDatabase.LoadAssetAtPath<Material>(Root + "/M_Foundation.mat");
        PrefabUtility.SaveAsPrefabAsset(prop, Root + "/P_Foundation.prefab");
        prop.name = "P_SecondUser";
        PrefabUtility.SaveAsPrefabAsset(prop, Root + "/P_SecondUser.prefab");
        Object.DestroyImmediate(prop);
        string luaPath = Root + "/foundation_test.lua.txt";
        System.IO.File.WriteAllText(luaPath, "function start() end\n");
        AssetDatabase.ImportAsset(luaPath);
        CreateAttraction("A_First");
        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);
        DreamParkPackageCompiler.Compile(SourceId);

        string shaderGuid = AssetDatabase.AssetPathToGUID(shaderPath);
        string luaGuid = AssetDatabase.AssetPathToGUID(luaPath);
        string manifestGuid = AssetDatabase.AssetPathToGUID(DreamParkPackageCompiler.ManifestPath(SourceId));
        string materialGuid = AssetDatabase.AssetPathToGUID(Root + "/M_Foundation.mat");
        string GroupOf(string guid) =>
            AddressableAssetSettingsDefaultObject.Settings.FindAssetEntry(guid)?.parentGroup?.Name;
        // Every grouped asset of this test (content folder + foundation), with
        // the upload id stripped from its group name.
        Dictionary<string, string> Grouping(string uploadId) =>
            AddressableAssetSettingsDefaultObject.Settings.groups
                .Where(g => g != null && g.Name.StartsWith(uploadId + "-", System.StringComparison.Ordinal))
                .SelectMany(g => g.entries.Select(e => (e.guid, group: g.Name.Substring(uploadId.Length + 1))))
                .Where(x => AssetDatabase.GUIDToAssetPath(x.guid).StartsWith(Root + "/")
                    || x.guid == shaderGuid)
                .ToDictionary(x => x.guid, x => x.group);
        Dictionary<string, string> betaGrouping = null;

        bool priorIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;   // preview rendering in batch mode
        try
        {
            ContentProcessor.ForceUpdateContent(SourceId, betaId);
            Assert.That(GroupOf(shaderGuid), Is.EqualTo(SmartBundleGrouper.SharedFoundationGroupName(betaId)),
                "Beta: the SDK shader is promoted once into {id}Beta-Shared-Foundation");
            Assert.That(GroupOf(luaGuid), Is.EqualTo(SmartBundleGrouper.CodeGroupName(betaId)),
                "Beta: content Lua goes to {id}Beta-Code");
            Assert.That(GroupOf(manifestGuid), Is.EqualTo(SmartBundleGrouper.BootstrapGroupName(betaId)),
                "Beta: the package manifest gets its own bootstrap bundle");
            Assert.That(DeliveryIndexGenerator.RoleForGroup(betaId, GroupOf(shaderGuid)), Is.EqualTo("foundation"));
            Assert.That(DeliveryIndexGenerator.RoleForGroup(betaId, GroupOf(luaGuid)), Is.EqualTo("code"));
            Assert.That(DeliveryIndexGenerator.RoleForGroup(betaId, GroupOf(manifestGuid)), Is.EqualTo("bootstrap"));
            Assert.That(GroupOf(materialGuid), Is.EqualTo(SmartBundleGrouper.SharedSmallGroupName(betaId)),
                "a small material used by two props goes to the shared-set bucket, not to the first prop");
            betaGrouping = Grouping(betaId);
        }
        finally
        {
            ContentProcessor.RestoreContentAfterTargetBuild(SourceId, betaId);
            LogAssert.ignoreFailingMessages = priorIgnore;
            if (!betaOrderExisted && System.IO.File.Exists(betaOrder)) AssetDatabase.DeleteAsset(betaOrder);
        }

        // The release pass is unchanged: same assets, release-named groups.
        Assert.That(GroupOf(shaderGuid), Is.EqualTo(SmartBundleGrouper.SharedFoundationGroupName(SourceId)));
        Assert.That(GroupOf(luaGuid), Is.EqualTo(SmartBundleGrouper.CodeGroupName(SourceId)));
        Assert.That(GroupOf(manifestGuid), Is.EqualTo(SmartBundleGrouper.BootstrapGroupName(SourceId)));
        Dictionary<string, string> releaseGrouping = Grouping(SourceId);
        Assert.That(betaGrouping.Count, Is.GreaterThan(5));
        Assert.That(betaGrouping, Is.EquivalentTo(releaseGrouping),
            "a Beta upload groups the same source exactly like a release upload, only the id prefix differs");
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

    [Test]
    public void CompilesArenaFrameCandidatesWithPinnedFramesAndFullLadder()
    {
        string guid = CreateFramedAttraction("A_Framed");
        var player = new GameObject("Player");
        player.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(player, Root + "/Player.prefab");
        Object.DestroyImmediate(player);
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = guid,
            endGuid = guid,
        });

        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(manifest.schemaVersion, Is.EqualTo(5));
        PackageRecipe arena = manifest.arena;
        Assert.That(arena, Is.Not.Null);
        // Empty attraction: every frame shrinks to exactly its authored size.
        Assert.That(arena.arenaBuckets.Select(b => (b.widthFeet, b.lengthFeet)),
            Is.EqualTo(new[] { (4, 6), (20, 20), (20, 30) }));
        Assert.That(arena.arenaBuckets.Select(b => b.occurrenceIds.Single()), Is.EqualTo(new[]
        {
            "arena-" + guid + "-minimum", "arena-" + guid, "arena-" + guid + "-fwide0001",
        }));
        Assert.That(arena.occurrences.Select(o => o.frameId),
            Is.EqualTo(new[] { "minimum", "base", "fwide0001" }));
        Assert.That(arena.occurrences.Select(o => (o.widthFeet, o.lengthFeet)),
            Is.EqualTo(new[] { (4, 6), (20, 20), (20, 30) }));
        Assert.That(arena.occurrences.Select(o => o.resourceAddress).Distinct().Count(), Is.EqualTo(1));
        Assert.That(arena.occurrences.All(o => !o.required), Is.True);
        foreach (PackageOccurrence occurrence in arena.occurrences)
        {
            Assert.That(occurrence.frames.Select(f => f.frameId),
                Is.EqualTo(new[] { "fwide0001", "base", "minimum" }), "full ladder, largest first");
            Assert.That(occurrence.authoredFootprintMeters.x, Is.EqualTo(20f * 0.3048f).Within(0.001f),
                "legacy envelope stays Base");
            Assert.That(occurrence.hasEssentialProps, Is.True, "Base essential mirrors Minimum");
            Assert.That(occurrence.essentialShrinkFootprintMeters.x,
                Is.EqualTo(6f * 0.3048f).Within(0.001f));
        }
        PackageFrameEnvelope wide = arena.occurrences[0].frames[0];
        Assert.That(wide.kind, Is.EqualTo("custom"));
        Assert.That(wide.name, Is.EqualTo("Wide"));
        Assert.That(wide.authoredFootprintMeters.x, Is.EqualTo(30f * 0.3048f).Within(0.001f));
        Assert.That(wide.growScale.x, Is.EqualTo(1.25f).Within(0.001f));
        Assert.That(arena.occurrences[0].frames.Select(f => f.kind),
            Is.EqualTo(new[] { "custom", "base", "minimum" }));

        // Adventure/Sequence occurrences carry the ladder but never pin a frame.
        Assert.That(manifest.adventure.occurrences.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(manifest.adventure.occurrences.All(o =>
            string.IsNullOrEmpty(o.frameId) && o.frames.Count == 3), Is.True);

        JSONObject web = ContentUploaderPanel.BuildPublishedPackageJson(arena, SourceId);
        var entries = web.GetField("buckets").list
            .Select(bucket => bucket.GetField("attractions").list.Single()).ToList();
        Assert.That(entries.Select(e => e.GetField("resourceName").stringValue).Distinct(),
            Is.EqualTo(new[] { "Content/" + SourceId + "/A_Framed" }));
        Assert.That(entries.Select(e => e.HasField("frameId") ? e.GetField("frameId").stringValue : null),
            Is.EqualTo(new[] { "minimum", null, "fwide0001" }), "frameId only when not Base");
    }

    [Test]
    public void ArenaFrameKeyWithoutABakedFrameIsDroppedBeforeCompile()
    {
        string guid = CreateFramedAttraction("A_Framed");
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
                    widthFeet = 20, lengthFeet = 20,
                    guids = new List<string> { guid + ":fgone0001", guid },
                },
            },
        });

        PackageRecipe arena = DreamParkPackageCompiler.CompileArenaRecipe(SourceId, TargetId,
            TargetId + "/Player", TargetId + "/Game Container");

        Assert.That(arena.occurrences.Select(o => o.occurrenceId), Does.Not.Contain(
            "arena-" + guid + "-fgone0001"));
        Assert.That(arena.arenaBuckets.Single(b => b.widthFeet == 20 && b.lengthFeet == 20)
            .occurrenceIds, Is.EqualTo(new[] { "arena-" + guid }));
        Assert.That(DreamParkPackageCompiler.ArenaOccurrenceId(guid, "base"), Is.EqualTo("arena-" + guid));
        Assert.That(DreamParkPackageCompiler.ArenaOccurrenceId(guid, "minimum"),
            Is.EqualTo("arena-" + guid + "-minimum"));
    }

    [Test]
    public void CatalogPackingV9CarriesFrameLadderAndMinimumActiveRoots()
    {
        var root = new GameObject("A_CatalogFrames");
        try
        {
            AttractionTemplate attraction = root.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(20f, 20f);
            ChildProp(root, "Left", -1f);
            Transform right = ChildProp(root, "Right", 1f);
            Transform extra = ChildProp(root, "Extra", 3f);
            extra.gameObject.SetActive(false);
            AttractionLayoutFrame minimum = Frame(AttractionTemplate.MinimumFrameId, "Minimum",
                AttractionLayoutFrameKind.Minimum, 10f, 10f, Toggle(right, false));
            AttractionLayoutFrame big = Frame("fbig00001", "Big",
                AttractionLayoutFrameKind.Custom, 30f, 20f, Toggle(extra, true));
            attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { minimum, big });
            Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

            JSONObject packing = ContentUploaderPanel.BuildPackingUpload(attraction);

            Assert.That(packing.GetField("schemaVersion").intValue, Is.EqualTo(9));
            Assert.That(packing.GetField("authored").GetField("widthFt").floatValue,
                Is.EqualTo(20f).Within(0.01f), "top-level fields describe Base");
            Assert.That(packing.GetField("essentialShrink").GetField("widthFt").floatValue,
                Is.EqualTo(minimum.Bake.ShrinkFootprintMeters.x / 0.3048f).Within(0.01f),
                "Base essential fields mirror the Minimum frame");
            Assert.That(packing.GetField("essentialProps").list
                    .Select(item => item.GetField("name").stringValue),
                Is.EqualTo(new[] { "Left" }), "the Minimum frame's active packing roots");
            JSONObject frames = packing.GetField("frames");
            Assert.That(frames.list.Select(f => f.GetField("id").stringValue),
                Is.EqualTo(new[] { "fbig00001", "base", "minimum" }));
            Assert.That(frames.list.Select(f => f.GetField("kind").stringValue),
                Is.EqualTo(new[] { "custom", "base", "minimum" }));
            Assert.That(frames.list.Select(f => f.GetField("name").stringValue),
                Is.EqualTo(new[] { "Big", "Base", "Minimum" }));
            JSONObject bigJson = frames.list[0];
            Assert.That(bigJson.GetField("authored").GetField("widthFt").floatValue,
                Is.EqualTo(30f).Within(0.01f));
            Assert.That(bigJson.GetField("authored").GetField("lengthFt").floatValue,
                Is.EqualTo(20f).Within(0.01f));
            Assert.That(bigJson.GetField("growScale").GetField("x").floatValue,
                Is.EqualTo(1.25f).Within(0.001f));
            Assert.That(bigJson.GetField("shrinkScale").HasField("z"), Is.True);
            Assert.That(bigJson.HasField("shrink") && bigJson.HasField("grow"), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        var plain = new GameObject("A_CatalogPlain");
        try
        {
            AttractionTemplate attraction = plain.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(20f, 20f);
            ChildProp(plain, "Only", 0f);
            Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

            JSONObject packing = ContentUploaderPanel.BuildPackingUpload(attraction);

            Assert.That(packing.GetField("schemaVersion").intValue, Is.EqualTo(9));
            Assert.That(packing.HasField("frames"), Is.False, "frames only when the attraction has frames");
            Assert.That(packing.GetField("essentialProps").count, Is.EqualTo(0));
        }
        finally
        {
            Object.DestroyImmediate(plain);
        }
    }

    // A framed prop-less attraction: Base 20 × 20 ft, custom "Wide" 30 × 20 ft and
    // a 6 × 4 ft Minimum. With no packing items every bake's shrink == authored.
    private static string CreateFramedAttraction(string name)
    {
        string path = Root + "/" + name + ".prefab";
        var root = new GameObject(name);
        var attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fwide0001", "Wide", AttractionLayoutFrameKind.Custom, 30f, 20f),
            Frame(AttractionTemplate.MinimumFrameId, "Minimum", AttractionLayoutFrameKind.Minimum, 6f, 4f),
        });
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        AttractionTemplate asset = AssetDatabase.LoadAssetAtPath<GameObject>(path)
            .GetComponent<AttractionTemplate>();
        Assert.That(AttractionPackingBaker.BakeIntoAsset(asset), Is.True);
        asset = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<AttractionTemplate>();
        Assert.That(asset.GetFrameEnvelopes().Count, Is.EqualTo(3), "Base + both frames baked");
        return AssetDatabase.AssetPathToGUID(path);
    }

    private static AttractionLayoutFrame Frame(string id, string name, AttractionLayoutFrameKind kind,
        float widthFeet, float lengthFeet, params AttractionLayoutOverride[] overrides)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = kind,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.overrides.AddRange(overrides);
        return frame;
    }

    private static AttractionLayoutOverride Toggle(Transform target, bool active) => new AttractionLayoutOverride
    {
        path = target.name,
        target = target,
        setActive = true,
        active = active,
    };

    private static Transform ChildProp(GameObject root, string name, float x)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
        PropTemplate prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        return go.transform;
    }

    // ── Phone Player slot (schema 5) ─────────────────────────────

    private static string CreateRig(string name, bool withRig = true)
    {
        string path = Root + "/" + name + ".prefab";
        var go = new GameObject(name);
        if (withRig) go.AddComponent<PlayerRig>();
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return AssetDatabase.AssetPathToGUID(path);
    }

    private static void SaveMinimalAdventure()
    {
        string guid = CreateAttraction("A_Only");
        ContentSequenceStore.Save(SourceId, new ContentSequenceStore.Data
        {
            hasExplicitEndpoints = true,
            startGuid = guid,
            endGuid = guid,
        });
    }

    [Test]
    public void EmptyPhoneSlotCompilesNoPhoneRigAndNeverPicksPhonePlayerAsPlayer()
    {
        // "Phone Player" sorts before "Rig" and no prefab is named "Player":
        // without the exclusion the phone rig would become the headset Player.
        CreateRig("Phone Player");
        CreateRig("Rig");
        SaveMinimalAdventure();

        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(manifest.adventure, Is.Not.Null);
        Assert.That(manifest.adventure.playerAddress, Is.EqualTo(TargetId + "/Rig"));
        Assert.That(manifest.adventure.phonePlayerAddress, Is.Empty);
    }

    [Test]
    public void PhoneSlotIsWrittenToEveryRecipeAndExcludedFromPlayer()
    {
        // The slot prefab sorts first; FindPlayerAddress must skip it.
        string phone = CreateRig("A Phone Rig");
        CreateRig("Z Headset Rig");
        SaveMinimalAdventure();
        PhonePlayerStore.Save(SourceId, phone);

        DreamParkPackageManifest manifest = DreamParkPackageCompiler.Compile(SourceId, TargetId);

        Assert.That(manifest.schemaVersion, Is.EqualTo(5));
        Assert.That(manifest.adventure.playerAddress, Is.EqualTo(TargetId + "/Z Headset Rig"));
        Assert.That(manifest.adventure.phonePlayerAddress, Is.EqualTo(TargetId + "/A Phone Rig"));
        if (manifest.arena != null)
            Assert.That(manifest.arena.phonePlayerAddress, Is.EqualTo(TargetId + "/A Phone Rig"));
        Assert.That(DreamParkPackageCompiler.FindPlayerAddress(Root, SourceId, SourceId),
            Is.EqualTo(SourceId + "/Z Headset Rig"));
    }

    [Test]
    public void PhoneSlotWithoutPlayerRigFailsTheCompile()
    {
        CreateRig("Player");
        string notARig = CreateRig("Not A Rig", withRig: false);
        SaveMinimalAdventure();
        PhonePlayerStore.Save(SourceId, notARig);

        var e = Assert.Throws<System.InvalidOperationException>(() =>
            DreamParkPackageCompiler.Compile(SourceId, TargetId));
        StringAssert.Contains("Phone Player slot is invalid", e.Message);
        StringAssert.Contains("PlayerRig", e.Message);
    }

    [Test]
    public void PhoneSlotOutsideTheContentFolderFailsTheCompile()
    {
        CreateRig("Player");
        SaveMinimalAdventure();
        // A prefab that exists but lives outside Assets/Content/{id}/.
        string outside = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/DreamPark" })
            .FirstOrDefault();
        Assume.That(outside, Is.Not.Null);
        PhonePlayerStore.Save(SourceId, outside);

        var e = Assert.Throws<System.InvalidOperationException>(() =>
            DreamParkPackageCompiler.Compile(SourceId, TargetId));
        StringAssert.Contains("is not inside", e.Message);
    }

    [Test]
    public void PhonePlayerStoreRoundTripsAndEmptyWritesNoFile()
    {
        PhonePlayerStore.Save(SourceId, "");
        Assert.That(System.IO.File.Exists(PhonePlayerStore.AssetPath(SourceId)), Is.False,
            "an empty slot is the default and needs no committed file");
        PhonePlayerStore.Save(SourceId, "0123456789abcdef0123456789abcdef");
        Assert.That(PhonePlayerStore.LoadGuid(SourceId), Is.EqualTo("0123456789abcdef0123456789abcdef"));
        Assert.That(PhonePlayerStore.Load(SourceId).schemaVersion, Is.EqualTo(1));
        PhonePlayerStore.Save(SourceId, "");
        Assert.That(PhonePlayerStore.LoadGuid(SourceId), Is.Empty);
        Assert.That(PhonePlayerStore.IsPhonePlayerPrefab(SourceId, Root + "/Phone Player.prefab"), Is.True);
        Assert.That(PhonePlayerStore.IsPhonePlayerPrefab(SourceId, Root + "/Player.prefab"), Is.False);
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

// Catalog packing v9 must always be a profile Web's lib/attractionPacking.js
// accepts: an invalid one is dropped whole, silently. In-memory only — no
// content folder, so no Addressables residue.
public sealed class CatalogPackingUploadTests
{
    private const float Ft = 0.3048f;

    [Test]
    public void FrameNamesAreJsonSafeAndCappedAt200Characters()
    {
        GameObject root = FramedAttraction(out AttractionTemplate attraction,
            Custom("fbig00001", "Big\\Room\t\"A\"", 30f, 20f),
            Custom("flong0001", new string('x', 250), 10f, 10f));
        try
        {
            JSONObject packing = ContentUploaderPanel.BuildPackingUpload(attraction);
            string json = packing.ToString();

            Assert.That(json, Does.Not.Contain("\\R"), "a backslash must never reach JSONObject unescaped");
            Assert.That(json, Does.Not.Contain("\t"));
            List<string> names = new JSONObject(json).GetField("frames").list
                .Select(frame => frame.GetField("name").stringValue).ToList();
            Assert.That(names[0], Is.EqualTo("Big/Room \"A\""));
            Assert.That(names.Single(name => name.StartsWith("xxx")).Length, Is.EqualTo(200));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void LadderIsCappedAtWebsFrameLimitKeepingBaseAndMinimum()
    {
        var frames = new List<AttractionLayoutFrame>();
        for (int i = 0; i < 70; i++)
            frames.Add(Custom("f" + i.ToString("x8"), "Frame " + i, 30f + i, 20f));
        AttractionLayoutFrame minimum = Custom(AttractionTemplate.MinimumFrameId, "Minimum", 4f, 4f);
        minimum.kind = AttractionLayoutFrameKind.Minimum;
        frames.Add(minimum);
        GameObject root = FramedAttraction(out AttractionTemplate attraction, frames.ToArray());
        try
        {
            Assert.That(attraction.GetFrameEnvelopes().Count, Is.EqualTo(72));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("72 layout frames"));

            List<string> ids = ContentUploaderPanel.BuildPackingUpload(attraction).GetField("frames").list
                .Select(frame => frame.GetField("id").stringValue).ToList();

            Assert.That(ids.Count, Is.EqualTo(ContentUploaderPanel.MaxCatalogFrames));
            Assert.That(ids, Does.Contain(AttractionTemplate.BaseFrameId));
            Assert.That(ids.Last(), Is.EqualTo(AttractionTemplate.MinimumFrameId));
            Assert.That(ids[0], Is.EqualTo("f" + 69.ToString("x8")), "the largest custom frames are kept");
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PublishedPackagesSendSchemaThreeOnlyWhenAnArenaEntryPinsAFrame()
    {
        var manifest = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
        try
        {
            Assert.That(ContentUploaderPanel.PublishedSchemaVersionFor(manifest), Is.EqualTo(2));
            manifest.sequence = new PackageRecipe { kind = "sequence" };
            manifest.sequence.occurrences.Add(new PackageOccurrence { occurrenceId = "s1" });
            manifest.arena = new PackageRecipe { kind = "arena" };
            manifest.arena.occurrences.Add(new PackageOccurrence
            {
                occurrenceId = "arena-g", frameId = AttractionTemplate.BaseFrameId,
            });
            manifest.arena.occurrences.Add(new PackageOccurrence { occurrenceId = "arena-p", frameId = "" });
            Assert.That(ContentUploaderPanel.PublishedSchemaVersionFor(manifest), Is.EqualTo(2),
                "Base and prop entries keep the exact v2 document");

            manifest.arena.occurrences.Add(new PackageOccurrence
            {
                occurrenceId = "arena-g-minimum", frameId = AttractionTemplate.MinimumFrameId,
            });
            Assert.That(ContentUploaderPanel.PublishedSchemaVersionFor(manifest),
                Is.EqualTo(ContentUploaderPanel.PublishedPackageSchemaVersion));
        }
        finally
        {
            Object.DestroyImmediate(manifest);
        }
    }

    private static GameObject FramedAttraction(out AttractionTemplate attraction,
        params AttractionLayoutFrame[] frames)
    {
        var root = new GameObject("A_CatalogUpload");
        attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.SetPackingBake(SyntheticBake(new Vector2(20f, 20f) * Ft));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>(frames));
        return root;
    }

    private static AttractionLayoutFrame Custom(string id, string name, float widthFeet, float lengthFeet)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.SetBake(SyntheticBake(frame.SizeMeters));
        return frame;
    }

    private static AttractionPackingBake SyntheticBake(Vector2 authored) =>
        new AttractionPackingBake(authored, authored, authored * 0.5f, authored * 1.25f, authored * 0.5f,
            new List<AttractionPropPackingPose>());
}
#endif
