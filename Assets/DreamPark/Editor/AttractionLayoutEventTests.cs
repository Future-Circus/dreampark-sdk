using System.Collections.Generic;
using System.Reflection;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using XLua;
using Object = UnityEngine.Object;

// LayoutLab REPORT §5.2: there was no layout-change event (onwalls fires only when the
// walls change). AttractionTemplate.LayoutApplied + Lua onlayout(frame_id, scale, footprint).
public sealed class AttractionLayoutEventTests
{
    private GameObject _root;

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
    }

    private AttractionTemplate CreateFramedAttraction()
    {
        _root = new GameObject("Attraction");
        var attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        var go = new GameObject("Game");
        go.transform.SetParent(_root.transform, false);
        var prop = go.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame>
        {
            new AttractionLayoutFrame { id = "fsmall001", name = "Small", kind = AttractionLayoutFrameKind.Custom, sizeFeet = new Vector2(10f, 10f) },
        });
        AttractionPackingBaker.Bake(attraction, false);
        return attraction;
    }

    [Test]
    public void LayoutAppliedFiresWhenFrameScaleOrFootprintChangesAndOnlyThen()
    {
        var attraction = CreateFramedAttraction();
        var seen = new List<string>();
        attraction.LayoutApplied += a => seen.Add(a.CurrentFrameId + "@" + a.CurrentFrameScale.x.ToString("F2"));

        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);   // no change: no event
        attraction.ApplyLayout("fsmall001", Vector2.one);
        attraction.ApplyLayout("fsmall001", new Vector2(0.9f, 0.9f));
        attraction.SetWalls(AttractionWallMask.Front);                          // walls only: layout unchanged

        Assert.That(seen, Is.EqualTo(new[] { "base@1.00", "fsmall001@1.00", "fsmall001@0.90" }));
    }

    [Test]
    public void LuaOnLayoutGetsFrameScaleAndFootprintOnBindAndOnChange()
    {
        var attraction = CreateFramedAttraction();
        attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one);

        var scriptGo = new GameObject("Script");
        scriptGo.transform.SetParent(_root.transform, false);
        var lb = scriptGo.AddComponent<LuaBehaviour>();
        var env = LuaBehaviour.GetLuaEnv();
        env.DoString(@"
__onlayout_log = {}
function __onlayout_probe(frame_id, scale, footprint)
    __onlayout_log[#__onlayout_log + 1] = string.format('%s %.2f %.2f', frame_id, scale.x, footprint.x)
end");
        var fn = env.Global.Get<LuaFunction>("__onlayout_probe");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LuaBehaviour).GetField("luaOnLayout", flags).SetValue(lb, fn);
        typeof(LuaBehaviour).GetMethod("BindLayout", flags).Invoke(lb, null);   // what start() does

        attraction.ApplyLayout("fsmall001", Vector2.one);

        var log = env.Global.Get<LuaTable>("__onlayout_log");
        Assert.That(log.Length, Is.EqualTo(2));
        float baseW = 20f * 0.3048f, smallW = 10f * 0.3048f;
        Assert.That(log.Get<int, string>(1), Is.EqualTo(string.Format("base 1.00 {0:F2}", baseW)), "sticky: current layout on bind");
        Assert.That(log.Get<int, string>(2), Is.EqualTo(string.Format("fsmall001 1.00 {0:F2}", smallW)));
        typeof(LuaBehaviour).GetField("luaOnLayout", flags).SetValue(lb, null);
        fn.Dispose();
    }
}
