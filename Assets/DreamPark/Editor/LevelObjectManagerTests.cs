using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DreamPark.ParkBuilder;
using NUnit.Framework;
using UnityEngine;

// Oct 2026 watch item: ParticleSystemSettings trusted playOnAwake on an object's FIRST park
// even when that park came long after spawn, so a system the game had stopped replayed on
// the first re-approach. playOnAwake is now trusted only within the spawn frame (+1).
public sealed class LevelObjectManagerTests
{
    GameObject _go;

    bool? _objectsEnabledBefore;

    [TearDown]
    public void TearDown()
    {
        LevelObject.ParticleSystemSettings.FrameOverride = -1;
        if (_go != null) Object.DestroyImmediate(_go);
        _go = null;
        if (_objectsEnabledBefore.HasValue) LevelObjectManager.objectsEnabled = _objectsEnabledBefore.Value;
        _objectsEnabledBefore = null;
        if (_paused) { DreamPark.GamePause.ResetForTests(); _paused = false; }
        if (_lomGo != null)
        {
            LevelObjectManager.Instance = _lomBefore;
            Object.DestroyImmediate(_lomGo);
            _lomGo = null;
            _lomBefore = null;
        }
        if (_opt != null) Object.DestroyImmediate(_opt);
        _opt = null;
        ClearStatic("s_ignoreRequests");
        ClearStatic("s_ignoreWarnedTitles");
    }

    static void ClearStatic(string field)
    {
        var f = typeof(LevelObjectManager).GetField(field, BindingFlags.Static | BindingFlags.NonPublic);
        var v = f?.GetValue(null);
        v?.GetType().GetMethod("Clear")?.Invoke(v, null);
    }

    ParticleSystem MakeStoppedPlayOnAwakeSystem()
    {
        _go = new GameObject("LOM Particle Test") { hideFlags = HideFlags.HideAndDontSave };
        var ps = _go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.playOnAwake = true;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);   // the game stopped it
        Assert.That(ps.isPlaying, Is.False);
        return ps;
    }

    [Test]
    public void FirstParkInTheSpawnFrameTrustsPlayOnAwake()
    {
        var ps = MakeStoppedPlayOnAwakeSystem();
        LevelObject.ParticleSystemSettings.FrameOverride = 100;
        var settings = new LevelObject.ParticleSystemSettings(ps);
        LevelObject.ParticleSystemSettings.FrameOverride = 101;   // same/next frame: Unity hasn't run it yet
        Assert.That(settings.ReadPlayingForPark(), Is.True);
    }

    [Test]
    public void FirstParkLongAfterSpawnReadsTheLiveStateSoAStoppedSystemStaysStopped()
    {
        var ps = MakeStoppedPlayOnAwakeSystem();
        LevelObject.ParticleSystemSettings.FrameOverride = 100;
        var settings = new LevelObject.ParticleSystemSettings(ps);
        LevelObject.ParticleSystemSettings.FrameOverride = 100 + 60 * 120;   // first cull two minutes later
        // Old code: firstPark ? (isPlaying || isPaused || playOnAwake) → true → replayed on restore.
        Assert.That(settings.ReadPlayingForPark(), Is.False);
        Assert.That(settings.Toggle(false), Is.True);
        Assert.That(settings.enabled, Is.False, "parked as stopped");
        Assert.That(settings.Toggle(true), Is.True);
        Assert.That(ps.isPlaying, Is.False, "restore must not replay a system the game stopped");
    }

    // ── Lua message relays across park cycles (Oct 2026, BirthdayBash cake clip) ──────────────
    //
    // On device core spawns live content (LiveParkContent: objectsEnabled already true) AFTER its
    // scripts booted, so a LuaBehaviour's relays (LuaLateUpdateRelay, LuaFixedUpdateRelay, …)
    // already exist when RegisterLevelObject snapshots the object. They used to get their own
    // ComponentSettings: on park the host was disabled first (its OnDisable turned the relays
    // off), then each relay's entry re-read its live state — OFF — and on unpark the host's
    // OnEnable turned the relays on and their entries immediately restored OFF. lateupdate(),
    // fixedupdate() and the zone hooks were dead for the rest of the session. The Editor's Park
    // Simulator boots scripts after registration, so it never showed.
    //
    // EditMode: runInEditMode makes Unity deliver the host's Awake/OnEnable/OnDisable to this one
    // instance, and the frame messages are dispatched by hand under Unity's own rule (Update-family
    // messages reach only active, enabled behaviours; trigger/collision messages also reach
    // disabled ones, by design — so ontriggerenter survives either way, and is asserted to).

    const string RelayScript = @"
