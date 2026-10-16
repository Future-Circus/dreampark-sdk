using System;
using System.Collections.Generic;
using System.Reflection;
using DreamPark;
using NUnit.Framework;
using UnityEngine;
using XLua;
using Object = UnityEngine.Object;

// Multiplayer services (Oct 2026 platform upgrade): DreamParkSession (leader,
// clock, seed), SharedState (snapshot, late join, idempotent start, leader
// hand-off) over a SIMULATED relay — several SessionEngines in one process,
// each its own "headset", with the real wire format and the relay's rules
// (fan-out to everyone but the sender, per-message latency). Plus the three
// Sequence bug fixes: NetScope stamping, owner-checked unregister, nested
// zones. Pure logic, in-memory objects; no scene, no Play mode.
public sealed class DreamParkNetTests
{
    // ── Simulated relay ───────────────────────────────────────────────
    sealed class Sim
    {
        public double t;
        public double latency = 0.03;
        public readonly List<Device> devices = new List<Device>();
        readonly List<(double at, Device from, string type, string json)> _q = new List<(double, Device, string, string)>();
        public int sent;
        public Func<Device, string, string, bool> drop;   // (from, type, payload) → lose it

        public Device Add(string id, bool phone = false, bool connected = true, double clockSkew = 0, double rate = 1, string[] titles = null,
            string[] worn = null)
        {
            var d = new Device { sim = this, id = id, phone = phone, connected = connected, skew = clockSkew, rate = rate, worn = worn };
            d.engine = new SessionEngine(id, d, () => d.skew + t * d.rate, () => d.phone,
                titles != null ? (Func<IEnumerable<string>>)(() => titles) : null,
                worn != null ? (Func<IEnumerable<string>>)(() => d.worn ?? new string[0]) : null);
            devices.Add(d);
            return d;
        }

        public void Enqueue(Device from, string type, string payload)
        {
            sent++;
            if (drop != null && drop(from, type, payload)) return;
            _q.Add((t + latency, from, type, "{\"type\":\"" + type + "\",\"payload\":" + payload + "}"));
        }

        public void Run(double seconds, double dt = 0.02)
        {
            double end = t + seconds;
            while (t < end - 1e-9)
            {
                t += dt;
                for (int i = 0; i < _q.Count; i++)
                {
                    var m = _q[i];
                    if (m.at > t) continue;
                    foreach (var d in devices)
                        if (d != m.from && d.connected && !d.gone) d.engine.HandleGlobal(m.type, m.json);
                }
                _q.RemoveAll(m => m.at <= t);
                foreach (var d in devices) if (!d.gone) d.engine.Tick();
            }
        }
    }

    sealed class Device : ISessionLink, ISessionRoomHint
    {
        public Sim sim;
        public string id;
        public bool phone, connected, gone;
        public bool alone;          // what the arbiter knows: nobody else can be in the room
        public string[] worn;       // titles whose PlayerRig this device wears (null = never says)
        public bool KnownAlone => alone;
        public double skew, rate = 1;
        public SessionEngine engine;
        public bool Present => true;
        public bool Connected => connected && !gone;
        public int PingMs => 5;
        public void Publish(string type, string payloadJson) { if (Connected) sim.Enqueue(this, type, payloadJson); }

        public SharedState State(string name = "boss", string bus = "o:42") => SharedState.CreateDetached(engine, bus, name);

        public void Leave(bool clean)
        {
            if (clean) engine.Shutdown();
            gone = true;
        }
    }

    // ── Leader ────────────────────────────────────────────────────────

    [Test]
    public void LeaderIsLowestSyncedHeadsetOnEveryDevice()
    {
        var sim = new Sim();
        var b = sim.Add("b"); var a = sim.Add("a"); var c = sim.Add("c");
        sim.Run(5);
        foreach (var d in sim.devices)
        {
            Assert.That(d.engine.Synced, Is.True, d.id + " synced");
            Assert.That(d.engine.LeaderId, Is.EqualTo("a"), d.id + " leader");
        }
        Assert.That(a.engine.IsLeader && !b.engine.IsLeader && !c.engine.IsLeader, Is.True);
    }

    [Test]
    public void PhonesNeverLeadWhileAHeadsetIsHere()
    {
        var sim = new Sim();
        sim.Add("a", phone: true); sim.Add("b");
        sim.Run(5);
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("b"));

