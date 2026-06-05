using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Shared editor GUI for any component implementing ILuaInjectable.
/// Used by both LuaBehaviourEditor and EasyLuaEditor.
/// All the variable list drawing, @var parsing, drag-reorder, color coding,
/// duplicate warnings, test logging, sync, and lua file section live here.
/// </summary>
public static class LuaInjectionEditorGUI {

    public enum VarType { GameObject, Float, String, Bool, Int, Script, AudioClip }

    public struct Entry {
        public VarType type;
        public string  name;
        public GameObject    goValue;
        public float         floatValue;
        public string        stringValue;
        public bool          boolValue;
        public int           intValue;
        public LuaBehaviour  scriptValue;
        public AudioClip     audioClipValue;
    }

    // ── Type color coding ──────────────────────────────────────────────
    public static readonly Dictionary<VarType, Color> typeColors = new Dictionary<VarType, Color> {
        { VarType.GameObject, new Color(0.90f, 0.55f, 0.20f) },
        { VarType.Float,      new Color(0.40f, 0.70f, 1.00f) },
        { VarType.String,     new Color(0.40f, 0.85f, 0.45f) },
        { VarType.Bool,       new Color(0.85f, 0.55f, 0.90f) },
        { VarType.Int,        new Color(1.00f, 0.80f, 0.30f) },
        { VarType.Script,     new Color(0.60f, 0.85f, 0.85f) },
        { VarType.AudioClip,  new Color(0.95f, 0.65f, 0.65f) },
    };

    public static readonly Dictionary<VarType, string> defaultKeys = new Dictionary<VarType, string> {
        { VarType.GameObject, "newGameObject" },
        { VarType.Float,      "newFloat" },
        { VarType.String,     "newString" },
        { VarType.Bool,       "newBool" },
        { VarType.Int,        "newInt" },
        { VarType.Script,     "newScript" },
        { VarType.AudioClip,  "newAudioClip" },
    };

    static readonly Dictionary<string, VarType> typeNameMap = new Dictionary<string, VarType> {
        { "gameobject", VarType.GameObject },
        { "go",         VarType.GameObject },
        { "float",      VarType.Float },
        { "number",     VarType.Float },
        { "string",     VarType.String },
        { "str",        VarType.String },
        { "bool",       VarType.Bool },
        { "boolean",    VarType.Bool },
        { "int",        VarType.Int },
        { "integer",    VarType.Int },
        { "script",     VarType.Script },
        { "luabehaviour", VarType.Script },
        { "audioclip",  VarType.AudioClip },
        { "audio",      VarType.AudioClip },
    };

    static readonly Regex varRegex = new Regex(
        @"^--\s*@var\s+(\w+)\s+(\w+)\s*(.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase
    );

    // ── Build entries from ILuaInjectable ───────────────────────────────
    public static List<Entry> BuildEntries(ILuaInjectable target) {
        var entries = new List<Entry>();

        if (target.injections != null)
            foreach (var inj in target.injections)
                entries.Add(new Entry { type = VarType.GameObject, name = inj.name, goValue = inj.value });

        if (target.floatInjections != null)
            foreach (var inj in target.floatInjections)
                entries.Add(new Entry { type = VarType.Float, name = inj.name, floatValue = inj.value });

        if (target.stringInjections != null)
            foreach (var inj in target.stringInjections)
                entries.Add(new Entry { type = VarType.String, name = inj.name, stringValue = inj.value });

        if (target.boolInjections != null)
            foreach (var inj in target.boolInjections)
                entries.Add(new Entry { type = VarType.Bool, name = inj.name, boolValue = inj.value });

        if (target.intInjections != null)
            foreach (var inj in target.intInjections)
                entries.Add(new Entry { type = VarType.Int, name = inj.name, intValue = inj.value });

        if (target.scriptInjections != null)
            foreach (var inj in target.scriptInjections)
                entries.Add(new Entry { type = VarType.Script, name = inj.name, scriptValue = inj.value });

        if (target.audioClipInjections != null)
            foreach (var inj in target.audioClipInjections)
                entries.Add(new Entry { type = VarType.AudioClip, name = inj.name, audioClipValue = inj.value });

        return entries;
    }

