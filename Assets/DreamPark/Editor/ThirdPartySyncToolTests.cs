using System.Linq;
using DreamPark.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// ArcadeLand, Oct 2026: Modular 3D Text fonts kept in tracked ThirdParty/ referenced .obj
// meshes still in ThirdPartyLocal. The GUID text scan skips ThirdParty/ as a referencer and
// can't read binary-serialized assets, so the meshes were never moved and the build broke.
public sealed class ThirdPartySyncToolTests
{
    private const string ContentId = "__ThirdPartySyncTest";
    private const string Root = "Assets/Content/" + ContentId;
    private const string Local = Root + "/ThirdPartyLocal";
    private const string Target = Root + "/ThirdParty";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
    }

    [Test]
    public void ClassifyLocal_KeepsOnlyThirdPartyLocalPaths()
    {
        var local = ThirdPartySyncTool.ClassifyLocal(new[]
        {
            Root + "/Prefabs/A_Thing.prefab",
            Local + "/Pack/Font.obj",
            Local + "/Pack/Scripts/FontRenderer.cs",
            Root + "/ThirdPartyLocalNot/Other.obj",
            Local + "/Pack/Font.obj",
        }, Local);

        Assert.That(local, Is.EqualTo(new[] { Local + "/Pack/Font.obj", Local + "/Pack/Scripts/FontRenderer.cs" }));
    }

    [Test]
    public void PlanSync_MovesWhatTrackedThirdPartyAssetsDependOn()
    {
        AssetDatabase.CreateFolder("Assets/Content", ContentId);
        AssetDatabase.CreateFolder(Root, "ThirdPartyLocal");
        AssetDatabase.CreateFolder(Local, "Pack");
        AssetDatabase.CreateFolder(Root, "ThirdParty");
        AssetDatabase.CreateFolder(Root, "Unused");

        var mesh = new Mesh { name = "GlyphMesh" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new[] { 0, 1, 2 };
        string meshPath = Local + "/Pack/GlyphMesh.asset";
        AssetDatabase.CreateAsset(mesh, meshPath);
        var unusedMesh = new Mesh { name = "Unused" };
        AssetDatabase.CreateAsset(unusedMesh, Local + "/Pack/Unused.asset");

        // A tracked ThirdParty asset (like the font) that references the local mesh.
        var holder = new GameObject("FontHolder");
        holder.AddComponent<MeshFilter>().sharedMesh = mesh;
        PrefabUtility.SaveAsPrefabAsset(holder, Target + "/FontHolder.prefab");
        Object.DestroyImmediate(holder);

        Assert.That(ThirdPartySyncTool.LocalDependenciesOf(Root, Local), Is.EqualTo(new[] { meshPath }));

        ThirdPartySyncTool.SyncPlan plan = ThirdPartySyncTool.PlanSync(Root, Local, Target);
        Assert.That(plan.moves.Select(m => m.source), Is.EqualTo(new[] { meshPath }),
            "only what the content depends on moves");
        Assert.That(plan.moves[0].target, Is.EqualTo(Target + "/Pack/GlyphMesh.asset"));
        Assert.That(plan.referencedScripts, Is.Empty);
    }
}
