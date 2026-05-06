#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace DreamPark.Editor
{
    /// <summary>
    /// Programmatically builds <c>Assets/DreamPark/Samples/MultiplayerTest.unity</c>
    /// — a ready-to-run scene for testing multiplayer against the local dev
    /// server (DreamPark → Multiplayer → Start Local Server).
    ///
    /// Generating scenes from code (vs. hand-authoring a .unity file) keeps
    /// the sample reproducible, diff-friendly, and regenerate-able if it ever
    /// gets broken by a Unity upgrade or a refactor.
    /// </summary>
    internal static class MultiplayerTestSceneGenerator
    {
        private const string SamplesDir = "Assets/DreamPark/Samples";
        private const string ScenePath = SamplesDir + "/MultiplayerTest.unity";

        [MenuItem("DreamPark/Multiplayer/Create Test Scene", priority = 50)]
        private static void CreateOrOpenTestScene()
        {
            if (File.Exists(ScenePath))
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Multiplayer Test Scene",
                    $"A test scene already exists at:\n{ScenePath}\n\n" +
                    "Open the existing one, or regenerate from scratch? " +
                    "Regenerating will overwrite the existing scene file.",
                    "Open Existing", "Cancel", "Regenerate");

                switch (choice)
                {
                    case 0: // Open existing
                        EditorSceneManager.OpenScene(ScenePath);
                        return;
                    case 1: // Cancel
                        return;
                    case 2: // Regenerate — fall through to Build()
                        break;
                }
            }

            Build();
        }

        private static void Build()
        {
            // Prompt to save unsaved changes in current scene first.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (!AssetDatabase.IsValidFolder(SamplesDir))
            {
                Directory.CreateDirectory(SamplesDir);
                AssetDatabase.Refresh();
            }

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Ground plane so the cube has something to sit on.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = Vector3.one * 2f;

            // Networked test prop.
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "TestProp";
            cube.transform.position = new Vector3(0, 0.75f, 0);
            cube.AddComponent<NetId>();
            var testObj = cube.AddComponent<TestNetObject>();

            // DreamBoxClient manager — pre-configured for local dev, auto-connect off.
            var clientGO = new GameObject("DreamBoxClient");
            var client = clientGO.AddComponent<DreamBoxClient>();
            client.serverIP = "127.0.0.1";
            client.serverPort = 7777;
            client.connectionKey = "dreambox";
            client.connectOnStart = false;

            // UI.
            BuildUI(client, testObj);

            // Nice default camera angle.
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0, 2.5f, -4f);
                cam.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
                cam.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
                cam.clearFlags = CameraClearFlags.SolidColor;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[DreamPark] Multiplayer test scene created at {ScenePath}");

            EditorUtility.DisplayDialog(
                "Multiplayer Test Scene Ready",
                "Created " + ScenePath + "\n\n" +
                "Next steps:\n" +
                "  1. DreamPark → Multiplayer → Start Local Server\n" +
                "  2. Press Play ▶ in this scene\n" +
                "  3. Click 'Connect to Local Server' in the on-screen panel\n" +
                "  4. Click 'Send Random Color' — the cube should change colour.",
                "OK");
        }

        // -----------------------------------------------------------
        // UI construction
        // -----------------------------------------------------------

        private static void BuildUI(DreamBoxClient client, TestNetObject testObj)
        {
            // Event system is required for UI input. Pick the input module
            // that matches the project's active input handling — the legacy
            // StandaloneInputModule throws every frame when the new Input
            // System is the active handler.
            var eventSystem = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            eventSystem.AddComponent<InputSystemUIInputModule>();
#else
            eventSystem.AddComponent<StandaloneInputModule>();
#endif

            // Canvas (screen-space overlay).
            var canvasGO = new GameObject("TestUI",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Panel (top-left).
            var panel = new GameObject("Panel",
                typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(canvasGO.transform, worldPositionStays: false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0, 1);
            panelRect.anchorMax = new Vector2(0, 1);
            panelRect.pivot = new Vector2(0, 1);
            panelRect.anchoredPosition = new Vector2(20, -20);
            panelRect.sizeDelta = new Vector2(520, 360);

            panel.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.09f, 0.92f);

            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 18, 18);
            layout.spacing = 8;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            CreateText(panel.transform, "Title", "DreamPark Multiplayer Test", 22, FontStyle.Bold);
            var stateLabel = CreateText(panel.transform, "StateLabel",
                "State: (press Play)", 16, FontStyle.Normal);

            // Small spacer.
            var spacer = CreateText(panel.transform, "Spacer", "", 4, FontStyle.Normal);
            spacer.GetComponent<LayoutElement>().minHeight = 4;

            var connectBtn = CreateButton(panel.transform, "ConnectBtn", "Connect to Local Server");
            var disconnectBtn = CreateButton(panel.transform, "DisconnectBtn", "Disconnect");
            var sendBtn = CreateButton(panel.transform, "SendBtn", "Send Random Color");

            var instructions = CreateText(panel.transform, "Instructions",
                "1. DreamPark → Multiplayer → Start Local Server\n" +
                "2. Press Play ▶\n" +
                "3. Click 'Connect to Local Server'\n" +
                "4. Click 'Send Random Color' — the cube changes colour.",
                12, FontStyle.Italic);
            instructions.color = new Color(0.7f, 0.72f, 0.78f);

            // Wire the UI controller.
            var controllerGO = new GameObject("TestUIController");
            var controller = controllerGO.AddComponent<MultiplayerTestUI>();
            controller.client = client;
            controller.testProp = testObj;
            controller.stateLabel = stateLabel;
            controller.connectButton = connectBtn;
            controller.disconnectButton = disconnectBtn;
            controller.sendButton = sendBtn;
        }

        // -----------------------------------------------------------
        // UI primitives
        // -----------------------------------------------------------

        private static Text CreateText(Transform parent, string name, string content,
                                       int fontSize, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, worldPositionStays: false);

            var text = go.AddComponent<Text>();
            text.text = content;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = new Color(0.9f, 0.92f, 0.95f);
            text.font = GetBuiltinFont();
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var le = go.AddComponent<LayoutElement>();
            le.minHeight = Mathf.Max(fontSize * 1.3f, fontSize + 4);

            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label)
        {
            var go = new GameObject(name,
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, worldPositionStays: false);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.18f, 0.38f, 0.78f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = image;

            var colors = btn.colors;
            colors.normalColor = new Color(0.18f, 0.38f, 0.78f);
            colors.highlightedColor = new Color(0.24f, 0.48f, 0.92f);
            colors.pressedColor = new Color(0.12f, 0.28f, 0.62f);
            colors.disabledColor = new Color(0.25f, 0.25f, 0.28f);
            btn.colors = colors;

            go.GetComponent<LayoutElement>().minHeight = 38;

            // Text child.
            var txtGO = new GameObject("Text", typeof(RectTransform));
            txtGO.transform.SetParent(go.transform, worldPositionStays: false);

            var txt = txtGO.AddComponent<Text>();
            txt.text = label;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;
            txt.fontSize = 15;
            txt.fontStyle = FontStyle.Bold;
            txt.font = GetBuiltinFont();

            var txtRect = txt.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.sizeDelta = Vector2.zero;
            txtRect.anchoredPosition = Vector2.zero;

            return btn;
        }

        /// <summary>
        /// Returns Unity's builtin runtime font. Name changed over versions —
        /// Unity 6 uses LegacyRuntime.ttf; older versions used Arial.ttf.
        /// </summary>
        private static Font GetBuiltinFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }
    }
}
#endif