late, fixed, trig = 0, 0, 0
function lateupdate() late = late + 1 end
function fixedupdate() fixed = fixed + 1 end
function ontriggerenter(other) trig = trig + 1 end
function onzoneenter() end
";

    LuaBehaviour MakeBootedScript()
    {
        if (LevelObjectManager.ParkContentLocked) Assert.Ignore("a park load holds the park-content lock in this editor");
        if (DreamPark.GamePause.IsPaused) Assert.Ignore("the game is paused in this editor");
        _objectsEnabledBefore = LevelObjectManager.objectsEnabled;
        LevelObjectManager.objectsEnabled = true;   // a live park: scripts boot at spawn

        _go = new GameObject("LOM Relay Test") { hideFlags = HideFlags.HideAndDontSave };
        var lb = _go.AddComponent<LuaBehaviour>();
        lb.luaScript = new TextAsset(RelayScript) { name = "lom_relay_test" };
        lb.runInEditMode = true;
        lb.EnsureBooted();
        Assert.That(lb.ScriptScope, Is.Not.Null, "script booted");

        var relays = Relays();
        Assert.That(relays.Count, Is.EqualTo(4), "lateupdate, fixedupdate, ontriggerenter and onzoneenter relays wired at boot");
        Assert.That(relays.All(r => r.enabled), Is.True, "relays on after boot");
        // The host must really get OnDisable/OnEnable here, or this test proves nothing.
        lb.enabled = false;
        if (relays.Any(r => r.enabled)) Assert.Inconclusive("Unity did not deliver the host's OnDisable in edit mode");
        lb.enabled = true;
        Assert.That(relays.All(r => r.enabled), Is.True, "host OnEnable re-enables every relay");
        return lb;
    }

    List<Behaviour> Relays() => _go.GetComponents<LuaRelay>().Cast<Behaviour>().ToList();

    static void Dispatch(Component c, string message, bool sentToDisabled, params object[] args)
    {
        var b = (Behaviour)c;
        if (!b.gameObject.activeInHierarchy) return;
        if (!sentToDisabled && !b.enabled) return;
        b.GetType().GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(b, args);
    }

    static int Count(LuaBehaviour lb, string name) => lb.ScriptScope.Get<int>(name);

    void Tick()
    {
        Dispatch(_go.GetComponent<LuaLateUpdateRelay>(), "LateUpdate", false);
        Dispatch(_go.GetComponent<LuaFixedUpdateRelay>(), "FixedUpdate", false);
        Dispatch(_go.GetComponent<LuaTriggerEnterRelay>(), "OnTriggerEnter", true, new object[] { null });
    }

    [Test]
    public void RelaysOfAScriptBootedBeforeRegistrationSurviveParkCycles()
    {
        var lb = MakeBootedScript();

        // Device order: booted, THEN registered (LevelAnchor.SetupNewObject → RegisterLevelObject(go, true)).
        var lo = new LevelObject(_go);
        lo.Disable();   // startDisabled

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            int late = Count(lb, "late"), fix = Count(lb, "fixed");
            Tick();
            Assert.That(Count(lb, "late"), Is.EqualTo(late), $"cycle {cycle}: lateupdate gated while parked");
            Assert.That(Count(lb, "fixed"), Is.EqualTo(fix), $"cycle {cycle}: fixedupdate gated while parked");

            lo.Enable(true);   // unpark
            Assert.That(lb.enabled, Is.True, $"cycle {cycle}: host restored");
            var off = Relays().Where(r => !r.enabled).Select(r => r.GetType().Name).ToList();
            Assert.That(off, Is.Empty, $"cycle {cycle}: relays left disabled after unpark: {string.Join(", ", off)}");

            int trig = Count(lb, "trig");
            late = Count(lb, "late"); fix = Count(lb, "fixed");
            Tick();
            Assert.That(Count(lb, "late"), Is.EqualTo(late + 1), $"cycle {cycle}: lateupdate fires after unpark");
            Assert.That(Count(lb, "fixed"), Is.EqualTo(fix + 1), $"cycle {cycle}: fixedupdate fires after unpark");
            Assert.That(Count(lb, "trig"), Is.EqualTo(trig + 1), $"cycle {cycle}: ontriggerenter fires after unpark");

            lo.Disable();   // park again (cull / build mode)
            Assert.That(Relays().All(r => !r.enabled), Is.True, $"cycle {cycle}: relays follow the parked host");
        }
    }

    [Test]
    public void AScriptTheGameDisabledKeepsItsRelaysOffAcrossParkCycles()
    {
        var lb = MakeBootedScript();
        var lo = new LevelObject(_go);
        lb.enabled = false;   // the game switched the script off while live

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            lo.Disable();
            lo.Enable(true);
            Assert.That(lb.enabled, Is.False, $"cycle {cycle}: live state re-read on the way out");
            Assert.That(Relays().All(r => !r.enabled), Is.True, $"cycle {cycle}: relays stay with their disabled host");
            int late = Count(lb, "late");
            Tick();
            Assert.That(Count(lb, "late"), Is.EqualTo(late), $"cycle {cycle}: no lateupdate on a disabled script");
        }
    }

    [Test]
    public void OtherBehavioursStillKeepTheirLiveStateAcrossParkCycles()
    {
        MakeBootedScript();
        var light = _go.AddComponent<Light>();
        var audio = _go.AddComponent<AudioSource>();
        var lo = new LevelObject(_go);
        light.enabled = false;   // changed by the game after registration

        lo.Disable();
        Assert.That(audio.enabled, Is.False, "parked");
        lo.Enable(true);
        Assert.That(light.enabled, Is.False, "a light the game switched off stays off");
        Assert.That(audio.enabled, Is.True, "an enabled behaviour comes back");
        Assert.That(lo.components.Any(c => c.component is LuaRelay), Is.False, "relays are owned by their host, not snapshotted");
        Assert.That(lo.components.Any(c => c.component is LuaBehaviour), Is.True, "the host itself is still managed");
    }

    // ── dp.enabled / dp.set_enabled: the INTENDED state across a park (Oct 2026) ─────────────
    //
    // Parking writes enabled=false onto the live components, so a script reading `.enabled` in
    // start() while the object it inspects is parked (still loading, or culled by distance) read
    // the PARK. BirthdayBash's Cake Boss recorded every rig renderer as "should be off" that way
    // and never rendered. dp.enabled returns what the unpark will restore; dp.set_enabled writes
    // it, closing the "a write made while parked is not seen" limit for content that opts in.
    // Content that never calls them is untouched (OtherBehavioursStillKeepTheirLiveState… and
    // ADpLiveObjectBehavesExactlyLikeEnabled guard that).

    GameObject _lomGo;
    LevelObjectManager _lomBefore;
    OptimizationSettings _opt;
    bool _paused;

    LevelObjectManager MakeManager()
    {
        if (LevelObjectManager.ParkContentLocked) Assert.Ignore("a park load holds the park-content lock in this editor");
        if (DreamPark.GamePause.IsPaused) Assert.Ignore("the game is paused in this editor");
        _objectsEnabledBefore = LevelObjectManager.objectsEnabled;
        LevelObjectManager.objectsEnabled = true;
        _lomBefore = LevelObjectManager.Instance;
        _lomGo = new GameObject("LOM Intended Test Manager") { hideFlags = HideFlags.HideAndDontSave };
        var lom = _lomGo.AddComponent<LevelObjectManager>();
        LevelObjectManager.Instance = lom;   // Awake does this in play mode
        return lom;
    }

    // An authored object: a visible mesh, a solid collider, a lit lamp, and one AudioSource the
    // creator shipped switched OFF (so "intended" is not simply "true").
    (MeshRenderer r, BoxCollider c, Light l, AudioSource offAudio) MakeAuthored()
    {
        _go = new GameObject("LOM Intended Test") { hideFlags = HideFlags.HideAndDontSave };
        _go.AddComponent<MeshFilter>();
        var r = _go.AddComponent<MeshRenderer>();
        var c = _go.AddComponent<BoxCollider>();
        var l = _go.AddComponent<Light>();
        var a = _go.AddComponent<AudioSource>();
        a.enabled = false;
        return (r, c, l, a);
    }

    static XLua.LuaEnv Env()
    {
        typeof(DreamPark.DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return LuaBehaviour.GetLuaEnv();
    }

    static object Lua(string code, params (string name, object value)[] globals)
    {
        var env = Env();
        foreach (var g in globals) env.Global.Set(g.name, g.value);
        try { return env.DoString(code)?.FirstOrDefault(); }
        finally { foreach (var g in globals) env.Global.Set<string, object>(g.name, null); }
    }

    static bool? DpEnabled(Component c) => (bool?)Lua("return dp.enabled(__t_c)", ("__t_c", c));
    static bool DpSetEnabled(Component c, bool on) => (bool)Lua("return dp.set_enabled(__t_c, __t_on)", ("__t_c", c), ("__t_on", on));

    LevelObject Registered(LevelObjectManager lom) => lom.levelObjects.Single(lo => lo.gameObject == _go);

    [Test]
    public void DpEnabledOnAParkedObjectReturnsTheAuthoredValue()
    {
        var lom = MakeManager();
        var (r, c, l, offAudio) = MakeAuthored();
        lom.RegisterLevelObject(_go, startDisabled: true);   // the load parks it on arrival
        var lo = Registered(lom);

        // Load park (no settings: behaviours and colliders, not renderers).
        Assert.That(c.enabled || l.enabled, Is.False, "premise: the load park reads false");
        Assert.That(DpEnabled(c), Is.True, "load park: dp.enabled(collider) = authored on");
        Assert.That(DpEnabled(l), Is.True, "load park: dp.enabled(light) = authored on");
        Assert.That(DpEnabled(offAudio), Is.False, "load park: dp.enabled(audio shipped off) = authored off");
        Assert.That(DpEnabled(r), Is.True, "load park leaves renderers live: dp.enabled = .enabled");

        _opt = ScriptableObject.CreateInstance<OptimizationSettings>();
        lo.Enable(true, _opt, 0);
        lo.DisableAndHide(_opt);   // distance culling: renderers hidden too (level 2)

        // The bug: every live flag reads the PARK.
        Assert.That(r.enabled, Is.False, "premise: a parked renderer reads false");
        Assert.That(c.enabled, Is.False, "premise: a parked collider reads false");
        Assert.That(l.enabled, Is.False, "premise: a parked light reads false");

        Assert.That(DpEnabled(r), Is.True, "dp.enabled(renderer) = authored on");
        Assert.That(DpEnabled(c), Is.True, "dp.enabled(collider) = authored on");
        Assert.That(DpEnabled(l), Is.True, "dp.enabled(light) = authored on");
        Assert.That(DpEnabled(offAudio), Is.False, "dp.enabled(audio shipped off) = authored off");

        // Reading changes nothing.
        Assert.That(r.enabled || c.enabled || l.enabled, Is.False, "still parked after the reads");
        lo.Enable(true, _opt, 0);
        Assert.That(r.enabled && c.enabled && l.enabled, Is.True, "unpark restores the authored state");
        Assert.That(offAudio.enabled, Is.False, "an authored-off component stays off");
    }

    [Test]
    public void SetEnabledWhileParkedSurvivesTheUnpark()
    {
        var lom = MakeManager();
        var (r, c, l, offAudio) = MakeAuthored();
        lom.RegisterLevelObject(_go);
        var lo = Registered(lom);
        _opt = ScriptableObject.CreateInstance<OptimizationSettings>();
        lo.DisableAndHide(_opt);   // culled by distance

        // The documented limit, unchanged for content that doesn't opt in: a raw write while
        // parked is overwritten by the restore.
        offAudio.enabled = true;
        lo.Enable(true, _opt, 0);
        Assert.That(offAudio.enabled, Is.False, "raw write while parked is still lost (known limit, behaviour unchanged)");
        lo.DisableAndHide(_opt);

        Assert.That(DpSetEnabled(r, false), Is.True);
        Assert.That(DpSetEnabled(c, false), Is.True);
        Assert.That(DpSetEnabled(l, false), Is.True);
        Assert.That(DpSetEnabled(offAudio, true), Is.True);
        Assert.That(r.enabled || c.enabled || l.enabled || offAudio.enabled, Is.False, "everything stays parked until the unpark");
        Assert.That(DpEnabled(r), Is.False, "the read sees the write");
        Assert.That(DpEnabled(offAudio), Is.True, "the read sees the write");

        lo.Enable(true, _opt, 0);
        Assert.That(r.enabled, Is.False, "renderer hidden while parked stays hidden");
        Assert.That(c.enabled, Is.False, "collider switched off while parked stays off");
        Assert.That(l.enabled, Is.False, "light switched off while parked stays off");
        Assert.That(offAudio.enabled, Is.True, "audio switched on while parked comes on at the unpark");

        // A simplified park (level 1) still draws: set_enabled follows the same rule now.
        Assert.That(DpSetEnabled(r, true), Is.True, "live write");
        Assert.That(r.enabled, Is.True);
        lo.DisableAndSimplifyRendering(_opt);
        Assert.That(r.enabled, Is.True, "premise: level 1 keeps an enabled renderer drawn");
        DpSetEnabled(r, false);
        Assert.That(r.enabled, Is.False, "hidden at once in a simplified park");
        DpSetEnabled(r, true);
        Assert.That(r.enabled, Is.True, "drawn again in a simplified park");
        lo.Enable(true, _opt, 0);
        Assert.That(r.enabled, Is.True);
    }

    [Test]
    public void ADpLiveObjectBehavesExactlyLikeEnabled()
    {
        var lom = MakeManager();
        var (r, c, l, offAudio) = MakeAuthored();
        lom.RegisterLevelObject(_go);   // registered, live
        var lo = Registered(lom);

        Assert.That(DpEnabled(r), Is.True);
        Assert.That(DpEnabled(offAudio), Is.False);
        Assert.That(DpSetEnabled(l, false), Is.True);
        Assert.That(l.enabled, Is.False, "live write lands at once");
        Assert.That(DpSetEnabled(c, false), Is.True);
        Assert.That(c.enabled, Is.False);
        Assert.That(DpEnabled(c), Is.False);

        // The re-read rule still applies to a live write.
        lo.Disable();
        lo.Enable(true);
        Assert.That(l.enabled, Is.False, "live write survives a park cycle");
        Assert.That(c.enabled, Is.False, "live write survives a park cycle");

        // Not registered at all (a hand-authored scene): plain .enabled.
        lom.UnregisterLevelObject(_go);
        Assert.That(DpEnabled(l), Is.False);
        DpSetEnabled(l, true);
        Assert.That(l.enabled, Is.True);

        // No manager (the SDK Editor before a park exists): plain .enabled.
        LevelObjectManager.Instance = null;
        Assert.That(DpEnabled(r), Is.True);
        DpSetEnabled(r, false);
        Assert.That(r.enabled, Is.False);

        // Anything without an enabled flag: nil / false, never an error.
        Assert.That(Lua("return dp.enabled(__t_c) == nil", ("__t_c", _go.transform)), Is.True, "Transform → nil");
        Assert.That(Lua("return dp.enabled(__t_c) == nil", ("__t_c", _go)), Is.True, "GameObject → nil");
        Assert.That(Lua("return dp.enabled(nil) == nil"), Is.True, "nil → nil");
        Assert.That(Lua("return dp.set_enabled(__t_c, true)", ("__t_c", _go)), Is.False, "GameObject → false");
    }

    [Test]
    public void DpEnabledSeesThroughTheGamePauseFreeze()
    {
        var lom = MakeManager();
        var (r, c, l, offAudio) = MakeAuthored();
        var script = _go.AddComponent<LuaBehaviour>();   // a MonoBehaviour the freeze switches off (no script: nothing boots)
        var offCollider = _go.AddComponent<SphereCollider>();
        offCollider.enabled = false;   // the game had this one off before the pause
        lom.RegisterLevelObject(_go);

        _paused = true;
        Assert.That(DreamPark.GamePause.Pause(new DreamPark.GamePause.Info { source = "editor" }), Is.True);
        Assert.That(c.enabled || script.enabled, Is.False, "premise: the freeze switches colliders and scripts off");
        Assert.That(DpEnabled(c), Is.True, "frozen collider: intended on");
        Assert.That(DpEnabled(script), Is.True, "frozen script: intended on");
        Assert.That(DpEnabled(offCollider), Is.False, "a collider off before the pause: intended off");
        Assert.That(DpEnabled(l), Is.True, "lights are not frozen: live");

        DpSetEnabled(c, false);
        DpSetEnabled(offCollider, true);
        Assert.That(offCollider.enabled, Is.False, "stays frozen until the resume");

        DreamPark.GamePause.Resume("editor");
        _paused = false;
        Assert.That(c.enabled, Is.False, "switched off while frozen stays off");
        Assert.That(offCollider.enabled, Is.True, "switched on while frozen comes on at the resume");
        Assert.That(script.enabled, Is.True, "the rest thaws as before");
    }

    // ── -- @optimizer-ignore / dp.optimizer_ignore (Oct 2026) ─────────────────────────────────
    //
    // The Lua equivalent of an OptimizedAFIgnore component for managers and always-on systems:
    // the distance sweep (OptimizedAF.RunOptimizedFrame) skips a LevelObject whose
    // ignoreOptimization is set — that flag is the sweep's only gate, so it is what these assert.

    const string IgnoredScript = "-- @optimizer-ignore  (the boss brain keeps thinking when you walk away)\nfunction start() end\n";

    // HideAndDontSave on every test object: nothing may touch (or dirty) the open scene.
    static GameObject Child(string name, GameObject parent)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    static LuaBehaviour AddScript(GameObject go, string source)
    {
        var lb = go.AddComponent<LuaBehaviour>();
        lb.luaScript = new TextAsset(source) { name = "lom_ignore_test" };
        return lb;
    }

    [Test]
    public void TheMarkerMustBeInALuaComment()
    {
        Assert.That(LevelObjectManager.ScriptHasOptimizerIgnore(IgnoredScript), Is.True);
        Assert.That(LevelObjectManager.ScriptHasOptimizerIgnore("x = 1\n  -- always on. @optimizer-ignore\n"), Is.True);
        Assert.That(LevelObjectManager.ScriptHasOptimizerIgnore("local s = '@optimizer-ignore'"), Is.False);
        Assert.That(LevelObjectManager.ScriptHasOptimizerIgnore("-- @packing-ignore\nfunction start() end"), Is.False);
        Assert.That(LevelObjectManager.ScriptHasOptimizerIgnore(null), Is.False);
    }

    [Test]
    public void TheAnnotationIsHonouredAtFirstRegistration()
    {
        var lom = MakeManager();
        _go = new GameObject("LOM Ignore Test") { hideFlags = HideFlags.HideAndDontSave };

        // On the registered object, parked on arrival (the load).
        var self = Child("Self", _go);
        AddScript(self, IgnoredScript);
        lom.RegisterLevelObject(self, startDisabled: true);
        Assert.That(lom.levelObjects.Single(lo => lo.gameObject == self).ignoreOptimization, Is.True, "annotated object");

        // Above the registered object (a manager on the attraction root): its subtree.
        var parent = Child("Manager", _go);
        AddScript(parent, IgnoredScript);
        var child = Child("Child", parent);
        lom.RegisterLevelObject(child);
        Assert.That(lom.levelObjects.Single(lo => lo.gameObject == child).ignoreOptimization, Is.True, "under an annotated object");

        // Below it: the LevelObject that contains the script is exempt.
        var holder = Child("Holder", _go);
        var brain = Child("Brain", holder);
        AddScript(brain, IgnoredScript);
        lom.RegisterLevelObject(holder);
        Assert.That(lom.levelObjects.Single(lo => lo.gameObject == holder).ignoreOptimization, Is.True, "contains an annotated object");

        // Unannotated content: unchanged.
        var plain = Child("Plain", _go);
        AddScript(plain, "-- a prop\nlocal s = '@optimizer-ignore'\nfunction start() end\n");
        lom.RegisterLevelObject(plain, startDisabled: true);
        var plainLo = lom.levelObjects.Single(lo => lo.gameObject == plain);
        Assert.That(plainLo.ignoreOptimization, Is.False, "no annotation: culled as before");
        Assert.That(LevelObjectManager.IsOptimizerIgnored(plain), Is.False);
    }

    [Test]
    public void TheAnnotationIsHonouredForALiveSpawnWhoseScriptAlreadyBooted()
    {
        if (LevelObjectManager.ParkContentLocked) Assert.Ignore("a park load holds the park-content lock in this editor");
        if (DreamPark.GamePause.IsPaused) Assert.Ignore("the game is paused in this editor");
        var lom = MakeManager();
        _go = new GameObject("LOM Ignore Live Test") { hideFlags = HideFlags.HideAndDontSave };
        var lb = AddScript(_go, IgnoredScript);
        lb.runInEditMode = true;
        lb.EnsureBooted();   // device order: scripts boot at spawn, THEN SetupNewObject registers
        Assert.That(lb.ScriptScope, Is.Not.Null, "script booted");
        lom.RegisterLevelObject(_go, startDisabled: true);
        Assert.That(Registered(lom).ignoreOptimization, Is.True);
    }

    [Test]
    public void TheRuntimeCallUnparksAndStopsParkingThenHandsBack()
    {
        var lom = MakeManager();
        var (r, c, l, offAudio) = MakeAuthored();
        lom.RegisterLevelObject(_go);
        var lo = Registered(lom);
        Assert.That(lo.ignoreOptimization, Is.False, "unannotated: managed");
        _opt = ScriptableObject.CreateInstance<OptimizationSettings>();
        lo.DisableAndHide(_opt);   // the player walked away
        Assert.That(r.enabled || c.enabled || l.enabled, Is.False, "premise: distance-parked");

        Assert.That(Lua("return dp.optimizer_ignore(__t_c, true)", ("__t_c", _go)), Is.EqualTo(1L));
        Assert.That(lo.ignoreOptimization, Is.True, "exempt from the sweep");
        Assert.That(r.enabled && c.enabled && l.enabled, Is.True, "restored from the snapshot, renderers included");
        Assert.That(offAudio.enabled, Is.False, "authored-off stays off");
        Assert.That(Lua("return dp.optimizer_ignore(__t_c, true)", ("__t_c", _go)), Is.EqualTo(1L), "idempotent");
        Assert.That(r.enabled && lo.enabled && lo.ignoreOptimization, Is.True);

        Assert.That(Lua("return dp.optimizer_ignore(__t_c, false)", ("__t_c", _go)), Is.EqualTo(1L));
        Assert.That(lo.ignoreOptimization, Is.False, "handed back");
        Assert.That(r.enabled, Is.True, "nothing parks until the next sweep");
        lo.DisableAndHide(_opt);   // the next sweep, far away
        Assert.That(r.enabled || l.enabled, Is.False, "managed again");
        lo.Enable(true, _opt, 0);

        // Nil-safe.
        Assert.That(Lua("return dp.optimizer_ignore(nil, true)"), Is.EqualTo(-1L));
        Assert.That(Lua("return dp.optimizer_ignore(nil)"), Is.EqualTo(-1L));
        // A Component is taken for its GameObject.
        Assert.That(Lua("return dp.optimizer_ignore(__t_c)", ("__t_c", l)), Is.EqualTo(1L));
        Assert.That(lo.ignoreOptimization, Is.True);
        Lua("return dp.optimizer_ignore(__t_c, false)", ("__t_c", _go));
    }

    [Test]
    public void TheRuntimeCallIsRememberedUntilRegistrationAndNeverUndoesTheComponent()
    {
        var lom = MakeManager();
        _go = new GameObject("LOM Ignore Early Test") { hideFlags = HideFlags.HideAndDontSave };
        var early = Child("Early", _go);
        Assert.That(Lua("return dp.optimizer_ignore(__t_c, true)", ("__t_c", early)), Is.EqualTo(0L), "awake(): not registered yet");
        lom.RegisterLevelObject(early, startDisabled: true);
        Assert.That(lom.levelObjects.Single(lo => lo.gameObject == early).ignoreOptimization, Is.True, "applied at registration");

        var platform = Child("Game Manager", _go);
        platform.AddComponent<OptimizedAFIgnore>();
        lom.RegisterLevelObject(platform);
        Lua("return dp.optimizer_ignore(__t_c, false)", ("__t_c", platform));
        Assert.That(lom.levelObjects.Single(lo => lo.gameObject == platform).ignoreOptimization, Is.True,
            "an OptimizedAFIgnore component is the platform's, never undone from Lua");
    }

    [Test]
    public void ExemptingTooManyObjectsWarnsOncePerTitle()
    {
        var lom = MakeManager();
        _go = new GameObject("LOM Ignore Many Test") { hideFlags = HideFlags.HideAndDontSave };
        AddScript(_go, IgnoredScript);
        var warnings = new List<string>();
        Application.LogCallback hook = (msg, _, type) =>
        {
            if (type == LogType.Warning && msg.Contains("exempt from distance culling")) warnings.Add(msg);
        };
        Application.logMessageReceived += hook;
        try
        {
            for (int i = 0; i < LevelObjectManager.LuaIgnoreWarnThreshold + 5; i++)
            {
                var child = Child("Part " + i, _go);
                lom.RegisterLevelObject(child);
            }
        }
        finally { Application.logMessageReceived -= hook; }
        Assert.That(lom.levelObjects.All(lo => lo.ignoreOptimization), Is.True);
        Assert.That(warnings.Count, Is.EqualTo(1), "one warning per title");
        Assert.That(warnings[0], Does.Contain((LevelObjectManager.LuaIgnoreWarnThreshold + 1) + " objects"));
    }
}
