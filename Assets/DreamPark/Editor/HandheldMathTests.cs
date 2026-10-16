using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// HandheldMath + Handhelds (dp.hold): the device wrist-frame mount for both sides, Grip
// offsets, flipVisual, RemoteRig active-hand switching, the Editor anchor frame and the
// validation the pre-upload check uses. Objects live in a preview scene, so the open scene
// is never dirtied (run through Tools/Builders without the Test Runner, or the Test Runner).
public sealed class HandheldMathTests
{
    Scene _scene;
    readonly List<GameObject> _made = new List<GameObject>();
    const float Eps = 1e-4f;

    [SetUp]
    public void SetUp()
    {
        _scene = EditorSceneManager.NewPreviewScene();
        Handhelds.ClearAll();
    }

    [TearDown]
    public void TearDown()
    {
        Handhelds.ClearAll();
        foreach (var go in _made) if (go != null) Object.DestroyImmediate(go);
        _made.Clear();
        EditorSceneManager.ClosePreviewScene(_scene);
    }

    GameObject Make(string name, Transform parent = null)
    {
        var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
        if (parent == null) SceneManager.MoveGameObjectToScene(go, _scene);
        else go.transform.SetParent(parent, false);
        _made.Add(go);
        return go;
    }

    Transform Node(string name = "Hand")
    {
        var n = Make(name).transform;
        n.SetPositionAndRotation(new Vector3(0.3f, 1.2f, -0.4f), Quaternion.Euler(23f, -71f, 38f));
        return n;
    }

    // A prop authored +Z forward, +Y up, Grip on the handle, Muzzle at the tip.
    Transform Prop(Transform parent, Vector3 gripPos, Quaternion gripRot)
    {
        var p = Make("Blaster", parent).transform;
        var g = Make(HandheldMath.GripName, p).transform;
        g.localPosition = gripPos;
        g.localRotation = gripRot;
        var m = Make(HandheldMath.MuzzleName, p).transform;
        m.localPosition = new Vector3(0f, 0.04f, 0.18f);
        return p;
    }

    static Vector3 Local(Transform node, Vector3 dir) => Quaternion.Inverse(node.rotation) * dir;

    static void Near(Vector3 a, Vector3 b, float eps, string what)
        => Assert.That((a - b).magnitude, Is.LessThan(eps), $"{what}: {a.ToString("F4")} vs {b.ToString("F4")}");

    [Test]
    public void RecipeNumbersMatchHandheldDoc()
    {
        HandheldMath.MountPose(false, Vector3.zero, Quaternion.identity, out var pr, out var rr);
        Near(pr, new Vector3(-0.007f, -0.033f, 0.081f), Eps, "right fist");
        Assert.That(Quaternion.Angle(rr, Quaternion.Euler(0, 0, 90)), Is.LessThan(0.01f));
        HandheldMath.MountPose(true, Vector3.zero, Quaternion.identity, out var pl, out var rl);
        Near(pl, new Vector3(0.007f, -0.033f, 0.081f), Eps, "left fist");
        Assert.That(Quaternion.Angle(rl, Quaternion.Euler(0, 0, -90)), Is.LessThan(0.01f));
    }

    [Test]
    public void RightHandPutsGripInFistTopOnThumbBarrelAlongFingers()
    {
        var node = Node();
        var prop = Prop(node, new Vector3(0f, -0.05f, -0.02f), Quaternion.identity);
        HandheldMath.Mount(prop, node, left: false);
        var grip = prop.Find(HandheldMath.GripName);
        Near(Quaternion.Inverse(node.rotation) * (grip.position - node.position), HandheldMath.FistCentre(false), Eps, "grip");
        Near(Local(node, prop.forward), Vector3.forward, Eps, "forward");
        Near(Local(node, prop.up), Vector3.left, Eps, "up = thumb (−X)");
        HandheldMath.MeasureMount(prop, node, false, null, out float dist, out float ang);
        Assert.That(dist, Is.LessThan(Eps));
        Assert.That(ang, Is.LessThan(0.05f));
    }

