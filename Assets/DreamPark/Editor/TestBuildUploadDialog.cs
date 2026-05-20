#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using DreamPark.API;

namespace DreamPark
{
    // Small modal-style EditorWindow for collecting a title + release notes
    // + the editor platforms to compile for before a Test Channel upload.
    //
    // Test builds are intended for previewing inside dreampark-core's
    // Content Manager (which runs in the Unity editor on Mac or Windows),
    // so the platform options are limited to the two editor targets —
    // iOS and Android builds aren't useful here and would just burn build
    // time. The platform toggles default to whichever of Mac/Windows was
    // last used (or both on, on first use) and persist via EditorPrefs so
    // the choice carries over between sessions.
    //
    // Defaults the title to the current park name (Assets/Content/{GameName})
    // plus a short timestamp so a one-click "Upload Test Build" still
    // produces a meaningful entry in the Test Channel listing without the
    // user typing anything.
    //
    // Patch base picker: when the caller passes any candidate test builds
    // (typically prior uploads for this same content), the dialog shows a
    // dropdown letting the user pick which build this upload should be a
    // PATCH of. Default is "the most recent candidate" so the common case
    // — "I just uploaded a test build, now I'm patching it" — Just Works.
    // Picking "Full upload" disables patching and pushes every bundle from
    // scratch. The selection is wired through to ContentAPI.CreateTestBuild
    // + UploadTestBuildArtifacts as the parentTestBuildId arg.
    public class TestBuildUploadDialog : EditorWindow
    {
        private const string BuildOsxPrefKey = "DreamPark.TestBuildUpload.BuildOsx";
        private const string BuildWindowsPrefKey = "DreamPark.TestBuildUpload.BuildWindows";

        private string title = "";
        private string releaseNotes = "";
        private string contentName = "";
        private bool buildOsx = true;
        private bool buildWindows = true;
        private Vector2 mainScroll;
        private bool awaitingEstimate;
        private bool refocusAfterEstimate;

        // Patch base state. patchBaseOptions[0] is always "Full upload"
        // (sentinel ID = null); subsequent entries are real test builds
        // sourced from the caller. patchBaseSelectedIndex tracks the user's
        // current dropdown selection.
        private List<PatchBaseOption> patchBaseOptions;
        private int patchBaseSelectedIndex;

        // Lightweight DTO for the parent-pick dropdown. Kept inside the
        // dialog so callers don't have to learn a new shape — they pass in
        // raw strings and we wrap them into the structured form here.
        public struct PatchBaseOption
        {
            public string testBuildId;   // null for "Full upload"
            public string displayLabel;  // shown in the dropdown
            public PatchBaseOption(string id, string label)
            {
                testBuildId = id;
                displayLabel = label;
            }
        }

        // Callback signature: (title, releaseNotes, contentName, buildOsx,
        // buildWindows, parentTestBuildId, estimateOnly). contentName
        // carries the originating park name through to the commit metadata
        // so the Test Channel listing can show "{title} · {contentName}"
        // the same way a production card shows the ContentName field.
        // parentTestBuildId is null when the user picked "Full upload" or
        // when no candidates were available. estimateOnly is true when the
        // user clicked "Check Patch Size" — the panel then runs a one-
        // platform compile + diff, surfaces stats inline, and lets the
        // main "Compile & Upload" button reuse the built bundles.
        private Action<string, string, string, bool, bool, string, bool> onConfirm;
        private bool didInitialFocus;

        // Backward-compat Show — no patch-base picker, behaves like the
        // pre-patch dialog (always full upload).
        public static void Show(string defaultContentName, Action<string, string, string, bool, bool> onConfirm)
        {
            Show(defaultContentName, null, (t, n, cn, osx, win, _, _2) => onConfirm?.Invoke(t, n, cn, osx, win));
        }

        // Older patch-aware overload — no estimate-only mode.
        public static void Show(
            string defaultContentName,
            List<PatchBaseOption> patchBaseCandidates,
            Action<string, string, string, bool, bool, string> onConfirm)
        {
            Show(defaultContentName, patchBaseCandidates, (t, n, cn, osx, win, parentId, _) => onConfirm?.Invoke(t, n, cn, osx, win, parentId));
        }

