using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEngine;

public sealed class DreamSequenceCompatibilityTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    [TestCase(12f, 18f)]
    [TestCase(18f, 12f)]
    [TestCase(8f, 14f)]
    public void AuthoredFootprintThatFitsStandardRoom_IsEligible(float width, float length)
    {
        Assert.That(DreamSequenceCompatibility.IsCompatible(Create(width, length)), Is.True);
    }

    [Test]
    public void LargerAttraction_IsEligibleWhenShrinkBakeFitsTwelveByEighteen()
    {
        AttractionTemplate attraction = Create(30f, 40f);
        attraction.maxGrowthScale = new Vector2(35f / 30f, 45f / 40f);
        attraction.SetPackingBake(new AttractionPackingBake(
            new Vector2(30f, 40f) * 0.3048f,
            new Vector2(30f, 40f) * 0.3048f,
            new Vector2(12f, 18f) * 0.3048f,
            new Vector2(35f, 45f) * 0.3048f,
            new Vector2(12f, 18f) * 0.3048f,
            new List<AttractionPropPackingPose>()));
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.True);
    }

    [Test]
    public void LargerAttractionWithoutCompatibleShrink_IsRejected()
    {
        Assert.That(DreamSequenceCompatibility.IsCompatible(Create(30f, 40f)), Is.False);
    }

    [Test]
    public void SequenceScaleStopsAtBakedGrowCeiling()
    {
        AttractionTemplate attraction = Create(6f, 9f);
        attraction.SetPackingBake(new AttractionPackingBake(
            new Vector2(6f, 9f) * 0.3048f,
            new Vector2(6f, 9f) * 0.3048f,
            new Vector2(4f, 6f) * 0.3048f,
            new Vector2(7.5f, 11.25f) * 0.3048f,
            new Vector2(4f, 6f) * 0.3048f,
            new List<AttractionPropPackingPose>()));
        Vector2 scale = DreamSequenceCompatibility.SequenceScale(attraction);
        Assert.That(scale.x, Is.EqualTo(1.25f).Within(0.001f));
        Assert.That(scale.y, Is.EqualTo(1.25f).Within(0.001f));
    }

    [Test]
    public void RotatedAttractionUsesLocalAxesThatFillTwelveByEighteen()
    {
        AttractionTemplate attraction = Create(18f, 12f);
        Assert.That(DreamSequenceCompatibility.ShouldRotate(attraction), Is.True);
        Vector2 scale = DreamSequenceCompatibility.SequenceScale(attraction);
        Assert.That(scale.x, Is.EqualTo(1f).Within(0.001f));
        Assert.That(scale.y, Is.EqualTo(1f).Within(0.001f));
    }

    [Test]
    public void IsCompatible_LargeAttractionBecomesEligibleOnlyThroughAFittingFrame()
    {
        AttractionTemplate attraction = CreateBaked(30f, 40f, new Vector2(20f, 30f));
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.False);

        // A frame whose bake no longer matches the growth settings is not in the ladder.
        AttractionLayoutFrame stale = Frame("fstale001", 10f, 10f, new Vector2(8f, 8f));
        stale.SetBake(new AttractionPackingBake(stale.SizeMeters, stale.SizeMeters,
            new Vector2(8f, 8f) * Ft, stale.SizeMeters * 2f, new Vector2(8f, 8f) * Ft,
            new List<AttractionPropPackingPose>()));
        AttractionLayoutFrame tooBig = Frame("fbig00001", 25f, 25f, new Vector2(15f, 20f));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { stale, tooBig });
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.False);

        // Fits only when turned: 17 × 11 ft shrink.
        AttractionLayoutFrame rotated = Frame("frot00001", 20f, 12f, new Vector2(17f, 11f));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { stale, tooBig, rotated });
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.True);

        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
            { tooBig, MinimumFrame(10f, 10f, new Vector2(8f, 8f)) });
        Assert.That(DreamSequenceCompatibility.IsCompatible(attraction), Is.True);
    }

    [Test]
    public void TryChooseFrame_WalksLadderLargestFirstAndClampsToThatFrame()
    {
        AttractionTemplate attraction = CreateLadder(out AttractionLayoutFrame custom,
            out AttractionLayoutFrame minimum);
        const string customId = "fcustom01";

        // A big room takes Base, capped at its grow scale.
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction, new Vector2(40f, 50f) * Ft,
            true, out string id, out Vector2 scale, out bool rotate), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(rotate, Is.False);
        Assert.That(scale.x, Is.EqualTo(1.25f).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(1.25f).Within(0.0001f));

        // The standard room: Base cannot shrink that far; the custom frame can,
        // and it is scaled to the room inside its own range.
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction,
            DreamSequenceCompatibility.StandardRoomMeters, true, out id, out scale, out rotate), Is.True);
        Assert.That(id, Is.EqualTo(customId));
        Assert.That(rotate, Is.False);
        Assert.That(scale.x, Is.EqualTo(12f / 14f).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(18f / 20f).Within(0.0001f));
        Assert.That(DreamSequenceCompatibility.SequenceScale(attraction).x, Is.EqualTo(12f / 14f).Within(0.0001f));
        Assert.That(DreamSequenceCompatibility.ShouldRotate(attraction), Is.False);

        // Below the custom frame's shrink, the Minimum frame is used, clamped up to its shrink.
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction, new Vector2(4.5f, 5.5f) * Ft,
            true, out id, out scale, out rotate), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(rotate, Is.False);
        Assert.That(scale.x, Is.EqualTo(4.5f / 6f).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(5.5f / 8f).Within(0.0001f));

        // A room only the turned Minimum frame fits.
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction, new Vector2(5f, 4f) * Ft,
            true, out id, out scale, out rotate), Is.True);
        Assert.That(id, Is.EqualTo(AttractionTemplate.MinimumFrameId));
        Assert.That(rotate, Is.True);
        Assert.That(scale.x, Is.EqualTo(minimum.Bake.ShrinkScale.x).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(minimum.Bake.ShrinkScale.y).Within(0.0001f));

        // ...which Start/Overlay/Game Over cannot use: they never rotate.
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction, new Vector2(5f, 4f) * Ft,
            false, out id, out scale, out rotate), Is.False);
        Assert.That(id, Is.EqualTo(AttractionTemplate.MinimumFrameId), "Fallback is the smallest shrink.");
        Assert.That(rotate, Is.False);
        Assert.That(scale, Is.EqualTo(minimum.Bake.ShrinkScale));
        Assert.That(custom.Bake, Is.Not.Null);
    }

    [Test]
    public void TryChooseFrame_PrefersAuthoredOrientationThenShrinkOrientation()
    {
        Vector2 room = DreamSequenceCompatibility.StandardRoomMeters;
        bool rotate;

        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(12f, 18f, 8f, 10f), room, true, out rotate), Is.True);
        Assert.That(rotate, Is.False, "Authored fits natively.");

        // The shrink fits natively too, but the authored layout only fits turned.
        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(18f, 12f, 11f, 8f), room, true, out rotate), Is.True);
        Assert.That(rotate, Is.True, "Authored fits rotated beats shrink native.");
        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(18f, 12f, 11f, 8f), room, false, out rotate), Is.True);
        Assert.That(rotate, Is.False, "Without rotation only the native shrink is left.");

        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(20f, 20f, 10f, 16f), room, true, out rotate), Is.True);
        Assert.That(rotate, Is.False, "Shrink fits natively.");
        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(20f, 20f, 16f, 10f), room, true, out rotate), Is.True);
        Assert.That(rotate, Is.True, "Shrink fits only rotated.");
        Assert.That(DreamSequenceCompatibility.TryOrientFrame(Envelope(20f, 20f, 16f, 10f), room, false, out rotate), Is.False);
        Assert.That(rotate, Is.False);

        // Frame scale is computed in prefab axes against the turned room.
        Vector2 scale = DreamSequenceCompatibility.FrameScaleFor(Envelope(18f, 12f, 11f, 8f), room, true);
        Assert.That(scale.x, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(1f).Within(0.0001f));

        // End to end: the first ladder frame that fits only turned rotates the attraction.
        AttractionTemplate attraction = CreateBaked(30f, 40f, new Vector2(20f, 30f));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            Frame("fwide0001", 18f, 12f, new Vector2(15f, 10f)),
            MinimumFrame(6f, 6f, new Vector2(5f, 5f)),
        });
        Assert.That(DreamSequenceCompatibility.ShouldRotate(attraction), Is.True);
        Assert.That(DreamSequenceCompatibility.TryChooseFrame(attraction, room, true,
            out string id, out scale, out rotate), Is.True);
        Assert.That(id, Is.EqualTo("fwide0001"));
        Assert.That(rotate, Is.True);
        Assert.That(scale.x, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(1f).Within(0.0001f));
    }

    [TestCase(18f, 12f, 0f, 0f)]
    [TestCase(8f, 14f, 0f, 0f)]
    [TestCase(6f, 9f, 4f, 6f)]
    [TestCase(30f, 20f, 16f, 11f)]
    [TestCase(30f, 40f, 12f, 18f)]
    [TestCase(30f, 40f, 20f, 30f)]
    public void TryChooseFrame_WithoutFramesReproducesLegacyRotationAndScale(
        float width, float length, float shrinkWidth, float shrinkLength)
    {
        AttractionTemplate attraction = shrinkWidth > 0f
            ? CreateBaked(width, length, new Vector2(shrinkWidth, shrinkLength))
            : Create(width, length);

        bool fits = DreamSequenceCompatibility.TryChooseFrame(attraction,
            DreamSequenceCompatibility.StandardRoomMeters, true,
            out string id, out Vector2 scale, out bool rotate);

        Assert.That(id, Is.EqualTo(AttractionTemplate.BaseFrameId));
        Assert.That(fits, Is.EqualTo(DreamSequenceCompatibility.IsCompatible(attraction)));
        Assert.That(rotate, Is.EqualTo(DreamSequenceCompatibility.ShouldRotate(attraction)));
        Vector2 legacy = DreamSequenceCompatibility.SequenceScale(attraction);
        Assert.That(scale.x, Is.EqualTo(legacy.x).Within(0.0001f));
        Assert.That(scale.y, Is.EqualTo(legacy.y).Within(0.0001f));
    }

    private const float Ft = 0.3048f;

    private AttractionTemplate CreateLadder(out AttractionLayoutFrame custom, out AttractionLayoutFrame minimum)
    {
        AttractionTemplate attraction = CreateBaked(30f, 40f, new Vector2(20f, 30f));
        custom = Frame("fcustom01", 14f, 20f, new Vector2(11f, 16f));
        minimum = MinimumFrame(6f, 8f, new Vector2(4f, 5f));
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { minimum, custom });
        return attraction;
    }

    private AttractionTemplate CreateBaked(float widthFeet, float lengthFeet, Vector2 shrinkFeet)
    {
        AttractionTemplate attraction = Create(widthFeet, lengthFeet);
        attraction.SetPackingBake(Bake(new Vector2(widthFeet, lengthFeet) * Ft, shrinkFeet * Ft));
        return attraction;
    }

    private static AttractionLayoutFrame Frame(string id, float widthFeet, float lengthFeet, Vector2 shrinkFeet)
    {
        var frame = new AttractionLayoutFrame
        {
            id = id,
            name = id,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = new Vector2(widthFeet, lengthFeet),
        };
        frame.SetBake(Bake(frame.SizeMeters, shrinkFeet * Ft));
        return frame;
    }

    private static AttractionLayoutFrame MinimumFrame(float widthFeet, float lengthFeet, Vector2 shrinkFeet)
    {
        AttractionLayoutFrame frame = Frame(AttractionTemplate.MinimumFrameId, widthFeet, lengthFeet, shrinkFeet);
        frame.kind = AttractionLayoutFrameKind.Minimum;
        return frame;
    }

    // Matches the default maxGrowthScale (1.25) so the bake is valid.
    private static AttractionPackingBake Bake(Vector2 authored, Vector2 shrink) =>
        new AttractionPackingBake(authored, authored, shrink, authored * 1.25f, shrink,
            new List<AttractionPropPackingPose>());

    private static AttractionFrameEnvelope Envelope(float widthFeet, float lengthFeet,
        float shrinkWidthFeet, float shrinkLengthFeet)
    {
        Vector2 authored = new Vector2(widthFeet, lengthFeet) * Ft;
        Vector2 shrink = new Vector2(shrinkWidthFeet, shrinkLengthFeet) * Ft;
        return new AttractionFrameEnvelope
        {
            frameId = "fenvelope",
            kind = "custom",
            name = "Envelope",
            authoredMeters = authored,
            shrinkMeters = shrink,
            growMeters = authored * 1.25f,
            shrinkScale = new Vector2(shrink.x / authored.x, shrink.y / authored.y),
            growScale = new Vector2(1.25f, 1.25f),
        };
    }

    private AttractionTemplate Create(float widthFeet, float lengthFeet)
    {
        var go = new GameObject("Attraction");
        created.Add(go);
        var attraction = go.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(widthFeet, lengthFeet);
        return attraction;
    }
}
