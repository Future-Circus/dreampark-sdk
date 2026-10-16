using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Oct 10 2026: a Zombiez beta publish held the main thread for over an hour because
// every save during the publish re-ran the full packing bake through the save hook,
// on top of the publish's own BakeAllInContent. Automated commands never bake from a save.
public sealed class AttractionPackingSaveHookTests
{
    private const string ContentId = "PackingSaveHookTest";
    private const string Root = "Assets/Content/" + ContentId;
    private const string PrefabPath = Root + "/A_SaveHook.prefab";

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", ContentId);
    }

    [TearDown]
    public void TearDown()
    {
        AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = null;
        AssetDatabase.DeleteAsset(Root);
    }

    [Test]
    public void AutomationIsOnDuringAnAutomationScope()
    {
        using (DreamParkAutomation.Begin())
            Assert.That(AttractionPackingPrefabSaveProcessor.SkipBakeForAutomation(), Is.True);
    }

    [Test]
    public void SaveUnderAutomation_DoesNotBake_InteractiveSaveDoes()
    {
        SaveUnbakedAttraction();
        AttractionTemplate asset = LoadAttraction();
        Assert.That(asset.HasPackingBake, Is.False, "precondition: the prefab is saved without a bake");

        AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = true;
        AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { PrefabPath });
        Assert.That(LoadAttraction().HasPackingBake, Is.False, "an automated save must not bake");
        Assert.That(AttractionPackingBaker.AutoBakeInProgress, Is.False);

        AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = false;
        AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { PrefabPath });
        Assert.That(LoadAttraction().HasPackingBake, Is.True, "the author's own save still bakes");
        Assert.That(AttractionPackingBaker.AutoBakeInProgress, Is.False);
    }

    [Test]
    public void GuiPublishInProgress_DoesNotBake_UntilItEnds()
    {
        SaveUnbakedAttraction();
        AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = false;   // an interactive editor
        var panel = new GameObject("PublishOwner");
        var abandoned = new GameObject("AbandonedOwner");
        try
        {
            AttractionPackingPrefabSaveProcessor.SetPublishing(panel, true);
            Assert.That(AttractionPackingPrefabSaveProcessor.PublishInProgress, Is.True);
            AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { PrefabPath });
            Assert.That(LoadAttraction().HasPackingBake, Is.False, "a save during a GUI publish must not bake");

            AttractionPackingPrefabSaveProcessor.SetPublishing(panel, false);
            // An owner destroyed mid-publish (window closed) never holds the hook off.
            AttractionPackingPrefabSaveProcessor.SetPublishing(abandoned, true);
            Object.DestroyImmediate(abandoned);
            Assert.That(AttractionPackingPrefabSaveProcessor.PublishInProgress, Is.False);

            AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { PrefabPath });
            Assert.That(LoadAttraction().HasPackingBake, Is.True, "a Prefab Mode save outside a publish still bakes");
        }
        finally
        {
            AttractionPackingPrefabSaveProcessor.SetPublishing(panel, false);
            Object.DestroyImmediate(panel);
        }
    }

    private static void SaveUnbakedAttraction()
    {
        var root = new GameObject("A_SaveHook");
        try
        {
            AttractionTemplate attraction = root.AddComponent<AttractionTemplate>();
            attraction.size = GameLevelSize.Custom;
            attraction.customSize = new Vector2(20f, 20f);
            attraction.safeArea = 0f;
            AddProp(root.transform, "Left", -1f);
            AddProp(root.transform, "Right", 1f);
            // The real hook runs during this save; keep it from baking whatever mode the
            // test runner is in, so the asset starts unbaked.
            AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = true;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            AttractionPackingPrefabSaveProcessor.AutomationOverrideForTests = null;
            Object.DestroyImmediate(root);
        }
    }

    private static void AddProp(Transform parent, string name, float x)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
        PropTemplate prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
    }

    private static AttractionTemplate LoadAttraction() =>
        AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<AttractionTemplate>();
}
