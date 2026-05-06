using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

[CustomEditor(typeof(LuaBehaviour))]
public class LuaBehaviourEditor : Editor {

    SerializedProperty luaScriptProp;
    List<LuaInjectionEditorGUI.Entry> entries = new List<LuaInjectionEditorGUI.Entry>();
    TextAsset lastSyncedScript;
    int dragFromIndex = -1;
    int dragToIndex   = -1;

    void OnEnable() {
        luaScriptProp = serializedObject.FindProperty("luaScript");
        var lb = (LuaBehaviour)target;
        entries = LuaInjectionEditorGUI.BuildEntries(lb);
        lastSyncedScript = lb.luaScript;
    }

    public override void OnInspectorGUI() {
        serializedObject.Update();

        var lb = (LuaBehaviour)target;

        // Apply serialized properties first
        serializedObject.ApplyModifiedProperties();

        // Auto-sync when Lua file changes
        if (lb.luaScript != lastSyncedScript) {
            lastSyncedScript = lb.luaScript;
            if (lb.luaScript != null) {
                if (LuaInjectionEditorGUI.SyncFromLua(lb.luaScript, entries))
                    LuaInjectionEditorGUI.ApplyEntries(lb, lb, entries);
            }
        }

        // Draw the shared variables GUI first
        bool dirty = LuaInjectionEditorGUI.DrawVariablesGUI(
            entries, lb.luaScript, ref dragFromIndex, ref dragToIndex, this);

        if (dirty)
            LuaInjectionEditorGUI.ApplyEntries(lb, lb, entries);

        // Lua File section at the bottom — uses SerializedProperty
        serializedObject.Update();
        LuaInjectionEditorGUI.DrawLuaFileSection(luaScriptProp, lb.luaScript);
        serializedObject.ApplyModifiedProperties();
    }
}