        // Full Show — accepts an optional list of candidate parent test
        // builds and an estimate-aware callback. When non-empty
        // patchBaseCandidates is provided, surfaces a "Patch base" dropdown
        // AND a "Check Patch Size" button that fires the callback with
        // estimateOnly: true. Otherwise the dialog matches the legacy
        // single-action layout.
        public static void Show(
            string defaultContentName,
            List<PatchBaseOption> patchBaseCandidates,
            Action<string, string, string, bool, bool, string, bool> onConfirm)
        {
            var window = CreateInstance<TestBuildUploadDialog>();
            window.titleContent = new GUIContent("Upload Test Build");
            window.contentName = defaultContentName ?? "";
            window.title = BuildDefaultTitle(defaultContentName);
            window.releaseNotes = "";
            window.onConfirm = onConfirm;
            window.buildOsx = EditorPrefs.GetBool(BuildOsxPrefKey, true);
            window.buildWindows = EditorPrefs.GetBool(BuildWindowsPrefKey, true);

            // Build the patch-base dropdown options. Position 0 is always
            // "Full upload" — even if the caller passed real candidates,
            // the user can opt out of patching.
            window.patchBaseOptions = new List<PatchBaseOption>();
            window.patchBaseOptions.Add(new PatchBaseOption(null, "Full upload (no patch base)"));
            if (patchBaseCandidates != null)
            {
                foreach (var c in patchBaseCandidates)
                {
                    if (!string.IsNullOrEmpty(c.testBuildId))
                        window.patchBaseOptions.Add(c);
                }
            }
            // Default to the first real candidate (most recent) when one
            // exists — "patch the latest" is the dominant workflow.
            window.patchBaseSelectedIndex = window.patchBaseOptions.Count > 1 ? 1 : 0;

            // Window height grows when the patch-base picker is present
            // so it doesn't overflow into the buttons row.
            float height = (window.patchBaseOptions.Count > 1) ? 700 : 600;
            window.minSize = new Vector2(520, height);
            window.maxSize = new Vector2(620, height);
            window.ShowUtility();
        }

        private static string BuildDefaultTitle(string parkName)
        {
            string safePark = string.IsNullOrWhiteSpace(parkName) ? "TestBuild" : parkName;
            string stamp = DateTime.Now.ToString("MMM d, h:mm tt");
            return $"{safePark} — {stamp}";
        }

