using System.Collections.Generic;
using System.Linq;
using DreamPark.PreUploadChecks;
using NUnit.Framework;
using UnityEngine;
using Source = DreamPark.PreUploadChecks.PreUploadMaterialSources.Source;

// Oct 2026 (Zombiez materials pass): meta-occlusion only read MeshRenderer /
// SkinnedMeshRenderer materials of the content roots, so particle and trail materials,
// materials injected into LuaBehaviours, and the Sequence special levels were never
// checked — 29 Zombiez materials drew over the guest's hands with no finding.
public sealed class PreUploadMaterialSourcesTests
{
    private readonly List<Object> made = new List<Object>();

    private Material Mat(string name)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = name };
        made.Add(m);
        return m;
    }

    private GameObject Go(string name, Transform parent)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        else made.Add(go);
        return go;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
        made.Clear();
    }

    [Test]
    public void CollectsMeshParticleTrailAndLuaInjectedMaterialsButNotLineRenderers()
    {
        Material meshMat = Mat("mesh"), particleMat = Mat("particle"), trailMat = Mat("trail"),
                 luaMat = Mat("lua"), lineMat = Mat("line"), inactiveMat = Mat("inactive");

        var root = Go("A_Root", null);
        Go("Mesh", root.transform).AddComponent<MeshRenderer>().sharedMaterial = meshMat;

        var fx = Go("Fx", root.transform);
        fx.AddComponent<ParticleSystem>();
        var psr = fx.GetComponent<ParticleSystemRenderer>();
        psr.sharedMaterial = particleMat;
        psr.trailMaterial = trailMat;

        var lua = Go("Lua", root.transform).AddComponent<LuaBehaviour>();
        lua.materialInjections = new[]
        {
            new MaterialInjection { name = "hit_mat", value = luaMat },
            new MaterialInjection { name = "empty", value = null },
        };

        Go("Line", root.transform).AddComponent<LineRenderer>().sharedMaterial = lineMat;

        var off = Go("Off", root.transform);
        off.AddComponent<MeshRenderer>().sharedMaterial = inactiveMat;
        off.SetActive(false);

        var hits = PreUploadMaterialSources.Collect(root);
        Source? SourceOf(Material m) => hits.Where(h => h.material == m).Select(h => (Source?)h.source).FirstOrDefault();

        Assert.That(SourceOf(meshMat), Is.EqualTo(Source.Mesh));
        Assert.That(SourceOf(inactiveMat), Is.EqualTo(Source.Mesh), "inactive children ship too");
        Assert.That(SourceOf(particleMat), Is.EqualTo(Source.Particle));
        Assert.That(SourceOf(trailMat), Is.EqualTo(Source.ParticleTrail));
        Assert.That(SourceOf(luaMat), Is.EqualTo(Source.LuaInjection));
        Assert.That(SourceOf(lineMat), Is.Null, "LineRenderer stays out of scope");
        Assert.That(hits.Count, Is.EqualTo(5), string.Join(", ", hits.Select(h => h.material.name + ":" + h.source)));

        // The predicate the check used before Oct 2026 (MeshRenderer || SkinnedMeshRenderer)
        // sees only the two mesh materials: the three new sources are what was missed.
        var legacy = root.GetComponentsInChildren<Renderer>(true)
            .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
            .SelectMany(r => r.sharedMaterials).ToList();
        CollectionAssert.DoesNotContain(legacy, particleMat);
        CollectionAssert.DoesNotContain(legacy, trailMat);
        CollectionAssert.DoesNotContain(legacy, luaMat);
    }

    [Test]
    public void SameMaterialFromTwoRenderersIsListedOncePerSource()
    {
        Material shared = Mat("shared");
        var root = Go("P_Root", null);
        Go("A", root.transform).AddComponent<MeshRenderer>().sharedMaterial = shared;
        Go("B", root.transform).AddComponent<MeshRenderer>().sharedMaterial = shared;
        var lua = Go("Lua", root.transform).AddComponent<LuaBehaviour>();
        lua.materialInjections = new[] { new MaterialInjection { name = "m", value = shared } };

        var hits = PreUploadMaterialSources.Collect(root);
        Assert.That(hits.Count(h => h.source == Source.Mesh), Is.EqualTo(1));
        Assert.That(hits.Count(h => h.source == Source.LuaInjection), Is.EqualTo(1));
    }

    [Test]
    public void SequenceSpecialLevelsAreAddedToTheScanWhenTheyExist()
    {
        const string start = "Assets/Content/Zombiez/DreamSequence/Start Level.prefab";
        const string overlay = "Assets/Content/Zombiez/DreamSequence/Overlay Level.prefab";
        const string gameOver = "Assets/Content/Zombiez/DreamSequence/Game Over Level.prefab";
        var roots = new[] { "Assets/Content/Zombiez/Prefabs/A_Garage.prefab", "Assets/Content/Zombiez/Prefabs/Player.prefab" };

        var all = PreUploadMaterialSources.WithSpecialLevels(roots, new[] { start, overlay, gameOver }, p => p != overlay);
        Assert.That(all, Is.EqualTo(new[] { roots[0], roots[1], start, gameOver }));

        // Never twice, whatever the case of the path.
        var dup = PreUploadMaterialSources.WithSpecialLevels(new[] { start.ToLowerInvariant() }, new[] { start }, p => true);
        Assert.That(dup.Count, Is.EqualTo(1));

        Assert.That(PreUploadMaterialSources.WithSpecialLevels(null, null, null), Is.Empty);
    }
}
