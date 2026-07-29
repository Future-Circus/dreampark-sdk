using UnityEngine;
using System.Collections.Generic;
using XLua;
using System;
using Defective.JSON;

[System.Serializable]
public class Injection {
    public string name;
    public GameObject value;
}

[System.Serializable]
public class FloatInjection {
    public string name;
    public float value;
}

[System.Serializable]
public class StringInjection {
    public string name;
    public string value;
}

[System.Serializable]
public class BoolInjection {
    public string name;
    public bool value;
}

[System.Serializable]
public class IntInjection {
    public string name;
    public int value;
}

[System.Serializable]
public class ScriptInjection {
    public string name;
    public LuaBehaviour value;
}

[System.Serializable]
public class AudioClipInjection {
    public string name;
    public AudioClip value;
}

[System.Serializable]
public class Vector3Injection {
    public string name;
    public Vector3 value;
}

[System.Serializable]
public class ColorInjection {
    public string name;
    public Color value = Color.white;
}

[System.Serializable]
public class TransformInjection {
    public string name;
    public Transform value;
}

[System.Serializable]
public class MaterialInjection {
    public string name;
    public Material value;
}

[System.Serializable]
public class SpriteInjection {
    public string name;
    public Sprite value;
}

[System.Serializable]
public class TextureInjection {
    public string name;
    public Texture value;
}

[System.Serializable]
public class ComponentInjection {
    public string name;
    public Component value;
}

[System.Serializable]
public class GameObjectListInjection {
    public string name;
    public GameObject[] value;
}

[LuaCallCSharp]
public class LuaBehaviour : MonoBehaviour, ILuaInjectable {

    public TextAsset          luaScript;
    public Injection[]        injections;
    public FloatInjection[]   floatInjections;
    public StringInjection[]  stringInjections;
    public BoolInjection[]    boolInjections;
    public IntInjection[]     intInjections;
    public ScriptInjection[]    scriptInjections;
    public AudioClipInjection[] audioClipInjections;
    public Vector3Injection[]        vector3Injections;
    public ColorInjection[]          colorInjections;
    public TransformInjection[]      transformInjections;
    public MaterialInjection[]       materialInjections;
    public SpriteInjection[]         spriteInjections;
    public TextureInjection[]        textureInjections;
    public ComponentInjection[]      componentInjections;
    public GameObjectListInjection[] gameObjectListInjections;

    // ILuaInjectable — explicit implementation wrapping the public fields
    TextAsset            ILuaInjectable.luaScript            { get => luaScript;            set => luaScript = value; }
    Injection[]          ILuaInjectable.injections           { get => injections;           set => injections = value; }
    FloatInjection[]     ILuaInjectable.floatInjections      { get => floatInjections;      set => floatInjections = value; }
    StringInjection[]    ILuaInjectable.stringInjections     { get => stringInjections;     set => stringInjections = value; }
    BoolInjection[]      ILuaInjectable.boolInjections       { get => boolInjections;       set => boolInjections = value; }
    IntInjection[]       ILuaInjectable.intInjections        { get => intInjections;        set => intInjections = value; }
    ScriptInjection[]    ILuaInjectable.scriptInjections     { get => scriptInjections;     set => scriptInjections = value; }
    AudioClipInjection[] ILuaInjectable.audioClipInjections  { get => audioClipInjections;  set => audioClipInjections = value; }
    Vector3Injection[]        ILuaInjectable.vector3Injections        { get => vector3Injections;        set => vector3Injections = value; }
    ColorInjection[]          ILuaInjectable.colorInjections          { get => colorInjections;          set => colorInjections = value; }
    TransformInjection[]      ILuaInjectable.transformInjections      { get => transformInjections;      set => transformInjections = value; }
    MaterialInjection[]       ILuaInjectable.materialInjections       { get => materialInjections;       set => materialInjections = value; }
    SpriteInjection[]         ILuaInjectable.spriteInjections         { get => spriteInjections;         set => spriteInjections = value; }
    TextureInjection[]        ILuaInjectable.textureInjections        { get => textureInjections;        set => textureInjections = value; }
    ComponentInjection[]      ILuaInjectable.componentInjections      { get => componentInjections;      set => componentInjections = value; }
    GameObjectListInjection[] ILuaInjectable.gameObjectListInjections { get => gameObjectListInjections; set => gameObjectListInjections = value; }

    internal static LuaEnv luaEnv = new LuaEnv();
    internal static float lastGCTime = 0;
    internal const float GCInterval = 1;

