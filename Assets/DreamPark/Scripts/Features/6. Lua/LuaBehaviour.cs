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

    // ILuaInjectable — explicit implementation wrapping the public fields
    TextAsset            ILuaInjectable.luaScript            { get => luaScript;            set => luaScript = value; }
    Injection[]          ILuaInjectable.injections           { get => injections;           set => injections = value; }
    FloatInjection[]     ILuaInjectable.floatInjections      { get => floatInjections;      set => floatInjections = value; }
    StringInjection[]    ILuaInjectable.stringInjections     { get => stringInjections;     set => stringInjections = value; }
    BoolInjection[]      ILuaInjectable.boolInjections       { get => boolInjections;       set => boolInjections = value; }
    IntInjection[]       ILuaInjectable.intInjections        { get => intInjections;        set => intInjections = value; }
    ScriptInjection[]    ILuaInjectable.scriptInjections     { get => scriptInjections;     set => scriptInjections = value; }
    AudioClipInjection[] ILuaInjectable.audioClipInjections  { get => audioClipInjections;  set => audioClipInjections = value; }

    internal static LuaEnv luaEnv = new LuaEnv();
    internal static float lastGCTime = 0;
    internal const float GCInterval = 1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void RegisterLuaGlobals()
    {
        luaEnv.Global.Set("json_parse", new Func<string, LuaTable>(JsonParseToLuaTable));
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
    private Action<Collision> luaOnCollisionEnter;
    private Action<Collider> luaOnTriggerEnter;
    private Action<string> luaOnNet;

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
    }

    void Awake() {
        if (luaScript == null) return;

        scriptScopeTable = luaEnv.NewTable();

        using (LuaTable meta = luaEnv.NewTable()) {
            meta.Set("__index", luaEnv.Global);
            scriptScopeTable.SetMetaTable(meta);
        }

        scriptScopeTable.Set("self", this);
        InjectAll();

        luaEnv.DoString(luaScript.text, luaScript.name, scriptScopeTable);

        luaAwake = scriptScopeTable.Get<Action>("awake");
        scriptScopeTable.Get("start",             out luaStart);
        scriptScopeTable.Get("update",            out luaUpdate);
        scriptScopeTable.Get("ondestroy",         out luaOnDestroy);
        scriptScopeTable.Get("onenable",          out luaOnEnable);
        scriptScopeTable.Get("ondisable",         out luaOnDisable);
        scriptScopeTable.Get("oncollisionenter",  out luaOnCollisionEnter);
        scriptScopeTable.Get("ontriggerenter",    out luaOnTriggerEnter);
        scriptScopeTable.Get("onnet",             out luaOnNet);

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
    void OnEnable()  => luaOnEnable?.Invoke();
    void OnDisable() => luaOnDisable?.Invoke();

    void Update() {
        if (scriptScopeTable == null) return;

        // Re-inject variables so Inspector changes take effect immediately
        InjectAll();

        luaUpdate?.Invoke();

        // Lua GC — important, don't skip this
        if (Time.time - LuaBehaviour.lastGCTime > GCInterval) {
            luaEnv.Tick();
            LuaBehaviour.lastGCTime = Time.time;
        }
    }

    void OnCollisionEnter(Collision c) => luaOnCollisionEnter?.Invoke(c);
    void OnTriggerEnter(Collider c)    => luaOnTriggerEnter?.Invoke(c);

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
