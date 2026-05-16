#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// Deduplicates assets that exist in BOTH ThirdPartyLocal and ThirdParty.
    ///
    /// Companion to ThirdPartySyncTool. That tool MOVES files from Local to
    /// Tracked, preserving GUIDs. This tool handles the leftover mess: assets
    /// that were independently imported into both Local and Tracked, ending up
    /// with two distinct GUIDs for byte-identical files. The Local copy gets
    /// pulled into bundles as an implicit dep (because SmartBundleGrouper
    /// excludes /ThirdPartyLocal/ from managed scope), which then duplicates
    /// across every consuming bundle — visible as warnings in the
    /// Addressables → Analyze → Check Duplicate Bundle Dependencies report.
    ///
    /// Algorithm:
    ///   1. Scan: enumerate every file under Assets/Content/{game}/ThirdPartyLocal/.
    ///      For each one, look for a same-name file inside the matching
    ///      Assets/Content/{game}/ThirdParty/{package}/ tree.
    ///   2. Classify:
    ///        Safe       — same filename, identical MD5. Auto-remappable.
    ///        Conflict   — same filename, DIFFERENT MD5. Probably one side
    ///                     was reimported / optimized. User reviews per-pair.
    ///        Orphan     — Local file has no same-named counterpart in Tracked.
    ///                     Tool can't help; user moves the file out of Local
    ///                     into proper content folders, or deletes it.
    ///   3. Reference scan: for each selected pair, grep YAML-text Unity assets
    ///      project-wide for `guid: <localGuid>` references.
    ///   4. Apply: rewrite consumer files' Local GUIDs → Tracked GUIDs (with
    ///      atomic file replace + backup), then AssetDatabase.DeleteAsset the
    ///      Local file. Prune empty Local folders. Save a JSON remap log to
    ///      Library/DreamPark/ThirdPartyLocalDedup/{timestamp}/ for traceability.
    ///
    /// Safety:
    ///   - Dry-run is the default; "Apply" is a separate, explicit button.
    ///   - Every consumer file rewritten gets copied to the timestamped backup
    ///     dir under Library/ before modification.
    ///   - Conflicts default to unchecked. Same-name-different-bytes is
    ///     usually an optimization difference and remapping silently can
    ///     change visual results.
    /// </summary>
    public class ThirdPartyLocalDeduplicator : EditorWindow
    {
        private const string ContentRootPath = "Assets/Content";
        private const string LocalFolderName = "ThirdPartyLocal";
        private const string TrackedFolderName = "ThirdParty";
        private const string ContentIdPrefKey = "DreamPark.ThirdPartyLocalDeduplicator.SelectedContentId";

        // YAML-text Unity asset extensions that may contain `guid: <hex>` refs.
        // Mirrors ThirdPartySyncTool.UnityFileExtensions so the two tools stay
        // consistent about what counts as a "consumer."
        private static readonly HashSet<string> ConsumerExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".mat", ".asset", ".controller", ".anim",
            ".overrideController", ".physicMaterial", ".physicsMaterial2D",
            ".cubemap", ".flare", ".renderTexture", ".mask", ".signal",
            ".playable", ".mixer", ".shadergraph", ".shadersubgraph",
            ".terrainlayer", ".brush", ".preset", ".lighting", ".spriteatlas",
            ".guiskin", ".fontsettings", ".meta",
        };

        // 32-hex GUID pattern Unity writes in .meta files.
        private static readonly Regex GuidLineRegex = new Regex(@"^\s*guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline);

        // ── UI / scan state ───────────────────────────────────────────────
        private static List<string> contentOptions = new List<string>();
        private static int selectedContentIndex = 0;
        private static string selectedContentId = "";
        private static Vector2 scrollPos;

        private ScanResult lastScan;
        private bool showSafe = true;
        private bool showConflicts = true;
        private bool showOrphans = false;
        private bool dryRun = true;

        // Sits under DreamPark/Troubleshooting/ — it's a recovery tool for
        // an asset-import edge case, not part of the normal authoring flow.
        // Priority 208 lands at the bottom of the existing Troubleshooting
        // group (200–207 are ContentProcessor's recovery utilities).
        [MenuItem("DreamPark/Troubleshooting/Deduplicate ThirdPartyLocal", false, 208)]
        public static void ShowWindow()
        {
            var w = GetWindow<ThirdPartyLocalDeduplicator>("Local Dedup");
            w.minSize = new Vector2(720, 480);
        }

        // ── Data model ────────────────────────────────────────────────────
        private enum PairKind { Safe, Conflict, Orphan }

        private class Pair
        {
            public PairKind kind;
            public string localPath;     // Assets/.../ThirdPartyLocal/...
            public string trackedPath;   // Assets/.../ThirdParty/...  (null for Orphan)
            public string localGuid;
            public string trackedGuid;   // null for Orphan
            public long sizeBytes;
            public bool selected;        // toggled in UI; defaults vary by kind
            public List<string> consumers;  // populated lazily during Scan, used in Apply
        }

        private class ScanResult
        {
            public List<Pair> safe = new List<Pair>();
            public List<Pair> conflicts = new List<Pair>();
            public List<Pair> orphans = new List<Pair>();
            public int consumerFilesTotal;        // unique consumer files across all pairs
            public long bytesIfApplied;           // bytes saved if every "safe" gets applied
            public string scannedContentId;
            public DateTime scannedAt;
        }

        // ── GUI ────────────────────────────────────────────────────────────
        private void OnEnable()
        {
            RefreshContentOptions();
            RestoreContentSelection();
        }

        private void OnFocus()
        {
            RefreshContentOptions();
        }

        private void OnGUI()
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField("ThirdPartyLocal Deduplicator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Finds assets that exist in BOTH ThirdPartyLocal and ThirdParty, " +
                "remaps GUID references from the Local copy to the Tracked copy, then " +
                "deletes the Local copy. Dry-run by default — no changes happen until " +
                "you click Apply.\n\n" +
                "Run this when Addressables Analyze flags ThirdPartyLocal assets as " +
                "duplicate bundle dependencies.",
                MessageType.Info);

            GUILayout.Space(6);
            DrawContentSelector();

            GUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(selectedContentId)))
                {
                    if (GUILayout.Button("Scan", GUILayout.Height(28), GUILayout.Width(120)))
                        RunScan();
                }
                dryRun = GUILayout.Toggle(dryRun, " Dry run (don't modify files)", GUILayout.Width(220));
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(lastScan == null || !AnythingSelected()))
                {
                    var color = GUI.backgroundColor;
                    GUI.backgroundColor = dryRun ? new Color(0.7f, 0.85f, 1f) : new Color(1f, 0.7f, 0.55f);
                    if (GUILayout.Button(dryRun ? "Apply (dry run)" : "Apply", GUILayout.Height(28), GUILayout.Width(180)))
                        RunApply();
                    GUI.backgroundColor = color;
                }
            }

            if (lastScan == null)
            {
                EditorGUILayout.LabelField("Click Scan to begin.", EditorStyles.miniLabel);
                return;
            }

            GUILayout.Space(8);
            DrawSummary();
            GUILayout.Space(4);
            DrawResults();
        }

        private void DrawContentSelector()
        {
            if (contentOptions.Count == 0)
            {
                EditorGUILayout.HelpBox($"No content folders found under {ContentRootPath}/.", MessageType.Warning);
                return;
            }
            int prev = selectedContentIndex;
            selectedContentIndex = EditorGUILayout.Popup("Content", selectedContentIndex, contentOptions.ToArray());
            if (selectedContentIndex < 0 || selectedContentIndex >= contentOptions.Count) selectedContentIndex = 0;
            selectedContentId = contentOptions[selectedContentIndex];
            if (prev != selectedContentIndex)
            {
                SaveContentSelection();
                lastScan = null;
            }
        }

        private void DrawSummary()
        {
            int safeSel = lastScan.safe.Count(p => p.selected);
            int confSel = lastScan.conflicts.Count(p => p.selected);
            long bytesSel =
                lastScan.safe.Where(p => p.selected).Sum(p => p.sizeBytes)
              + lastScan.conflicts.Where(p => p.selected).Sum(p => p.sizeBytes);
            EditorGUILayout.LabelField(
                $"Safe: {lastScan.safe.Count}  ({safeSel} selected)   " +
                $"Conflicts: {lastScan.conflicts.Count}  ({confSel} selected)   " +
                $"Orphans: {lastScan.orphans.Count}");
            EditorGUILayout.LabelField(
                $"Would free up: {FormatBytes(bytesSel)}   " +
                $"Consumers to rewrite: {lastScan.consumerFilesTotal}");
        }

        private void DrawResults()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            DrawBucket("Safe (identical MD5 — auto-remap)", ref showSafe, lastScan.safe, new Color(0.5f, 0.85f, 0.55f, 0.18f), defaultChecked: true);
            DrawBucket("Conflicts (different MD5 — review each)", ref showConflicts, lastScan.conflicts, new Color(0.95f, 0.7f, 0.3f, 0.20f), defaultChecked: false);
            DrawBucket("Orphans (no Tracked counterpart — cannot auto-remap)", ref showOrphans, lastScan.orphans, new Color(0.85f, 0.35f, 0.35f, 0.18f), defaultChecked: false, readOnly: true);

            EditorGUILayout.EndScrollView();
        }

        private void DrawBucket(string title, ref bool expanded, List<Pair> items, Color tint, bool defaultChecked, bool readOnly = false)
        {
            if (items.Count == 0) return;
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prev;

            using (new EditorGUILayout.HorizontalScope())
            {
                expanded = EditorGUILayout.Foldout(expanded, $"{title}   ({items.Count})", true);
                GUILayout.FlexibleSpace();
                if (!readOnly)
                {
                    if (GUILayout.Button("All", GUILayout.Width(50))) foreach (var p in items) p.selected = true;
                    if (GUILayout.Button("None", GUILayout.Width(50))) foreach (var p in items) p.selected = false;
                }
            }

            if (expanded)
            {
                foreach (var p in items)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (!readOnly)
                            p.selected = GUILayout.Toggle(p.selected, GUIContent.none, GUILayout.Width(18));
                        else
                            GUILayout.Space(20);

                        EditorGUILayout.LabelField(
                            new GUIContent(ShortenPath(p.localPath), p.localPath),
                            GUILayout.MinWidth(280));
                        EditorGUILayout.LabelField("→", GUILayout.Width(14));
                        EditorGUILayout.LabelField(
                            new GUIContent(p.trackedPath != null ? ShortenPath(p.trackedPath) : "(no match)", p.trackedPath ?? ""),
                            GUILayout.MinWidth(280));
                        EditorGUILayout.LabelField(FormatBytes(p.sizeBytes), GUILayout.Width(80));
                        if (GUILayout.Button("Ping", GUILayout.Width(50)))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p.localPath);
                            if (obj != null) EditorGUIUtility.PingObject(obj);
                        }
                    }
                }
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(4);
        }

        // ── Scan ───────────────────────────────────────────────────────────
        private void RunScan()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Local Dedup", "Enumerating ThirdPartyLocal...", 0f);
                var scan = ScanContent(selectedContentId);
                EditorUtility.DisplayProgressBar("Local Dedup", "Indexing consumer files...", 0.6f);
                IndexConsumers(scan);
                lastScan = scan;
                Debug.Log($"[Local Dedup] Scan complete for '{selectedContentId}': " +
                          $"{scan.safe.Count} safe, {scan.conflicts.Count} conflicts, {scan.orphans.Count} orphans, " +
                          $"{scan.consumerFilesTotal} unique consumer files.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private ScanResult ScanContent(string contentId)
        {
            var result = new ScanResult { scannedContentId = contentId, scannedAt = DateTime.Now };

            string localRoot = $"{ContentRootPath}/{contentId}/{LocalFolderName}";
            string trackedRoot = $"{ContentRootPath}/{contentId}/{TrackedFolderName}";
            if (!AssetDatabase.IsValidFolder(localRoot))
            {
                Debug.LogWarning($"[Local Dedup] No ThirdPartyLocal folder at {localRoot}.");
                return result;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string localRootFs = Path.Combine(projectRoot, localRoot.Replace('/', Path.DirectorySeparatorChar));
            string trackedRootFs = Path.Combine(projectRoot, trackedRoot.Replace('/', Path.DirectorySeparatorChar));

            // Walk Local. For each top-level package folder, build an index of
            // Tracked files (filename → list of absolute paths) inside the
            // matching package, then resolve each Local file against it.
            var localPackages = Directory.Exists(localRootFs)
                ? Directory.GetDirectories(localRootFs).Select(d => Path.GetFileName(d)).ToArray()
                : Array.Empty<string>();

            foreach (var pkg in localPackages)
            {
                string localPkgFs = Path.Combine(localRootFs, pkg);
                string trackedPkgFs = Path.Combine(trackedRootFs, pkg);

                // Index Tracked package by lowercased filename.
                var trackedByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(trackedPkgFs))
                {
                    foreach (var f in Directory.EnumerateFiles(trackedPkgFs, "*", SearchOption.AllDirectories))
                    {
                        if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        string name = Path.GetFileName(f);
                        if (!trackedByName.TryGetValue(name, out var list))
                            trackedByName[name] = list = new List<string>();
                        list.Add(f);
                    }
                }

                foreach (var localFs in Directory.EnumerateFiles(localPkgFs, "*", SearchOption.AllDirectories))
                {
                    if (localFs.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string localAssetPath = ToAssetPath(localFs, projectRoot);
                    string localMeta = localFs + ".meta";
                    if (!File.Exists(localMeta)) continue; // skip files Unity hasn't imported

                    long size = SafeSize(localFs);
                    string filename = Path.GetFileName(localFs);

                    // Look up Tracked candidates by filename.
                    if (!trackedByName.TryGetValue(filename, out var candidates) || candidates.Count == 0)
                    {
                        result.orphans.Add(new Pair {
                            kind = PairKind.Orphan,
                            localPath = localAssetPath,
                            trackedPath = null,
                            localGuid = ReadGuid(localMeta),
                            trackedGuid = null,
                            sizeBytes = size,
                            selected = false,
                        });
                        continue;
                    }

                    // Pick the best candidate: prefer one with matching MD5;
                    // otherwise pick the first (deterministic by enumeration
                    // order). MD5 is computed lazily.
                    string localMd5 = ComputeMd5(localFs);
                    string chosen = null;
                    string chosenMd5 = null;
                    foreach (var c in candidates)
                    {
                        string cm = ComputeMd5(c);
                        if (cm == localMd5) { chosen = c; chosenMd5 = cm; break; }
                    }
                    if (chosen == null) { chosen = candidates[0]; chosenMd5 = ComputeMd5(chosen); }

                    string trackedAssetPath = ToAssetPath(chosen, projectRoot);
                    string trackedMeta = chosen + ".meta";
                    if (!File.Exists(trackedMeta))
                    {
                        // Tracked file present but Unity hasn't imported it
                        // (no .meta). Treat as if no tracked counterpart.
                        result.orphans.Add(new Pair {
                            kind = PairKind.Orphan,
                            localPath = localAssetPath,
                            localGuid = ReadGuid(localMeta),
                            sizeBytes = size,
                        });
                        continue;
                    }

                    var pair = new Pair {
                        localPath = localAssetPath,
                        trackedPath = trackedAssetPath,
                        localGuid = ReadGuid(localMeta),
                        trackedGuid = ReadGuid(trackedMeta),
                        sizeBytes = size,
                    };

                    if (pair.localGuid == null || pair.trackedGuid == null)
                    {
                        // Defensive — meta missing GUID line means file is malformed.
                        // Park in orphans bucket so the user notices.
                        pair.kind = PairKind.Orphan;
                        pair.trackedPath = null;
                        pair.trackedGuid = null;
                        result.orphans.Add(pair);
                        continue;
                    }

                    if (localMd5 == chosenMd5)
                    {
                        pair.kind = PairKind.Safe;
                        pair.selected = true;        // default-checked
                        result.safe.Add(pair);
                    }
                    else
                    {
                        pair.kind = PairKind.Conflict;
                        pair.selected = false;       // default-unchecked
                        result.conflicts.Add(pair);
                    }
                }
            }

            // Sort each bucket by Local path for stable display.
            result.safe.Sort((a, b) => string.Compare(a.localPath, b.localPath, StringComparison.OrdinalIgnoreCase));
            result.conflicts.Sort((a, b) => string.Compare(a.localPath, b.localPath, StringComparison.OrdinalIgnoreCase));
            result.orphans.Sort((a, b) => string.Compare(a.localPath, b.localPath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // For every pair, find which Unity YAML-text assets reference the
        // Local GUID. We do ONE project-wide grep pass and accumulate hits
        // into each pair's consumers list — far cheaper than per-pair file
        // walks for large pair counts.
        private void IndexConsumers(ScanResult scan)
        {
            var allPairs = scan.safe.Concat(scan.conflicts).ToList();
            var byGuid = allPairs.ToDictionary(p => p.localGuid, p => p);
            foreach (var p in allPairs) p.consumers = new List<string>();

            if (byGuid.Count == 0) { scan.consumerFilesTotal = 0; return; }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string assetsRoot = Application.dataPath;
            var uniqueConsumerFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int processed = 0;
            int total = byGuid.Count;
            foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                processed++;
                if ((processed & 4095) == 0)
                    EditorUtility.DisplayProgressBar("Local Dedup", "Scanning project for GUID references…",
                        0.6f + 0.3f * Mathf.Min(1f, processed / 100000f));

                string ext = Path.GetExtension(file);
                if (!ConsumerExtensions.Contains(ext)) continue;

                // Skip anything inside Local — those files are about to be
                // deleted anyway, and rewriting them just wastes IO.
                if (file.IndexOf($"{Path.DirectorySeparatorChar}{LocalFolderName}{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // Skip the Tracked .meta files themselves — they own the
                // GUIDs we're remapping to. Defensive; they wouldn't contain
                // the Local GUID anyway.
                string content;
                try { content = File.ReadAllText(file); }
                catch { continue; }

                foreach (var m in GuidLineRegex.Matches(content).Cast<Match>())
                {
                    string g = m.Groups[1].Value.ToLowerInvariant();
                    if (!byGuid.TryGetValue(g, out var pair)) continue;
                    pair.consumers.Add(file);
                    uniqueConsumerFiles.Add(file);
                }
            }

            // De-dupe per-pair (a single file can contain N refs to the same GUID).
            foreach (var p in allPairs)
                p.consumers = p.consumers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            scan.consumerFilesTotal = uniqueConsumerFiles.Count;
        }

        // ── Apply ──────────────────────────────────────────────────────────
        private bool AnythingSelected()
        {
            if (lastScan == null) return false;
            return lastScan.safe.Any(p => p.selected) || lastScan.conflicts.Any(p => p.selected);
        }

        private void RunApply()
        {
            if (lastScan == null) return;
            var toApply = lastScan.safe.Concat(lastScan.conflicts).Where(p => p.selected).ToList();
            if (toApply.Count == 0)
            {
                Debug.LogWarning("[Local Dedup] Nothing selected.");
                return;
            }

            // Build remap table.
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in toApply)
            {
                if (p.localGuid == null || p.trackedGuid == null) continue;
                if (string.Equals(p.localGuid, p.trackedGuid, StringComparison.OrdinalIgnoreCase)) continue;
                remap[p.localGuid] = p.trackedGuid;
            }

            // Confirm.
            string verb = dryRun ? "Preview" : "Apply";
            int consumerCount = toApply.SelectMany(p => p.consumers ?? new List<string>())
                                       .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (!EditorUtility.DisplayDialog(
                    $"{verb}: ThirdPartyLocal Dedup",
                    $"{toApply.Count} pair(s) to remap.\n" +
                    $"{consumerCount} consumer file(s) will be rewritten.\n" +
                    $"{toApply.Count} Local asset(s) will be deleted.\n\n" +
                    (dryRun
                        ? "DRY RUN — no files will actually be changed."
                        : "This will MODIFY files on disk. A backup is saved to Library/DreamPark/ThirdPartyLocalDedup/."),
                    dryRun ? "Run dry run" : "Apply",
                    "Cancel"))
            {
                return;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string runDir = Path.Combine(projectRoot, "Library", "DreamPark", "ThirdPartyLocalDedup", runStamp);
            string backupDir = Path.Combine(runDir, "backup");
            if (!dryRun) Directory.CreateDirectory(backupDir);

            int filesRewritten = 0;
            int guidHitsRewritten = 0;
            int assetsDeleted = 0;
            int failures = 0;
            var log = new StringBuilder();
            log.AppendLine($"# DreamPark ThirdPartyLocal Dedup");
            log.AppendLine($"# Content:  {lastScan.scannedContentId}");
            log.AppendLine($"# Scanned:  {lastScan.scannedAt:O}");
            log.AppendLine($"# Applied:  {DateTime.Now:O}");
            log.AppendLine($"# DryRun:   {dryRun}");
            log.AppendLine();
            log.AppendLine($"# Remap pairs ({remap.Count}):");
            foreach (var p in toApply)
                log.AppendLine($"REMAP\t{p.localGuid}\t->\t{p.trackedGuid}\t{p.localPath}\t=>\t{p.trackedPath}");
            log.AppendLine();

            try
            {
                // Pass 1: rewrite consumer files.
                var consumerFiles = toApply.SelectMany(p => p.consumers ?? new List<string>())
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList();
                for (int i = 0; i < consumerFiles.Count; i++)
                {
                    var file = consumerFiles[i];
                    EditorUtility.DisplayProgressBar("Local Dedup",
                        $"Rewriting {Path.GetFileName(file)}", (float)i / Mathf.Max(1, consumerFiles.Count));

                    string content;
                    try { content = File.ReadAllText(file); }
                    catch (Exception e) { Debug.LogWarning($"[Local Dedup] Read failed {file}: {e.Message}"); failures++; continue; }

                    int hitsThisFile = 0;
                    string newContent = GuidLineRegex.Replace(content, match =>
                    {
                        string g = match.Groups[1].Value.ToLowerInvariant();
                        if (remap.TryGetValue(g, out var newG))
                        {
                            hitsThisFile++;
                            return match.Value.Replace(match.Groups[1].Value, newG);
                        }
                        return match.Value;
                    });

                    if (hitsThisFile == 0 || string.Equals(content, newContent, StringComparison.Ordinal))
                        continue;

                    log.AppendLine($"REWRITE\t{hitsThisFile}\t{ToAssetPath(file, projectRoot)}");
                    if (!dryRun)
                    {
                        try
                        {
                            // Backup with a path that mirrors the asset path
                            // so the user can find each file's original easily.
                            string rel = ToAssetPath(file, projectRoot);
                            string backupPath = Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                            File.Copy(file, backupPath, overwrite: true);

                            // Atomic write: temp + replace.
                            string tmp = file + ".tpl_dedup_tmp";
                            File.WriteAllText(tmp, newContent);
                            File.Replace(tmp, file, null);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[Local Dedup] Write failed {file}: {e.Message}");
                            log.AppendLine($"FAIL_REWRITE\t{file}\t{e.Message}");
                            failures++;
                            continue;
                        }
                    }
                    filesRewritten++;
                    guidHitsRewritten += hitsThisFile;
                }

                // Pass 2: delete Local assets.
                for (int i = 0; i < toApply.Count; i++)
                {
                    var p = toApply[i];
                    EditorUtility.DisplayProgressBar("Local Dedup",
                        $"Deleting {Path.GetFileName(p.localPath)}", (float)i / Mathf.Max(1, toApply.Count));

                    log.AppendLine($"DELETE\t{p.localPath}");
                    if (!dryRun)
                    {
                        bool ok = AssetDatabase.DeleteAsset(p.localPath);
                        if (!ok)
                        {
                            Debug.LogError($"[Local Dedup] DeleteAsset failed for {p.localPath}.");
                            log.AppendLine($"FAIL_DELETE\t{p.localPath}");
                            failures++;
                            continue;
                        }
                    }
                    assetsDeleted++;
                }

                // Pass 3: prune empty Local subfolders.
                if (!dryRun)
                {
                    string localRoot = $"{ContentRootPath}/{lastScan.scannedContentId}/{LocalFolderName}";
                    string localRootFs = Path.Combine(projectRoot, localRoot.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(localRootFs))
                        PruneEmptyDirs(localRootFs, log);
                }

                // Pass 4: refresh and persist log.
                if (!dryRun)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    Directory.CreateDirectory(runDir);
                    File.WriteAllText(Path.Combine(runDir, "remap.log"), log.ToString());
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string verbDone = dryRun ? "Dry run complete" : "Apply complete";
            Debug.Log($"[Local Dedup] {verbDone}. " +
                      $"Pairs={toApply.Count}, FilesRewritten={filesRewritten}, GuidHits={guidHitsRewritten}, " +
                      $"AssetsDeleted={assetsDeleted}, Failures={failures}.");

            if (!dryRun)
            {
                // Re-scan so the UI reflects the new state.
                lastScan = null;
            }

            EditorUtility.DisplayDialog($"{verbDone}",
                $"Pairs:           {toApply.Count}\n" +
                $"Files rewritten: {filesRewritten}\n" +
                $"GUID hits:       {guidHitsRewritten}\n" +
                $"Assets deleted:  {assetsDeleted}\n" +
                $"Failures:        {failures}\n\n" +
                (dryRun
                    ? "Dry run — no changes were made."
                    : $"Backup + log: Library/DreamPark/ThirdPartyLocalDedup/{runStamp}/"),
                "OK");
        }

        // Walk the directory tree post-order and remove any leaf directory
        // that's empty (or contains only its .meta). Doesn't touch the root
        // itself even if empty — leaving the empty `ThirdPartyLocal/` folder
        // is harmless and means future imports still land in the expected
        // place.
        private static void PruneEmptyDirs(string root, StringBuilder log)
        {
            foreach (var sub in Directory.GetDirectories(root))
                PruneEmptyDirs(sub, log);

            try
            {
                var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
                if (entries.Length == 0)
                {
                    Directory.Delete(root);
                    log.AppendLine($"PRUNE_DIR\t{root}");
                    string meta = root + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Local Dedup] Prune failed for {root}: {e.Message}");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────
        private static void RefreshContentOptions()
        {
            contentOptions.Clear();
            if (!Directory.Exists(ContentRootPath)) return;
            foreach (var d in Directory.GetDirectories(ContentRootPath))
                contentOptions.Add(Path.GetFileName(d));
            contentOptions.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void RestoreContentSelection()
        {
            string saved = EditorPrefs.GetString(ContentIdPrefKey, "");
            int idx = contentOptions.IndexOf(saved);
            selectedContentIndex = idx >= 0 ? idx : 0;
            if (contentOptions.Count > 0)
                selectedContentId = contentOptions[selectedContentIndex];
        }

        private void SaveContentSelection()
        {
            EditorPrefs.SetString(ContentIdPrefKey, selectedContentId);
        }

        private static string ReadGuid(string metaPath)
        {
            try
            {
                string text = File.ReadAllText(metaPath);
                var m = GuidLineRegex.Match(text);
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
            }
            catch { return null; }
        }

        private static string ComputeMd5(string path)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var fs = File.OpenRead(path))
                {
                    var hash = md5.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static long SafeSize(string fs)
        {
            try { return new FileInfo(fs).Length; } catch { return 0; }
        }

        private static string ToAssetPath(string fsPath, string projectRoot)
        {
            string norm = fsPath.Replace('\\', '/');
            string rootNorm = projectRoot.Replace('\\', '/') + "/";
            if (norm.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase))
                norm = norm.Substring(rootNorm.Length);
            return norm;
        }

        private static string ShortenPath(string assetPath)
        {
            // The interesting part is everything after the package root.
            int idx = assetPath.IndexOf($"/{LocalFolderName}/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = assetPath.IndexOf($"/{TrackedFolderName}/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return "…" + assetPath.Substring(idx);
            return assetPath;
        }

        private static string FormatBytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:0.0} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.0} MB";
            return $"{b / (1024.0 * 1024 * 1024):0.00} GB";
        }
    }
}
#endif
