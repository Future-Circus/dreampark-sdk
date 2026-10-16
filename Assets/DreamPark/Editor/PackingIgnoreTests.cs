using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// LayoutLab REPORT §5.6: a debug overlay was kept out of the packing bake only because its
// name contained "gizmo". Explicit conventions now: "[nopack]" in the name, or a LuaBehaviour
// whose script carries "-- @packing-ignore". No component.
public sealed class PackingIgnoreTests
{
    private GameObject _root;
    private TextAsset _script;

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
        if (_script != null) Object.DestroyImmediate(_script);
    }

    private Vector2 BakeWithOverlay(string overlayName, string luaSource)
    {
        if (_root != null) Object.DestroyImmediate(_root);
        _root = new GameObject("Attraction");
        var attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        var game = new GameObject("Game");
        game.transform.SetParent(_root.transform, false);
        var prop = game.AddComponent<PropTemplate>();
        prop.useColliderBounds = false;
        prop.customFootprintMeters = Vector2.one;

        // A big flat overlay mesh (LayoutLab draws the footprint like this).
        var overlay = GameObject.CreatePrimitive(PrimitiveType.Cube);
        overlay.name = overlayName;
        Object.DestroyImmediate(overlay.GetComponent<Collider>());
        overlay.transform.SetParent(_root.transform, false);
        overlay.transform.localScale = new Vector3(5f, 0.01f, 5f);
        if (luaSource != null)
        {
            var lb = overlay.AddComponent<LuaBehaviour>();
            _script = new TextAsset(luaSource);
            lb.luaScript = _script;
        }
        Assert.That(AttractionPackingBaker.Bake(attraction, false), Is.True);
        return attraction.PackingBake.ShrinkFootprintMeters;
    }

    [Test]
    public void AnUnmarkedOverlayInflatesTheBake()
    {
        Vector2 f = BakeWithOverlay("Layout Overlay", null);
        Assert.That(f.x, Is.GreaterThan(4f), "control: the overlay is measured (the bug the markers avoid)");
    }

    [Test]
    public void NoPackNameTokenKeepsAnOverlayOutOfTheBake()
    {
        Vector2 f = BakeWithOverlay("Layout Overlay [NoPack]", null);
        Assert.That(f.x, Is.EqualTo(1f).Within(0.05f));
        Assert.That(f.y, Is.EqualTo(1f).Within(0.05f));
    }

    [Test]
    public void LuaPackingIgnoreAnnotationKeepsAnOverlayOutOfTheBake()
    {
        Vector2 f = BakeWithOverlay("Layout Overlay", "-- @packing-ignore\nfunction start() end\n");
        Assert.That(f.x, Is.EqualTo(1f).Within(0.05f));
    }

    [Test]
    public void TheAnnotationMustBeAComment()
    {
        Assert.That(PackingIgnore.ScriptHasMarker("local s = '@packing-ignore'"), Is.False);
        Assert.That(PackingIgnore.ScriptHasMarker("x = 1\n  -- draws the layout. @packing-ignore\n"), Is.True);
        Assert.That(PackingIgnore.ScriptHasMarker(null), Is.False);
    }
}
