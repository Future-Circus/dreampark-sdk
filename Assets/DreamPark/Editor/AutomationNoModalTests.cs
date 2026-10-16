#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DreamPark;
using DreamPark.PreUploadChecks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Release night, Oct 8 2026: dreampark_content_publish hung ArcadeLand's Editor behind modals twice
// (a Save-As panel for an Untitled scene from the strict preflight's SaveOpenScenes, then Addressables'
// "Unsaved Scenes" dialog after the Android platform switch). Owner, 2026-10-09: no modal on any
// automated publish/preflight path. Each test below sets up the exact editor state that raised a
// dialog on the old path and runs the new path, which must finish without one. Runs in core too
// (RunSuites): nothing here touches a scene it did not create, and nothing outside its temp folder is
// saved.
public sealed class AutomationNoModalTests
{
    private const string TempRoot = "Assets/__DreamParkNoModalTests";
    private const string PrefabPath = TempRoot + "/NoModalBox.prefab";
    private const string ScenePath = TempRoot + "/NoModalDirtyOnLoad.unity";

    private Scene untitled;

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.sceneOpened -= DirtyOnLoad;
        if (untitled.IsValid() && untitled.isLoaded) EditorSceneManager.CloseScene(untitled, true);
        untitled = default;
        Scene fixture = SceneManager.GetSceneByPath(ScenePath);
        if (fixture.IsValid()) EditorSceneManager.CloseScene(fixture, true);
        // The test's own fixture folder only.
        if (AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.DeleteAsset(TempRoot);
    }

    // ---- Untitled scene: the old preflight called SaveOpenScenes() -> Save-As panel ----

    [Test]
    public void UntitledDirtySceneIsReportedAndNeverSaved()
    {
        untitled = NewDirtyUntitledScene();
        Assert.That(DreamParkAutomation.IsUntitled(untitled), Is.True);
        Assert.That(untitled.isDirty, Is.True, "precondition: the state in which SaveOpenScenes opens a Save-As panel");

        var report = DreamParkAutomation.SaveDirtyScenesWithoutPrompt(TempRoot);

        Assert.That(report.Ok, Is.True, report.Summary());
        Assert.That(report.untitled.Count, Is.EqualTo(1), report.Summary());
        Assert.That(report.saved, Is.Empty, "nothing outside the temp scope may be saved");
        Assert.That(untitled.isDirty, Is.True, "the Untitled scene is left exactly as it was");
        StringAssert.Contains("Untitled", report.Summary());
    }

    [Test]
    public void ScenesOutsideTheContentFolderAreLeftAlone()
    {
        // Every loaded scene that is dirty and outside the scope is reported, never saved.
        var before = DreamParkAutomation.LoadedDirtyScenes().Select(s => s.path).ToList();
        var report = DreamParkAutomation.SaveDirtyScenesWithoutPrompt(TempRoot);
        Assert.That(report.saved, Is.Empty);
        Assert.That(DreamParkAutomation.LoadedDirtyScenes().Select(s => s.path).ToList(), Is.EqualTo(before));
    }

    // ---- A scene that dirties itself on load: read it, close it, never prompt or save ----

    [Test]
    public void SceneThatDirtiesOnLoadIsReadAndClosedWithoutSavingOrPrompting()
    {
        BuildDirtyOnLoadFixture();
        string hashBefore = Hash(ScenePath);
        Scene activeBefore = SceneManager.GetActiveScene();
        int countBefore = SceneManager.sceneCount;

        EditorSceneManager.sceneOpened += DirtyOnLoad;
        var lease = DreamParkAutomation.OpenSceneForReading(ScenePath);
        try
        {
            Assert.That(lease.openedHere, Is.True);
            Assert.That(lease.dirtiedOnLoad, Is.True, "precondition: the scene marked itself dirty while loading");
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(activeBefore), "additive open keeps the active scene");

            GameObject instance = lease.scene.GetRootGameObjects().Single(g => g.name == "NoModalBox");
            PropertyModification[] inMemory = PrefabUtility.GetPropertyModifications(instance);
            Assert.That(inMemory.Any(m => m.propertyPath.StartsWith("m_Size", StringComparison.Ordinal)), Is.True,
                "precondition: the load-time churn shows up as an override in memory");

            var onDisk = SceneYamlModifications.Read(ScenePath);
            Assert.That(onDisk, Is.Not.Null);
            Assert.That(onDisk.ContainsKey(SceneYamlModifications.PrefabInstanceFileId(instance)), Is.True,
                "the open instance maps to its PrefabInstance block in the YAML");

            PropertyModification[] real = SceneYamlModifications.WithoutLoadChurn(instance, inMemory, onDisk);
            Assert.That(real.Any(m => m.propertyPath == "m_IsTrigger"), Is.True, "the saved override is still reported");
            Assert.That(real.Any(m => m.propertyPath.StartsWith("m_Size", StringComparison.Ordinal)), Is.False,
                "the load-time churn is not reported as an unapplied override");
        }
        finally
        {
            EditorSceneManager.sceneOpened -= DirtyOnLoad;
            Assert.That(DreamParkAutomation.CloseSceneWithoutSaving(lease), Is.True);
        }

