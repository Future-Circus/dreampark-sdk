#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.IO;
using DreamPark;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class DreamSequenceGeneratorTests
{
    private const string ContentId = "__DreamSequenceGeneratorTests";
    private const string Root = "Assets/Content/" + ContentId;
    private const string OtherContentId = "__DreamSequenceGeneratorTestsOther";
    private const string OtherRoot = "Assets/Content/" + OtherContentId;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", ContentId);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        AssetDatabase.DeleteAsset(OtherRoot);
    }

    [Test]
    public void ScaffoldUsesPlainFloorlessAttractionTemplates()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);

        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.ContainerPrefabPath(ContentId)), Is.Not.Null);

        AssertPlainLevel(DreamSequenceGenerator.StartLevelPath(ContentId));
        AssertPlainLevel(DreamSequenceGenerator.OverlayLevelPath(ContentId));
        AssertPlainLevel(DreamSequenceGenerator.GameOverLevelPath(ContentId));
        AssertBorder(DreamSequenceGenerator.StartLevelPath(ContentId));
        AssertBorder(DreamSequenceGenerator.OverlayLevelPath(ContentId));
        AssertBorder(DreamSequenceGenerator.GameOverLevelPath(ContentId));
    }

    [Test]
    public void DeveloperCanRemoveDefaultBorderWithoutScaffoldRestoringIt()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        string path = DreamSequenceGenerator.StartLevelPath(ContentId);
        GameObject contents = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Transform border = Find(contents.transform, "Sequence Floor Border");
            Assert.That(border, Is.Not.Null);
            Object.DestroyImmediate(border.gameObject);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }

        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.False);
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        GameObject start = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Assert.That(Find(start.transform, "Sequence Floor Border"), Is.Null);
    }

    [Test]
    public void ExistingSpecialLevelsGainPackingFollowerOnceAndRemainCustomizable()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        string[] paths = {
            DreamSequenceGenerator.StartLevelPath(ContentId),
            DreamSequenceGenerator.OverlayLevelPath(ContentId),
            DreamSequenceGenerator.GameOverLevelPath(ContentId)
        };
        foreach (string path in paths)
        {
            SetBorderFollower(path, false);
            if (path == paths[0])
            {
                GameObject start = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    start.GetComponent<AttractionTemplate>().generateFloor = true;
                    PrefabUtility.SaveAsPrefabAsset(start, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(start); }
            }
            AssetImporter importer = AssetImporter.GetAtPath(path);
            importer.userData = importer.userData.Replace(
                "\nDreamSequenceDefaultFollowerV1", string.Empty);
            importer.SaveAndReimport();
        }

        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.True);
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.False);
        foreach (string path in paths)
        {
            AssertBorder(path);
            if (path == paths[0])
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path)
                    .GetComponent<AttractionTemplate>().generateFloor, Is.True);
            SetBorderFollower(path, false);
        }

        DreamSequenceGenerator.EnsureScaffold(ContentId);
        foreach (string path in paths)
        {
            GameObject level = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(Find(level.transform, "Sequence Floor Border")
                .GetComponent<AttractionPackingFollower>().followPacking, Is.False);
        }
    }

    private static void SetBorderFollower(string path, bool followPacking)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(path);
        try
        {
            AttractionPackingFollower follower = Find(contents.transform,
                "Sequence Floor Border").GetComponent<AttractionPackingFollower>();
            follower.followPacking = followPacking;
            PrefabUtility.RecordPrefabInstancePropertyModifications(follower);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
    }

    [Test]
    public void ScaffoldPlacesCenteredStartAndLeftWallHumanScaleNavigation()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.False);

        GameObject start = AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.StartLevelPath(ContentId));
        Transform button = Find(start.transform, "START — Super Adventure Land Button")
            ?? Find(start.transform, "START — Fallback Button");
        Assert.That(button, Is.Not.Null);
        Assert.That(button.localPosition, Is.EqualTo(Vector3.zero));
        Assert.That(AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(button.gameObject)),
            Is.EqualTo(DreamSequenceGenerator.StartButtonPrefabPath));

        GameObject overlay = AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.OverlayLevelPath(ContentId));
        AttractionPackingBake overlayBake = overlay.GetComponent<AttractionTemplate>().PackingBake;
        Assert.That(overlayBake.ShrinkFootprintMeters.x,
            Is.LessThan(overlayBake.AuthoredFootprintMeters.x * 0.5f));
        Transform navigation = Find(overlay.transform, "Default 3D Level Navigation");
        Assert.That(navigation, Is.Not.Null);
        Assert.That(AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(navigation.gameObject)),
            Is.EqualTo(DreamSequenceGenerator.ElevatorControlsPrefabPath));
        Assert.That(navigation.localPosition.x, Is.LessThan(0f));
        Assert.That(navigation.localPosition.y, Is.EqualTo(0f).Within(0.001f));
        Assert.That(Vector3.Dot(navigation.TransformDirection(Vector3.back), Vector3.right),
            Is.GreaterThan(0.99f));
        LuaBehaviour[] controls = navigation.GetComponentsInChildren<LuaBehaviour>();
        Assert.That(controls.Length, Is.EqualTo(2));
        Assert.That(HasAction(controls, "back"), Is.True);
        Assert.That(HasAction(controls, "forward"), Is.True);
        Transform transition = Find(overlay.transform, "Default Level Transition");
        Assert.That(transition, Is.Not.Null);
        Assert.That(AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(transition.gameObject)),
            Is.EqualTo(DreamSequenceGenerator.TransitionEffectPrefabPath));
        ParticleSystem[] particles = transition.GetComponentsInChildren<ParticleSystem>(true);
        Assert.That(particles.Length, Is.EqualTo(2));
        foreach (ParticleSystem particle in particles)
            Assert.That(particle.main.playOnAwake, Is.False);
        Assert.That(transition.GetComponentsInChildren<AudioSource>(true).Length, Is.Zero);
        LuaBehaviour audio = transition.GetComponent<LuaBehaviour>();
        Assert.That(audio, Is.Not.Null);
        Assert.That(audio.audioClipInjections.Length, Is.EqualTo(1));
        Assert.That(audio.audioClipInjections[0].value, Is.Not.Null);
        foreach (string dependency in AssetDatabase.GetDependencies(
                     DreamSequenceGenerator.TransitionEffectPrefabPath, true))
            Assert.That(dependency, Does.Not.Contain("/Content/SuperAdventureLand/"));

        GameObject gameOver = AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.GameOverLevelPath(ContentId));
        Assert.That(gameOver.transform.childCount, Is.EqualTo(1));
        Assert.That(Find(gameOver.transform, "Sequence Floor Border"), Is.Not.Null);
        Assert.That(gameOver.GetComponent<AttractionTemplate>().PackingBake.ShrinkFootprintMeters.x,
            Is.LessThan(0.2f));
    }

    [Test]
    public void ScaffoldRotatesOldOutwardFacingPanelInPlace()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        string path = DreamSequenceGenerator.OverlayLevelPath(ContentId);
        GameObject contents = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Find(contents.transform, "Default 3D Level Navigation").localRotation =
                Quaternion.Euler(0f, 90f, 0f);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }

        // The initial migration fixes legacy navigation, but subsequent
        // developer-authored rotations are not silently corrected.
        AssetImporter importer = AssetImporter.GetAtPath(path);
        importer.userData = importer.userData.Replace("\nDreamSequenceNavigationV1", string.Empty);
        importer.SaveAndReimport();

        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.True);
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        GameObject overlay = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Transform navigation = Find(overlay.transform, "Default 3D Level Navigation");
        Assert.That(AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(navigation.gameObject)),
            Is.EqualTo(DreamSequenceGenerator.ElevatorControlsPrefabPath));
        Assert.That(Vector3.Dot(navigation.TransformDirection(Vector3.back), Vector3.right),
            Is.GreaterThan(0.99f));
        Assert.That(DreamSequenceGenerator.NeedsScaffoldRefresh(ContentId), Is.False);
    }

    [Test]
    public void DeveloperCanRemoveDefaultControlsAfterScaffold()
    {
        DreamSequenceGenerator.EnsureScaffold(ContentId);
        string startPath = DreamSequenceGenerator.StartLevelPath(ContentId);
        string overlayPath = DreamSequenceGenerator.OverlayLevelPath(ContentId);
        GameObject start = PrefabUtility.LoadPrefabContents(startPath);
        try
        {
            Transform button = Find(start.transform, "START — Super Adventure Land Button")
                ?? Find(start.transform, "START — Fallback Button");
            Object.DestroyImmediate(button.gameObject);
            PrefabUtility.SaveAsPrefabAsset(start, startPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(start); }
        GameObject overlay = PrefabUtility.LoadPrefabContents(overlayPath);
        try
        {
            Object.DestroyImmediate(Find(overlay.transform, "Default Level Transition").gameObject);
            PrefabUtility.SaveAsPrefabAsset(overlay, overlayPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(overlay); }

        DreamSequenceGenerator.EnsureScaffold(ContentId);
        Assert.That(Find(AssetDatabase.LoadAssetAtPath<GameObject>(startPath).transform,
            "START — Super Adventure Land Button"), Is.Null);
        Assert.That(Find(AssetDatabase.LoadAssetAtPath<GameObject>(overlayPath).transform,
            "Default Level Transition"), Is.Null);
    }

    [Test]
    public void GameManagerAndItsScriptSurviveDefinitionRecompile()
    {
        string managerPath = DreamSequenceGenerator.EnsureContainer(ContentId);
        GameObject contents = PrefabUtility.LoadPrefabContents(managerPath);
        try
        {
            new GameObject("Custom Score Manager").transform.SetParent(contents.transform, false);
            PrefabUtility.SaveAsPrefabAsset(contents, managerPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        string scriptPath = Root + "/Scripts/game-container.lua.txt";
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", scriptPath));
        File.WriteAllText(fullPath, "function next_level() -- creator policy\nend\n");
        AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate);

        DreamSequencePackageCompiler.Compile(ContentId);
        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);

        Assert.That(definition.gameManagerPrefab,
            Is.EqualTo(AssetDatabase.LoadAssetAtPath<GameObject>(managerPath)));
        Assert.That(Find(definition.gameManagerPrefab.transform, "Custom Score Manager"), Is.Not.Null);
        Assert.That(File.ReadAllText(fullPath), Does.Contain("creator policy"));
        Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
            DreamSequenceGenerator.SequencePrefabPath(ContentId)), Is.Null);
    }

    [Test]
    public void DefinitionKeepsRepeatedAttractionsAsAddressesWithoutEmbeddingPrefabs()
    {
        string attractionPath = Root + "/Reusable Attraction.prefab";
        var authored = new GameObject("Reusable Attraction");
        try
        {
            var attraction = authored.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(10f, 10f);
            PrefabUtility.SaveAsPrefabAsset(authored, attractionPath);
        }
        finally { Object.DestroyImmediate(authored); }
        string guid = AssetDatabase.AssetPathToGUID(attractionPath);
        var layout = new ContentSequenceStore.Data { hasExplicitEndpoints = true };
        layout.items.Add(new ContentSequenceStore.Entry { kind = "attraction", id = "one", attractionGuid = guid });
        layout.items.Add(new ContentSequenceStore.Entry { kind = "attraction", id = "two", attractionGuid = guid });
        ContentSequenceStore.Save(ContentId, layout, true);

        DreamSequencePackageDefinition definition = DreamSequencePackageCompiler.Compile(ContentId);

        Assert.That(definition.levels.Count, Is.EqualTo(2));
        Assert.That(definition.levels[0].sourceGuid, Is.EqualTo(guid));
        Assert.That(definition.levels[1].sourceGuid, Is.EqualTo(guid));
        Assert.That(AssetDatabase.GetDependencies(DreamSequencePackageCompiler.DefinitionPath(ContentId), true),
            Does.Not.Contain(attractionPath));
    }

    [Test]
    public void EachTitleGetsItsOwnDefinitionAndEditableParts()
    {
        if (!AssetDatabase.IsValidFolder(OtherRoot))
            AssetDatabase.CreateFolder("Assets/Content", OtherContentId);

        DreamSequencePackageDefinition first = DreamSequencePackageCompiler.Compile(ContentId);
        DreamSequencePackageDefinition second = DreamSequencePackageCompiler.Compile(OtherContentId);

        Assert.That(first, Is.Not.SameAs(second));
        Assert.That(DreamSequencePackageCompiler.DefinitionPath(ContentId),
            Is.Not.EqualTo(DreamSequencePackageCompiler.DefinitionPath(OtherContentId)));
        Assert.That(AssetDatabase.GetAssetPath(first.startLevelPrefab),
            Is.EqualTo(DreamSequenceGenerator.StartLevelPath(ContentId)));
        Assert.That(AssetDatabase.GetAssetPath(second.startLevelPrefab),
            Is.EqualTo(DreamSequenceGenerator.StartLevelPath(OtherContentId)));
    }

    private static bool HasAction(LuaBehaviour[] controls, string action)
    {
        foreach (LuaBehaviour control in controls)
            foreach (StringInjection injection in control.stringInjections)
                if (injection.name == "action" && injection.value == action) return true;
        return false;
    }

    private static void AssertBorder(string path)
    {
        GameObject level = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Transform border = Find(level.transform, "Sequence Floor Border");
        Assert.That(border, Is.Not.Null);
        Assert.That(AssetDatabase.GetAssetPath(
                PrefabUtility.GetCorrespondingObjectFromOriginalSource(border.gameObject)),
            Is.EqualTo(DreamSequenceGenerator.BorderPrefabPath));
        FloorAnchor anchor = border.GetComponent<FloorAnchor>();
        Assert.That(anchor, Is.Not.Null);
        Assert.That(anchor.autoFindFloor && anchor.matchGrade, Is.True);
        AttractionPackingFollower follower = border.GetComponent<AttractionPackingFollower>();
        Assert.That(follower, Is.Not.Null);
        Assert.That(follower.followPacking, Is.True);
        Assert.That(follower.scaleMode, Is.EqualTo(AttractionPackingScaleMode.XZIndependent));
        AttractionTemplate attraction = level.GetComponent<AttractionTemplate>();
        Assert.That(attraction.HasPackingBake, Is.True);
        Assert.That(attraction.PackingBake.Followers.Count, Is.EqualTo(1));
        Assert.That(anchor.HasPrecalculatedBounds(), Is.True);
        Vector3[] corners = anchor.GetPrecalculatedCornersWorld();
        Assert.That(Vector3.Distance(corners[0], corners[1]),
            Is.EqualTo(12f * 0.3048f).Within(0.001f));
        Assert.That(Vector3.Distance(corners[1], corners[2]),
            Is.EqualTo(18f * 0.3048f).Within(0.001f));
        Assert.That(border.GetComponent<Collider>(), Is.Null);
        Assert.That(border.GetComponent<MeshRenderer>().sharedMaterial.GetTexture("_baseTex"), Is.Not.Null);
        Assert.That(border.localScale.x * 10f, Is.EqualTo(12f * 0.3048f).Within(0.001f));
        Assert.That(border.localScale.z * 10f, Is.EqualTo(18f * 0.3048f).Within(0.001f));
    }

    private static void AssertPlainLevel(string path)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Assert.That(prefab, Is.Not.Null);
        AttractionTemplate attraction = prefab.GetComponent<AttractionTemplate>();
        Assert.That(attraction, Is.Not.Null);
        Assert.That(prefab.GetComponent<DreamSequenceSpecialLevelTemplate>(), Is.Null);
        Assert.That(attraction.size, Is.EqualTo(GameLevelSize.Custom));
        Assert.That(attraction.customSize, Is.EqualTo(new Vector2(12f, 18f)));
        Assert.That(attraction.generateFloor, Is.False);
    }

    private static Transform Find(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform match = Find(root.GetChild(i), name);
            if (match != null) return match;
        }
        return null;
    }
}
#endif