    [Test]
    public void LeftHandIsARotationNotAMirror()
    {
        var node = Node();
        var prop = Prop(node, new Vector3(0f, -0.05f, -0.02f), Quaternion.identity);
        HandheldMath.Mount(prop, node, left: true);
        var grip = prop.Find(HandheldMath.GripName);
        Near(Quaternion.Inverse(node.rotation) * (grip.position - node.position), HandheldMath.FistCentre(true), Eps, "grip");
        Near(Local(node, prop.forward), Vector3.forward, Eps, "forward");
        Near(Local(node, prop.up), Vector3.right, Eps, "up = thumb (+X)");
        Assert.That(prop.lossyScale.x, Is.GreaterThan(0f), "never a negative scale");
    }

    [Test]
    public void RotatedNestedGripLandsOnTheFistFrame()
    {
        var node = Node();
        var prop = Prop(node, Vector3.zero, Quaternion.identity);
        var grip = prop.Find(HandheldMath.GripName);
        var model = Make(HandheldMath.ModelName, prop).transform;
        model.localPosition = new Vector3(0.01f, -0.02f, 0.005f);
        model.localRotation = Quaternion.Euler(0f, 0f, 15f);
        grip.SetParent(model, false);
        grip.localPosition = new Vector3(0.003f, -0.012f, -0.004f);
        grip.localRotation = Quaternion.Euler(-12f, 4f, 0f);
        foreach (bool left in new[] { false, true })
        {
            HandheldMath.Mount(prop, node, left);
            Near(Quaternion.Inverse(node.rotation) * (grip.position - node.position), HandheldMath.FistCentre(left), 1e-3f, "grip " + left);
            Assert.That(Quaternion.Angle(Quaternion.Inverse(node.rotation) * grip.rotation, HandheldMath.HandFrame(left)), Is.LessThan(0.05f), "grip frame " + left);
        }
    }

    [Test]
    public void FlipVisualTrackerMountsInTheRawFrameUnderneath()
    {
        var node = Node("LeftHand");
        var tracker = node.gameObject.AddComponent<HandTracker>();
        tracker.handPreference = HandTracker.HandPreference.Left;
        tracker.flipVisual = true;
        tracker.flipVisualRotation = new Vector3(180f, 180f, 0f);
        tracker.flipVisualPosition = new Vector3(0.01f, 0f, -0.02f);
        Assert.That(HandheldMath.TrackerIsLeft(tracker), Is.True);
        var prop = Prop(node, new Vector3(0f, -0.04f, 0f), Quaternion.identity);
        HandheldMath.Mount(prop, node, left: true, tracker);
        // the raw wrist frame under the tracker: node = raw * TRS(flipPos, flipRot)
        Quaternion fr = Quaternion.Euler(tracker.flipVisualRotation);
        Quaternion rawRot = node.rotation * Quaternion.Inverse(fr);
        Vector3 rawPos = node.position - rawRot * tracker.flipVisualPosition;
        var grip = prop.Find(HandheldMath.GripName);
        Near(Quaternion.Inverse(rawRot) * (grip.position - rawPos), HandheldMath.FistCentre(true), Eps, "grip in raw");
        Near(Quaternion.Inverse(rawRot) * prop.up, Vector3.right, Eps, "thumb in raw");
        Near(Quaternion.Inverse(rawRot) * prop.forward, Vector3.forward, Eps, "fingers in raw");
        HandheldMath.MeasureMount(prop, node, true, tracker, out float dist, out float ang);
        Assert.That(dist, Is.LessThan(Eps));
        Assert.That(ang, Is.LessThan(0.05f));
    }

    // The reference: handheld_mount.lua.txt mount(side), transcribed line by line.
    static void LuaMount(Transform tf, Transform node, HandTracker tracker, bool left)
    {
        Transform g = null; bool palm = false;
        foreach (var n in left ? new[] { "GripLeft", "PalmGripLeft", "Grip", "PalmGrip" } : new[] { "Grip", "PalmGrip" })
            if ((g = tf.Find(n)) != null) { palm = n.Substring(0, 4) == "Palm"; break; }
        Vector3 lp = g != null ? g.localPosition : Vector3.zero;
        Quaternion lr = g != null ? g.localRotation : Quaternion.identity;
        Quaternion grot = Quaternion.Euler(0, 0, left ? -90 : 90);
        Vector3 gpos = palm ? new Vector3(0f, -0.022f, 0.064f) : new Vector3(left ? 0.007f : -0.007f, -0.033f, 0.081f);
        if (left && tracker != null && tracker.flipVisual)
        {
            var fi = Quaternion.Inverse(Quaternion.Euler(tracker.flipVisualRotation));
            gpos = fi * (gpos - tracker.flipVisualPosition); grot = fi * grot;
        }
        var rot = grot * Quaternion.Inverse(lr);
        var pos = gpos - rot * lp;
        tf.position = node.TransformPoint(pos);
        tf.rotation = node.rotation * rot;
    }