        private void OnEnable()
        {
            ContentAPI.UploadProgressChanged += OnUploadProgressChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            ContentAPI.UploadProgressChanged -= OnUploadProgressChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnUploadProgressChanged()
        {
            Repaint();
        }

        private void OnEditorUpdate()
        {
            var owner = FindOwnerPanel();
            if (owner != null && (owner.IsUploading || owner.HasPendingTestBuildUpload))
            {
                Repaint();
            }

            if (refocusAfterEstimate && owner != null && owner.HasPendingTestBuildUpload && !owner.IsUploading)
            {
                refocusAfterEstimate = false;
                awaitingEstimate = false;
                Focus();
                Repaint();
            }
        }

        private void OnGUI()
        {
            var owner = FindOwnerPanel();

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            EditorGUILayout.HelpBox(
                "Test builds compile editor-only addressables (Mac and/or Windows) and push them to the " +
                "Test Channel in dreampark-core's Content Manager. iOS and Android are skipped because " +
                "test builds are for previewing inside the Unity editor. Auto-expires after 7 days. " +
                "Admin / DreamPark teammates only.",
                MessageType.Info);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Title", EditorStyles.boldLabel);
            GUI.SetNextControlName("TestBuildTitleField");
            title = EditorGUILayout.TextField(title);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Release Notes (optional)", EditorStyles.boldLabel);
            releaseNotes = EditorGUILayout.TextArea(releaseNotes, GUILayout.Height(60));

            // Patch base picker — only shown when there's at least one real
            // candidate. Otherwise we just default to "Full upload" silently
            // and skip a dropdown that would have only one entry.
            if (patchBaseOptions != null && patchBaseOptions.Count > 1)
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField("Patch Base", EditorStyles.boldLabel);
                var labels = new string[patchBaseOptions.Count];
                for (int i = 0; i < patchBaseOptions.Count; i++)
                    labels[i] = patchBaseOptions[i].displayLabel ?? "(unnamed)";
                patchBaseSelectedIndex = EditorGUILayout.Popup(patchBaseSelectedIndex, labels);
                if (patchBaseSelectedIndex > 0)
                {
                    EditorGUILayout.HelpBox(
                        "Only bundles whose content changed since the patch base will upload — the rest " +
                        "are server-side-copied from the base. Massive bandwidth savings on iterative " +
                        "test builds.",
                        MessageType.None);
                }
            }

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Editor Targets", EditorStyles.boldLabel);
            bool newOsx = EditorGUILayout.ToggleLeft("Editor (Mac) — StandaloneOSX", buildOsx);
            if (newOsx != buildOsx)
            {
                buildOsx = newOsx;
                EditorPrefs.SetBool(BuildOsxPrefKey, buildOsx);
            }
            bool newWindows = EditorGUILayout.ToggleLeft("Editor (Windows) — StandaloneWindows", buildWindows);
            if (newWindows != buildWindows)
            {
                buildWindows = newWindows;
                EditorPrefs.SetBool(BuildWindowsPrefKey, buildWindows);
            }

            if (!buildOsx && !buildWindows)
            {
                EditorGUILayout.HelpBox("Pick at least one editor target.", MessageType.Warning);
            }

            DrawPatchEstimateSection(owner);

            GUILayout.FlexibleSpace();

            // Determine whether the user has actually selected a patch base
            // (not "Full upload") — only then does "Check Patch Size" make
            // sense, because there's no parent to diff against otherwise.
            string selectedParentId = null;
            if (patchBaseOptions != null && patchBaseSelectedIndex >= 0 && patchBaseSelectedIndex < patchBaseOptions.Count)
                selectedParentId = patchBaseOptions[patchBaseSelectedIndex].testBuildId;
            bool patchModeArmed = !string.IsNullOrEmpty(selectedParentId);

            using (new GUILayout.HorizontalScope())
            {
                bool canConfirm = !string.IsNullOrWhiteSpace(title) && (buildOsx || buildWindows) && (owner == null || !owner.IsUploading);
                bool canEstimate = canConfirm && patchModeArmed;

                // "Check Patch Size" button — only shown when a real patch
                // base is selected. Triggers a one-platform-only compile +
                // diff so the user can preview the patch size before
                // committing to a full upload. The resulting bundles in
                // ServerData/ are reused if the user proceeds with "Upload
                // Now" from the popup, so no double-build.
                using (new EditorGUI.DisabledScope(!canEstimate))
                {
                    if (GUILayout.Button(
                        new GUIContent("Check Patch Size",
                            "Build one platform's bundles and compute the patch diff against the chosen base, without uploading. Lets you see exactly how big the patch is before committing — and if you then click Compile & Upload with the same settings, it reuses that build so no time is wasted."),
                        GUILayout.Width(140),
                        GUILayout.Height(24)))
                    {
                        FireConfirm(estimateOnly: true);
                    }
                }
                GUILayout.Space(6f);

                using (new EditorGUI.DisabledScope(!canConfirm))
                {
                    if (GUILayout.Button("Compile & Upload", GUILayout.Width(160), GUILayout.Height(24)))
                    {
                        FirePrimaryUpload(owner);
                    }
                }
            }

            EditorGUILayout.EndScrollView();

            // Focus the title field on first paint so the user can start
            // typing immediately without clicking. Only once — re-focusing
            // every frame would steal focus from the release-notes field
            // mid-typing.
            if (!didInitialFocus && Event.current.type == EventType.Repaint)
            {
                didInitialFocus = true;
                EditorGUI.FocusTextInControl("TestBuildTitleField");
            }
        }

