#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// Layout frame previews (LAYOUT_FRAMES_SPEC.md §7): rendering applies the
// frame, generation writes Previews/Frames/{prefab}@{frameId}.png and cleans up
// stale/orphan PNGs, per-frame settings fall back to Base, and frame previews
// are never Addressables. Everything touches only a temporary content folder.
public sealed class AttractionLayoutFramePreviewTests
{
    private const string ContentId = "__LayoutFramePreviewTests";
    private const string Root = "Assets/Content/" + ContentId;
    private const string PrefabPath = Root + "/Prefabs/A_Stack.prefab";
    private const string BigFrame = "fbig00001";

    private GameObject _root;

    [SetUp]
    public void SetUp()
    {
        CleanUpContentFolder();
        ContentProcessor.ExecuteWithWatchdogPaused(() =>
        {
            if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
            AssetDatabase.CreateFolder("Assets/Content", ContentId);
            AssetDatabase.CreateFolder(Root, "Prefabs");
        });
    }

    [TearDown]
    public void TearDown()
    {
        PrefabPreviewRenderer.BeforeBoundsHookForTests = null;
        if (_root != null) Object.DestroyImmediate(_root);
        CleanUpContentFolder();
    }

    [Test]
    public void RenderPreview_AppliesTheFrameToTheCloneBeforeBoundsAreMeasured()
    {
        AttractionTemplate attraction = CreateStackAttraction();
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        Assert.That(attraction.FrameHasValidBake(attraction.MinimumFrame), Is.True);

        bool rightActive = true, extraActive = false;
        string appliedFrame = null;
        PrefabPreviewRenderer.BeforeBoundsHookForTests = instance =>
        {
            rightActive = instance.transform.Find("Right").gameObject.activeSelf;
            extraActive = instance.transform.Find("Extra").gameObject.activeSelf;
            appliedFrame = instance.GetComponent<AttractionTemplate>().CurrentFrameId;
        };

        Texture2D minimum = PrefabPreviewRenderer.RenderPreview(_root, PreviewSettings.Default, 64,
            AttractionTemplate.MinimumFrameId);
        Assert.That(rightActive, Is.False, "The Minimum frame hides Right before the shot is framed.");
        Assert.That(appliedFrame, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(minimum, Is.Not.Null);
        Object.DestroyImmediate(minimum);

        Texture2D big = PrefabPreviewRenderer.RenderPreview(_root, PreviewSettings.Default, 64, BigFrame);
        Assert.That(extraActive, Is.True, "The big frame shows the extra that Base keeps inactive.");
        Assert.That(appliedFrame, Is.EqualTo(BigFrame));
        Object.DestroyImmediate(big);

        Texture2D baseline = PrefabPreviewRenderer.RenderPreview(_root, PreviewSettings.Default, 64);
        Assert.That(rightActive, Is.True);
        Assert.That(extraActive, Is.False);
        Assert.That(appliedFrame, Is.EqualTo(AttractionTemplate.BaseFrameId), "Base renders untouched.");
        Object.DestroyImmediate(baseline);

        Assert.That(_root.transform.Find("Right").gameObject.activeSelf, Is.True,
            "Only the hidden clone is ever put in a frame.");
        Assert.That(_root.transform.Find("Extra").gameObject.activeSelf, Is.False);
    }

    [Test]
    public void RenderPreview_UnbakedFrameStillShowsItsOverrides()
    {
        AttractionTemplate attraction = CreateStackAttraction();
        AttractionPackingBaker.Bake(attraction, false);
        var frames = attraction.LayoutFrames.ToList();
        frames.Add(Frame("fnobake01", "Fresh", 12f, 12f, Hide(_root.transform.Find("Left"))));
        attraction.SetLayoutFrames(frames);
        Assert.That(attraction.FrameHasValidBake(attraction.FindFrame("fnobake01")), Is.False);

        bool leftActive = true;
        PrefabPreviewRenderer.BeforeBoundsHookForTests = instance =>
            leftActive = instance.transform.Find("Left").gameObject.activeSelf;

        Texture2D preview = PrefabPreviewRenderer.RenderPreview(_root, PreviewSettings.Default, 64, "fnobake01");
        Assert.That(leftActive, Is.False);
        if (preview != null) Object.DestroyImmediate(preview);

        LogAssert.Expect(LogType.Warning, new Regex("has no layout frame 'fmissing1'"));
        preview = PrefabPreviewRenderer.RenderPreview(_root, PreviewSettings.Default, 64, "fmissing1");
        Assert.That(leftActive, Is.True, "An unknown frame renders Base.");
        if (preview != null) Object.DestroyImmediate(preview);
    }

    [Test]
    public void GenerateAllLevelPreviews_WritesFramePathsAndRemovesStaleAndOrphanPngs()
    {
        SaveStackPrefab();
        string minimumPath = AttractionLayoutFramePreviewPaths.PathFor(ContentId, "A_Stack", AttractionTemplate.MinimumFrameId);
        string bigPath = AttractionLayoutFramePreviewPaths.PathFor(ContentId, "A_Stack", BigFrame);
        string stalePath = AttractionLayoutFramePreviewPaths.PathFor(ContentId, "A_Stack", "fstale001");
        string orphanPath = AttractionLayoutFramePreviewPaths.PathFor(ContentId, "A_Gone", AttractionTemplate.MinimumFrameId);
        byte[] placeholder = WriteDummyPng(minimumPath);
        WriteDummyPng(stalePath);
        WriteDummyPng(orphanPath);
        PreviewMetadataStore.SetFrame(ContentId, "A_Stack", "fstale001",
            new PreviewSettings { azimuth = 10f, elevation = 20f, zoom = 1f });

        ContentProcessor.ExecuteWithWatchdogPaused(() => ContentProcessor.GenerateAllLevelPreviews(ContentId));

        Assert.That(File.Exists($"{Root}/Previews/A_Stack.png"), Is.True, "Base keeps Previews/{prefab}.png.");
        Assert.That(File.Exists(bigPath), Is.True, "A missing frame preview is rendered.");
        Assert.That(File.ReadAllBytes(minimumPath), Is.EqualTo(placeholder),
            "An existing frame preview is skipped unless forced.");
        Assert.That(File.Exists(stalePath), Is.False, "A PNG of a frame the prefab no longer has is deleted.");
        Assert.That(File.Exists(orphanPath), Is.False, "A PNG of a prefab that no longer exists is deleted.");
        Assert.That(PreviewMetadataStore.HasFrame(ContentId, "A_Stack", "fstale001"), Is.False,
            "The deleted frame's override goes with it.");
        Assert.That(Directory.GetFiles($"{Root}/Previews", "A_Stack@*"), Is.Empty,
            "Frame previews never land beside the base previews.");
        Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(bigPath), Is.Not.Null, "Frame PNGs are imported.");

        Assert.That(ContentProcessor.RegenerateFramePreview(ContentId, PrefabPath, AttractionTemplate.MinimumFrameId),
            Is.True);
        Assert.That(File.ReadAllBytes(minimumPath), Is.Not.EqualTo(placeholder), "A single-frame save re-renders.");

        LogAssert.Expect(LogType.Warning, new Regex("has no layout frame 'fstale001'"));
        Assert.That(ContentProcessor.RegenerateFramePreview(ContentId, PrefabPath, "fstale001"), Is.False);
    }

