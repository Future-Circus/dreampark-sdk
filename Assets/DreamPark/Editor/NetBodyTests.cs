using System.Collections.Generic;
using System.Linq;
using DreamPark;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// NetBody (dp.net_body) with simulated peers: every "device" has its own copy of the body,
// its own link (uid, leader flag) and its own budget; a fake relay fans each message out to
// every OTHER device through the real wire JSON — exactly what the relay does (no self-echo).
// Covers ownership leases, conflicts, the shared budget, dead bands, follower smoothing,
// attraction-relative poses, late join, home reset and optimizer parking.
public sealed class NetBodyTests
{
    sealed class Relay
    {
        public readonly List<Device> devices = new List<Device>();
        public readonly List<(Device from, string type, string json)> wire = new List<(Device, string, string)>();
        public int delivered;

        public void Flush()
        {
            while (wire.Count > 0)
            {
                var batch = wire.ToList();
                wire.Clear();
                foreach (var m in batch)
                {
                    string full = "{\"type\":\"" + m.type + "\",\"payload\":{\"netId\":1," + m.json.Substring(1) + "}";
                    foreach (var d in devices)
                    {
                        if (d == m.from || !d.online) continue;
                        foreach (var b in d.bodies) b.Receive(full);
                        delivered++;
                    }
                }
            }
        }
    }

    sealed class Link : INetBodyLink
    {
        public Device device;
        public string LocalId => device.id;
        public bool IsLeader => device.relay.devices.Where(d => d.online).Min(d => d.id) == device.id;
        public bool Connected => device.online;
        public int PeerCount => device.relay.devices.Count(d => d.online && d != device);
        public bool HasHeadroom(NetBodyPriority p) => true;
        public bool Send(NetBody body, string type, string json)
        {
            device.relay.wire.Add((device, type, json));
            device.sent.Add(type + " " + json);
            return true;
        }
    }

    sealed class Device
    {
        public string id;
        public Relay relay;
        public bool online = true;
        public Link link;
        public NetBodyBudget budget = new NetBodyBudget(20f);
        public Transform frame;
        public readonly List<NetBody> bodies = new List<NetBody>();
        public readonly List<string> sent = new List<string>();
        public NetBody Body => bodies[0];
    }

    Scene _scene;
    readonly List<GameObject> _made = new List<GameObject>();
    double _t;
    Relay _relay;

    [SetUp]
    public void SetUp()
    {
        _scene = EditorSceneManager.NewPreviewScene();
        _t = 100.0;
        _relay = new Relay();
    }