    [Test]
    public void HoldMatchesTheLuaReferenceExactly()
    {
        // pistol (LaserTag hold, GripLeft), wand (Wizard's Way), palm (PalmGrip/PalmGripLeft), flipVisual tracker
        foreach (bool flipOn in new[] { false, true })
        foreach (string kind in new[] { "pistol", "wand", "palm" })
        foreach (bool left in new[] { false, true })
        {
            var node = Node(left ? "LeftHand" : "RightHand");
            HandTracker tracker = null;
            if (flipOn)
            {
                tracker = node.gameObject.AddComponent<HandTracker>();
                tracker.handPreference = left ? HandTracker.HandPreference.Left : HandTracker.HandPreference.Right;
                tracker.flipVisual = true;
                tracker.flipVisualRotation = new Vector3(0f, 0f, 180f);
                tracker.flipVisualPosition = new Vector3(0f, -0.06f, 0f);
            }
            var prop = Make(kind, node).transform;
            void Marker(string n, Vector3 p, Vector3 e) { var m = Make(n, prop).transform; m.localPosition = p; m.localRotation = Quaternion.Euler(e); }
            if (kind == "pistol") { Marker("Grip", new Vector3(-0.033f, -0.063f, 0.012f), Vector3.zero); Marker("GripLeft", new Vector3(0.033f, -0.063f, 0.012f), Vector3.zero); }
            if (kind == "wand") Marker("Grip", new Vector3(-0.011f, 0.001f, 0.058f), new Vector3(24.2f, 9.5f, 3.9f));
            if (kind == "palm") { Marker("PalmGrip", new Vector3(0f, -0.004f, -0.01f), new Vector3(0f, 0f, -90f)); Marker("PalmGripLeft", new Vector3(0f, -0.004f, -0.01f), new Vector3(0f, 0f, 90f)); }

            var twin = Object.Instantiate(prop.gameObject, node).transform;
            _made.Add(twin.gameObject);
            twin.name = kind + "_lua";
            LuaMount(twin, node, tracker, left);
            Assert.That(Handhelds.Hold(prop.gameObject, left ? "left" : "right", node), Is.True);
            string what = $"{kind} {(left ? "L" : "R")} flip={flipOn}";
            Assert.That((prop.position - twin.position).magnitude, Is.LessThan(1e-6f), what + " position (0 mm)");
            Assert.That(Quaternion.Angle(prop.rotation, twin.rotation), Is.LessThan(0.01f), what + " rotation (0°)");
            Handhelds.Release(prop.gameObject);
        }
    }

    [Test]
    public void NestedMarkersAreFoundWithDirectChildrenFirst()
    {
        var prop = Make("Rifle").transform;
        var gun = Make("Gun", prop).transform;
        gun.localPosition = new Vector3(0f, 0.01f, 0.02f);
        gun.localRotation = Quaternion.Euler(0f, 0f, 10f);
        var nested = Make(HandheldMath.GripName, gun).transform;
        nested.localPosition = new Vector3(0f, -0.05f, 0f);
        var muzzle = Make(HandheldMath.MuzzleName, gun).transform;
        muzzle.localPosition = new Vector3(0f, 0f, 0.3f);
        Assert.That(HandheldMath.FindGrip(prop, false, out _), Is.EqualTo(nested), "prop/Gun/Grip is found");
        Assert.That(HandheldMath.FindMuzzle(prop), Is.EqualTo(muzzle));
        var node = Node("RightHand");
        prop.SetParent(node, false);
        HandheldMath.Place(node, prop, false);
        Near(node.InverseTransformPoint(nested.position), HandheldMath.FistCentre(false), Eps, "nested grip lands on the fist");
        Assert.That(Quaternion.Angle(Quaternion.Inverse(node.rotation) * nested.rotation, HandheldMath.HandFrame(false)), Is.LessThan(0.05f));
        var direct = Make(HandheldMath.GripName, prop).transform;
        Assert.That(HandheldMath.FindGrip(prop, false, out _), Is.EqualTo(direct), "a direct child wins");
        var left = Make(HandheldMath.GripLeftName, gun).transform;
        Assert.That(HandheldMath.FindGrip(prop, true, out _), Is.EqualTo(left), "name priority beats depth: GripLeft first for the left hand");
    }

