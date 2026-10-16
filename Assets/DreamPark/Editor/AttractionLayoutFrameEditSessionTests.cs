using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class AttractionLayoutFrameEditSessionTests
{
    private Scene _scene;
    private GameObject _root;
    private GameObject _contentsRoot;
    private string _temporaryPrefabPath;

    [SetUp]
    public void SetUp()
    {
        AttractionLayoutFrameEditSession.ResetForTests();
        // A preview scene keeps these objects (and their Undo records) out of the open scene.
        _scene = EditorSceneManager.NewPreviewScene();
    }

    [TearDown]
    public void TearDown()
    {
        AttractionLayoutFrameEditSession.ResetForTests();
        if (_contentsRoot != null) PrefabUtility.UnloadPrefabContents(_contentsRoot);
        if (_root != null) Object.DestroyImmediate(_root);
        if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
        if (!string.IsNullOrEmpty(_temporaryPrefabPath))
            AssetDatabase.DeleteAsset(_temporaryPrefabPath);
    }

    [Test]
    public void Commit_StoresOnlyChangedProperties()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate moved = CreateProp("Moved", -3f);
        PropTemplate rotated = CreateProp("Rotated", -1f);
        PropTemplate nudged = CreateProp("Nudged", 1f);
        PropTemplate hidden = CreateProp("Hidden", 3f);
        var child = new GameObject("Child");
        child.transform.SetParent(moved.transform, false);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { Frame("fbig00001", "Big", 30f, 20f) });

        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fbig00001"), Is.True);
        Assert.That(AttractionLayoutFrameEditSession.IsEditing(attraction), Is.True);
        moved.transform.localPosition = new Vector3(-4f, 0f, 0.5f);
        rotated.transform.localRotation = Quaternion.Euler(0f, 0.05f, 0f);
        nudged.transform.localPosition += new Vector3(0.00005f, 0f, 0f);
        nudged.transform.localScale = new Vector3(1.00005f, 1f, 1f);
        nudged.transform.localRotation = Quaternion.Euler(0f, 0.005f, 0f);
        hidden.gameObject.SetActive(false);

        AttractionLayoutFrameEditSession.Result result = AttractionLayoutFrameEditSession.Commit(false);

        Assert.That(result.success, Is.True, result.message);
        Assert.That(result.changedCount, Is.EqualTo(3));
        Assert.That(AttractionLayoutFrameEditSession.IsActive, Is.False);
        AttractionLayoutFrame frame = attraction.FindFrame("fbig00001");
        Assert.That(frame.overrides.Count, Is.EqualTo(3), "Sub-epsilon nudges and untouched children are not stored.");

        AttractionLayoutOverride move = Find(frame, "Moved");
        Assert.That(move.target, Is.SameAs(moved.transform));
        Assert.That(move.setPosition, Is.True);
        Assert.That(move.localPosition, Is.EqualTo(new Vector3(-4f, 0f, 0.5f)));
        Assert.That(move.setActive || move.setRotation || move.setScale, Is.False);

        AttractionLayoutOverride turn = Find(frame, "Rotated");
        Assert.That(turn.setRotation, Is.True);
        Assert.That(turn.setActive || turn.setPosition || turn.setScale, Is.False);
        Assert.That(AttractionLayoutFrameEditSession.AngleDegrees(Quaternion.identity, turn.localRotation),
            Is.EqualTo(0.05f).Within(0.001f));

        AttractionLayoutOverride hide = Find(frame, "Hidden");
        Assert.That(hide.setActive, Is.True);
        Assert.That(hide.active, Is.False);
        Assert.That(hide.setPosition || hide.setRotation || hide.setScale, Is.False);
        Assert.That(frame.overrides.Exists(o => o.path == "Nudged" || o.path == "Moved/Child"), Is.False);

        // Base is back, exactly.
        Assert.That(moved.transform.localPosition, Is.EqualTo(new Vector3(-3f, 0f, 0f)));
        Assert.That(rotated.transform.localRotation, Is.EqualTo(Quaternion.identity));
        Assert.That(nudged.transform.localPosition.x, Is.EqualTo(1f));
        Assert.That(nudged.transform.localScale, Is.EqualTo(Vector3.one));
        Assert.That(hidden.gameObject.activeSelf, Is.True);
        Assert.That(attraction.FrameHasValidBake(frame), Is.True, "Saving a frame re-bakes it.");
    }

    [Test]
    public void Commit_CreatedObjectIsInactiveInBaseAndShownByTheFrame()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { Frame("fbig00001", "Big", 30f, 20f) });

        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fbig00001"), Is.True);
        PropTemplate extra = CreateProp("Extra", 4f);
        var glow = new GameObject("Glow");
        glow.transform.SetParent(extra.transform, false);

        AttractionLayoutFrameEditSession.Result result = AttractionLayoutFrameEditSession.Commit(false);

        Assert.That(result.success, Is.True, result.message);
        Assert.That(result.createdCount, Is.EqualTo(1));
        AttractionLayoutFrame frame = attraction.FindFrame("fbig00001");
        Assert.That(frame.overrides.Count, Is.EqualTo(1), "Only the top-most new object gets an entry.");
        AttractionLayoutOverride show = frame.overrides[0];
        Assert.That(show.path, Is.EqualTo("Extra"));
        Assert.That(show.target, Is.SameAs(extra.transform));
        Assert.That(show.setActive, Is.True);
        Assert.That(show.active, Is.True);
        Assert.That(show.setPosition || show.setRotation || show.setScale, Is.False);

        Assert.That(extra != null, Is.True, "Created objects stay in the prefab.");
        Assert.That(extra.gameObject.activeSelf, Is.False, "...turned off in Base.");
        Assert.That(glow.activeSelf, Is.True);
        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(1));
        Assert.That(frame.Bake.Props.Count, Is.EqualTo(2));

        Assert.That(attraction.ApplyLayout("fbig00001", Vector2.one), Is.True);
        Assert.That(extra.gameObject.activeSelf, Is.True);
        Assert.That(attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one), Is.True);
        Assert.That(extra.gameObject.activeSelf, Is.False);
        Assert.That(left.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void Cancel_RestoresBaseAndTheFramesOriginalOverrides()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f, Hide(right.transform), Move(left.transform, Vector3.zero)),
        });

        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fsmall001"), Is.True);
        Assert.That(right.gameObject.activeSelf, Is.False, "Begin applies the frame.");
        Assert.That(left.transform.localPosition, Is.EqualTo(Vector3.zero));

        left.transform.localPosition = new Vector3(0.5f, 0f, 2f);
        right.gameObject.SetActive(true);
        var temporary = new GameObject("Temporary");
        temporary.transform.SetParent(_root.transform, false);

        AttractionLayoutFrameEditSession.Result result = AttractionLayoutFrameEditSession.Cancel();

        Assert.That(result.success, Is.True, result.message);
        Assert.That(AttractionLayoutFrameEditSession.IsActive, Is.False);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(right.gameObject.activeSelf, Is.True);
        Assert.That(temporary == null, Is.True, "Objects created during a cancelled edit are removed.");
        List<AttractionLayoutOverride> overrides = attraction.FindFrame("fsmall001").overrides;
        Assert.That(overrides.Count, Is.EqualTo(2));
        Assert.That(overrides[0].target, Is.SameAs(right.transform));
        Assert.That(overrides[0].setActive && !overrides[0].active, Is.True);
        Assert.That(overrides[1].target, Is.SameAs(left.transform));
        Assert.That(overrides[1].localPosition, Is.EqualTo(Vector3.zero));
    }

    [Test]
    public void Commit_IsBlockedByADeletedObject()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { Frame("fbig00001", "Big", 30f, 20f) });

        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fbig00001"), Is.True);
        left.transform.localPosition = new Vector3(-2f, 0f, 0f);
        Object.DestroyImmediate(right.gameObject);

        AttractionLayoutFrameEditSession.Result result = AttractionLayoutFrameEditSession.Commit(false);

        Assert.That(result.success, Is.False);
        Assert.That(result.deletedPaths, Is.EqualTo(new[] { "Right" }));
        Assert.That(result.message, Does.Contain("Undo"));
        Assert.That(AttractionLayoutFrameEditSession.IsEditing(attraction), Is.True, "The session stays open.");
        Assert.That(attraction.FindFrame("fbig00001").overrides, Is.Empty, "Nothing was committed.");
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-2f, 0f, 0f)));

        Assert.That(AttractionLayoutFrameEditSession.Cancel().success, Is.True);
        Assert.That(left.transform.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
    }

    [Test]
    public void SaveGuard_WritesTheFrameAndNeverSavesFrameStateAsBase()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate left = CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f, Hide(right.transform)),
        });
        _temporaryPrefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/__LayoutFrameSaveGuardTest.prefab");
        PrefabUtility.SaveAsPrefabAsset(_root, _temporaryPrefabPath);
        Object.DestroyImmediate(_root);

        _contentsRoot = PrefabUtility.LoadPrefabContents(_temporaryPrefabPath);
        attraction = _contentsRoot.GetComponent<AttractionTemplate>();
        Transform liveLeft = _contentsRoot.transform.Find("Left");
        Transform liveRight = _contentsRoot.transform.Find("Right");
        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fsmall001", _temporaryPrefabPath, out string error),
            Is.True, error);
        Assert.That(liveRight.gameObject.activeSelf, Is.False);
        liveLeft.localPosition = new Vector3(0.25f, 0f, 0f);
        var extra = new GameObject("Extra");
        extra.transform.SetParent(_contentsRoot.transform, false);

        AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { "Assets/Unrelated.prefab" });
        Assert.That(AttractionLayoutFrameEditSession.IsSuspended, Is.False, "Other saves are ignored.");

        AttractionPackingPrefabSaveProcessor.ProcessWillSaveForTests(new[] { _temporaryPrefabPath });

        // Before anything is written, the hierarchy is Base and the frame holds the edits.
        Assert.That(AttractionLayoutFrameEditSession.IsSuspended, Is.True);
        Assert.That(AttractionLayoutFrameEditSession.IsEditing(attraction), Is.True);
        Assert.That(liveRight.gameObject.activeSelf, Is.True);
        Assert.That(liveLeft.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(extra.activeSelf, Is.False);
        AttractionLayoutFrame frame = attraction.FindFrame("fsmall001");
        Assert.That(frame.overrides.Count, Is.EqualTo(3));
        Assert.That(Find(frame, "Right").active, Is.False);
        Assert.That(Find(frame, "Left").localPosition, Is.EqualTo(new Vector3(0.25f, 0f, 0f)));
        Assert.That(Find(frame, "Extra").active, Is.True);

        PrefabUtility.SaveAsPrefabAsset(_contentsRoot, _temporaryPrefabPath);

        GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(_temporaryPrefabPath);
        Assert.That(saved.transform.Find("Right").gameObject.activeSelf, Is.True);
        Assert.That(saved.transform.Find("Left").localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(saved.transform.Find("Extra").gameObject.activeSelf, Is.False);
        AttractionLayoutFrame savedFrame = saved.GetComponent<AttractionTemplate>().FindFrame("fsmall001");
        Assert.That(savedFrame.overrides.Count, Is.EqualTo(3));
        Assert.That(Find(savedFrame, "Left").target, Is.SameAs(saved.transform.Find("Left")));

        // After the save the session carries on exactly where it was.
        AttractionLayoutFrameEditSession.ResumeAfterSave();
        Assert.That(AttractionLayoutFrameEditSession.IsSuspended, Is.False);
        Assert.That(liveRight.gameObject.activeSelf, Is.False);
        Assert.That(liveLeft.localPosition, Is.EqualTo(new Vector3(0.25f, 0f, 0f)));
        Assert.That(extra.activeSelf, Is.True);

        Assert.That(AttractionLayoutFrameEditSession.Cancel().success, Is.True);
        Assert.That(liveLeft.localPosition, Is.EqualTo(new Vector3(-1f, 0f, 0f)));
        Assert.That(liveRight.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void UndoPastBegin_EndsTheSessionAtBase()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);
        PropTemplate right = CreateProp("Right", 1f);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fsmall001", "Small", 10f, 10f, Hide(right.transform)),
        });

        Assert.That(AttractionLayoutFrameEditSession.Begin(attraction, "fsmall001"), Is.True);
        Assert.That(right.gameObject.activeSelf, Is.False);
        Undo.PerformUndo();

        Assert.That(AttractionLayoutFrameEditSession.IsActive, Is.False);
        Assert.That(right.gameObject.activeSelf, Is.True);
    }

    [Test]
    public void FrameListEdits_AreUndoable()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Left", -1f);

        Undo.IncrementCurrentGroup();
        AttractionLayoutFrame added = AttractionLayoutFrameAuthoring.AddFrame(attraction, new Vector2(30f, 20f));
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(1));
        Assert.That(AttractionTemplate.IsValidFrameId(added.id), Is.True);
        Assert.That(added.id, Does.StartWith("f").And.Length.EqualTo(9));
        Assert.That(added.kind, Is.EqualTo(AttractionLayoutFrameKind.Custom));
        Undo.PerformUndo();
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(0));
        Undo.PerformRedo();
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(1));

        Undo.IncrementCurrentGroup();
        AttractionLayoutFrame minimum = AttractionLayoutFrameAuthoring.CreateMinimumFrame(attraction);
        Assert.That(minimum.id, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(minimum.sizeFeet, Is.EqualTo(new Vector2(20f, 20f)));
        Assert.That(minimum.overrides, Is.Empty);

        string id = added.id;
        Undo.IncrementCurrentGroup();
        Assert.That(AttractionLayoutFrameAuthoring.RenameFrame(attraction, id, "Huge"), Is.True);
        Undo.IncrementCurrentGroup();
        Assert.That(AttractionLayoutFrameAuthoring.ResizeFrame(attraction, id, new Vector2(40f, 20f)), Is.True);
        Undo.IncrementCurrentGroup();
        AttractionLayoutFrame copy = AttractionLayoutFrameAuthoring.DuplicateFrame(attraction, id);
        Assert.That(copy.id, Is.Not.EqualTo(id));
        Assert.That(copy.name, Is.EqualTo("Huge copy"));
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(3));
        Undo.IncrementCurrentGroup();
        Assert.That(AttractionLayoutFrameAuthoring.DeleteFrame(attraction, id), Is.True);
        Assert.That(attraction.FindFrame(id), Is.Null);

        Undo.PerformUndo();
        Assert.That(attraction.FindFrame(id), Is.Not.Null, "Delete undone.");
        Undo.PerformUndo();
        Assert.That(attraction.LayoutFrames.Count, Is.EqualTo(2), "Duplicate undone.");
        Undo.PerformUndo();
        Assert.That(attraction.FindFrame(id).sizeFeet, Is.EqualTo(new Vector2(30f, 20f)), "Resize undone.");
        Undo.PerformUndo();
        Assert.That(attraction.FindFrame(id).name, Is.Not.EqualTo("Huge"), "Rename undone.");
    }

    private static AttractionLayoutOverride Find(AttractionLayoutFrame frame, string path)
    {
        AttractionLayoutOverride found = frame.overrides.Find(o => o.path == path);
        Assert.That(found, Is.Not.Null, $"No override for '{path}'.");
        return found;
    }

    private static AttractionLayoutFrame Frame(string id, string name, float widthFeet, float lengthFeet,
        params AttractionLayoutOverride[] overrides)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.overrides.AddRange(overrides);
        return frame;
    }

    private AttractionLayoutOverride Hide(Transform target) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setActive = true,
        active = false,
    };

    private AttractionLayoutOverride Move(Transform target, Vector3 localPosition) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setPosition = true,
        localPosition = localPosition,
    };

    private AttractionTemplate CreateAttraction()
    {
        _root = new GameObject("Attraction");
        SceneManager.MoveGameObjectToScene(_root, _scene);
        AttractionTemplate attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        return attraction;
    }

    private PropTemplate CreateProp(string name, float x)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, 0f);
        PropTemplate prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        return prop;
    }
}