    // ── Apply entries back to ILuaInjectable ────────────────────────────
    public static void ApplyEntries(Object undoTarget, ILuaInjectable target, List<Entry> entries) {
        Undo.RecordObject(undoTarget, "Modify Lua Injections");

        var goList        = new List<Injection>();
        var floatList     = new List<FloatInjection>();
        var stringList    = new List<StringInjection>();
        var boolList      = new List<BoolInjection>();
        var intList       = new List<IntInjection>();
        var scriptList    = new List<ScriptInjection>();
        var audioClipList = new List<AudioClipInjection>();

        foreach (var e in entries) {
            switch (e.type) {
                case VarType.GameObject:
                    goList.Add(new Injection { name = e.name, value = e.goValue });
                    break;
                case VarType.Float:
                    floatList.Add(new FloatInjection { name = e.name, value = e.floatValue });
                    break;
                case VarType.String:
                    stringList.Add(new StringInjection { name = e.name, value = e.stringValue });
                    break;
                case VarType.Bool:
                    boolList.Add(new BoolInjection { name = e.name, value = e.boolValue });
                    break;
                case VarType.Int:
                    intList.Add(new IntInjection { name = e.name, value = e.intValue });
                    break;
                case VarType.Script:
                    scriptList.Add(new ScriptInjection { name = e.name, value = e.scriptValue });
                    break;
                case VarType.AudioClip:
                    audioClipList.Add(new AudioClipInjection { name = e.name, value = e.audioClipValue });
                    break;
            }
        }

        target.injections           = goList.ToArray();
        target.floatInjections      = floatList.ToArray();
        target.stringInjections     = stringList.ToArray();
        target.boolInjections       = boolList.ToArray();
        target.intInjections        = intList.ToArray();
        target.scriptInjections     = scriptList.ToArray();
        target.audioClipInjections  = audioClipList.ToArray();

        EditorUtility.SetDirty(undoTarget);
    }

    // ── Duplicate detection ────────────────────────────────────────────
    public static HashSet<string> FindDuplicateKeys(List<Entry> entries) {
        var seen = new HashSet<string>();
        var dupes = new HashSet<string>();
        foreach (var e in entries) {
            if (!string.IsNullOrEmpty(e.name)) {
                if (!seen.Add(e.name))
                    dupes.Add(e.name);
            }
        }
        return dupes;
    }

    // ── Parse @var from Lua source ─────────────────────────────────────
    public static List<Entry> ParseLuaVars(string luaSource) {
        var parsed = new List<Entry>();
        if (string.IsNullOrEmpty(luaSource)) return parsed;

        // Parse line-by-line to avoid multiline regex edge cases
        foreach (var line in luaSource.Split('\n')) {
            Match m = varRegex.Match(line);
            if (!m.Success) continue;

            string varName    = m.Groups[1].Value;
            string typeName   = m.Groups[2].Value.ToLower();
            string defaultStr = m.Groups[3].Value.Trim();

            if (!typeNameMap.TryGetValue(typeName, out VarType varType))
                continue;

            var entry = new Entry {
                type        = varType,
                name        = varName,
                stringValue = "",
            };

            if (!string.IsNullOrEmpty(defaultStr)) {
                switch (varType) {
                    case VarType.Float:
                        if (float.TryParse(defaultStr, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float f))
                            entry.floatValue = f;
                        break;
                    case VarType.Int:
                        if (int.TryParse(defaultStr, out int iv))
                            entry.intValue = iv;
                        break;
                    case VarType.Bool:
                        entry.boolValue = defaultStr.ToLower() == "true" || defaultStr == "1";
                        break;
                    case VarType.String:
                        if (defaultStr.Length >= 2 &&
                            ((defaultStr.StartsWith("\"") && defaultStr.EndsWith("\"")) ||
                             (defaultStr.StartsWith("'") && defaultStr.EndsWith("'"))))
                            defaultStr = defaultStr.Substring(1, defaultStr.Length - 2);
                        entry.stringValue = defaultStr;
                        break;
                }
            }

            parsed.Add(entry);
        }

        return parsed;
    }

