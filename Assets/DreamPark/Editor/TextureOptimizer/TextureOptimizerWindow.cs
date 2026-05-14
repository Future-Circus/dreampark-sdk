#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// The Texture Optimizer review window.
    ///
    /// Workflow:
    ///   1. User opens via DreamPark → Texture Optimizer...
    ///   2. Click "Scan" — the window builds a TextureUsageGraph over the
    ///      content folder and feeds it through the Planner. ~5-20s.
    ///   3. The table shows every texture with a proposed re-encode. The
    ///      header shows current debt + projected savings.
    ///   4. User reviews per-row, toggles individual rows, optionally
    ///      overrides target resolution via the dropdown.
    ///   5. Click "Apply Selected" — Executor runs, the file system is
    ///      mutated, importer settings tightened, GUIDs preserved.
    ///
    /// The window's design intentionally mirrors BundleSizeBreakdown so
    /// creators don't have to learn a new layout — header card,
    /// controls row, scrollable table with helpbox-styled rows.
    /// </summary>
    public class TextureOptimizerWindow : EditorWindow
    {
        // ─── Menu ────────────────────────────────────────────────────────
        // Priority 120 puts this between top-level utilities (1-50) and
        // Troubleshooting (200+). Texture optimization is both a
        // diagnostic and a mutator, so it lives in its own top-level slot
        // rather than nested under Diagnostics/.
        [MenuItem("DreamPark/Texture Optimizer...", false, 120)]
        public static void Open()
        {
            var w = GetWindow<TextureOptimizerWindow>("Texture Optimizer");
            w.minSize = new Vector2(1040, 600);
            w.Show();
        }

        // ─── State ──────────────────────────────────────────────────────

        private string _rootFolder = "Assets/Content";
        private List<TexturePlanRow> _rows = new List<TexturePlanRow>();
        private Vector2 _scroll;
        private string _search = "";
        private SortMode _sort = SortMode.SavingsDesc;
        private bool _showSkipped = true;
        private DateTime _lastScanUtc = DateTime.MinValue;

        private enum SortMode
        {
            SavingsDesc,
            SizeDesc,
            PathAsc,
            FormatAsc,
        }

        // ─── Window lifecycle ───────────────────────────────────────────

        private void OnEnable()
        {
            // Auto-pick the first non-placeholder content folder if the
            // remembered one doesn't exist.
            _rootFolder = EditorPrefs.GetString("DreamPark.TextureOptimizer.Root", _rootFolder);
            if (!AssetDatabase.IsValidFolder(_rootFolder))
                _rootFolder = AutoPickContentFolder();

            // First-use auto-install of Magick.NET. The installer is
            // careful: it only downloads when files are actually missing
            // (every subsequent open is a no-op). The install ends with
            // AssetDatabase.Refresh which triggers a domain reload and
            // closes this window; the [InitializeOnLoad] hook in the
            // installer reopens it on the next editor tick so the user
            // sees "click → progress bar → window ready" as one flow.
            EnsureMagickNetInstalled();
        }

        private static void EnsureMagickNetInstalled()
        {
            if (MagickNetInstaller.IsInstalled() && MagickNetBootstrap.IsAvailable) return;

            // Don't auto-install if the user is doing something else with
            // the editor — Unity in Play Mode, importing assets, or
            // compiling scripts. We'd interrupt their work. The install
            // will happen the next time they open the window in a quiet
            // editor.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || Application.isPlaying)
                return;

            try
            {
                MagickNetInstaller.InstallSync((p, msg) =>
                    EditorUtility.DisplayProgressBar("Setting up Texture Optimizer", msg, p));
                MagickNetBootstrap.Invalidate();
            }
            catch (Exception e)
            {
                Debug.LogError($"[TextureOptimizer] Magick.NET install failed: {e}");
                EditorUtility.DisplayDialog(
                    "Texture Optimizer setup failed",
                    "Couldn't download Magick.NET on first use:\n\n" + e.Message +
                    "\n\nCheck your internet connection and reopen the window to retry. " +
                    "See the Console for full details.",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static string AutoPickContentFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Content")) return "Assets/Content";
            // Prefer the first subfolder under Assets/Content that isn't
            // the placeholder.
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
            DrawSetupCardIfNeeded();
            DrawSummaryBar();
            EditorGUILayout.Space(4);
            DrawTable();
        }

        /// <summary>
        /// Surfaces a recovery card when Magick.NET isn't loaded. The
        /// auto-install flow handles the happy path silently; this card
        /// only appears when the bootstrap reports unavailable AFTER
        /// OnEnable already tried to install (e.g. a partial install
        /// left Unity in a broken state, or no internet on first run).
        /// The card has one button that wipes the install folder and
        /// retries — same code path that runs on first open, just
        /// triggerable from a button instead of from OnEnable.
        /// </summary>
        private void DrawSetupCardIfNeeded()
        {
            if (MagickNetBootstrap.IsAvailable) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("⚠  Magick.NET not loaded", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Scanning works, but applying changes needs Magick.NET. "
                    + "A previous auto-install may have failed midway and left "
                    + "Unity in a half-loaded state. Click the button to wipe "
                    + "the install folder and retry from a clean slate.",
                    EditorStyles.wordWrappedMiniLabel);

                if (GUILayout.Button("Reinstall Magick.NET (clean download, ~30s)", GUILayout.Height(26)))
                {
                    try
                    {
                        MagickNetInstaller.InstallSync((p, msg) =>
                            EditorUtility.DisplayProgressBar("Reinstalling Magick.NET", msg, p));
                        MagickNetBootstrap.Invalidate();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[TextureOptimizer] Reinstall failed: {e}");
                        EditorUtility.DisplayDialog(
                            "Reinstall failed",
                            "Magick.NET reinstall failed:\n\n" + e.Message +
                            "\n\nSee Console for full details. If the failure repeats, "
                            + "close Unity and manually delete:\n\n"
                            + MagickNetInstaller.Root + "\n\nThen reopen Unity.",
                            "OK");
                    }
                    finally
                    {
                        EditorUtility.ClearProgressBar();
                    }
                }
            }
        }

        // ─── Header ─────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Texture Optimizer", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Scans the content folder, converts transparent textures to PNG and opaque "
                    + "to JPG, and picks 256/512/1024 based on the largest renderer that uses each "
                    + "texture. Review every row before committing — Unity GUIDs are preserved so "
                    + "material and prefab references stay intact.",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Content folder:", GUILayout.Width(100));
                    string newRoot = EditorGUILayout.TextField(_rootFolder);
                    if (newRoot != _rootFolder)
                    {
                        _rootFolder = newRoot;
                        EditorPrefs.SetString("DreamPark.TextureOptimizer.Root", _rootFolder);
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
                                EditorPrefs.SetString("DreamPark.TextureOptimizer.Root", _rootFolder);
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
                EditorGUILayout.HelpBox("Click 'Scan' to inventory textures under " + _rootFolder + ".", MessageType.Info);
                return;
            }

            long totalBefore = 0, totalAfter = 0;
            int modified = 0, skipped = 0;
            foreach (var r in _rows)
            {
                totalBefore += r.usage.fileBytes;
                if (r.WillBeModified) { modified++; totalAfter += r.estimatedAfterBytes; }
                else { skipped++; totalAfter += r.usage.fileBytes; }
            }
            long saved = Math.Max(0, totalBefore - totalAfter);
            float pct = totalBefore > 0 ? 100f * saved / totalBefore : 0f;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    $"Total textures: {_rows.Count}    |    Approved: {modified}    |    Skipped: {skipped}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"Texture debt:  {FormatBytes(totalBefore)}    →    Projected: {FormatBytes(totalAfter)}    " +
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

        private void DrawTableHeader(List<TexturePlanRow> visibleRows)
        {
            // Compute the header-checkbox state from the currently-visible
            // (filtered/sorted) rows that aren't hard-skipped. If every one
            // of them is approved, the header is checked; if none are, it's
            // unchecked. Clicking flips them all to the opposite state.
            // This is the standard table "select-all" affordance — and it
            // respects the search filter, so creators can search "rocks_*"
            // and tick the header to enable just that subset.
            int eligible = 0, approved = 0;
            foreach (var r in visibleRows)
            {
                if (IsHardSkip(r)) continue;
                eligible++;
                if (r.approved) approved++;
            }
            bool headerChecked = eligible > 0 && approved == eligible;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(eligible == 0))
                {
                    bool nextChecked = EditorGUILayout.Toggle(
                        headerChecked,
                        GUILayout.Width(20));
                    if (nextChecked != headerChecked)
                        SetApprovedAll(nextChecked, onlyVisible: visibleRows);
                }
                GUILayout.Label("", GUILayout.Width(44));               // thumb
                GUILayout.Label("Asset", EditorStyles.miniLabel, GUILayout.MinWidth(220));
                GUILayout.Label("Current", EditorStyles.miniLabel, GUILayout.Width(170));
                GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(18));
                GUILayout.Label("Target", EditorStyles.miniLabel, GUILayout.Width(220));
                GUILayout.Label("Savings", EditorStyles.miniLabel, GUILayout.Width(110));
                GUILayout.Label("Notes", EditorStyles.miniLabel, GUILayout.MinWidth(160));
            }
        }

        private void DrawRow(TexturePlanRow row)
        {
            bool isSkipped = !string.IsNullOrEmpty(row.skipReason);
            var bg = isSkipped ? new Color(0.6f, 0.6f, 0.6f, 0.08f)
                               : (row.approved ? new Color(0.3f, 0.7f, 0.3f, 0.05f) : Color.clear);
            var rect = EditorGUILayout.BeginVertical();
            if (bg.a > 0) EditorGUI.DrawRect(rect, bg);

            using (new EditorGUILayout.HorizontalScope())
            {
                // Checkbox: disabled when there's a hard skip reason that
                // came from the planner (e.g. lightmap). For soft skips
                // (no bounds, orphan), the user can flip it on.
                using (new EditorGUI.DisabledScope(IsHardSkip(row)))
                {
                    bool wasApproved = row.approved;
                    row.approved = EditorGUILayout.Toggle(row.approved, GUILayout.Width(20));
                    if (row.approved != wasApproved && row.approved)
                        row.skipReason = null; // user opted back in — clear the soft skip
                }

                // Thumbnail
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(row.usage.assetPath);
                var thumbRect = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
                if (tex != null) EditorGUI.DrawPreviewTexture(thumbRect, tex);

                // Asset name (clickable to ping)
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(220)))
                {
                    if (GUILayout.Button(Path.GetFileName(row.usage.assetPath), EditorStyles.linkLabel))
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.assetPath));
                    EditorGUILayout.LabelField(
                        ShortDir(row.usage.assetPath),
                        EditorStyles.miniLabel);
                }

                // Current column
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(170)))
                {
                    EditorGUILayout.LabelField(
                        $"{row.usage.extension.TrimStart('.').ToUpperInvariant()}  {row.usage.sourceWidth}×{row.usage.sourceHeight}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"{FormatBytes(row.usage.fileBytes)}", EditorStyles.boldLabel);
                }

                GUILayout.Label("→", GUILayout.Width(18));

                // Target column — format + size dropdowns
                using (new EditorGUI.DisabledScope(IsHardSkip(row)))
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(220)))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var newFormat = (TargetFormat)EditorGUILayout.EnumPopup(row.targetFormat, GUILayout.Width(80));
                            if (newFormat != row.targetFormat)
                            {
                                row.targetFormat = newFormat;
                                row.estimatedAfterBytes = TextureOptimizationPlanner.EstimateBytes(
                                    row.targetWidth, row.targetHeight, row.targetFormat);
                            }
                            int idx = Array.IndexOf(TextureSizingPolicy.AllowedSizes, row.targetMaxSize);
                            if (idx < 0) idx = 2;
                            int newIdx = EditorGUILayout.Popup(idx,
                                Array.ConvertAll(TextureSizingPolicy.AllowedSizes, s => s.ToString()),
                                GUILayout.Width(80));
                            if (newIdx != idx)
                            {
                                row.targetMaxSize = TextureSizingPolicy.AllowedSizes[newIdx];
                                row.targetImporterMaxSize = row.targetMaxSize;
                                (row.targetWidth, row.targetHeight) = TextureOptimizationPlanner.ScaleKeepingAspect(
                                    row.usage.sourceWidth, row.usage.sourceHeight, row.targetMaxSize);
                                row.estimatedAfterBytes = TextureOptimizationPlanner.EstimateBytes(
                                    row.targetWidth, row.targetHeight, row.targetFormat);
                            }
                        }
                        EditorGUILayout.LabelField(
                            $"{row.targetWidth}×{row.targetHeight}    {FormatBytes(row.estimatedAfterBytes)}",
                            EditorStyles.miniLabel);
                    }
                }

                // Savings %
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(110)))
                {
                    if (row.WillBeModified)
                    {
                        EditorGUILayout.LabelField(
                            $"{FormatBytes(row.EstimatedSavedBytes)}",
                            EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"({row.EstimatedSavingsPercent:0.#}%)", EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("—", EditorStyles.miniLabel);
                    }
                }

                // Notes column
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(160)))
                {
                    if (!string.IsNullOrEmpty(row.skipReason))
                    {
                        EditorGUILayout.LabelField(row.skipReason, EditorStyles.wordWrappedMiniLabel);
                    }
                    else
                    {
                        string usageLabel = row.usage.kind switch
                        {
                            TextureUsageKind.WorldRenderer => $"world prop · {row.usage.maxRendererSizeMeters:0.##}m",
                            TextureUsageKind.UI            => "UI",
                            TextureUsageKind.Particle      => "particle",
                            TextureUsageKind.Skybox        => "skybox",
                            TextureUsageKind.Lightmap      => "lightmap",
                            TextureUsageKind.Orphan        => "orphan",
                            TextureUsageKind.UnusedMaterial => "unused mat",
                            _                              => row.usage.kind.ToString(),
                        };
                        EditorGUILayout.LabelField(
                            $"{usageLabel} · {row.usage.role.ToString().ToLowerInvariant()}",
                            EditorStyles.miniLabel);
                        if (!string.IsNullOrEmpty(row.usage.largestUseExample))
                        {
                            if (GUILayout.Button(
                                $"↗ {Path.GetFileNameWithoutExtension(row.usage.largestUseExample)}",
                                EditorStyles.linkLabel))
                            {
                                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.largestUseExample));
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
            // Thin separator
            var sep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sep, new Color(0, 0, 0, 0.1f));
        }

        private static bool IsHardSkip(TexturePlanRow row)
        {
            // Hard skip = lightmap, cubemap, light cookie. The planner
            // sets row.hardSkip = true on these and the UI greys out the
            // checkbox. Soft skips (orphan, already-tight) stay toggleable
            // so the user can override the planner's recommendation.
            return row.hardSkip;
        }

        // ─── Filter / sort ──────────────────────────────────────────────

        private List<TexturePlanRow> FilterAndSort(List<TexturePlanRow> rows)
        {
            IEnumerable<TexturePlanRow> q = rows;
            if (!_showSkipped) q = q.Where(r => string.IsNullOrEmpty(r.skipReason));
            if (!string.IsNullOrEmpty(_search))
            {
                string s = _search.Trim();
                q = q.Where(r => r.usage.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            switch (_sort)
            {
                case SortMode.SavingsDesc: q = q.OrderByDescending(r => r.EstimatedSavedBytes); break;
                case SortMode.SizeDesc:    q = q.OrderByDescending(r => r.usage.fileBytes); break;
                case SortMode.PathAsc:     q = q.OrderBy(r => r.usage.assetPath, StringComparer.Ordinal); break;
                case SortMode.FormatAsc:   q = q.OrderBy(r => r.usage.extension).ThenByDescending(r => r.usage.fileBytes); break;
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
                var usages = TextureUsageGraph.Build(_rootFolder, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Texture Optimizer", msg, p));
                _rows = TextureOptimizationPlanner.Plan(usages);
                _lastScanUtc = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TextureOptimizer] Scan failed: {e}");
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
                    "No rows are currently approved. Use the Approve All button or tick individual rows first.",
                    "OK");
                return;
            }

            string confirm =
                $"You are about to re-encode and resize {approvedCount} textures under {_rootFolder}.\n\n"
                + "The original .tga/.tif/.psd files will be REPLACED with optimized .png/.jpg files. "
                + "Material and prefab references will be preserved.\n\n"
                + "Recommend: commit your current git state first so the change can be diffed and reverted.\n\n"
                + "Proceed?";
            if (!EditorUtility.DisplayDialog("Apply texture optimization", confirm, "Apply", "Cancel"))
                return;

            try
            {
                var result = TextureOptimizationExecutor.Apply(_rows, dryRun, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Texture Optimizer", msg, p));

                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(
                    "Texture Optimizer",
                    $"Processed: {result.processed}\nSucceeded: {result.succeeded}\nFailed: {result.failed}\n\n"
                    + $"Before: {FormatBytes(result.bytesBefore)}\n"
                    + $"After:  {FormatBytes(result.bytesAfter)}\n"
                    + $"Saved:  {FormatBytes(result.BytesSaved)}  ({result.PercentSaved:0.#}%)",
                    "OK");

                // Re-scan to refresh the table with the new state.
                RunScan();
            }
            catch (Exception e)
            {
                Debug.LogError($"[TextureOptimizer] Apply failed: {e}");
                EditorUtility.DisplayDialog("Apply failed", e.Message + "\n\nSee Console for details.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Set <see cref="TexturePlanRow.approved"/> on every row that
        /// isn't a hard skip. When <paramref name="onlyVisible"/> is
        /// provided, only those rows get touched — that's how the
        /// header-row "select-all" checkbox respects the current
        /// search/filter (so creators can search a subset and toggle
        /// just that subset).
        /// </summary>
        private void SetApprovedAll(bool approved, IList<TexturePlanRow> onlyVisible = null)
        {
            var target = onlyVisible ?? (IList<TexturePlanRow>)_rows;
            foreach (var r in target)
            {
                if (IsHardSkip(r)) continue;
                r.approved = approved;
                if (approved) r.skipReason = null;
            }
        }

        // ─── Utility ────────────────────────────────────────────────────

        private static string ShortDir(string assetPath)
        {
            string dir = Path.GetDirectoryName(assetPath) ?? "";
            // Trim the leading Assets/Content/{Game}/ for compact display.
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
