#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AnimationOptimization
{
    /// <summary>
    /// The Animation Optimizer review window.
    ///
    /// Workflow:
    ///   1. User opens via DreamPark → Animation Optimizer...
    ///   2. Click "Scan" — the window builds an AnimationUsageGraph that
    ///      enumerates both FBX sub-clips AND standalone .anim files,
    ///      detecting the source FBX for each standalone via bone-path
    ///      Jaccard similarity.
    ///   3. The table shows every clip with a proposed strategy + the
    ///      route the executor will take (in-place on the FBX vs round-trip
    ///      with GUID preservation).
    ///   4. User reviews per-row, can change global error tolerances, and
    ///      previews the run (dry-run) before committing.
    ///   5. "Apply Selected" runs Unity's own ModelImporter keyframe reducer
    ///      on every group of rows.
    /// </summary>
    public class AnimationOptimizerWindow : EditorWindow
    {
        // Sits under DreamPark/Optimization/ alongside Texture (120) and
        // Audio (122) optimizers.
        [MenuItem("DreamPark/Optimization/Animation Optimizer...", false, 121)]
        public static void Open()
        {
            var w = GetWindow<AnimationOptimizerWindow>("Animation Optimizer");
            w.minSize = new Vector2(1080, 600);
            w.Show();
        }

        // ─── State ──────────────────────────────────────────────────────

        private string _rootFolder = "Assets/Content";
        private List<AnimationPlanRow> _rows = new List<AnimationPlanRow>();
        private Vector2 _scroll;
        private string _search = "";
        private SortMode _sort = SortMode.SavingsDesc;
        private bool _showSkipped = true;
        private DateTime _lastScanUtc = DateTime.MinValue;

        // Error tolerances are intentionally NOT exposed in the UI — Unity's
        // defaults (0.5° rotation, 0.5 position, 0.5 scale) are what
        // "Anim. Compression = Optimal" uses and work for every case we've
        // seen. The values still live on AnimationPlanRow so a future
        // Advanced panel can surface per-row tuning without a schema change.

        private enum SortMode
        {
            SavingsDesc,
            SizeDesc,
            KeyframesDesc,
            PathAsc,
        }

        // ─── Window lifecycle ───────────────────────────────────────────

        private void OnEnable()
        {
            _rootFolder = EditorPrefs.GetString("DreamPark.AnimationOptimizer.Root", _rootFolder);
            if (!AssetDatabase.IsValidFolder(_rootFolder))
                _rootFolder = AutoPickContentFolder();
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

        // ─── Header ─────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Animation Optimizer", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Lists every animation clip in the content folder — both FBX sub-clips and standalone "
                    + ".anim files. Optimization runs through Unity's own ModelImporter keyframe reducer "
                    + "(the same algorithm as setting Anim. Compression = Optimal in the inspector). "
                    + "Standalone .anim files with a detected FBX source are round-tripped through the FBX "
                    + "and re-extracted with their original GUID preserved — every reference stays intact.",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Content folder:", GUILayout.Width(100));
                    string newRoot = EditorGUILayout.TextField(_rootFolder);
                    if (newRoot != _rootFolder)
                    {
                        _rootFolder = newRoot;
                        EditorPrefs.SetString("DreamPark.AnimationOptimizer.Root", _rootFolder);
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
                                EditorPrefs.SetString("DreamPark.AnimationOptimizer.Root", _rootFolder);
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

        // ─── Controls ───────────────────────────────────────────────────

        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶  Scan", GUILayout.Width(120), GUILayout.Height(26)))
                    RunScan();

                GUI.enabled = _rows.Count > 0;
                if (GUILayout.Button("Apply Selected", GUILayout.Width(140), GUILayout.Height(26)))
                    RunApply(dryRun: false);
                if (GUILayout.Button(new GUIContent("Preview (no writes)",
                        "Runs the optimizer in dry-run mode. Reports the predicted byte savings without modifying any files."),
                    GUILayout.Width(150), GUILayout.Height(26)))
                    RunApply(dryRun: true);
                GUI.enabled = true;

                GUI.enabled = _rows.Count > 0;
                if (GUILayout.Button("✓ Check All", GUILayout.Width(100), GUILayout.Height(26)))
                    SetApprovedAll(true);
                if (GUILayout.Button("✗ Uncheck All", GUILayout.Width(110), GUILayout.Height(26)))
                    SetApprovedAll(false);
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("Sort:", GUILayout.Width(40));
                _sort = (SortMode)EditorGUILayout.EnumPopup(_sort, GUILayout.Width(140));

                _showSkipped = GUILayout.Toggle(_showSkipped, "Show skipped", GUILayout.Width(110));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                _search = EditorGUILayout.TextField(_search);
                if (!string.IsNullOrEmpty(_search) && GUILayout.Button("✕", GUILayout.Width(24)))
                    _search = "";

                if (_lastScanUtc != DateTime.MinValue)
                    EditorGUILayout.LabelField($"Scanned {_lastScanUtc.ToLocalTime():HH:mm:ss}", EditorStyles.miniLabel, GUILayout.Width(140));
            }
        }

        // ─── Summary ────────────────────────────────────────────────────

        private void DrawSummaryBar()
        {
            if (_rows.Count == 0)
            {
                EditorGUILayout.HelpBox("Click 'Scan' to inventory animation clips under " + _rootFolder + ".", MessageType.Info);
                return;
            }

            long totalBefore = 0, totalAfter = 0;
            int modified = 0, skipped = 0;
            int subClipRows = 0, standaloneRows = 0, orphanRows = 0;
            foreach (var r in _rows)
            {
                totalBefore += r.usage.fileBytes;
                if (r.WillBeModified)
                {
                    modified++;
                    totalAfter += r.estimatedAfterBytes;
                }
                else
                {
                    skipped++;
                    totalAfter += r.usage.fileBytes;
                }
                switch (r.usage.rowKind)
                {
                    case AnimationRowKind.FbxSubClip:          subClipRows++; break;
                    case AnimationRowKind.StandaloneWithSource: standaloneRows++; break;
                    case AnimationRowKind.StandaloneOrphan:    orphanRows++; break;
                }
            }
            long saved = Math.Max(0, totalBefore - totalAfter);
            float pct = totalBefore > 0 ? 100f * saved / totalBefore : 0f;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    $"Clips: {_rows.Count}    |    FBX sub-clips: {subClipRows}    |    Standalone w/ source: {standaloneRows}    |    Orphans: {orphanRows}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"Approved: {modified}    Skipped: {skipped}    " +
                    $"Debt: {FormatBytes(totalBefore)}  →  {FormatBytes(totalAfter)}    " +
                    $"({FormatBytes(saved)} saved, {pct:0.#}%)",
                    EditorStyles.boldLabel);
            }
        }

        // ─── Table ──────────────────────────────────────────────────────

        private void DrawTable()
        {
            if (_rows.Count == 0) return;

            var rows = FilterAndSort(_rows);

            DrawTableHeader(rows);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var r in rows) DrawRow(r);
            EditorGUILayout.EndScrollView();
        }

        private void DrawTableHeader(List<AnimationPlanRow> visibleRows)
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
                        SetApprovedAll(nextChecked, onlyVisible: visibleRows);
                }
                GUILayout.Label("", GUILayout.Width(28));
                GUILayout.Label("Clip", EditorStyles.miniLabel, GUILayout.MinWidth(220));
                GUILayout.Label("Where", EditorStyles.miniLabel, GUILayout.Width(180));
                GUILayout.Label("Current", EditorStyles.miniLabel, GUILayout.Width(170));
                GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(18));
                GUILayout.Label("Strategy", EditorStyles.miniLabel, GUILayout.Width(160));
                GUILayout.Label("Savings", EditorStyles.miniLabel, GUILayout.Width(110));
                GUILayout.Label("Notes", EditorStyles.miniLabel, GUILayout.MinWidth(160));
            }
        }

        private void DrawRow(AnimationPlanRow row)
        {
            bool isSkipped = !string.IsNullOrEmpty(row.skipReason);
            var bg = isSkipped ? new Color(0.6f, 0.6f, 0.6f, 0.08f)
                               : (row.approved ? new Color(0.3f, 0.7f, 0.3f, 0.05f) : Color.clear);
            var rect = EditorGUILayout.BeginVertical();
            if (bg.a > 0) EditorGUI.DrawRect(rect, bg);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(row.hardSkip))
                {
                    bool wasApproved = row.approved;
                    row.approved = EditorGUILayout.Toggle(row.approved, GUILayout.Width(20));
                    if (row.approved != wasApproved && row.approved)
                        row.skipReason = null;
                }

                // Icon
                var iconRect = GUILayoutUtility.GetRect(24, 24, GUILayout.Width(24), GUILayout.Height(24));
                var icon = EditorGUIUtility.IconContent("AnimationClip Icon").image;
                if (icon != null) GUI.DrawTexture(iconRect, icon);

                // Clip name + path
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(220)))
                {
                    if (GUILayout.Button(row.usage.clipName, EditorStyles.linkLabel))
                        PingRow(row);
                    EditorGUILayout.LabelField(ShortDir(row.usage.assetPath), EditorStyles.miniLabel);
                }

                // Where — row kind + source FBX info. This is the column that
                // tells the reviewer "this is going to go through Unity's
                // importer on Spider 1.fbx" vs "this is an orphan" at a glance.
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(180)))
                {
                    EditorGUILayout.LabelField(RowKindLabel(row.usage.rowKind), EditorStyles.boldLabel);
                    if (row.usage.rowKind == AnimationRowKind.StandaloneWithSource && row.usage.fbxSource != null)
                    {
                        if (GUILayout.Button($"↗ {Path.GetFileName(row.usage.fbxSource.fbxPath)}", EditorStyles.linkLabel))
                            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.fbxSource.fbxPath));
                    }
                    else if (row.usage.rowKind == AnimationRowKind.FbxSubClip)
                    {
                        EditorGUILayout.LabelField(Path.GetFileName(row.usage.assetPath), EditorStyles.miniLabel);
                    }
                }

                // Current — size, length, curves, keys
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(170)))
                {
                    EditorGUILayout.LabelField(FormatBytes(row.usage.fileBytes), EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        $"{row.usage.length:0.##}s @ {row.usage.frameRate:0}fps   {row.usage.totalKeyframes:N0} keys",
                        EditorStyles.miniLabel);
                }

                GUILayout.Label("→", GUILayout.Width(18));

                // Strategy dropdown
                using (new EditorGUI.DisabledScope(row.hardSkip))
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(160)))
                    {
                        var newStrategy = (OptimizationStrategy)EditorGUILayout.EnumPopup(row.strategy, GUILayout.Width(150));
                        if (newStrategy != row.strategy)
                        {
                            row.strategy = newStrategy;
                            row.estimatedAfterBytes = AnimationOptimizationPlanner.EstimateBytes(row.usage, row);
                        }
                        EditorGUILayout.LabelField(StrategyDescription(row), EditorStyles.miniLabel);
                    }
                }

                // Savings
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(110)))
                {
                    if (row.WillBeModified)
                    {
                        EditorGUILayout.LabelField(FormatBytes(row.EstimatedSavedBytes), EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"({row.EstimatedSavingsPercent:0.#}%)", EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("—", EditorStyles.miniLabel);
                    }
                }

                // Notes
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(160)))
                {
                    if (!string.IsNullOrEmpty(row.skipReason))
                    {
                        EditorGUILayout.LabelField(row.skipReason, EditorStyles.wordWrappedMiniLabel);
                    }
                    else
                    {
                        string label = $"{row.usage.clipKind.ToString().ToLowerInvariant()} · " +
                                       $"now: {CompressionShortLabel(row.usage.currentCompression)}";
                        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

                        if (!string.IsNullOrEmpty(row.usage.largestUseExample))
                        {
                            if (GUILayout.Button(
                                $"↗ used by {Path.GetFileNameWithoutExtension(row.usage.largestUseExample)}",
                                EditorStyles.linkLabel))
                            {
                                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.largestUseExample));
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
            var sep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sep, new Color(0, 0, 0, 0.1f));
        }

        private static void PingRow(AnimationPlanRow row)
        {
            // For sub-clip rows, ping the host FBX (the clip itself is a
            // sub-asset and pinging by Object isn't always reliable).
            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.assetPath));
        }

        private static string RowKindLabel(AnimationRowKind k)
        {
            switch (k)
            {
                case AnimationRowKind.FbxSubClip:           return "FBX sub-clip";
                case AnimationRowKind.StandaloneWithSource: return "Standalone (via FBX)";
                case AnimationRowKind.StandaloneOrphan:     return "Standalone (orphan)";
                default:                                    return k.ToString();
            }
        }

        private static string CompressionShortLabel(ModelImporterAnimationCompression c)
        {
            switch (c)
            {
                case ModelImporterAnimationCompression.Optimal:                       return "Optimal";
                case ModelImporterAnimationCompression.KeyframeReductionAndCompression: return "KFR+Compress";
                case ModelImporterAnimationCompression.KeyframeReduction:             return "Keyframe Red.";
                default:                                                              return "Off";
            }
        }

        private static string StrategyDescription(AnimationPlanRow row)
        {
            switch (row.strategy)
            {
                case OptimizationStrategy.Optimal:            return "Anim Compression = Optimal";
                case OptimizationStrategy.KeyframeReduction:  return "Anim Compression = Keyframe Red.";
                case OptimizationStrategy.KeepAsIs:
                default:                                      return "no-op";
            }
        }

        // ─── Filter / sort ──────────────────────────────────────────────

        private List<AnimationPlanRow> FilterAndSort(List<AnimationPlanRow> rows)
        {
            IEnumerable<AnimationPlanRow> q = rows;
            if (!_showSkipped) q = q.Where(r => string.IsNullOrEmpty(r.skipReason));
            if (!string.IsNullOrEmpty(_search))
            {
                string s = _search.Trim();
                q = q.Where(r => r.usage.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                              || r.usage.clipName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            switch (_sort)
            {
                case SortMode.SavingsDesc:   q = q.OrderByDescending(r => r.EstimatedSavedBytes); break;
                case SortMode.SizeDesc:      q = q.OrderByDescending(r => r.usage.fileBytes); break;
                case SortMode.KeyframesDesc: q = q.OrderByDescending(r => r.usage.totalKeyframes); break;
                case SortMode.PathAsc:       q = q.OrderBy(r => r.usage.assetPath, StringComparer.Ordinal); break;
            }
            return q.ToList();
        }

        // ─── Actions ────────────────────────────────────────────────────

        private void RunScan()
        {
            if (!AssetDatabase.IsValidFolder(_rootFolder))
            {
                EditorUtility.DisplayDialog("Folder not found",
                    "The path '" + _rootFolder + "' isn't a valid Assets-relative folder. Use the Browse button to pick one.",
                    "OK");
                return;
            }

            try
            {
                var usages = AnimationUsageGraph.Build(_rootFolder, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Animation Optimizer", msg, p));
                _rows = AnimationOptimizationPlanner.Plan(usages);
                _lastScanUtc = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnimationOptimizer] Scan failed: {e}");
                EditorUtility.DisplayDialog("Scan failed",
                    "The scan threw an exception:\n\n" + e.Message + "\n\nSee Console for details.",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RunApply(bool dryRun)
        {
            int approvedCount = _rows.Count(r => r.WillBeModified);
            if (approvedCount == 0)
            {
                EditorUtility.DisplayDialog("Nothing to apply",
                    "No rows are currently approved. Use the Check All button or tick individual rows first.",
                    "OK");
                return;
            }

            int subClips = _rows.Count(r => r.WillBeModified && r.usage.rowKind == AnimationRowKind.FbxSubClip);
            int standalones = _rows.Count(r => r.WillBeModified && r.usage.rowKind == AnimationRowKind.StandaloneWithSource);

            string actionDesc = dryRun
                ? $"Dry-run: simulate optimization for {approvedCount} clips ({subClips} FBX sub-clips, {standalones} standalone)."
                : $"You are about to optimize {approvedCount} clips ({subClips} FBX sub-clips, {standalones} standalone) under {_rootFolder}.\n\n"
                + "FBX sub-clips: import settings on the host model will be set to your chosen strategy + tolerances, "
                + "then the model is re-imported. Unity's keyframe reducer runs on the embedded clips.\n\n"
                + "Standalone .anim files: their source FBX is re-imported the same way, then the compressed sub-clip "
                + "is copied back to the .anim path. Original .meta is carried forward to preserve the GUID — every "
                + "controller and prefab reference stays valid.\n\n"
                + "Recommend: commit your current git state first so the change can be reverted with one command.\n\n"
                + "Proceed?";

            if (!EditorUtility.DisplayDialog(
                    dryRun ? "Preview animation optimization" : "Apply animation optimization",
                    actionDesc,
                    dryRun ? "Preview" : "Apply",
                    "Cancel"))
                return;

            try
            {
                var result = AnimationOptimizationExecutor.Apply(_rows, dryRun, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Animation Optimizer", msg, p));

                EditorUtility.ClearProgressBar();

                string failedDetails = "";
                if (result.failed > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("\n\nFailed rows (first 5):");
                    int shown = 0;
                    foreach (var rr in result.rows)
                    {
                        if (rr.ok) continue;
                        sb.Append("\n  • ").Append(Path.GetFileName(rr.assetPath))
                          .Append(rr.clipName != null && rr.clipName != Path.GetFileNameWithoutExtension(rr.assetPath) ? " [" + rr.clipName + "]" : "")
                          .Append(" — ").Append(string.IsNullOrEmpty(rr.error) ? "unknown error" : rr.error);
                        if (++shown >= 5) break;
                    }
                    if (result.failed > shown)
                        sb.Append("\n  …and ").Append(result.failed - shown).Append(" more (see Console).");
                    failedDetails = sb.ToString();
                }

                EditorUtility.DisplayDialog(
                    dryRun ? "Animation Optimizer — Preview" : "Animation Optimizer",
                    $"Processed: {result.processed}\nSucceeded: {result.succeeded}\nFailed: {result.failed}\n\n"
                    + $"Before:    {FormatBytes(result.bytesBefore)}\n"
                    + $"{(dryRun ? "Predicted" : "After:    ")}: {FormatBytes(result.bytesAfter)}\n"
                    + $"{(dryRun ? "Estimated" : "Saved:    ")}: {FormatBytes(result.BytesSaved)}  ({result.PercentSaved:0.#}%)"
                    + failedDetails,
                    "OK");

                if (!dryRun) RunScan();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnimationOptimizer] Apply failed: {e}");
                EditorUtility.DisplayDialog("Apply failed", e.Message + "\n\nSee Console for details.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void SetApprovedAll(bool approved, IList<AnimationPlanRow> onlyVisible = null)
        {
            var target = onlyVisible ?? (IList<AnimationPlanRow>)_rows;
            foreach (var r in target)
            {
                if (r.hardSkip) continue;
                r.approved = approved;
                if (approved) r.skipReason = null;
            }
        }

        // ─── Utility ────────────────────────────────────────────────────

        private static string ShortDir(string assetPath)
        {
            string dir = Path.GetDirectoryName(assetPath) ?? "";
            const string prefix = "Assets/Content/";
            if (dir.StartsWith(prefix, StringComparison.Ordinal))
            {
                int idx = dir.IndexOf('/', prefix.Length);
                if (idx >= 0) return ".../" + dir.Substring(idx + 1);
            }
            return dir;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024d && u < units.Length - 1) { v /= 1024d; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }
}
#endif