    // ── Sync: add missing vars ─────────────────────────────────────────
    public static bool SyncFromLua(TextAsset script, List<Entry> entries) {
        if (script == null) return false;

        var parsed = ParseLuaVars(script.text);
        if (parsed.Count == 0) return false;

        var existingKeys = new HashSet<string>(entries.Select(e => e.name));
        int added = 0;

        foreach (var p in parsed) {
            if (!existingKeys.Contains(p.name)) {
                entries.Add(p);
                existingKeys.Add(p.name);
                added++;
            }
        }

        return added > 0;
    }

    // ── Full sync: replace with Lua vars, preserve existing values ─────
    public static void FullSyncFromLua(TextAsset script, List<Entry> entries) {
        if (script == null) return;

        var parsed = ParseLuaVars(script.text);

        var existingMap = new Dictionary<string, Entry>();
        foreach (var e in entries)
            if (!string.IsNullOrEmpty(e.name))
                existingMap[e.name] = e;

        entries.Clear();

        foreach (var p in parsed) {
            if (existingMap.TryGetValue(p.name, out Entry existing) && existing.type == p.type) {
                entries.Add(existing);
            } else {
                entries.Add(p);
            }
        }
    }

    // ── Draw the full injection GUI ────────────────────────────────────
    // Returns true if entries were modified (dirty).
    public static bool DrawVariablesGUI(
        List<Entry> entries,
        TextAsset luaScript,
        ref int dragFromIndex,
        ref int dragToIndex,
        Editor editor
    ) {
        bool dirty = false;

        EditorGUILayout.Space(6);

        // ── Header row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Lua Variables", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField($"{entries.Count} var{(entries.Count == 1 ? "" : "s")}",
            EditorStyles.miniLabel, GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        // ── Color legend
        EditorGUILayout.BeginHorizontal();
        foreach (var kvp in typeColors) {
            var prev = GUI.color;
            GUI.color = kvp.Value;
            GUILayout.Label("\u25A0", GUILayout.Width(10));
            GUI.color = prev;
            GUILayout.Label(kvp.Key.ToString(), EditorStyles.miniLabel, GUILayout.Width(62));
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(2);

        // ── @var sync status
        if (luaScript != null) {
            var parsedVars = ParseLuaVars(luaScript.text);
            if (parsedVars.Count > 0) {
                var existingKeys = new HashSet<string>(entries.Select(e => e.name));
                int missingCount = parsedVars.Count(p => !existingKeys.Contains(p.name));
                string status = missingCount > 0
                    ? $"{parsedVars.Count} @var(s) declared in Lua \u2014 {missingCount} not yet in Inspector"
                    : $"{parsedVars.Count} @var(s) declared in Lua \u2014 all synced";
                EditorGUILayout.HelpBox(status,
                    missingCount > 0 ? MessageType.Info : MessageType.None);
            }
        }

        // ── Duplicate detection
        var dupes = FindDuplicateKeys(entries);
        if (dupes.Count > 0) {
            EditorGUILayout.HelpBox(
                "Duplicate keys: " + string.Join(", ", dupes) +
                "\nOnly the last value for each duplicate key will be used in Lua.",
                MessageType.Warning);
        }

        // ── Draw entries
        int removeIndex = -1;

        for (int i = 0; i < entries.Count; i++) {
            var e = entries[i];
            bool isDupe = dupes.Contains(e.name);

            Color rowColor = typeColors.ContainsKey(e.type) ? typeColors[e.type] : Color.white;
            Color bgColor = new Color(rowColor.r, rowColor.g, rowColor.b, 0.12f);
            if (isDupe)
                bgColor = new Color(1f, 0.2f, 0.2f, 0.25f);

            Rect rowRect = EditorGUILayout.BeginHorizontal();

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rowRect, bgColor);

            // Drag handle
            var dragHandleRect = GUILayoutUtility.GetRect(14f, 18f, GUILayout.Width(14));
            EditorGUIUtility.AddCursorRect(dragHandleRect, MouseCursor.Pan);

            if (Event.current.type == EventType.Repaint) {
                var prevC = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.7f);
                float cx = dragHandleRect.x + 4f;
                for (int dot = 0; dot < 3; dot++) {
                    float cy = dragHandleRect.y + 4f + dot * 5f;
                    EditorGUI.DrawRect(new Rect(cx, cy, 2, 2), GUI.color);
                    EditorGUI.DrawRect(new Rect(cx + 4, cy, 2, 2), GUI.color);
                }
                GUI.color = prevC;
            }

            if (Event.current.type == EventType.MouseDown && dragHandleRect.Contains(Event.current.mousePosition)) {
                dragFromIndex = i; dragToIndex = i;
                Event.current.Use();
            }
            if (Event.current.type == EventType.MouseDrag && dragFromIndex >= 0 && rowRect.Contains(Event.current.mousePosition)) {
                dragToIndex = i;
                Event.current.Use();
                editor.Repaint();
            }
            if (dragFromIndex >= 0 && dragToIndex == i && dragFromIndex != i) {
                Rect lineRect = new Rect(rowRect.x, rowRect.y - 1, rowRect.width, 2);
                EditorGUI.DrawRect(lineRect, new Color(0.3f, 0.6f, 1f, 0.9f));
            }

            // Color pip
            var prevColor = GUI.color;
            GUI.color = rowColor;
            GUILayout.Label("\u25CF", GUILayout.Width(12));
            GUI.color = prevColor;

            // Type dropdown
            var newType = (VarType)EditorGUILayout.EnumPopup(e.type, GUILayout.Width(90));
            if (newType != e.type) {
                e.name = defaultKeys[newType]; e.type = newType;
                e.goValue = null; e.floatValue = 0f; e.stringValue = "";
                e.boolValue = false; e.intValue = 0;
                dirty = true;
            }

            // Name field
            var nameStyle = new GUIStyle(EditorStyles.textField);
            if (isDupe) nameStyle.normal.textColor = new Color(1f, 0.25f, 0.25f);
            var newName = EditorGUILayout.TextField(e.name, nameStyle, GUILayout.Width(120));
            if (newName != e.name) { e.name = newName; dirty = true; }

            // Value field
            switch (e.type) {
                case VarType.GameObject:
                    var newGo = (GameObject)EditorGUILayout.ObjectField(e.goValue, typeof(GameObject), true);
                    if (newGo != e.goValue) { e.goValue = newGo; dirty = true; }
                    break;
                case VarType.Float:
                    var newF = EditorGUILayout.FloatField(e.floatValue);
                    if (newF != e.floatValue) { e.floatValue = newF; dirty = true; }
                    break;
                case VarType.String:
                    var newS = EditorGUILayout.TextField(e.stringValue);
                    if (newS != e.stringValue) { e.stringValue = newS; dirty = true; }
                    break;
                case VarType.Bool:
                    var newB = EditorGUILayout.Toggle(e.boolValue);
                    if (newB != e.boolValue) { e.boolValue = newB; dirty = true; }
                    break;
                case VarType.Int:
                    var newI = EditorGUILayout.IntField(e.intValue);
                    if (newI != e.intValue) { e.intValue = newI; dirty = true; }
                    break;
                case VarType.Script:
                    var newScript = (LuaBehaviour)EditorGUILayout.ObjectField(e.scriptValue, typeof(LuaBehaviour), true);
                    if (newScript != e.scriptValue) { e.scriptValue = newScript; dirty = true; }
                    break;
                case VarType.AudioClip:
                    var newClip = (AudioClip)EditorGUILayout.ObjectField(e.audioClipValue, typeof(AudioClip), false);
                    if (newClip != e.audioClipValue) { e.audioClipValue = newClip; dirty = true; }
                    break;
            }

            // Remove button
            var xStyle = new GUIStyle(GUI.skin.button);
            xStyle.normal.textColor = new Color(1f, 0.35f, 0.35f);
            xStyle.fontStyle = FontStyle.Bold;
            if (GUILayout.Button("\u2715", xStyle, GUILayout.Width(22)))
                removeIndex = i;

            EditorGUILayout.EndHorizontal();
            entries[i] = e;
        }

