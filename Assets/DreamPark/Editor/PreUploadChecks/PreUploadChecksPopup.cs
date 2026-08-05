#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    // The unified Pre-Upload Checks window: one section per check, shared ignore
    // list, one decision at the bottom.
    //
    // Follows the house popup pattern used by ContentUploadFlowPopup /
    // ContentIdSetupPopup / UpdateAvailablePopup / ParticleDiffPopup: EditorWindow +
    // ShowUtility (never ShowModalUtility), singleton guard through
    // Resources.FindObjectsOfTypeAll, result returned via a captured Action, styles
    // built inline in OnGUI, one Vector2 per scroll view.
    //
    // Two modes:
    //   Gate mode    — opened by PreUploadChecksGate; footer offers Cancel / Continue.
    //   Review mode  — opened from Pre Launch Options or a tile badge; footer is Close.
    //
    // "Continue" is disabled while any un-ignored Blocking finding remains. There is
    // no "upload anyway" button for Blocking, on purpose. The escape hatch is
    // Ignore-with-a-reason, which writes a record into a git-tracked file where a
    // reviewer can see it — an override that leaves a trace, rather than a click that
    // leaves nothing.
    public class PreUploadChecksPopup : EditorWindow
    {
        // Severity palette, verbatim from ParticleDiffPopup / MaterialConverterWindow
        // so this window reads as part of the same toolset.
        private static readonly Color ColBlocking = new Color(0.85f, 0.30f, 0.30f);
        private static readonly Color ColWarning  = new Color(0.95f, 0.65f, 0.30f);
        private static readonly Color ColInfo     = new Color(0.45f, 0.65f, 0.95f);
        private static readonly Color ColClean    = new Color(0.45f, 0.78f, 0.45f);

        private const float WindowWidth = 720f;
        private const float WindowHeight = 760f;

        private EditorWindow owner;
        private string contentId;
        private PreUploadReport report;
        private Action<bool> onDecision;     // null → review mode
        private string focusAssetPath;       // opened from a tile badge

        private Vector2 mainScroll;
        private readonly Dictionary<string, bool> sectionOpen = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> detailOpen = new Dictionary<string, bool>();
        private string ignoringKey;          // finding currently showing its reason field
        private string ignoreReason = "";
        private bool ignoredSectionOpen;
        private bool busy;

        // Anything that changes how many controls OnGUI draws must NOT take effect in
        // the middle of a pass. IMGUI allocates control ids by draw order, so showing
        // the ignore-reason field the instant its button returns true — during a
        // MouseUp pass whose Layout pass never included it — throws "Getting control
        // N's position in a group with only M controls", and the rest of the window
        // lays out wrong.
        //
        // delayCall rather than "apply at the next Layout": several of these actions
        // open a modal confirmation dialog, and running a modal out of an in-progress
        // OnGUI is its own class of trouble. Between frames is the safe place for both.
        private void Defer(Action action)
        {
            if (action == null) return;

            EditorApplication.delayCall += () =>
            {
                if (this == null) return;   // window closed before the tick landed
                try { action(); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Pre-upload popup action failed: {e}");
                }
                Repaint();
            };
        }

        // ------------------------------------------------------------------
        // Show

        public static void Show(EditorWindow owner, string contentId,
                                PreUploadReport report, Action<bool> onDecision)
        {
            var win = GetOrCreate();
            win.owner = owner;
            win.contentId = contentId;
            win.report = report;
            win.AdoptDecision(onDecision);
            win.focusAssetPath = null;
            win.SeedSectionState();
            win.Focus();
            win.Repaint();
        }

        // Replaces the pending gate callback, RETIRING the old one first.
        //
        // Without this, a gate-mode window sitting open with an upload waiting on its
        // answer would silently lose that answer the moment the user clicked a tile
        // badge or the "Review Pre-Upload Checks…" button — both of which reuse this
        // singleton and overwrite onDecision with null. The upload would then never
        // proceed AND never cancel, with no dialog and no log.
        private void AdoptDecision(Action<bool> next)
        {
            var pending = onDecision;
            onDecision = next;

            if (pending == null || ReferenceEquals(pending, next)) return;

            try { pending(false); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] PreUploadChecksPopup callback threw: {e}");
            }
        }

        // Review mode from Pre Launch Options — runs the full suite first.
        public static void ShowForReview(EditorWindow owner, string contentId)
        {
            PreUploadReport report;
            try
            {
                report = PreUploadCheckRunner.RunAll(contentId, (t, m) =>
                    EditorUtility.DisplayProgressBar("DreamPark", m ?? "Running checks…", Mathf.Clamp01(t)));
            }
            finally { EditorUtility.ClearProgressBar(); }

            var win = GetOrCreate();
            win.owner = owner;
            win.contentId = contentId;
            win.report = report;
            win.AdoptDecision(null);
            win.focusAssetPath = null;
            win.SeedSectionState();
            win.Focus();
            win.Repaint();
        }

        // Review mode from a tile badge — uses the cached report so clicking a badge
        // is instant rather than kicking off a fresh multi-second scan.
        public static void ShowForAsset(EditorWindow owner, string contentId, string assetPath)
        {
            var cached = PreUploadCheckRunner.CachedReportFor(contentId);
            if (cached == null)
            {
                ShowForReview(owner, contentId);
                var opened = GetOrCreate();
                opened.focusAssetPath = assetPath;
                opened.SeedSectionState();
                return;
            }

            var win = GetOrCreate();
            win.owner = owner;
            win.contentId = contentId;
            win.report = cached;
            win.AdoptDecision(null);
            win.focusAssetPath = assetPath;
            win.SeedSectionState();
            win.Focus();
            win.Repaint();
        }

        private static PreUploadChecksPopup GetOrCreate()
        {
            var existing = Resources.FindObjectsOfTypeAll<PreUploadChecksPopup>();
            if (existing != null && existing.Length > 0) return existing[0];

            var win = CreateInstance<PreUploadChecksPopup>();
            win.titleContent = new GUIContent("Pre-Upload Checks");
            win.minSize = new Vector2(620f, 520f);
            win.maxSize = new Vector2(1000f, 1200f);

            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - WindowWidth) / 2f,
                main.y + (main.height - WindowHeight) / 2f,
                WindowWidth, WindowHeight);

            win.ShowUtility();
            return win;
        }

        private void SeedSectionState()
        {
            sectionOpen.Clear();
            detailOpen.Clear();
            ignoringKey = null;
            ignoreReason = "";

            if (report == null) return;

            foreach (var r in report.results)
            {
                var active = ActiveFindings(r);
                bool hasBlocking = active.Any(f => f.severity == CheckSeverity.Blocking);
                bool focused = !string.IsNullOrEmpty(focusAssetPath)
                    && active.Any(f => string.Equals(f.assetPath, focusAssetPath,
                                                     StringComparison.OrdinalIgnoreCase));

                // Blocking sections open by default; everything else collapsed, so the
                // window opens on the thing that will actually stop the upload.
                sectionOpen[r.checkId] = hasBlocking || focused;
            }
        }

        private void OnDisable()
        {
            // Closing the window in gate mode is a cancel, not a silent proceed.
            // Invoked here rather than in OnDestroy so it also covers the utility
            // window being dismissed by the OS.
            var pending = onDecision;
            onDecision = null;
            if (pending != null)
            {
                try { pending(false); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] PreUploadChecksPopup callback threw: {e}");
                }
            }
        }

        // ------------------------------------------------------------------
        // GUI

        private void OnGUI()
        {
            if (report == null)
            {
                EditorGUILayout.HelpBox("No check results to show.", MessageType.Info);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            DrawHero();

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);

            foreach (var result in OrderedResults())
                DrawSection(result);

            DrawIgnoredSection();

            EditorGUILayout.EndScrollView();

            GUILayout.Space(6f);
            DrawFooter();
        }

        private IEnumerable<CheckResult> OrderedResults()
        {
            var checks = PreUploadCheckRunner.AllChecks.ToList();
            return report.results
                .OrderByDescending(r => WorstOf(r))
                .ThenBy(r => checks.FindIndex(c => c.Id == r.checkId));
        }

        private CheckSeverity WorstOf(CheckResult r)
        {
            var active = ActiveFindings(r);
            return active.Count == 0 ? CheckSeverity.Info : active.Max(f => f.severity);
        }

        private List<Finding> ActiveFindings(CheckResult r)
        {
            if (r.findings == null) return new List<Finding>();
            return r.findings.Where(f => !f.isIgnored).ToList();
        }

        private void DrawHero()
        {
            var band = GUILayoutUtility.GetRect(10f, 78f, GUILayout.ExpandWidth(true));

            Color bg;
            if (report.HasBlocking) bg = new Color(0.33f, 0.16f, 0.13f);
            else if (report.WarningCount > 0) bg = new Color(0.30f, 0.24f, 0.10f);
            else bg = new Color(0.08f, 0.30f, 0.22f);
            EditorGUI.DrawRect(band, bg);

            var eyebrow = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                normal = { textColor = new Color(0.82f, 0.86f, 0.90f) },
                fontSize = 10,
            };
            var title = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                fontSize = 19,
            };
            var subtitle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = new Color(0.88f, 0.92f, 0.95f) },
                fontSize = 11,
            };

            GUI.Label(new Rect(band.x + 16f, band.y + 8f, band.width - 32f, 14f),
                      "DREAMPARK", eyebrow);
            GUI.Label(new Rect(band.x + 16f, band.y + 22f, band.width - 32f, 26f),
                      "Pre-Upload Checks", title);
            GUI.Label(new Rect(band.x + 16f, band.y + 50f, band.width - 32f, 20f),
                      SummaryLine(), subtitle);
        }

        private string SummaryLine()
        {
            var parts = new List<string> { contentId };
            if (report.BlockingCount > 0) parts.Add($"{report.BlockingCount} blocking");
            if (report.WarningCount > 0) parts.Add($"{report.WarningCount} warning{(report.WarningCount == 1 ? "" : "s")}");
            if (report.IgnoredCount > 0) parts.Add($"{report.IgnoredCount} ignored");
            if (report.BlockingCount == 0 && report.WarningCount == 0) parts.Add("all clear");
            return string.Join("  ·  ", parts);
        }

        private void DrawSection(CheckResult result)
        {
            var check = PreUploadCheckRunner.AllChecks.FirstOrDefault(c => c.Id == result.checkId);
            string displayName = check != null ? check.DisplayName : result.checkId;
            string rationale = check != null ? check.Rationale : "";

            var active = ActiveFindings(result);

            GUILayout.Space(6f);
            GUILayout.BeginVertical(EditorStyles.helpBox);

            // Header row
            GUILayout.BeginHorizontal();

            bool open;
            if (!sectionOpen.TryGetValue(result.checkId, out open)) open = false;

            string glyph = GlyphFor(result, active);
            string header = $"{glyph}  {displayName}";
            if (active.Count > 0) header += $"  ({active.Count})";

            var headerStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = TintFor(result, active) },
                onNormal = { textColor = TintFor(result, active) },
            };

            open = EditorGUILayout.Foldout(open, header, true, headerStyle);
            sectionOpen[result.checkId] = open;

            GUILayout.FlexibleSpace();
            DrawSectionBulkAction(result, active);
            GUILayout.EndHorizontal();

            if (open)
            {
                if (!string.IsNullOrEmpty(rationale))
                    EditorGUILayout.LabelField(rationale, EditorStyles.wordWrappedMiniLabel);

                switch (result.outcome)
                {
                    case CheckOutcome.Errored:
                        EditorGUILayout.HelpBox(
                            "This check could not run, so it did not block anything. See the console.\n" +
                            (result.errorMessage ?? ""),
                            MessageType.Info);
                        break;

                    case CheckOutcome.Skipped:
                        EditorGUILayout.HelpBox(result.skipReason ?? "Skipped.", MessageType.Info);
                        if (GUILayout.Button("Run this check now", GUILayout.Height(22f)))
                        {
                            string id = result.checkId;
                            Defer(() => RerunCheck(id));
                        }
                        break;

                    default:
                        if (active.Count == 0)
                        {
                            EditorGUILayout.LabelField("No issues found.", EditorStyles.miniLabel);
                        }
                        else
                        {
                            foreach (var f in active.OrderByDescending(f => f.severity)
                                                    .ThenBy(f => f.assetPath, StringComparer.OrdinalIgnoreCase))
                                DrawFinding(f);
                        }
                        break;
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawSectionBulkAction(CheckResult result, List<Finding> active)
        {
            // A bulk action exists when every active finding offers the same first fix
            // AND that fix actually changes something.
            //
            // Without the resolvesFinding test this offered "Select material — all (2)",
            // which pings two assets in the Project window and calls it a batch
            // operation. It appeared because navigation actions are the ONLY fix on
            // findings that have no automatic remedy, so a section mixing "convert this"
            // with "we couldn't verify this" agreed on "Select material" as its common
            // first action.
            if (active.Count < 2) return;

            var firstFixes = active.Select(f => f.fixes.Count > 0 ? f.fixes[0] : null).ToList();
            if (firstFixes.Any(f => f == null || !f.resolvesFinding)) return;

            var firstLabels = firstFixes.Select(f => f.label).ToList();
            if (firstLabels.Any(string.IsNullOrEmpty)) return;
            if (firstLabels.Distinct(StringComparer.Ordinal).Count() != 1) return;

            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button($"{firstLabels[0]} — all ({active.Count})",
                                     EditorStyles.miniButton, GUILayout.Width(190f)))
                {
                    // Build the confirmation from the WHOLE batch. Reusing the first
                    // finding's message would show a dialog naming one prefab and one
                    // new name, and then act on N of them.
                    string bulkMessage =
                        $"Apply \"{firstLabels[0]}\" to all {active.Count} findings?\n\n"
                      + string.Join("\n", active.Take(12).Select(f => "• " + (f.assetPath ?? f.title)))
                      + (active.Count > 12 ? $"\n…and {active.Count - 12} more" : "")
                      + "\n\nCannot be undone with Ctrl-Z. Use version control to revert.";

                    var batch = active.Select(f => f.fixes[0]).ToList();
                    string batchCheckId = result.checkId;
                    string batchTitle = $"{firstLabels[0]} — all {batch.Count}";
                    Defer(() => RunFixes(batchCheckId, batch, batchTitle, bulkMessage));
                }
            }
        }

        private void DrawFinding(Finding f)
        {
            GUILayout.Space(2f);
            GUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.BeginHorizontal();

            var tint = TintForSeverity(f.severity);
            var titleStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = tint },
                wordWrap = true,
            };

            bool highlighted = !string.IsNullOrEmpty(focusAssetPath)
                && string.Equals(f.assetPath, focusAssetPath, StringComparison.OrdinalIgnoreCase);

            EditorGUILayout.LabelField(new GUIContent((highlighted ? "▶ " : "") + f.title, f.detail),
                                       titleStyle);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(f.detail))
                EditorGUILayout.LabelField(f.detail, EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(f.assetPath))
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(f.assetPath, EditorStyles.miniLabel);
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(56f)))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(f.assetPath);
                    if (asset != null)
                    {
                        Selection.activeObject = asset;
                        EditorGUIUtility.PingObject(asset);
                    }
                }
                GUILayout.EndHorizontal();
            }

            // Actions
            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(busy))
            {
                foreach (var fix in f.fixes)
                {
                    var content = new GUIContent(fix.label, fix.tooltip);
                    float w = Mathf.Max(90f, EditorStyles.miniButton.CalcSize(content).x + 14f);
                    if (GUILayout.Button(content, EditorStyles.miniButton, GUILayout.Width(w)))
                    {
                        var capturedFix = fix;
                        string capturedCheckId = f.checkId;
                        Defer(() => RunFixes(capturedCheckId, new List<FixAction> { capturedFix },
                                             capturedFix.confirmTitle ?? capturedFix.label,
                                             capturedFix.confirmMessage));
                    }
                }

                GUILayout.FlexibleSpace();

                if (ignoringKey != f.IgnoreKey)
                {
                    if (GUILayout.Button(new GUIContent("Ignore…",
                            "Records this in .preupload-ignores.json, which is tracked by git and shared with your team."),
                            EditorStyles.miniButton, GUILayout.Width(70f)))
                    {
                        string key = f.IgnoreKey;
                        Defer(() => { ignoringKey = key; ignoreReason = ""; });
                    }
                }
            }
            GUILayout.EndHorizontal();

            if (ignoringKey == f.IgnoreKey)
                DrawIgnorePrompt(f);

            GUILayout.EndVertical();
        }

        private void DrawIgnorePrompt(Finding f)
        {
            GUILayout.Space(2f);
            GUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(
                f.severity == CheckSeverity.Blocking
                    ? "Why is this one safe to ship? This is a blocking finding — the reason is recorded in git for your team to review."
                    : "Optional note for your team. Recorded in .preupload-ignores.json.",
                EditorStyles.wordWrappedMiniLabel);

            ignoreReason = EditorGUILayout.TextField(ignoreReason);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(70f)))
            {
                Defer(() => { ignoringKey = null; ignoreReason = ""; });
            }

            // A blocking finding requires a stated reason. A warning does not — asking
            // for justification on every low-stakes dismissal is how you train people
            // to type "x" and stop reading.
            bool needsReason = f.severity == CheckSeverity.Blocking;
            using (new EditorGUI.DisabledScope(needsReason && string.IsNullOrWhiteSpace(ignoreReason)))
            {
                if (GUILayout.Button("Ignore this finding", EditorStyles.miniButton, GUILayout.Width(140f)))
                {
                    var target = f;
                    string reason = ignoreReason;
                    Defer(() =>
                    {
                        PreUploadIgnoreStore.Ignore(contentId, target.checkId, target.assetGuid,
                                                    target.assetPath, target.subKey, reason);
                        target.isIgnored = true;
                        ignoringKey = null;
                        ignoreReason = "";
                        PreUploadCheckRunner.InvalidateCache(contentId);
                    });
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawIgnoredSection()
        {
            var ignored = report.AllFindings.Where(f => f.isIgnored).ToList();
            if (ignored.Count == 0) return;

            GUILayout.Space(6f);
            GUILayout.BeginVertical(EditorStyles.helpBox);

            ignoredSectionOpen = EditorGUILayout.Foldout(
                ignoredSectionOpen, $"ⓘ  Ignored  ({ignored.Count})", true);

            if (ignoredSectionOpen)
            {
                EditorGUILayout.LabelField(
                    "These findings were dismissed and recorded in .preupload-ignores.json. " +
                    "Nothing here is hidden — un-ignore to bring it back.",
                    EditorStyles.wordWrappedMiniLabel);

                foreach (var f in ignored.OrderBy(f => f.checkId, StringComparer.Ordinal))
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(new GUIContent($"{f.title}", f.detail),
                                               EditorStyles.miniLabel);
                    if (GUILayout.Button("Un-ignore", EditorStyles.miniButton, GUILayout.Width(80f)))
                    {
                        var target = f;
                        Defer(() =>
                        {
                            PreUploadIgnoreStore.Unignore(contentId, target.checkId,
                                                          target.assetGuid, target.subKey);
                            target.isIgnored = false;
                            PreUploadCheckRunner.InvalidateCache(contentId);
                        });
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawFooter()
        {
            GUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Re-run all checks", GUILayout.Height(30f), GUILayout.Width(140f)))
                    RerunAll();
            }

            GUILayout.FlexibleSpace();

            if (onDecision == null)
            {
                if (GUILayout.Button("Close", GUILayout.Height(30f), GUILayout.Width(120f)))
                    Close();
            }
            else
            {
                if (GUILayout.Button("Cancel Upload", GUILayout.Height(30f), GUILayout.Width(130f)))
                {
                    Decide(false);
                }

                GUILayout.Space(6f);

                using (new EditorGUI.DisabledScope(report.HasBlocking || busy))
                {
                    string label = report.HasBlocking
                        ? $"Fix {report.BlockingCount} blocking issue{(report.BlockingCount == 1 ? "" : "s")} to continue"
                        : "Continue · Upload";
                    if (GUILayout.Button(label, GUILayout.Height(30f), GUILayout.Width(230f)))
                        Decide(true);
                }
            }

            GUILayout.EndHorizontal();
        }

        private void Decide(bool proceed)
        {
            var pending = onDecision;
            onDecision = null;   // so OnDisable doesn't fire a second, contradicting answer
            Close();

            if (pending == null) return;
            try { pending(proceed); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] PreUploadChecksPopup callback threw: {e}");
            }
        }

        // ------------------------------------------------------------------
        // Fixes

        private void RunFixes(string checkId, List<FixAction> fixes, string confirmTitle, string confirmMessage)
        {
            if (!string.IsNullOrEmpty(confirmMessage))
            {
                if (!EditorUtility.DisplayDialog(confirmTitle ?? "DreamPark", confirmMessage, "Do it", "Cancel"))
                    return;
            }

            busy = true;
            int applied = 0, failed = 0, navigated = 0;
            try
            {
                // Pause the content watchdog across the WHOLE batch. Without this a
                // five-prefab rename retriggers ContentProcessor's stamping pass once
                // per file. ExecuteWithWatchdogPaused is ref-counted and
                // exception-safe, which hand-rolled Pause/Resume is not.
                ContentProcessor.ExecuteWithWatchdogPaused(() =>
                {
                    foreach (var fix in fixes)
                    {
                        try
                        {
                            if (fix.run == null) continue;

                            bool resolved = fix.run();

                            // Navigation actions ("Select", "Open scene") resolve
                            // nothing by design. Counting those as failures printed a
                            // warning on every single click, which is how you teach
                            // people to stop reading this log.
                            if (!fix.resolvesFinding) { navigated++; continue; }

                            if (resolved) applied++; else failed++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Debug.LogWarning($"[DreamPark] Pre-upload fix '{fix.label}' failed: {e}");
                        }
                    }
                });
            }
            finally
            {
                busy = false;
                EditorUtility.ClearProgressBar();
            }

            if (failed > 0)
                Debug.LogWarning($"[DreamPark] {failed} of {fixes.Count} fix(es) did not complete — see warnings above.");
            else if (applied > 0)
                Debug.Log($"[DreamPark] Applied {applied} pre-upload fix(es).");

            // Nothing changed, so there is nothing to re-scan.
            if (applied == 0 && failed == 0) return;

            RerunCheck(checkId);
        }

        private void RerunCheck(string checkId)
        {
            try
            {
                PreUploadCheckRunner.Rerun(report, checkId, scenesAreSaved: false);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not re-run check '{checkId}': {e}");
            }
            Repaint();
        }

        private void RerunAll()
        {
            try
            {
                // scenesAreSaved: false — this button does not save anything, and the
                // scene-override check must know that before it opens and restores
                // scenes.
                report = PreUploadCheckRunner.RunAll(contentId, (t, m) =>
                    EditorUtility.DisplayProgressBar("DreamPark", m ?? "Running checks…", Mathf.Clamp01(t)),
                    scenesAreSaved: false);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Pre-upload checks could not run: {e}");
            }
            finally { EditorUtility.ClearProgressBar(); }

            SeedSectionState();
            Repaint();
        }

        // ------------------------------------------------------------------
        // Styling helpers

        private string GlyphFor(CheckResult result, List<Finding> active)
        {
            if (result.outcome == CheckOutcome.Errored) return "…";
            if (result.outcome == CheckOutcome.Skipped) return "ⓘ";
            if (active.Count == 0) return "✓";
            return active.Max(f => f.severity) == CheckSeverity.Blocking ? "⛔" : "⚠";
        }

        private Color TintFor(CheckResult result, List<Finding> active)
        {
            if (result.outcome == CheckOutcome.Errored || result.outcome == CheckOutcome.Skipped)
                return ColInfo;
            if (active.Count == 0) return ColClean;
            return TintForSeverity(active.Max(f => f.severity));
        }

        private Color TintForSeverity(CheckSeverity s)
        {
            switch (s)
            {
                case CheckSeverity.Blocking: return ColBlocking;
                case CheckSeverity.Warning: return ColWarning;
                default: return ColInfo;
            }
        }
    }
}
#endif