        // Snapshots the current dialog state into locals, closes the
        // window, then invokes the confirm callback. Centralizing this
        // here means the two buttons (Check / Compile & Upload) can't
        // drift out of sync on what they capture.
        private void FireConfirm(bool estimateOnly)
        {
            var confirmCallback = onConfirm;
            var t = title.Trim();
            var notes = releaseNotes ?? "";
            var cn = contentName ?? "";
            var osx = buildOsx;
            var win = buildWindows;
            string parentId = null;
            if (patchBaseOptions != null && patchBaseSelectedIndex >= 0 && patchBaseSelectedIndex < patchBaseOptions.Count)
                parentId = patchBaseOptions[patchBaseSelectedIndex].testBuildId;
            if (estimateOnly)
            {
                awaitingEstimate = true;
                refocusAfterEstimate = true;
                confirmCallback?.Invoke(t, notes, cn, osx, win, parentId, estimateOnly);
                return;
            }
            Close();
            confirmCallback?.Invoke(t, notes, cn, osx, win, parentId, estimateOnly);
        }

        // Routes the dialog's single primary upload button. If the current
        // form state still matches a checked patch sitting in ServerData/,
        // reuse that build without recompiling; otherwise fall back to the
        // normal fresh compile + upload path.
        private void FirePrimaryUpload(ContentUploaderPanel owner)
        {
            string parentId = null;
            if (patchBaseOptions != null && patchBaseSelectedIndex >= 0 && patchBaseSelectedIndex < patchBaseOptions.Count)
                parentId = patchBaseOptions[patchBaseSelectedIndex].testBuildId;

            if (owner != null && owner.PendingTestBuildMatches(
                title.Trim(),
                releaseNotes ?? "",
                contentName ?? "",
                buildOsx,
                buildWindows,
                parentId))
            {
                Close();
                owner.ResumePendingTestBuildUpload();
                return;
            }

            FireConfirm(estimateOnly: false);
        }

        private ContentUploaderPanel FindOwnerPanel()
        {
            var windows = Resources.FindObjectsOfTypeAll<ContentUploaderPanel>();
            if (windows == null || windows.Length == 0) return null;
            foreach (var window in windows)
            {
                if (window != null && string.Equals(window.ContentId, contentName, StringComparison.OrdinalIgnoreCase))
                    return window;
            }
            return windows[0];
        }

        private void DrawPatchEstimateSection(ContentUploaderPanel owner)
        {
            bool hasPatchStats = ContentAPI.CurrentPatchStats != null;
            bool hasPendingEstimate = owner != null && owner.HasPendingTestBuildUpload;
            bool isBusy = owner != null && owner.IsUploading;
            bool isReady = hasPatchStats && !isBusy;
            bool isChecking = awaitingEstimate || isBusy;
            bool hasError = owner != null && owner.UploadStatusIsError && !string.IsNullOrEmpty(owner.UploadStatusMessage);
            string selectedParentId = null;
            if (patchBaseOptions != null && patchBaseSelectedIndex >= 0 && patchBaseSelectedIndex < patchBaseOptions.Count)
                selectedParentId = patchBaseOptions[patchBaseSelectedIndex].testBuildId;
            bool patchModeArmed = !string.IsNullOrEmpty(selectedParentId);

            GUILayout.Space(8f);
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Patch Estimate", EditorStyles.boldLabel);

            if (hasError)
            {
                if (owner != null && !string.IsNullOrEmpty(owner.UploadStatusTitle))
                {
                    EditorGUILayout.LabelField(owner.UploadStatusTitle, EditorStyles.miniBoldLabel);
                }
                EditorGUILayout.HelpBox(owner.UploadStatusMessage, MessageType.Error);
                DrawPatchStatsBlock(isUploading: false, showPlaceholder: true);
            }
            else if (isChecking)
            {
                if (owner != null && !string.IsNullOrEmpty(owner.UploadStatusTitle))
                {
                    EditorGUILayout.LabelField(owner.UploadStatusTitle, EditorStyles.miniBoldLabel);
                }
                EditorGUILayout.HelpBox(
                    "Running Check Patch Size for this test build. The estimate will appear here as soon as the diff finishes.",
                    MessageType.None);
                DrawPatchStatsBlock(isUploading: isBusy, showPlaceholder: false);
            }
            else if (isReady)
            {
                EditorGUILayout.LabelField("Patch estimate ready", EditorStyles.miniBoldLabel);
                DrawPatchStatsBlock(isUploading: false, showPlaceholder: false);
                if (hasPendingEstimate)
                {
                    EditorGUILayout.HelpBox(
                        "Click the main \"Compile & Upload\" button below with the same settings to upload this checked patch without rebuilding.",
                        MessageType.None);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    patchModeArmed
                        ? "Click \"Check Patch Size\" to see how many bundles will upload and how many MB the patch will ship."
                        : "Select a patch base to enable \"Check Patch Size\" and preview how many bundles and how many MB the patch will ship.",
                    MessageType.None);
                DrawPatchStatsBlock(isUploading: false, showPlaceholder: true);
            }

            if (owner != null && owner.UploadStatusProgress >= 0f && owner.IsUploading)
            {
                GUILayout.Space(4f);
                Rect progressRect = GUILayoutUtility.GetRect(18f, 18f, "TextField");
                EditorGUI.ProgressBar(progressRect, Mathf.Clamp01(owner.UploadStatusProgress), $"{owner.UploadStatusProgress * 100f:0}%");
            }

            GUILayoutUtility.GetRect(1f, 12f);

            GUILayout.EndVertical();
        }