    [Test]
    public void GenerateAllLevelPreviews_KeepsAFramePngWhosePrefabNameDiffersOnlyInCase()
    {
        SaveStackPrefab();
        // A case-only prefab rename ("a_stack" → "A_Stack") leaves the old-case
        // PNG behind. On a case-insensitive file system that file IS
        // A_Stack@minimum.png, so the skip-if-exists check keeps it and the
        // orphan sweep must not then delete it.
        WriteDummyPng(AttractionLayoutFramePreviewPaths.PathFor(ContentId, "a_stack", AttractionTemplate.MinimumFrameId));

        ContentProcessor.ExecuteWithWatchdogPaused(() => ContentProcessor.GenerateAllLevelPreviews(ContentId));

        Assert.That(File.Exists(AttractionLayoutFramePreviewPaths.PathFor(
                ContentId, "A_Stack", AttractionTemplate.MinimumFrameId)), Is.True,
            "A_Stack still has a Minimum frame preview after generation.");
    }

    [Test]
    public void PreviewMetadataStore_FrameSettingsFallBackToTheBaseSettings()
    {
        const string prefab = "A_Stack";
        Assert.That(PreviewMetadataStore.GetOrDefaultForFrame(ContentId, prefab, "minimum").azimuth,
            Is.EqualTo(PreviewSettings.DefaultAzimuth));

        PreviewMetadataStore.Set(ContentId, prefab, new PreviewSettings { azimuth = 90f, elevation = 30f, zoom = 1f });
        Assert.That(PreviewMetadataStore.GetOrDefaultForFrame(ContentId, prefab, "minimum").azimuth, Is.EqualTo(90f));
        Assert.That(PreviewMetadataStore.TryGetFrame(ContentId, prefab, "minimum", out _), Is.False,
            "The Base fallback is not the frame's own override.");

        PreviewMetadataStore.SetFrame(ContentId, prefab, "minimum", new PreviewSettings { azimuth = 10f, elevation = 30f, zoom = 1f });
        Assert.That(PreviewMetadataStore.GetOrDefaultForFrame(ContentId, prefab, "minimum").azimuth, Is.EqualTo(10f));
        Assert.That(PreviewMetadataStore.Has(ContentId, AttractionLayoutFramePreviewPaths.MetadataKey(prefab, "minimum")),
            Is.True, "Stored under {prefab}@{frameId}.");
        Assert.That(PreviewMetadataStore.GetOrDefault(ContentId, prefab).azimuth, Is.EqualTo(90f),
            "A frame override never changes Base.");
        Assert.That(PreviewMetadataStore.GetOrDefaultForFrame(ContentId, prefab, AttractionTemplate.BaseFrameId).azimuth,
            Is.EqualTo(90f), "\"base\" is the prefab's own entry.");

        PreviewMetadataStore.ClearFrame(ContentId, prefab, "minimum");
        Assert.That(PreviewMetadataStore.GetOrDefaultForFrame(ContentId, prefab, "minimum").azimuth, Is.EqualTo(90f));
        Assert.That(PreviewMetadataStore.Has(ContentId, prefab), Is.True, "Clearing a frame keeps Base.");
    }

