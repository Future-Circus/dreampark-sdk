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
    public void SequenceScaleOverridesNormalGrowCeiling()
    {
        AttractionTemplate attraction = Create(6f, 9f);
        attraction.maxGrowthScale = new Vector2(1.25f, 1.25f);
        Vector2 scale = DreamSequenceCompatibility.SequenceScale(attraction);
        Assert.That(scale.x, Is.EqualTo(2f).Within(0.001f));
        Assert.That(scale.y, Is.EqualTo(2f).Within(0.001f));
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