        private void DrawPatchStatsBlock(bool isUploading, bool showPlaceholder)
        {
            var stats = ContentAPI.CurrentPatchStats;
            if (stats == null || (stats.newFiles == 0 && stats.inheritedFiles == 0))
            {
                if (showPlaceholder)
                {
                    GUILayout.Space(4f);
                    EditorGUILayout.LabelField("No patch estimate yet.", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField("Bundle patch size: -- out of -- total", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Will upload -- new bundle(s)  ·  -- inherited from parent  ·  -- total", EditorStyles.miniLabel);
                }
                return;
            }

            string parentId = stats.parentTestBuildId;
            int newCount = stats.newFiles;
            int inheritedCount = stats.inheritedFiles;
            long patchSize = Math.Max(0L, stats.patchSizeBytes);
            long inheritedSize = Math.Max(0L, stats.inheritedSizeBytes);
            long totalSize = Math.Max(0L, stats.totalSizeBytes);
            int uploaded = Math.Min(Math.Max(0, stats.uploadedSoFar), newCount);
            int totalBundles = newCount + inheritedCount;
            bool isPatch = !string.IsNullOrEmpty(parentId);
            float patchFraction = totalSize <= 0L ? 0f : (float)((double)patchSize / (double)totalSize);
            float patchPct = patchFraction * 100f;

            GUILayout.Space(4f);
            EditorGUILayout.LabelField(isUploading
                ? (isPatch ? "Patch upload" : "Full upload")
                : (isPatch ? "Patch estimate" : "Full upload estimate"), EditorStyles.miniBoldLabel);

            string countLine;
            if (isUploading)
            {
                countLine = isPatch
                    ? $"Uploading {uploaded} of {newCount} new bundle(s)  ·  {inheritedCount} inherited from parent  ·  {totalBundles} total"
                    : $"Uploading {uploaded} of {newCount} bundle(s)";
            }
            else
            {
                countLine = isPatch
                    ? $"Will upload {newCount} new bundle(s)  ·  {inheritedCount} inherited from parent  ·  {totalBundles} total"
                    : $"Will upload {newCount} bundle(s)";
            }
            EditorGUILayout.LabelField(countLine);

            string sizeLine = isPatch
                ? $"Bundle patch size: {FormatBytes(patchSize)} out of {FormatBytes(totalSize)} total   ·   Inherited: {FormatBytes(inheritedSize)}"
                : $"Upload size: {FormatBytes(patchSize)}";
            EditorGUILayout.LabelField(sizeLine, EditorStyles.miniLabel);

            if (isPatch && inheritedSize > 0L)
            {
                string savings = $"Skipping {FormatBytes(inheritedSize)} of upload by reusing parent — pushing only {patchPct:0.0}% of total content";
                EditorGUILayout.LabelField(savings, EditorStyles.miniLabel);

                Rect r = GUILayoutUtility.GetRect(18f, 10f, "TextField");
                EditorGUI.ProgressBar(r, Mathf.Clamp01(patchFraction), $"{patchPct:0.0}% of bundles need upload");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
#endif