        var solo = new Sim();
        solo.Add("p2", phone: true); solo.Add("p1", phone: true);
        solo.Run(3);
        foreach (var d in solo.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("p1"), "phone-only room: lowest phone");
    }

    static SessionEngine.Candidate C(string id, bool phone, bool synced, double joined = double.NaN) => new SessionEngine.Candidate(id, phone, synced, joined);

    [Test]
    public void PickLeaderRule()
    {
        Assert.That(SessionEngine.PickLeader(new[] { C("c", false, true, 1), C("a", false, false), C("b", false, true, 1) }), Is.EqualTo("b"),
            "an unsynced lower id never beats a synced headset; equal seniority breaks on id");
        // Oct 11 2026: synced before device kind — unsynced headsets adopt a synced
        // phone's timeline (they used to rank themselves first and wait forever).
        Assert.That(SessionEngine.PickLeader(new[] { C("c", false, false), C("a", true, true, 0), C("b", false, false) }), Is.EqualTo("a"),
            "a synced phone outranks unsynced headsets");
        Assert.That(SessionEngine.PickLeader(new[] { C("c", false, true, 5), C("a", true, true, 0), C("b", false, false) }), Is.EqualTo("c"),
            "a synced phone never beats a synced headset, however senior");
        Assert.That(SessionEngine.PickLeader(new[] { C("a", false, true, 50), C("z", false, true, 10) }), Is.EqualTo("z"),
            "the senior (earlier joined) device keeps the lead over a lower id");
    }

    [Test]
    public void IncumbentKeepsTheLeadWhenALowerIdJoins()
    {
        var sim = new Sim();
        var b = sim.Add("b"); var c = sim.Add("c");
        sim.Run(5);
        var events = new List<string>();
        b.engine.LeaderChanged += (o, n) => events.Add(o + ">" + n);

        var a = sim.Add("a");
        sim.Run(0.04);   // a's first (unsynced) beat has landed, nothing back yet
        Assert.That(b.engine.LeaderId, Is.EqualTo("b"), "an unsynced late joiner must not take the room over");
        Assert.That(a.engine.Synced, Is.False);

        sim.Run(2);
        Assert.That(a.engine.Synced, Is.True);
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("b"), d.id + ": sticky — the newcomer never takes over mid-round");
        Assert.That(Math.Abs(a.engine.Now - b.engine.Now), Is.LessThan(0.05), "the late joiner adopted the room's clock");
        Assert.That(events, Is.Empty);
        foreach (var d in sim.devices)
        {
            Assert.That(d.engine.Rank("b"), Is.EqualTo(1));
            Assert.That(d.engine.Rank("c"), Is.EqualTo(2));
            Assert.That(d.engine.Rank("a"), Is.EqualTo(3), "rank is join order, the same on every device");
        }

        b.Leave(clean: true);
        sim.Run(0.3);
        foreach (var d in sim.devices) if (d != b) Assert.That(d.engine.LeaderId, Is.EqualTo("c"), "next most senior, not the lowest id");
    }

    [Test]
    public void LeaderIsScopedToDevicesRunningTheTitle()
    {
        var sim = new Sim();
        var a = sim.Add("a", titles: new[] { "Zombiez" });
        var b = sim.Add("b", titles: new[] { "BirthdayBash", "Zombiez" });
        var c = sim.Add("c", titles: new[] { "BirthdayBash" });
        sim.Run(4);
        foreach (var d in sim.devices)
        {
            Assert.That(d.engine.LeaderFor(null), Is.EqualTo("a"), "session-wide");
            Assert.That(d.engine.LeaderFor("Zombiez"), Is.EqualTo("a"));
            Assert.That(d.engine.LeaderFor("Zombiez") == "a" && d.engine.LeaderFor("BirthdayBash") == "b", Is.True,
                d.id + ": a device without the title never leads it");
        }
        Assert.That(c.engine.Rank("c", "BirthdayBash"), Is.EqualTo(2));
        Assert.That(c.engine.Rank("a", "BirthdayBash"), Is.EqualTo(0));
    }

    [Test]
    public void PausedLeaderHandsOffAtOnceAndReturnsAsNewest()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        sim.Run(5);
        a.engine.Pause(true);
        a.connected = false;
        sim.Run(0.2);
        Assert.That(b.engine.LeaderId, Is.EqualTo("b"), "a doffed leader says goodbye");
        a.engine.Pause(false);
        a.connected = true;
        sim.Run(2);
        Assert.That(a.engine.Synced, Is.True);
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("b"), "the returning device rejoins as newest");
    }

    [Test]
    public void LeaderLeavingHandsOffWithoutAClockJump()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b", clockSkew: 500);
        sim.Run(5);
        double before = b.engine.Now;
        a.Leave(clean: true);
        sim.Run(0.2);
        Assert.That(b.engine.LeaderId, Is.EqualTo("b"), "a clean bye re-elects at once");
        Assert.That(b.engine.Now - before, Is.InRange(0.15, 0.25), "the timeline keeps counting");

        // Silent loss (battery): re-elected after the peer timeout.
        var sim2 = new Sim();
        var x = sim2.Add("x"); var y = sim2.Add("y");
        sim2.Run(3);
        x.Leave(clean: false);
        sim2.Run(y.engine.PeerTimeout + 0.5);
        Assert.That(y.engine.LeaderId, Is.EqualTo("y"));
    }

    // ── Clock ─────────────────────────────────────────────────────────

    [Test]
    public void ClockAgreesAcrossSkewedDevicesAndSlewsMonotonically()
    {
        var sim = new Sim();
        var a = sim.Add("a");
        var b = sim.Add("b", clockSkew: 1234.5, rate: 1.01);   // different start, 1% fast crystal
        sim.Run(5);
        Assert.That(b.engine.ClockSynced, Is.True);
        Assert.That(Math.Abs(a.engine.Now - b.engine.Now), Is.LessThan(0.06));

        double last = b.engine.Now;
        double worst = 0;
        for (int i = 0; i < 500; i++)
        {
            sim.Run(0.02);
            double now = b.engine.Now;
            Assert.That(now, Is.GreaterThanOrEqualTo(last), "the shared clock never runs backwards while slewing");
            last = now;
            worst = Math.Max(worst, Math.Abs(a.engine.Now - now));
        }
        Assert.That(worst, Is.LessThan(0.08), "slewing keeps a 1% fast clock within a few frames");
    }

    [Test]
    public void SoloDeviceIsSyncedAndRejoinsTheRoomsTimeline()
    {
        var sim = new Sim();
        var a = sim.Add("a");
        var b = sim.Add("b", connected: false, clockSkew: 77);
        sim.Run(1);
        Assert.That(b.engine.Synced, Is.False, "a client that is still connecting waits");
        sim.Run(b.engine.SoloGraceDisconnected);
        Assert.That(b.engine.Synced && b.engine.IsLeader, Is.True, "never connected: plays solo");
        uint soloSeed = b.engine.Seed;

        b.connected = true;
        sim.Run(2);
        Assert.That(b.engine.LeaderId, Is.EqualTo("a"));
        Assert.That(Math.Abs(a.engine.Now - b.engine.Now), Is.LessThan(0.05), "the room's clock wins over the solo one");
        Assert.That(b.engine.Seed, Is.EqualTo(a.engine.Seed));
        Assert.That(b.engine.Seed, Is.Not.EqualTo(soloSeed));
    }

    // ── Seed / random ─────────────────────────────────────────────────

    [Test]
    public void SeedAndRandomAgreeOnEveryDevice()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b"); var c = sim.Add("c", phone: true);
        sim.Run(5);
        Assert.That(a.engine.Seed, Is.Not.EqualTo(0u));
        foreach (var d in sim.devices) Assert.That(d.engine.Seed, Is.EqualTo(a.engine.Seed), d.id);
        for (int i = 0; i < 20; i++)
        {
            float r = a.engine.Random01("attack", i);
            Assert.That(b.engine.Random01("attack", i), Is.EqualTo(r));
            Assert.That(c.engine.Random01("attack", i), Is.EqualTo(r));
            Assert.That(r, Is.InRange(0f, 0.99999994f));
        }
        Assert.That(a.engine.Random01("attack", 0), Is.Not.EqualTo(a.engine.Random01("attack", 1)));
        Assert.That(a.engine.Random01("attack", 0), Is.Not.EqualTo(a.engine.Random01("target", 0)));

        // Roughly uniform.
        double sum = 0; for (int i = 0; i < 4000; i++) sum += SessionEngine.HashToUnit(12345u, "k", i);
        Assert.That(sum / 4000, Is.InRange(0.47, 0.53));
    }

    // ── SharedState ───────────────────────────────────────────────────

    [Test]
    public void NonLeaderWritesBecomeLeaderAppliedSnapshots()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); var sb = b.State();
        sim.Run(5);
        Assert.That(sa.Synced && sb.Synced, Is.True);
        Assert.That(sb.LeaderId, Is.EqualTo("a"));

        Assert.That(sb.SetNumber("hp", 5), Is.True);
        Assert.That(sb.Has("hp"), Is.False, "a non-leader does not apply its own write");
        sim.Run(0.3);
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(5));
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(5));
        Assert.That(sa.Rev, Is.EqualTo(1)); Assert.That(sb.Rev, Is.EqualTo(1));
        Assert.That(sb.Writer, Is.EqualTo("a"));

        var changes = new List<string>();
        sb.Changed += (k, v) => changes.Add(k + "=" + v);
        sa.SetString("phase", "fight");
        sa.Delete("hp");
        sim.Run(0.3);
        Assert.That(sb.GetString("phase"), Is.EqualTo("fight"));
        Assert.That(sb.Has("hp"), Is.False);
        Assert.That(changes, Is.EquivalentTo(new[] { "phase=\"fight\"", "hp=" }));
    }

    [Test]
    public void LateJoinerGetsTheSnapshotAndCannotResetTheRoom()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); b.State();
        sim.Run(5);
        sa.SetString("phase", "fight"); sa.SetNumber("hp", 3);
        sim.Run(1);

        var c = sim.Add("0c");          // lowest id in the room, arrives mid-fight
        var sc = c.State();
        // Its start() "initialises" the encounter before it heard anything.
        sc.SetString("phase", "idle"); sc.SetNumber("hp", 10);
        Assert.That(sc.Synced, Is.False);
        sim.Run(2.5);

        Assert.That(sc.Synced, Is.True);
        Assert.That(sc.GetString("phase"), Is.EqualTo("fight"), "late joiner renders the room's snapshot");
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(3));
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(3), "the late joiner's pre-sync init was dropped, not applied");
        Assert.That(sa.GetString("phase"), Is.EqualTo("fight"));

        // The incumbent keeps writing; the newcomer's writes are requests.
        sim.Run(1);
        Assert.That(sa.LeaderId, Is.EqualTo("a"));
        Assert.That(sc.LeaderId, Is.EqualTo("a"));
        int rev = sa.Rev;
        sc.SetNumber("hp", 2);
        sim.Run(0.3);
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(2));
        Assert.That(sa.Rev, Is.EqualTo(rev + 1));
    }

    [Test]
    public void AFreshRoomIsStartedByOneDevice()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); var sb = b.State();
        // Both run the same start(): init the encounter before anyone synced.
        sa.SetString("phase", "idle"); sa.SetNumber("hp", 5);
        sb.SetString("phase", "idle"); sb.SetNumber("hp", 5); sb.SetString("bonus", "late");
        sim.Run(5);
        Assert.That(sa.Synced && sb.Synced, Is.True, "a fresh room syncs quickly (no 10 s wait) once everyone announced");
        Assert.That(sb.GetString("phase"), Is.EqualTo("idle"));
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(5));
        Assert.That(sa.Has("bonus") || sb.Has("bonus"), Is.False, "only the device that starts the room initialises it");
        Assert.That(sa.Rev, Is.EqualTo(sb.Rev));
    }

    [Test]
    public void ALateJoinerWhoseRequestIsLostNeverStartsASecondEncounter()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); b.State();
        sim.Run(5);
        sa.SetString("phase", "fight"); sa.SetNumber("hp", 4);
        sim.Run(0.5);

        int lost = 0;
        var c = sim.Add("c");
        sim.drop = (from, type, payload) => from == c && type == SessionEngine.TypeState && payload.Contains("\"k\":\"rq\"") && lost++ < 3;
        var sc = c.State();
        sc.SetString("phase", "idle");      // its start() tries to reset the encounter
        bool sawEmpty = false;
        for (int i = 0; i < 400; i++)
        {
            sim.Run(0.02);
            if (sc.Synced && sc.GetString("phase") != "fight") sawEmpty = true;
        }
        Assert.That(lost, Is.GreaterThanOrEqualTo(2), "precondition: its first requests were lost");
        Assert.That(sawEmpty, Is.False, "it waited for the room instead of starting its own");
        Assert.That(sc.Synced, Is.True);
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(4));
        Assert.That(sa.GetString("phase"), Is.EqualTo("fight"));
    }

    [Test]
    public void ARoomOfOneStartsByItself()
    {
        var sim = new Sim();
        var a = sim.Add("a");
        var sa = a.State();
        sa.SetNumber("score", 0);
        sim.Run(3.5);
        Assert.That(sa.Synced && sa.IsLeader, Is.True);
        Assert.That(sa.GetNumber("score", -1), Is.EqualTo(0));
    }

    [Test]
    public void MergeCellsNeedNoLeaderAndSurviveLateJoinAndBlips()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b"); var c = sim.Add("c");
        var sa = a.State(); var sb = b.State(); var sc = c.State();
        sim.Run(5);
        // Three players hit the boss in the same frame: grow-only per-player cells.
        sa.Add("dmg", 2); sb.Add("dmg", 3); sc.Add("dmg", 1); sb.Add("dmg", 1);
        sb.Union("claimed", new[] { "cake", "hat" }); sc.Union("claimed", new[] { "hat", "gift" });
        sa.SetMine("seat", "\"red\""); sc.SetMine("seat", "\"blue\"");
        sb.Max("best", 12); sc.Max("best", 9);
        sim.Run(0.3);
        foreach (var s in new[] { sa, sb, sc })
        {
            Assert.That(s.Total("dmg"), Is.EqualTo(7));
            Assert.That(s.Items("claimed"), Is.EqualTo(new[] { "cake", "gift", "hat" }));
            Assert.That(s.MineOf("seat", "a"), Is.EqualTo("\"red\""));
            Assert.That(s.MineOf("seat", "c"), Is.EqualTo("\"blue\""));
            Assert.That(s.GetNumber("best"), Is.EqualTo(12));
            Assert.That(s.GetNumber("dmg"), Is.EqualTo(7), "get on a counter is its total");
        }

        // A blip: b misses writes, then heartbeats repair it.
        b.connected = false;
        sa.Add("dmg", 10); sc.Union("claimed", new[] { "balloon" });
        sim.Run(0.3);
        b.connected = true;
        sim.Run(7);
        Assert.That(sb.Total("dmg"), Is.EqualTo(17));
        Assert.That(sb.Contains("claimed", "balloon"), Is.True);

        // A late joiner gets every cell; only the leader can reset one.
        var d = sim.Add("d");
        var sd = d.State();
        sim.Run(5);
        Assert.That(sd.Synced, Is.True);
        Assert.That(sd.Total("dmg"), Is.EqualTo(17));
        Assert.That(sd.Clear("dmg"), Is.False, "a non-leader cannot clear");
        Assert.That(sa.Clear("dmg"), Is.True);
        sim.Run(0.3);
        sb.Add("dmg", 1);
        sim.Run(0.5);
        foreach (var s in new[] { sa, sb, sc, sd }) Assert.That(s.Total("dmg"), Is.EqualTo(1), "a new epoch: old parts are gone everywhere");
    }

    [Test]
    public void SimultaneousStartsProduceOneFight()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b"); var c = sim.Add("c");
        var sa = a.State(); var sb = b.State(); var sc = c.State();
        sim.Run(5);
        int fights = 0;
        sa.Requested += (kind, json, from) =>
        {
            if (kind == "start" && sa.GetString("phase") != "fight") { fights++; sa.SetString("phase", "fight"); }
        };
        // Three players touch the cake in the same frame.
        sa.Request("start", "{}"); sb.Request("start", "{}"); sc.Request("start", "{}");
        sb.SetString("phase", "fight"); sc.SetString("phase", "fight");
        sim.Run(0.5);
        Assert.That(fights, Is.EqualTo(1));
        Assert.That(sa.Rev, Is.EqualTo(1), "the duplicate sets were no-ops on the leader");
        Assert.That(sb.GetString("phase"), Is.EqualTo("fight"));
        Assert.That(sc.Rev, Is.EqualTo(1));
    }

    [Test]
    public void LeaderHandOffKeepsTheState()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b"); var c = sim.Add("c");
        var sa = a.State(); var sb = b.State(); var sc = c.State();
        sim.Run(5);
        for (int i = 0; i < 4; i++) { sa.SetNumber("hp", 10 - i); sim.Run(0.1); }
        Assert.That(sc.Rev, Is.EqualTo(4));

        a.Leave(clean: true);
        sim.Run(0.3);
        Assert.That(sb.IsLeader, Is.True);
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(7), "the new leader already holds the table");
        sc.SetNumber("hp", 6);   // request -> new leader
        sim.Run(0.3);
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(6));
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(6));
        Assert.That(sb.Rev, Is.EqualTo(5), "the rev continues; nothing was lost or reset");
        Assert.That(sb.Writer, Is.EqualTo("b"));
    }

    [Test]
    public void AnyHolderAnswersALateJoinerWhenTheLeaderVanished()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); var sb = b.State();
        sim.Run(5);
        sa.SetNumber("hp", 8); sim.Run(0.3);
        a.Leave(clean: false);      // battery died: b still thinks a leads for a few seconds

        var c = sim.Add("c");
        var sc = c.State();
        sim.Run(2.5);
        Assert.That(b.engine.LeaderId, Is.EqualTo("a"), "precondition: b has not timed a out yet");
        Assert.That(sc.Synced, Is.True, "b answered on the leader's behalf");
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(8));
    }

    [Test]
    public void HeartbeatsRepairADeviceThatMissedUpdates()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); var sb = b.State();
        sim.Run(5);
        b.connected = false;            // a short Wi-Fi blip
        sa.SetNumber("hp", 1); sim.Run(0.2);
        sa.SetNumber("hp", 2); sim.Run(0.2);
        b.connected = true;
        sim.Run(4);
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(2));
        Assert.That(sb.Rev, Is.EqualTo(sa.Rev));
    }

    [Test]
    public void StatesOnDifferentBusesOrNamesAreIndependent()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var a1 = a.State("boss", "o:1"); var b1 = b.State("boss", "o:1");
        var a2 = a.State("boss", "o:2"); var b2 = b.State("boss", "o:2");
        var a3 = a.State("party", "o:1"); var b3 = b.State("party", "o:1");
        sim.Run(5);
        a1.SetNumber("hp", 1); a2.SetNumber("hp", 2); a3.SetNumber("hp", 3);
        sim.Run(0.3);
        Assert.That(b1.GetNumber("hp"), Is.EqualTo(1));
        Assert.That(b2.GetNumber("hp"), Is.EqualTo(2));
        Assert.That(b3.GetNumber("hp"), Is.EqualTo(3));
    }

    [Test]
    public void TrafficStaysInsideTheBudget()
    {
        var sim = new Sim();
        for (int i = 0; i < 6; i++) sim.Add("p" + i);
        foreach (var d in sim.devices) { d.State("boss"); d.State("party"); d.State("props", "o:7"); }
        sim.Run(5);
        int before = sim.sent;
        // The leader hammers one key every frame for 5 s.
        var lead = sim.devices[0];
        var s = SharedState.CreateDetached(lead.engine, "o:99", "spam");
        for (int i = 0; i < 250; i++) { s.SetNumber("t", i); sim.Run(0.02); }
        double perDevicePerSecond = (sim.sent - before) / 5.0 / sim.devices.Count;
        Assert.That(perDevicePerSecond, Is.LessThan(DreamBoxClient.DesignBudget / 3.0),
            "session + shared state cost a small, bounded slice of the 60/s budget");
        foreach (var d in sim.devices) Assert.That(d.engine.Budget.Sent, Is.LessThanOrEqualTo(d.engine.Budget.Hz * sim.t + 30), "never past the shared bucket");
    }

    [Test]
    public void OneSdkBudgetWithPriorityClassesAndAGameShare()
    {
        double t = 0;
        var b = new NetSendBudget(() => t);
        Assert.That(NetSendBudget.PresenceShare + NetSendBudget.SdkShare + NetSendBudget.GameShare, Is.EqualTo(DreamBoxClient.DesignBudget));
        Assert.That(NetSendBudget.GameShare, Is.GreaterThanOrEqualTo(20), "content net_send keeps a real share");
        int low = 0; while (b.TryTake(NetBodyPriority.Low)) low++;
        Assert.That(low, Is.EqualTo(NetSendBudget.SdkShare / 2), "Low only spends the top half");
        int normal = 0; while (b.TryTake(NetBodyPriority.Normal)) normal++;
        Assert.That(normal, Is.EqualTo(NetSendBudget.SdkShare / 2 - (int)b.NormalReserve), "Normal leaves the reserve for High");
        int high = 0; while (b.TryTake(NetBodyPriority.High)) high++;
        Assert.That(high, Is.EqualTo((int)(b.NormalReserve + b.CriticalDebt)), "High may borrow, boundedly");
        t = 10;
        int total = 0; for (int i = 0; i < 1000; i++) if (b.TryTake(NetBodyPriority.Normal)) total++;
        Assert.That(total, Is.LessThanOrEqualTo(NetSendBudget.SdkShare), "never more than the SDK share per second");

        // Bodies draw from the SAME bucket as shared state and the session.
        var engine = new SessionEngine("x", null, () => t, () => false);
        var previous = DreamParkSession.SwapEngine(engine);
        try
        {
            float before = engine.Budget.Available;
            Assert.That(NetBodies.Budget.Take(t, NetBodyPriority.Normal), Is.True);
            Assert.That(engine.Budget.Available, Is.EqualTo(before - 1).Within(1e-3));
        }
        finally { DreamParkSession.SwapEngine(previous); }
    }

    [Test]
    public void InvalidWritesAreRejected()
    {
        var sim = new Sim();
        var a = sim.Add("a");
        var s = a.State();
        sim.Run(5);
        Assert.That(s.Synced, Is.True);
        Assert.That(s.Set("netId", "1"), Is.False);
        Assert.That(s.Set("x", "{\"netId\":3}"), Is.False);
        Assert.That(s.Set("x", "not json"), Is.False);
        Assert.That(s.Set("", "1"), Is.False);
        Assert.That(s.Set("x", "{\"a\":[1,2,3]}"), Is.True);
        Assert.That(s.GetJson("x"), Is.EqualTo(new Defective.JSON.JSONObject("{\"a\":[1,2,3]}").Print()));
    }

    // ── Bug 1: NetScope on streamed Sequence levels ───────────────────

    static GameObject BuildSequence(string deviceName, string[] loadOrder, bool stamp, out Dictionary<string, NetId> bosses)
    {
        var root = new GameObject(deviceName + " Dream Sequence");
        root.AddComponent<NetScope>().scopeKey = "seq|BirthdayBash";
        var levels = new GameObject("Levels");
        levels.transform.SetParent(root.transform, false);
        new GameObject("Level00_Start Level").transform.SetParent(levels.transform, false);
        new GameObject("Level99_Game Over Level").transform.SetParent(levels.transform, false);
        var addresses = new[] { "BB/Levels/A", "BB/Levels/B", "BB/Levels/C" };
        bosses = new Dictionary<string, NetId>();
        foreach (string name in loadOrder)
        {
            int index = Array.IndexOf(addresses, "BB/Levels/" + name);
            var level = new GameObject(name);
            if (stamp) DreamLevelLoader.StampLevelScope(level, root.transform, index, addresses[index]);
            level.transform.SetParent(levels.transform, false);
            var boss = new GameObject("Cake Boss");
            boss.transform.SetParent(level.transform, false);
            new GameObject("Prop").transform.SetParent(level.transform, false);
            bosses[name] = boss.AddComponent<NetId>();
        }
        return root;
    }

    [Test]
    public void StreamedLevelsHashTheSameWhateverTheLoadOrder()
    {
        var one = BuildSequence("A", new[] { "A", "B", "C" }, true, out var b1);
        var two = BuildSequence("B", new[] { "C", "A", "B" }, true, out var b2);
        var ctl1 = BuildSequence("C", new[] { "A", "B", "C" }, false, out var c1);
        var ctl2 = BuildSequence("D", new[] { "C", "A", "B" }, false, out var c2);
        try
        {
            foreach (var n in new[] { "A", "B", "C" })
                Assert.That(b2[n].PreviewId(), Is.EqualTo(b1[n].PreviewId()), "level " + n);
            Assert.That(b1["A"].PreviewId(), Is.Not.EqualTo(b1["B"].PreviewId()), "levels stay distinct");
            Assert.That(c2["A"].PreviewId(), Is.Not.EqualTo(c1["A"].PreviewId()),
                "control: without the stamp the id depends on load order (the Birthday Bash bug)");
        }
        finally { Object.DestroyImmediate(one); Object.DestroyImmediate(two); Object.DestroyImmediate(ctl1); Object.DestroyImmediate(ctl2); }
    }

    [Test]
    public void ReEnteringALevelGetsTheSameIds()
    {
        var root = new GameObject("Dream Sequence");
        root.AddComponent<NetScope>().scopeKey = "seq|X";
        try
        {
            var first = new GameObject("Lvl"); first.transform.SetParent(root.transform, false);
            DreamLevelLoader.StampLevelScope(first, root.transform, 2, "X/Levels/Lvl");
            var id1 = first.AddComponent<NetId>().PreviewId();
            new GameObject("Something else").transform.SetParent(root.transform, false);
            var again = new GameObject("Lvl"); again.transform.SetParent(root.transform, false);
            DreamLevelLoader.StampLevelScope(again, root.transform, 2, "X/Levels/Lvl");
            Assert.That(again.AddComponent<NetId>().PreviewId(), Is.EqualTo(id1));
            Assert.That(NetId.HashPath(again.transform), Is.EqualTo(id1));
        }
        finally { Object.DestroyImmediate(root); }
    }

    // ── Bug 2: owner-checked unregister ───────────────────────────────

    static readonly MethodInfo EnsureRegistered = typeof(NetId).GetMethod("EnsureRegistered", BindingFlags.Instance | BindingFlags.NonPublic);

    static NetId Registered(string name, uint explicitId)
    {
        var go = new GameObject(name);
        var n = go.AddComponent<NetId>();
        n.explicitId = explicitId;
        EnsureRegistered.Invoke(n, null);
        return n;
    }

    [Test]
    public void ADestroyedDuplicateCannotDeafenTheLiveHolder()
    {
        NetRegistry.Clear();
        var live = Registered("Net Bus (worn rig)", 8240900);
        var dup = Registered("Net Bus (duplicate rig)", 8240900);
        try
        {
            Assert.That(NetRegistry.Holder(8240900), Is.SameAs(live), "first live registrant keeps the id");
            NetRegistry.Unregister(8240900, dup);   // what dup.OnDestroy does
            Assert.That(NetRegistry.Holder(8240900), Is.SameAs(live), "the duplicate's destroy does not remove the live one");

            int got = 0;
            live.OnNetEvent += _ => got++;
            NetRegistry.Dispatch(8240900, "{\"type\":\"x\",\"payload\":{\"netId\":8240900}}");
            Assert.That(got, Is.EqualTo(1));
        }
        finally { Object.DestroyImmediate(live.gameObject); Object.DestroyImmediate(dup.gameObject); NetRegistry.Clear(); }
    }

    [Test]
    public void AWaitingRegistrantIsPromotedWhenTheHolderGoes()
    {
        NetRegistry.Clear();
        var oldLevel = Registered("Cake Boss (old instance)", 77);
        var newLevel = Registered("Cake Boss (re-entered)", 77);
        try
        {
            Assert.That(NetRegistry.Holder(77), Is.SameAs(oldLevel));
            NetRegistry.Unregister(77, oldLevel);
            Assert.That(NetRegistry.Holder(77), Is.SameAs(newLevel), "promoted, not left unregistered");
            NetRegistry.Unregister(77, newLevel);
            Assert.That(NetRegistry.Holder(77), Is.Null);
        }
        finally { Object.DestroyImmediate(oldLevel.gameObject); Object.DestroyImmediate(newLevel.gameObject); NetRegistry.Clear(); }
    }

    [Test]
    public void ReplayBufferKeepsTheNewestAndIsPurgedOnUnregister()
    {
        NetRegistry.Clear();
        try
        {
            for (int i = 0; i < 40; i++) NetRegistry.Dispatch(5, "{\"type\":\"m" + i + "\",\"payload\":{\"netId\":5}}");
            Assert.That(NetRegistry.BufferedCount(5), Is.EqualTo(32), "bounded per id");
            for (uint id = 1000; id < 1300; id++) NetRegistry.Dispatch(id, "{}");
            Assert.That(NetRegistry.BufferedCount(5), Is.EqualTo(0), "a full table evicts the least recently buffered id...");
            Assert.That(NetRegistry.BufferedCount(1299), Is.EqualTo(1), "...instead of refusing every new id");

            NetRegistry.Dispatch(9, "{\"type\":\"old\",\"payload\":{\"netId\":9}}");
            var n = Registered("Late listener", 9);   // registers with no subscriber yet
            NetRegistry.Unregister(9, n);
            var got = new List<string>();
            var again = Registered("Re-entered level", 9);
            again.OnNetEvent += p => got.Add(p);
            Assert.That(got, Is.Empty, "nothing from before the previous holder left is replayed");
            Object.DestroyImmediate(n.gameObject); Object.DestroyImmediate(again.gameObject);
        }
        finally { NetRegistry.Clear(); }
    }

    [Test]
    public void ContentIdIsMixedIntoHashedAndExplicitIds()
    {
        GameObject Title(string gameId, uint explicitId, out NetId bus)
        {
            var root = new GameObject("Player");
            var area = root.AddComponent<GameArea>();
            area.gameId = gameId;
            var child = new GameObject("Net Bus");
            child.transform.SetParent(root.transform, false);
            bus = child.AddComponent<NetId>();
            bus.explicitId = explicitId;
            return root;
        }
        var sal = Title("SuperAdventureLand", 8300001, out var salBus);
        var ww = Title("WizardsWay", 8300001, out var wwBus);
        var sal2 = Title("SuperAdventureLand", 8300001, out var salBus2);
        var scene = new GameObject("Scene Prop").AddComponent<NetId>();
        scene.explicitId = 777042;
        try
        {
            Assert.That(salBus.PreviewId(), Is.Not.EqualTo(wwBus.PreviewId()), "two titles that picked the same explicit id no longer collide");
            Assert.That(salBus.PreviewId(), Is.EqualTo(salBus2.PreviewId()), "the same title computes the same id on every device");
            salBus.explicitId = 0; wwBus.explicitId = 0;
            Assert.That(salBus.PreviewId(), Is.Not.EqualTo(wwBus.PreviewId()), "hashed ids too");
            Assert.That(scene.PreviewId(), Is.EqualTo(777042u), "content-less scene props keep their id");
            Assert.That(NetId.ContentIdOf(wwBus.transform), Is.EqualTo("WizardsWay"));
        }
        finally { Object.DestroyImmediate(sal); Object.DestroyImmediate(ww); Object.DestroyImmediate(sal2); Object.DestroyImmediate(scene.gameObject); }
    }

    static GameObject SpecialLevel(string name)
    {
        var go = new GameObject(name);
        var level = go.AddComponent<AttractionTemplate>();
        level.size = GameLevelSize.Custom;
        level.customSize = new Vector2(12f, 18f);
        return go;
    }

    [Test]
    public void SequenceAssemblyScopesEveryLevelByPackageIdentity()
    {
        var definition = ScriptableObject.CreateInstance<DreamSequencePackageDefinition>();
        definition.startLevelPrefab = SpecialLevel("Start");
        var boss = new GameObject("Cake Boss");
        boss.transform.SetParent(definition.startLevelPrefab.transform, false);
        boss.AddComponent<NetId>();
        definition.overlayLevelPrefab = SpecialLevel("Overlay");
        definition.gameOverLevelPrefab = SpecialLevel("Game Over");
        definition.gameManagerPrefab = new GameObject("Game Manager Prefab");
        definition.levels = new List<DreamSequenceLevel>();
        var parentA = new GameObject(Guid.NewGuid().ToString("N"));   // core's per-load anchor name
        var parentB = new GameObject(Guid.NewGuid().ToString("N"));
        new GameObject("sibling").transform.SetParent(parentB.transform, false);
        GameObject one = null, two = null;
        try
        {
            one = DreamSequencePackageRuntime.Create(definition, "BirthdayBash", parentA.transform, false);
            two = DreamSequencePackageRuntime.Create(definition, "BirthdayBash", parentB.transform, false);
            Assert.That(one.GetComponent<NetScope>().scopeKey, Is.EqualTo("BirthdayBash|seq"));
            var hostA = one.GetComponent<DreamParkPackageHost>();
            var hostB = two.GetComponent<DreamParkPackageHost>();
            Assert.That(hostA.startLevel.GetComponent<NetScope>().scopeKey, Is.EqualTo("BirthdayBash|seq|start"));
            Assert.That(hostA.gameOverLevel.GetComponent<NetScope>().scopeKey, Is.EqualTo("BirthdayBash|seq|over"));
            Assert.That(hostA.overlayLevel.GetComponent<NetScope>().scopeKey, Is.EqualTo("BirthdayBash|seq|overlay"));
            Assert.That(hostA.startLevel.GetComponentInChildren<NetId>(true).PreviewId(),
                Is.EqualTo(hostB.startLevel.GetComponentInChildren<NetId>(true).PreviewId()),
                "the Start Level's Cake Boss hashes the same under two different per-load anchors");
        }
        finally
        {
            if (one != null) Object.DestroyImmediate(one);
            if (two != null) Object.DestroyImmediate(two);
            Object.DestroyImmediate(parentA); Object.DestroyImmediate(parentB);
            Object.DestroyImmediate(definition.startLevelPrefab); Object.DestroyImmediate(definition.overlayLevelPrefab);
            Object.DestroyImmediate(definition.gameOverLevelPrefab); Object.DestroyImmediate(definition.gameManagerPrefab);
            Object.DestroyImmediate(definition);
        }
    }

    // ── Bug 3: zone events inside a Sequence level ────────────────────

    [Test]
    public void ScriptsInsideTheActiveSequenceLevelAreInTheZone()
    {
        var root = new GameObject("Dream Sequence");
        var rootArea = root.AddComponent<GameArea>();
        var level = new GameObject("Level 2");
        level.transform.SetParent(root.transform, false);
        var levelArea = level.AddComponent<GameArea>();
        var other = new GameObject("Other attraction").AddComponent<GameArea>();
        var script = new GameObject("Boombox");
        script.transform.SetParent(level.transform, false);
        var previous = GameArea.currentGameArea;
        try
        {
            Assert.That(LuaZoneRelay.IsInside(rootArea, levelArea), Is.True, "package zone current → level scripts inside");
            Assert.That(LuaZoneRelay.IsInside(levelArea, rootArea), Is.True, "level zone current → package scripts inside");
            Assert.That(LuaZoneRelay.IsInside(rootArea, rootArea), Is.True);
            Assert.That(LuaZoneRelay.IsInside(other, levelArea), Is.False);
            Assert.That(LuaZoneRelay.IsInside(null, levelArea), Is.False);

            // The real relay, as LuaBehaviour wires it: the package root's zone
            // is current (it was there first), the level's zone never enters.
            var relay = script.AddComponent<LuaZoneEnterRelay>();
            int entered = 0;
            relay.cb = () => entered++;
            LuaMessageRelays.NotifyHostStarted(new List<Behaviour> { relay });
            GameArea.currentGameArea = rootArea;
            var sync = typeof(LuaZoneRelay).GetMethod("Sync", BindingFlags.Instance | BindingFlags.NonPublic);
            sync.Invoke(relay, null);
            Assert.That(entered, Is.EqualTo(1), "onzoneenter reaches a script inside the streamed level");
            sync.Invoke(relay, null);
            Assert.That(entered, Is.EqualTo(1), "exactly once");

            var exit = script.AddComponent<LuaZoneExitRelay>();
            int left = 0;
            exit.cb = () => left++;
            LuaMessageRelays.NotifyHostStarted(new List<Behaviour> { exit });
            sync.Invoke(exit, null);
            GameArea.currentGameArea = null;
            sync.Invoke(exit, null);
            Assert.That(left, Is.EqualTo(1), "and onzoneexit when the player leaves the room");
        }
        finally
        {
            GameArea.currentGameArea = previous;
            Object.DestroyImmediate(root); Object.DestroyImmediate(other.gameObject);
        }
    }

    // ── Lua surface (dp.random / dp.clock / dp.shared_state / typed wire) ──

    sealed class NoRelay : ISessionLink
    {
        public bool Present => false;
        public bool Connected => false;
        public int PingMs => 0;
        public void Publish(string type, string payloadJson) { }
    }

    [Test]
    public void LuaApiRoundTripsAttractionRelativeValues()
    {
        var env = LuaBehaviour.GetLuaEnv();
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        double t = 0;
        var engine = new SessionEngine(PlayerPresence.MyId, new NoRelay(), () => t, () => false);
        var previous = DreamParkSession.SwapEngine(engine);
        var attraction = new GameObject("A_Party");
        attraction.AddComponent<LevelTemplate>();
        attraction.transform.position = new Vector3(10, 0, 5);
        var script = new GameObject("Manager");
        script.transform.SetParent(attraction.transform, false);
        try
        {
            env.Global.Set("__t_go", script);
            env.Global.Set("__t_frame", attraction.transform);
            object[] r = env.DoString(@"
local a = dp.random('k')
local b = dp.random('k')
local d6 = dp.random('roll', 6)
local f = dp.random('f', 2.0, 3.0)
local s = dp.shared_state('boss', nil, __t_go)
__t_s = s
s.set('hp', 5)
s.set('spot', CS.UnityEngine.Vector3(11, 1, 6))
s.set('cfg', { speed = 2.5, name = 'cake', list = {1, 2, 3} })
local packed = dp.pack({ p = CS.UnityEngine.Vector3(11, 0, 5) }, __t_frame)
return a == b, d6 >= 1 and d6 <= 6 and math.type(d6) == 'integer', f >= 2 and f < 3, s.synced(), packed, dp.to_attraction(CS.UnityEngine.Vector3(12, 0, 5), __t_go).x
", "net.test");
            Assert.That((bool)r[0], Is.True, "dp.random is deterministic");
            Assert.That((bool)r[1], Is.True, "dp.random(key, m) is an integer 1..m");
            Assert.That((bool)r[2], Is.True);
            Assert.That((bool)r[3], Is.False, "not synced yet: writes are held");
            StringAssert.Contains("\"$a\":[1.000,0.000,0.000]", (string)r[4], "points also travel attraction-relative");
            StringAssert.Contains("\"$p\":", (string)r[4], "...beside the park-local form older apps read");
            Assert.That(Convert.ToDouble(r[5]), Is.EqualTo(2).Within(1e-4), "dp.to_attraction");

            t = 1; engine.Tick();
            Assert.That(engine.Random01("k"), Is.EqualTo((float)(double)env.DoString("return dp.random('k')")[0]).Within(1e-6));

            // Move the attraction: the stored point follows it (attraction-relative).
            attraction.transform.position = new Vector3(20, 0, 5);
            object[] g = env.DoString(@"
local s = __t_s
local spot = s.get('spot')
local cfg = s.get('cfg')
return s.synced(), s.get('hp'), spot.x, spot.y, cfg.speed, cfg.name, #cfg.list, s.get('missing', 'dflt'), s.is_leader()
", "net.test2");
            Assert.That((bool)g[0], Is.True);
            Assert.That(Convert.ToDouble(g[1]), Is.EqualTo(5));
            Assert.That(Convert.ToDouble(g[2]), Is.EqualTo(21).Within(1e-3), "x moved with the attraction");
            Assert.That(Convert.ToDouble(g[3]), Is.EqualTo(1).Within(1e-3));
            Assert.That(Convert.ToDouble(g[4]), Is.EqualTo(2.5).Within(1e-9), "numbers keep double precision");
            Assert.That((string)g[5], Is.EqualTo("cake"));
            Assert.That(Convert.ToInt64(g[6]), Is.EqualTo(3));
            Assert.That((string)g[7], Is.EqualTo("dflt"));
            Assert.That((bool)g[8], Is.True);

            // on_change is sticky and then live (pumped).
            object[] c = env.DoString(@"
__t_seen = {}
__t_s.on_change(function(k, v) __t_seen[#__t_seen + 1] = k end)
local n0 = #__t_seen
__t_s.set('hp', 4)
__dp_pump_net()
return n0, #__t_seen, __t_s.get('hp')
", "net.test3");
            Assert.That(Convert.ToInt64(c[0]), Is.EqualTo(3), "fires for every key already present");
            Assert.That(Convert.ToInt64(c[1]), Is.EqualTo(4));
            Assert.That(Convert.ToDouble(c[2]), Is.EqualTo(4));

            object[] m = env.DoString(@"
local s = __t_s
s.add('dmg', 3); s.add('dmg', 2)
s.union('got', { 'cake', 'hat' }); s.union('got', 'cake')
s.mine('seat', CS.UnityEngine.Vector3(11, 0, 5))
s.max('best', 7); s.max('best', 3)
local seat = s.of('seat')
local hits, lead = 0, nil
local h = dp.on_leader_changed(function(old, new) hits = hits + 1; lead = new end)
__dp_pump_net()
return s.total('dmg'), s.get('dmg'), #s.items('got'), s.has('got', 'hat'), seat.x, s.get('best'), dp.leader(), dp.is_leader(), dp.rank(), hits, lead
", "net.test4");
            Assert.That(Convert.ToDouble(m[0]), Is.EqualTo(5));
            Assert.That(Convert.ToDouble(m[1]), Is.EqualTo(5));
            Assert.That(Convert.ToInt64(m[2]), Is.EqualTo(2));
            Assert.That((bool)m[3], Is.True);
            Assert.That(Convert.ToDouble(m[4]), Is.EqualTo(11).Within(1e-3), "owner values round-trip in world space");
            Assert.That(Convert.ToDouble(m[5]), Is.EqualTo(7));
            Assert.That((string)m[6], Is.EqualTo(PlayerPresence.MyId));
            Assert.That((bool)m[7], Is.True);
            Assert.That(Convert.ToInt64(m[8]), Is.EqualTo(1));
            Assert.That(Convert.ToInt64(m[9]), Is.EqualTo(1), "on_leader_changed is sticky: fires once with the current leader");
            Assert.That((string)m[10], Is.EqualTo(PlayerPresence.MyId));
        }
        finally
        {
            foreach (var s in new List<SharedState>(engine.SharedStates)) s.Dispose();
            DreamParkSession.SwapEngine(previous);
            Object.DestroyImmediate(attraction);
        }
    }

    // ── Wave 2 (Oct 8 2026): solo sync, leader tie-break, rig-scoped leader, ──
    // ── queue-expiry log, replay on re-entry, per-instance manager bus      ──

    static readonly FieldInfo JoinedField = typeof(SessionEngine).GetField("_joined", BindingFlags.Instance | BindingFlags.NonPublic);

    [Test]
    public void EqualJoinTimesBreakOnIdOnEveryDevice()
    {
        var sim = new Sim();
        var a = sim.Add("a"); var b = sim.Add("b");
        var sa = a.State(); var sb = b.State();
        sim.Run(5);
        // Two devices synced on the same tick (Birthday Bash, 0 ms ping): their
        // seniority is equal at the wire's precision (4 decimals) but not to the bit.
        JoinedField.SetValue(a.engine, 12.00003);
        JoinedField.SetValue(b.engine, 12.00004);
        sim.Run(2.1);   // the beats carry the new join times
        Assert.That(a.engine.LeaderId, Is.EqualTo(b.engine.LeaderId), "both devices name the same leader (not each other)");
        Assert.That(a.engine.LeaderId, Is.EqualTo("a"), "equal seniority breaks on id");
        Assert.That(sa.LeaderId, Is.EqualTo(sb.LeaderId), "the shared state's writer agrees too");
        Assert.That(a.engine.Rank("a"), Is.EqualTo(1)); Assert.That(b.engine.Rank("a"), Is.EqualTo(1));
        Assert.That(a.engine.Rank("b"), Is.EqualTo(2)); Assert.That(b.engine.Rank("b"), Is.EqualTo(2));
    }

    [Test]
    public void LeaderIsAPlayerWearingTheTitlesRig()
    {
        var both = new[] { "BirthdayBash", "Zombiez" };
        var sim = new Sim();
        var a = sim.Add("a", titles: both, worn: new[] { "Zombiez" });        // senior, but standing in Zombiez
        var b = sim.Add("b", titles: both, worn: new[] { "BirthdayBash" });
        var c = sim.Add("c", titles: both, worn: new string[0]);              // between zones, no rig worn
        sim.Run(4);
        foreach (var d in sim.devices)
        {
            Assert.That(d.engine.LeaderFor("BirthdayBash"), Is.EqualTo("b"), d.id + ": a device in another title's zone never leads this one");
            Assert.That(d.engine.LeaderFor("Zombiez"), Is.EqualTo("a"), d.id);
            Assert.That(d.engine.LeaderFor(null), Is.EqualTo("a"), d.id + ": the session-wide leader is unchanged");
            Assert.That(d.engine.Rank("b", "BirthdayBash"), Is.EqualTo(1), d.id);
            Assert.That(d.engine.Rank("a", "BirthdayBash"), Is.EqualTo(0), d.id + ": not in this title's room");
            Assert.That(d.engine.Rank("c", "BirthdayBash"), Is.EqualTo(0), d.id);
        }

        // Nobody wears the rig (everyone between zones): every device running the title counts, as before.
        b.worn = new string[0];
        sim.Run(3);
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderFor("BirthdayBash"), Is.EqualTo("a"), d.id);

        // A device that never says what it wears (an older build) keeps the old rule.
        var sim2 = new Sim();
        var x = sim2.Add("x", titles: both);                                   // no worn source
        var y = sim2.Add("y", titles: both, worn: new[] { "BirthdayBash" });
        sim2.Run(4);
        Assert.That(x.engine.LeaderFor("BirthdayBash"), Is.EqualTo("x"));
        Assert.That(y.engine.LeaderFor("BirthdayBash"), Is.EqualTo("x"), "unknown = eligible, the senior one leads");
    }

    [Test]
    public void AHostAloneInItsRoomSyncsAtOnce()
    {
        var sim = new Sim();
        var a = sim.Add("a"); a.alone = true;   // the arbiter hosts and nobody else is on its relay
        var sa = a.State();
        sa.SetNumber("score", 7);               // start() before sync: held, then applied
        sim.Run(0.1);
        Assert.That(a.engine.Synced && a.engine.ClockSynced && a.engine.IsLeader, Is.True, "the session syncs at once, not after the 1.5 s silence");
        Assert.That(sa.Synced && sa.IsLeader, Is.True, "and so does every shared state");
        Assert.That(sa.GetNumber("score", -1), Is.EqualTo(7), "the held write was applied");
    }

    [Test]
    public void AKnownAloneDevicePlaysSoloAtOnceAndKeepsItWhenItHostsAlone()
    {
        var sim = new Sim();
        var a = sim.Add("a", connected: false); a.alone = true;   // listened a full window, heard no host
        var sa = a.State();
        sa.SetString("phase", "fight");
        sim.Run(0.1);
        Assert.That(a.engine.Synced && sa.Synced, Is.True, "solo at once, not after the 6 s grace");
        Assert.That(sa.GetString("phase"), Is.EqualTo("fight"));
        int rev = sa.Rev; uint seed = a.engine.Seed; double joined = a.engine.JoinedAt;

        a.connected = true;                     // it self-hosts; still nobody in the room
        sim.Run(0.5);
        Assert.That(a.engine.Synced && sa.Synced, Is.True, "no un-sync blip when the room it joins is its own, empty one");
        Assert.That(sa.GetString("phase"), Is.EqualTo("fight"));
        Assert.That(sa.Rev, Is.EqualTo(rev));
        Assert.That(a.engine.Seed, Is.EqualTo(seed));
        Assert.That(a.engine.JoinedAt, Is.EqualTo(joined));
    }

    [Test]
    public void ASoloDeviceStillAdoptsTheRoomWhenOneAppears()
    {
        var sim = new Sim();
        var room = sim.Add("b"); var sr = room.State();
        sim.Run(5);
        sr.SetNumber("hp", 9);
        sim.Run(0.3);

        var a = sim.Add("a", connected: false); a.alone = true;
        var sa = a.State();
        sa.SetNumber("hp", 1);
        sim.Run(0.1);
        Assert.That(sa.Synced, Is.True, "alone: plays at once");
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(1));

        a.alone = false; a.connected = true;    // it found a room after all
        sim.Run(3);
        Assert.That(sa.Synced, Is.True);
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(9), "the room's snapshot wins over the solo one");
        Assert.That(sr.GetNumber("hp"), Is.EqualTo(9), "and the solo write never reset the room");
        Assert.That(a.engine.LeaderId, Is.EqualTo("b"));
    }

    [Test]
    public void APlayerJoiningAnEmptyRoomsHostGetsItsSnapshot()
    {
        var sim = new Sim();
        var a = sim.Add("a"); a.alone = true;
        var sa = a.State();
        sa.SetNumber("hp", 4);
        sim.Run(0.1);
        var b = sim.Add("b"); a.alone = false;  // b connected to a's relay
        var sb = b.State();
        sb.SetNumber("hp", 10);                 // its start() tries to reset the encounter
        sim.Run(3);
        Assert.That(sb.Synced, Is.True);
        Assert.That(sb.GetNumber("hp"), Is.EqualTo(4));
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(4));
        Assert.That(b.engine.LeaderId, Is.EqualTo("a"));
    }

    // ── Merge: a solo game meets a room (Oct 11 2026 playtest) ────────
    // "The game vanished and came back, with a physics jolt, when a headset
    // joined": the phone (which cannot host) played solo; a headset booted,
    // heard no host and hosted its own relay; the phone connected to it and
    // dropped its game for the headset's empty one.

    [Test]
    public void MergeWinnerRule()
    {
        Assert.That(SessionEngine.MergeWinner(30, true, "p", 2, false, "h"), Is.True, "the older timeline wins, phone or not");
        Assert.That(SessionEngine.MergeWinner(2, false, "h", 30, true, "p"), Is.False, "and the other side agrees");
        Assert.That(SessionEngine.MergeWinner(5.0, true, "a", 5.6, false, "z"), Is.False, "within the tie window the headset wins");
        Assert.That(SessionEngine.MergeWinner(5.6, false, "z", 5.0, true, "a"), Is.True, "(both sides)");
        Assert.That(SessionEngine.MergeWinner(5.0, false, "a", 5.2, false, "b"), Is.True, "then the lower id");
        Assert.That(SessionEngine.MergeWinner(5.2, false, "b", 5.0, false, "a"), Is.False, "(both sides)");
    }

    /// The playtest: a phone plays solo for 30 s, a headset hosts its own relay
    /// and starts its own (younger) game, then the phone connects to it.
    (Sim sim, Device p, Device h, SharedState sp, SharedState sh) PhoneSoloThenHeadsetRelay(bool simultaneousWrites = false)
    {
        var sim = new Sim();
        var p = sim.Add("p", phone: true, connected: false); p.alone = true;   // phones cannot host: solo
        var sp = p.State();
        sim.Run(0.1);
        Assert.That(p.engine.Synced && sp.Synced, Is.True, "precondition: the phone plays solo");
        sp.SetString("phase", "fight"); sp.SetNumber("hp", 3); sp.SetNumber("wave", 4);
        sim.Run(30);

        // Boots, hears no host, hosts its own relay. Its own clock is far off the
        // phone's, so adopting it would be a visible jump.
        var h = sim.Add("h", clockSkew: 500); h.alone = true;
        var sh = h.State();
        sim.Run(0.1);
        // Its game writes more than the phone's did (a higher rev must not win).
        sh.SetString("phase", "idle"); sh.SetNumber("hp", 10); sh.SetNumber("wave", 1); sh.SetNumber("a", 1); sh.SetNumber("b", 1);
        sim.Run(1);
        Assert.That(h.engine.Synced && sh.Synced && sh.Rev > sp.Rev, Is.True, "precondition: the headset runs its own younger game, at a higher rev");

        h.alone = false; p.alone = false; p.connected = true;   // the phone finds the relay
        if (simultaneousWrites) { sp.SetNumber("score", 1); sh.SetNumber("score", 2); }
        return (sim, p, h, sp, sh);
    }

    [Test]
    public void APhonePlayingSoloKeepsItsGameWhenALoneHeadsetRelayAppears()
    {
        var (sim, p, h, sp, sh) = PhoneSoloThenHeadsetRelay(simultaneousWrites: true);
        int rev = sp.Rev; uint seed = p.engine.Seed;
        var changes = new List<string>();
        sp.Changed += (k, v) => changes.Add(k);

        bool blinked = false; double maxJump = 0, last = p.engine.Now - sim.t;
        for (int i = 0; i < 200; i++)
        {
            sim.Run(0.02);
            if (!sp.Synced || !p.engine.Synced) blinked = true;
            double off = p.engine.Now - sim.t;
            maxJump = Math.Max(maxJump, Math.Abs(off - last));
            last = off;
        }
        Assert.That(blinked, Is.False, "the phone's game never blinked out");
        Assert.That(changes, Is.Empty, "not one value changed on the phone: nothing respawned");
        Assert.That(sp.Rev, Is.EqualTo(rev), "its table was never replaced");
        Assert.That(maxJump, Is.LessThan(0.05), "and its clock never jumped");
        Assert.That(p.engine.Seed, Is.EqualTo(seed));

        Assert.That(h.engine.Synced && sh.Synced, Is.True);
        Assert.That(h.engine.Seed, Is.EqualTo(seed), "the headset adopted the phone's seed");
        Assert.That(Math.Abs(h.engine.Now - p.engine.Now), Is.LessThan(0.05), "and its clock");
        Assert.That(sh.GetString("phase"), Is.EqualTo("fight"), "and its game");
        Assert.That(sh.GetNumber("hp"), Is.EqualTo(3));
        Assert.That(sh.GetNumber("wave"), Is.EqualTo(4));
        Assert.That(sh.Has("a") || sh.Has("b"), Is.False, "nothing of the younger game survives");
        Assert.That(sh.GetNumber("score") == 1 && sp.GetNumber("score") == 1, Is.True,
            "simultaneous writes at the moment of joining: the seeding side's stands, everywhere");
        Assert.That(sh.Rev, Is.EqualTo(sp.Rev));
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("h"), d.id + ": once synced the headset leads, as always");

        // The game goes on: a phone write is applied by the headset leader and reaches both.
        sp.SetNumber("hp", 2);
        sim.Run(0.3);
        Assert.That(sh.GetNumber("hp"), Is.EqualTo(2));
        Assert.That(sp.GetNumber("hp"), Is.EqualTo(2));
        Assert.That(sh.Rev, Is.EqualTo(rev + 1));
    }

    [Test]
    public void AfterTheMergeALateJoinerGetsTheGameAndTheHostCanLeave()
    {
        var (sim, p, h, sp, sh) = PhoneSoloThenHeadsetRelay();
        sim.Run(4);
        Assert.That(sh.GetNumber("hp"), Is.EqualTo(3), "precondition: merged");

        // Late join: a second headset arrives and its start() tries to reset the encounter.
        var c = sim.Add("c");
        var sc = c.State();
        sc.SetString("phase", "idle"); sc.SetNumber("hp", 10);
        sim.Run(3);
        Assert.That(c.engine.Synced && sc.Synced, Is.True);
        Assert.That(sc.GetString("phase"), Is.EqualTo("fight"), "the late joiner renders the merged game");
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(3));
        Assert.That(sp.GetNumber("hp"), Is.EqualTo(3), "and never reset it");
        Assert.That(c.engine.Seed, Is.EqualTo(p.engine.Seed));
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("h"), d.id + ": the incumbent keeps the lead");

        // The host leaves: its relay goes with it; c hosts a new one and p reconnects.
        int rev = sp.Rev;
        var pChanges = new List<string>();
        sp.Changed += (k, v) => pChanges.Add(k);
        h.Leave(clean: true);
        p.connected = false; c.connected = false;
        sim.Run(0.5);
        c.connected = true; p.connected = true;
        bool blinked = false;
        for (int i = 0; i < 150; i++) { sim.Run(0.02); if (!sp.Synced || !sc.Synced || !p.engine.Synced) blinked = true; }
        Assert.That(blinked, Is.False, "a short relay hand-off is not a merge: nobody un-syncs");
        Assert.That(pChanges, Is.Empty, "and nothing respawned");
        Assert.That(sp.Rev, Is.EqualTo(rev));
        foreach (var d in new[] { p, c }) Assert.That(d.engine.LeaderId, Is.EqualTo("c"), d.id + ": the remaining headset leads");
        sp.SetNumber("hp", 1);
        sim.Run(0.3);
        Assert.That(sc.GetNumber("hp"), Is.EqualTo(1), "the new leader applies writes");
        Assert.That(sp.GetNumber("hp"), Is.EqualTo(1));
    }

    [Test]
    public void AnOlderSoloDeviceStillAdoptsAnEstablishedRoom()
    {
        var sim = new Sim();
        var x = sim.Add("x", connected: false); x.alone = true;
        var sx = x.State();
        sim.Run(0.1);
        sx.SetNumber("hp", 1);
        sim.Run(30);                                // solo for a long time

        var a = sim.Add("a"); var b = sim.Add("b");  // two players already together
        var sa = a.State(); b.State();
        sim.Run(5);
        sa.SetNumber("hp", 9);
        sim.Run(0.5);

        x.alone = false; x.connected = true;
        sim.Run(4);
        Assert.That(sx.Synced, Is.True);
        Assert.That(sx.GetNumber("hp"), Is.EqualTo(9), "an established room wins, however old the solo game");
        Assert.That(sa.GetNumber("hp"), Is.EqualTo(9));
        Assert.That(x.engine.Seed, Is.EqualTo(a.engine.Seed));
    }

    [Test]
    public void TheSideHoldingStateSeedsAnOlderEmptyRoom()
    {
        var sim = new Sim();
        var h = sim.Add("h"); h.alone = true;       // an idle headset, hosting for a while
        var sh = h.State();
        sim.Run(30);
        Assert.That(sh.Synced && sh.Rev == 0, Is.True, "precondition: its room holds nothing");

        var p = sim.Add("p", phone: true, connected: false); p.alone = true;
        var sp = p.State();
        sim.Run(0.1);
        sp.SetString("phase", "fight"); sp.SetNumber("hp", 3);
        sim.Run(5);                                 // a short solo game

        h.alone = false; p.alone = false; p.connected = true;
        var changes = new List<string>();
        sp.Changed += (k, v) => changes.Add(k);
        sim.Run(4);
        Assert.That(p.engine.LeaderId, Is.EqualTo("h"));
        Assert.That(p.engine.Seed, Is.EqualTo(h.engine.Seed), "the older timeline (clock, seed) wins");
        Assert.That(sp.Synced && sh.Synced, Is.True);
        Assert.That(changes, Is.Empty, "the phone's game was never replaced by the empty room");
        Assert.That(sp.GetString("phase"), Is.EqualTo("fight"));
        Assert.That(sh.GetString("phase"), Is.EqualTo("fight"), "the side holding state seeded the room");
        Assert.That(sh.GetNumber("hp"), Is.EqualTo(3));
        Assert.That(sh.Rev, Is.EqualTo(sp.Rev));
    }

    [Test]
    public void AnUnsyncedHeadsetAdoptsASyncedPhonesTimeline()
    {
        var sim = new Sim();
        var p = sim.Add("p", phone: true);
        var sp = p.State();
        sim.Run(3);
        sp.SetNumber("hp", 5);
        sim.Run(0.3);
        var h = sim.Add("h");
        var sh = h.State();
        sim.Run(3);
        Assert.That(h.engine.Synced, Is.True, "it used to rank itself first and never take the phone's beat");
        Assert.That(h.engine.Seed, Is.EqualTo(p.engine.Seed));
        Assert.That(Math.Abs(h.engine.Now - p.engine.Now), Is.LessThan(0.05));
        Assert.That(sh.GetNumber("hp"), Is.EqualTo(5));
        foreach (var d in sim.devices) Assert.That(d.engine.LeaderId, Is.EqualTo("h"), "then leads it, as a synced headset");
    }

    // Oct 11 2026: the phone guest showed on one headset only — its presence beat the
    // fire-and-forget Phone Player template load, the RemotePlayer "built" to nothing
    // and never retried. RegisterTemplate now re-arms every affected live peer.
    [Test]
    public void ALateRemoteRigTemplateRebuildsPeersAlreadyBuilt()
    {
        Assert.That(RemoteRig.TemplateAffects("G", "phone", "G", "phone"), Is.True);
        Assert.That(RemoteRig.TemplateAffects("G", "headset", "G", "phone"), Is.False, "another device keeps its rig");
        Assert.That(RemoteRig.TemplateAffects("H", "phone", "G", "phone"), Is.False, "another game keeps its rig");
        Assert.That(RemoteRig.TemplateAffects("", "phone", "", "phone"), Is.False, "no game: nothing to build");

        var go = new GameObject("RemotePlayer (template test)");
        try
        {
            var rp = go.AddComponent<RemotePlayer>();
            var built = typeof(RemotePlayer).GetField("_builtForGame", BindingFlags.NonPublic | BindingFlags.Instance);
            var next = typeof(RemotePlayer).GetField("_nextBuildTry", BindingFlags.NonPublic | BindingFlags.Instance);
            built.SetValue(rp, "G");      // built (to nothing) before the template arrived
            next.SetValue(rp, 99f);
            rp.TemplateChanged();
            Assert.That(built.GetValue(rp), Is.Null, "the finished build is re-armed");
            Assert.That((float)next.GetValue(rp), Is.EqualTo(0f), "and retried on the next frame");
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void ExpiredQueueWarningsAreThrottledWhileNeverConnected()
    {
        var go = new GameObject("DreamBox (expiry test)");
        var client = go.AddComponent<DreamBoxClient>();
        float now = 0;
        typeof(DreamBoxClient).GetField("_timeOverride", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(client, (Func<float>)(() => now));
        var enqueue = typeof(DreamBoxClient).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic);
        var lines = new List<string>();
        Application.LogCallback cb = (msg, st, type) => { if (msg.Contains("[DreamBox]") && msg.Contains("expired")) lines.Add(msg); };
        Application.logMessageReceived += cb;
        try
        {
            // A client that never connects while content keeps sending (5 msg/s for 2 minutes).
            for (int i = 0; i <= 600; i++) { now = i * 0.2f; enqueue.Invoke(client, new object[] { "boss", "{}" }); }
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1), "the first expiry is still reported");
            Assert.That(lines.Count, Is.LessThanOrEqualTo(1 + 120 / 30), "then at most one summary per 30 s, not one line per message (was " + lines.Count + ")");
            StringAssert.Contains("total", lines[lines.Count - 1], "the summary carries the accumulated count");
        }
        finally
        {
            Application.logMessageReceived -= cb;
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void QuickReEntryNeverReplaysTheLastVisitsMessages()
    {
        NetRegistry.Clear();
        string Msg(string k) => "{\"type\":\"" + k + "\",\"payload\":{\"netId\":4242}}";
        NetId first = null, again = null, fresh = null;
        try
        {
            first = Registered("Board (first visit)", 4242);
            int firstGot = 0;
            first.OnNetEvent += _ => firstGot++;
            NetRegistry.Dispatch(4242, Msg("score"));
            Assert.That(firstGot, Is.EqualTo(1));

            NetRegistry.Unregister(4242, first);   // the level is torn down (NetId.OnDestroy)
            // A peer still on the board keeps sending while this headset walks away and back (< TTL).
            NetRegistry.Dispatch(4242, Msg("ball"));
            NetRegistry.Dispatch(4242, Msg("game_over"));
            again = Registered("Board (re-entered)", 4242);
            var got = new List<string>();
            again.OnNetEvent += p => got.Add(p);
            Assert.That(got, Is.Empty, "the previous visit's traffic is not replayed into the new instance");
            NetRegistry.Dispatch(4242, Msg("live"));
            Assert.That(got.Count, Is.EqualTo(1), "live traffic flows");

            // An id that never registered before still gets its backlog (the join-time burst).
            NetRegistry.Dispatch(5151, "{\"type\":\"hello\",\"payload\":{\"netId\":5151}}");
            fresh = Registered("New level", 5151);
            int freshGot = 0;
            fresh.OnNetEvent += _ => freshGot++;
            Assert.That(freshGot, Is.EqualTo(1));
        }
        finally
        {
            foreach (var n in new[] { first, again, fresh }) if (n != null) Object.DestroyImmediate(n.gameObject);
            NetRegistry.Clear();
        }
    }

#if DREAMPARKCORE
    [Test]
    public void EachPackageInstanceGetsItsOwnManagerBus()
    {
        var stamp = typeof(DreamParkPackageRuntime).GetMethod("StampPackageScope", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var titleOf = typeof(SharedState).GetMethod("TitleOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var made = new List<GameObject>();
        uint Manager(string instance, out GameObject go)
        {
            go = new GameObject("Game Manager");
            made.Add(go);
            var nid = go.AddComponent<NetId>();
            stamp.Invoke(null, stamp.GetParameters().Length >= 4
                ? new object[] { go, "CrashCourse", "manager", instance }
                : new object[] { go, "CrashCourse", "manager" });
            return nid.PreviewId();
        }
        try
        {
            uint a = Manager("pkgi-track-a", out var goA);
            uint b = Manager("pkgi-track-b", out _);
            uint a2 = Manager("pkgi-track-a", out _);
            Assert.That(a, Is.Not.EqualTo(b), "two package instances of one title get different manager buses (CC7)");
            Assert.That(a2, Is.EqualTo(a), "one instance (park-document id) hashes the same on every headset and across an Adventure↔Sequence replacement");
            Assert.That((string)titleOf.Invoke(null, new object[] { goA }), Is.EqualTo("CrashCourse"), "SharedState.TitleOf still parses the title");
            uint d1 = Manager("CrashCourse:adventure", out _);
            uint d2 = Manager("CrashCourse:sequence", out _);
            Assert.That(d1, Is.EqualTo(d2), "an instance without a park id keeps the per-title bus, whatever kind it fell back to");
        }
        finally { foreach (var g in made) Object.DestroyImmediate(g); }
    }
#endif
}
