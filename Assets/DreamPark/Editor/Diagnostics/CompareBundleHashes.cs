#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace DreamPark.Diagnostics
{
    // Phase 0 validation spike for PATCH_DIFF_PLAN.md.
    //
    // Purpose
    // -------
    // The patch-diff plan assumes that when Unity churns a bundle's filename
    // hash because of the MonoScript dependency cascade, the bundle's *file
    // bytes* are still identical. The whole content-hash diff approach
    // depends on that being true. This window proves or disproves it
    // empirically before we commit to any production implementation.
    //
    // Workflow
    // --------
    //   1. Build Addressables once. Click "Snapshot 'Before'" — copies
    //      ServerData/ to ~/dreampark-bundle-spike/before/.
    //   2. Make a trivial C# edit (e.g. add `private int _unused;` to any
    //      content script). Save. Build Addressables again.
    //   3. Click "Snapshot 'After'" — copies ServerData/ to .../after/.
    //   4. Click "Compare". The results table tells you whether the
    //      assumption holds.
    //
    // What the table tells you
    // ------------------------
    //   - Same SHA: full plan works as-written with whole-file hashing.
    //   - SHA differs but only in the first few KB: Unity stamps the dep
    //     hash in a preamble. Production impl hashes (file bytes minus
    //     preamble). Still works, slightly more code.
    //   - SHA differs across many byte ranges: Unity rewrites the bundle
    //     payload. Production impl needs to parse the bundle format.
    //     Significant rethink required.
    //
    // This is a throwaway diagnostic. After the spike informs the plan we
    // can either delete it or keep it around as a regression-detection
    // tool for future Unity upgrades.
    public class CompareBundleHashes : EditorWindow
    {
        [MenuItem("DreamPark/Diagnostics/Bundle Hash Spike...", false, 1000)]
        public static void Open()
        {
            var w = GetWindow<CompareBundleHashes>("Bundle Hash Spike");
            w.minSize = new Vector2(720, 480);
            w.Show();
        }

        // Where snapshots get stashed. User-home so we never accidentally
        // include them in the project, and so multiple project clones can
        // share the snapshot dir if convenient.
        private static string SpikeRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "dreampark-bundle-spike");

        private static string SnapshotPath(string label) => Path.Combine(SpikeRoot, label);

        private Vector2 _scroll;
        private ComparisonReport _lastReport;
        private bool _includeUnityFolder = false;     // .unitypackage isn't a bundle; usually skip
        private bool _hashStrippedPreamble = true;    // also hash bytes past offset PREAMBLE_BYTES
        private bool _forceUncompressed = false;      // spike-only: override SmartGrouper's LZ4 to test LZ4-cascade hypothesis
        private bool _disableNonRecursive = false;    // spike-only: set NonRecursiveBuilding = false to embed MonoScripts per-bundle
        private const int kPreambleProbeBytes = 4096; // first 4 KB treated as "header"; tuneable

        // Bundle filename pattern: "<stem>_<32hex>.bundle"
        // Stem captures everything before the content-hash suffix so we can
        // pair the same logical bundle across two builds.
        private static readonly Regex BundleNamePattern = new Regex(
            @"^(?<stem>.+)_(?<hash>[a-f0-9]{32})\.bundle$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // -----------------------------------------------------------------
        // UI
        // -----------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Bundle Hash Spike — Phase 0 Validation", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Captures two snapshots of ServerData/ and compares paired bundles by content hash. " +
                "Use this to verify whether Unity-churned bundle filenames are byte-identical when " +
                "their semantic content hasn't changed.\n\n" +
                "1. Click 'Build (current platform)' → click 'Snapshot Before'\n" +
                "2. Make a trivial C# edit → click 'Build (current platform)' again → click 'Snapshot After'\n" +
                "3. Click 'Compare'",
                MessageType.Info);

            EditorGUILayout.Space();

            // Build trigger — saves the dev from leaving the window to fire
            // a build. Uses the active build target as-is; no profile
            // mutation (we don't want to disturb upload settings).
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;
            if (GUILayout.Button($"Build Addressables ({activeTarget})", GUILayout.Height(28)))
                BuildCurrentPlatform();

            _forceUncompressed = EditorGUILayout.ToggleLeft(
                "Force Uncompressed for this build (overrides Smart Grouper's LZ4 default — spike only)",
                _forceUncompressed);

            _disableNonRecursive = EditorGUILayout.ToggleLeft(
                "Disable NonRecursiveBuilding (embed MonoScripts per-bundle, no shared monoscripts.bundle — spike only)",
                _disableNonRecursive);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Snapshot 'Before'", GUILayout.Height(28)))
                    TakeSnapshot("before");
                if (GUILayout.Button("Snapshot 'After'", GUILayout.Height(28)))
                    TakeSnapshot("after");
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(!Directory.Exists(SnapshotPath("before"))
                                          || !Directory.Exists(SnapshotPath("after")));
                if (GUILayout.Button("Compare", GUILayout.Height(28)))
                    _lastReport = Compare(SnapshotPath("before"), SnapshotPath("after"));
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Open Snapshot Folder", GUILayout.Height(28)))
                    EditorUtility.RevealInFinder(SpikeRoot);
            }

            EditorGUILayout.Space();
            _includeUnityFolder = EditorGUILayout.ToggleLeft(
                "Include 'Unity' platform folder (rarely useful — .unitypackage isn't a bundle)",
                _includeUnityFolder);
            _hashStrippedPreamble = EditorGUILayout.ToggleLeft(
                $"Also hash file with first {kPreambleProbeBytes} bytes stripped (fallback probe)",
                _hashStrippedPreamble);

            EditorGUILayout.Space();
            DrawSnapshotStatus();
            EditorGUILayout.Space();

            if (_lastReport != null)
                DrawReport(_lastReport);
        }

        private void DrawSnapshotStatus()
        {
            EditorGUILayout.LabelField("Snapshots", EditorStyles.boldLabel);
            DrawSnapshotRow("Before", SnapshotPath("before"));
            DrawSnapshotRow("After ", SnapshotPath("after"));
        }

        private void DrawSnapshotRow(string label, string path)
        {
            if (!Directory.Exists(path))
            {
                EditorGUILayout.LabelField($"  {label}: not captured yet");
                return;
            }
            int bundles = 0;
            long bytes = 0;
            foreach (var f in Directory.GetFiles(path, "*.bundle", SearchOption.AllDirectories))
            {
                bundles++;
                bytes += new FileInfo(f).Length;
            }
            EditorGUILayout.LabelField($"  {label}: {bundles} bundles, {FormatBytes(bytes)} at {path}");
        }

        // -----------------------------------------------------------------
        // Build trigger
        // -----------------------------------------------------------------

        // Matches the real upload pipeline (ContentUploaderPanel) as closely
        // as the spike needs: applies the same monoscripts naming + player
        // version + Smart bundle grouping that production does, then runs
        // BuildPlayerContent. Skips namespace enforcement and the unitypackage
        // build since those don't affect bundle output.
        //
        // When _forceUncompressed is true, the schema override happens AFTER
        // ForceUpdateContent finishes (which would otherwise stomp our change
        // by re-running ConfigureBundleSchema's hardcoded LZ4 default) and
        // BEFORE BuildPlayerContent reads the schema.
        private void BuildCurrentPlatform()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[BundleSpike] No AddressableAssetSettings — open Addressables Groups window first.");
                return;
            }

            // Reuse whatever content title the user last selected in the
            // Content Uploader panel. Spike only makes sense in the context
            // of a real content title.
            string contentId = EditorPrefs.GetString("DreamPark.ContentUploader.LastContentId", "");
            if (string.IsNullOrEmpty(contentId))
            {
                EditorUtility.DisplayDialog(
                    "No contentId set",
                    "Open the Content Uploader panel and select a content title once. The spike reuses that selection.",
                    "OK");
                return;
            }

            // Capture the original NonRecursiveBuilding value so we can
            // restore it after the build — we don't want the spike to leave
            // the project's settings.asset modified.
            bool originalNonRecursive = settings.NonRecursiveBuilding;

            try
            {
                EditorUtility.DisplayProgressBar("Bundle Hash Spike", $"Applying Smart grouping for {contentId}...", 0.2f);

                // Same pre-build setup the production upload flow does.
                settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
                settings.MonoScriptBundleCustomNaming = contentId + "_";
                settings.OverridePlayerVersion = contentId;

                // Spike-only NonRecursiveBuilding override — when set to false,
                // Unity embeds MonoScript metadata directly into each bundle
                // that uses it instead of producing a shared monoscripts.bundle.
                // This removes the cross-bundle dep-hash that LZ4 amplifies.
                if (_disableNonRecursive)
                {
                    settings.NonRecursiveBuilding = false;
                    Debug.Log("[BundleSpike] NonRecursiveBuilding = false for this build (per-bundle embedded MonoScripts).");
                }

                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();

                // This is what actually runs SmartBundleGrouper (when Smart strategy
                // is active). Without it the build uses whatever group state happens
                // to be saved on disk.
                ContentProcessor.ForceUpdateContent(contentId);

                // Spike-only override: ForceUpdateContent (and SmartBundleGrouper inside
                // it) hardcodes Compression = LZ4 on every group's schema. To test the
                // "uncompressed bundles are stable across builds" hypothesis, override
                // every group's compression mode here, after the SDK has finished
                // configuring schemas but before Unity actually reads them in
                // BuildPlayerContent. The override is in-memory only — the next real
                // ForceUpdateContent (e.g. from a normal upload) restores LZ4.
                int overridden = 0;
                if (_forceUncompressed)
                {
                    foreach (var g in settings.groups)
                    {
                        if (g == null) continue;
                        var bag = g.GetSchema<BundledAssetGroupSchema>();
                        if (bag == null) continue;
                        bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.Uncompressed;
                        EditorUtility.SetDirty(g);
                        overridden++;
                    }
                    Debug.Log($"[BundleSpike] Forced Uncompressed on {overridden} group(s) before build.");
                }

                EditorUtility.DisplayProgressBar("Bundle Hash Spike", $"Building Addressables for {target}...", 0.6f);
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                EditorUtility.ClearProgressBar();

                if (!string.IsNullOrEmpty(result.Error))
                {
                    Debug.LogError($"[BundleSpike] Addressables build failed: {result.Error}");
                    return;
                }

                Debug.Log($"[BundleSpike] Build complete: {target}, contentId={contentId}, strategy={BundlingStrategyPrefs.Current}");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[BundleSpike] Build threw: {e}");
            }
            finally
            {
                // Restore NonRecursiveBuilding so the spike doesn't leave
                // the project's addressable settings modified after a run.
                if (_disableNonRecursive && settings != null && settings.NonRecursiveBuilding != originalNonRecursive)
                {
                    settings.NonRecursiveBuilding = originalNonRecursive;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[BundleSpike] Restored NonRecursiveBuilding = {originalNonRecursive}.");
                }
            }
        }

        // -----------------------------------------------------------------
        // Snapshot capture
        // -----------------------------------------------------------------

        private static void TakeSnapshot(string label)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string serverData = Path.Combine(projectRoot, "ServerData");
            if (!Directory.Exists(serverData))
            {
                EditorUtility.DisplayDialog(
                    "No ServerData found",
                    $"Couldn't find {serverData}.\nBuild Addressables first (Compile & Upload's 'Validate Build' button works too — it does a build without an upload).",
                    "OK");
                return;
            }

            string dest = SnapshotPath(label);
            try
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                Directory.CreateDirectory(dest);
                CopyDirectoryRecursive(serverData, dest);
                Debug.Log($"[BundleSpike] Snapshot '{label}' captured → {dest}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BundleSpike] Snapshot failed: {e}");
                EditorUtility.DisplayDialog("Snapshot failed", e.Message, "OK");
            }
        }

        private static void CopyDirectoryRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(src))
                CopyDirectoryRecursive(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }

        // -----------------------------------------------------------------
        // Comparison logic
        // -----------------------------------------------------------------

        // One row of the output table — represents a paired bundle across
        // the two snapshots.
        public class BundlePair
        {
            public string platform;
            public string stem;
            public string beforeFileName;
            public string afterFileName;
            public long beforeSize;
            public long afterSize;
            public string sha256Before;
            public string sha256After;
            public string sha256StrippedBefore;  // populated when a probe size matched
            public string sha256StrippedAfter;
            public int matchedStripBytes;        // the strip offset that first made stripped SHAs match (0 = none did)
            public long? firstDiffOffset;
            public long? lastDiffOffset;
            public long totalDiffBytes;

            public bool FilenameChanged => beforeFileName != afterFileName;
            public bool SizeIdentical => beforeSize == afterSize;
            public bool FullShaIdentical => sha256Before == sha256After;
            public bool StrippedShaIdentical =>
                !string.IsNullOrEmpty(sha256StrippedBefore)
                && sha256StrippedBefore == sha256StrippedAfter;
            public long BundleSize => Math.Max(beforeSize, afterSize);
            public double DiffPercent =>
                BundleSize > 0 ? 100.0 * totalDiffBytes / BundleSize : 0;
        }

        public class ComparisonReport
        {
            public List<BundlePair> pairs = new List<BundlePair>();
            public List<string> orphans = new List<string>();  // bundles in only one snapshot
            public DateTime generatedAtUtc;
        }

        private ComparisonReport Compare(string beforeDir, string afterDir)
        {
            var report = new ComparisonReport { generatedAtUtc = DateTime.UtcNow };

            // Index bundles in each snapshot by (platform, stem).
            var beforeIndex = IndexSnapshot(beforeDir);
            var afterIndex = IndexSnapshot(afterDir);

            // All (platform, stem) keys present in either snapshot.
            var allKeys = new HashSet<(string platform, string stem)>(beforeIndex.Keys);
            foreach (var k in afterIndex.Keys) allKeys.Add(k);

            int total = allKeys.Count;
            int processed = 0;

            try
            {
                foreach (var key in allKeys.OrderBy(k => k.platform).ThenBy(k => k.stem))
                {
                    processed++;
                    EditorUtility.DisplayProgressBar(
                        "Comparing bundles",
                        $"{key.platform}/{key.stem}",
                        (float)processed / Math.Max(1, total));

                    bool hasBefore = beforeIndex.TryGetValue(key, out var beforePath);
                    bool hasAfter = afterIndex.TryGetValue(key, out var afterPath);

                    if (!hasBefore || !hasAfter)
                    {
                        report.orphans.Add(
                            $"{key.platform}/{key.stem}  (present in {(hasBefore ? "Before" : "After")} only)");
                        continue;
                    }

                    var pair = BuildPair(key.platform, key.stem, beforePath, afterPath);
                    report.pairs.Add(pair);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // Persist the report next to the snapshots so the dev can keep it
            // around for the memo / decision write-up.
            try
            {
                string outPath = Path.Combine(SpikeRoot,
                    $"report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(outPath, FormatReportAsText(report));
                Debug.Log($"[BundleSpike] Report written to {outPath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BundleSpike] Could not save report file: {e.Message}");
            }

            return report;
        }

        private Dictionary<(string platform, string stem), string> IndexSnapshot(string root)
        {
            var index = new Dictionary<(string platform, string stem), string>();
            if (!Directory.Exists(root)) return index;

            foreach (var platformDir in Directory.GetDirectories(root))
            {
                string platform = Path.GetFileName(platformDir);
                if (!_includeUnityFolder && string.Equals(platform, "Unity", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var file in Directory.GetFiles(platformDir, "*.bundle", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    var m = BundleNamePattern.Match(name);
                    if (!m.Success) continue;        // skip files we can't parse
                    string stem = m.Groups["stem"].Value;
                    index[(platform, stem)] = file;
                }
            }
            return index;
        }

        private BundlePair BuildPair(string platform, string stem, string beforePath, string afterPath)
        {
            var pair = new BundlePair
            {
                platform = platform,
                stem = stem,
                beforeFileName = Path.GetFileName(beforePath),
                afterFileName = Path.GetFileName(afterPath),
                beforeSize = new FileInfo(beforePath).Length,
                afterSize = new FileInfo(afterPath).Length,
                sha256Before = Sha256OfFile(beforePath),
                sha256After = Sha256OfFile(afterPath),
            };

            if (pair.FullShaIdentical) return pair;  // no need to probe deeper

            // Probe a series of "strip the first N bytes" offsets. If any
            // matches, that's where the divergent preamble ends. Going up to
            // 1 MB catches even unusually large Unity dep-tables. We hash
            // smallest-first so we find the tightest matching offset, which
            // tells us how much preamble actually differs.
            int[] probes = { 4 * 1024, 16 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024 };
            foreach (var skip in probes)
            {
                if (skip >= pair.beforeSize || skip >= pair.afterSize) break;
                string sBefore = Sha256OfFile(beforePath, skipBytes: skip);
                string sAfter = Sha256OfFile(afterPath, skipBytes: skip);
                if (sBefore == sAfter)
                {
                    pair.matchedStripBytes = skip;
                    pair.sha256StrippedBefore = sBefore;
                    pair.sha256StrippedAfter = sAfter;
                    break;
                }
            }

            // Locate where the differences actually live.
            var (first, last, total) = FindDiffRange(beforePath, afterPath);
            pair.firstDiffOffset = first;
            pair.lastDiffOffset = last;
            pair.totalDiffBytes = total;
            return pair;
        }

        private static string Sha256OfFile(string path, long skipBytes = 0)
        {
            using var stream = File.OpenRead(path);
            if (skipBytes > 0 && skipBytes < stream.Length) stream.Seek(skipBytes, SeekOrigin.Begin);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Returns (firstDifferingOffset, lastDifferingOffset, totalDifferingBytes)
        // or (-1, -1, 0) when the files are identical. Streams both files in
        // 64 KB chunks; safe on the multi-hundred-MB bundles we care about.
        private static (long first, long last, long total) FindDiffRange(string a, string b)
        {
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);

            const int kBufSize = 64 * 1024;
            byte[] bufA = new byte[kBufSize];
            byte[] bufB = new byte[kBufSize];

            long firstDiff = -1;
            long lastDiff = -1;
            long totalDiff = 0;
            long pos = 0;

            while (true)
            {
                int readA = ReadFull(sa, bufA);
                int readB = ReadFull(sb, bufB);
                int common = Math.Min(readA, readB);

                for (int i = 0; i < common; i++)
                {
                    if (bufA[i] != bufB[i])
                    {
                        if (firstDiff < 0) firstDiff = pos + i;
                        lastDiff = pos + i;
                        totalDiff++;
                    }
                }

                if (readA != readB)
                {
                    // Tail of the longer file is implicitly all "different".
                    int diff = Math.Abs(readA - readB);
                    if (firstDiff < 0) firstDiff = pos + common;
                    lastDiff = pos + Math.Max(readA, readB) - 1;
                    totalDiff += diff;
                }

                if (readA == 0 && readB == 0) break;
                pos += common;
            }

            return (firstDiff, lastDiff, totalDiff);
        }

        private static int ReadFull(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        // -----------------------------------------------------------------
        // Report rendering
        // -----------------------------------------------------------------

        private void DrawReport(ComparisonReport report)
        {
            int identical = report.pairs.Count(p => p.FullShaIdentical);
            int preambleMatch = report.pairs.Count(p => !p.FullShaIdentical && p.StrippedShaIdentical);
            int trueChanges = report.pairs.Count - identical - preambleMatch;

            // The "patch size" the production uploader would actually ship:
            // every non-byte-identical bundle's "After" size, plus the size of
            // any bundles that exist only in After (newly-added content). The
            // saved bytes count is what byte-identical detection bought us.
            long patchBytes = report.pairs
                .Where(p => !p.FullShaIdentical)
                .Sum(p => p.afterSize);
            long savedBytes = report.pairs
                .Where(p => p.FullShaIdentical)
                .Sum(p => p.afterSize);
            long totalAfterBytes = report.pairs.Sum(p => p.afterSize);
            double patchPercent = totalAfterBytes > 0 ? 100.0 * patchBytes / totalAfterBytes : 0;

            EditorGUILayout.LabelField("Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"  Paired bundles:                                  {report.pairs.Count}");
            EditorGUILayout.LabelField($"  ✅ Byte-identical (no churn at all):              {identical}");
            EditorGUILayout.LabelField($"  ⚠️  Differ only in preamble (rest of file matches): {preambleMatch}");
            EditorGUILayout.LabelField($"  ❌ Differ throughout (real content change):       {trueChanges}");
            if (report.orphans.Count > 0)
                EditorGUILayout.LabelField($"  🚫 Bundles in only one snapshot:                  {report.orphans.Count}");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Patch size", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"  📦 Total content (After):                          {FormatBytes(totalAfterBytes)}");
            EditorGUILayout.LabelField($"  🚀 Would upload (changed bundles):                 {FormatBytes(patchBytes)}  ({patchPercent:F2}% of total)");
            EditorGUILayout.LabelField($"  💾 Saved by byte-identical match:                  {FormatBytes(savedBytes)}");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Interpretation", EditorStyles.boldLabel);
            string conclusion = ConcludeFor(report);
            EditorGUILayout.HelpBox(conclusion, MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Per-bundle details", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var p in report.pairs.OrderBy(p => p.platform).ThenBy(p => p.stem))
                DrawPairRow(p);
            if (report.orphans.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Orphans", EditorStyles.boldLabel);
                foreach (var o in report.orphans) EditorGUILayout.LabelField($"  {o}");
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawPairRow(BundlePair p)
        {
            string verdict;
            if (p.FullShaIdentical)
            {
                verdict = "byte-identical";
            }
            else if (p.StrippedShaIdentical)
            {
                verdict = $"preamble-only diff (rest matches after stripping {FormatBytes(p.matchedStripBytes)}). " +
                          $"{p.totalDiffBytes:N0} bytes differ across offsets {p.firstDiffOffset:N0}..{p.lastDiffOffset:N0} " +
                          $"({p.DiffPercent:F4}% of bundle)";
            }
            else
            {
                verdict = $"diffs throughout. {p.totalDiffBytes:N0} bytes differ " +
                          $"across offsets {p.firstDiffOffset:N0}..{p.lastDiffOffset:N0} " +
                          $"({p.DiffPercent:F4}% of {FormatBytes(p.BundleSize)})";
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{p.platform} / {p.stem}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  Before: {p.beforeFileName}  ({FormatBytes(p.beforeSize)})");
                EditorGUILayout.LabelField($"  After:  {p.afterFileName}  ({FormatBytes(p.afterSize)})");
                EditorGUILayout.LabelField($"  Verdict: {verdict}");
            }
        }

        private static string ConcludeFor(ComparisonReport report)
        {
            if (report.pairs.Count == 0)
                return "No paired bundles found. Did both snapshots capture the same platform set?";

            int identical = report.pairs.Count(p => p.FullShaIdentical);
            int preambleMatches = report.pairs.Count(p => !p.FullShaIdentical && p.StrippedShaIdentical);
            int trueChanges = report.pairs.Count - identical - preambleMatches;
            double byteIdenticalPct = 100.0 * identical / report.pairs.Count;

            // The right interpretation depends on what the user changed.
            // A serialization-touching C# change SHOULD legitimately invalidate
            // every bundle whose prefabs use that class — those are real
            // content changes, not cascade. So "many bundles changed" isn't
            // automatically a failure; it depends on whether they're the
            // bundles we'd expect to change.
            //
            // The heuristic: how concentrated are the byte-identical bundles?
            //   ≥70% byte-identical → cascade is bounded; only script-using
            //     bundles changed. Production diff via file-CRC works.
            //   30-70% byte-identical → mixed; worth confirming the changes
            //     correlate with script usage rather than being broad cascade.
            //   <30% byte-identical → likely broad cascade still in play.
            if (byteIdenticalPct >= 70)
            {
                return
                    $"✅ {identical}/{report.pairs.Count} bundles are byte-identical between builds. " +
                    $"{trueChanges} have real content diffs — typically these are the bundles whose " +
                    "prefabs use the C# class you modified, which is correct, expected behavior.\n\n" +
                    "The cascade is bounded: bundles that don't depend on the changed script stay " +
                    "byte-stable. Production diff can use file-CRC matching directly — no catalog " +
                    "rewriter, no bundle parsing.\n\n" +
                    "Sanity-check by spot-checking that the 'real diff' bundles do contain prefabs " +
                    "that reference the script you modified.";
            }

            if (byteIdenticalPct >= 30)
            {
                return
                    $"⚠️ Mixed result. {identical} byte-identical, {preambleMatches} preamble-only diff, " +
                    $"{trueChanges} with real diffs.\n\n" +
                    "Worth checking whether the 'real diff' bundles all use the script you changed " +
                    "(legitimate, expected) or if unrelated bundles are also changing (cascade still " +
                    "active). Look at which bundles diff vs. which don't, and cross-reference against " +
                    "what your edit actually touched.";
            }

            return
                $"❌ Most bundles ({trueChanges}/{report.pairs.Count}) show real content diffs. " +
                "Looks like a broad cascade is still in play. Possible causes: the C# change touched " +
                "something widely-referenced (e.g. a base class), monoscripts dep hash is still " +
                "embedded shared across bundles, or LZ4 is amplifying a small upstream diff.\n\n" +
                "Spot-check the per-bundle rows: if bundles unrelated to your edit show diffs, " +
                "the dep-hash cascade isn't fully neutralized yet.";
        }

        private static string FormatReportAsText(ComparisonReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Bundle Hash Spike Report");
            sb.AppendLine($"Generated: {report.generatedAtUtc:o}");
            sb.AppendLine();
            sb.AppendLine($"Paired bundles: {report.pairs.Count}");
            sb.AppendLine($"  byte-identical: {report.pairs.Count(p => p.FullShaIdentical)}");
            sb.AppendLine($"  preamble-only differ: {report.pairs.Count(p => !p.FullShaIdentical && p.StrippedShaIdentical)}");
            sb.AppendLine($"  true content diff: {report.pairs.Count(p => !p.FullShaIdentical && !p.StrippedShaIdentical)}");
            sb.AppendLine($"Orphans: {report.orphans.Count}");
            sb.AppendLine();

            long patchBytes = report.pairs.Where(p => !p.FullShaIdentical).Sum(p => p.afterSize);
            long savedBytes = report.pairs.Where(p => p.FullShaIdentical).Sum(p => p.afterSize);
            long totalAfter = report.pairs.Sum(p => p.afterSize);
            double patchPct = totalAfter > 0 ? 100.0 * patchBytes / totalAfter : 0;
            sb.AppendLine($"Patch size:");
            sb.AppendLine($"  total content (After):  {FormatBytes(totalAfter)}");
            sb.AppendLine($"  would upload:           {FormatBytes(patchBytes)}  ({patchPct:F2}% of total)");
            sb.AppendLine($"  saved by content match: {FormatBytes(savedBytes)}");
            sb.AppendLine();
            sb.AppendLine("## Pairs");
            foreach (var p in report.pairs.OrderBy(p => p.platform).ThenBy(p => p.stem))
            {
                sb.AppendLine();
                sb.AppendLine($"### {p.platform}/{p.stem}");
                sb.AppendLine($"  Before:    {p.beforeFileName} ({FormatBytes(p.beforeSize)})");
                sb.AppendLine($"  After:     {p.afterFileName} ({FormatBytes(p.afterSize)})");
                sb.AppendLine($"  SHA-full:  {p.sha256Before}");
                sb.AppendLine($"             {p.sha256After}");
                if (!string.IsNullOrEmpty(p.sha256StrippedBefore))
                {
                    sb.AppendLine($"  SHA-strip: {p.sha256StrippedBefore}");
                    sb.AppendLine($"             {p.sha256StrippedAfter}");
                }
                if (!p.FullShaIdentical)
                    sb.AppendLine($"  Diff:      {p.totalDiffBytes:N0} bytes, offsets {p.firstDiffOffset:N0}..{p.lastDiffOffset:N0}");
            }
            if (report.orphans.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Orphans");
                foreach (var o in report.orphans) sb.AppendLine($"  {o}");
            }
            return sb.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes; int u = 0;
            while (v >= 1024d && u < units.Length - 1) { v /= 1024d; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }
}
#endif
