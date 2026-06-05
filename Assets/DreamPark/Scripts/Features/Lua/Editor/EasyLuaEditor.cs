#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

[CustomEditor(typeof(EasyLua))]
public class EasyLuaEditor : EasyEventEditor {

    SerializedProperty luaScriptProp;
    SerializedProperty delayNextEventProp;
    List<LuaInjectionEditorGUI.Entry> entries = new List<LuaInjectionEditorGUI.Entry>();
    TextAsset lastSyncedScript;
    int dragFromIndex = -1;
    int dragToIndex   = -1;

    void OnEnable() {
        luaScriptProp      = serializedObject.FindProperty("luaScript");
        delayNextEventProp = serializedObject.FindProperty("delayNextEvent");
        var el = (EasyLua)target;
        entries = LuaInjectionEditorGUI.BuildEntries(el);
        lastSyncedScript = el.luaScript;
    }

    public override void OnInspectorGUI() {
        serializedObject.Update();

        var el = (EasyLua)target;

        // Draw EasyLua-specific field
        EditorGUILayout.PropertyField(delayNextEventProp,
            new GUIContent("Delay Next Event", "Wait for update() to return true before triggering the next EasyEvent"));

        // Apply serialized properties first
        serializedObject.ApplyModifiedProperties();

        // Auto-sync when Lua file changes
        if (el.luaScript != lastSyncedScript) {
            lastSyncedScript = el.luaScript;
            if (el.luaScript != null) {
                if (LuaInjectionEditorGUI.SyncFromLua(el.luaScript, entries))
                    LuaInjectionEditorGUI.ApplyEntries(el, el, entries);
            }
        }

        // Draw the shared variables GUI first
        bool dirty = LuaInjectionEditorGUI.DrawVariablesGUI(
            entries, el.luaScript, ref dragFromIndex, ref dragToIndex, this);

        if (dirty)
            LuaInjectionEditorGUI.ApplyEntries(el, el, entries);

        // Lua File section at the bottom
        serializedObject.Update();
        LuaInjectionEditorGUI.DrawLuaFileSection(luaScriptProp, el.luaScript);
        serializedObject.ApplyModifiedProperties();

        // Draw EasyEvent chain footer (state indicator, chain info)
        DrawEasyEventInspectorFooter(el);
    }
}
#endif
