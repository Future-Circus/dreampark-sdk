#if UNITY_EDITOR
using System.Reflection;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using XLua;

/// <summary>
/// Portal opening crossers (PortalScene.AddCrosser / dp.portal_cross, Nov 2026 sdk-crossers): where a crosser is relative to
/// its opening decides what the room draws and whether the clip goes into its property block. The opening here is a plain
/// transform on the plane z = 3 whose +Z points into the room (the room is z &lt; 3).
/// </summary>
public sealed class PortalCrossersTests
{
    static readonly int IdCross = Shader.PropertyToID("_DPCross");
    static readonly int IdPlane = Shader.PropertyToID("_DPCrossPlane");
    GameObject _root;
    PortalScene _sc;
    Transform _opening;

    [SetUp]
    public void Make()
    {
        _root = new GameObject("PXT_Root");
        var scene = new GameObject("Scene");
        scene.transform.SetParent(_root.transform, false);
        GameObject far = GameObject.CreatePrimitive(PrimitiveType.Cube);
        far.transform.SetParent(scene.transform, false);
        far.transform.localPosition = new Vector3(0f, 0f, 20f);
        _sc = scene.AddComponent<PortalScene>();
        _opening = new GameObject("Opening").transform;
        _opening.SetParent(_root.transform, false);
        _opening.SetPositionAndRotation(new Vector3(0f, 1f, 3f), Quaternion.LookRotation(Vector3.back));   // +Z into the room
    }

    [TearDown]
    public void Kill() { Object.DestroyImmediate(_root); }

    Renderer Crosser(float z)
    {
        GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cube);
        c.name = "Crosser";
        c.transform.SetParent(_root.transform, false);
        c.transform.localPosition = new Vector3(0f, 1f, z);
        c.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
        return c.GetComponent<Renderer>();
    }

    static Vector4 Block(Renderer r, int id)
    {
        if (!r.HasPropertyBlock()) return Vector4.zero;
        var b = new MaterialPropertyBlock();
        r.GetPropertyBlock(b);
        return b.GetVector(id);
    }

    [Test]
    public void WhollyInTheRoom_DrawnAsAuthored_NoBlock()
    {
        Renderer r = Crosser(0f);
        _sc.AddCrosser(r.transform, _opening);
        _sc.Refresh();      // (edit mode: the scene never got OnEnable, so AddCrosser leaves the refresh to us)
        Assert.That(_sc.IsCrossing(r.transform), Is.True);
        Assert.That(r.forceRenderingOff, Is.False);
        Assert.That(r.HasPropertyBlock(), Is.False, "no clip, no block: the renderer stays SRP-batched");
    }

    [Test]
    public void Straddling_RoomKeepsTheRoomSide()
    {
        Renderer r = Crosser(3f);
        _sc.AddCrosser(r.transform, _opening);
        _sc.Refresh();      // (edit mode: the scene never got OnEnable, so AddCrosser leaves the refresh to us)
        Assert.That(r.forceRenderingOff, Is.False);
        Vector4 c = Block(r, IdCross);
        Assert.That(c.x, Is.EqualTo(1f), "clip on");
        Assert.That(c.y, Is.EqualTo(0f), "the room's cameras never map");
        Vector4 p = Block(r, IdPlane);
        // the room point (0, 1, 0) is kept (>= 0), the far point (0, 1, 6) is clipped (< 0)
        Assert.That(p.x * 0f + p.y * 1f + p.z * 0f + p.w, Is.GreaterThan(0f));
        Assert.That(p.y * 1f + p.z * 6f + p.w, Is.LessThan(0f));
    }

    [Test]
    public void WhollyBeyond_HiddenFromTheRoom_AndBackWhenRemoved()
    {
        Renderer r = Crosser(6f);
        _sc.AddCrosser(r.transform, _opening);
        _sc.Refresh();      // (edit mode: the scene never got OnEnable, so AddCrosser leaves the refresh to us)
        Assert.That(r.forceRenderingOff, Is.True, "only the portal draws it");
        Assert.That(_sc.RemoveCrosser(r.transform), Is.True);
        _sc.Refresh();
        Assert.That(r.forceRenderingOff, Is.False, "back in the room as authored");
        Assert.That(r.HasPropertyBlock(), Is.False);
    }

    [Test]
    public void LeavingTheOpening_ClearsTheClip_KeepsItsOwnBlock()
    {
        Renderer r = Crosser(3f);
        var own = new MaterialPropertyBlock();
        own.SetColor("_baseColor", Color.red);
        r.SetPropertyBlock(own);
        _sc.AddCrosser(r.transform, _opening);
        _sc.Refresh();      // (edit mode: the scene never got OnEnable, so AddCrosser leaves the refresh to us)
        Assert.That(Block(r, IdCross).x, Is.EqualTo(1f));
        r.transform.localPosition = new Vector3(0f, 1f, 0f);
        _sc.Refresh();
        Assert.That(Block(r, IdCross), Is.EqualTo(Vector4.zero));
        var b = new MaterialPropertyBlock();
        r.GetPropertyBlock(b);
        Assert.That(b.GetColor("_baseColor"), Is.EqualTo(Color.red), "the crosser's own values survive");
    }

    [Test]
    public void OneOpeningAtATime()
    {
        Renderer r = Crosser(3f);
        Transform other = new GameObject("Other").transform;
        other.SetParent(_root.transform, false);
        _sc.AddCrosser(r.transform, _opening);
        _sc.Refresh();      // (edit mode: the scene never got OnEnable, so AddCrosser leaves the refresh to us)
        _sc.AddCrosser(r.transform, other);
        Assert.That(_sc.openingCrossers.Count, Is.EqualTo(1));
        Assert.That(_sc.openingCrossers[0].opening, Is.EqualTo(other));
    }

    [Test]
    public void SceneContentIsNeverACrosser()
    {
        Renderer member = _sc.GetComponentInChildren<Renderer>();
        _sc.AddCrosser(member.transform, _opening);
        _sc.Refresh();
        Assert.That(member.forceRenderingOff, Is.True, "still a scene member: drawn only by portals");
        Assert.That(member.HasPropertyBlock(), Is.False);
    }

    [Test]
    public void Lua_PortalCross()
    {
        LuaEnv env = LuaBehaviour.GetLuaEnv();
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        Renderer r = Crosser(3f);
        env.Global.Set("__px_go", r.gameObject);
        env.Global.Set("__px_open", _opening.gameObject);
        env.Global.Set("__px_scene", _sc.gameObject);
        object[] on = env.DoString("return dp.portal_cross(__px_go.transform, __px_open, true, __px_scene)");
        Assert.That(on[0], Is.EqualTo(true));
        _sc.Refresh();
        Assert.That(_sc.IsCrossing(r.transform), Is.True);
        Assert.That(Block(r, IdCross).x, Is.EqualTo(1f));
        // (off leaves the openings of every ACTIVE scene; edit mode has none, so drop it here directly)
        Assert.That(_sc.RemoveCrosser(r.transform), Is.True);
        Assert.That(env.DoString("return dp.portal_cross(nil, __px_open)")[0], Is.EqualTo(false));
        env.Global.Set("__px_go", (object)null); env.Global.Set("__px_open", (object)null); env.Global.Set("__px_scene", (object)null);
    }
}
#endif
