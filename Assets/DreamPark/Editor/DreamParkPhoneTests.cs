using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// Phone players: DreamParkDevice defaults / Set / SimulatePhone, and
// DreamParkGestures routing (raycast, queue, Enabled). In-memory scene
// objects only. The Lua half (dp.on_tap & co.) is covered by
// Tests~/lua/gestures_harness.lua.
public sealed class DreamParkDeviceTests
{
    bool _simulate;

    [SetUp]
    public void SetUp()
    {
        _simulate = DreamParkDevice.SimulatePhone;
        DreamParkDevice.SimulatePhone = false;
        DreamParkDevice.ResetToDefault();
    }

    [TearDown]
    public void TearDown()
    {
        DreamParkDevice.ResetToDefault();
        DreamParkDevice.SimulatePhone = _simulate;
    }

    [Test]
    public void DefaultsToEditorWhichBehavesAsAHeadset()
    {
        Assert.That(DreamParkDevice.Current, Is.EqualTo(DreamParkDeviceKind.Editor));
        Assert.That(DreamParkDevice.IsHeadset, Is.True);
        Assert.That(DreamParkDevice.IsPhone, Is.False);
        Assert.That(DreamParkDevice.PeerWire, Is.EqualTo("headset"));
    }

    [Test]
    public void SetOverridesTheDefault()
    {
        DreamParkDevice.Set(DreamParkDeviceKind.Phone);
        Assert.That(DreamParkDevice.Current, Is.EqualTo(DreamParkDeviceKind.Phone));
        Assert.That(DreamParkDevice.IsPhone, Is.True);
        Assert.That(DreamParkDevice.IsHeadset, Is.False);

        DreamParkDevice.Set(DreamParkDeviceKind.Headset);
        Assert.That(DreamParkDevice.Current, Is.EqualTo(DreamParkDeviceKind.Headset));
        Assert.That(DreamParkDevice.IsHeadset, Is.True);

        DreamParkDevice.ResetToDefault();
        Assert.That(DreamParkDevice.Current, Is.EqualTo(DreamParkDeviceKind.Editor));
    }

    [Test]
    public void SimulatePhoneMakesTheEditorAPhone()
    {
        DreamParkDevice.SimulatePhone = true;
        Assert.That(DreamParkDevice.Current, Is.EqualTo(DreamParkDeviceKind.Phone));
        Assert.That(DreamParkDevice.IsPhone, Is.True);
        Assert.That(DreamParkDevice.IsHeadset, Is.False);
        Assert.That(DreamParkDevice.PeerWire, Is.EqualTo("phone"));
        // Even over a host's Set(Headset) — the toggle is an explicit request.
        DreamParkDevice.Set(DreamParkDeviceKind.Headset);
        Assert.That(DreamParkDevice.IsPhone, Is.True);
    }

    [Test]
    public void WireNames()
    {
        Assert.That(DreamParkDevice.Wire(DreamParkDeviceKind.Phone), Is.EqualTo("phone"));
        Assert.That(DreamParkDevice.Wire(DreamParkDeviceKind.Headset), Is.EqualTo("headset"));
        Assert.That(DreamParkDevice.Wire(DreamParkDeviceKind.Editor), Is.EqualTo("editor"));
        Assert.That((int)DreamParkDeviceKind.Headset, Is.EqualTo(0));
        Assert.That((int)DreamParkDeviceKind.Phone, Is.EqualTo(1));
        Assert.That((int)DreamParkDeviceKind.Editor, Is.EqualTo(2));
    }
}

public sealed class DreamParkGestureRoutingTests
{
    readonly List<Object> _created = new List<Object>();
    readonly List<DreamParkRoutedGesture> _routed = new List<DreamParkRoutedGesture>();
    Camera _camera;

    [SetUp]
    public void SetUp()
    {
        DreamParkGestures.Enabled = true;
        DreamParkGestures.ClearPending();
        _routed.Clear();
        DreamParkGestures.Routed += OnRouted;

        var camGo = new GameObject("PhoneTestCamera");
        _created.Add(camGo);
        _camera = camGo.AddComponent<Camera>();
        camGo.transform.SetPositionAndRotation(new Vector3(0f, 1000f, 0f), Quaternion.identity);   // looking +Z, away from any scene content
        DreamParkGestures.RaycastCamera = _camera;
    }

    [TearDown]
    public void TearDown()
    {
        DreamParkGestures.Routed -= OnRouted;
        DreamParkGestures.RaycastCamera = null;
        DreamParkGestures.Enabled = true;
        DreamParkGestures.ClearPending();
        foreach (var o in _created) if (o != null) Object.DestroyImmediate(o);
        _created.Clear();
    }