    [Test]
    public void FramePreviews_AreNeverAddressablesNorBasePreviews()
    {
        Assert.That(ContentProcessor.IsLayoutFramePreviewAsset("Assets/Content/Game/Previews/Frames/A_X@minimum.png"), Is.True);
        Assert.That(ContentProcessor.IsLayoutFramePreviewAsset("Assets/Content/Game/Previews/Frames"), Is.False,
            "The folder itself is skipped as a folder, not as a preview.");
        Assert.That(ContentProcessor.IsLayoutFramePreviewAsset("Assets/Content/Game/Previews/A_X.png"), Is.False);
        Assert.That(ContentProcessor.IsLayoutFramePreviewAsset("Assets/Content/Game/Previews/A_X@minimum.png"), Is.False,
            "A base preview of a prefab whose name contains '@' stays a base preview.");
        Assert.That(ContentProcessor.IsLayoutFramePreviewAsset("Assets/Content/Game/ThirdParty/Pack/Previews/Frames/t.png"),
            Is.False, "A vendor pack's own Previews/Frames folder is not ours.");
        Assert.That(ContentProcessor.ShouldSkipAsset("Assets/Content/Game/Previews/Frames/A_X@f1a2b3c4d.png"), Is.True);
    }

    [Test]
    public void DisplayFrames_BaseFirstThenCustomByAreaThenMinimum()
    {
        AttractionTemplate attraction = CreateAttraction();
        AttractionLayoutFrame minimum = Frame(AttractionTemplate.MinimumFrameId, "Minimum", 4f, 4f);
        minimum.kind = AttractionLayoutFrameKind.Minimum;
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            minimum,
            Frame("fsmall001", "Small", 10f, 10f),
            Frame("Bad Id", "Invalid", 50f, 50f),
            Frame(AttractionTemplate.BaseFrameId, "Reserved", 60f, 60f),
            Frame("fhuge0001", "Huge", 40f, 30f),
            Frame("fsame0001", "Same area, later", 10f, 10f),
        });

        List<AttractionLayoutFramePreviews.FrameInfo> frames = AttractionLayoutFramePreviews.DisplayFrames(attraction);
        Assert.That(frames.Select(f => f.frameId), Is.EqualTo(new[]
        {
            AttractionTemplate.BaseFrameId, "fhuge0001", "fsmall001", "fsame0001", AttractionTemplate.MinimumFrameId,
        }));
        Assert.That(frames[0].sizeFeet.x, Is.EqualTo(20f).Within(0.001f));
        Assert.That(AttractionLayoutFramePreviews.PreviewFrameIds(attraction), Is.EqualTo(new[]
        {
            "fhuge0001", "fsmall001", "fsame0001", AttractionTemplate.MinimumFrameId,
        }));
        Assert.That(AttractionLayoutFramePreviews.FormatFeet(new Vector2(20f, 12.5f)), Is.EqualTo("20 × 12.5 ft"));

        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>());
        Assert.That(AttractionLayoutFramePreviews.DisplayFrames(attraction).Count, Is.EqualTo(1));
        Assert.That(AttractionLayoutFramePreviews.PreviewFrameIds(attraction), Is.Empty);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    // Left + Right visible cubes; Extra only exists in the big frame; the
    // Minimum frame hides Right.
    private AttractionTemplate CreateStackAttraction()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateCube("Left", -1f);
        Transform right = CreateCube("Right", 1f);
        Transform extra = CreateCube("Extra", 3f);
        extra.gameObject.SetActive(false);
        AttractionLayoutFrame minimum = Frame(AttractionTemplate.MinimumFrameId, "Minimum", 10f, 10f, Hide(right));
        minimum.kind = AttractionLayoutFrameKind.Minimum;
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame(BigFrame, "Big", 30f, 20f, Show(extra)),
            minimum,
        });
        return attraction;
    }

    private void SaveStackPrefab()
    {
        AttractionTemplate attraction = CreateStackAttraction();
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        ContentProcessor.ExecuteWithWatchdogPaused(() =>
        {
            PrefabUtility.SaveAsPrefabAsset(_root, PrefabPath, out bool saved);
            Assert.That(saved, Is.True);
        });
        Object.DestroyImmediate(_root);
        _root = null;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Assert.That(prefab.GetComponent<AttractionTemplate>().HasLayoutFrames, Is.True);
    }

    private AttractionTemplate CreateAttraction()
    {
        _root = new GameObject("A_Stack");
        AttractionTemplate attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        return attraction;
    }

    private Transform CreateCube(string name, float x)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = new Vector3(x, 0.5f, 0f);
        PropTemplate prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        return go.transform;
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

    private AttractionLayoutOverride Show(Transform target) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setActive = true,
        active = true,
    };

    private static byte[] WriteDummyPng(string assetPath)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        texture.SetPixels32(Enumerable.Repeat(new Color32(255, 0, 0, 255), 16).ToArray());
        texture.Apply();
        byte[] bytes = texture.EncodeToPNG();
        Object.DestroyImmediate(texture);
        ContentProcessor.ExecuteWithWatchdogPaused(() =>
        {
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            File.WriteAllBytes(assetPath, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        });
        return bytes;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static void CleanUpContentFolder()
    {
        ContentProcessor.ExecuteWithWatchdogPaused(() =>
        {
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            // The metadata dot-file is invisible to the AssetDatabase.
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            if (File.Exists(Root + ".meta")) File.Delete(Root + ".meta");
        });
    }
}
#endif
