using DreamPark.EditorTools;
using DreamPark.EditorTools.Shaders;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// WizardsWay, Oct 2026: converted particle materials had their features enabled as
// keywords with the [Toggle(...)] floats left at 0, so the next validation of the
// material (re-deriving keywords from the floats) silently switched them off.
public sealed class DreamParkKeywordTogglesTests
{
    private const string Folder = "Assets/__KeywordTogglesTest";
    private Material _material;

    [TearDown]
    public void TearDown()
    {
        if (_material != null && !AssetDatabase.Contains(_material)) Object.DestroyImmediate(_material);
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
    }

    [Test]
    public void ParticleShaderTogglesAreReadFromItsAttributes()
    {
        var toggles = new System.Collections.Generic.List<DreamParkKeywordToggles.ToggleBinding>();
        var enums = new System.Collections.Generic.List<DreamParkKeywordToggles.EnumBinding>();
        DreamParkKeywordToggles.Describe(Shader.Find(DreamParkKeywordToggles.ParticleShaderName), toggles, enums);

        Assert.That(toggles, Has.Some.Matches<DreamParkKeywordToggles.ToggleBinding>(
            t => t.property == "_SoftParticles" && t.keyword == "_SOFTPARTICLES_ON"));
        Assert.That(toggles, Has.Some.Matches<DreamParkKeywordToggles.ToggleBinding>(
            t => t.property == "_NoiseMod" && t.keyword == "_NOISE_MOD_ON"));
        Assert.That(enums, Has.Some.Matches<DreamParkKeywordToggles.EnumBinding>(
            e => e.property == "_BlendMode" && e.keywords[1] == "_BLENDMODE_ADDITIVE"));
    }

    [Test]
    public void ConvertParticleMaterial_SetsEveryToggleItsKeywordsNeed()
    {
        Shader source = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        Assume.That(source, Is.Not.Null, "URP particle shader not available");
        _material = new Material(source) { name = "KeywordToggleSource" };
        _material.EnableKeyword("_SOFTPARTICLES_ON");
        _material.EnableKeyword("_ALPHATEST_ON");
        _material.SetFloat("_CameraFadingEnabled", 1f);   // URP's camera fade is a float, not a keyword
        _material.EnableKeyword("_EMISSION");
        if (_material.HasProperty("_EmissionColor")) _material.SetColor("_EmissionColor", Color.white);

        Assert.That(MaterialConverter.ConvertParticleMaterial(_material), Is.True);

        Assert.That(_material.shader.name, Is.EqualTo(DreamParkKeywordToggles.ParticleShaderName));
        foreach (var (keyword, toggle) in new[]
                 {
                     ("_SOFTPARTICLES_ON", "_SoftParticles"), ("_ALPHATEST_ON", "_AlphaTest"),
                     ("_CAMERAFADE_ON", "_CameraFade"), ("_EMISSION_ON", "_Emission"),
                 })
        {
            Assert.That(_material.IsKeywordEnabled(keyword), Is.True, keyword);
            Assert.That(_material.GetFloat(toggle), Is.EqualTo(1f), toggle + " must match " + keyword);
        }
        Assert.That(DreamParkKeywordToggles.FindMismatches(_material), Is.Empty);
    }

    [Test]
    public void Sync_KeywordWins_ReportOnlyChangesNothing()
    {
        _material = new Material(Shader.Find(DreamParkKeywordToggles.ParticleShaderName));
        PrepareMismatched(_material);

        var reported = DreamParkKeywordToggles.SyncTogglesToKeywords(_material, apply: false);
        Assert.That(reported.Count, Is.EqualTo(3), string.Join("\n", reported));
        Assert.That(_material.GetFloat("_NoiseMod"), Is.EqualTo(0f), "report-only must not write");

        DreamParkKeywordToggles.SyncTogglesToKeywords(_material, apply: true);
        Assert.That(_material.GetFloat("_NoiseMod"), Is.EqualTo(1f));
        Assert.That(_material.GetFloat("_Dissolve"), Is.EqualTo(0f), "a toggle on with its keyword off follows the keyword");
        Assert.That(_material.GetFloat("_BlendMode"), Is.EqualTo(1f));
        Assert.That(_material.IsKeywordEnabled("_NOISE_MOD_ON"), Is.True);
        Assert.That(DreamParkKeywordToggles.FindMismatches(_material), Is.Empty);
    }

    [Test]
    public void ProjectRepair_ReportsThenFixesSavedMaterials()
    {
        AssetDatabase.CreateFolder("Assets", "__KeywordTogglesTest");
        string path = Folder + "/M_Mismatched.mat";
        AssetDatabase.CreateAsset(new Material(Shader.Find(DreamParkKeywordToggles.ParticleShaderName)), path);
        // What a converted WizardsWay material looks like on disk: the keyword is listed,
        // its toggle float is still 0.
        string yaml = System.IO.File.ReadAllText(path);
        int start = yaml.IndexOf("  m_ValidKeywords:", System.StringComparison.Ordinal);
        int end = yaml.IndexOf("  m_InvalidKeywords:", System.StringComparison.Ordinal);
        Assume.That(start >= 0 && end > start, "unexpected material YAML");
        yaml = yaml.Substring(0, start)
            + "  m_ValidKeywords:\n  - _BLENDMODE_ALPHA\n  - _NOISE_MOD_ON\n  - _VC_MULTIPLY\n"
            + yaml.Substring(end);
        System.IO.File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        _material = material;
        Assume.That(material.IsKeywordEnabled("_NOISE_MOD_ON") && material.GetFloat("_NoiseMod") == 0f,
            "the imported asset keeps the mismatch");

        var report = DreamParkKeywordToggles.RepairParticleToggles(apply: false, folders: new[] { Folder });
        Assert.That(report.scanned, Is.EqualTo(1));
        Assert.That(report.materials, Is.EqualTo(new[] { path }));
        Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path).GetFloat("_NoiseMod"), Is.EqualTo(0f));

        report = DreamParkKeywordToggles.RepairParticleToggles(apply: true, folders: new[] { Folder });
        Assert.That(report.materials.Count, Is.EqualTo(1));
        Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path).GetFloat("_NoiseMod"), Is.EqualTo(1f));
        Assert.That(DreamParkKeywordToggles.RepairParticleToggles(apply: false, folders: new[] { Folder }).materials,
            Is.Empty);
    }

    // Noise keyword on with its toggle off; Dissolve toggle on with its keyword off;
    // blend mode keyword Additive with the enum float still at Alpha (0).
    private static void PrepareMismatched(Material material)
    {
        foreach (string keyword in material.shaderKeywords) material.DisableKeyword(keyword);
        material.EnableKeyword("_NOISE_MOD_ON");
        material.SetFloat("_NoiseMod", 0f);
        material.DisableKeyword("_DISSOLVE_ON");
        material.SetFloat("_Dissolve", 1f);
        material.EnableKeyword("_BLENDMODE_ADDITIVE");
        material.SetFloat("_BlendMode", 0f);
        material.EnableKeyword("_VC_MULTIPLY");
        material.SetFloat("_VC", 1f);
    }
}
