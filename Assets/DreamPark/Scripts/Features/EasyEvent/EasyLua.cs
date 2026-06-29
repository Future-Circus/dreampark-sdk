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
    public Vector3Injection[]        vector3Injections;
    public ColorInjection[]          colorInjections;
    public TransformInjection[]      transformInjections;
    public MaterialInjection[]       materialInjections;
    public SpriteInjection[]         spriteInjections;
    public TextureInjection[]        textureInjections;
    public ComponentInjection[]      componentInjections;
    public GameObjectListInjection[] gameObjectListInjections;

    // ILuaInjectable
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

    public bool delayNextEvent = true;

    // Lua callbacks
    private Action luaStart;
    private Action luaOnDestroy;
    private Action luaOnEnable;
    private Action luaOnDisable;

    // update() called via LuaFunction so we can check return value
    // without needing Func<bool> in CSharpCallLua
    private LuaFunction luaUpdate;

    // Optional Unity messages (physics, frame-timing, app lifecycle) are wired via
    // opt-in relay components — added only when the script defines the function.
    private readonly System.Collections.Generic.List<Behaviour> luaRelays = new System.Collections.Generic.List<Behaviour>();

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

    // Collections pushed once in Awake (not in the per-frame InjectAll) to avoid
    // allocating a fresh LuaTable every frame. 1-based so #list / ipairs work in Lua.
    private void InjectCollections() {
        if (scriptScopeTable == null || gameObjectListInjections == null) return;
        foreach (var inj in gameObjectListInjections) {
            using (LuaTable t = LuaBehaviour.luaEnv.NewTable()) {
                if (inj.value != null)
                    for (int i = 0; i < inj.value.Length; i++)
                        t.Set(i + 1, inj.value[i]);
                scriptScopeTable.Set(inj.name, t);
            }
        }
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
        InjectCollections();

        LuaBehaviour.luaEnv.DoString(luaScript.text, luaScript.name, scriptScopeTable);

        scriptScopeTable.Get("start",     out luaStart);
        scriptScopeTable.Get("ondestroy", out luaOnDestroy);
        scriptScopeTable.Get("onenable",  out luaOnEnable);
        scriptScopeTable.Get("ondisable", out luaOnDisable);

        // Get update as LuaFunction so we can inspect its return value
        luaUpdate = scriptScopeTable.Get<LuaFunction>("update");

        // Opt-in physics + frame-timing + app-lifecycle messages (incl. ontriggerenter /
        // oncollisionenter, now routed through the shared relay system).
        LuaMessageRelays.Bind(gameObject, scriptScopeTable, luaRelays);
        // Match host state now in case this component starts disabled (OnEnable won't fire).
        LuaMessageRelays.SetEnabled(luaRelays, isActiveAndEnabled);
    }

    private void OnEnable() {
        LuaMessageRelays.SetEnabled(luaRelays, true);
        luaOnEnable?.Invoke();
    }

    private void OnDisable() {
        luaOnDisable?.Invoke();
        LuaMessageRelays.SetEnabled(luaRelays, false);
    }

    // ── EasyEvent chain ────────────────────────────────────────────────

    public override void OnEvent(object arg0 = null) {
        isEnabled = true;
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

    private void OnDestroy() {
        luaOnDestroy?.Invoke();
        luaUpdate?.Dispose();
        if (scriptScopeTable != null)
            scriptScopeTable.Dispose();
    }
}
