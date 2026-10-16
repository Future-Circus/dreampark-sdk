using DreamPark.PreUploadChecks;
using NUnit.Framework;

// Oct 2026 sweep (DreamSports / Wizard's Way): MetaOcclusionCheck missed the un-culled MRUK
// occluder, and the scene-overrides check opened vendor demo scenes (≈93 Missing Prefab errors).
public sealed class PreUploadScopesTests
{
    [Test]
    public void BothMrukInvisibleOccludersAreExemptFromTheOcclusionCheck()
    {
        Assert.That(PreUploadScopes.IsOcclusionExempt("Meta/MRUK/MixedReality/InvisibleOccluder"), Is.True);
        Assert.That(PreUploadScopes.IsOcclusionExempt("Meta/MRUK/MixedReality/InvisibleOccluderCulled"), Is.True);
        Assert.That(PreUploadScopes.IsOcclusionExempt("Meta/EnvironmentDepth/DepthMask"), Is.True);
        Assert.That(PreUploadScopes.IsOcclusionExempt("Universal Render Pipeline/Lit"), Is.False);
        Assert.That(PreUploadScopes.IsOcclusionExempt("Meta/MRUK/MixedReality/InvisibleOccluderX"), Is.False);
        Assert.That(PreUploadScopes.IsOcclusionExempt(null), Is.False);
    }

    [Test]
    public void SceneOverridesSkipsVendorScenesButKeepsTheProjectsOwn()
    {
        Assert.That(PreUploadScopes.IsVendorScene("Assets/Content/ArcadeLand/ThirdParty/Plugins/Tiny Giant Studio/Demo/Demo.unity"), Is.True);
        Assert.That(PreUploadScopes.IsVendorScene("Assets/Content/DreamSports/ThirdPartyLocal/Pack/Scenes/Example.unity"), Is.True);
        Assert.That(PreUploadScopes.IsVendorScene("Assets/Content/DreamSports/Scenes/DreamSportsTest.unity"), Is.False);
        Assert.That(PreUploadScopes.IsVendorScene("Assets/Content/WizardsWay/WizardsWay.unity"), Is.False);
        Assert.That(PreUploadScopes.IsVendorScene("Assets/Content/X/MyThirdPartyTests/Test.unity"), Is.False);
    }
}