    [Test]
    public void TwoHandAimPointsGripToGrip2AtTheOffFist()
    {
        var main = Node("RightHand");
        var off = Make("LeftHand").transform;
        off.SetPositionAndRotation(main.position + main.rotation * new Vector3(0.05f, 0.1f, 0.35f), main.rotation * Quaternion.Euler(0f, 0f, 30f));
        var prop = Make("Rifle", main).transform;
        var g = Make(HandheldMath.GripName, prop).transform;
        var g2 = Make(HandheldMath.Grip2Name, prop).transform;
        g2.localPosition = new Vector3(0f, 0.02f, 0.3f);
        Assert.That(Handhelds.HoldTwoHanded(prop.gameObject, "right", off, main), Is.True);
        Vector3 a = main.TransformPoint(HandheldMath.FistCentre(false));
        Vector3 b = off.TransformPoint(HandheldMath.FistCentre(true));
        Near(g.position, a, Eps, "Grip stays on the main fist");
        Assert.That(Vector3.Angle(g2.position - g.position, b - a), Is.LessThan(0.05f), "Grip→Grip2 aims at the off fist");
        off.gameObject.SetActive(false);   // off hand lost → one-hand mount
        Handhelds.Tick();
        HandheldMath.MeasureMount(prop, main, false, null, out float dist, out float ang);
        Assert.That(dist, Is.LessThan(Eps));
        Assert.That(ang, Is.LessThan(0.05f));
    }

    [Test]
    public void CalibratedTargetOverridesTheFistCentre()
    {
        var node = Node("LeftHand");
        var prop = Prop(node, Vector3.zero, Quaternion.identity);
        Handhelds.Hold(prop.gameObject, "left", node);
        var cal = new Vector3(0.0221f, -0.0188f, 0.10f);
        Handhelds.SetTarget(prop.gameObject, cal);
        Near(node.InverseTransformPoint(prop.position), new Vector3(-cal.x, cal.y, cal.z), Eps, "left mirrors x");
    }

    [Test]
    public void HoldReparentsAndRemoteActiveHandSwitchesSides()
    {
        var clone = Make("RemoteRig_Test").transform;
        var peer = clone.gameObject.AddComponent<RemotePlayer>();
        peer.activeHand = "r";
        var rr = clone.gameObject.AddComponent<RemoteRig>();
        rr.peer = peer;
        var hand = Make("Hand", clone).transform;
        hand.SetPositionAndRotation(new Vector3(1f, 1f, 1f), Quaternion.Euler(0f, 90f, 30f));
        var prop = Prop(hand, new Vector3(0f, -0.05f, 0f), Quaternion.identity);

        Assert.That(Handhelds.Hold(prop.gameObject, "active"), Is.True);
        Assert.That(Handhelds.Side(prop.gameObject), Is.EqualTo("right"));
        Near(Local(hand, prop.up), Vector3.left, Eps, "right: thumb −X");

        peer.activeHand = "l";
        Handhelds.Tick();
        Assert.That(Handhelds.Side(prop.gameObject), Is.EqualTo("left"));
        Near(Local(hand, prop.up), Vector3.right, Eps, "left: thumb +X");
        var grip = prop.Find(HandheldMath.GripName);
        Near(Quaternion.Inverse(hand.rotation) * (grip.position - hand.position), HandheldMath.FistCentre(true), Eps, "left grip");

        Assert.That(Handhelds.Release(prop.gameObject), Is.True);
        peer.activeHand = "r";
        Handhelds.Tick();
        Near(Local(hand, prop.up), Vector3.right, Eps, "released: left alone");
    }

