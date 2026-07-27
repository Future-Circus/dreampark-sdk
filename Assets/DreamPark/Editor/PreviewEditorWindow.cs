#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Preview Editor — opened by clicking a card in the Content Uploader's
    // "Park Assets" grid. Lets a creator orbit the camera, tune the zoom, and
    // freeze animated entities on a chosen clip and frame for a single
    // prefab, previewing the result LIVE through the exact same
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
    // — the live view, animation playback, and the on-Save bake — is deferred
    // to EditorApplication.delayCall, which fires after the OnGUI tick
    // returns.
    public class PreviewEditorWindow : EditorWindow
    {
        // Fired after a preview PNG has been regenerated on Save, with the
        // affected contentId. The Content Uploader panel listens so its grid
        // refreshes without the user hitting "Rebuild Previews".
        public static event Action<string> PreviewSaved;

        private const int kRenderResolution = 512;
        private const float kControlsWidth = 320f;
        private const float kOrbitDegPerPixel = 0.5f;
        private const float kZoomWheelSpeed = 0.06f;

        // Playback pacing. Each played frame costs a full two-pass render, so
        // the loop is capped rather than run at editor tick rate, and a slow
        // render makes playback slower instead of letting the clock run away
        // from what's actually on screen.
        private const double kPlaybackInterval = 1.0 / 30.0;
        private const double kPlaybackMaxStep = 0.1;

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
        [NonSerialized] private Vector2 _controlsScroll;

        // Animated entities discovered on the prefab, with a cached dropdown
        // option array per entity (index 0 is always "None").
        [NonSerialized] private List<PreviewAnimationSampler.AnimatedEntity> _entities;
        [NonSerialized] private List<string[]> _clipOptions;
        [NonSerialized] private HashSet<string> _expandedEntities;

        // Playback runs for one entity at a time — animating several at once
        // would multiply the per-frame render cost for no authoring benefit.
        [NonSerialized] private bool _isPlaying;
        [NonSerialized] private string _playingPath;
        [NonSerialized] private double _lastPlayTick;
        [NonSerialized] private bool _playTickHooked;

        // Animation-section edits are queued here and applied once the whole
        // GUI pass has been emitted, never inline. IMGUI hands out control
        // ids by position, so if a block emits a different number of controls
        // after a click than it did during the layout pass, every control
        // after it shifts and the click's value lands on a DIFFERENT entity's
        // control. Deferring keeps the emitted set identical for the whole
        // pass, which is what makes per-entity edits independent.
        [NonSerialized] private Action _pendingChange;

        public static void Open(string contentId, string assetPath, string prefabName, string subLabel)
        {
            var window = GetWindow<PreviewEditorWindow>(utility: false, title: "Preview Editor", focus: true);
            window.minSize = new Vector2(640f, 420f);
            window.Load(contentId, assetPath, prefabName, subLabel);
            window.Show();
        }

        private void Load(string contentId, string assetPath, string prefabName, string subLabel)
        {
            StopPlayback();
            _pendingChange = null;   // never let an edit queued for the previous prefab land on this one

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
            _controlsScroll = Vector2.zero;

            DiscoverAnimation();
            MarkDirty();
        }

        // Animated-entity discovery walks the whole prefab and hits the
        // AssetDatabase, so it happens once per opened prefab rather than on
        // every OnGUI tick.
        private void DiscoverAnimation()
        {
            _entities = PreviewAnimationSampler.Discover(_prefab);
            _clipOptions = new List<string[]>(_entities.Count);
            _expandedEntities = new HashSet<string>();

            for (int i = 0; i < _entities.Count; i++)
            {
                var entity = _entities[i];

                var options = new string[entity.clipLabels.Length + 1];
                options[0] = "None (bind pose)";
                Array.Copy(entity.clipLabels, 0, options, 1, entity.clipLabels.Length);
                _clipOptions.Add(options);

                // Open the entities a creator is most likely to want: anything
                // already posed, plus the first one when nothing is.
                if (_settings.TryGetPose(entity.path, out var pose) && pose.HasClip)
                    _expandedEntities.Add(entity.path);
            }

            if (_entities.Count > 0 && _expandedEntities.Count == 0)
                _expandedEntities.Add(_entities[0].path);
        }

        private void OnDisable()
        {
            StopPlayback();
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

            // A domain reload (script compile, play-mode toggle) wipes the
            // NonSerialized caches while the window itself survives.
            if (_entities == null) DiscoverAnimation();

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreviewArea();
                DrawControls();
            }

            // Every control has now been emitted, so mutating state can no
            // longer desynchronise this pass's control ids.
            if (_pendingChange != null)
            {
                var change = _pendingChange;
                _pendingChange = null;
                change();
                MarkDirty();
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
        // Scrollable above, fixed action row below — the animation section is
        // unbounded in height (one block per animated entity), so the Save
        // button can't live at the end of a growing list.
        private void DrawControls()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(kControlsWidth)))
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(_controlsScroll))
                {
                    _controlsScroll = scroll.scrollPosition;

                    GUILayout.Space(6);
                    GUILayout.Label(_prefabName, EditorStyles.boldLabel);
                    GUILayout.Label($"{_subLabel}  ·  {_contentId}", EditorStyles.miniLabel);

                    GUILayout.Space(2);
                    string overrideState = _hasStoredOverride
                        ? "Custom preview saved for this prefab."
                        : "Using default framing (no override saved).";
                    EditorGUILayout.LabelField(overrideState, EditorStyles.miniLabel);

                    DrawCameraControls();
                    DrawAnimationControls();

                    GUILayout.Space(6);
                    EditorGUILayout.HelpBox(
                        "Drag in the preview to orbit · scroll to zoom.",
                        MessageType.None);
                    GUILayout.Space(6);
                }

                DrawActionRow();
            }
        }

        private void DrawCameraControls()
        {
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
        }

        // ── Animation ───────────────────────────────────────────────────────
        private void DrawAnimationControls()
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);

            if (_entities.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No animated entities on this prefab.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField(
                _entities.Count == 1
                    ? "1 animated entity found."
                    : $"{_entities.Count} animated entities found.",
                EditorStyles.miniLabel);

            for (int i = 0; i < _entities.Count; i++)
                DrawEntityBlock(i);

            if (!_settings.HasAnyPose)
            {
                GUILayout.Space(2);
                if (GUILayout.Button(new GUIContent(
                        "Pose All · first clip @ 30%",
                        "Freeze every animated entity on the first clip it offers, 30% of the way in — " +
                        "a fast way off the bind pose before fine-tuning.")))
                {
                    _pendingChange = PoseAllWithFirstClip;
                }
            }

            DrawStalePoseWarning();
        }

        // Everything here is decided from state captured BEFORE the first
        // control is emitted, and every edit is queued rather than applied
        // inline — see the _pendingChange comment. Reading state that a
        // control just changed, or returning early once a button reports a
        // click, is what makes one entity's dropdown move another entity's.
        private void DrawEntityBlock(int index)
        {
            var entity = _entities[index];
            string[] options = _clipOptions[index];

            bool hasPose = _settings.TryGetPose(entity.path, out var pose) && pose.HasClip;
            int clipIndex = hasPose ? PreviewAnimationSampler.IndexOfClip(entity, pose) : -1;
            AnimationClip clip = clipIndex >= 0 ? entity.clips[clipIndex] : null;
            bool clipMissing = hasPose && clip == null;
            bool expanded = _expandedEntities.Contains(entity.path);

            string summary = clip != null
                ? clip.name
                : (clipMissing ? $"{pose.clipName} (missing)" : "none");

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool nowExpanded = EditorGUILayout.Foldout(
                    expanded,
                    new GUIContent($"{entity.label}  ·  {summary}",
                        $"{entity.pathLabel}\nClips from: {entity.sourceLabel}"),
                    true);

                if (nowExpanded != expanded)
                {
                    string key = entity.path;
                    _pendingChange = () =>
                    {
                        if (nowExpanded) _expandedEntities.Add(key);
                        else _expandedEntities.Remove(key);
                    };
                }

                // Uses the snapshot, not nowExpanded: the control count for
                // this pass must not depend on a click made during it.
                if (!expanded) return;

                EditorGUILayout.LabelField(
                    $"{entity.pathLabel}  ·  {entity.sourceLabel}", EditorStyles.miniLabel);

                int popupIndex = clipIndex >= 0 ? clipIndex + 1 : 0;
                int newPopupIndex = EditorGUILayout.Popup(
                    new GUIContent("Clip", "Which animation to freeze this entity on for the preview."),
                    popupIndex, options);

                if (newPopupIndex != popupIndex)
                {
                    var captured = pose;
                    bool capturedHadPose = hasPose;
                    _pendingChange = () => ApplyClipSelection(entity, captured, capturedHadPose, newPopupIndex);
                }

                if (clipMissing)
                {
                    EditorGUILayout.HelpBox(
                        $"The saved clip '{pose.clipName}' is no longer reachable from this prefab. " +
                        "Pick another clip, or set it to None to render this entity in its bind pose.",
                        MessageType.Warning);
                }
                else if (clip != null)
                {
                    DrawTimeline(entity, pose, clip);
                }
            }
        }

        private void DrawTimeline(PreviewAnimationSampler.AnimatedEntity entity, AnimationPose pose, AnimationClip clip)
        {
            int frames = PreviewAnimationSampler.FrameCountOf(clip);
            int positions = frames + 1;                      // timeline runs 0..frames inclusive
            int frame = PreviewAnimationSampler.FrameFromNormalized(clip, pose.normalizedTime);
            float fps = PreviewAnimationSampler.FrameRateOf(clip);

            int nextFrame = frame;

            EditorGUI.BeginChangeCheck();
            int sliderFrame = EditorGUILayout.IntSlider(
                new GUIContent("Frame",
                    $"{clip.name} · {positions} frames · {clip.length:0.##}s @ {fps:0.##} fps"),
                frame, 0, frames);
            if (EditorGUI.EndChangeCheck()) nextFrame = sliderFrame;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("◀", "Previous frame"),
                        EditorStyles.miniButtonLeft, GUILayout.Width(26f)))
                    nextFrame = Wrap(frame - 1, positions);

                if (GUILayout.Button(new GUIContent("▶", "Next frame"),
                        EditorStyles.miniButtonRight, GUILayout.Width(26f)))
                    nextFrame = Wrap(frame + 1, positions);

                GUILayout.Space(6);

                bool playingThis = _isPlaying && _playingPath == entity.path;
                bool wantsPlay = GUILayout.Toggle(
                    playingThis,
                    new GUIContent(playingThis ? "Pause" : "Play",
                        "Play the clip in the live preview. Pause on the frame you want, then Save."),
                    EditorStyles.miniButton, GUILayout.Width(56f));

                if (wantsPlay != playingThis)
                {
                    string key = entity.path;
                    _pendingChange = () =>
                    {
                        if (wantsPlay) StartPlayback(key);
                        else StopPlayback();
                    };
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{pose.normalizedTime * clip.length:0.00}s", EditorStyles.miniLabel);
            }

            if (nextFrame != frame)
            {
                string key = entity.path;
                var captured = pose;
                _pendingChange = () =>
                {
                    // Scrubbing by hand overrides playback rather than
                    // fighting it for the playhead.
                    if (_isPlaying && _playingPath == key) StopPlayback();
                    SetFrame(key, captured, clip, nextFrame);
                };
            }
        }

        private static int Wrap(int value, int count)
        {
            if (count <= 0) return 0;
            return ((value % count) + count) % count;
        }

        private void ApplyClipSelection(
            PreviewAnimationSampler.AnimatedEntity entity, AnimationPose pose, bool hadPose, int popupIndex)
        {
            if (popupIndex <= 0)
            {
                if (_playingPath == entity.path) StopPlayback();
                _settings.ClearPose(entity.path);
                MarkDirty();
                return;
            }

            var chosen = entity.clips[popupIndex - 1];
            var updated = hadPose ? pose : default;
            updated.targetPath = entity.path;
            PreviewAnimationSampler.FillClipRef(ref updated, chosen);

            // A brand-new selection lands on the suggested frame rather than
            // frame 0, which for most loops is the least interesting pose in
            // the clip. Switching clips on an entity that already had one
            // keeps the creator's playhead position.
            if (!hadPose) updated.normalizedTime = PreviewSettings.SuggestedNormalizedTime;

            _settings.SetPose(updated);
            _expandedEntities.Add(entity.path);
            MarkDirty();
        }

        private void SetFrame(string targetPath, AnimationPose pose, AnimationClip clip, int frame)
        {
            pose.targetPath = targetPath;
            pose.normalizedTime = PreviewAnimationSampler.NormalizedFromFrame(clip, frame);
            _settings.SetPose(pose);
            MarkDirty();
        }

        private void PoseAllWithFirstClip()
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                var entity = _entities[i];
                if (entity.clips.Count == 0) continue;

                var pose = new AnimationPose
                {
                    targetPath = entity.path,
                    normalizedTime = PreviewSettings.SuggestedNormalizedTime,
                };
                PreviewAnimationSampler.FillClipRef(ref pose, entity.clips[0]);
                _settings.SetPose(pose);
                _expandedEntities.Add(entity.path);
            }
            MarkDirty();
        }

        // Poses saved against a hierarchy path this prefab no longer has —
        // it was restructured, or a child got renamed, since the last Save.
        // They're harmless (the renderer skips them with a warning) but they
        // would silently rot, so offer a one-click cleanup.
        private void DrawStalePoseWarning()
        {
            if (_settings.animationPoses == null) return;

            var stalePaths = new List<string>();
            var staleLabels = new List<string>();
            for (int i = 0; i < _settings.animationPoses.Count; i++)
            {
                var pose = _settings.animationPoses[i];
                if (!pose.HasClip) continue;

                bool known = false;
                for (int j = 0; j < _entities.Count; j++)
                {
                    if (_entities[j].path == pose.targetPath) { known = true; break; }
                }
                if (known) continue;

                stalePaths.Add(pose.targetPath);
                staleLabels.Add(string.IsNullOrEmpty(pose.targetPath) ? "(root)" : pose.targetPath);
            }

            if (stalePaths.Count == 0) return;

            GUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"{stalePaths.Count} saved pose(s) point at objects this prefab no longer has " +
                $"({string.Join(", ", staleLabels)}). They're ignored when rendering.",
                MessageType.Warning);

            if (GUILayout.Button("Remove Stale Poses"))
            {
                _pendingChange = () =>
                {
                    for (int i = 0; i < stalePaths.Count; i++) _settings.ClearPose(stalePaths[i]);
                };
            }
        }

        // ── Playback ────────────────────────────────────────────────────────
        private void StartPlayback(string targetPath)
        {
            _isPlaying = true;
            _playingPath = targetPath;
            _lastPlayTick = EditorApplication.timeSinceStartup;

            if (!_playTickHooked)
            {
                EditorApplication.update += OnPlaybackTick;
                _playTickHooked = true;
            }
        }

        private void StopPlayback()
        {
            _isPlaying = false;
            _playingPath = null;

            if (_playTickHooked)
            {
                EditorApplication.update -= OnPlaybackTick;
                _playTickHooked = false;
            }
        }

        private void OnPlaybackTick()
        {
            if (!_isPlaying || _prefab == null) { StopPlayback(); return; }

            // Never queue a frame on top of one that hasn't been drawn yet —
            // and don't advance the clock either, so a heavy prefab plays
            // slower rather than skipping the frames it can't keep up with.
            if (_needsRender || _renderScheduled) return;

            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - _lastPlayTick;
            if (elapsed < kPlaybackInterval) return;
            _lastPlayTick = now;

            if (!_settings.TryGetPose(_playingPath, out var pose) || !pose.HasClip)
            {
                StopPlayback();
                Repaint();
                return;
            }

            var clip = PreviewAnimationSampler.ResolveClip(pose);
            if (clip == null || clip.length <= 0f)
            {
                StopPlayback();
                Repaint();
                return;
            }

            float step = (float)Math.Min(elapsed, kPlaybackMaxStep);
            pose.normalizedTime = Mathf.Repeat(pose.normalizedTime + step / clip.length, 1f);
            _settings.SetPose(pose);
            MarkDirty();
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
                // A render that throws mid-playback would otherwise throw once
                // per frame, forever.
                StopPlayback();
            }

            Repaint();
        }

        // ── Save / remove ───────────────────────────────────────────────────
        private void DrawActionRow()
        {
            using (new EditorGUI.DisabledScope(_settings.IsDefault))
            {
                if (GUILayout.Button(new GUIContent(
                        "Reset to Defaults",
                        "Back to the default 45°/30° framing with no animation poses.")))
                {
                    StopPlayback();
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

        private void Save()
        {
            // Freeze the playhead first — what's on screen is what gets baked.
            StopPlayback();

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
            StopPlayback();

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