    /// <summary>
    /// Public accessor for the shared Lua environment. Used by code in
    /// other assemblies (creator game asmdefs, ProfileAPI, etc.) that
    /// needs to register globals or evaluate Lua. Returns the same singleton
    /// LuaEnv that every LuaBehaviour script runs in.
    /// </summary>
    public static LuaEnv GetLuaEnv() => luaEnv;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void RegisterLuaGlobals()
    {
        luaEnv.Global.Set("json_parse", new Func<string, LuaTable>(JsonParseToLuaTable));
#if DREAMPARKCORE
        // Production-only: clamp what creator Lua can reach when it runs inside
        // the core app. Compiled out of the SDK so developers build/test with the
        // full CS.* surface. Source lives in core's Assets/Scripts (not the SDK).
        DreamPark.Security.LuaSecuritySandbox.Install(luaEnv);
#endif
    }

    static LuaTable JsonParseToLuaTable(string json)
    {
        var obj = new JSONObject(json);
        return JsonObjectToLuaTable(obj);
    }

    static LuaTable JsonObjectToLuaTable(JSONObject obj)
    {
        var table = luaEnv.NewTable();

        switch (obj.type)
        {
            case JSONObject.Type.Object:
                for (int i = 0; i < obj.keys.Count; i++)
                {
                    var val = obj.list[i];
                    switch (val.type)
                    {
                        case JSONObject.Type.String:
                            table.Set(obj.keys[i], val.stringValue);
                            break;
                        case JSONObject.Type.Number:
                            table.Set(obj.keys[i], val.floatValue);
                            break;
                        case JSONObject.Type.Bool:
                            table.Set(obj.keys[i], val.boolValue);
                            break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:
                            table.Set(obj.keys[i], JsonObjectToLuaTable(val));
                            break;
                        case JSONObject.Type.Null:
                            break;
                    }
                }
                break;

            case JSONObject.Type.Array:
                for (int i = 0; i < obj.list.Count; i++)
                {
                    var val = obj.list[i];
                    switch (val.type)
                    {
                        case JSONObject.Type.String:
                            table.Set(i + 1, val.stringValue);
                            break;
                        case JSONObject.Type.Number:
                            table.Set(i + 1, val.floatValue);
                            break;
                        case JSONObject.Type.Bool:
                            table.Set(i + 1, val.boolValue);
                            break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:
                            table.Set(i + 1, JsonObjectToLuaTable(val));
                            break;
                        case JSONObject.Type.Null:
                            break;
                    }
                }
                break;
        }

        return table;
    }

    private Action luaAwake;
    private Action luaStart;
    private Action luaUpdate;
    private Action luaOnDestroy;
    private Action luaOnEnable;
    private Action luaOnDisable;
    private Action<string> luaOnNet;

    // Optional Unity messages (FixedUpdate, OnTrigger*, OnCollision*, app pause/focus)
    // are wired via lightweight relay components added only when the Lua script defines
    // the matching function — see LuaMessageRelays. Tracked here so OnEnable/OnDisable
    // can toggle them in step with this component.
    private readonly List<Behaviour> luaRelays = new List<Behaviour>();

    private LuaTable scriptScopeTable;

    // Set the instant the scope table exists, before the script body runs, so a
    // re-entrant EnsureBooted (script A injecting script B injecting script A)
    // returns the partially built scope instead of recursing forever.
    private bool booted;

    /// <summary>
    /// The Lua scope table for this script. Other LuaBehaviours can read/write
    /// variables and call functions through this table.
    ///
    /// Reading this boots the script if it hasn't run yet, so a cross-script
    /// reference no longer depends on Unity's undefined Awake ordering.
    /// </summary>
    public LuaTable ScriptScope {
        get {
            // Never boot from edit mode. Editor tooling reads this property, and
            // booting means running the script's file scope and awake() - which can
            // touch the scene - outside Play. Editors get whatever already exists,
            // which is the pre-change behaviour (null until Play).
            if (Application.isPlaying)
                EnsureBooted();
            return scriptScopeTable;
        }
    }

