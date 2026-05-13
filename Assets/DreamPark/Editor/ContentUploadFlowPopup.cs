#if UNITY_EDITOR && !DREAMPARKCORE
using System.Linq;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    public class ContentUploadFlowPopup : EditorWindow
    {
        private ContentUploaderPanel owner;
        private bool buildBeforeUpload = true;
        // Mirrored from UploadModePrefs on Show(). Carrying it on the popup
        // (rather than reading prefs every OnGUI tick) keeps the mode stable
        // during a long-running flow even if some other tool flips prefs
        // mid-upload, and lets the user override their saved default for
        // just this run without persisting.
        private UploadMode uploadMode = UploadMode.Patch;
        private Vector2 progressScroll;
        private Vector2 notesScroll;
        private Vector2 mainScroll;

        public static void Show(ContentUploaderPanel owner, bool buildBeforeUpload)
        {
            // Whether we're reusing an existing window or spawning a new one,
            // (re)opening the popup is the user's "I want to do an upload"
            // signal — so wipe any leftover completion state from the last
            // run that would otherwise pin the popup to its DrawCompletionView
            // branch. Mid-upload re-opens are a no-op (the panel guards on
            // isUploading) so we can call this unconditionally.
            if (owner != null) owner.ResetCompletionStateForNextRun();

            // First uploads must use Upload All — Patch needs a baseline and
            // Code/Previews-only need prior-version bundles to fall back to,
            // neither of which exist when v1 is the first thing this contentId
            // has ever shipped. Override the saved pref for this run only so
            // the user's normal default isn't trampled.
            UploadMode initialMode = IsFirstUpload(owner) ? UploadMode.All : UploadModePrefs.Current;

            var existing = Resources.FindObjectsOfTypeAll<ContentUploadFlowPopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].owner = owner;
                existing[0].buildBeforeUpload = buildBeforeUpload;
                existing[0].uploadMode = initialMode;
                existing[0].titleContent = new GUIContent(buildBeforeUpload ? "Compile & Upload" : "Try Reupload");
                existing[0].Focus();
                existing[0].Repaint();
                return;
            }

            var win = CreateInstance<ContentUploadFlowPopup>();
            win.owner = owner;
            win.buildBeforeUpload = buildBeforeUpload;
            win.uploadMode = initialMode;
            win.titleContent = new GUIContent(buildBeforeUpload ? "Compile & Upload" : "Try Reupload");
            win.minSize = new Vector2(620f, 720f);
            win.maxSize = new Vector2(820f, 1100f);
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - 660f) / 2f,
                main.y + (main.height - 820f) / 2f,
                660f,
                820f);
            win.ShowUtility();
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
            if (owner != null && owner.IsUploading)
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (owner == null)
            {
                EditorGUILayout.HelpBox("The Content Uploader window is no longer available.", MessageType.Info);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            if (owner.UploadCompleted)
            {
                DrawCompletionView();
                return;
            }

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            DrawSummaryCard();
            GUILayout.Space(8);
            DrawBuildTargetsCard();
            GUILayout.Space(8);
            DrawUploadModeCard();
            GUILayout.Space(8);
            DrawReleaseNotesCard();
            GUILayout.Space(8);
            DrawReleaseInsightsCard();
            GUILayout.Space(8);
            DrawStatusCard();

            GUILayout.Space(8);
            DrawHero();
            GUILayout.Space(8);

            const float actionButtonHeight = 34f;
            // The Start button label hints at the chosen scope so the user
            // gets one more chance to spot a mis-selected mode before
            // committing. CodeOnly/PreviewsOnly modes also gate the button
            // when the active bundling strategy doesn't support them.
            string cta = $"Start · {UploadModePrefs.ShortLabel(uploadMode)}";
            bool modeRequiresSmart = UploadModePrefs.RequiresSmart(uploadMode);
            bool smartActive = BundlingStrategyPrefs.Current == BundlingStrategy.Smart;
            bool modeBlocked = modeRequiresSmart && !smartActive;
            GUI.enabled = !owner.IsUploading && !modeBlocked;
            if (GUILayout.Button(cta, GUILayout.Height(actionButtonHeight)))
            {
                bool started = owner.BeginUploadFromPopup(buildBeforeUpload, uploadMode);
                if (started)
                {
                    GUI.FocusControl(null);
                    Repaint();
                }
            }
            GUI.enabled = true;
            EditorGUILayout.EndScrollView();
        }

        // True when this contentId hasn't published a server-side version
        // yet (or we haven't loaded that metadata yet, in which case we
        // err on the cautious side — All is still the safe option). Used
        // to lock the upload mode to All for the first release, since
        // Patch needs a saved baseline and Code/Previews-only need prior-
        // version bundles to fall back to.
        private static bool IsFirstUpload(ContentUploaderPanel owner)
        {
            if (owner == null) return true;
            int? v = owner.LatestPublishedVersionNumber;
            return !v.HasValue || v.Value <= 0;
        }

        // ── Upload mode picker ───────────────────────────────────────────
        // Surfaces the four shipping shapes (All / Patch / Code only /
        // Previews only) as a Popup. CodeOnly and PreviewsOnly are listed
        // even when Smart isn't active so users can discover them, but the
        // Start button gates on the strategy and a helpbox calls out the
        // mismatch.
        //
        // Two contexts hide or lock the picker entirely:
        //   - Legacy bundling: the carve-out groups Code/Previews depend on
        //     don't exist, and Legacy always sends a full upload anyway.
        //     Showing the picker would just be misleading clutter.
        //   - First upload (no prior server version): only All works on a
        //     cold start. The picker stays visible (so the user sees the
        //     options will become available later) but locks to All.
        private void DrawUploadModeCard()
        {
            // Legacy — every upload is a full re-upload by definition.
            // Skip the whole card so the popup stays focused on what the
            // user actually controls in this configuration.
            if (BundlingStrategyPrefs.Current != BundlingStrategy.Smart)
            {
                // Keep uploadMode aligned with what the engine will actually
                // do so the Start button label doesn't promise a mode the
                // pipeline is about to downgrade away from.
                if (uploadMode != UploadMode.All)
                {
                    uploadMode = UploadMode.All;
                }
                return;
            }

            bool firstUpload = IsFirstUpload(owner);
            // First-upload override: lock to All. We don't persist this to
            // UploadModePrefs because the user's saved default should
            // re-apply once they have a prior version to patch against.
            if (firstUpload && uploadMode != UploadMode.All)
            {
                uploadMode = UploadMode.All;
            }

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Upload Scope", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Pick what this run pushes to the server. Code-only and Previews-only require a prior published version to patch against.",
                EditorStyles.wordWrappedMiniLabel);

            var values = (UploadMode[])System.Enum.GetValues(typeof(UploadMode));
            var labels = values.Select(v => UploadModePrefs.Label(v)).ToArray();
            int currentIdx = System.Array.IndexOf(values, uploadMode);
            if (currentIdx < 0) currentIdx = System.Array.IndexOf(values, UploadMode.All);
            if (currentIdx < 0) currentIdx = 0;

            // Disable the whole picker on first upload — All is the only
            // viable option, and showing it as a fixed "Mode: All" while
            // every other entry would be greyed out one-by-one is noisier
            // than a single dropdown locked to its only valid value.
            using (new EditorGUI.DisabledScope(owner.IsUploading || firstUpload))
            {
                int newIdx = EditorGUILayout.Popup("Mode", currentIdx, labels);
                if (newIdx != currentIdx)
                {
                    uploadMode = values[newIdx];
                    UploadModePrefs.Current = uploadMode;
                }
            }

            EditorGUILayout.LabelField(UploadModePrefs.Description(uploadMode), EditorStyles.wordWrappedMiniLabel);

            if (firstUpload)
            {
                EditorGUILayout.HelpBox(
                    "First release for this content — Upload All is the only valid mode because " +
                    "there's no prior version yet to patch against. Patch, Code-only, and " +
                    "Previews-only will be available starting with your second upload.",
                    MessageType.Info);
            }

            // Reupload flow can't usefully ship Code/Previews-only because
            // ServerData/ may not reflect the carved-out groups if it was
            // populated by a Legacy build. We don't hard-block, but the
            // user deserves a heads-up.
            if (!buildBeforeUpload && UploadModePrefs.RequiresSmart(uploadMode))
            {
                EditorGUILayout.HelpBox(
                    "Reupload uses the current ServerData/ output as-is. If the existing build " +
                    "wasn't produced by Smart bundling, the Code/Previews bundles won't be there " +
                    "and the upload will be empty. Compile & Upload is the safer route for " +
                    $"{UploadModePrefs.ShortLabel(uploadMode)}.",
                    MessageType.Info);
            }

            GUILayout.EndVertical();
        }

        private void DrawCompletionView()
        {
            GUILayout.Space(12f);

            Rect rect = GUILayoutUtility.GetRect(10f, 118f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, owner.UploadSucceeded
                ? new Color(0.08f, 0.30f, 0.22f)
                : new Color(0.33f, 0.16f, 0.13f));

            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                fontSize = 22,
                alignment = TextAnchor.MiddleCenter
            };
            var subtitleStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = new Color(0.92f, 0.97f, 0.95f) },
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter
            };

            GUI.Label(
                new Rect(rect.x + 18f, rect.y + 24f, rect.width - 36f, 30f),
                owner.UploadSucceeded ? "Upload Complete" : "Upload Finished With Issues",
                titleStyle);
            GUI.Label(
                new Rect(rect.x + 18f, rect.y + 60f, rect.width - 36f, 36f),
                string.IsNullOrEmpty(owner.UploadStatusMessage)
                    ? (owner.UploadSucceeded ? "Your release is ready." : "Review the final status below.")
                    : owner.UploadStatusMessage,
                subtitleStyle);

            GUILayout.Space(12f);

            GUILayout.BeginVertical(EditorStyles.helpBox);
            if (owner.LogoTexture != null)
            {
                const float logoSize = 72f;
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Rect logoRect = GUILayoutUtility.GetRect(logoSize, logoSize, GUILayout.Width(logoSize), GUILayout.Height(logoSize));
                GUI.DrawTexture(logoRect, owner.LogoTexture, ScaleMode.ScaleToFit, true);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }

            var centeredTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            var centeredBodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                alignment = TextAnchor.MiddleCenter
            };
            var metaStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            string title = string.IsNullOrEmpty(owner.ContentName) ? owner.ContentId : owner.ContentName;
            EditorGUILayout.LabelField(title, centeredTitleStyle, GUILayout.MinHeight(28f));

            if (!string.IsNullOrEmpty(owner.ContentDescription))
            {
                GUILayout.Space(4f);
                float availableWidth = Mathf.Max(220f, position.width - 80f);
                float descriptionHeight = Mathf.Max(22f, centeredBodyStyle.CalcHeight(new GUIContent(owner.ContentDescription), availableWidth));
                Rect descriptionRect = GUILayoutUtility.GetRect(availableWidth, descriptionHeight, GUILayout.ExpandWidth(true));
                GUI.Label(descriptionRect, owner.ContentDescription, centeredBodyStyle);
            }

            GUILayout.Space(10f);
            EditorGUILayout.LabelField($"Content ID: {owner.ContentId}", metaStyle);
            EditorGUILayout.LabelField($"Version: {owner.GetVersionSummary()}", metaStyle);
            EditorGUILayout.LabelField($"Targets: {owner.GetBuildTargetSummary()}", metaStyle);
            GUILayout.EndVertical();

            GUILayout.Space(10f);
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Release Notes", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(owner.ReleaseNotes) ? "(none)" : owner.ReleaseNotes,
                EditorStyles.wordWrappedLabel);
            GUILayout.EndVertical();

            GUILayout.Space(10f);
            if (!string.IsNullOrEmpty(owner.UploadStatusTitle) || !string.IsNullOrEmpty(owner.UploadStatusMessage))
            {
                GUILayout.BeginVertical(EditorStyles.helpBox);
                if (!string.IsNullOrEmpty(owner.UploadStatusTitle))
                {
                    GUILayout.Label(owner.UploadStatusTitle, EditorStyles.boldLabel);
                }
                if (!string.IsNullOrEmpty(owner.UploadStatusMessage))
                {
                    EditorGUILayout.HelpBox(
                        owner.UploadStatusMessage,
                        owner.UploadSucceeded ? MessageType.Info : MessageType.Error);
                }
                GUILayout.EndVertical();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Space(12f);
            if (GUILayout.Button("Close", GUILayout.Height(34f)))
            {
                Close();
            }
        }

        private void DrawHero()
        {
            Rect rect = GUILayoutUtility.GetRect(10f, 104f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.09f, 0.27f, 0.23f));

            Rect glowRect = new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f);
            EditorGUI.DrawRect(glowRect, new Color(0.16f, 0.45f, 0.38f, 0.35f));

            var eyebrowStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                normal = { textColor = new Color(0.78f, 0.94f, 0.88f) },
                fontSize = 10
            };
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                fontSize = 20
            };
            var subtitleStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = new Color(0.86f, 0.97f, 0.93f) },
                fontSize = 11
            };

            GUI.Label(new Rect(rect.x + 18f, rect.y + 12f, rect.width - 36f, 16f), "FINAL LAUNCH CHECK", eyebrowStyle);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 30f, rect.width - 36f, 26f),
                buildBeforeUpload ? "Compile, verify, and send your release" : "Send the existing release build",
                titleStyle);

            string summary = string.IsNullOrEmpty(owner.ContentName) ? owner.ContentId : owner.ContentName;
            string detail = $"{summary}  ·  {owner.GetVersionSummary()}  ·  {owner.GetBuildTargetSummary()}";
            GUI.Label(new Rect(rect.x + 18f, rect.y + 60f, rect.width - 36f, 34f),
                detail,
                subtitleStyle);
        }

        private void DrawSummaryCard()
        {
            string title = string.IsNullOrEmpty(owner.ContentName) ? owner.ContentId : owner.ContentName;
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter
            };
            var descriptionStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };
            var metaStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter
            };

            if (owner.LogoTexture != null)
            {
                const float logoSize = 88f;
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Rect logoRect = GUILayoutUtility.GetRect(logoSize, logoSize, GUILayout.Width(logoSize), GUILayout.Height(logoSize));
                GUI.DrawTexture(logoRect, owner.LogoTexture, ScaleMode.ScaleToFit, true);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(8f);
            }

            EditorGUILayout.LabelField(title, titleStyle, GUILayout.MinHeight(28f));
            GUILayout.Space(4f);

            string description = string.IsNullOrEmpty(owner.ContentDescription) ? "No description added yet." : owner.ContentDescription;
            float availableWidth = Mathf.Max(220f, position.width - 80f);
            float descriptionHeight = Mathf.Max(22f, descriptionStyle.CalcHeight(new GUIContent(description), availableWidth));
            Rect descriptionRect = GUILayoutUtility.GetRect(availableWidth, descriptionHeight, GUILayout.ExpandWidth(true));
            GUI.Label(descriptionRect, description, descriptionStyle);

            GUILayout.Space(10f);
            EditorGUILayout.LabelField($"Content ID: {owner.ContentId}", metaStyle);
            EditorGUILayout.LabelField($"Version: {owner.GetVersionSummary()}", metaStyle);
        }

        private void DrawBuildTargetsCard()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Build Targets", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft("Android (required)", true);
                EditorGUILayout.ToggleLeft("iOS (required)", true);
            }

            using (new EditorGUI.DisabledScope(owner.IsUploading))
            {
                bool nextBuildOsx = EditorGUILayout.ToggleLeft("Editor (Mac)", owner.BuildOsx);
                if (nextBuildOsx != owner.BuildOsx)
                {
                    owner.BuildOsx = nextBuildOsx;
                }

                bool nextBuildWindows = EditorGUILayout.ToggleLeft("Editor (Windows)", owner.BuildWindows);
                if (nextBuildWindows != owner.BuildWindows)
                {
                    owner.BuildWindows = nextBuildWindows;
                }
            }

            if (!owner.BuildOsx || !owner.BuildWindows)
            {
                EditorGUILayout.HelpBox("Editor targets are required for official release", MessageType.Warning);
            }

            EditorGUILayout.LabelField("Selected targets", owner.GetBuildTargetSummary(), EditorStyles.miniLabel);
            GUILayout.EndVertical();
        }

        private void DrawReleaseNotesCard()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Release Notes", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("These notes ship into the DreamPark app and are read by end users, so write them like player-facing release notes.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(owner.IsUploading))
            {
                notesScroll = EditorGUILayout.BeginScrollView(notesScroll, GUILayout.MinHeight(100f), GUILayout.MaxHeight(150f));
                string updated = EditorGUILayout.TextArea(owner.ReleaseNotes, GUILayout.ExpandHeight(true));
                if (updated != owner.ReleaseNotes)
                {
                    owner.ReleaseNotes = updated;
                }
                EditorGUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
        }

        private void DrawReleaseInsightsCard()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Release Insights", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(owner.GetPatchEstimateSummary(), MessageType.None);

            if (buildBeforeUpload)
            {
                EditorGUILayout.LabelField(
                    owner.CleanBeforeEachTarget
                        ? "Addressables cache cleanup is enabled for every target in this run."
                        : "Addressables cache cleanup is off for this run.",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Reupload mode reuses the current ServerData output without rebuilding.",
                    EditorStyles.miniLabel);
            }
            GUILayout.EndVertical();
        }

        private void DrawStatusCard()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(owner.IsUploading ? "Launch Progress" : "Status", EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(owner.UploadStatusTitle))
            {
                EditorGUILayout.LabelField(owner.UploadStatusTitle, EditorStyles.miniBoldLabel);
            }

            if (!string.IsNullOrEmpty(owner.UploadStatusMessage))
            {
                EditorGUILayout.HelpBox(
                    owner.UploadStatusMessage,
                    owner.UploadStatusIsError ? MessageType.Error : (owner.UploadSucceeded ? MessageType.Info : MessageType.None));
            }
            else if (!owner.IsUploading)
            {
                EditorGUILayout.HelpBox("Nothing is running yet. When you start, this window will carry the compile, upload, and finish states.", MessageType.None);
            }

            if (owner.UploadStatusProgress >= 0f && owner.IsUploading)
            {
                Rect progressRect = GUILayoutUtility.GetRect(18f, 18f, "TextField");
                EditorGUI.ProgressBar(progressRect, Mathf.Clamp01(owner.UploadStatusProgress), $"{owner.UploadStatusProgress * 100f:0}%");
                GUILayout.Space(6f);
            }

            var progressEntries = owner.GetProgressEntries();
            if (progressEntries != null && progressEntries.Count > 0)
            {
                float overall = progressEntries.Average(e => e.progress);
                long uploadedBytes = progressEntries.Sum(e => e.uploadedBytes);
                long totalBytes = progressEntries.Sum(e => e.totalBytes);
                int doneCount = progressEntries.Count(e => e.completed);
                int failedCount = progressEntries.Count(e => e.failed);

                EditorGUILayout.LabelField(
                    $"{doneCount}/{progressEntries.Count} files complete · {FormatBytes(uploadedBytes)} of {FormatBytes(totalBytes)}",
                    EditorStyles.miniLabel);
                Rect uploadRect = GUILayoutUtility.GetRect(18f, 18f, "TextField");
                string uploadLabel = failedCount > 0
                    ? $"{overall * 100f:0}% · {failedCount} failed"
                    : $"{overall * 100f:0}%";
                EditorGUI.ProgressBar(uploadRect, Mathf.Clamp01(overall), uploadLabel);
                GUILayout.Space(6f);

                progressScroll = EditorGUILayout.BeginScrollView(progressScroll, GUILayout.MinHeight(140f), GUILayout.MaxHeight(220f));
                foreach (var entry in progressEntries)
                {
                    string status = entry.failed ? "Failed" : (entry.completed ? "Done" : "Uploading");
                    EditorGUILayout.LabelField($"{entry.platform} / {entry.fileName}", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(
                        $"{status}  ·  {FormatBytes(entry.uploadedBytes)} / {FormatBytes(entry.totalBytes)}  ·  {(entry.progress * 100f):0.0}%",
                        EditorStyles.miniLabel);
                    Rect rowRect = GUILayoutUtility.GetRect(16f, 16f, "TextField");
                    EditorGUI.ProgressBar(rowRect, Mathf.Clamp01(entry.progress), $"{entry.progress * 100f:0.0}%");
                    GUILayout.Space(4f);
                }
                EditorGUILayout.EndScrollView();
            }

            if (owner.UploadCompleted && owner.UploadSucceeded)
            {
                GUILayout.Space(6f);
                EditorGUILayout.LabelField("Release landed cleanly. Nice work.", EditorStyles.miniBoldLabel);
            }

            GUILayout.EndVertical();
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
