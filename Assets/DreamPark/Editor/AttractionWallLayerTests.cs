using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// WALL_LAYERS_SPEC.md: "no walls" is the baseline; a wall layer switches on
// only for sides core reports as walled, resolved in list order.
public sealed class AttractionWallLayerTests
{
    private const float Ft = 0.3048f;
    private GameObject _root;

    [TearDown]
    public void TearDown()
    {
        AttractionPackingBaker.WallCombinationIssues.Clear();
        if (_root != null) Object.DestroyImmediate(_root);
    }

    [Test]
    public void NoWallLayers_WallsAreRecordedButNothingMoves()
    {
        AttractionTemplate attraction = CreateAttraction();
        PropTemplate prop = CreateProp("Prop", 1f);
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Front);

        Assert.That(prop.transform.localPosition, Is.EqualTo(new Vector3(1f, 0f, 0f)));
        Assert.That(attraction.Walls, Is.EqualTo(AttractionWallMask.Front));
        Assert.That(attraction.HasWall(AttractionWallSide.Front), Is.True);
        Assert.That(attraction.HasWallNamed("front"), Is.True);
        Assert.That(attraction.HasWallNamed("left"), Is.False);
        Assert.That(attraction.WallsWire, Is.EqualTo("front"));
    }

    [Test]
    public void WallLayer_ShowsExtrasOnlyWhenItsSideIsWalled()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate extra = CreateProp("Extra", 0f);
        extra.gameObject.SetActive(false);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Show(extra.transform), Move(extra.transform, new Vector3(0f, 0f, 2.5f))),
        });
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
        Assert.That(extra.gameObject.activeSelf, Is.False, "no walls: Base as authored");

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Front);
        Assert.That(extra.gameObject.activeSelf, Is.True);
        Assert.That(extra.transform.localPosition, Is.EqualTo(new Vector3(0f, 0f, 2.5f)));

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Back);
        Assert.That(extra.gameObject.activeSelf, Is.False, "another side's wall does not switch this layer on");

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
        Assert.That(extra.gameObject.activeSelf, Is.False);
        Assert.That(extra.transform.localPosition, Is.EqualTo(Vector3.zero), "the Base pose is restored");
    }

    [Test]
    public void SharedObject_GoesToTheFirstWalledLayerInListOrder()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate target = CreateProp("Target", 0f);
        target.gameObject.SetActive(false);
        var front = new Vector3(0f, 0f, 2.5f);
        var left = new Vector3(-2.5f, 0f, 0f);
        AttractionWallLayer frontLayer = Layer("wfront001", AttractionWallSide.Front, Show(target.transform), Move(target.transform, front));
        AttractionWallLayer leftLayer = Layer("wleft0001", AttractionWallSide.Left, Show(target.transform), Move(target.transform, left));
        attraction.SetWallLayers(new List<AttractionWallLayer> { frontLayer, leftLayer });
        AttractionPackingBaker.Bake(attraction, false);

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Front | AttractionWallMask.Left);
        Assert.That(target.transform.localPosition, Is.EqualTo(front), "both walled: the higher layer wins");
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Left);
        Assert.That(target.transform.localPosition, Is.EqualTo(left), "only Left walled: it moves to Left");
        Assert.That(target.gameObject.activeSelf, Is.True);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Right);
        Assert.That(target.gameObject.activeSelf, Is.False, "neither walled: Base");

        attraction.SetWallLayers(new List<AttractionWallLayer> { leftLayer, frontLayer });
        AttractionPackingBaker.Bake(attraction, false);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.Front | AttractionWallMask.Left);
        Assert.That(target.transform.localPosition, Is.EqualTo(left), "reordering the list changes the priority");
    }

    [Test]
    public void EverySideCombination_IsOrderIndependent()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate a = CreateProp("A", 1f);
        PropTemplate b = CreateProp("B", -1f);
        b.gameObject.SetActive(false);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Move(a.transform, new Vector3(1f, 0f, 2.5f))),
            Layer("wback0001", AttractionWallSide.Back, Show(b.transform), Move(b.transform, new Vector3(-1f, 0f, -2.5f))),
            Layer("wright001", AttractionWallSide.Right, Move(a.transform, new Vector3(2.5f, 0f, 0f))),
        });
        AttractionPackingBaker.Bake(attraction, false);

        var fresh = new Dictionary<int, string>();
        for (int mask = 0; mask < 16; mask++)
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, (AttractionWallMask)mask);
            fresh[mask] = Snapshot(a, b);
        }
        for (int mask = 0; mask < 16; mask++)
        {
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, (AttractionWallMask)(15 - mask));
            attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, (AttractionWallMask)mask);
            Assert.That(Snapshot(a, b), Is.EqualTo(fresh[mask]), $"mask {mask} depends on what was applied before it");
        }
        Assert.That(fresh[(int)(AttractionWallMask.Front | AttractionWallMask.Right)], Does.Contain("A(1.00,0.00,2.50)"),
            "Front is listed before Right, so it wins A");
    }

    [Test]
    public void AnchorToSide_KeepsDistanceFromTheEdgeAndRelativePlaceAlongIt()
    {
        var front = new AttractionWallLayer { side = AttractionWallSide.Front, authoredFootprintMeters = new Vector2(6f, 6f) };
        Vector3 p = AttractionTemplate.AnchorToSide(new Vector3(1.5f, 1f, 2.5f), front, new Vector2(8f, 4f));
        Assert.That(p.z, Is.EqualTo(1.5f).Within(1e-5f), "0.5 m from the front edge before and after");
        Assert.That(p.x, Is.EqualTo(2f).Within(1e-5f), "same relative place along the side");
        Assert.That(p.y, Is.EqualTo(1f), "height never changes");

        var right = new AttractionWallLayer { side = AttractionWallSide.Right, authoredFootprintMeters = new Vector2(6f, 6f) };
        Vector3 r = AttractionTemplate.AnchorToSide(new Vector3(2.5f, 0f, 1.5f), right, new Vector2(4f, 8f));
        Assert.That(r.x, Is.EqualTo(1.5f).Within(1e-5f));
        Assert.That(r.z, Is.EqualTo(2f).Within(1e-5f));

        Vector3 same = AttractionTemplate.AnchorToSide(new Vector3(1f, 0f, 2f), front, new Vector2(6f, 6f));
        Assert.That(same, Is.EqualTo(new Vector3(1f, 0f, 2f)), "authored size is the identity");
    }

    [Test]
    public void WallLayer_FollowsTheAppliedFrameFootprint()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate extra = CreateProp("Extra", 0f);
        extra.gameObject.SetActive(false);
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            new AttractionLayoutFrame { id = "fsmall001", name = "Small", kind = AttractionLayoutFrameKind.Custom, sizeFeet = new Vector2(10f, 10f) },
        });
        AttractionWallLayer layer = Layer("wfront001", AttractionWallSide.Front,
            Show(extra.transform), Move(extra.transform, new Vector3(0f, 0f, 2.9f)));
        attraction.SetWallLayers(new List<AttractionWallLayer> { layer });
        AttractionPackingBaker.Bake(attraction, false);

        attraction.ApplyLayout("fsmall001", Vector2.one, AttractionWallMask.Front);

        float baseHalf = 20f * Ft * 0.5f, smallHalf = 10f * Ft * 0.5f;
        Assert.That(extra.gameObject.activeSelf, Is.True);
        Assert.That(extra.transform.localPosition.z, Is.EqualTo(2.9f - (baseHalf - smallHalf)).Within(1e-4f),
            "it stays the same distance from the front edge of the smaller frame");
    }

    [Test]
    public void WallsApplied_FiresOnlyWhenTheWallsChange()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate extra = CreateProp("Extra", 0f);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Move(extra.transform, new Vector3(0f, 0f, 2f))),
        });
        AttractionPackingBaker.Bake(attraction, false);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
        int fired = 0;
        attraction.WallsApplied += _ => fired++;

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one, AttractionWallMask.None);
        Assert.That(fired, Is.EqualTo(0));
        attraction.SetWalls(AttractionWallMask.Front);
        Assert.That(fired, Is.EqualTo(1));
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        Assert.That(fired, Is.EqualTo(1), "re-applying the layout keeps the walls and does not re-fire");
        Assert.That(attraction.Walls, Is.EqualTo(AttractionWallMask.Front));
        attraction.SetWalls(AttractionWallMask.None);
        Assert.That(fired, Is.EqualTo(2));
    }

    [Test]
    public void Bake_FramelessWallAttractionSkipsWallOnlyExtrasAndRecordsBaseStates()
    {
        AttractionTemplate attraction = CreateAttraction();
        CreateProp("Game", 0f);
        PropTemplate extra = CreateProp("Extra", 3f);
        extra.gameObject.SetActive(false);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Show(extra.transform)),
        });

        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);

        Assert.That(attraction.PackingBake.Props.Count, Is.EqualTo(1), "a wall-only extra never sizes Base");
        bool recorded = false;
        foreach (AttractionLayoutBaseState state in attraction.LayoutBaseStates)
            if (state.target == extra.transform) recorded = !state.active;
        Assert.That(recorded, Is.True, "the extra's Base state is recorded so it can be restored");
    }

    [Test]
    public void CombinationCheck_ReportsOnlyNewOverlaps()
    {
        AttractionTemplate attraction = CreateAttraction();
        Transform table = Cube("Table", new Vector3(0f, 0.5f, 2f));
        Transform poster = Cube("Poster", new Vector3(-2f, 0.5f, -2f));
        poster.gameObject.SetActive(false);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Show(poster), Move(poster, new Vector3(0f, 0.5f, 2.2f))),
            Layer("wleft0001", AttractionWallSide.Left, Move(table, new Vector3(-2.5f, 0.5f, 0f))),
        });
        AttractionPackingBaker.Bake(attraction, false);

        List<string> issues = AttractionPackingBaker.FindWallCombinationIssues(attraction);

        Assert.That(issues.Count, Is.EqualTo(1), string.Join("\n", issues));
        Assert.That(issues[0], Does.Contain("Poster").And.Contain("Table").And.Contain("front"));
        Assert.That(table.localPosition, Is.EqualTo(new Vector3(0f, 0.5f, 2f)), "the check restores the hierarchy");
        Assert.That(poster.gameObject.activeSelf, Is.False);
    }

    [Test]
    public void CombinationCheck_StopsAtItsTimeBudgetAndRestoresTheHierarchy()
    {
        AttractionTemplate attraction = CreateAttraction();
        Transform table = Cube("Table", new Vector3(0f, 0.5f, 2f));
        Transform poster = Cube("Poster", new Vector3(-2f, 0.5f, -2f));
        poster.gameObject.SetActive(false);
        attraction.SetWallLayers(new List<AttractionWallLayer>
        {
            Layer("wfront001", AttractionWallSide.Front, Show(poster), Move(poster, new Vector3(0f, 0.5f, 2.2f))),
            Layer("wleft0001", AttractionWallSide.Left, Move(table, new Vector3(-2.5f, 0.5f, 0f))),
        });
        AttractionPackingBaker.Bake(attraction, false);

        double budget = AttractionPackingBaker.WallCheckBudgetSeconds;
        List<string> issues;
        try
        {
            AttractionPackingBaker.WallCheckBudgetSeconds = -1d;   // already over budget
            issues = AttractionPackingBaker.FindWallCombinationIssues(attraction);
        }
        finally
        {
            AttractionPackingBaker.WallCheckBudgetSeconds = budget;
        }

        Assert.That(issues.Count, Is.EqualTo(1), string.Join("\n", issues));
        Assert.That(issues[0], Does.Contain("wall check stopped").And.Contain("0 of 4"));
        Assert.That(table.localPosition, Is.EqualTo(new Vector3(0f, 0.5f, 2f)), "the check restores the hierarchy");
        Assert.That(poster.gameObject.activeSelf, Is.False);
    }

    [Test]
    public void Wire_RoundTripsAndIgnoresUnknownTokens()
    {
        Assert.That(AttractionWallSides.ToWire(AttractionWallMask.Left | AttractionWallMask.Front), Is.EqualTo("front,left"));
        Assert.That(AttractionWallSides.ToWire(AttractionWallMask.None), Is.EqualTo(""));
        Assert.That(AttractionWallSides.FromWire("left, FRONT, ceiling"), Is.EqualTo(AttractionWallMask.Front | AttractionWallMask.Left));
        Assert.That(AttractionWallSides.FromWire("+x,-z"), Is.EqualTo(AttractionWallMask.Right | AttractionWallMask.Back));
        Assert.That(AttractionWallSides.FromWire(null), Is.EqualTo(AttractionWallMask.None));
        Assert.That(AttractionWallSides.TryFromLocalDirection(new Vector3(0.0001f, 0f, -1f), out AttractionWallSide side), Is.True);
        Assert.That(side, Is.EqualTo(AttractionWallSide.Back));
        Assert.That(AttractionWallSides.TryFromLocalDirection(new Vector3(1f, 0f, 1f), out _), Is.False);
    }

    // ------------------------------------------------------------------ helpers

    private static string Snapshot(params PropTemplate[] props)
    {
        var parts = new List<string>();
        foreach (PropTemplate p in props)
        {
            Vector3 v = p.transform.localPosition;
            parts.Add($"{p.name}({v.x:0.00},{v.y:0.00},{v.z:0.00}){(p.gameObject.activeSelf ? "+" : "-")}");
        }
        return string.Join(" ", parts);
    }

    private static AttractionWallLayer Layer(string id, AttractionWallSide side, params AttractionLayoutOverride[] overrides)
    {
        var layer = new AttractionWallLayer
        {
            id = id,
            side = side,
            authoredFootprintMeters = new Vector2(20f, 20f) * Ft,
        };
        layer.overrides.AddRange(overrides);
        return layer;
    }

    private AttractionTemplate CreateAttraction()
    {
        _root = new GameObject("Attraction");
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

    private Transform Cube(string name, Vector3 localPosition)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = localPosition;
        return go.transform;
    }

    private AttractionLayoutOverride Show(Transform target) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setActive = true,
        active = true,
    };

    private AttractionLayoutOverride Move(Transform target, Vector3 localPosition) => new AttractionLayoutOverride
    {
        path = AttractionPackingBaker.PathFrom(_root.transform, target),
        target = target,
        setPosition = true,
        localPosition = localPosition,
    };
}
