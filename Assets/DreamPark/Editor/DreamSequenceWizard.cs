#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace DreamPark.Editor
{
    public sealed class DreamSequenceWizard : EditorWindow
    {
        private string contentId;
        private string sequenceName = "Dream Sequence";
        private List<Candidate> candidates = new List<Candidate>();
        private List<Candidate> selected = new List<Candidate>();
        private ReorderableList selectedList;
        private Action onGenerated;

        private sealed class Candidate
        {
            public string path;
            public string name;
            public AttractionTemplate template;
            public bool selected;
        }

        public static void Show(string contentId, string[] attractionPaths, Action onGenerated)
        {
            var window = CreateInstance<DreamSequenceWizard>();
            window.titleContent = new GUIContent("Add Dream Sequence");
            window.contentId = contentId;
            window.onGenerated = onGenerated;
            int baked = global::AttractionPackingBaker.BakeAllInContent(contentId);
            Debug.Log($"[DreamSequence] Refreshed packing data for {baked} attraction prefab(s) before compatibility filtering.");
            foreach (string path in attractionPaths ?? Array.Empty<string>())
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AttractionTemplate template = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
                if (template == null || template is DreamSequenceTemplate
                    || prefab.name.IndexOf("DreamSequence", StringComparison.OrdinalIgnoreCase) >= 0
                    || !DreamSequenceCompatibility.IsCompatible(template)) continue;
                window.candidates.Add(new Candidate { path = path, name = prefab.name, template = template });
            }
            window.RebuildList();
            window.minSize = new Vector2(480f, 460f);
            window.ShowUtility();
        }

        private void RebuildList()
        {
            selectedList = new ReorderableList(selected, typeof(Candidate), true, true, false, true);
            selectedList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Sequence order (drag to reorder)");
            selectedList.drawElementCallback = (rect, index, active, focused) =>
            {
                if (index >= 0 && index < selected.Count)
                    EditorGUI.LabelField(rect, $"{index + 1}. {selected[index].name}");
            };
            selectedList.onRemoveCallback = list =>
            {
                if (list.index < 0 || list.index >= selected.Count) return;
                selected[list.index].selected = false;
                selected.RemoveAt(list.index);
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Generate Dream Sequence", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Dream Sequences are the required 12 × 18 ft single-room fallback. Eligible attractions fit 12 × 18 ft at their authored or shrink size. Generated levels are scaled to the full sequence room even when that exceeds their normal grow limit.",
                MessageType.Info);
            sequenceName = EditorGUILayout.TextField("Name", sequenceName);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select all eligible"))
            {
                selected.Clear();
                foreach (var candidate in candidates) { candidate.selected = true; selected.Add(candidate); }
                RebuildList();
            }
            if (GUILayout.Button("Clear"))
            {
                foreach (var candidate in candidates) candidate.selected = false;
                selected.Clear();
                RebuildList();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Eligible attractions", EditorStyles.boldLabel);
            foreach (var candidate in candidates)
            {
                bool value = EditorGUILayout.ToggleLeft(candidate.name, candidate.selected);
                if (value == candidate.selected) continue;
                candidate.selected = value;
                // Checkbox selection inherits the Content Uploader's progression
                // order. The reorderable list remains available for an intentional
                // per-sequence override after selection.
                selected = candidates.Where(c => c.selected).ToList();
                RebuildList();
            }
            if (candidates.Count == 0) EditorGUILayout.HelpBox("No compatible attractions found. You can still generate the default start/end sequence.", MessageType.Warning);

            GUILayout.Space(8f);
            selectedList.DoLayoutList();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(sequenceName)))
            {
                if (GUILayout.Button("Generate Dream Sequence", GUILayout.Height(34f)))
                {
                    string path = DreamSequenceGenerator.Generate(contentId, sequenceName.Trim(), selected.Select(x => x.path).ToList());
                    if (!string.IsNullOrEmpty(path))
                    {
                        onGenerated?.Invoke();
                        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        EditorGUIUtility.PingObject(Selection.activeObject);
                        Close();
                    }
                }
            }
        }
    }

    internal static class DreamSequenceGenerator
    {
        public static string Generate(string contentId, string displayName, List<string> attractionPaths)
        {
            string contentRoot = $"Assets/Content/{contentId}";
            string prefabs = EnsureFolder(contentRoot, "Prefabs");
            string scripts = EnsureFolder(contentRoot, "Scripts");
            string materials = EnsureFolder(contentRoot, "Materials");
            string safeName = Sanitize(displayName);
            string controllerPath = $"{scripts}/dreamsequence-{safeName}-controller.lua.txt";
            string buttonPath = $"{scripts}/dreamsequence-{safeName}-button.lua.txt";
            WriteTextAsset(controllerPath, ControllerLua);
            WriteTextAsset(buttonPath, ButtonLua);
            Material material = GetOrCreateMaterial($"{materials}/DreamSequenceDefault.mat");

            string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{prefabs}/A_{safeName}.prefab");
            var root = new GameObject("A_" + safeName);
            try
            {
                var sequence = root.AddComponent<DreamSequenceTemplate>();
                sequence.sequenceName = displayName;
                sequence.size = GameLevelSize.Custom;
                sequence.customSize = new Vector2(DreamSequenceTemplate.StandardWidthFeet, DreamSequenceTemplate.StandardLengthFeet);
                sequence.generateFloor = true;
                // The sequence is required as an upload fallback, but it is not a
                // park-layout requirement. Conflating those would force the fallback
                // itself into every full-size generated park.
                sequence.gameRequiresAttraction = false;

                var levels = new GameObject("Levels");
                levels.transform.SetParent(root.transform, false);
                CreateStartLevel(levels.transform, material, AssetDatabase.LoadAssetAtPath<TextAsset>(buttonPath));

                int levelNumber = 1;
                foreach (string path in attractionPaths ?? new List<string>())
                {
                    GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    AttractionTemplate attraction = source != null ? source.GetComponent<AttractionTemplate>() : null;
                    if (attraction == null || !DreamSequenceCompatibility.IsCompatible(attraction)) continue;

                    GameObject instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
                    instance.name = $"Level{levelNumber:00}_{source.name}";
                    instance.transform.SetParent(levels.transform, false);
                    Vector2 scale = DreamSequenceCompatibility.SequenceScale(attraction);
                    bool rotate = DreamSequenceCompatibility.ShouldRotate(attraction);
                    instance.transform.localRotation = rotate ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
                    instance.transform.localScale = new Vector3(scale.x, 1f, scale.y);
                    instance.SetActive(false);

                    string guid = AssetDatabase.AssetPathToGUID(path);
                    sequence.levels.Add(new DreamSequenceLevel
                    {
                        sourceGuid = guid,
                        displayName = source.name,
                        address = $"{contentId}/Levels/{attraction.size}/{source.name}"
                    });
                    levelNumber++;
                }

                CreateEndLevel(levels.transform, material);
                CreateNavigation(root.transform, material, AssetDatabase.LoadAssetAtPath<TextAsset>(buttonPath));

                var controller = root.AddComponent<LuaBehaviour>();
                controller.luaScript = AssetDatabase.LoadAssetAtPath<TextAsset>(controllerPath);
                controller.injections = new[] { new Injection { name = "levelParent", value = levels } };
                controller.stringInjections = new[] { new StringInjection { name = "globalName", value = "dream_sequence" } };
                controller.boolInjections = new[] { new BoolInjection { name = "autoStart", value = false } };

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[DreamSequence] Generated '{prefabPath}' with {attractionPaths?.Count ?? 0} selected attraction(s).");
            return prefabPath;
        }

        private static void CreateStartLevel(Transform parent, Material material, TextAsset buttonScript)
        {
            var level = new GameObject("Level00_Start");
            level.transform.SetParent(parent, false);
            CreateCube("Start Pedestal", level.transform, new Vector3(0f, 0.35f, 0f), new Vector3(2.4f, 0.7f, 2.4f), material, false);
            GameObject button = CreateCube("START", level.transform, new Vector3(0f, 0.85f, 0f), new Vector3(1.8f, 0.28f, 1.8f), material, true);
            AddButtonScript(button, buttonScript, "start");
        }

        private static void CreateEndLevel(Transform parent, Material material)
        {
            var level = new GameObject("Level99_EndScreen");
            level.transform.SetParent(parent, false);
            level.SetActive(false);
            CreateCube("End Screen", level.transform, new Vector3(0f, 1.6f, 3.6f), new Vector3(5f, 3f, 0.18f), material, false);
        }

        private static void CreateNavigation(Transform root, Material material, TextAsset buttonScript)
        {
            var nav = new GameObject("Default Sequence Navigation");
            nav.transform.SetParent(root, false);
            nav.transform.localPosition = new Vector3(0f, 0f, -7.7f);
            GameObject back = CreateCube("BACK", nav.transform, new Vector3(-1.2f, 1f, 0f), new Vector3(1.4f, 1.4f, 0.35f), material, true);
            GameObject forward = CreateCube("FORWARD", nav.transform, new Vector3(1.2f, 1f, 0f), new Vector3(1.4f, 1.4f, 0.35f), material, true);
            AddButtonScript(back, buttonScript, "back");
            AddButtonScript(forward, buttonScript, "forward");
        }

        private static GameObject CreateCube(string name, Transform parent, Vector3 position, Vector3 scale, Material material, bool trigger)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<BoxCollider>().isTrigger = trigger;
            return go;
        }

        private static void AddButtonScript(GameObject go, TextAsset script, string action)
        {
            var lua = go.AddComponent<LuaBehaviour>();
            lua.luaScript = script;
            lua.stringInjections = new[] { new StringInjection { name = "action", value = action } };
        }

        private static Material GetOrCreateMaterial(string path)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader shader = Shader.Find("Shader Graphs/DreamPark-Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader) { name = "Dream Sequence Default" };
            if (material.HasProperty("_baseColor")) material.SetColor("_baseColor", new Color(0.15f, 0.55f, 1f, 1f));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static string EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, child);
            return path;
        }

        private static void WriteTextAsset(string path, string contents)
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (!File.Exists(full) || File.ReadAllText(full) != contents) File.WriteAllText(full, contents);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        private static string Sanitize(string value)
        {
            string result = new string((value ?? "DreamSequence").Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrEmpty(result) ? "DreamSequence" : result;
        }

        private const string ControllerLua = @"-- Generated Dream Sequence controller. Safe to customize.
local levels = {}
local current = 1
local running = false

local function rebuild()
    levels = {}
    local t = levelParent.transform
    for i = 0, t.childCount - 1 do levels[#levels + 1] = t:GetChild(i).gameObject end
end

local function show(index)
    if #levels == 0 then return end
    current = math.max(1, math.min(index, #levels))
    for i = 1, #levels do levels[i]:SetActive(i == current) end
end

function start_game() running = true; show(math.min(2, #levels)) end
function advance() show(current + 1); if current == #levels then running = false end end
function back() show(current - 1) end
function is_running() return running end
function onready()
    rebuild(); show(autoStart == true and math.min(2, #levels) or 1)
    if globalName ~= nil and globalName ~= '' then rawset(_G, globalName, self.ScriptScope) end
end
function ondestroy() if globalName ~= nil and globalName ~= '' then rawset(_G, globalName, nil) end end
";

        private const string ButtonLua = @"-- Generated hand-collider navigation button. Safe to customize.
local armed = true
local function controller()
    local t = self.transform.parent
    while t ~= nil do
        local scope = dp.scope(t.gameObject)
        if scope ~= nil and scope.advance ~= nil then return scope end
        t = t.parent
    end
    return dp.attraction(self.gameObject)
end
function ontriggerenter(other)
    if not armed or not dp.is_player(other) then return end
    armed = false
    local c = controller()
    if c == nil then return end
    if action == 'start' and c.start_game ~= nil then c.start_game()
    elseif action == 'back' and c.back ~= nil then c.back()
    elseif c.advance ~= nil then c.advance() end
end
function ontriggerexit(other) if dp.is_player(other) then armed = true end end
";
    }
}
#endif