    // ArcadeLand / Zombiez: a rig with ONLY a BothHands tracker. dp.hold(prop,'left') must mount on the
    // LEFT hand even while the right hand is the active one (the tracker sits on the right hand).
    [Test]
    public void NamedSideOnABothHandsRigMountsOnThatHandNotTheActiveOne()
    {
        var prevRig = PlayerRig.Instance;
        try
        {
            var rigGo = Make("Player");
            var rig = rigGo.AddComponent<PlayerRig>();
            PlayerRig.Instance = rig;
            var both = Make("BothHands", rigGo.transform).AddComponent<HandTracker>();
            both.handPreference = HandTracker.HandPreference.Both;

            var leftAnchor = Make("LeftHandAnchor").transform;
            leftAnchor.SetPositionAndRotation(new Vector3(-0.3f, 1.1f, 0.2f), Quaternion.Euler(10f, 40f, -70f));
            var rightAnchor = Make("RightHandAnchor").transform;
            rightAnchor.SetPositionAndRotation(new Vector3(0.35f, 1.0f, 0.25f), Quaternion.Euler(-20f, -30f, 80f));
            both.leftHand = Make("OVRHandPrefab L", leftAnchor).AddComponent<OVRHand>();
            both.rightHand = Make("OVRHandPrefab R", rightAnchor).AddComponent<OVRHand>();
            // The tracker follows the ACTIVE (right) hand.
            both.transform.SetPositionAndRotation(rightAnchor.position, rightAnchor.rotation);

            foreach (bool authoredUnderTracker in new[] { false, true })
            {
                Handhelds.ClearAll();
                var prop = Prop(authoredUnderTracker ? both.transform : null, new Vector3(0f, -0.05f, 0f), Quaternion.identity);
                Assert.That(Handhelds.Hold(prop.gameObject, "left"), Is.True);
                Assert.That(Handhelds.Side(prop.gameObject), Is.EqualTo("left"));
                Assert.That(prop.parent, Is.Not.EqualTo(both.transform), "must not ride the active-hand tracker");
                Assert.That(prop.IsChildOf(rigGo.transform), Is.True, "stays under the rig (hides with it)");
                var grip = prop.Find(HandheldMath.GripName);
                Near(Quaternion.Inverse(leftAnchor.rotation) * (grip.position - leftAnchor.position),
                     HandheldMath.FistCentre(true), Eps, "left grip on the LEFT fist (under=" + authoredUnderTracker + ")");
                Near(Local(leftAnchor, prop.up), Vector3.right, Eps, "left: thumb +X");

                // The left hand moves; the prop follows it on the next tick, not the right hand.
                leftAnchor.position += new Vector3(0.1f, 0.05f, -0.02f);
                Handhelds.Tick();
                Near(Quaternion.Inverse(leftAnchor.rotation) * (grip.position - leftAnchor.position),
                     HandheldMath.FistCentre(true), Eps, "follows the left hand");
                Object.DestroyImmediate(prop.gameObject);
            }

            // 'active' keeps riding the BothHands tracker.
            Handhelds.ClearAll();
            var active = Prop(null, new Vector3(0f, -0.05f, 0f), Quaternion.identity);
            Assert.That(Handhelds.Hold(active.gameObject, "active"), Is.True);
            Assert.That(active.parent, Is.EqualTo(both.transform));
        }
        finally { PlayerRig.Instance = prevRig; }
    }

    [Test]
    public void HoldWithExplicitNodeReparentsUnderIt()
    {
        var hand = Node("RightHand");
        var prop = Prop(null, Vector3.zero, Quaternion.identity);
        Assert.That(Handhelds.Hold(prop.gameObject, "right", hand), Is.True);
        Assert.That(prop.parent, Is.EqualTo(hand));
        Near(Local(hand, prop.up), Vector3.left, Eps, "thumb −X");
        HandheldMath.Muzzle(prop, out var mp, out var mf);
        Near(mf, prop.forward, Eps, "muzzle forward");
        Near(mp, prop.Find(HandheldMath.MuzzleName).position, Eps, "muzzle position");
    }

    [Test]
    public void EditorAnchorFrameMakesAMountedPropPointWhereYouLook()
    {
        Quaternion head = Quaternion.Euler(-10f, 35f, 0f);
        foreach (bool left in new[] { false, true })
        {
            var node = Make(left ? "LeftHandAnchor" : "RightHandAnchor").transform;
            node.rotation = HandheldMath.EditorAnchorRotation(head, left);
            var prop = Prop(node, new Vector3(0f, -0.05f, 0f), Quaternion.identity);
            HandheldMath.Mount(prop, node, left);
            Assert.That(Quaternion.Angle(prop.rotation, head), Is.LessThan(0.05f), "upright, forward: " + left);
            // fingers forward, thumb up, palm inward
            Near(node.forward, head * Vector3.forward, Eps, "fingers");
            Near(left ? node.right : -node.right, head * Vector3.up, Eps, "thumb up");
        }
    }

