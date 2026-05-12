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
        private Vector2 progressScroll;
        private Vector2 notesScroll;
        private Vector2 mainScroll;

        public static void Show(ContentUploaderPanel owner, bool buildBeforeUpload)
        {
            var existing = Resources.FindObjectsOfTypeAll<ContentUploadFlowPopup>();
            if (existing != null && existing.Length > 0)
            {
                existing[0].owner = owner;
                existing[0].buildBeforeUpload = buildBeforeUpload;
                existing[0].titleContent = new GUIContent(buildBeforeUpload ? "Compile & Upload" : "Try Reupload");
                existing[0].Focus();
                existing[0].Repaint();
                return;
            }

            var win = CreateInstance<ContentUploadFlowPopup>();
            win.owner = owner;
            win.buildBeforeUpload = buildBeforeUpload;
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
            DrawReleaseNotesCard();
            GUILayout.Space(8);
            DrawReleaseInsightsCard();
            GUILayout.Space(8);
            DrawStatusCard();

            GUILayout.Space(8);
            DrawHero();
            GUILayout.Space(8);

            const float actionButtonHeight = 34f;
            string cta = "Start";
            GUI.enabled = !owner.IsUploading;
            if (GUILayout.Button(cta, GUILayout.Height(actionButtonHeight)))
            {
                bool started = owner.BeginUploadFromPopup(buildBeforeUpload);
                if (started)
                {
                    GUI.FocusControl(null);
                    Repaint();
                }
            }
            GUI.enabled = true;
            EditorGUILayout.EndScrollView();
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
