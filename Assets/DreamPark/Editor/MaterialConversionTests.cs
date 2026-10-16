using System.Collections.Generic;
using DreamPark.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Oct 2026 (Zombiez materials pass). MaterialConverter → DreamPark shaders:
//  - a Transparent result came out writing depth: both graphs default Depth Write to
//    "Force Enabled", the swap picked that up, and URP's surface sync (what the converter,
//    DreamParkMaterialRules, the inspector and the import postprocessor all run) honoured it;
//  - an untextured source came out with the graphs' default grey checker in _baseTex.
// Each test fails on the pre-Oct-2026 converter and passes now. BaseShaderGUI.SetupMaterialBlendMode
// is called again at the end of each one because that is the repair path's own call
// (DreamParkMaterialRules.SyncSurfaceState): it must not turn depth write back on.
public sealed class MaterialConversionTests
{
    private const string Unlit = "Shader Graphs/DreamPark-Unlit";
    private const string Universal = "Shader Graphs/DreamPark-UniversalShader";
    private readonly List<Object> made = new List<Object>();

    private Material Make(string shaderName)
    {
        var s = Shader.Find(shaderName);
        if (s == null) Assert.Ignore($"shader '{shaderName}' is not available in this project");
        var m = new Material(s) { name = "conv-test " + shaderName };
        made.Add(m);
        return m;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
        made.Clear();
    }

    private static void AssertTransparentNoDepthWrite(Material m)
    {
        Assert.That(m.GetFloat("_Surface"), Is.EqualTo(1f), "Surface Type = Transparent");
        Assert.That(m.GetFloat("_ZWriteControl"), Is.EqualTo(0f), "Depth Write = Auto");
        Assert.That(m.GetFloat("_ZWrite"), Is.EqualTo(0f), "a transparent surface does not write depth");
        Assert.That(m.GetShaderPassEnabled("DepthOnly"), Is.False);
        Assert.That(m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
        Assert.That(m.renderQueue, Is.EqualTo(3000));

        // The repair path's own sync must leave it that way.
        BaseShaderGUI.SetupMaterialBlendMode(m);
        Assert.That(m.GetFloat("_ZWrite"), Is.EqualTo(0f), "URP surface sync turned depth write back on");
    }

    private static void AssertNoChecker(Material m)
    {
        Texture t = m.GetTexture("_baseTex");
        Assert.That(t, Is.Null, $"_baseTex should be empty (samples white), got '{(t != null ? t.name : "")}'");
    }

    [Test]
    public void TransparentUntexturedUrpUnlit_ToDreamParkUnlit_NoDepthWriteNoChecker()
    {
        var m = Make("Universal Render Pipeline/Unlit");
        m.SetFloat("_Surface", 1f);
        m.SetColor("_BaseColor", new Color(1f, 0.2f, 0.1f, 0.5f));
        BaseShaderGUI.SetupMaterialBlendMode(m);

        Assert.That(MaterialConverter.ConvertMaterialToUnlit(m), Is.True);
        Assert.That(m.shader.name, Is.EqualTo(Unlit));
        AssertTransparentNoDepthWrite(m);
        AssertNoChecker(m);
        Color c = m.GetColor("_baseColor");
        Assert.That(Vector4.Distance(c, new Color(1f, 0.2f, 0.1f, 0.5f)), Is.LessThan(1e-3f), "colour unchanged: " + c);
    }

    [Test]
    public void TransparentUntexturedUrpLit_ToDreamParkUniversal_NoDepthWriteNoChecker()
    {
        var m = Make("Universal Render Pipeline/Lit");
        m.SetFloat("_Surface", 1f);
        m.SetColor("_BaseColor", new Color(0.1f, 0.6f, 1f, 0.4f));
        BaseShaderGUI.SetupMaterialBlendMode(m);

        Assert.That(MaterialConverter.ConvertMaterial(m), Is.True);
        Assert.That(m.shader.name, Is.EqualTo(Universal));
        AssertTransparentNoDepthWrite(m);
        AssertNoChecker(m);
    }

    [Test]
    public void StandardFade_ToDreamParkUnlit_IsTransparentWithoutDepthWrite()
    {
        var m = Make("Standard");
        m.SetFloat("_Mode", 2f);   // Fade
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = 3000;

        Assert.That(MaterialConverter.ConvertMaterialToUnlit(m), Is.True);
        AssertTransparentNoDepthWrite(m);
        Assert.That(m.GetFloat("_Blend"), Is.EqualTo(0f), "Alpha blend");
        AssertNoChecker(m);
    }

    [Test]
    public void OpaqueUntexturedSources_StayOpaqueWithDepthWriteAndNoChecker()
    {
        // Standard (no _Surface) → Unlit, whose graph default is Transparent: the source is
        // opaque, so the result must be too.
        var std = Make("Standard");
        std.SetFloat("_Mode", 0f);
        std.SetColor("_Color", new Color(0.3f, 0.8f, 0.2f, 1f));
        Assert.That(MaterialConverter.ConvertMaterialToUnlit(std), Is.True);
        Assert.That(std.GetFloat("_Surface"), Is.EqualTo(0f));
        Assert.That(std.GetFloat("_ZWrite"), Is.EqualTo(1f));
        Assert.That(std.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.False);
        AssertNoChecker(std);

        var lit = Make("Universal Render Pipeline/Lit");
        lit.SetFloat("_Surface", 0f);
        BaseShaderGUI.SetupMaterialBlendMode(lit);
        Assert.That(MaterialConverter.ConvertMaterial(lit), Is.True);
        Assert.That(lit.GetFloat("_Surface"), Is.EqualTo(0f));
        Assert.That(lit.GetFloat("_ZWrite"), Is.EqualTo(1f));
        BaseShaderGUI.SetupMaterialBlendMode(lit);
        Assert.That(lit.GetFloat("_ZWrite"), Is.EqualTo(1f));
        AssertNoChecker(lit);
    }

    [Test]
    public void ATexturedSourceKeepsItsTexture_AndAPlaceholderCheckerIsNotCarriedAcross()
    {
        var tex = new Texture2D(4, 4) { name = "conv-test albedo" };
        made.Add(tex);
        var m = Make("Universal Render Pipeline/Unlit");
        m.SetTexture("_BaseMap", tex);
        Assert.That(MaterialConverter.ConvertMaterialToUnlit(m), Is.True);
        Assert.That(m.GetTexture("_baseTex"), Is.SameAs(tex));

        // A DreamPark-Universal material that still has the graph's default checker,
        // switched to Unlit: the checker is "no texture", not something to copy.
        var dp = Make(Universal);
        Assert.That(dp.GetTexture("_baseTex"), Is.Not.Null, "precondition: the graph default fills _baseTex");
        Assert.That(MaterialConverter.ConvertMaterialToUnlit(dp), Is.True);
        AssertNoChecker(dp);
    }
}
