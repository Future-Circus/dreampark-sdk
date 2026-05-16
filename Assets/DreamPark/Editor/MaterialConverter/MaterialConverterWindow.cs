#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.MaterialConversion
{
    /// <summary>
    /// Material Converter review window — mirrors TextureOptimizerWindow.
    ///
    /// Workflow:
    ///   1. Open via DreamPark → Material Converter... (or the Pre-Launch
    ///      section of the Content Uploader).
    ///   2. Click "Scan" — builds MaterialUsageGraph, runs the planner.
    ///   3. The table groups materials by kind:
    ///        - Convert → Universal
    ///        - Convert → Unlit
    ///        - Convert → Particles
    ///        - Exotic particle (skipped)
    ///        - Embedded in model (read-only, hint to extract)
    ///        - Orphan (not referenced from any prefab/scene)
    ///        - Already converted
    ///   4. Per-row checkbox + Ping. The kind dropdown lets the user
    ///      flip Universal ↔ Unlit when the planner's auto-route was wrong.
    ///   5. "Apply Selected" runs the Executor.
    /// </summary>
    public class MaterialConverterWindow : EditorWindow
    {
        [MenuItem("DreamPark/Material Converter...", false, 119)]
        public static void Open()
        {
            var w = GetWindow<MaterialConverterWindow>("Material Converter");
            w.minSize = new Vector2(960, 560);
            w.Show();
        }

        // ─── State ──────────────────────────────────────────────────────
        private string _rootFolder = "Assets/Content";
        private List<MaterialPlanRow> _rows = new List<MaterialPlanRow>();
        private Vector2 _scroll;
        private string _search = "";
        // Default to UsageCountDesc: the most-referenced materials are the
        // riskiest to convert wrong (they're on the most prefabs), so they
        // should bubble to the top of the review list.
        private SortMode _sort = SortMode.UsageCountDesc;
        private bool _showAlreadyConverted = false;
        private bool _showOrphans = true;
        private bool _showExotic = true;
        private bool _showEmbedded = true;
        private DateTime _lastScanUtc = DateTime.MinValue;

        // Preview-generation strategy:
        //
        // Calling AssetPreview.GetAssetPreview() inside OnGUI on every paint
        // for every visible row causes a fight with Unity's preview cache.
        // AssetPreview returns references to textures it OWNS, and sometimes
        // returns null briefly even for previews it just generated (the
        // queue is asynchronous and not transactionally safe). If we Repaint
        // every time we see a null, we're racing the cache and producing
        // visible flicker on already-loaded thumbnails.
        //
        // Fix: ask AssetPreview AT MOST ONCE per material per session
        // (stored as row.cachedPreview, which is non-null forever after the
        // first hit). To drive the initial preview generation without
        // entering a paint loop, we use EditorApplication.update — a
        // delegate Unity calls on its own ~10x/sec, completely independent
        // of GUI repaints. While any row is still missing a preview, that
        // delegate calls Repaint() at a measured cadence and gives up after
        // a few seconds. Once a row HAS a preview, OnGUI just draws the
        // cached reference and never re-queries.
        private double _previewPumpDeadline;
        private const double kPreviewPumpDuration = 8.0;   // give Unity 8s to settle every preview after a scan
        private const double kPreviewPumpInterval = 0.4;   // 2.5 Hz pump — slow enough to never look like flicker
        private double _lastPreviewPumpTime;

        private enum SortMode { UsageCountDesc, PathAsc, ShaderAsc, KindAsc }

        // ─── Lifecycle ──────────────────────────────────────────────────
        private void OnEnable()
        {
            _rootFolder = EditorPrefs.GetString("DreamPark.MaterialConverter.Root", _rootFolder);
            if (!AssetDatabase.IsValidFolder(_rootFolder))
                _rootFolder = AutoPickContentFolder();

            // Bump Unity's global asset-preview cache way up so material
            // preview spheres don't thrash in/out of view as the user
            // scrolls. Default cache is ~32 entries — far too small for a
            // table of 100+ materials. SetPreviewTextureCacheSize has a
            // floor of 32 and is sticky across the editor session, so
            // calling it once here is enough.
            AssetPreview.SetPreviewTextureCacheSize(1024);

            // Drive periodic Repaint() OUTSIDE of OnGUI so we never start
            // a paint-inside-paint loop. EditorApplication.update fires
            // ~10x/sec independent of GUI activity; we self-throttle to
            // ~2.5 Hz and stop pumping entirely once kPreviewPumpDuration
            // elapses past the last scan. See _previewPumpDeadline.
            EditorApplication.update += PumpPreviews;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PumpPreviews;
        }

        // Called by EditorApplication.update. Asks Unity to repaint THIS
        // window if (a) we're still inside the post-scan settle window and
        // (b) the throttle interval has elapsed. The repaint causes OnGUI
        // to run, which queries AssetPreview for any rows that don't yet
        // have a cachedPreview — that's how previews populate over time
        // without OnGUI itself triggering Repaint().
        private void PumpPreviews()
        {
            if (EditorApplication.timeSinceStartup > _previewPumpDeadline) return;
            if (EditorApplication.timeSinceStartup - _lastPreviewPumpTime < kPreviewPumpInterval) return;
            _lastPreviewPumpTime = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private static string AutoPickContentFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Content")) return "Assets/Content";
            foreach (var sub in AssetDatabase.GetSubFolders("Assets/Content"))
            {
                if (!sub.EndsWith("YOUR_GAME_HERE", StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
            return "Assets/Content";
        }

        // ─── OnGUI ──────────────────────────────────────────────────────
        // Strictly no Repaint() calls inside OnGUI for previews. All
        // repaints needed to drive AssetPreview generation come from
        // PumpPreviews via EditorApplication.update.
        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(4);
            DrawControls();
            EditorGUILayout.Space(4);
            DrawSummaryBar();
            EditorGUILayout.Space(4);
            DrawTable();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Material Converter", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Scans every material in this park's content folder and converts vendor / URP / "
                    + "Standard materials to DreamPark-Universal (lit), DreamPark-Unlit (flat), or "
                    + "DreamPark/Particles (VFX). Per-row review before any material is touched; "
                    + "GUIDs preserved so prefab references stay intact.",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Content folder:", GUILayout.Width(100));
                    string newRoot = EditorGUILayout.TextField(_rootFolder);
                    if (newRoot != _rootFolder)
                    {
                        _rootFolder = newRoot;
                        EditorPrefs.SetString("DreamPark.MaterialConverter.Root", _rootFolder);
                    }
                    if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                    {
                        string abs = EditorUtility.OpenFolderPanel("Pick content folder", _rootFolder, "");
                        if (!string.IsNullOrEmpty(abs))
                        {
                            string rel = AbsToAssetsRel(abs);
                            if (!string.IsNullOrEmpty(rel))
                            {
                                _rootFolder = rel;
                                EditorPrefs.SetString("DreamPark.MaterialConverter.Root", _rootFolder);
                            }
                        }
                    }
                }
            }
        }

        private static string AbsToAssetsRel(string abs)
        {
            string dataPath = Application.dataPath;
            abs = abs.Replace('\\', '/');
            if (abs.StartsWith(dataPath, StringComparison.Ordinal))
                return "Assets" + abs.Substring(dataPath.Length);
            return null;
        }

        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶  Scan", GUILayout.Width(120), GUILayout.Height(26)))
                    RunScan();

                GUI.enabled = _rows.Any(r => r.WillBeModified);
                if (GUILayout.Button("Apply Selected", GUILayout.Width(140), GUILayout.Height(26)))
                    RunApply();
                GUI.enabled = true;

                GUI.enabled = _rows.Count > 0;
                if (GUILayout.Button("✓ Check All", GUILayout.Width(100), GUILayout.Height(26)))
                    SetApproved(true, convertibleOnly: true);
                if (GUILayout.Button("✗ Uncheck All", GUILayout.Width(110), GUILayout.Height(26)))
                    SetApproved(false, convertibleOnly: false);
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("Sort:", GUILayout.Width(40));
                _sort = (SortMode)EditorGUILayout.EnumPopup(_sort, GUILayout.Width(120));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                _search = EditorGUILayout.TextField(_search);
                if (!string.IsNullOrEmpty(_search) && GUILayout.Button("✕", GUILayout.Width(24)))
                    _search = "";
                GUILayout.Space(12);
                _showAlreadyConverted = GUILayout.Toggle(_showAlreadyConverted, "Show already converted", GUILayout.Width(170));
                _showOrphans = GUILayout.Toggle(_showOrphans, "Orphans", GUILayout.Width(80));
                _showExotic  = GUILayout.Toggle(_showExotic,  "Exotic particles", GUILayout.Width(130));
                _showEmbedded= GUILayout.Toggle(_showEmbedded,"Embedded", GUILayout.Width(90));
            }

            // ── Readiness bulk-select row ────────────────────────────────
            // Three color-coded toggle buttons that flip approval for ALL
            // convertible rows of a given readiness state. Lets the user
            // say "tick every clean conversion" without scrolling, or
            // "include the orange ones too if I'm feeling brave", without
            // ever accidentally arming a red row.
            //
            // Behavior: clicking the button toggles approval for that
            // group — if any row in the group is currently unchecked,
            // checking them all; if every row in the group is already
            // checked, unchecking them all. Standard "select-all" pattern,
            // scoped to a color bucket.
            //
            // The button color reflects what gets selected, NOT the
            // current toggle state, so the visual is stable across clicks.
            if (_rows.Count > 0)
            {
                int readyCount       = CountByReadiness(ConversionReadiness.Ready);
                int approxCount      = CountByReadiness(ConversionReadiness.ReadyWithApproximations);
                int diffCount        = CountByReadiness(ConversionReadiness.WillLookDifferent);
                int blockedCount     = CountByReadiness(ConversionReadiness.Blocked);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Bulk select:", EditorStyles.miniBoldLabel, GUILayout.Width(80));

                    DrawReadinessButton(
                        $"✓ Ready ({readyCount})",
                        new Color(0.45f, 0.78f, 0.45f),
                        ConversionReadiness.Ready,
                        readyCount);

                    DrawReadinessButton(
                        $"✓ Ready (lit approx) ({approxCount})",
                        new Color(0.45f, 0.65f, 0.95f),
                        ConversionReadiness.ReadyWithApproximations,
                        approxCount);

                    DrawReadinessButton(
                        $"⚠ Will look different ({diffCount})",
                        new Color(0.95f, 0.65f, 0.30f),
                        ConversionReadiness.WillLookDifferent,
                        diffCount);

                    DrawReadinessButton(
                        $"⛔ Blocked ({blockedCount})",
                        new Color(0.85f, 0.30f, 0.30f),
                        ConversionReadiness.Blocked,
                        blockedCount);

                    GUILayout.FlexibleSpace();
                }
            }
        }

        // Counts rows whose effective readiness matches `r`. "Effective"
        // means: particle rows use particleDiff.Readiness; convertible
        // opaque rows are treated as Ready (no particle-specific issues
        // apply to them); hard-skipped rows count as nothing.
        private int CountByReadiness(ConversionReadiness r)
        {
            int n = 0;
            foreach (var row in _rows)
            {
                if (GetRowReadiness(row) == r) n++;
            }
            return n;
        }

        // Returns the readiness state a given row should map to in the
        // bulk-select buttons, or null if the row is hard-skipped /
        // non-convertible and shouldn't be affected.
        private static ConversionReadiness? GetRowReadiness(MaterialPlanRow row)
        {
            if (row.hardSkip) return null;
            if (row.particleDiff != null) return row.particleDiff.Readiness;
            if (row.kind == MaterialConvertKind.ConvertOpaqueToUniversal
             || row.kind == MaterialConvertKind.ConvertOpaqueToUnlit
             || row.kind == MaterialConvertKind.ConvertParticle)
                return ConversionReadiness.Ready;
            return null;
        }

        // Render one of the readiness bulk-select buttons. Toggling
        // semantics: if any row in this group is unchecked, the click
        // checks them all; otherwise (all already checked) it unchecks
        // them all. Disabled when the group is empty.
        private void DrawReadinessButton(string label, Color tint, ConversionReadiness target, int groupCount)
        {
            using (new EditorGUI.DisabledScope(groupCount == 0))
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = tint;
                if (GUILayout.Button(label, GUILayout.Height(22)))
                {
                    // Are any rows in this group currently unchecked?
                    bool anyUnchecked = false;
                    foreach (var row in _rows)
                    {
                        if (GetRowReadiness(row) != target) continue;
                        if (!row.approved) { anyUnchecked = true; break; }
                    }
                    // Toggle: set them ALL to the inverse of the current
                    // majority state. Standard select-all behavior.
                    bool newState = anyUnchecked;
                    foreach (var row in _rows)
                    {
                        if (GetRowReadiness(row) != target) continue;
                        row.approved = newState;
                    }
                }
                GUI.backgroundColor = prev;
            }
        }

        private void DrawSummaryBar()
        {
            if (_rows.Count == 0)
            {
                EditorGUILayout.LabelField(_lastScanUtc == DateTime.MinValue
                    ? "Click Scan to begin."
                    : "No materials found.",
                    EditorStyles.miniLabel);
                return;
            }

            int total = _rows.Count;
            int university = _rows.Count(r => r.kind == MaterialConvertKind.ConvertOpaqueToUniversal);
            int unlit      = _rows.Count(r => r.kind == MaterialConvertKind.ConvertOpaqueToUnlit);
            int particle   = _rows.Count(r => r.kind == MaterialConvertKind.ConvertParticle);
            int exotic     = _rows.Count(r => r.kind == MaterialConvertKind.ExoticParticle);
            int embedded   = _rows.Count(r => r.kind == MaterialConvertKind.ReadOnlyEmbedded);
            int orphan     = _rows.Count(r => r.kind == MaterialConvertKind.Orphan);
            int done       = _rows.Count(r => r.kind == MaterialConvertKind.AlreadyConverted);
            int approvedCount = _rows.Count(r => r.WillBeModified);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"Materials: {total}    Selected to convert: {approvedCount}",
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"→ Universal: {university}   → Unlit: {unlit}   → Particles: {particle}   " +
                    $"Exotic: {exotic}   Embedded: {embedded}   Orphan: {orphan}   Already DP: {done}");
            }
        }

        private void DrawTable()
        {
            var filtered = FilteredRows();
            if (filtered.Count == 0)
            {
                EditorGUILayout.LabelField("No rows match the current filters.", EditorStyles.miniLabel);
                return;
            }

            DrawTableHeader(filtered);
            // alwaysShowVertical: the scrollbar reserves its ~13px on the
            // right edge regardless of content height. Without this, the
            // row contents shift left by 13px the moment the table grows
            // tall enough to scroll — and the header doesn't shift, so
            // every column becomes misaligned. Forcing the scrollbar to
            // always render keeps header + rows pinned to the same widths.
            _scroll = EditorGUILayout.BeginScrollView(_scroll, alwaysShowHorizontal: false, alwaysShowVertical: true);
            foreach (var row in filtered)
                DrawRow(row);
            EditorGUILayout.EndScrollView();
        }

        // Header toolbar mirrors TextureOptimizerWindow.DrawTableHeader. The
        // top-left checkbox toggles approval for every currently-VISIBLE row
        // that isn't hard-skipped, so the user can filter to a subset and
        // tick-all just that subset.
        private void DrawTableHeader(List<MaterialPlanRow> visibleRows)
        {
            int eligible = 0, approved = 0;
            foreach (var r in visibleRows)
            {
                if (r.hardSkip) continue;
                eligible++;
                if (r.approved) approved++;
            }
            bool headerChecked = eligible > 0 && approved == eligible;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(eligible == 0))
                {
                    bool nextChecked = EditorGUILayout.Toggle(headerChecked, GUILayout.Width(20));
                    if (nextChecked != headerChecked)
                    {
                        foreach (var r in visibleRows)
                            if (!r.hardSkip) r.approved = nextChecked;
                    }
                }
                GUILayout.Label("", GUILayout.Width(44));                           // thumb column
                GUILayout.Label("Material",     EditorStyles.miniLabel, GUILayout.MinWidth(260));
                GUILayout.Label("Current",      EditorStyles.miniLabel, GUILayout.Width(220));
                GUILayout.Label("→",            EditorStyles.miniLabel, GUILayout.Width(18));
                GUILayout.Label("Target",       EditorStyles.miniLabel, GUILayout.Width(150));
                GUILayout.Label("Status",        EditorStyles.miniLabel, GUILayout.Width(160));
                GUILayout.Label("Notes",        EditorStyles.miniLabel, GUILayout.MinWidth(160));
            }
        }

        // Single source of truth for the target-shader dropdown. Keeps the
        // UI consistent across opaque and particle rows — same widget, same
        // three options, no "particles get a different label" surprise.
        private static readonly string[] TargetDropdownOptions = new[]
        {
            "→ Universal",
            "→ Unlit",
            "→ Particles",
        };

        private static int KindToTargetIndex(MaterialConvertKind k)
        {
            switch (k)
            {
                case MaterialConvertKind.ConvertOpaqueToUniversal: return 0;
                case MaterialConvertKind.ConvertOpaqueToUnlit:     return 1;
                case MaterialConvertKind.ConvertParticle:          return 2;
                case MaterialConvertKind.ExoticParticle:           return 2;  // shown but action's gated by hardSkip
                default:                                           return 0;
            }
        }

        private static (MaterialConvertKind kind, string shader) TargetIndexToKind(int idx)
        {
            switch (idx)
            {
                case 1: return (MaterialConvertKind.ConvertOpaqueToUnlit,     DreamParkShaderNames.Unlit);
                case 2: return (MaterialConvertKind.ConvertParticle,          DreamParkShaderNames.Particles);
                default: return (MaterialConvertKind.ConvertOpaqueToUniversal, DreamParkShaderNames.Universal);
            }
        }

        private void DrawRow(MaterialPlanRow row)
        {
            // Background tint: green tinge when approved, grey when skipped,
            // none otherwise. Plus a *kind* hue along the left edge to keep
            // the at-a-glance kind cue from the old layout.
            bool isSkipped = row.hardSkip || !string.IsNullOrEmpty(row.skipReason);
            var bg = isSkipped ? new Color(0.6f, 0.6f, 0.6f, 0.08f)
                               : (row.approved ? new Color(0.3f, 0.7f, 0.3f, 0.06f) : Color.clear);
            var rect = EditorGUILayout.BeginVertical();
            if (bg.a > 0) EditorGUI.DrawRect(rect, bg);

            // Left edge color strip carries the kind hue (same palette as before).
            Color kindStripe;
            switch (row.kind)
            {
                case MaterialConvertKind.ConvertOpaqueToUniversal: kindStripe = new Color(0.45f, 0.85f, 0.55f, 0.85f); break;
                case MaterialConvertKind.ConvertOpaqueToUnlit:     kindStripe = new Color(0.50f, 0.75f, 0.95f, 0.85f); break;
                case MaterialConvertKind.ConvertParticle:          kindStripe = new Color(0.95f, 0.70f, 0.40f, 0.85f); break;
                case MaterialConvertKind.ExoticParticle:           kindStripe = new Color(0.95f, 0.45f, 0.30f, 0.85f); break;
                case MaterialConvertKind.ReadOnlyEmbedded:         kindStripe = new Color(0.70f, 0.70f, 0.70f, 0.85f); break;
                case MaterialConvertKind.Orphan:                   kindStripe = new Color(0.85f, 0.50f, 0.50f, 0.85f); break;
                case MaterialConvertKind.AlreadyConverted:         kindStripe = new Color(0.50f, 0.50f, 0.50f, 0.55f); break;
                default:                                           kindStripe = Color.clear; break;
            }

            // Pre-load the material once for use across all columns.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(row.usage.assetPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                // Checkbox — disabled on hard-skipped rows (AlreadyConverted,
                // ReadOnlyEmbedded, ExoticParticle). For soft skips (Orphan)
                // it stays enabled so the user can opt in.
                using (new EditorGUI.DisabledScope(row.hardSkip))
                {
                    row.approved = EditorGUILayout.Toggle(row.approved, GUILayout.Width(20));
                }

                // ── Thumbnail (44×40) ────────────────────────────────────
                // Width matches the header's 44px thumb slot exactly. The
                // inner preview is 36px with a 4px margin on each side
                // (the left 4px is also where the kind stripe lives).
                //
                // Materials get the asset-preview sphere. The preview is
                // async — Unity may return null while it's still rendering,
                // and the global preview cache used to evict mid-scroll
                // (now sized to 512 in OnEnable, so that's solved).
                //
                // While the preview's loading, we draw a flat color swatch
                // sampled from the material's base color. That way the user
                // never sees the generic blue-orb fallback — they see
                // either the rendered sphere or the material's tint.
                var thumbRect = GUILayoutUtility.GetRect(44, 40, GUILayout.Width(44), GUILayout.Height(40));
                if (kindStripe.a > 0)
                    EditorGUI.DrawRect(new Rect(thumbRect.x, thumbRect.y, 3, thumbRect.height), kindStripe);

                var previewRect = new Rect(thumbRect.x + 4, thumbRect.y, 36, thumbRect.height);

                // Preview resolution policy (in order):
                //   1. If we already have a cached preview for this row
                //      AND its Texture is still alive in Unity, use it.
                //      OnGUI never re-queries AssetPreview for a row that
                //      already has one — that's the rule that kills the
                //      flicker.
                //   2. Otherwise ask AssetPreview ONCE. If it returns
                //      non-null, latch it onto the row for the rest of
                //      the session. We do NOT trigger a Repaint() here —
                //      PumpPreviews on EditorApplication.update is what
                //      drives subsequent paints until every row has its
                //      preview.
                //   3. Otherwise draw the base-color swatch.
                Texture preview = null;
                if (row.cachedPreview != null && (Texture)row.cachedPreview != null)
                {
                    preview = row.cachedPreview;
                }
                else if (mat != null)
                {
                    preview = AssetPreview.GetAssetPreview(mat);
                    if (preview != null)
                    {
                        row.cachedPreview = preview;
                    }
                }

                if (preview != null)
                {
                    EditorGUI.DrawPreviewTexture(previewRect, preview);
                }
                else
                {
                    // Color swatch fallback — always shows something useful.
                    Color swatch = mat != null ? GetMaterialBaseColor(mat) : new Color(0.5f, 0.5f, 0.5f);
                    EditorGUI.DrawRect(previewRect, swatch);
                    // 1px border so a near-white swatch is still visible
                    // against the row background.
                    var borderCol = new Color(0, 0, 0, 0.25f);
                    EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.y, previewRect.width, 1), borderCol);
                    EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.yMax - 1, previewRect.width, 1), borderCol);
                    EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.y, 1, previewRect.height), borderCol);
                    EditorGUI.DrawRect(new Rect(previewRect.xMax - 1, previewRect.y, 1, previewRect.height), borderCol);
                }

                // Asset name (clickable) + folder path.
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(260)))
                {
                    if (GUILayout.Button(Path.GetFileName(row.usage.assetPath), EditorStyles.linkLabel))
                    {
                        if (mat != null) EditorGUIUtility.PingObject(mat);
                    }
                    EditorGUILayout.LabelField(ShortDir(row.usage.assetPath), EditorStyles.miniLabel);
                }

                // Current shader column.
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220)))
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(TrimShaderName(row.usage.shaderName), row.usage.shaderName),
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(KindLabel(row.kind), EditorStyles.boldLabel);
                }

                GUILayout.Label("→", GUILayout.Width(18));

                // ── Target dropdown ──────────────────────────────────────
                // ALWAYS show the three-option dropdown for any convertible
                // kind (opaque + particle alike). Consistent UI beats a
                // mode-switching label/dropdown pair — the user can also
                // override the planner's auto-routing here (e.g. force a
                // particle material to Unlit if it turns out to be a flat
                // sprite-card effect).
                //
                // For hard-skipped kinds (AlreadyConverted, ReadOnlyEmbedded,
                // Orphan), we show a label instead because the dropdown
                // would be misleading — there's no conversion to do.
                using (new EditorGUI.DisabledScope(row.hardSkip))
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
                {
                    bool isConvertible = !row.hardSkip
                        && (row.kind == MaterialConvertKind.ConvertOpaqueToUniversal
                         || row.kind == MaterialConvertKind.ConvertOpaqueToUnlit
                         || row.kind == MaterialConvertKind.ConvertParticle);

                    if (isConvertible)
                    {
                        int curIdx = KindToTargetIndex(row.kind);
                        int newIdx = EditorGUILayout.Popup(curIdx, TargetDropdownOptions, GUILayout.Width(150));
                        if (newIdx != curIdx)
                        {
                            (row.kind, row.targetShader) = TargetIndexToKind(newIdx);
                        }
                        EditorGUILayout.LabelField(TargetSubLabel(newIdx), EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(TargetShortName(row), EditorStyles.miniLabel);
                        EditorGUILayout.LabelField("", EditorStyles.miniLabel);
                    }
                }

                // ── Particle diff column ─────────────────────────────────
                // Shown for any row whose CURRENT kind is a particle path —
                // including ExoticParticle, where the diff explains what
                // they'd lose if they force-converted. Width bumped to 160
                // so the badge fits without truncation; "View Diff" is now
                // a real button (the link-label version had a thin click
                // target that was confusing).
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(160)))
                {
                    bool isParticleRow = row.kind == MaterialConvertKind.ConvertParticle
                                      || row.kind == MaterialConvertKind.ExoticParticle;
                    if (isParticleRow && row.particleDiff != null)
                    {
                        // Three-state badge keyed off ConversionReadiness.
                        // The underlying analyzer still produces severity-
                        // graded entries (needed for prioritizing the popup),
                        // but the badge collapses the four-level scale into
                        // the three answers the user actually wants:
                        //   • Ready             → convert it
                        //   • Will look diff    → convert if you're okay with the changes
                        //   • Blocked           → don't convert without manual prep
                        var d = row.particleDiff;
                        Color badgeCol;
                        switch (d.Readiness)
                        {
                            case ConversionReadiness.Blocked:
                                badgeCol = new Color(0.85f, 0.30f, 0.30f); break;
                            case ConversionReadiness.WillLookDifferent:
                                badgeCol = new Color(0.95f, 0.65f, 0.30f); break;
                            case ConversionReadiness.ReadyWithApproximations:
                                badgeCol = new Color(0.45f, 0.65f, 0.95f); break;
                            default:
                                badgeCol = new Color(0.45f, 0.78f, 0.45f); break;
                        }

                        var prevTextCol = GUI.contentColor;
                        GUI.contentColor = badgeCol;
                        EditorGUILayout.LabelField(d.Headline, EditorStyles.miniBoldLabel);
                        GUI.contentColor = prevTextCol;
                        EditorGUILayout.LabelField(d.SubHeadline, EditorStyles.miniLabel);

                        // Real button (not a link label) so the click target
                        // is unambiguous and matches the rest of the row.
                        if (GUILayout.Button("View Diff", GUILayout.Width(90), GUILayout.Height(20)))
                        {
                            if (mat != null)
                                ParticleDiffPopup.Open(mat, row.usage.assetPath, row.particleDiff);
                        }
                    }
                    else if (isParticleRow)
                    {
                        // Particle row whose diff didn't get computed.
                        // Almost certainly means the user came back to the
                        // window after a domain reload — the [NonSerialized]
                        // diff got wiped but the rows persisted. Prompt a
                        // rescan instead of showing a scary "no diff."
                        EditorGUILayout.LabelField("rescan needed", EditorStyles.miniLabel);
                    }
                    else if (row.kind == MaterialConvertKind.ConvertOpaqueToUniversal
                          || row.kind == MaterialConvertKind.ConvertOpaqueToUnlit)
                    {
                        // Opaque convert — no particle-specific diff applies.
                        // Surface it as a positive ready state so the column
                        // never looks empty.
                        var prevTextCol = GUI.contentColor;
                        GUI.contentColor = new Color(0.45f, 0.78f, 0.45f);
                        EditorGUILayout.LabelField("✓ Ready to convert", EditorStyles.miniBoldLabel);
                        GUI.contentColor = prevTextCol;
                        EditorGUILayout.LabelField("opaque mapping", EditorStyles.miniLabel);
                    }
                    else
                    {
                        // Non-convertible row (AlreadyConverted, Orphan,
                        // ReadOnlyEmbedded). Dash communicates "n/a."
                        EditorGUILayout.LabelField("—", EditorStyles.miniLabel);
                    }
                }

                // Notes column — skip reason on top, Used-by foldout below.
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(160)))
                {
                    if (!string.IsNullOrEmpty(row.skipReason))
                    {
                        EditorGUILayout.LabelField(row.skipReason, EditorStyles.wordWrappedMiniLabel);
                    }
                    else
                    {
                        int uses = row.usage.usingPrefabs.Count + row.usage.usingScenes.Count;
                        if (uses == 0)
                        {
                            EditorGUILayout.LabelField("no users found", EditorStyles.miniLabel);
                        }
                        else
                        {
                            // Foldout for the dependent-prefabs list. Closed by
                            // default — if open, each prefab is a clickable
                            // link that pings it in the Project window.
                            row.showUsersExpanded = EditorGUILayout.Foldout(
                                row.showUsersExpanded,
                                $"Used by {uses} {(uses == 1 ? "asset" : "assets")}",
                                true,
                                EditorStyles.foldout);

                            if (row.showUsersExpanded)
                            {
                                foreach (var p in row.usage.usingPrefabs)
                                {
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(12);
                                        if (GUILayout.Button(
                                                $"↗ {Path.GetFileNameWithoutExtension(p)}",
                                                EditorStyles.linkLabel))
                                        {
                                            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(p));
                                        }
                                    }
                                }
                                foreach (var s in row.usage.usingScenes)
                                {
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        GUILayout.Space(12);
                                        if (GUILayout.Button(
                                                $"↗ {Path.GetFileNameWithoutExtension(s)} (scene)",
                                                EditorStyles.linkLabel))
                                        {
                                            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(s));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();

            // Thin separator between rows — same trick TextureOptimizer uses.
            var sep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sep, new Color(0, 0, 0, 0.1f));
        }

        // Helpers for the new row layout ─────────────────────────────────

        private static string ShortDir(string assetPath)
        {
            // Show "…/Content/Foo/Materials" — just enough to know which
            // subsection the asset lives in. Mirrors TextureOptimizer's
            // ShortDir behavior.
            int idx = assetPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
            if (idx >= 0)
            {
                int contentRel = dir.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                if (contentRel >= 0) return "…" + dir.Substring(contentRel);
            }
            return dir;
        }

        // Trim shader names that get unwieldy. "Universal Render Pipeline/
        // Particles/Lit" → "URP/Particles/Lit". The full name stays in the
        // tooltip so power users can still see it.
        private static string TrimShaderName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(none)";
            s = s.Replace("Universal Render Pipeline/", "URP/");
            s = s.Replace("Shader Graphs/", "");
            if (s.Length > 40) s = s.Substring(0, 37) + "…";
            return s;
        }

        private static string TargetShortName(MaterialPlanRow row)
        {
            switch (row.kind)
            {
                case MaterialConvertKind.ConvertParticle:   return "DreamPark/Particles";
                case MaterialConvertKind.ExoticParticle:    return "(keep vendor)";
                case MaterialConvertKind.AlreadyConverted:  return "(already DP)";
                case MaterialConvertKind.ReadOnlyEmbedded:  return "(extract first)";
                case MaterialConvertKind.Orphan:            return "(unreferenced)";
                default:                                    return "—";
            }
        }

        // Sub-label rendered beneath the target dropdown — shows the full
        // shader name. Keeps the dropdown text terse while still letting
        // the user verify which DreamPark shader they picked.
        private static string TargetSubLabel(int idx)
        {
            switch (idx)
            {
                case 1: return "DreamPark-Unlit";
                case 2: return "DreamPark/Particles";
                default: return "DreamPark-Universal";
            }
        }

        // Best-effort base-color sample for the swatch fallback. Tries the
        // property names used by DreamPark shaders, URP, Standard, and
        // common vendor packs. Returns mid-grey if nothing matches — that
        // way the swatch always has SOMETHING visible.
        private static readonly string[] BaseColorAliases = new[]
        {
            "_baseColor", "_BaseColor", "_Color", "_TintColor",
            "_MainColor", "_AlbedoColor", "_DiffuseColor", "_Tint",
        };
        private static Color GetMaterialBaseColor(Material mat)
        {
            if (mat == null) return new Color(0.5f, 0.5f, 0.5f);
            foreach (var prop in BaseColorAliases)
            {
                if (mat.HasProperty(prop))
                {
                    var c = mat.GetColor(prop);
                    // Force the swatch fully opaque so transparent particle
                    // materials don't render as invisible chips.
                    c.a = 1f;
                    return c;
                }
            }
            return new Color(0.5f, 0.5f, 0.5f);
        }

        private static string KindLabel(MaterialConvertKind k)
        {
            switch (k)
            {
                case MaterialConvertKind.AlreadyConverted:  return "(done)";
                case MaterialConvertKind.ConvertParticle:   return "→ Particles";
                case MaterialConvertKind.ExoticParticle:    return "skip (exotic)";
                case MaterialConvertKind.ReadOnlyEmbedded:  return "skip (embedded)";
                case MaterialConvertKind.Orphan:            return "(orphan)";
                default:                                    return k.ToString();
            }
        }

        // ─── Scan / Apply ───────────────────────────────────────────────
        private void RunScan()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Material Converter", "Scanning materials...", 0f);
                var usages = MaterialUsageGraph.Build(_rootFolder,
                    (p, msg) => EditorUtility.DisplayProgressBar("Material Converter", msg, p));
                _rows = MaterialConverterPlanner.Plan(usages);
                _lastScanUtc = DateTime.UtcNow;

                // Open the preview-pump window. For the next kPreviewPumpDuration
                // seconds, PumpPreviews will gently Repaint() this window so OnGUI
                // can latch in each material's preview as Unity finishes
                // generating it. After the window closes, OnGUI runs only on
                // user interaction (mouse, scroll, click) — which is plenty
                // for the rare row whose preview hadn't latched by then.
                _previewPumpDeadline = EditorApplication.timeSinceStartup + kPreviewPumpDuration;

                Debug.Log($"[MaterialConverter] Scan complete: {_rows.Count} materials under {_rootFolder}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RunApply()
        {
            int will = _rows.Count(r => r.WillBeModified);
            if (will == 0) return;
            if (!EditorUtility.DisplayDialog(
                    "Apply Material Conversions",
                    $"{will} material(s) will be converted in place.\n\n" +
                    "GUIDs are preserved — prefab references will stay valid.\n" +
                    "This cannot be undone via Ctrl-Z; use version control to revert.",
                    "Apply",
                    "Cancel"))
            {
                return;
            }

            try
            {
                var result = MaterialConverterExecutor.Apply(_rows,
                    (p, msg) => EditorUtility.DisplayProgressBar("Material Converter", msg, p));
                string streamsLine = result.rendererStreamsReset > 0
                    ? $"\n\nParticle renderers refreshed: {result.rendererStreamsReset}\n" +
                      "(Custom Vertex Streams reset to default — fixes the\n" +
                      " \"whole flipbook visible at once\" symptom that\n" +
                      " happens when converting vendor materials whose\n" +
                      " renderers were configured for motion-vector\n" +
                      " flipbook blending.)"
                    : "";
                EditorUtility.DisplayDialog("Material Converter",
                    $"Converted:  {result.converted}\n" +
                    $"Failed:     {result.failed}\n" +
                    $"Skipped:    {result.skipped}" +
                    streamsLine + "\n\n" +
                    "See Console for per-row details.",
                    "OK");
                // Re-scan so the table reflects the new state.
                RunScan();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void SetApproved(bool value, bool convertibleOnly)
        {
            foreach (var row in _rows)
            {
                if (row.hardSkip) continue;
                if (convertibleOnly && !(row.kind == MaterialConvertKind.ConvertOpaqueToUniversal
                                      || row.kind == MaterialConvertKind.ConvertOpaqueToUnlit
                                      || row.kind == MaterialConvertKind.ConvertParticle))
                    continue;
                row.approved = value;
            }
        }

        private List<MaterialPlanRow> FilteredRows()
        {
            IEnumerable<MaterialPlanRow> q = _rows;

            if (!_showAlreadyConverted) q = q.Where(r => r.kind != MaterialConvertKind.AlreadyConverted);
            if (!_showOrphans)          q = q.Where(r => r.kind != MaterialConvertKind.Orphan);
            if (!_showExotic)           q = q.Where(r => r.kind != MaterialConvertKind.ExoticParticle);
            if (!_showEmbedded)         q = q.Where(r => r.kind != MaterialConvertKind.ReadOnlyEmbedded);

            if (!string.IsNullOrEmpty(_search))
            {
                string s = _search.Trim();
                q = q.Where(r =>
                       r.usage.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.usage.shaderName?.IndexOf(s, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            switch (_sort)
            {
                case SortMode.UsageCountDesc:
                    // Most-referenced materials first — the high-value
                    // targets that warrant the closest review. Asset path
                    // as tiebreaker so the table is deterministic when
                    // many rows share the same usage count (e.g. lots of
                    // orphans tied at 0).
                    q = q.OrderByDescending(r => r.usage.usingPrefabs.Count + r.usage.usingScenes.Count)
                         .ThenBy(r => r.usage.assetPath, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortMode.ShaderAsc:
                    q = q.OrderBy(r => r.usage.shaderName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.usage.assetPath, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortMode.KindAsc:
                    q = q.OrderBy(r => (int)r.kind)
                         .ThenBy(r => r.usage.assetPath, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortMode.PathAsc:
                default:
                    q = q.OrderBy(r => r.usage.assetPath, StringComparer.OrdinalIgnoreCase);
                    break;
            }

            return q.ToList();
        }
    }
}
#endif