        Assert.That(SceneManager.GetSceneByPath(ScenePath).isLoaded, Is.False);
        Assert.That(SceneManager.sceneCount, Is.EqualTo(countBefore));
        Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(activeBefore));
        Assert.That(Hash(ScenePath), Is.EqualTo(hashBefore), "closing discards the churn; the file is untouched");
    }

    [Test]
    public void AnAlreadyOpenSceneIsReadInPlaceAndNotClosed()
    {
        Scene active = SceneManager.GetActiveScene();
        if (DreamParkAutomation.IsUntitled(active)) Assert.Ignore("needs a saved active scene");
        bool dirtyBefore = active.isDirty;

        var lease = DreamParkAutomation.OpenSceneForReading(active.path);
        Assert.That(lease.openedHere, Is.False);
        Assert.That(DreamParkAutomation.CloseSceneWithoutSaving(lease), Is.True);
        Assert.That(active.isLoaded, Is.True);
        Assert.That(active.isDirty, Is.EqualTo(dirtyBefore));
    }

    // ---- Addressables' "Unsaved Scenes" dialog after the platform switch ----

    [Test]
    public void AddressablesUnsavedScenesPromptIsNotReachedDuringAutomation()
    {
        var dirtyBefore = DreamParkAutomation.LoadedDirtyScenes().Select(DreamParkAutomation.Label).ToList();
        untitled = NewDirtyUntitledScene();
        Assert.That(AnySceneDirtyAsAddressablesSeesIt(), Is.True,
            "precondition: BuildUtility.CheckModifiedScenesAndAskToSave would show its modal now");

        using (var suspension = DreamParkAutomation.SuspendSceneDirtiness())
        {
            Assert.That(suspension.Error, Is.Null, suspension.Error);
            // Throws (and ends the test) before Addressables' own check is called if anything is
            // still dirty, so this test can never raise the dialog itself.
            Assert.That(AnySceneDirtyAsAddressablesSeesIt(), Is.False);
            Assert.That(BuildUtility.CheckModifiedScenesAndAskToSave(), Is.True,
                "Addressables' own pre-build scene check passes without a dialog");
        }

        Assert.That(untitled.isDirty, Is.True, "the unsaved flag is put back; nothing was saved or discarded");
        var dirtyAfter = DreamParkAutomation.LoadedDirtyScenes().Select(DreamParkAutomation.Label).ToList();
        Assert.That(dirtyAfter.Except(new[] { DreamParkAutomation.Label(untitled) }).ToList(), Is.EqualTo(dirtyBefore),
            "every other scene's unsaved flag is exactly as it was");
    }

    // ---- Scope and plumbing ----

    [Test]
    public void AutomationScopeNestsAndEnds()
    {
        bool baseline = DreamParkAutomation.IsActive;
        using (DreamParkAutomation.Begin())
        {
            Assert.That(DreamParkAutomation.IsActive, Is.True);
            using (DreamParkAutomation.Begin()) Assert.That(DreamParkAutomation.IsActive, Is.True);
            Assert.That(DreamParkAutomation.IsActive, Is.True);
        }
        Assert.That(DreamParkAutomation.IsActive, Is.EqualTo(baseline));
    }

    [Test]
    public void SceneOverridesReadsOnlyThisContentFoldersOwnScenes()
    {
        const string root = "Assets/Content/ArcadeLand";
        Assert.That(PreUploadScopes.IsSceneOverridesScope("Assets/Content/ArcadeLand/SpaceInvaders3D/Scenes/SpaceInvaders3DTest.unity", root), Is.True);
        Assert.That(PreUploadScopes.IsSceneOverridesScope("Assets/Content/ArcadeLand/ThirdParty/Pack/Demo.unity", root), Is.False);
        Assert.That(PreUploadScopes.IsSceneOverridesScope("Assets/Content/ArcadeLandBeta/Scenes/X.unity", root), Is.False);
        Assert.That(PreUploadScopes.IsSceneOverridesScope("Assets/TestScene.unity", root), Is.False);
        Assert.That(PreUploadScopes.IsSceneOverridesScope("Assets/Scenes/Main.unity", root), Is.False);
        Assert.That(PreUploadScopes.IsSceneOverridesScope("", root), Is.False);
    }

    [Test]
    public void SwitchingToTheActiveTargetIsANoOp()
    {
        BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
        BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(active);
        Assert.That(DreamParkAutomation.TrySwitchBuildTarget(group, active, out string error), Is.True, error);
        Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(active));
    }

    // ---- fixtures ----

    // The exact test BuildUtility.CheckModifiedScenesAndAskToSave makes before its DisplayDialog
    // (and SBP's internal ValidationMethods.HasDirtyScenes before it returns UnsavedChanges).
    private static bool AnySceneDirtyAsAddressablesSeesIt()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty) return true;
        return false;
    }

    private static Scene NewDirtyUntitledScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var go = new GameObject("NoModalUnsavedWork");
        SceneManager.MoveGameObjectToScene(go, scene);
        EditorSceneManager.MarkSceneDirty(scene);
        return scene;
    }

    private static void BuildDirtyOnLoadFixture()
    {
        if (!AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.CreateFolder("Assets", "__DreamParkNoModalTests");

        var source = new GameObject("NoModalBox");
        new GameObject("Child").transform.SetParent(source.transform, false);
        source.transform.GetChild(0).gameObject.AddComponent<BoxCollider>();
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
        UnityEngine.Object.DestroyImmediate(source);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var collider = instance.GetComponentInChildren<BoxCollider>();
        collider.isTrigger = true;                                    // a real, saved override
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
        Assert.That(EditorSceneManager.SaveScene(scene, ScenePath), Is.True);
        EditorSceneManager.CloseScene(scene, true);
    }

    // Stands in for Modular 3D Text's TextUpdater: a script that rewrites a prefab-instance
    // property every time the scene loads, which leaves the scene dirty.
    private static void DirtyOnLoad(Scene scene, OpenSceneMode mode)
    {
        if (scene.path != ScenePath) return;
        GameObject instance = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "NoModalBox");
        if (instance == null) return;
        var collider = instance.GetComponentInChildren<BoxCollider>();
        collider.size = new Vector3(2f, 3f, 4f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static string Hash(string assetPath)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.GetFullPath(assetPath))));
    }
}
#endif