    [Test]
    public void ValidateRigFlagsTheDeviceBugNegativeScaleRemoteDriftAndActiveHands()
    {
        var rig = Make("Player").transform;
        var right = Make("RightHand", rig).transform;
        var tr = right.gameObject.AddComponent<HandTracker>();
        tr.handPreference = HandTracker.HandPreference.Right;
        var gun = Prop(right, new Vector3(0f, -0.05f, 0f), Quaternion.identity);   // identity under the hand: the classic bug
        var issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Exists(i => i.level == HandheldMath.IssueLevel.Fail && i.message.Contains("rotated 90")), Is.True, string.Join("\n", issues));

        HandheldMath.Mount(gun, right, false, tr);
        issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Count, Is.EqualTo(0), string.Join("\n", issues));

        gun.localScale = new Vector3(-1f, 1f, 1f);
        issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Exists(i => i.message.Contains("negative scale")), Is.True, string.Join("\n", issues));
        gun.localScale = Vector3.one;

        var remote = Make("RemoteRig", rig).transform;
        var rRight = Make("RightHand", remote).transform;
        rRight.SetPositionAndRotation(new Vector3(2f, 1f, 0f), Quaternion.Euler(0f, 40f, 0f));
        var copy = Prop(rRight, new Vector3(0f, -0.05f, 0f), Quaternion.identity);
        HandheldMath.Place(rRight, copy, false);
        Assert.That(HandheldMath.ValidateRig(rig.gameObject).Count, Is.EqualTo(0), "remote copy matches local");
        copy.position += rRight.right * 0.01f;   // 1 cm off: inside the 2 cm grip tolerance, outside remote≈local
        issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Exists(i => i.message.Contains("remote copy differs")), Is.True, string.Join("\n", issues));

        var activeHand = Make("Hand", remote).transform;
        var held = Prop(activeHand, Vector3.zero, Quaternion.identity);
        HandheldMath.Place(activeHand, held, false);
        issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Exists(i => i.path.Contains("RemoteRig/Hand") && i.message.Contains("ACTIVE-HAND")), Is.True, string.Join("\n", issues));
        issues = HandheldMath.ValidateRig(rig.gameObject, p => p == held);
        Assert.That(issues.Exists(i => i.path.Contains("RemoteRig/Hand")), Is.False, "dp.hold users are not pose-checked");
    }

    [Test]
    public void ValidateRigNamesTheOldTemplatePivot()
    {
        var rig = Make("Player").transform;
        var right = Make("RightHand", rig).transform;
        right.gameObject.AddComponent<HandTracker>().handPreference = HandTracker.HandPreference.Right;
        var pivot = Make("Right Handheld Pivot (Attach guns, handhelds, etc)", right).transform;
        pivot.localPosition = new Vector3(0.0221f, -0.0188f, 0.1f);
        pivot.localRotation = Quaternion.Euler(0f, -90f, -98.6f);
        Prop(pivot, Vector3.zero, Quaternion.identity);
        var issues = HandheldMath.ValidateRig(rig.gameObject);
        Assert.That(issues.Exists(i => i.level == HandheldMath.IssueLevel.Fail && i.message.Contains("Handheld Pivot")), Is.True, string.Join("\n", issues));
    }

    [Test]
    public void TemplatePivotPoseIsTheMountOfAnIdentityGrip()
    {
        // Player.prefab "Right/Left Handheld Pivot" (HANDHELD.md §8 fix) = the Grip pose.
        var node = Node("RightHand");
        var pivot = Make("Right Handheld Pivot (Attach guns, handhelds, etc)", node).transform;
        pivot.localPosition = new Vector3(-0.007f, -0.033f, 0.081f);
        pivot.localRotation = Quaternion.Euler(0f, 0f, 90f);
        HandheldMath.MeasureMount(pivot, node, false, null, out float dist, out float ang);
        Assert.That(dist, Is.LessThan(Eps));
        Assert.That(ang, Is.LessThan(0.05f));
    }
}