    void OnRouted(DreamParkRoutedGesture g) => _routed.Add(g);

    GameObject Cube(string name, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        _created.Add(go);
        return go;
    }

    static DreamParkGesture Tap(float x = 0.5f, float y = 0.5f) => new DreamParkGesture
    {
        kind = DreamParkGestureKind.Tap,
        screen = new Vector2(x, y),
    };

    [Test]
    public void TapThroughTheCenterHitsTheColliderInFront()
    {
        var target = Cube("Target", _camera.transform.position + new Vector3(0f, 0f, 5f));
        Physics.SyncTransforms();

        DreamParkGestures.Submit(Tap());

        Assert.That(_routed.Count, Is.EqualTo(1));
        var r = _routed[0];
        Assert.That(r.hasHit, Is.True);
        Assert.That(r.hitObject, Is.SameAs(target));
        Assert.That(r.position.z, Is.EqualTo(_camera.transform.position.z + 4.5f).Within(0.01f));
        Assert.That(r.normal.z, Is.EqualTo(-1f).Within(0.01f));
        Assert.That(r.distance, Is.EqualTo(4.5f).Within(0.01f));
        Assert.That(r.KindName, Is.EqualTo("tap"));
        Assert.That(r.gesture.pinchScale, Is.EqualTo(1f), "a default gesture reports no scale change");

        Assert.That(DreamParkGestures.PendingCount, Is.EqualTo(1), "queued for dp.on_tap");
        var drained = DreamParkGestures.DrainPending();
        Assert.That(drained[0].hitObject, Is.SameAs(target));
        Assert.That(DreamParkGestures.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void TapIntoNothingRoutesWithoutAHit()
    {
        Physics.SyncTransforms();
        DreamParkGestures.Submit(Tap());
        Assert.That(_routed.Count, Is.EqualTo(1));
        Assert.That(_routed[0].hasHit, Is.False);
        Assert.That(_routed[0].hitObject, Is.Null);
    }

    [Test]
    public void IgnoreRaycastLayerAndTheLocalRigAreSeeThrough()
    {
        Vector3 origin = _camera.transform.position;
        var ignored = Cube("Ignored", origin + new Vector3(0f, 0f, 3f));
        ignored.layer = LayerMask.NameToLayer("Ignore Raycast");
        var rigBody = Cube("RigBody", origin + new Vector3(0f, 0f, 4f));
        var rig = new GameObject("PhoneRig");
        _created.Add(rig);
        rig.AddComponent<PlayerRig>();   // edit mode: no Awake, so nothing registers
        rigBody.transform.SetParent(rig.transform, true);
        var target = Cube("Behind", origin + new Vector3(0f, 0f, 6f));
        Physics.SyncTransforms();

        DreamParkGestures.Submit(Tap());

        Assert.That(_routed[0].hitObject, Is.SameAs(target));
    }

    [Test]
    public void DisabledDropsEverything()
    {
        Cube("Target", _camera.transform.position + new Vector3(0f, 0f, 5f));
        Physics.SyncTransforms();
        DreamParkGestures.Enabled = false;

        DreamParkGestures.Submit(Tap());
        DreamParkGestures.Submit(new DreamParkGesture { kind = DreamParkGestureKind.Pinch, pinchScale = 1.5f });

        Assert.That(_routed, Is.Empty);
        Assert.That(DreamParkGestures.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void QueueIsBounded()
    {
        for (int i = 0; i < DreamParkGestures.MaxPending + 10; i++)
            DreamParkGestures.Submit(new DreamParkGesture { kind = DreamParkGestureKind.DragMove, screen = new Vector2(0.5f, 0.5f) });
        Assert.That(DreamParkGestures.PendingCount, Is.EqualTo(DreamParkGestures.MaxPending));
    }

    [Test]
    public void KindAndPhaseNames()
    {
        Assert.That(DreamParkGestures.KindName(DreamParkGestureKind.TouchDown), Is.EqualTo("touch"));
        Assert.That(DreamParkGestures.PhaseName(DreamParkGestureKind.TouchDown), Is.EqualTo("down"));
        Assert.That(DreamParkGestures.PhaseName(DreamParkGestureKind.TouchUp), Is.EqualTo("up"));
        Assert.That(DreamParkGestures.KindName(DreamParkGestureKind.DragMove), Is.EqualTo("drag"));
        Assert.That(DreamParkGestures.PhaseName(DreamParkGestureKind.DragBegin), Is.EqualTo("begin"));
        Assert.That(DreamParkGestures.PhaseName(DreamParkGestureKind.DragEnd), Is.EqualTo("end"));
        Assert.That(DreamParkGestures.KindName(DreamParkGestureKind.Pinch), Is.EqualTo("pinch"));
    }
}
