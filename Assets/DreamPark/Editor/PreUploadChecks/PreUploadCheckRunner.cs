#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    // Orchestrates the check suite, applies the ignore list, and caches results for
    // the Content Uploader's tile badges.
    //
    // CONTRACT, copied verbatim from LuaSurfaceGate:
    //   "Never throws: a broken check must not be able to block shipping."
    //
    // Every check runs inside its own try/catch. A check that throws becomes an
    // Errored result — a grey row in the popup and a console warning — and
    // contributes zero blocking findings. That is the whole point: the failure mode
    // of this system must be "we didn't check", never "you can't ship".
    public static class PreUploadCheckRunner
    {
        // Registration order is display order in the popup. Blocking checks first so
        // the thing that will actually stop the upload is the thing you read first.
        public static IReadOnlyList<IPreUploadCheck> AllChecks
        {
            get
            {
                return new IPreUploadCheck[]
                {
                    new Checks.DuplicateNamesCheck(),
                    new Checks.SunLightCheck(),
                    new Checks.MetaOcclusionCheck(),
                    new Checks.SceneOverridesCheck(),
                    new Checks.OutsideContentFolderCheck(),
                };
            }
        }

        // ---------------------------------------------------------------------
        // Cache — feeds the uploader's tile badges without rescanning every repaint.

        private static readonly Dictionary<string, PreUploadReport> cache =
            new Dictionary<string, PreUploadReport>(StringComparer.Ordinal);

        // Fired after a scan completes so the panel can repaint its badges.
        public static event Action ReportChanged;

        public static PreUploadReport CachedReportFor(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            PreUploadReport report;
            return cache.TryGetValue(contentId, out report) ? report : null;
        }

        public static void InvalidateCache(string contentId = null)
        {
            if (string.IsNullOrEmpty(contentId)) cache.Clear();
            else cache.Remove(contentId);

            // Notify, or the panel keeps drawing badges for findings the user just
            // ignored until some unrelated event happens to fire.
            RaiseChanged();
        }

        // Asset path → worst active severity + tooltip, for the tile badges.
        // Built once per scan and read from the panel's OnGUI, so the answer is stable
        // across the Layout and Repaint passes. That matters: GUI.Button consumes a
        // control id, and a badge that appears in one pass but not the other shifts
        // the id stream and corrupts every later control in the card.
        public static Dictionary<string, KeyValuePair<CheckSeverity, string>> BuildBadgeMap(
            PreUploadReport report)
        {
            var map = new Dictionary<string, KeyValuePair<CheckSeverity, string>>(
                StringComparer.OrdinalIgnoreCase);
            if (report == null) return map;

            foreach (var group in report.ActiveFindings
                         .Where(f => !string.IsNullOrEmpty(f.assetPath)
                                  && f.severity >= CheckSeverity.Warning)
                         .GroupBy(f => f.assetPath, StringComparer.OrdinalIgnoreCase))
            {
                var findings = group.ToList();
                var worst = findings.Max(f => f.severity);

                var first = findings.OrderByDescending(f => f.severity).First();
                string tooltip = Truncate(first.detail ?? first.title, 220);
                if (findings.Count > 1)
                    tooltip += $"\n\n(+{findings.Count - 1} more finding{(findings.Count == 2 ? "" : "s")})";
                tooltip += "\n\nClick to review.";

                map[group.Key] = new KeyValuePair<CheckSeverity, string>(worst, tooltip);
            }

            return map;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "");
            return s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        }

        // ---------------------------------------------------------------------
        // Running

        public static PreUploadReport Run(
            string contentId,
            IEnumerable<IPreUploadCheck> checks,
            Action<float, string> onProgress,
            bool scenesAreSaved,
            bool advisoryOnly)
        {
            return Run(contentId, checks, onProgress, scenesAreSaved, advisoryOnly, publishToCache: true);
        }

        private static PreUploadReport Run(
            string contentId,
            IEnumerable<IPreUploadCheck> checks,
            Action<float, string> onProgress,
            bool scenesAreSaved,
            bool advisoryOnly,
            bool publishToCache)
        {
            var report = new PreUploadReport { contentId = contentId };

            List<ContentRootInfo> roots;
            try
            {
                roots = ContentRootScanner.Scan(contentId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Pre-upload checks could not enumerate content roots: {e}");
                if (publishToCache) { cache[contentId ?? ""] = report; RaiseChanged(); }
                return report;
            }

            // Read the ignore list exactly once for the whole run.
            HashSet<string> ignoreKeys = null;
            try
            {
                ignoreKeys = new HashSet<string>(
                    PreUploadIgnoreStore.All(contentId).Select(IgnoreKeyOf),
                    StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                // An unreadable ignore file must not turn into a blocked upload OR a
                // silently suppressed finding. Nothing is ignored, and we say why.
                Debug.LogWarning($"[DreamPark] Could not read the pre-upload ignore list: {e.Message}");
            }

            var list = checks.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var check = list[i];

                if (advisoryOnly && !check.RunsInAdvisoryScan)
                {
                    report.results.Add(CheckResult.Skipped(check.Id,
                        "Not run automatically — open Pre-Upload Checks and press Run, or start an upload."));
                    continue;
                }

                float baseT = list.Count == 0 ? 0f : (float)i / list.Count;
                float span = list.Count == 0 ? 1f : 1f / list.Count;

                var ctx = new PreUploadCheckContext
                {
                    contentId = contentId,
                    contentRoot = ContentRootScanner.RootFor(contentId),
                    roots = roots,
                    scenesAreSaved = scenesAreSaved,
                    onProgress = (t, msg) =>
                    {
                        if (onProgress != null)
                            onProgress(baseT + Mathf.Clamp01(t) * span, msg);
                    },
                };

                CheckResult result;
                try
                {
                    result = check.Run(ctx) ?? CheckResult.Clean(check.Id);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Pre-upload check '{check.Id}' failed: {e}");
                    result = CheckResult.Errored(check.Id, e.Message);
                }

                // Stamp identity the checks shouldn't have to remember, and apply the
                // ignore list centrally so every check gets the behaviour for free.
                foreach (var f in result.findings)
                {
                    if (string.IsNullOrEmpty(f.checkId)) f.checkId = check.Id;

                    if (string.IsNullOrEmpty(f.assetGuid) && !string.IsNullOrEmpty(f.assetPath))
                        f.assetGuid = AssetDatabase.AssetPathToGUID(f.assetPath);

                    // Matched against a set loaded ONCE per run. Calling
                    // PreUploadIgnoreStore.IsIgnored per finding re-reads and re-parses
                    // the whole JSON file each time — 300 findings meant 300 file reads.
                    f.isIgnored = ignoreKeys != null && ignoreKeys.Contains(IgnoreKeyOf(f));
                }

                report.results.Add(result);
            }

            // An advisory pass only runs the cheap checks, so publishing it verbatim
            // would blank out whatever a previous full run had found for the expensive
            // ones — tile badges for scene overrides and occlusion would vanish, and a
            // gate popup holding the old report would be handed a degraded one. Carry
            // the previous real results forward instead.
            if (publishToCache && advisoryOnly)
            {
                PreUploadReport previous;
                if (cache.TryGetValue(contentId ?? "", out previous) && previous != null)
                {
                    for (int r = 0; r < report.results.Count; r++)
                    {
                        if (report.results[r].outcome != CheckOutcome.Skipped) continue;

                        var carried = previous.results
                            .FirstOrDefault(p => p.checkId == report.results[r].checkId
                                              && p.outcome != CheckOutcome.Skipped);
                        if (carried != null) report.results[r] = carried;
                    }
                }
            }

            if (publishToCache)
            {
                cache[contentId ?? ""] = report;
                RaiseChanged();
            }
            return report;
        }

        private static string IgnoreKeyOf(Finding f)
        {
            return f.checkId + "|" + (f.assetGuid ?? "") + "|" + (f.subKey ?? "");
        }

        private static string IgnoreKeyOf(PreUploadIgnoreStore.Entry e)
        {
            return e.checkId + "|" + (e.assetGuid ?? "") + "|" + (e.subKey ?? "");
        }

        // The full suite.
        //
        // scenesAreSaved must ONLY be true when the caller has genuinely just flushed
        // open scenes to disk — the upload gate does, the "Review…" button and the
        // popup's Re-run button do not. The scene-override check uses it to decide
        // whether it is safe to open and restore scenes at all, and getting it wrong
        // there costs somebody their unsaved work.
        public static PreUploadReport RunAll(string contentId, Action<float, string> onProgress,
                                             bool scenesAreSaved = false)
        {
            return Run(contentId, AllChecks, onProgress, scenesAreSaved, advisoryOnly: false);
        }

        // Convenience: the cheap checks only, when the panel is opened.
        public static PreUploadReport RunAdvisory(string contentId)
        {
            return Run(contentId, AllChecks, null, scenesAreSaved: false, advisoryOnly: true);
        }

        // Re-runs one check and splices the result back into a cached report, so a fix
        // applied in the popup makes its finding disappear without a full rescan.
        public static void Rerun(PreUploadReport report, string checkId, bool scenesAreSaved)
        {
            if (report == null) return;

            var check = AllChecks.FirstOrDefault(c => c.Id == checkId);
            if (check == null) return;

            // A re-run exists because something on disk changed. Memoised verdicts
            // from before that change are exactly what must not survive it — a shader
            // the dev just fixed would otherwise report Missing forever.
            Checks.MetaOcclusionCheck.InvalidateShaderCaches();

            // publishToCache: false — otherwise the single-check report would briefly
            // replace the cached full report and the badge map would be rebuilt from
            // it, blanking every badge that belongs to the other four checks.
            var fresh = Run(report.contentId, new[] { check }, null, scenesAreSaved,
                            advisoryOnly: false, publishToCache: false);

            var replacement = fresh.results.FirstOrDefault(r => r.checkId == checkId);
            if (replacement == null) return;

            report.results.RemoveAll(r => r.checkId == checkId);
            report.results.Add(replacement);

            cache[report.contentId ?? ""] = report;
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try { if (ReportChanged != null) ReportChanged(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Pre-upload ReportChanged subscriber threw: {e}");
            }
        }
    }
}
#endif
