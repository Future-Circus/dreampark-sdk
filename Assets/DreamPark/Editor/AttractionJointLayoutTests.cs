using System.Collections.Generic;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Joints follow the layout (Oct 19, sal-mole-fix): a frame that rescales content after the joints were built must
/// rebuild them, or a ragdoll / jointed trunk keeps its AUTHORED scale inside PhysX and tears apart at Compact.
/// A hanging chain of four hinged links under an AttractionTemplate, a 0.67 frame, then real physics steps.
/// </summary>
public sealed class AttractionJointLayoutTests
{
    private const float LinkLength = 0.5f;
    private const float FrameScale = 0.67f;
    private GameObject _root;
    private SimulationMode _mode;
    private bool _rebuild;

    [SetUp]
    public void SetUp()
    {
        _mode = Physics.simulationMode;
        _rebuild = AttractionTemplate.RebuildJointsOnLayout;
        Physics.simulationMode = SimulationMode.Script;
    }

    [TearDown]
    public void TearDown()
    {
        Physics.simulationMode = _mode;
        AttractionTemplate.RebuildJointsOnLayout = _rebuild;
        if (_root != null) Object.DestroyImmediate(_root);
    }

    [Test]
    public void ApplyLayout_RescaledJointChainStaysConnected()
    {
        var (attraction, chain, links, joints) = CreateJointedAttraction();
        Assert.That(attraction.ApplyLayout("fcompact1", Vector2.one), Is.True);
        Simulate(150);

        foreach (Transform link in links)
            Assert.That(link.lossyScale.y, Is.EqualTo(LinkLength * FrameScale).Within(0.001f), link.name + " scaled");
        float worst = WorstGap(joints);
        Assert.That(worst, Is.LessThanOrEqualTo(0.03f), "joint gap after a 0.67 frame: " + worst);
        // The whole chain hangs at the frame's scale (4 × 0.5 m × 0.67), not the authored 2 m.
        float hang = chain.position.y - (links[links.Count - 1].position.y - 0.5f * LinkLength * FrameScale);
        Assert.That(hang, Is.EqualTo(4f * LinkLength * FrameScale).Within(0.06f), "chain length " + hang);

        // A title's own rebuild afterwards (SAL's Lua rebuild_joints) is harmless: idempotent.
        foreach (Joint j in joints) AttractionTemplate.RebuildJoint(j);
        foreach (Joint j in joints) AttractionTemplate.RebuildJoint(j);
        Simulate(100);
        Assert.That(WorstGap(joints), Is.LessThanOrEqualTo(0.03f), "after a second (title) rebuild");

        // Re-applying the same frame touches nothing; switching back to Base rebuilds at full scale.
        Assert.That(attraction.ApplyLayout("fcompact1", Vector2.one), Is.True);
        Assert.That(attraction.ApplyLayout(AttractionTemplate.BaseFrameId, Vector2.one), Is.True);
        Simulate(150);
        Assert.That(links[0].lossyScale.y, Is.EqualTo(LinkLength).Within(0.001f), "back at Base scale");
        Assert.That(WorstGap(joints), Is.LessThanOrEqualTo(0.03f), "joint gap back at Base");
    }

    [Test]
    public void ApplyLayout_WithoutTheRebuild_TheChainTears()
    {
        // The control: proves the chain above exercises the bug (PhysX keeps the authored scale).
        AttractionTemplate.RebuildJointsOnLayout = false;
        var (attraction, _, _, joints) = CreateJointedAttraction();
        Assert.That(attraction.ApplyLayout("fcompact1", Vector2.one), Is.True);
        Simulate(150);
        Assert.That(WorstGap(joints), Is.GreaterThan(0.1f), "stale joints tear apart");
    }

    private (AttractionTemplate, Transform, List<Transform>, List<Joint>) CreateJointedAttraction()
    {
        _root = new GameObject("Attraction");
        AttractionTemplate attraction = _root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(20f, 20f);
        attraction.safeArea = 0f;
        var prop = new GameObject("Prop");
        prop.transform.SetParent(_root.transform, false);
        PropTemplate pt = prop.AddComponent<PropTemplate>();
        pt.useColliderBounds = false;
        pt.customFootprintMeters = Vector2.one;

        // A kinematic pivot 3 m up and four 0.5 m links hanging from it, each hinged at its top.
        var chain = new GameObject("Chain").transform;
        chain.SetParent(_root.transform, false);
        chain.localPosition = new Vector3(2f, 3f, 0f);
        Rigidbody pivot = chain.gameObject.AddComponent<Rigidbody>();
        pivot.isKinematic = true;
        var links = new List<Transform>();
        var joints = new List<Joint>();
        Rigidbody previous = pivot;
        for (int i = 0; i < 4; i++)
        {
            GameObject link = GameObject.CreatePrimitive(PrimitiveType.Cube);
            link.name = "Link" + i;
            link.transform.SetParent(chain, false);
            link.transform.localPosition = new Vector3(0f, -LinkLength * (i + 0.5f), 0f);
            link.transform.localScale = new Vector3(0.1f, LinkLength, 0.1f);   // child scale: the parent rescales it
            Object.DestroyImmediate(link.GetComponent<Collider>());               // links never collide with each other
            Rigidbody rb = link.AddComponent<Rigidbody>();
            rb.linearDamping = 2f; rb.angularDamping = 2f;
            HingeJoint hinge = link.AddComponent<HingeJoint>();
            hinge.anchor = new Vector3(0f, 0.5f, 0f);                             // the link's top (local, scaled by the cube)
            hinge.axis = Vector3.forward;
            hinge.autoConfigureConnectedAnchor = true;
            hinge.connectedBody = previous;
            previous = rb;
            links.Add(link.transform);
            joints.Add(hinge);
        }
        var frame = new AttractionLayoutFrame
        {
            id = "fcompact1", name = "Compact", kind = AttractionLayoutFrameKind.Custom, sizeFeet = new Vector2(10f, 10f),
        };
        frame.overrides.Add(new AttractionLayoutOverride
        {
            path = AttractionPackingBaker.PathFrom(_root.transform, chain), target = chain,
            setScale = true, localScale = Vector3.one * FrameScale,
        });
        attraction.SetLayoutFrames(new List<AttractionLayoutFrame> { frame });
        AttractionPackingBaker.Bake(attraction, false);
        Physics.SyncTransforms();
        Simulate(5);   // the joints exist in PhysX at the AUTHORED scale, as after Awake in a real park
        return (attraction, chain, links, joints);
    }

    private static void Simulate(int steps)
    {
        for (int i = 0; i < steps; i++) Physics.Simulate(0.02f);
    }

    /// Distance between each hinge's two anchor points in world space (0 = connected).
    private static float WorstGap(List<Joint> joints)
    {
        float worst = 0f;
        foreach (Joint j in joints)
        {
            Vector3 a = j.transform.TransformPoint(j.anchor);
            Vector3 b = j.connectedBody != null ? j.connectedBody.transform.TransformPoint(j.connectedAnchor) : j.connectedAnchor;
            worst = Mathf.Max(worst, Vector3.Distance(a, b));
        }
        return worst;
    }
}
