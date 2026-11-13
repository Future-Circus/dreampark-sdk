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

/// <summary>
/// Path gizmo for any LuaBehaviour with a GameObject-list @var named "path" (e.g. a SAL Jubbo patrol): while the
/// walker, its path container or one of its path points is selected, the Scene view shows the route in order —
/// a line between the points (closed when the string @var "patrol" is not "pingpong"), a numbered marker per point,
/// an arrow per leg and a dotted line from the walker to its first point. Editor-only drawing: no objects, no
/// renderers, nothing in a build (it replaces the mesh "gizmo" spheres and rods authored next to old patrols).
/// </summary>
[InitializeOnLoad]
static class LuaPathGizmo {
    const string PathVar = "path";
    const string ModeVar = "patrol";
    static readonly Color LineColor = new Color(1f, 0.78f, 0.15f, 0.95f);
    static readonly Color PointColor = new Color(1f, 0.55f, 0.1f, 1f);

    static LuaPathGizmo() { SceneView.duringSceneGui += OnSceneGUI; }

    static GameObject[] PathOf(LuaBehaviour lb) {
        if (lb == null || lb.gameObjectListInjections == null) return null;
        foreach (var inj in lb.gameObjectListInjections)
            if (inj != null && inj.name == PathVar && inj.value != null && inj.value.Length > 0) return inj.value;
        return null;
    }

    static bool IsLoop(LuaBehaviour lb) {
        if (lb.stringInjections != null)
            foreach (var s in lb.stringInjections)
                if (s != null && s.name == ModeVar) return !string.Equals(s.value, "pingpong", System.StringComparison.OrdinalIgnoreCase);
        return true;
    }

    // the walkers whose route the selection belongs to: the selected walker itself, else any LuaBehaviour near it
    // (up to three parents up) whose path lists the selected object, one of its children or its parent
    static readonly List<LuaBehaviour> found = new List<LuaBehaviour>();
    static List<LuaBehaviour> Owners(GameObject sel) {
        found.Clear();
        foreach (var lb in sel.GetComponents<LuaBehaviour>()) if (PathOf(lb) != null) found.Add(lb);
        if (found.Count > 0) return found;
        Transform scope = sel.transform;
        for (int up = 0; up < 3 && scope.parent != null; up++) {
            scope = scope.parent;
            foreach (var lb in scope.GetComponentsInChildren<LuaBehaviour>(true)) {
                var pts = PathOf(lb);
                if (pts == null) continue;
                foreach (var p in pts) {
                    if (p == null) continue;
                    if (p == sel || p.transform.IsChildOf(sel.transform) || (sel.transform.parent != null && p.transform.parent == sel.transform)) {
                        found.Add(lb); break;
                    }
                }
            }
            if (found.Count > 0) break;
        }
        return found;
    }

    static void OnSceneGUI(SceneView view) {
        if (Event.current.type != EventType.Repaint) return;
        var sel = Selection.activeGameObject;
        if (sel == null) return;
        foreach (var lb in Owners(sel)) Draw(lb);
    }

    static void Draw(LuaBehaviour lb) {
        var raw = PathOf(lb);
        var pts = new List<Vector3>();
        foreach (var g in raw) if (g != null) pts.Add(g.transform.position);
        if (pts.Count == 0) return;
        bool loop = IsLoop(lb) && pts.Count > 2;
        var prevZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = new Color(LineColor.r, LineColor.g, LineColor.b, 0.6f);
        Handles.DrawDottedLine(lb.transform.position, pts[0], 4f);
        Handles.color = LineColor;
        int legs = loop ? pts.Count : pts.Count - 1;
        for (int i = 0; i < legs; i++) {
            Vector3 a = pts[i], b = pts[(i + 1) % pts.Count];
            Handles.DrawAAPolyLine(4f, a, b);
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-6f) continue;
            Vector3 mid = (a + b) * 0.5f;
            float s = HandleUtility.GetHandleSize(mid) * 0.12f;
            Handles.ConeHandleCap(0, mid, Quaternion.LookRotation(d.normalized), s, EventType.Repaint);
        }
        for (int i = 0; i < pts.Count; i++) {
            float s = HandleUtility.GetHandleSize(pts[i]) * 0.09f;
            Handles.color = PointColor;
            Handles.SphereHandleCap(0, pts[i], Quaternion.identity, s, EventType.Repaint);
            Handles.Label(pts[i] + Vector3.up * s * 1.5f, (i + 1).ToString(), EditorStyles.boldLabel);
        }
        Handles.zTest = prevZ;
    }
}
