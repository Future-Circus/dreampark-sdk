#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// Creates reusable SDK-owned controls. Existing prefabs retain their
    /// authored materials, colliders, and layout; a one-time upgrade only
    /// separates each visual mesh and adds press feedback when it is missing.
    /// </summary>
    public static class DreamSequenceControlPrefabBuilder
    {
        internal const string StartButtonPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Start Button.prefab";
        internal const string ElevatorControlsPrefabPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Elevator Controls.prefab";

        private const string StartMeshPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Sphere.001.mesh";
        private const string StartMaterialPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/StartButton/Start.mat";
        private const string ArrowTexturePath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/elevator-arrow-up.png";
        private const string ArrowMaterialPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Elevator Arrow.mat";
        private const string BackplateMaterialPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/Overlay/Elevator Backplate.mat";
        private const string ButtonScriptPath =
            "Assets/DreamPark/RuntimeAssets/DreamSequence/DreamSequenceButton.lua.txt";

        [MenuItem("DreamPark/Developer/Create Missing Dream Sequence Control Prefabs")]
        public static void CreateMissingDefaults()
        {
            Material arrow = GetOrCreateMaterial(ArrowMaterialPath,
                new Color(1f, 1f, 1f, 1f), AssetDatabase.LoadAssetAtPath<Texture2D>(ArrowTexturePath));
            Material backplate = GetOrCreateMaterial(BackplateMaterialPath,
                new Color(0.07f, 0.11f, 0.16f, 1f), null);
            TextAsset buttonScript = AssetDatabase.LoadAssetAtPath<TextAsset>(ButtonScriptPath);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(StartButtonPrefabPath) == null)
                CreateStartButton(buttonScript);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(ElevatorControlsPrefabPath) == null)
                CreateElevatorControls(arrow, backplate, buttonScript);

            UpgradeFeedbackOnExistingDefaults();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("DreamPark/Developer/Upgrade Dream Sequence Button Feedback")]
        public static void UpgradeFeedbackOnExistingDefaults()
        {
            UpgradePrefab(StartButtonPrefabPath);
            UpgradePrefab(ElevatorControlsPrefabPath);
        }

        private static void UpgradePrefab(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) return;
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                bool changed = false;
                foreach (LuaBehaviour lua in root.GetComponentsInChildren<LuaBehaviour>(true))
                    changed |= EnsureButtonFeedback(lua.gameObject);
                if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void CreateStartButton(TextAsset buttonScript)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(StartMeshPath);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(StartMaterialPath);
            if (mesh == null)
            {
                Debug.LogError($"[DreamSequence] Missing extracted Start button mesh: {StartMeshPath}");
                return;
            }

            var root = new GameObject("START — Super Adventure Land Button");
            try
            {
                root.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                root.transform.localScale = new Vector3(0.6772702f, 0.4715968f, 0.6772702f);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = material;
                var trigger = root.AddComponent<BoxCollider>();
                trigger.center = mesh.bounds.center;
                trigger.size = mesh.bounds.size;
                trigger.isTrigger = true;
                AddButtonScript(root, buttonScript, "start");
                EnsureButtonFeedback(root);
                PrefabUtility.SaveAsPrefabAsset(root, StartButtonPrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void CreateElevatorControls(Material arrow, Material backplate, TextAsset buttonScript)
        {
            var root = new GameObject("Default 3D Level Navigation");
            try
            {
                CreateCube("Navigation Backplate", root.transform, Vector3.zero,
                    new Vector3(0.58f, 1.15f, 0.10f), backplate, false);
                GameObject up = CreateCube("UP", root.transform, new Vector3(0f, 0.27f, -0.09f),
                    new Vector3(0.38f, 0.38f, 0.12f), arrow, true);
                AddButtonScript(up, buttonScript, "back");
                GameObject down = CreateCube("DOWN", root.transform, new Vector3(0f, -0.27f, -0.09f),
                    new Vector3(0.38f, 0.38f, 0.12f), arrow, true);
                down.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
                AddButtonScript(down, buttonScript, "forward");
                EnsureButtonFeedback(up);
                EnsureButtonFeedback(down);
                PrefabUtility.SaveAsPrefabAsset(root, ElevatorControlsPrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static GameObject CreateCube(string name, Transform parent, Vector3 position,
            Vector3 scale, Material material, bool trigger)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<BoxCollider>().isTrigger = trigger;
            return go;
        }

        private static void AddButtonScript(GameObject target, TextAsset script, string action)
        {
            var lua = target.AddComponent<LuaBehaviour>();
            lua.luaScript = script;
            lua.stringInjections = new[] { new StringInjection { name = "action", value = action } };
        }

        internal static bool EnsureButtonFeedback(GameObject button)
        {
            if (button == null || button.GetComponent<DreamSequenceButtonFeedback>() != null) return false;
            MeshFilter oldFilter = button.GetComponent<MeshFilter>();
            MeshRenderer oldRenderer = button.GetComponent<MeshRenderer>();
            if (oldFilter == null || oldRenderer == null || button.GetComponent<Collider>() == null)
                return false;

            var visual = new GameObject("Press Visual");
            visual.transform.SetParent(button.transform, false);
            MeshFilter filter = visual.AddComponent<MeshFilter>();
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            EditorUtility.CopySerialized(oldFilter, filter);
            EditorUtility.CopySerialized(oldRenderer, renderer);
            Object.DestroyImmediate(oldFilter);
            Object.DestroyImmediate(oldRenderer);

            var feedback = button.AddComponent<DreamSequenceButtonFeedback>();
            feedback.visual = visual.transform;
            return true;
        }

        private static Material GetOrCreateMaterial(string path, Color color, Texture texture)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            Shader shader = Shader.Find("Shader Graphs/DreamPark-Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            if (material.HasProperty("_baseColor")) material.SetColor("_baseColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_baseTex")) material.SetTexture("_baseTex", texture);
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
#endif
