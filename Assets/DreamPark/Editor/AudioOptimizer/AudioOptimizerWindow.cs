#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// The Audio Optimizer review window.
    ///
    /// Workflow:
    ///   1. User opens via DreamPark → Audio Optimizer...
    ///   2. Click "Scan" — the window builds an AudioUsageGraph over the
    ///      content folder, then feeds it through the Planner. ~3-10s.
    ///   3. The table shows every clip with a proposed re-encode +
    ///      importer settings. Header shows current size + projected
    ///      savings.
    ///   4. User reviews per-row, toggles individual rows, optionally
    ///      overrides compression format / sample rate / quality.
    ///   5. Click "Apply Selected" — Executor runs. WAV files become
    ///      OGGs on disk (GUIDs preserved); importer settings are
    ///      tightened; a final dialog reports savings.
    ///
    /// The window is intentionally a near-mirror of TextureOptimizerWindow
    /// so creators don't have to re-learn the mental model.
    /// </summary>
    public class AudioOptimizerWindow : EditorWindow
    {
        // ─── Menu ────────────────────────────────────────────────────────
        // Priority 122 lines up with the other optimizers: Texture at 120,
        // Animation at 121, Audio at 122. Same top-level slot keeps them
        // visually grouped.
        [MenuItem("DreamPark/Audio Optimizer...", false, 122)]
        public static void Open()
        {
            var w = GetWindow<AudioOptimizerWindow>("Audio Optimizer");
            w.minSize = new Vector2(1080, 600);
            w.Show();
        }

        // ─── State ──────────────────────────────────────────────────────

        private string _rootFolder = "Assets/Content";
        private List<AudioPlanRow> _rows = new List<AudioPlanRow>();
        private Vector2 _scroll;
        private string _search = "";
        private SortMode _sort = SortMode.SavingsDesc;
        private bool _showSkipped = true;
        private DateTime _lastScanUtc = DateTime.MinValue;

        private enum SortMode
        {
            SavingsDesc,
            SizeDesc,
            DurationDesc,
            PathAsc,
        }

        // ─── Window lifecycle ───────────────────────────────────────────

        private void OnEnable()
        {
            _rootFolder = EditorPrefs.GetString("DreamPark.AudioOptimizer.Root", _rootFolder);
            if (!AssetDatabase.IsValidFolder(_rootFolder))
                _rootFolder = AutoPickContentFolder();

            // First-use auto-install of OggVorbisEncoder. Mirrors the
            // Texture Optimizer's MagickNet flow — the installer is
            // careful to no-op when files are already on disk, so every
            // subsequent open is instant.
            EnsureEncoderInstalled();
        }

        // ─── Install-loop circuit breaker ───────────────────────────────
        // SessionState resets every Unity restart but survives domain
        // reloads. We use it to make sure auto-install runs at most ONCE
        // per editor session. The failure mode it prevents: if our
        // reflection bindings against OggVorbisEncoder are wrong,
        // IsAvailable will keep returning false, the install logic will
        // re-trigger on every window open, every install ends in a
        // domain reload, and the reopen hook reopens the window. That
        // would loop forever and look exactly like Unity freezing.
        // With this guard, at worst the user sees the install fail once,
        // gets the recovery card, and clicks Reinstall manually.
        private const string InstallAttemptedKey = "DreamPark.AudioOptimizer.InstallAttempted";

        private static void EnsureEncoderInstalled()
        {
            if (OggVorbisEncoderInstaller.IsInstalled() && OggVorbisEncoderBootstrap.IsAvailable) return;

            // Circuit breaker: only attempt auto-install once per session.
            if (SessionState.GetBool(InstallAttemptedKey, false)) return;

            // Don't auto-install if the user is doing something else.
            // Same defensive guard as the Magick installer — we don't
            // want a re-install to interrupt the user's flow.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || Application.isPlaying)
                return;

            // Latch BEFORE the install call so a throw inside InstallSync
            // doesn't leave us in a state where retry-on-reopen could
            // still loop. Once latched, the user has to manually click
            // the Reinstall button on the recovery card to retry.
            SessionState.SetBool(InstallAttemptedKey, true);

            try
            {
                OggVorbisEncoderInstaller.InstallSync((p, msg) =>
                    EditorUtility.DisplayProgressBar("Setting up Audio Optimizer", msg, p));
                OggVorbisEncoderBootstrap.Invalidate();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AudioOptimizer] OggVorbisEncoder install failed: {e}");
                EditorUtility.DisplayDialog(
                    "Audio Optimizer setup failed",
                    "Couldn't download OggVorbisEncoder on first use:\n\n" + e.Message +
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
        /// Recovery card when OggVorbisEncoder isn't loaded. Mirrors the
        /// Texture Optimizer's Magick.NET setup card — appears only when
        /// the bootstrap reports unavailable AFTER OnEnable already
        /// tried to install. Almost always means a partial install
        /// failed or no internet on first run.
        /// </summary>
        private void DrawSetupCardIfNeeded()
        {
            if (OggVorbisEncoderBootstrap.IsAvailable) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("⚠  OggVorbisEncoder not loaded", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Scanning works, and importer-only changes can still be applied — "
                    + "but WAV → OGG re-encoding needs OggVorbisEncoder. A previous auto-install "
                    + "may have failed midway and left Unity in a half-loaded state. Click the "
                    + "button to wipe the install folder and retry from a clean slate.",
                    EditorStyles.wordWrappedMiniLabel);

                if (GUILayout.Button("Reinstall OggVorbisEncoder (clean download, ~10s)", GUILayout.Height(26)))
                {
                    // Manual retry resets the per-session install latch so
                    // subsequent window opens won't be blocked by the
                    // circuit breaker if this install ultimately succeeds
                    // but takes a domain reload to surface.
                    SessionState.SetBool(InstallAttemptedKey, false);
                    try
                    {
                        OggVorbisEncoderInstaller.InstallSync((p, msg) =>
                            EditorUtility.DisplayProgressBar("Reinstalling OggVorbisEncoder", msg, p));
                        OggVorbisEncoderBootstrap.Invalidate();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[AudioOptimizer] Reinstall failed: {e}");
                        EditorUtility.DisplayDialog(
                            "Reinstall failed",
                            "OggVorbisEncoder reinstall failed:\n\n" + e.Message +
                            "\n\nSee Console for full details. If the failure repeats, "
                            + "close Unity and manually delete:\n\n"
                            + OggVorbisEncoderInstaller.Root + "\n\nThen reopen Unity.",
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
                EditorGUILayout.LabelField("Audio Optimizer", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Scans the content folder, re-encodes .wav files to .ogg Vorbis on disk, and "
                    + "tightens AudioImporter settings (sample rate, mono, load type) based on each "
                    + "clip's usage. Unity GUIDs are preserved across the .wav → .ogg rename so "
                    + "prefab AudioSource references stay intact.",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(2);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Content folder:", GUILayout.Width(100));
                    string newRoot = EditorGUILayout.TextField(_rootFolder);
                    if (newRoot != _rootFolder)
                    {
                        _rootFolder = newRoot;
                        EditorPrefs.SetString("DreamPark.AudioOptimizer.Root", _rootFolder);
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
                                EditorPrefs.SetString("DreamPark.AudioOptimizer.Root", _rootFolder);
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
                EditorGUILayout.HelpBox("Click 'Scan' to inventory audio clips under " + _rootFolder + ".", MessageType.Info);
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
                    $"Total clips: {_rows.Count}    |    Approved: {modified}    |    Skipped: {skipped}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"Audio size:  {FormatBytes(totalBefore)}    →    Projected: {FormatBytes(totalAfter)}    " +
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

        private void DrawTableHeader(List<AudioPlanRow> visibleRows)
        {
            // "Select-all" checkbox that respects the current search filter.
            // Same affordance as the Texture Optimizer — search "ui_" and
            // tick the header to bulk-approve just the UI subset.
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
                GUILayout.Label("", GUILayout.Width(44));               // play btn
                GUILayout.Label("Clip", EditorStyles.miniLabel, GUILayout.MinWidth(220));
                GUILayout.Label("Current", EditorStyles.miniLabel, GUILayout.Width(190));
                GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(18));
                GUILayout.Label("Target", EditorStyles.miniLabel, GUILayout.Width(240));
                GUILayout.Label("Savings", EditorStyles.miniLabel, GUILayout.Width(110));
                GUILayout.Label("Notes", EditorStyles.miniLabel, GUILayout.MinWidth(160));
            }
        }

        private void DrawRow(AudioPlanRow row)
        {
            bool isSkipped = !string.IsNullOrEmpty(row.skipReason);
            var bg = isSkipped ? new Color(0.6f, 0.6f, 0.6f, 0.08f)
                               : (row.approved ? new Color(0.3f, 0.7f, 0.3f, 0.05f) : Color.clear);
            var rect = EditorGUILayout.BeginVertical();
            if (bg.a > 0) EditorGUI.DrawRect(rect, bg);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(IsHardSkip(row)))
                {
                    bool wasApproved = row.approved;
                    row.approved = EditorGUILayout.Toggle(row.approved, GUILayout.Width(20));
                    if (row.approved != wasApproved && row.approved)
                        row.skipReason = null;
                }

                // Play button — drag-friendly: pings the clip on click and
                // previews it on double-click via Unity's AudioUtil
                // (private API; we ping for v1 since AudioUtil reflection
                // breaks across Unity versions).
                var playRect = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
                if (GUI.Button(playRect, "▶"))
                {
                    var clipAsset = AssetDatabase.LoadAssetAtPath<AudioClip>(row.usage.assetPath);
                    if (clipAsset != null) EditorGUIUtility.PingObject(clipAsset);
                }

                // Asset name (clickable to ping) + path
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(220)))
                {
                    if (GUILayout.Button(Path.GetFileName(row.usage.assetPath), EditorStyles.linkLabel))
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.assetPath));
                    EditorGUILayout.LabelField(
                        $"{ShortDir(row.usage.assetPath)}    {row.usage.durationSeconds:0.##}s    {row.usage.sourceChannels}ch",
                        EditorStyles.miniLabel);
                }

                // Current column
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(190)))
                {
                    EditorGUILayout.LabelField(
                        $"{row.usage.extension.TrimStart('.').ToUpperInvariant()}  {row.usage.sourceSampleRate / 1000f:0.#}kHz",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"{FormatBytes(row.usage.fileBytes)}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        $"{row.usage.currentCompression} · {row.usage.currentLoadType}",
                        EditorStyles.miniLabel);
                }

                GUILayout.Label("→", GUILayout.Width(18));

                // Target column — compression + sample rate + quality dropdowns
                using (new EditorGUI.DisabledScope(IsHardSkip(row)))
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var newComp = (AudioTargetCompression)EditorGUILayout.EnumPopup(row.targetCompression, GUILayout.Width(80));
                            if (newComp != row.targetCompression)
                            {
                                row.targetCompression = newComp;
                                RecomputeRowEstimate(row);
                            }
                            int curRate = row.targetSampleRate > 0 ? row.targetSampleRate : row.usage.sourceSampleRate;
                            int idx = Array.IndexOf(AudioSizingPolicy.AllowedSampleRates, curRate);
                            if (idx < 0) idx = 2; // 44100
                            int newIdx = EditorGUILayout.Popup(idx,
                                Array.ConvertAll(AudioSizingPolicy.AllowedSampleRates, s => (s / 1000) + "k"),
                                GUILayout.Width(60));
                            if (newIdx != idx)
                            {
                                int newRate = AudioSizingPolicy.AllowedSampleRates[newIdx];
                                row.targetSampleRate = newRate >= row.usage.sourceSampleRate ? 0 : newRate;
                                RecomputeRowEstimate(row);
                            }

                            bool wasMono = row.targetForceToMono;
                            row.targetForceToMono = GUILayout.Toggle(row.targetForceToMono, "mono", GUILayout.Width(60));
                            if (wasMono != row.targetForceToMono) RecomputeRowEstimate(row);
                        }

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Load:", GUILayout.Width(36));
                            var newLT = (AudioTargetLoadType)EditorGUILayout.EnumPopup(row.targetLoadType, GUILayout.Width(140));
                            row.targetLoadType = newLT;

                            if (row.targetCompression == AudioTargetCompression.Vorbis)
                            {
                                float wasQ = row.targetVorbisQuality;
                                row.targetVorbisQuality = EditorGUILayout.Slider(row.targetVorbisQuality, 0.1f, 1f);
                                if (Math.Abs(wasQ - row.targetVorbisQuality) > 0.001f) RecomputeRowEstimate(row);
                            }
                        }
                    }
                }

                // Savings
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(110)))
                {
                    if (row.WillBeModified)
                    {
                        EditorGUILayout.LabelField($"{FormatBytes(row.EstimatedSavedBytes)}", EditorStyles.boldLabel);
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
                        string usageLabel = row.usage.kind switch
                        {
                            AudioUsageKind.UI       => "UI",
                            AudioUsageKind.SFX      => "SFX",
                            AudioUsageKind.Voice    => "voice",
                            AudioUsageKind.Music    => "music",
                            AudioUsageKind.Ambient  => "ambient",
                            AudioUsageKind.Orphan   => "orphan",
                            _ => row.usage.kind.ToString(),
                        };
                        EditorGUILayout.LabelField(
                            $"{usageLabel} · {row.usage.audioSourceRefCount} ref{(row.usage.audioSourceRefCount == 1 ? "" : "s")}",
                            EditorStyles.miniLabel);
                        if (!string.IsNullOrEmpty(row.usage.usageExample))
                        {
                            if (GUILayout.Button(
                                $"↗ {Path.GetFileNameWithoutExtension(row.usage.usageExample)}",
                                EditorStyles.linkLabel))
                            {
                                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.usage.usageExample));
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
            var sep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(sep, new Color(0, 0, 0, 0.1f));
        }

        /// <summary>
        /// Re-run the policy's bytes estimator after the user nudges a
        /// per-row setting. Keeps the savings column accurate as the user
        /// experiments with overrides.
        /// </summary>
        private static void RecomputeRowEstimate(AudioPlanRow row)
        {
            int rate = row.targetSampleRate > 0 ? row.targetSampleRate : row.usage.sourceSampleRate;
            int channels = row.targetForceToMono ? 1 : row.usage.sourceChannels;
            row.estimatedAfterBytes = AudioSizingPolicy.EstimateBytes(
                row.targetCompression, rate, channels, row.usage.durationSeconds, row.targetVorbisQuality);
        }

        private static bool IsHardSkip(AudioPlanRow row) => row.hardSkip;

        // ─── Filter / sort ──────────────────────────────────────────────

        private List<AudioPlanRow> FilterAndSort(List<AudioPlanRow> rows)
        {
            IEnumerable<AudioPlanRow> q = rows;
            if (!_showSkipped) q = q.Where(r => string.IsNullOrEmpty(r.skipReason));
            if (!string.IsNullOrEmpty(_search))
            {
                string s = _search.Trim();
                q = q.Where(r => r.usage.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            switch (_sort)
            {
                case SortMode.SavingsDesc:  q = q.OrderByDescending(r => r.EstimatedSavedBytes); break;
                case SortMode.SizeDesc:     q = q.OrderByDescending(r => r.usage.fileBytes); break;
                case SortMode.DurationDesc: q = q.OrderByDescending(r => r.usage.durationSeconds); break;
                case SortMode.PathAsc:      q = q.OrderBy(r => r.usage.assetPath, StringComparer.Ordinal); break;
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
                var usages = AudioUsageGraph.Build(_rootFolder, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Audio Optimizer", msg, p));
                _rows = AudioOptimizationPlanner.Plan(usages);
                _lastScanUtc = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AudioOptimizer] Scan failed: {e}");
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
                    "No rows are currently approved. Use Check All or tick individual rows first.",
                    "OK");
                return;
            }

            int wavCount = _rows.Count(r => r.WillBeModified && r.sourceWillBeReplaced);

            string confirm =
                $"You are about to optimize {approvedCount} audio clips under {_rootFolder}.\n\n"
                + (wavCount > 0
                    ? $"{wavCount} .wav files will be REPLACED with .ogg files. "
                      + "AudioSource and Lua-by-name references will be preserved (GUIDs carried).\n\n"
                    : "Only importer settings will change — no .wav files are being re-encoded.\n\n")
                + "Recommend: commit your current git state first so the change can be diffed and reverted.\n\n"
                + "Proceed?";
            if (!EditorUtility.DisplayDialog("Apply audio optimization", confirm, "Apply", "Cancel"))
                return;

            try
            {
                var result = AudioOptimizationExecutor.Apply(_rows, dryRun, (p, msg) =>
                    EditorUtility.DisplayProgressBar("Audio Optimizer", msg, p));

                EditorUtility.ClearProgressBar();

                // Roll up first 5 failures into the dialog so users don't
                // have to dig through Console — same UX as the Texture
                // Optimizer's apply result dialog.
                string failedDetails = "";
                if (result.failed > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("\n\nFailed rows (first 5):");
                    int shown = 0;
                    foreach (var rr in result.rows)
                    {
                        if (rr.ok) continue;
                        sb.Append("\n  • ").Append(Path.GetFileName(rr.sourcePath))
                          .Append(" — ").Append(string.IsNullOrEmpty(rr.error) ? "unknown error" : rr.error);
                        if (++shown >= 5) break;
                    }
                    if (result.failed > shown)
                        sb.Append("\n  …and ").Append(result.failed - shown).Append(" more (see Console).");
                    failedDetails = sb.ToString();
                }

                EditorUtility.DisplayDialog(
                    "Audio Optimizer",
                    $"Processed: {result.processed}\nSucceeded: {result.succeeded}\nFailed: {result.failed}\n\n"
                    + $"Before: {FormatBytes(result.bytesBefore)}\n"
                    + $"After:  {FormatBytes(result.bytesAfter)}\n"
                    + $"Saved:  {FormatBytes(result.BytesSaved)}  ({result.PercentSaved:0.#}%)"
                    + failedDetails,
                    "OK");

                // Re-scan to refresh the table with the new state.
                RunScan();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AudioOptimizer] Apply failed: {e}");
                EditorUtility.DisplayDialog("Apply failed", e.Message + "\n\nSee Console for details.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Set <see cref="AudioPlanRow.approved"/> on every row that isn't
        /// a hard skip. When <paramref name="onlyVisible"/> is provided,
        /// only those rows get touched — that's how the header-row
        /// select-all respects the current search/filter.
        /// </summary>
        private void SetApprovedAll(bool approved, IList<AudioPlanRow> onlyVisible = null)
        {
            var target = onlyVisible ?? (IList<AudioPlanRow>)_rows;
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
