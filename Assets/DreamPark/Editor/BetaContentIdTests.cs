#if UNITY_EDITOR && !DREAMPARKCORE
using DreamPark;
using NUnit.Framework;

public sealed class BetaContentIdTests
{
    [Test]
    public void DeriveFrom_ProducesIndependentPathSafeTarget()
    {
        string betaId = BetaContentId.DeriveFrom("SuperAdventureLand");

        Assert.That(betaId, Is.EqualTo("SuperAdventureLandBeta"));
        Assert.That(BetaContentId.IsBetaTargetId(betaId), Is.True);
        Assert.That(BetaContentId.IsPathSafe(betaId), Is.True);
        Assert.That(BetaContentId.ReleaseContentIdOf(betaId), Is.EqualTo("SuperAdventureLand"));
    }

    [TestCase("AlphaBeta")]
    [TestCase("alphabeta")]
    public void DeriveFrom_RejectsIdsInReservedBetaNamespace(string releaseId)
    {
        Assert.That(BetaContentId.DeriveFrom(releaseId), Is.Null);
    }

    [Test]
    public void DeriveFrom_RejectsPlaceholderAndSampleFolders()
    {
        Assert.That(BetaContentId.DeriveFrom("Sample"), Is.Null);
        Assert.That(BetaContentId.DeriveFrom("YOUR_GAME_HERE"), Is.Null);
    }
}
#endif