        // Drag finish
        if (Event.current.type == EventType.MouseUp && dragFromIndex >= 0) {
            if (dragFromIndex != dragToIndex && dragToIndex >= 0 && dragToIndex < entries.Count) {
                var dragged = entries[dragFromIndex];
                entries.RemoveAt(dragFromIndex);
                int insertAt = dragToIndex;
                if (dragFromIndex < dragToIndex) insertAt--;
                insertAt = Mathf.Clamp(insertAt, 0, entries.Count);
                entries.Insert(insertAt, dragged);
                dirty = true;
            }
            dragFromIndex = -1; dragToIndex = -1;
            Event.current.Use();
            editor.Repaint();
        }

        if (removeIndex >= 0) {
            entries.RemoveAt(removeIndex);
            dirty = true;
        }

        EditorGUILayout.Space(4);

        // ── Bottom buttons
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("+ Add Variable")) {
            entries.Add(new Entry {
                type = VarType.Float,
                name = defaultKeys[VarType.Float],
                stringValue = ""
            });
            dirty = true;
        }

        bool hasScript = luaScript != null;
        EditorGUI.BeginDisabledGroup(!hasScript);
        var syncStyle = new GUIStyle(GUI.skin.button);
        syncStyle.normal.textColor = new Color(0.4f, 0.7f, 1f);
        syncStyle.fontStyle = FontStyle.Bold;
        if (GUILayout.Button("\u21BB Sync from Lua", syncStyle, GUILayout.Width(120))) {
            FullSyncFromLua(luaScript, entries);
            dirty = true;
        }
        EditorGUI.EndDisabledGroup();

        var testStyle = new GUIStyle(GUI.skin.button);
        testStyle.normal.textColor = new Color(0.3f, 0.75f, 0.4f);
        testStyle.fontStyle = FontStyle.Bold;
        if (GUILayout.Button("\u25B6 Test Log", testStyle, GUILayout.Width(90))) {
            LogAllInjections(entries);
        }

        EditorGUILayout.EndHorizontal();

        return dirty;
    }

    // ── Draw the Lua File section ──────────────────────────────────────
    public static void DrawLuaFileSection(SerializedProperty luaScriptProp, TextAsset currentScript) {
        EditorGUILayout.Space(12);

        Rect separatorRect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(separatorRect, new Color(0.35f, 0.35f, 0.35f));

        EditorGUILayout.Space(6);

        var luaHeaderStyle = new GUIStyle(EditorStyles.boldLabel) {
            fontSize  = 13,
            alignment = TextAnchor.MiddleLeft
        };
        EditorGUILayout.LabelField("Lua File", luaHeaderStyle);

        EditorGUILayout.Space(2);

        EditorGUILayout.PropertyField(luaScriptProp, new GUIContent("Script (.lua.txt)"));

        if (currentScript == null) {
            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "No Lua file assigned. This component won't run any Lua at runtime.\n" +
                "Drag a .lua.txt TextAsset here.",
                MessageType.Error);
        }
    }

    // ── Test log ───────────────────────────────────────────────────────
    public static void LogAllInjections(List<Entry> entries) {
        Debug.Log($"\u2500\u2500 Lua Injections \u2014 {entries.Count} variables \u2500\u2500");

        var dupes = FindDuplicateKeys(entries);

        foreach (var e in entries) {
            string dupeTag = dupes.Contains(e.name) ? "  \u26A0 DUPLICATE" : "";
            string val;
            switch (e.type) {
                case VarType.GameObject:
                    val = e.goValue != null ? e.goValue.name : "(null)";
                    break;
                case VarType.Float:  val = e.floatValue.ToString("F3"); break;
                case VarType.String: val = $"\"{e.stringValue}\"";      break;
                case VarType.Bool:   val = e.boolValue.ToString().ToLower(); break;
                case VarType.Int:    val = e.intValue.ToString();        break;
                case VarType.Script:    val = e.scriptValue != null ? e.scriptValue.name : "(null)"; break;
                case VarType.AudioClip: val = e.audioClipValue != null ? e.audioClipValue.name : "(null)"; break;
                default:                val = "?";                          break;
            }
            Debug.Log($"  [{e.type}]  {e.name} = {val}{dupeTag}");
        }

        if (dupes.Count > 0)
            Debug.LogWarning($"  \u26A0 {dupes.Count} duplicate key(s) detected: {string.Join(", ", dupes)}");

        Debug.Log("\u2500\u2500 End \u2500\u2500");
    }
}
