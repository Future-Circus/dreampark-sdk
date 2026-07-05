#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Preview Editor — opened by clicking a card in the Content Uploader's
    // "Park Assets" grid. Lets a creator orbit the camera and tune the zoom
    // for a single prefab, previewing the result LIVE through the exact same
    // PrefabPreviewRenderer that bakes the shipped PNG (WYSIWYG), then Save
    // to (a) persist the choice as per-prefab metadata and (b) regenerate
    // Previews/{name}.png so the grid and every future batch reflect it.
    //
    // It does NOT introduce a second preview generator — it only feeds
    // PreviewSettings into the existing one.
    //
    // Threading / URP note: cam.Render() must never run inside OnGUI (it
    // nests one render pass inside the editor window's own pass and throws
    // "EndRenderPass: Not inside a Renderpass" under URP). Every render here
    // — the live view and the on-Save bake — is deferred to
    // EditorApplication.delayCall, which fires after the OnGUI tick returns.
    public class PreviewEditorWindow : EditorWindow
    {
        // Fired after a preview PNG has been regenerated on Save, with the
        // affected contentId. The Content Uploader panel listens so its grid
        // refreshes without the user hitting "Rebuild Previews".
        public static event Action<string> PreviewSaved;

        private const int kRenderResolution = 512;
        private const float kControlsWidth = 288f;
        private const float kOrbitDegPerPixel = 0.5f;
        private const float kZoomWheelSpeed = 0.06f;

        private string _contentId;
        private string _assetPath;
        private string _prefabName;
        private string _subLabel;

        private GameObject _prefab;
        private PreviewSettings _settings = PreviewSettings.Default;
        private bool _hasStoredOverride;

        [NonSerialized] private Texture2D _preview;
        [NonSerialized] private bool _needsRender;
        [NonSerialized] private bool _renderScheduled;
        [NonSerialized] private bool _renderedEmpty;   // render ran but produced no geometry
        [NonSerialized] private bool _isDragging;
        [NonSerialized] private string _statusMessage;

        public static void Open(string contentId, string assetPath, string prefabName, string subLabel)
        {
            var window = GetWindow<PreviewEditorWindow>(utility: false, title: "Preview Editor", focus: true);
            window.minSize = new Vector2(560f, 360f);
            window.Load(contentId, assetPath, prefabName, subLabel);
            window.Show();
        }

        private void Load(string contentId, string assetPath, string prefabName, string subLabel)
        {
            _contentId = contentId;
            _assetPath = assetPath;
            _prefabName = prefabName;
            _subLabel = subLabel;

            _prefab = string.IsNullOrEmpty(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

            _hasStoredOverride = PreviewMetadataStore.TryGet(contentId, prefabName, out var stored);
            _settings = stored;
            _statusMessage = null;

            MarkDirty();
        }

        private void OnDisable()
        {
            DestroyPreviewTexture();
        }

        private void MarkDirty()
        {
            _needsRender = true;
            Repaint();
        }

        private void OnGUI()
        {
            if (_prefab == null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "No prefab loaded. Open the Preview Editor by clicking an item in the " +
                    "Content Uploader's Park Assets grid.",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreviewArea();
                DrawControls();
            }

            // Schedule the deferred (off-OnGUI) render if anything changed.
            if (_needsRender && !_renderScheduled)
            {
                _renderScheduled = true;
                EditorApplication.delayCall += DoDeferredRender;
            }
        }

        // ── Left: live preview + orbit/zoom input ───────────────────────────
        private void DrawPreviewArea()
        {
            Rect area = GUILayoutUtility.GetRect(
                200f, 200f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            // Square, centered viewport inside the available area.
            float side = Mathf.Min(area.width, area.height);
            Rect view = new Rect(
                area.x + (area.width - side) * 0.5f,
                area.y + (area.height - side) * 0.5f,
                side, side);

            EditorGUI.DrawRect(view, new Color(0.16f, 0.16f, 0.18f, 1f));

            if (_preview != null)
            {
                GUI.DrawTexture(view, _preview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                    wordWrap = true,
                };
                string msg = _renderedEmpty
                    ? "This prefab has no renderable geometry to preview."
                    : "Rendering…";
                GUI.Label(view, msg, style);
            }

            // Thin frame.
            DrawBorder(view, new Color(0f, 0f, 0f, 0.5f));

            HandleViewportInput(view);
        }

        private void HandleViewportInput(Rect view)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && view.Contains(e.mousePosition))
                    {
                        _isDragging = true;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDragging)
                    {
                        _settings.azimuth = Mathf.Repeat(_settings.azimuth + e.delta.x * kOrbitDegPerPixel, 360f);
                        _settings.elevation = Mathf.Clamp(
                            _settings.elevation - e.delta.y * kOrbitDegPerPixel,
                            PreviewSettings.MinElevation, PreviewSettings.MaxElevation);
                        MarkDirty();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isDragging)
                    {
                        _isDragging = false;
                        e.Use();
                    }
                    break;

                case EventType.ScrollWheel:
                    if (view.Contains(e.mousePosition))
                    {
                        // Wheel up (delta.y < 0) zooms in.
                        _settings.zoom = Mathf.Clamp(
                            _settings.zoom * Mathf.Exp(-e.delta.y * kZoomWheelSpeed),
                            PreviewSettings.MinZoom, PreviewSettings.MaxZoom);
                        MarkDirty();
                        e.Use();
                    }
                    break;
            }
        }

        // ── Right: controls ─────────────────────────────────────────────────
        private void DrawControls()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(kControlsWidth)))
            {
                GUILayout.Space(6);
                GUILayout.Label(_prefabName, EditorStyles.boldLabel);
                GUILayout.Label($"{_subLabel}  ·  {_contentId}", EditorStyles.miniLabel);

                GUILayout.Space(2);
                string overrideState = _hasStoredOverride
                    ? "Custom preview saved for this prefab."
                    : "Using default framing (no override saved).";
                EditorGUILayout.LabelField(overrideState, EditorStyles.miniLabel);

                GUILayout.Space(8);
                EditorGUILayout.LabelField("Camera", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();

                float azimuth = EditorGUILayout.Slider(
                    new GUIContent("Azimuth", "Orbit the camera left/right around the prefab (degrees)."),
                    _settings.azimuth, 0f, 360f);

                float elevation = EditorGUILayout.Slider(
                    new GUIContent("Elevation", "Tilt the camera up/down (degrees)."),
                    _settings.elevation, PreviewSettings.MinElevation, PreviewSettings.MaxElevation);

                GUILayout.Space(4);
                EditorGUILayout.LabelField("Framing", EditorStyles.boldLabel);

                float zoom = EditorGUILayout.Slider(
                    new GUIContent("Zoom", "How much of the frame the subject fills. 1 = default auto-fit."),
                    _settings.zoom, PreviewSettings.MinZoom, PreviewSettings.MaxZoom);

                GUILayout.Space(4);
                EditorGUILayout.LabelField("Layers", EditorStyles.boldLabel);

                bool hideGizmo = EditorGUILayout.Toggle(
                    new GUIContent("Hide Gizmo Layer",
                        $"Cull objects on the '{PreviewSettings.GizmoLayerName}' layer from the preview, " +
                        "so in-prefab helper/gizmo objects don't show up in the generated image."),
                    _settings.hideGizmoLayer);

                if (EditorGUI.EndChangeCheck())
                {
                    _settings.azimuth = azimuth;
                    _settings.elevation = elevation;
                    _settings.zoom = zoom;
                    _settings.hideGizmoLayer = hideGizmo;
                    MarkDirty();
                }

                GUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    "Drag in the preview to orbit · scroll to zoom.",
                    MessageType.None);

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_settings.IsDefault))
                {
                    if (GUILayout.Button("Reset to Default Angle"))
                    {
                        _settings = PreviewSettings.Default;
                        MarkDirty();
                    }
                }

                using (new EditorGUI.DisabledScope(!_hasStoredOverride))
                {
                    if (GUILayout.Button("Remove Saved Override"))
                    {
                        RemoveOverride();
                    }
                }

                GUILayout.Space(4);
                var saveStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                using (new EditorGUI.DisabledScope(_renderedEmpty))
                {
                    if (GUILayout.Button("Save Preview", saveStyle, GUILayout.Height(28)))
                    {
                        Save();
                    }
                }

                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField(_statusMessage, EditorStyles.miniLabel);
                }

                GUILayout.Space(6);
            }
        }

        // ── Deferred rendering (off the OnGUI stack) ────────────────────────
        private void DoDeferredRender()
        {
            _renderScheduled = false;
            if (this == null) return;      // window closed between schedule and fire
            if (!_needsRender) return;
            _needsRender = false;

            if (_prefab == null) return;

            try
            {
                Texture2D fresh = PrefabPreviewRenderer.RenderPreview(_prefab, _settings, kRenderResolution);
                DestroyPreviewTexture();
                _preview = fresh;
                _renderedEmpty = fresh == null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PreviewEditor] Render failed for {_prefabName}: {ex.Message}\n{ex.StackTrace}");
                _renderedEmpty = _preview == null;
            }

            Repaint();
        }

        // ── Save / remove ───────────────────────────────────────────────────
        private void Save()
        {
            var sanitized = _settings.Sanitized();
            _settings = sanitized;

            // Storing a value identical to the default would only add noise to
            // the metadata file (and mark the prefab "overridden" for no
            // reason), so a default-valued Save clears the override instead.
            if (sanitized.IsDefault)
            {
                PreviewMetadataStore.Clear(_contentId, _prefabName);
                _hasStoredOverride = false;
            }
            else
            {
                PreviewMetadataStore.Set(_contentId, _prefabName, sanitized);
                _hasStoredOverride = true;
            }

            string contentId = _contentId;
            string assetPath = _assetPath;
            _statusMessage = "Saving preview…";

            // Bake the PNG off the OnGUI stack (URP render-pass safety).
            EditorApplication.delayCall += () =>
            {
                bool ok = ContentProcessor.RegeneratePreviewForPrefab(contentId, assetPath);
                _statusMessage = ok
                    ? $"Saved · {DateTime.Now:HH:mm:ss}"
                    : "Save failed — see Console.";
                if (ok)
                {
                    PreviewSaved?.Invoke(contentId);
                    ShowNotification(new GUIContent("Preview saved"));
                }
                Repaint();
            };
        }

        private void RemoveOverride()
        {
            PreviewMetadataStore.Clear(_contentId, _prefabName);
            _hasStoredOverride = false;
            _settings = PreviewSettings.Default;

            string contentId = _contentId;
            string assetPath = _assetPath;
            _statusMessage = "Reverting to default…";

            EditorApplication.delayCall += () =>
            {
                bool ok = ContentProcessor.RegeneratePreviewForPrefab(contentId, assetPath);
                _statusMessage = ok ? "Reverted to default framing." : "Revert failed — see Console.";
                if (ok) PreviewSaved?.Invoke(contentId);
                Repaint();
            };

            MarkDirty();
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        private void DestroyPreviewTexture()
        {
            if (_preview != null)
            {
                DestroyImmediate(_preview);
                _preview = null;
            }
        }

        private static void DrawBorder(Rect r, Color c)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }
    }
}
#endif
