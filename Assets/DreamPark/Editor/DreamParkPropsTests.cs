#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using DreamPark;
using DreamPark.ParkBuilder;
using NUnit.Framework;
using UnityEngine;
using XLua;

/// <summary>
/// dp.spawn_prop / preload_prop / despawn_prop / on_prop_* (DreamParkLuaProps.cs). Loading is
/// replaced by the module's test hooks, so every spawn completes synchronously in edit mode.
/// HideAndDontSave on every object: nothing may touch (or dirty) the open scene.
/// </summary>
public sealed class DreamParkPropsTests
{
    readonly List<GameObject> _made = new List<GameObject>();
    readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();
    readonly HashSet<string> _failDownload = new HashSet<string>();
    int _loads;
    GameObject _lomGo;
    LevelObjectManager _lomBefore;
    bool? _objectsEnabledBefore;
    bool _paused;

    GameObject Make(string name, GameObject parent = null)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        if (parent != null) go.transform.SetParent(parent.transform, false);
        _made.Add(go);
        return go;
    }

    [SetUp]
    public void SetUp()
    {
        DreamParkLuaProps.AddressResolver = (req, game) =>
        {
            string leaf = req.Substring(req.LastIndexOf('/') + 1);
            if (!_prefabs.ContainsKey(leaf)) return null;
            return leaf.StartsWith("A_") ? "Test/Levels/Custom/" + leaf : "Test/Props/Generic/" + leaf;
        };
        DreamParkLuaProps.AssetLoader = address =>
        {
            _loads++;
            string leaf = address.Substring(address.LastIndexOf('/') + 1);
            return UniTask.FromResult(_prefabs.TryGetValue(leaf, out var p) && !_failDownload.Contains(leaf) ? p : null);
        };
        DreamParkLuaProps.DependencyDownloader = address =>
            UniTask.FromResult(!_failDownload.Contains(address.Substring(address.LastIndexOf('/') + 1)));

        // The "prefab assets": inert templates (inactive, so nothing on them ever boots).
        _prefabs["P_Crate"] = MakePropTemplate("P_Crate", withProp: true);
        _prefabs["P_NoTemplate"] = MakePropTemplate("P_NoTemplate", withProp: false);
        _prefabs["A_Room"] = MakePropTemplate("A_Room", withProp: false);
    }

    GameObject _templates;

    GameObject MakePropTemplate(string name, bool withProp)
    {
        // Active templates under an INACTIVE holder: inert here, active once instantiated (like an asset).
        if (_templates == null) { _templates = Make("Prop Templates"); _templates.SetActive(false); }
        var root = Make(name, _templates);
        if (withProp) root.AddComponent<PropTemplate>().gameId = "Test";
        var lid = Make("Lid", root);
        lid.AddComponent<BoxCollider>();
        lid.AddComponent<NetId>();
        return root;
    }

    [TearDown]
    public void TearDown()
    {
        if (_paused) { GamePause.ResetForTests(); _paused = false; }
        for (int i = _made.Count - 1; i >= 0; i--) if (_made[i] != null) UnityEngine.Object.DestroyImmediate(_made[i]);
        _made.Clear();
        _templates = null;
        _prefabs.Clear();
        _failDownload.Clear();
        DreamParkLuaProps.AddressResolver = null;
        DreamParkLuaProps.AssetLoader = null;
        DreamParkLuaProps.DependencyDownloader = null;
        if (_objectsEnabledBefore.HasValue) LevelObjectManager.objectsEnabled = _objectsEnabledBefore.Value;
        _objectsEnabledBefore = null;
        if (_lomGo != null)
        {
            LevelObjectManager.Instance = _lomBefore;
            UnityEngine.Object.DestroyImmediate(_lomGo);
            _lomGo = null;
        }
        var env = LuaBehaviour.GetLuaEnv();
        env?.DoString("__dp_prop_fns = { loaded = {}, failed = {} } __t_events = nil", "props.test.reset");
    }

    LevelObjectManager MakeManager()
    {
        if (LevelObjectManager.ParkContentLocked) Assert.Ignore("a park load holds the park-content lock in this editor");
        if (GamePause.IsPaused) Assert.Ignore("the game is paused in this editor");
        _objectsEnabledBefore = LevelObjectManager.objectsEnabled;
        LevelObjectManager.objectsEnabled = true;
        _lomBefore = LevelObjectManager.Instance;
        _lomGo = new GameObject("Props Test LOM") { hideFlags = HideFlags.HideAndDontSave };
        var lom = _lomGo.AddComponent<LevelObjectManager>();
        LevelObjectManager.Instance = lom;
        return lom;
    }

    static LuaEnv Env()
    {
        typeof(DreamParkLuaAPI).GetMethod("RegisterLua", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        return LuaBehaviour.GetLuaEnv();
    }

    static object[] Lua(string code, params (string name, object value)[] globals)
    {
        var env = Env();
        foreach (var g in globals) env.Global.Set(g.name, g.value);
        try { return env.DoString(code, "props.test"); }
        finally { foreach (var g in globals) env.Global.Set<string, object>(g.name, null); }
    }

    /// An attraction: LevelTemplate root (GameArea rides along) with a slot child.
    (GameObject root, GameObject slot, GameObject script) MakeAttraction(string name = "A_Hub")
    {
        var root = Make(name);
        var lt = root.AddComponent<LevelTemplate>();
        lt.gameId = "Test";
        var slot = Make("Slot North", root);
        slot.transform.localPosition = new Vector3(2, 0, 3);
        var script = Make("Director", root);
        return (root, slot, script);
    }

    [Test]
    public void SpawnParentsPosesScopesAndIsIdempotentByKey()
    {
        var (root, slot, caller) = MakeAttraction();
        var r = Lua(@"
local h = dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'slot:north', pos = CS.UnityEngine.Vector3(0, 0.5, 0) }, __t_caller)
local again = dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'slot:north' }, __t_caller)
local other = dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'slot:south' }, __t_caller)
return h.loaded(), h.go, h.key, h.address, h == again, h.id ~= other.id, h.state(), h.error()
", ("__t_slot", slot.transform), ("__t_caller", caller));

        Assert.That((bool)r[0], Is.True, "loaded");
        var go = (GameObject)r[1];
        Assert.That(go, Is.Not.Null);
        Assert.That(go.transform.parent, Is.SameAs(slot.transform), "parented under the given slot");
        Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(0, 0.5f, 0)), "pos is local by default");
        Assert.That(go.name, Is.EqualTo("P_Crate"));
        Assert.That((string)r[2], Is.EqualTo("slot:north"));
        Assert.That((string)r[3], Is.EqualTo("Test/Props/Generic/P_Crate"));
        Assert.That((bool)r[4], Is.True, "same key → the same handle");
        Assert.That((bool)r[5], Is.True, "another key → another prop");
        Assert.That((string)r[6], Is.EqualTo("loaded"));
        Assert.That(r[7], Is.Null, "no error");
        Assert.That(_loads, Is.EqualTo(2), "the idempotent call loaded nothing");

        var scope = go.GetComponent<NetScope>();
        Assert.That(scope, Is.Not.Null, "a NetScope is stamped");
        Assert.That(scope.scopeKey, Is.EqualTo(DreamParkLuaProps.ScopeKeyFor(root, "slot:north")));
        Assert.That(go.GetComponentInParent<LevelTemplate>(), Is.Not.Null, "nested under the attraction");
        Assert.That(go.GetComponent<PropTemplate>().IsNestedUnderTemplate, Is.True,
            "PropTemplate sees itself nested, so its PropTemplate/GameArea are suppressed like an authored prop's");
    }

    [Test]
    public void NetIdsInsideASpawnedPropAreTheSameOnEveryHeadsetAndEveryRespawn()
    {
        uint IdFor(string attractionName, string key)
        {
            var (root, slot, caller) = MakeAttraction(attractionName);
            // Device-dependent noise ABOVE the attraction must not matter: the owner hash is NetScope-bound.
            root.AddComponent<NetScope>().scopeKey = "level-7|obj-42";
            var h = Lua("return dp.spawn_prop('P_Crate', { parent = __t_slot, key = __t_key }, __t_caller).go",
                ("__t_slot", slot.transform), ("__t_caller", caller), ("__t_key", key));
            var go = (GameObject)h[0];
            uint id = go.transform.Find("Lid").GetComponent<NetId>().PreviewId();
            UnityEngine.Object.DestroyImmediate(root);
            return id;
        }

        uint a = IdFor("A_Hub", "slot:north");
        uint b = IdFor("A_Hub", "slot:north");     // another headset / a respawn
        uint c = IdFor("A_Hub", "slot:south");
        Assert.That(b, Is.EqualTo(a), "same attraction scope + key → same NetId");
        Assert.That(c, Is.Not.EqualTo(a), "another key → other NetIds");
    }

    [Test]
    public void WorldSpacePoseIsConvertedToTheParent()
    {
        var (root, slot, caller) = MakeAttraction();
        root.transform.position = new Vector3(10, 0, 0);
        var r = Lua(@"
local h = dp.spawn_prop('P_Crate', { parent = __t_slot, world = true, pos = CS.UnityEngine.Vector3(13, 1, 3),
    rot = CS.UnityEngine.Quaternion.Euler(0, 90, 0) }, __t_caller)
return h.go", ("__t_slot", slot.transform), ("__t_caller", caller));
        var go = (GameObject)r[0];
        Assert.That(Vector3.Distance(go.transform.position, new Vector3(13, 1, 3)), Is.LessThan(1e-4f));
        Assert.That(Quaternion.Angle(go.transform.rotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01f));
    }

    [Test]
    public void DefaultParentIsTheCallersAttraction()
    {
        var (root, _, caller) = MakeAttraction();
        var r = Lua("return dp.spawn_prop('P_Crate', nil, __t_caller).go", ("__t_caller", caller));
        Assert.That(((GameObject)r[0]).transform.parent, Is.SameAs(root.transform));
    }

    [Test]
    public void ClearErrorsReachHandlesAndHandlers()
    {
        var (_, slot, caller) = MakeAttraction();
        _failDownload.Add("P_Crate");
        var r = Lua(@"
__t_events = {}
dp.on_prop_failed(function(h, err) __t_events[#__t_events + 1] = err end, nil, __t_caller)
local a = dp.spawn_prop('P_Nope', { parent = __t_slot }, __t_caller)
local b = dp.spawn_prop('P_NoTemplate', { parent = __t_slot }, __t_caller)
local c = dp.spawn_prop('A_Room', { parent = __t_slot }, __t_caller)
local d = dp.spawn_prop('P_Crate', { parent = __t_slot }, __t_caller)
return a.error(), b.error(), c.error(), d.error(), d.go, #__t_events, a.state()
", ("__t_slot", slot.transform), ("__t_caller", caller));
        Assert.That((string)r[0], Is.EqualTo("not-found"));
        Assert.That((string)r[1], Is.EqualTo("not-a-prop"), "a prefab without PropTemplate");
        Assert.That((string)r[2], Is.EqualTo("not-a-prop"), "an attraction address");
        Assert.That((string)r[3], Is.EqualTo("download-failed"));
        Assert.That(r[4], Is.Null);
        Assert.That(Convert.ToInt32(r[5]), Is.EqualTo(4), "on_prop_failed heard every failure");
        Assert.That((string)r[6], Is.EqualTo("failed"));

        // A failed key can be retried once the cause is gone.
        _failDownload.Clear();
        r = Lua("local d = dp.spawn_prop('P_Crate', { parent = __t_slot }, __t_caller) return d.loaded()",
            ("__t_slot", slot.transform), ("__t_caller", caller));
        Assert.That((bool)r[0], Is.True);
    }

    [Test]
    public void LoadedHandlersHearTheirTitleOnlyAndDropWithTheirOwner()
    {
        var (_, slot, caller) = MakeAttraction();
        var other = Make("Other Title");
        other.AddComponent<GameArea>().gameId = "OtherGame";
        var ownerScript = Make("Owner Script");
        var r = Lua(@"
__t_events = {}
dp.on_prop_loaded(function(h) __t_events[#__t_events + 1] = 'mine:' .. h.key end, nil, __t_caller)
dp.on_prop_loaded(function(h) __t_events[#__t_events + 1] = 'other' end, nil, __t_other)
dp.on_prop_loaded(function(h) __t_events[#__t_events + 1] = 'gone' end, __t_owner, __t_caller)
return true", ("__t_caller", caller), ("__t_other", other), ("__t_owner", ownerScript.transform));
        UnityEngine.Object.DestroyImmediate(ownerScript);
        r = Lua(@"
dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'k1' }, __t_caller)
return table.concat(__t_events, ',')", ("__t_slot", slot.transform), ("__t_caller", caller));
        Assert.That((string)r[0], Is.EqualTo("mine:k1"), "the Beta-folded title filters; a destroyed owner's handler is dropped");
    }

    [Test]
    public void DespawnByHandleOrObjectAndTheKeyIsFreeAgain()
    {
        var (_, slot, caller) = MakeAttraction();
        var r = Lua(@"
local h = dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'k' }, __t_caller)
local go = h.go
local ok = h.despawn()
local again = dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'k' }, __t_caller)
local ok2 = dp.despawn_prop(again.go.transform:Find('Lid').gameObject)
return ok, h.state(), h.go, again.id ~= h.id, ok2, again.state(), dp.despawn_prop(h)
", ("__t_slot", slot.transform), ("__t_caller", caller));
        Assert.That((bool)r[0], Is.True);
        Assert.That((string)r[1], Is.EqualTo("despawned"));
        Assert.That(r[2], Is.Null, "h.go is nil once despawned");
        Assert.That((bool)r[3], Is.True, "a despawned key spawns a fresh prop");
        Assert.That((bool)r[4], Is.True, "despawn_prop accepts any object inside the prop");
        Assert.That((string)r[5], Is.EqualTo("despawned"));
        Assert.That((bool)r[6], Is.False, "a second despawn is a no-op");
        Assert.That(slot.transform.childCount, Is.EqualTo(0), "nothing left in the slot");
    }

    [Test]
    public void PropsDespawnWithTheirOwnerWhereverTheyWereParented()
    {
        var (root, _, caller) = MakeAttraction();
        var elsewhere = Make("Outside The Attraction");
        var r = Lua("local h = dp.spawn_prop('P_Crate', { parent = __t_p }, __t_caller) return h.go, h",
            ("__t_p", elsewhere.transform), ("__t_caller", caller));
        var go = (GameObject)r[0];
        var handle = (LuaTable)r[1];
        Assert.That(go.transform.parent, Is.SameAs(elsewhere.transform));
        UnityEngine.Object.DestroyImmediate(root);
        Assert.That(go == null, Is.True, "the attraction went away, so did its prop");
        using (var state = handle.Get<LuaFunction>("state"))
            Assert.That((string)state.Call()[0], Is.EqualTo("despawned"));
    }

    [Test]
    public void SpawnedPropIsRegisteredLikeANestedAuthoredPropAndUnregisteredOnDespawn()
    {
        var lom = MakeManager();
        var (_, slot, caller) = MakeAttraction();
        var r = Lua("local h = dp.spawn_prop('P_Crate', { parent = __t_slot }, __t_caller) return h.go, h.id",
            ("__t_slot", slot.transform), ("__t_caller", caller));
        var go = (GameObject)r[0];
        var lo = lom.levelObjects.SingleOrDefault(o => o.gameObject == go);
        Assert.That(lo, Is.Not.Null, "one LevelObject for the prop root (nested-prop rule)");
        Assert.That(lom.levelObjects.Any(o => o.gameObject == go.transform.Find("Lid").gameObject), Is.False,
            "not recursed into: the prop is not a standalone template");

        // Culled like authored content: parking switches its collider off, unparking restores it.
        var col = go.GetComponentInChildren<BoxCollider>();
        lo.Disable();
        Assert.That(col.enabled, Is.False, "parked by the optimizer");
        lo.Enable(true);
        Assert.That(col.enabled, Is.True, "restored");

        DreamParkLuaProps.Despawn(Convert.ToInt32(r[1]));
        Assert.That(lom.levelObjects.Any(o => o.gameObject == go), Is.False, "unregistered on despawn");
    }

    [Test]
    public void SpawnedWhileTheParkIsParkedArrivesParked()
    {
        var lom = MakeManager();
        var (_, slot, caller) = MakeAttraction();
        LevelObjectManager.objectsEnabled = false;   // build mode / a park load
        var r = Lua("return dp.spawn_prop('P_Crate', { parent = __t_slot }, __t_caller).go",
            ("__t_slot", slot.transform), ("__t_caller", caller));
        var go = (GameObject)r[0];
        Assert.That(go.GetComponentInChildren<BoxCollider>().enabled, Is.False, "parked on arrival like the authored set");
        LevelObjectManager.objectsEnabled = true;
        lom.ReleaseAllLevelObjects();
        Assert.That(go.GetComponentInChildren<BoxCollider>().enabled, Is.True, "released with the park");
    }

    [Test]
    public void SpawnedWhileTheGameIsHeldIsFrozenOnArrival()
    {
        var lom = MakeManager();
        var (_, slot, caller) = MakeAttraction();
        _paused = true;
        Assert.That(GamePause.Pause(new GamePause.Info { source = "editor" }), Is.True);
        var r = Lua("return dp.spawn_prop('P_Crate', { parent = __t_slot }, __t_caller).go",
            ("__t_slot", slot.transform), ("__t_caller", caller));
        var go = (GameObject)r[0];
        var lo = lom.levelObjects.Single(o => o.gameObject == go);
        Assert.That(GamePause.IsFrozen(lo), Is.True, "the game-master pause freezes it like the rest of the park");
        GamePause.Resume("editor");
        _paused = false;
        Assert.That(go.GetComponentInChildren<BoxCollider>().enabled, Is.True, "thawed");
    }

    [Test]
    public void PreloadDownloadsOnceAndReportsReady()
    {
        var (_, _, caller) = MakeAttraction();
        var r = Lua(@"
local a = dp.preload_prop('P_Crate', __t_caller)
local b = dp.preload_prop('Test/Props/Generic/P_Crate', __t_caller)
local c = dp.preload_prop('P_Nope', __t_caller)
return a.ready(), a == b, a.go, c.ready(), c.error(), a.address
", ("__t_caller", caller));
        Assert.That((bool)r[0], Is.True);
        Assert.That((bool)r[1], Is.True, "one preload per address");
        Assert.That(r[2], Is.Null, "a preload instantiates nothing");
        Assert.That((bool)r[3], Is.False);
        Assert.That((string)r[4], Is.EqualTo("not-found"));
        Assert.That((string)r[5], Is.EqualTo("Test/Props/Generic/P_Crate"));
        Assert.That(_loads, Is.EqualTo(0), "the prefab itself is not loaded by a preload");
    }

    [Test]
    public void SeatAddsAFloorAnchor()
    {
        var (_, slot, caller) = MakeAttraction();
        var r = Lua("return dp.spawn_prop('P_Crate', { parent = __t_slot, seat = true }, __t_caller).go, dp.spawn_prop('P_Crate', { parent = __t_slot, key = 'b' }, __t_caller).go",
            ("__t_slot", slot.transform), ("__t_caller", caller));
        Assert.That(((GameObject)r[0]).GetComponent<FloorAnchor>(), Is.Not.Null);
        Assert.That(((GameObject)r[1]).GetComponent<FloorAnchor>(), Is.Null);
    }

    [Test]
    public void BetaTwinsAreOneTitle()
    {
        Assert.That(DreamParkLuaProps.BaseTitle("WizardsWayBeta"), Is.EqualTo("WizardsWay"));
        Assert.That(DreamParkLuaProps.BaseTitle("WizardsWay"), Is.EqualTo("WizardsWay"));
        Assert.That(DreamParkLuaProps.BaseTitle("Beta"), Is.EqualTo("Beta"));
    }
}
#endif
