using UnityEngine;
using XLua;
using System;

/// <summary>
/// An EasyEvent that runs a Lua script. Sits in the EasyEvent chain
/// alongside all other C# EasyEvents (EasyCollision, EasyDelay, etc).
///
/// Uses the same Lua script conventions as LuaBehaviour:
///   - start()    → called when the chain triggers OnEvent()
///   - update()   → called every frame while active. Return true = done.
///   - ondestroy() → called on disable/cleanup
///
/// When update() returns true, EasyLua automatically fires the next
/// event in the chain and disables. No special Lua API needed.
///
/// The same .lua.txt file works in both LuaBehaviour and EasyLua.
/// In LuaBehaviour, returning true from update() is harmless (ignored).
/// In EasyLua, returning true means "I'm done, pass to next".
/// </summary>
[LuaCallCSharp]
public class EasyLua : EasyEvent, ILuaInjectable {

    public new TextAsset        luaScript;
    public new Injection[]      injections;
    public FloatInjection[]     floatInjections;
    public StringInjection[]    stringInjections;
    public BoolInjection[]      boolInjections;
    public IntInjection[]       intInjections;
    public ScriptInjection[]    scriptInjections;
    public AudioClipInjection[] audioClipInjections;

    // ILuaInjectable
    TextAsset            ILuaInjectable.luaScript            { get => luaScript;            set => luaScript = value; }
    Injection[]          ILuaInjectable.injections           { get => injections;           set => injections = value; }
    FloatInjection[]     ILuaInjectable.floatInjections      { get => floatInjections;      set => floatInjections = value; }
    StringInjection[]    ILuaInjectable.stringInjections     { get => stringInjections;     set => stringInjections = value; }
    BoolInjection[]      ILuaInjectable.boolInjections       { get => boolInjections;       set => boolInjections = value; }
    IntInjection[]       ILuaInjectable.intInjections        { get => intInjections;        set => intInjections = value; }
    ScriptInjection[]    ILuaInjectable.scriptInjections     { get => scriptInjections;     set => scriptInjections = value; }
    AudioClipInjection[] ILuaInjectable.audioClipInjections  { get => audioClipInjections;  set => audioClipInjections = value; }

    public bool delayNextEvent = true;

    // Lua callbacks
    private Action luaStart;
    private Action luaOnDestroy;
    private Action<Collision> luaOnCollisionEnter;
    private Action<Collider> luaOnTriggerEnter;

    // update() called via LuaFunction so we can check return value
    // without needing Func<bool> in CSharpCallLua
    private LuaFunction luaUpdate;

    private LuaTable scriptScopeTable;

    private void InjectAll() {
        if (scriptScopeTable == null) return;

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
        if (scriptInjections != null)
            foreach (var inj in scriptInjections)
                if (inj.value != null && inj.value.ScriptScope != null)
                    scriptScopeTable.Set(inj.name, inj.value.ScriptScope);
        if (audioClipInjections != null)
            foreach (var inj in audioClipInjections)
                scriptScopeTable.Set(inj.name, inj.value);
    }

    public override void Awake() {
        base.Awake();

        if (luaScript == null) return;

        scriptScopeTable = LuaBehaviour.luaEnv.NewTable();

        using (LuaTable meta = LuaBehaviour.luaEnv.NewTable()) {
            meta.Set("__index", LuaBehaviour.luaEnv.Global);
            scriptScopeTable.SetMetaTable(meta);
        }

        scriptScopeTable.Set("self", this);
        InjectAll();

        LuaBehaviour.luaEnv.DoString(luaScript.text, luaScript.name, scriptScopeTable);

        scriptScopeTable.Get("start",             out luaStart);
        scriptScopeTable.Get("ondestroy",         out luaOnDestroy);
        scriptScopeTable.Get("oncollisionenter",  out luaOnCollisionEnter);
        scriptScopeTable.Get("ontriggerenter",    out luaOnTriggerEnter);

        // Get update as LuaFunction so we can inspect its return value
        luaUpdate = scriptScopeTable.Get<LuaFunction>("update");
    }

    // ── EasyEvent chain ────────────────────────────────────────────────

    public override void OnEvent(object arg0 = null) {
        base.OnEvent(arg0);
        luaStart?.Invoke();

        if (!delayNextEvent) {
            onEvent?.Invoke(arg0);
        }
    }

    public override void OnEventDisable() {
        base.OnEventDisable();
        luaOnDestroy?.Invoke();
    }

    // ── Update — runs while active, auto-detects done ──────────────────

    private void Update() {
        if (scriptScopeTable == null) return;
        if (!isEnabled) return;

        InjectAll();

        if (luaUpdate != null) {
            object[] results = luaUpdate.Call();

            // If update() returned true, script is done
            if (results != null && results.Length > 0 && results[0] is bool done && done) {
                if (delayNextEvent) {
                    OnEventDisable();
                    onEvent?.Invoke(null);
                }
            }
        }
    }

    private void OnCollisionEnter(Collision c) => luaOnCollisionEnter?.Invoke(c);
    private void OnTriggerEnter(Collider c)    => luaOnTriggerEnter?.Invoke(c);

    private void OnDestroy() {
        luaOnDestroy?.Invoke();
        luaUpdate?.Dispose();
        if (scriptScopeTable != null)
            scriptScopeTable.Dispose();
    }
}