    // Re-pushes all Inspector variables into the Lua scope table
    void InjectAll() {
        if (injections != null)
            foreach (var inj in injections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (floatInjections != null)
            foreach (var inj in floatInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (stringInjections != null)
            foreach (var inj in stringInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (boolInjections != null)
            foreach (var inj in boolInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (intInjections != null)
            foreach (var inj in intInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        // Script links — inject the other script's Lua scope table directly
        if (scriptInjections != null)
            foreach (var inj in scriptInjections)
                if (inj.value != null && inj.value.ScriptScope != null)
                    scriptScopeTable.Set(inj.name, inj.value.ScriptScope);
        if (audioClipInjections != null)
            foreach (var inj in audioClipInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (vector3Injections != null)
            foreach (var inj in vector3Injections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (colorInjections != null)
            foreach (var inj in colorInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (transformInjections != null)
            foreach (var inj in transformInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (materialInjections != null)
            foreach (var inj in materialInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (spriteInjections != null)
            foreach (var inj in spriteInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (textureInjections != null)
            foreach (var inj in textureInjections)
                scriptScopeTable.Set(inj.name, inj.value);
        if (componentInjections != null)
            foreach (var inj in componentInjections)
                scriptScopeTable.Set(inj.name, inj.value);
    }

    // Collections are pushed once (in Awake), NOT in InjectAll — building a fresh
    // LuaTable on every re-inject would allocate, and EasyLua re-injects per frame.
    // Lua sees a 1-based array table (so #list and ipairs work as expected).
    void InjectCollections() {
        if (gameObjectListInjections == null) return;
        foreach (var inj in gameObjectListInjections) {
            using (LuaTable t = luaEnv.NewTable()) {
                if (inj.value != null)
                    for (int i = 0; i < inj.value.Length; i++)
                        t.Set(i + 1, inj.value[i]);
                scriptScopeTable.Set(inj.name, t);
            }
        }
    }

    /// <summary>
    /// Re-push all Inspector-authored variables into the Lua scope. Injection already
    /// happens once in Awake; call this only if you mutate an injection value from C#
    /// at runtime and need Lua to observe the change. (In the editor this also runs
    /// automatically when you tweak the Inspector during play — see Update/OnValidate.)
    /// </summary>
    public void ReinjectVariables() {
        if (scriptScopeTable != null)
            InjectAll();
    }

#if UNITY_EDITOR
    // Set by OnValidate when the Inspector mutates this component. Lets designers keep
    // live-tweak during play without re-marshaling every field into Lua every frame.
    private bool _injectionDirty;
    void OnValidate() => _injectionDirty = true;
#endif

    /// <summary>
    /// True while the park has content parked - Build Mode, where levels and props are
    /// spawned only so the player can position them. Nothing gameplay-side should be
    /// running yet.
    ///
    /// objectsEnabled is the park's own build/play switch and defaults to true, so a
    /// plain Unity scene with no park in it is never parked and every script boots in
    /// Awake exactly as it always did.
    /// </summary>
    internal static bool ParkContentIsParked => !DreamPark.ParkBuilder.LevelObjectManager.objectsEnabled;

    // Why booting is conditional rather than unconditional in Awake.
    //
    // Unity runs Awake AND OnEnable synchronously inside Instantiate, before the
    // spawner gets its instance back and before anything can switch the content off.
    // So a script bootstrapped in Awake had already run its file scope and its awake()
    // by the time Build Mode could park it - which is why zombies walked around, items
    // spun, and agents snapped to the navmesh in an editing session. The only place
    // that can be fixed is here, at the moment the SDK decides to start a script.
    //
    // Parked content therefore boots later: on the OnEnable that Build Mode -> Play
    // delivers, or on the first Update for objects the park never disables (the player
    // rig). The contract a content author sees is the one they already know from their
    // own Unity build - awake() runs when the game starts, update() runs while it is
    // running - with no SDK state to consult from Lua.
    void Awake() {
        if (!ParkContentIsParked)
            EnsureBooted();
    }

    /// <summary>
    /// Run this script's file scope and awake() exactly once, unless the park still has
    /// content parked. Safe to call from anywhere; every entry point after the first is
    /// a no-op.
    /// </summary>
    public void EnsureBooted() {
        if (booted) return;
        if (luaScript == null) return;
        if (ParkContentIsParked) return;
        booted = true;

        scriptScopeTable = luaEnv.NewTable();

        using (LuaTable meta = luaEnv.NewTable()) {
            meta.Set("__index", luaEnv.Global);
            scriptScopeTable.SetMetaTable(meta);
        }

        scriptScopeTable.Set("self", this);
        InjectAll();
        InjectCollections();

        // inject `storage` — per-user game/attraction key-value storage
        // (GameStorageAPI) bound to this script's owning attraction via a
        // lazy proxy. Injected BEFORE the script body runs so file-scope
        // reads work; lazy because the park spawner parents spawned content
        // AFTER Awake (same reason net_send reads netId.Id at send time) —
        // the GameArea/PropTemplate/LevelTemplate walk happens on first use.
        // Pure Lua-side wiring: no C#→Lua delegate bridging, IL2CPP-safe.
        // (nil-guarded so an Inspector injection named `storage` wins.)
        luaEnv.DoString("if storage == nil then storage = __dp_storage_lazy and __dp_storage_lazy(self.gameObject) or nil end",
            "gamestorage.inject", scriptScopeTable);

        luaEnv.DoString(luaScript.text, luaScript.name, scriptScopeTable);

        luaAwake = scriptScopeTable.Get<Action>("awake");
        scriptScopeTable.Get("start",             out luaStart);
        scriptScopeTable.Get("update",            out luaUpdate);
        scriptScopeTable.Get("ondestroy",         out luaOnDestroy);
        scriptScopeTable.Get("onenable",          out luaOnEnable);
        scriptScopeTable.Get("ondisable",         out luaOnDisable);
        scriptScopeTable.Get("onnet",             out luaOnNet);

        // Opt-in physics + frame-timing + app-lifecycle messages. Only adds a relay
        // (and its per-object engine cost) for functions this script actually defines.
        LuaMessageRelays.Bind(gameObject, scriptScopeTable, luaRelays);
        // Match host state now in case this component starts disabled (OnEnable won't fire).
        LuaMessageRelays.SetEnabled(luaRelays, isActiveAndEnabled);

        // auto-wire NetId if present on same GameObject
        var netId = GetComponent<NetId>();
        if (luaOnNet != null && netId != null)
            netId.OnNetEvent += luaOnNet;

        // inject net_send(eventType, payloadJson) into this script's scope.
        // Read netId.Id at SEND time, not here: NetId finalizes its id in
        // Start (after the park spawner parents/renames/stamps NetScope),
        // which runs after this Awake — capturing the value now would
        // freeze a stale/zero id for park-spawned content.
        if (netId != null) {
            var client = FindObjectOfType<DreamBoxClient>();
            if (client != null) {
                scriptScopeTable.Set("net_send", new Action<string, string>((eventType, payload) => {
                    client.SendToNetId(netId.Id, eventType, payload);
                }));
            }
        }

        luaAwake?.Invoke();
    }

    // Unity fires Start exactly once and never again, so a script that was still
    // parked when Start came round would silently lose its start() forever. Dispatch is
    // tracked separately and also driven from Update, which guarantees start() lands on
    // the first frame the script actually runs, whichever path booted it.
    private bool startDispatched;

    void Start() => DispatchStart();

    void DispatchStart() {
        if (startDispatched || !booted) return;
        startDispatched = true;
        luaStart?.Invoke();
    }

    /// <summary>
    /// Non-zero while OptimizedAF is parking/unparking components. Lua
    /// onenable/ondisable are SUPPRESSED for the duration.
    ///
    /// LuaBehaviour is not in LevelObject's ProtectedComponentTypes, so the
    /// optimizer switches it off at spawn and back on when the level finishes
    /// loading, and again on every cull cycle. Routed straight through, that
    /// meant creator scripts received onenable()/ondisable() as LOAD ARTIFACTS —
    /// and, depending on which order the toggles landed, a script could see
    /// ondisable() before it had ever seen onenable(). Content that reads those
    /// as "the player can see me now" / "tear my state down" was being lied to
    /// by the loader.
    ///
    /// These hooks now mean what a creator expects: gameplay or authored
    /// enabling. Engine-level parking is invisible. Nothing else is suppressed —
    /// EnsureBooted still runs (the first enable is what boots the script) and
    /// the relays still track the real component state, so physics callbacks stay
    /// correctly gated.
    ///
    /// A depth counter rather than a bool: Enable() can run re-entrantly across
    /// nested LevelObjects, and a bool would be cleared by the inner scope while
    /// the outer one was still toggling.
    /// </summary>
    public static int OptimizerToggleDepth;

    void OnEnable() {
        // First enable is what boots the script (see Awake). After that this is just
        // keeping the relays in lockstep with this component's enabled state, so a
        // disabled LuaBehaviour doesn't keep firing FixedUpdate/OnTrigger* via them.
        EnsureBooted();
        LuaMessageRelays.SetEnabled(luaRelays, true);
        if (OptimizerToggleDepth == 0) luaOnEnable?.Invoke();
    }

    void OnDisable() {
        if (OptimizerToggleDepth == 0) luaOnDisable?.Invoke();
        LuaMessageRelays.SetEnabled(luaRelays, false);
    }

    void Update() {
        // Content that was parked at spawn boots the first frame it genuinely runs.
        // This is the path for objects the park never disables - the player rig - which
        // get no OnEnable when Build Mode ends.
        if (!booted) {
            EnsureBooted();
            if (!booted) return;
        }
        DispatchStart();

        if (scriptScopeTable == null) return;

#if UNITY_EDITOR
        // Re-inject only on the frames the Inspector actually changed, so live-tweak
        // still works in the editor. In a build this block compiles out entirely —
        // zero per-frame injection cost (was ~once-per-field marshaling every frame).
        if (_injectionDirty) {
            InjectAll();
            _injectionDirty = false;
        }
#endif

        luaUpdate?.Invoke();

        // Lua GC — important, don't skip this
        if (Time.time - LuaBehaviour.lastGCTime > GCInterval) {
            luaEnv.Tick();
            LuaBehaviour.lastGCTime = Time.time;
        }
    }

    void OnDestroy() {
        luaOnDestroy?.Invoke();
        if (scriptScopeTable != null)
            scriptScopeTable.Dispose();
        luaOnDestroy = null;
        luaUpdate = null;
        luaStart = null;
        injections = null;
        scriptInjections = null;
    }
}
