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

    /// <summary>
    /// The Lua scope table for this script. Other LuaBehaviours can read/write
    /// variables and call functions through this table.
    /// </summary>
    public LuaTable ScriptScope => scriptScopeTable;

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

    void Awake() {
        if (luaScript == null) return;

        scriptScopeTable = luaEnv.NewTable();

        using (LuaTable meta = luaEnv.NewTable()) {
            meta.Set("__index", luaEnv.Global);
            scriptScopeTable.SetMetaTable(meta);
        }

        scriptScopeTable.Set("self", this);
        InjectAll();
        InjectCollections();

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

        // inject net_send(eventType, payloadJson) into this script's scope
        if (netId != null) {
            var client = FindObjectOfType<DreamBoxClient>();
            if (client != null) {
                uint id = netId.Id;
                scriptScopeTable.Set("net_send", new Action<string, string>((eventType, payload) => {
                    client.SendToNetId(id, eventType, payload);
                }));
            }
        }

        luaAwake?.Invoke();
    }

    void Start()     => luaStart?.Invoke();

    void OnEnable() {
        // Relays are created in Awake (before the first OnEnable), so by here the list
        // is populated. Keep them in lockstep with this component's enabled state so a
        // disabled LuaBehaviour doesn't keep firing FixedUpdate/OnTrigger* via its relays.
        LuaMessageRelays.SetEnabled(luaRelays, true);
        luaOnEnable?.Invoke();
    }

    void OnDisable() {
        luaOnDisable?.Invoke();
        LuaMessageRelays.SetEnabled(luaRelays, false);
    }

    void Update() {
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