    [TearDown]
    public void TearDown()
    {
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

    /// One device with an attraction frame at <paramref name="framePos"/>/<paramref name="frameYaw"/>
    /// (its own world) and <paramref name="count"/> bodies resting in it at the same LOCAL spots.
    Device AddDevice(string id, Vector3 framePos = default, float frameYaw = 0f, int count = 1, bool kinematic = false)
    {
        var d = new Device { id = id, relay = _relay };
        d.link = new Link { device = d };
        d.frame = Make("Attraction_" + id).transform;
        d.frame.SetPositionAndRotation(framePos, Quaternion.Euler(0f, frameYaw, 0f));
        for (int i = 0; i < count; i++)
        {
            var go = Make("Gift" + i, d.frame);
            go.transform.localPosition = new Vector3(i * 0.5f, 0.2f, 1f);
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = kinematic;
            var b = new NetBody(rb, new NetBodyOptions(), d.link, d.budget, () => _t);
            b.SetFrame(d.frame);
            d.bodies.Add(b);
        }
        _relay.devices.Add(d);
        return d;
    }

    void Step(float seconds, float hz = 60f, System.Action perFrame = null)
    {
        int n = Mathf.RoundToInt(seconds * hz);
        for (int i = 0; i < n; i++)
        {
            _t += 1.0 / hz;
            perFrame?.Invoke();
            foreach (var d in _relay.devices)
                foreach (var b in d.bodies) { b.Tick(); b.FixedTick(1f / hz); }
            _relay.Flush();
        }
    }

    [Test]
    public void TouchClaimMakesOthersFollowKinematically()
    {
        var a = AddDevice("a");
        var b = AddDevice("b");
        Assert.That(a.Body.Claim(force: false, pin: false), Is.True);
        _relay.Flush();
        Assert.That(a.Body.Owner, Is.EqualTo("a"));
        Assert.That(a.Body.Revision, Is.EqualTo(1));
        Assert.That(b.Body.Owner, Is.EqualTo("a"));
        Assert.That(b.Body.IsFollowing, Is.True);
        Assert.That(b.Body.Body.isKinematic, Is.True, "followers never simulate");
        Assert.That(a.Body.Body.isKinematic, Is.False, "the owner simulates");
    }

    [Test]
    public void ALiveStreamCannotBeStolenButASilentOneCan()
    {
        var a = AddDevice("a");
        var b = AddDevice("b");
        a.Body.Claim(force: false, pin: true);   // held from Lua: keepalive while still
        Step(0.5f);
        Assert.That(b.Body.Claim(force: false, pin: false), Is.False, "someone else is streaming it");
        Assert.That(b.Body.Owner, Is.EqualTo("a"));
        a.online = false;                         // their headset goes away mid-hold
        Step(2.5f);
        Assert.That(b.Body.IsFollowing, Is.False, "a silent owner hands back to physics");
        Assert.That(b.Body.Claim(force: false, pin: false), Is.True);
        Assert.That(b.Body.Owner, Is.EqualTo("b"));
    }

    [Test]
    public void SimultaneousClaimsConvergeOnOneOwner()
    {
        var a = AddDevice("a");
        var b = AddDevice("b");
        a.Body.Claim(force: false, pin: false);
        b.Body.Claim(force: false, pin: false);
        _relay.Flush();
        Assert.That(a.Body.Owner, Is.EqualTo(b.Body.Owner));
        Assert.That(a.Body.Owner, Is.EqualTo("b"), "equal revision → higher uid wins, on every device");
        Assert.That(a.Body.IsFollowing, Is.True);
        Assert.That(b.Body.IsFollowing, Is.False);
    }

    [Test]
    public void TheWholeHeadsetStaysInsideTheSharedBudget()
    {
        var a = AddDevice("a", count: 40, kinematic: true);   // 40 scripted bodies, all held and moving
        AddDevice("b", count: 40, kinematic: true);
        foreach (var body in a.bodies) body.Claim(force: false, pin: true);
        Step(0.5f, 60f, () => { foreach (var body in a.bodies) body.Body.transform.position += Vector3.right * 0.01f; });
        int before = a.sent.Count;
        var perBody = a.bodies.ToDictionary(x => x, x => x.Sent);
        Step(2f, 60f, () => { foreach (var body in a.bodies) body.Body.transform.position += Vector3.right * 0.01f; });
        int perSecond = (a.sent.Count - before) / 2;
        Assert.That(perSecond, Is.LessThanOrEqualTo(21), "shared budget 20/s across every body");
        Assert.That(perSecond, Is.GreaterThanOrEqualTo(15), "and it is actually used");
        foreach (var body in a.bodies)
            Assert.That((body.Sent - perBody[body]) / 2f, Is.LessThanOrEqualTo(10.5f), "≤ 10 Hz per body");
    }

    [Test]
    public void DeadBandsKeepAStillBodyQuiet()
    {
        var a = AddDevice("a", kinematic: true);
        AddDevice("b", kinematic: true);
        Step(1.5f);                        // the one late-join request per connection goes out first
        a.Body.Claim(force: false, pin: false);
        Step(0.2f, 60f, () => a.Body.Body.transform.position += Vector3.forward * 0.02f);
        Step(0.5f);                        // comes to rest → one settled pose, lease released
        int n = a.sent.Count;
        Assert.That(a.sent.Last(), Does.Contain("\"s\":1"));
        Assert.That(a.Body.HoldsLease, Is.False, "released at rest");
        Step(3f);
        Assert.That(a.sent.Count, Is.EqualTo(n), "nothing while it rests");

        a.Body.Claim(force: false, pin: true);   // held but still: slow keepalive only
        int m = a.sent.Count;
        Step(3f);
        Assert.That(a.sent.Count - m, Is.InRange(6, 12), "≈ one keepalive every 0.3 s");
    }

    [Test]
    public void FollowerGlideIsCappedAndNeverOvershoots()
    {
        Vector3 cur = Vector3.zero, target = new Vector3(1f, 0f, 0f);
        float dt = 1f / 72f;
        float prevDist = 1f;
        for (int i = 0; i < 200; i++)
        {
            Vector3 next = NetBody.GlideStep(cur, target, dt, out bool teleport);
            Assert.That(teleport, Is.False);
            Assert.That((next - cur).magnitude, Is.LessThanOrEqualTo(NetBody.MaxFollowSpeed * dt + 1e-5f), "capped speed");
            float dist = (target - next).magnitude;
            Assert.That(dist, Is.LessThanOrEqualTo(prevDist + 1e-6f), "monotonic, no overshoot");
            prevDist = dist;
            cur = next;
        }
        Assert.That(prevDist, Is.LessThan(1e-3f), "converges within ~3 s");
        NetBody.GlideStep(Vector3.zero, new Vector3(4f, 0f, 0f), dt, out bool far);
        Assert.That(far, Is.True, "teleport only for a gross error");
        Quaternion q = NetBody.RotateStep(Quaternion.identity, Quaternion.Euler(0f, 170f, 0f), dt);
        Assert.That(Quaternion.Angle(Quaternion.identity, q), Is.LessThanOrEqualTo(1080f * dt + 0.01f));
    }

    [Test]
    public void PosesAreAttractionRelative()
    {
        var a = AddDevice("a");
        var b = AddDevice("b", new Vector3(2f, 0f, -3f), 90f);   // same attraction, different world placement
        a.Body.Body.transform.localPosition = new Vector3(0.3f, 0.5f, 1.4f);
        a.Body.Claim(force: false, pin: false);
        _relay.Flush();
        Vector3 want = b.frame.TransformPoint(new Vector3(0.3f, 0.5f, 1.4f));
        Assert.That(b.Body.FollowTargetWorld.HasValue, Is.True);
        Assert.That((b.Body.FollowTargetWorld.Value - want).magnitude, Is.LessThan(0.002f));
    }

    [Test]
    public void LateJoinerGetsTheLeadersSnapshot()
    {
        var a = AddDevice("a");                    // leader (lowest uid)
        var c0 = AddDevice("c");
        a.Body.Body.transform.localPosition = new Vector3(0.8f, 0.2f, 2f);
        a.Body.Claim(force: false, pin: false);
        a.Body.Claim(force: true, pin: false);     // rev 2
        Step(0.5f);
        Assert.That(a.Body.Revision, Is.GreaterThan(0));

        var late = AddDevice("d");                 // joins now, body at its authored spot
        Assert.That(late.Body.Revision, Is.EqualTo(0));
        // (EditMode has no physics step, so MovePosition does not move: the follower reaches the
        // snapshot through the settled-follow timeout snap. On device it glides there.)
        Step(3f);
        Assert.That(late.sent.Any(s => s.StartsWith(NetBody.TypeRequest)), Is.True, "asks once");
        Assert.That(late.Body.Revision, Is.EqualTo(a.Body.Revision));
        Assert.That(late.Body.Owner, Is.EqualTo(a.Body.Owner));
        Vector3 want = late.frame.TransformPoint(new Vector3(0.8f, 0.2f, 2f));
        Assert.That((late.Body.Body.transform.position - want).magnitude, Is.LessThan(0.01f), "glided to the snapshot");
        Assert.That(late.Body.IsFollowing, Is.False, "and handed back to physics");
    }

    [Test]
    public void FallingOutOfTheRoomResetsHomeEverywhere()
    {
        var a = AddDevice("a", kinematic: true);
        var b = AddDevice("b", kinematic: true);
        Vector3 home = a.Body.Body.transform.position;
        a.Body.Claim(force: false, pin: true);
        a.Body.Body.transform.position = home + Vector3.down * 2f;   // through the floor
        Step(0.1f);
        Assert.That((a.Body.Body.transform.position - home).magnitude, Is.LessThan(1e-3f), "owner put it back");
        Assert.That(a.sent.Any(s => s.Contains("\"h\":1")), Is.True);
        Vector3 homeB = b.frame.TransformPoint(a.frame.InverseTransformPoint(home));
        Assert.That((b.Body.Body.transform.position - homeB).magnitude, Is.LessThan(0.01f), "followers jump with it");
    }

    [Test]
    public void ParkedBodiesAreNeverTouchedAndReconcileOnUnpark()
    {
        var a = AddDevice("a");
        var b = AddDevice("b");
        var rb = b.Body.Body;
        // LevelObjectManager parks b's copy (it was dynamic): forced kinematic, no collisions.
        rb.isKinematic = true;
        rb.detectCollisions = false;
        a.Body.Claim(force: false, pin: true);    // held: a live stream
        _relay.Flush();
        Step(0.2f);
        Assert.That(b.Body.Owner, Is.EqualTo("a"), "state is tracked while parked");
        Assert.That(rb.detectCollisions, Is.False, "parking flags untouched");
        // Unpark: the optimizer restores the dynamic body…
        rb.isKinematic = false;
        rb.detectCollisions = true;
        Step(1f / 60f);
        Assert.That(rb.isKinematic, Is.True, "…and the follower takes it kinematic again");
        a.Body.Release();
        a.Body.Body.linearVelocity = Vector3.zero;
        Step(2f);
        Assert.That(b.Body.IsFollowing, Is.False);
        Assert.That(rb.isKinematic, Is.False, "the game's dynamic body is dynamic again");
    }

    [Test]
    public void AParkedOwnerReleasesItsLease()
    {
        var a = AddDevice("a");
        var b = AddDevice("b");
        a.Body.Claim(force: false, pin: true);   // held from Lua
        Step(0.3f);
        Assert.That(b.Body.Claim(force: false, pin: false), Is.False, "live: cannot steal");
        // LevelObjectManager culls a's copy (the owner walked away).
        a.Body.Body.isKinematic = true;
        a.Body.Body.detectCollisions = false;
        Step(0.1f);
        Assert.That(a.Body.HoldsLease, Is.False, "a culled owner lets go");
        Assert.That(a.sent.Last(), Does.Contain("\"s\":1"), "and says where it settled");
        Step(0.1f);
        Assert.That(b.Body.Claim(force: false, pin: false), Is.True, "so someone who can see it may take it");
    }

    // Oct 11 2026 playtest: "the game vanished and came back with a physics jolt when a
    // headset joined". The phone ("z", it seeds the room) moved the gift while playing solo;
    // the headset ("a", leader by uid here) moved its own copy elsewhere, at a HIGHER
    // revision, in its own younger game. They meet and the headset loses the session merge
    // (SessionEngine.MergeLosses → NetBodies.Tick → ForgetRevisionForMerge).
    [Test]
    public void AMergeLoserNeverYanksTheSeedersBodies()
    {
        var a = AddDevice("a");
        var z = AddDevice("z");
        var pz = new Vector3(0.8f, 0.2f, 2f);
        var pa = new Vector3(-0.6f, 0.2f, 0.4f);

        a.online = false;                                   // the phone plays solo
        z.Body.Body.transform.localPosition = pz;
        z.Body.Claim(force: false, pin: false);             // rev 1
        Step(1.5f);
        z.online = false; a.online = true;                  // the headset's own game
        a.Body.Body.transform.localPosition = pa;
        for (int i = 0; i < 3; i++) { a.Body.Claim(force: false, pin: false); Step(1.5f); }   // three pushes: rev 3
        Assert.That(a.Body.Revision, Is.GreaterThan(z.Body.Revision), "precondition: the loser's copy outranks the seeder's");

        a.Body.ForgetRevisionForMerge();                    // a lost the merge
        Assert.That(a.Body.Revision, Is.EqualTo(0));
        z.online = true;
        Step(3f);

        Vector3 zWant = z.frame.TransformPoint(pz);
        Assert.That(z.Body.IsFollowing, Is.False, "the seeder's body was never taken over");
        Assert.That((z.Body.Body.transform.position - zWant).magnitude, Is.LessThan(0.01f), "and never moved: no jolt");
        Assert.That(z.Body.Owner, Is.EqualTo("z"));
        Assert.That(a.Body.Owner, Is.EqualTo("z"), "the loser took the seeder's body");
        Assert.That(a.Body.Revision, Is.EqualTo(z.Body.Revision));
        Vector3 aWant = a.frame.TransformPoint(pz);
        Assert.That((a.Body.Body.transform.position - aWant).magnitude, Is.LessThan(0.01f), "and put its copy where the seeder's is");
    }

    [Test]
    public void WhileTheSessionHoldsASoloGameRoomPosesAreIgnored()
    {
        var a = AddDevice("a");
        var z = AddDevice("z");
        var old = NetBody.SessionMerging;
        try
        {
            NetBody.SessionMerging = () => true;   // shared hook: we only look at z
            a.Body.Body.transform.localPosition = new Vector3(0.8f, 0.2f, 2f);
            a.Body.Claim(force: false, pin: false);
            _relay.Flush();
            Assert.That(z.Body.IsFollowing, Is.False, "a held solo game is not yanked while the merge settles");
            Assert.That(z.Body.Revision, Is.EqualTo(0));

            NetBody.SessionMerging = () => false;
            a.Body.Claim(force: true, pin: false);
            _relay.Flush();
            Assert.That(z.Body.IsFollowing, Is.True, "once settled, the room streams as usual");
            Assert.That(z.Body.Owner, Is.EqualTo("a"));
        }
        finally { NetBody.SessionMerging = old; }
    }

    [Test]
    public void OptionsSetTheBodyLimits()
    {
        var a = AddDevice("a");
        var rb = a.Body.Body;
        a.Body.SetOptions(new NetBodyOptions { maxSpeed = 3f, maxSpin = 5f, maxPush = 0.7f, rate = 40f });
        Assert.That(rb.maxDepenetrationVelocity, Is.EqualTo(0.7f).Within(1e-4f));
        Assert.That(rb.maxAngularVelocity, Is.EqualTo(5f).Within(1e-4f));
        Assert.That(a.Body.Options.rate, Is.EqualTo(10f), "never more than 10 Hz");
    }
}
