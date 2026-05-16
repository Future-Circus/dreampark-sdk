#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEngine;
using System.IO;
using DreamPark.API;
using DreamPark.Editor;
using System;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using Defective.JSON;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.AddressableAssets;
using System.Collections.Generic;

namespace DreamPark {
    public class ContentUploaderPanel : EditorWindow
    {
        private string contentId = "";
        private string contentName = "";
        private string contentDescription = "";
        private string releaseNotes = "";
        private Texture2D logoTexture = null;
        private bool isUploading = false;
        private bool isLoadingMetadata = false;
        private int? latestPublishedVersionNumber = null;
        private int? lastSchemaVersion = null;
        private string uploadStatusTitle = "";
        private string uploadStatusMessage = "";
        private float uploadStatusProgress = -1f;
        private bool uploadStatusIsError = false;
        private bool uploadCompleted = false;
        private bool uploadSucceeded = false;
        private bool uploadBuildMode = true;
        // Set by BeginUploadFromPopup before the async UploadContent flow
        // starts so the inner skip-set computation can route through
        // UploadModeFilter for the chosen mode. Defaults to Patch — the
        // historical behavior when older code paths call UploadContent
        // without explicitly picking a mode.
        private UploadMode pendingUploadMode = UploadMode.Patch;

        // Set by BeginUploadFromPopup when the user picks "Upload Failed
        // Bundles" on the Try Reupload dialog. Overrides pendingUploadMode
        // and reroutes the upload through FailedBundleStore so only the
        // bundles that failed in the previous run get re-sent, while the
        // commitUpload payload still references the full set (previous
        // successes + this run's successes). Cleared at the end of every
        // upload run so a subsequent normal upload doesn't accidentally
        // pick up the flag.
        private bool pendingFailedOnly = false;

        // Main panel scroll. Persists for the lifetime of the window so scroll
        // position doesn't reset every time OnGUI runs (which is many times
        // per second). Reset would feel jumpy as the user types.
        private Vector2 mainScroll;

        private const string SectionContentInfoPrefKey = "DreamPark.ContentUploader.Section.ContentInfo";
        private const string SectionTeamPrefKey = "DreamPark.ContentUploader.Section.Team";
        private const string SectionContentOverviewPrefKey = "DreamPark.ContentUploader.Section.ContentOverview";
        private const string SectionTroubleshootingPrefKey = "DreamPark.ContentUploader.Section.Troubleshooting";
        private const string SectionPreLaunchPrefKey = "DreamPark.ContentUploader.Section.PreLaunch";
        private const string SectionReleaseLaunchPrefKey = "DreamPark.ContentUploader.Section.ReleaseLaunch";
        private bool foldContentInfo = true;
        private bool foldTeam = true;
        private bool foldContentOverview = true;
        private bool foldTroubleshooting = false;
        // Pre Launch Options defaults to expanded — these are the
        // "do-this-before-you-publish" tools (texture optimizer, etc.),
        // and we want them visible at the moment the creator's about to
        // hit Release. Hiding them behind a foldout would defeat the
        // point of moving the optimizer into the upload flow.
        private bool foldPreLaunch = true;
        private bool foldReleaseLaunch = true;

        // Foldout state for the "Park Assets" preview block. EditorPrefs-
        // backed so collapse choices survive Unity restarts and domain
        // reloads — otherwise the user has to re-collapse every recompile.
        private const string ParkAssetsFoldPrefKey       = "DreamPark.ContentUploader.Fold.ParkAssets";
        private const string ParkAssetsAttractionsPrefKey = "DreamPark.ContentUploader.Fold.ParkAssets.Attractions";
        private const string ParkAssetsPropsPrefKey       = "DreamPark.ContentUploader.Fold.ParkAssets.Props";
        private const string ParkAssetsPlayerPrefKey      = "DreamPark.ContentUploader.Fold.ParkAssets.Player";
        private bool parkAssetsFold = true;
        private bool foldAttractions = true;
        private bool foldProps = true;
        private bool foldPlayer = true;

        private List<string> contentIdOptions = new List<string>();
        private int contentIdIndex = 0;

        // Team / collaborators state. Loaded from /api/content/:contentId/users
        private class TeamMember
        {
            public string userId;
            public string email;
        }
        private List<TeamMember> teamMembers = new List<TeamMember>();
        private string teamPrimaryOwnerId = null;
        private bool isLoadingTeam = false;
        private string teamErrorMessage = null;

        // Access state from the most recent FetchContentMetadata call.
        // null = unknown / not yet checked, true = we own (200 or 404 — fresh content),
        // false = 403 (someone else owns this contentId). Used to gate the panel
        // without leaking owner identity.
        private bool? isContentAccessibleByMe = null;
        private const string LogoPrefKeyPrefix = "DreamPark.ContentUploader.LogoPath.";
        private const string ContentIdPrefKey = "DreamPark.ContentUploader.LastContentId";
        private const string BuildAndroidPrefKey = "DreamPark.ContentUploader.Build.Android";
        private const string BuildIosPrefKey = "DreamPark.ContentUploader.Build.iOS";
        private const string BuildOsxPrefKey = "DreamPark.ContentUploader.Build.StandaloneOSX";
        private const string BuildWindowsPrefKey = "DreamPark.ContentUploader.Build.StandaloneWindows";
        private const string CleanBeforeEachTargetPrefKey = "DreamPark.ContentUploader.Build.CleanBeforeEachTarget";
        private static readonly HashSet<string> BuiltInUnityTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "Untagged",
            "Respawn",
            "Finish",
            "EditorOnly",
            "MainCamera",
            "Player",
            "GameController"
        };
        private bool buildAndroid = true;
        private bool buildIos = true;
        private bool buildOsx = true;
        private bool buildWindows = true;
        private bool cleanBeforeEachTarget = false;

        // Patch-estimator state. Cached so we don't re-walk ServerData every
        // frame; refreshed on panel open, contentId change, target-toggle
        // change, after a successful build, and when the user explicitly
        // clicks the refresh button.
        private BuildManifest patchBaseline;
        private BuildManifest patchCurrentSnapshot;
        private BuildManifestDiff patchDiff;
        private string patchEstimateContentId;
        private DateTime patchEstimateComputedAt;

        // Source-aware estimate: matches dirty-groups (touched in real-time
        // by the ContentFolderWatchdog) against bundle filenames in the
        // baseline manifest. Tells the user "what would change if I
        // uploaded right now" without having to actually run a build.
        private DirtyGroupsEstimate dirtyGroupsEstimate;

        // ── "What you're uploading" preview state ──────────────────────
        // Cached list of root prefabs (Attractions, Props, Player rig) that
        // would actually ship in this content's bundles, plus their
        // matching preview thumbnails. Refreshed on panel open, contentId
        // change, and (deferred-debounced) on AssetDatabase project changes.
        private enum ContentRootKind { Attraction, Prop, Player }
        private class ContentRootEntry
        {
            public string assetPath;
            public string name;
            public ContentRootKind kind;
            // Optional hand-curated screenshot from Previews/{name}.{png,jpg}.
            // Null when the user hasn't dropped one in — DrawCard falls back to
            // Unity's auto-generated AssetPreview at draw time.
            public Texture2D customPreview;
            // Cached prefab asset reference — avoids re-doing
            // AssetDatabase.LoadMainAssetAtPath on every paint.
            public UnityEngine.Object cachedAsset;
            // Last AssetPreview snapshot. Treated as a hint, not a permanent
            // store — Unity may evict from its preview cache, so we always
            // re-fetch in DrawCard and update this field.
            public Texture2D autoPreview;
            // True once AssetPreview has handed back a non-null thumbnail at
            // least once. Drives the OnGUI repaint loop so we keep polling
            // until every root is resolved.
            public bool autoPreviewResolved;
            // First wall-clock moment we asked for this preview. Used to
            // bail out of the polling loop if AssetPreview never produces a
            // rich render — common for empty container prefabs (just a
            // LevelTemplate component, no mesh) where Unity simply won't
            // generate a preview. We accept the fallback icon and stop
            // burning CPU on repaints.
            public double firstPollTime;
            public string subLabel;
        }
        private List<ContentRootEntry> contentRoots = new List<ContentRootEntry>();
        private string contentRootsContentId;
        private bool contentRootsDirty;

        // priority 0 pins Content Uploader to the top of the DreamPark menu;
        // the big priority gap to the next item (Multiplayer at 100) creates
        // a separator so it sits in its own section.
        [MenuItem("DreamPark/Content Uploader", false, 0)]
        public static void ShowWindow()
        {
            GetWindow<ContentUploaderPanel>("Content Uploader");
        }

        // Listen for folder changes in Assets/Content and update content options

        private static FileSystemWatcher contentFolderWatcher;

        [InitializeOnLoadMethod]
        private static void InitContentFolderWatcher()
        {
            string path = Path.Combine(Application.dataPath, "Content");
            if (!Directory.Exists(path)) return;

            if (contentFolderWatcher != null)
            {
                contentFolderWatcher.EnableRaisingEvents = false;
                contentFolderWatcher.Dispose();
            }

            contentFolderWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            contentFolderWatcher.Created += (s, e) =>
            {
                if (Directory.Exists(e.FullPath))
                {
                    // Folder created
                    EditorApplication.delayCall += () =>
                    {
                        // Find all open ContentUploaderPanels and refresh their content options
                        foreach (ContentUploaderPanel win in Resources.FindObjectsOfTypeAll<ContentUploaderPanel>())
                        {
                            win.RefreshContentIdOptions();
                            win.Repaint();
                        }
                    };
                }
            };

            contentFolderWatcher.Deleted += (s, e) =>
            {
                EditorApplication.delayCall += () =>
                {
                    // Find all open ContentUploaderPanels and refresh their content options
                    foreach (ContentUploaderPanel win in Resources.FindObjectsOfTypeAll<ContentUploaderPanel>())
                    {
                        win.RefreshContentIdOptions();
                        win.Repaint();
                    }
                };
            };
        }

        private void OnEnable()
        {
            RefreshContentIdOptions();
            ContentAPI.UploadProgressChanged += OnUploadProgressChanged;
            AuthAPI.LoginStateChanged += OnLoginStateChanged;
            SDKUpdateChecker.ManifestUpdated += OnManifestUpdated;
            EditorApplication.projectChanged += OnProjectChangedForRoots;

            RestoreContentIdSelection();
            LoadBuildTargetSelection();
            LoadFoldoutPrefs();
            LoadSectionFoldoutPrefs();

            LoadLogoSelection();
            FetchContentMetadata();
            FetchContentUsers();
            RefreshPatchEstimate();
            RefreshContentRoots();
        }

        private void LoadFoldoutPrefs()
        {
            parkAssetsFold  = EditorPrefs.GetBool(ParkAssetsFoldPrefKey,        true);
            foldAttractions = EditorPrefs.GetBool(ParkAssetsAttractionsPrefKey, true);
            foldProps       = EditorPrefs.GetBool(ParkAssetsPropsPrefKey,       true);
            foldPlayer      = EditorPrefs.GetBool(ParkAssetsPlayerPrefKey,      true);
        }

        private void LoadSectionFoldoutPrefs()
        {
            foldContentInfo = EditorPrefs.GetBool(SectionContentInfoPrefKey, true);
            foldTeam = EditorPrefs.GetBool(SectionTeamPrefKey, true);
            foldContentOverview = EditorPrefs.GetBool(SectionContentOverviewPrefKey, true);
            foldTroubleshooting = EditorPrefs.GetBool(SectionTroubleshootingPrefKey, false);
            foldPreLaunch = EditorPrefs.GetBool(SectionPreLaunchPrefKey, true);
            foldReleaseLaunch = EditorPrefs.GetBool(SectionReleaseLaunchPrefKey, true);
        }

        // ProjectChanged fires for every asset save/import/move which can be
        // dozens of times per second during big imports. We just mark dirty
        // and let the next OnGUI call refresh once — coalesces the storm
        // into a single rebuild of the preview list.
        private void OnProjectChangedForRoots()
        {
            contentRootsDirty = true;
            Repaint();
        }

        private void OnDisable()
        {
            ContentAPI.UploadProgressChanged -= OnUploadProgressChanged;
            AuthAPI.LoginStateChanged -= OnLoginStateChanged;
            SDKUpdateChecker.ManifestUpdated -= OnManifestUpdated;
            EditorApplication.projectChanged -= OnProjectChangedForRoots;
        }

        private void OnManifestUpdated() => Repaint();

        // Called by ContentIdSetupPopup after a successful rename. Refreshes the
        // dropdown options and selects the newly-named folder so the panel
        // immediately moves past the rename gate.
        private void OnContentFolderRenamed(string newFolderName)
        {
            RefreshContentIdOptions();
            int idx = contentIdOptions.IndexOf(newFolderName);
            if (idx >= 0)
            {
                contentIdIndex = idx;
                contentId = newFolderName;
                SaveContentIdSelection();
            }
            // Force fresh metadata + team list for the new id.
            releaseNotes = "";
            LoadLogoSelection();
            FetchContentMetadata();
            FetchContentUsers();
            RefreshPatchEstimate();
            RefreshContentRoots();
            Repaint();
        }

        // Repaint and re-fetch content lists when login state changes — e.g. after
        // AuthState detects an expired session, or after the user logs in via the popup.
        private void OnLoginStateChanged(bool isLoggedIn)
        {
            if (isLoggedIn)
            {
                FetchContentMetadata();
                FetchContentUsers();
            }
            else
            {
                // Wipe per-content state so we don't show a stale team list to the
                // next user who logs in.
                teamMembers.Clear();
                teamPrimaryOwnerId = null;
                teamErrorMessage = null;
            }
            Repaint();
        }

        private void OnUploadProgressChanged()
        {
            Repaint();
        }

        private void RefreshContentIdOptions()
        {
            contentIdOptions.Clear();
            string contentPath = Path.Combine(Application.dataPath, "Content");
            if (Directory.Exists(contentPath))
            {
                var dirs = Directory.GetDirectories(contentPath)
                    .Select(d => Path.GetFileName(d))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .OrderBy(d => d)
                    .ToList();
                contentIdOptions.AddRange(dirs);
            }
            else
            {
                Debug.LogWarning("No Assets/Content folder exists in this project.");
            }

            // Sanity - no invalid selection
            if (contentIdOptions.Count == 0)
            {
                contentId = "";
                contentIdIndex = 0;
            }
        }

        private void OnGUI()
        {
            // Auth gate: if logged out, the rest of the panel is hidden behind a
            // single Login CTA. Authentication itself happens in AuthPopup.
            if (!AuthAPI.isLoggedIn)
            {
                DrawLoginGate("Log in to use the Content Uploader.");
                return;
            }

            // Wrap the entire configuration UI in a scroll view. Without this
            // the panel runs off-screen on smaller windows once the content
            // preview and troubleshooting sections both open up.
            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);

            // Compact logged-in header — full email + Logout
            GUILayout.BeginHorizontal();
            string displayEmail = !string.IsNullOrEmpty(AuthAPI.email) ? AuthAPI.email : ("uid: " + AuthAPI.userId);
            EditorGUILayout.LabelField("Signed in as " + displayEmail, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Log out", GUILayout.Width(70)))
            {
                Logout();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("Content Uploader", EditorStyles.boldLabel);

            // ContentId dropdown
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Content ID", GUILayout.Width(EditorGUIUtility.labelWidth));
            int prevIndex = contentIdIndex;

            // Disable while uploading — the change handler below wipes
            // releaseNotes and fires metadata refetches, which would silently
            // discard the user's typed release notes and put the panel into
            // a confusing state mid-upload.
            EditorGUI.BeginDisabledGroup(contentIdOptions.Count == 0 || isUploading);

            contentIdIndex = EditorGUILayout.Popup(contentIdIndex, contentIdOptions.ToArray());
            if (contentIdOptions.Count > 0)
            {
                // Only set contentId if a valid selection is available
                contentId = contentIdOptions[contentIdIndex];
            }
            else
            {
                contentId = "";
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();
            if (prevIndex != contentIdIndex)
            {
                releaseNotes = "";
                SaveContentIdSelection();
                LoadLogoSelection();
                FetchContentMetadata();
                FetchContentUsers();
                RefreshPatchEstimate();
                RefreshContentRoots();
            }

            // Deferred refresh: the projectChanged callback only sets a
            // dirty flag — we coalesce the rebuild to one pass here per
            // OnGUI tick so a big import doesn't thrash the preview list.
            // Also re-syncs if the cached content id drifts from the
            // selected one (e.g. dropdown restored from prefs).
            if ((contentRootsDirty || contentRootsContentId != contentId) && !isUploading)
            {
                RefreshContentRoots();
            }

            if (contentIdOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No content folders found under Assets/Content. Please create at least one game/content folder.", MessageType.Warning);
            }

            // ── Gate 1: placeholder folder name (SDK template default).
            //    User must rename YOUR_GAME_HERE to a real ID before doing anything.
            if (contentId == ContentIdSetupPopup.PlaceholderName)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "Please give your game an ID (ex: SuperAdventureLand). Your game folder is still the SDK template.",
                    MessageType.Warning);
                if (GUILayout.Button("Set Content ID", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            // ── Gate 2: existing folder has unsafe characters (dashes, spaces,
            //    punctuation). Anything that fails the same regex the popup uses
            //    breaks Addressables / upload paths, so we refuse to proceed.
            if (!string.IsNullOrEmpty(contentId) && !ContentIdSetupPopup.IsValid(contentId))
            {
                GUILayout.Space(8);
                string why = ContentIdSetupPopup.ExplainInvalid(contentId) ?? "Invalid folder name.";
                EditorGUILayout.HelpBox(
                    $"Your content folder '{contentId}' has an invalid name: {why} Rename it to use only letters and digits.",
                    MessageType.Error);
                if (GUILayout.Button("Fix Folder Name", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            // ── Gate 3: backend says we don't have access to this contentId.
            //    Some other user owns it. Show a generic message — NO emails, no
            //    owner identity, nothing that confirms anything about the other
            //    user. The fix is to rename the local folder to something else.
            if (isContentAccessibleByMe == false)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "You do not have access to this project. Rename your folder to a different ID to upload.",
                    MessageType.Error);
                if (GUILayout.Button("Rename Folder", GUILayout.Height(28)))
                {
                    ContentIdSetupPopup.Show(contentId, OnContentFolderRenamed);
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            GUILayout.Space(5);
            if (BeginSectionBox(ref foldContentInfo, SectionContentInfoPrefKey, "Content Info", "d_Prefab Icon"))
            {
                contentName = EditorGUILayout.TextField("Name", contentName);
                EditorGUILayout.LabelField("Description");
                contentDescription = EditorGUILayout.TextArea(contentDescription, GUILayout.MinHeight(52));
                logoTexture = (Texture2D)EditorGUILayout.ObjectField("Logo", logoTexture, typeof(Texture2D), false);
                if (isLoadingMetadata)
                {
                    EditorGUILayout.HelpBox("Loading content metadata from backend...", MessageType.Info);
                }
                EndSectionBox();
            }

            if (BeginSectionBox(ref foldTeam, SectionTeamPrefKey, "Team", "d_UnityEditor.InspectorWindow"))
            {
                DrawTeamSection();
                EndSectionBox();
            }

            if (BeginSectionBox(ref foldContentOverview, SectionContentOverviewPrefKey, "Content Overview", "d_SceneViewFx"))
            {
                DrawBundlingStrategySection();
                GUILayout.Space(6);
                DrawContentPreviewSection();
                EndSectionBox();
            }

            GUILayout.Space(6);

            // Upload gate: if the manifest fetch succeeded AND the local SDK
            // version is older than the published latest, block uploads. We
            // fail open on manifest errors (offline, 500, etc.) so a transient
            // backend issue doesn't lock everyone out.
            bool sdkOutOfDate = SDKUpdateChecker.ManifestFetchSucceeded
                                && !string.IsNullOrEmpty(SDKUpdateChecker.LatestVersion)
                                && SDKVersion.Compare(SDKVersion.Current, SDKUpdateChecker.LatestVersion) < 0;
            if (sdkOutOfDate)
            {
                EditorGUILayout.HelpBox(
                    $"Your DreamPark SDK is out of date (installed v{SDKVersion.Current}, latest v{SDKUpdateChecker.LatestVersion}). " +
                    "Update before uploading content to avoid version drift between creators.",
                    MessageType.Error);
                if (GUILayout.Button("Update SDK Now", GUILayout.Height(28)))
                {
                    UpdateAvailablePopup.Show(
                        SDKVersion.Current,
                        SDKUpdateChecker.LatestVersion,
                        SDKUpdateChecker.LatestReleaseNotes,
                        SDKUpdateChecker.LatestDownloadUrl);
                }
                GUILayout.Space(6);
            }

            if (BeginSectionBox(ref foldTroubleshooting, SectionTroubleshootingPrefKey, "Troubleshooting", "d_console.warnicon"))
            {
                DrawTroubleshootingSection();
                EndSectionBox();
            }

            // Pre Launch Options sits right above Release Launch so the
            // optimization tools are visible at the moment the creator's
            // about to publish. Anything that should be sanity-checked
            // before sending content to the OTA pipeline lives here.
            if (BeginSectionBox(ref foldPreLaunch, SectionPreLaunchPrefKey, "Pre Launch Options", "d_CustomTool"))
            {
                DrawPreLaunchSection();
                EndSectionBox();
            }

            if (BeginSectionBox(ref foldReleaseLaunch, SectionReleaseLaunchPrefKey, "Release Launch", "d_BuildSettings.Editor.Small"))
            {
                DrawLaunchActions(sdkOutOfDate);
                EndSectionBox();
            }

            EditorGUILayout.EndScrollView();
        }

        private bool BeginSectionBox(ref bool foldState, string prefKey, string title, string iconName)
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            var icon = EditorGUIUtility.IconContent(iconName);
            string headerTitle = icon != null && icon.image != null ? $" {title}" : title;
            bool nextState = EditorGUILayout.BeginFoldoutHeaderGroup(foldState, new GUIContent(headerTitle, icon != null ? icon.image : null));
            if (nextState != foldState)
            {
                foldState = nextState;
                EditorPrefs.SetBool(prefKey, foldState);
            }

            if (!foldState)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                GUILayout.EndVertical();
                return false;
            }

            GUILayout.Space(4);
            return true;
        }

        private void EndSectionBox()
        {
            EditorGUILayout.EndFoldoutHeaderGroup();
            GUILayout.EndVertical();
        }

        private void DrawLaunchActions(bool sdkOutOfDate)
        {
            bool shippable = HasShippableContent();
            bool hasBuildArtifacts = patchCurrentSnapshot != null && patchCurrentSnapshot.TotalFileCount > 0;
            bool canLaunch = !isUploading
                             && !string.IsNullOrEmpty(contentId)
                             && !string.IsNullOrEmpty(contentName)
                             && !sdkOutOfDate
                             && shippable;

            if (isUploading)
            {
                EditorGUILayout.HelpBox(
                    "An upload is currently running in the launch window. You can keep editing prep here, but upload actions stay locked until that run finishes.",
                    MessageType.Info);
                if (GUILayout.Button("Open Upload Window", GUILayout.Height(24)))
                {
                    ContentUploadFlowPopup.Show(this, uploadBuildMode);
                }
                GUILayout.Space(6);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Final title, description, release notes, build targets, and live progress now happen in the launch window so this screen can stay focused on setup.",
                    MessageType.None);
            }

            GUI.enabled = canLaunch;
            string compileLabel = shippable
                ? "Compile & Upload"
                : "Compile & Upload (add an Attraction or Prop first)";
            if (GUILayout.Button(compileLabel, GUILayout.Height(34)))
            {
                SaveLogoSelection();
                ContentUploadFlowPopup.Show(this, true);
            }
            GUI.enabled = true;

            GUI.enabled = canLaunch && hasBuildArtifacts;
            string reuploadLabel = hasBuildArtifacts
                ? "Try Reupload"
                : "Try Reupload (no build artifacts)";
            if (GUILayout.Button(new GUIContent(reuploadLabel,
                hasBuildArtifacts
                    ? "Re-upload the contents of ServerData/ without rebuilding."
                    : "Run Compile & Upload first — ServerData/ is empty."),
                GUILayout.Height(28)))
            {
                SaveLogoSelection();

                // If the previous upload for this content left a failed-run
                // record behind, give the user the choice between re-sending
                // only the bundles that failed last time vs. re-uploading the
                // whole batch. No record (or no retryable failures in it)
                // means the standard "Reupload All" path with no extra
                // friction — same UX as before this feature existed.
                bool useFailedOnly = false;
                var failedRecord = !string.IsNullOrEmpty(contentId)
                    ? FailedBundleStore.Load(contentId)
                    : null;
                if (failedRecord != null && failedRecord.HasRetryableFailures)
                {
                    // DisplayDialogComplex button slots:
                    //   ok  → "Upload Failed Only ({failed})"  → choice 0
                    //   cancel → "Cancel"                       → choice 1
                    //   alt → "Reupload All ({total})"          → choice 2
                    int choice = EditorUtility.DisplayDialogComplex(
                        "Retry Upload",
                        $"Last upload had {failedRecord.FailedCount} of {failedRecord.totalFiles} bundle(s) fail.\n\n" +
                        $"• Upload Failed Only: re-send just the {failedRecord.FailedCount} failed bundle(s). " +
                        $"The {failedRecord.SucceededCount} bundle(s) that already uploaded last time will be reused " +
                        $"and committed alongside.\n\n" +
                        $"• Reupload All: ignore the previous run and re-send every bundle currently in ServerData/.",
                        $"Upload Failed Only ({failedRecord.FailedCount})",
                        "Cancel",
                        $"Reupload All ({failedRecord.totalFiles})");
                    if (choice == 1) return; // Cancel — leave panel state untouched.
                    useFailedOnly = (choice == 0);
                }

                ContentUploadFlowPopup.Show(this, false, useFailedOnly);
            }
            GUI.enabled = true;

            if (uploadCompleted)
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(uploadStatusMessage)
                        ? (uploadSucceeded ? "Upload complete." : "Upload ended with an issue.")
                        : uploadStatusMessage,
                    uploadSucceeded ? MessageType.Info : MessageType.Error);
            }
        }

        // Pre Launch Options: optimization tools the creator should run
        // before publishing. Each tool opens its own review window — we
        // keep this section minimal (just buttons) because every tool
        // surfaces its own header card and description on open. Each
        // button's tooltip carries the one-liner about what the tool does.
        private void DrawPreLaunchSection()
        {
            EditorGUILayout.HelpBox(
                "Run these before you publish — they shrink the bundle size your players download and "
                + "speed up first-launch attraction load times.",
                MessageType.None);

            GUILayout.Space(4);

            // Helper: build a 28px-tall button with a Unity built-in icon
            // (asset-type icon matching the tool's domain). The icons make
            // the optimizer suite read as a first-class, official part of
            // the SDK — same visual weight as Unity's own toolbars.
            //
            // EditorGUIUtility.IconContent auto-picks the right theme
            // variant (light / dark), so we just pass the canonical name.
            // Asset-type icons like "Material Icon", "Texture Icon",
            // "AudioClip Icon" have been stable across every Unity 2019+
            // release.
            bool ToolButton(string label, string iconName, string tooltip)
            {
                var iconContent = EditorGUIUtility.IconContent(iconName);
                var content = new GUIContent(" " + label, iconContent?.image, tooltip);
                return GUILayout.Button(content, GUILayout.Height(28));
            }

            if (ToolButton(
                    "Open Material Converter...",
                    "Material Icon",
                    "Scan every material in this park's content folder. Flips Standard / URP / vendor "
                    + "shaders to DreamPark-Universal (lit), DreamPark-Unlit (flat), or DreamPark/Particles. "
                    + "Per-row review before any material is touched; GUIDs preserved so prefab "
                    + "references stay intact. Cuts shader-variant duplication across bundles."))
            {
                DreamPark.EditorTools.MaterialConversion.MaterialConverterWindow.Open();
            }

            if (ToolButton(
                    "Open Texture Optimizer...",
                    "Texture Icon",
                    "Scan every texture in this park's content folder. Converts oversized .tga / .tif sources "
                    + "to PNG (alpha) or JPG (opaque) and picks 256 / 512 / 1024 based on the largest prop "
                    + "using each texture. Per-row review before any file is touched; Unity GUIDs preserved."))
            {
                DreamPark.EditorTools.TextureOptimization.TextureOptimizerWindow.Open();
            }

            if (ToolButton(
                    "Open Animation Optimizer...",
                    "AnimationClip Icon",
                    "Scan every .anim and FBX sub-clip in this park's content folder. Routes each clip through "
                    + "Unity's ModelImporter keyframe reducer — standalones round-trip through their source "
                    + "FBX with GUID preservation, sub-clips compress in place."))
            {
                DreamPark.EditorTools.AnimationOptimization.AnimationOptimizerWindow.Open();
            }

            if (ToolButton(
                    "Open Audio Optimizer...",
                    "AudioClip Icon",
                    "Scan every AudioClip in this park's content folder. Re-encodes oversized WAVs as "
                    + "Vorbis or ADPCM (matched to clip duration and use case), down-mixes to mono where "
                    + "appropriate, and resamples to 22 kHz / 44 kHz based on the clip's role. Per-row "
                    + "review before any file is re-encoded; GUIDs preserved."))
            {
                DreamPark.EditorTools.AudioOptimization.AudioOptimizerWindow.Open();
            }

            if (ToolButton(
                    "Open Bundle Size Breakdown...",
                    "Package Manager",
                    "Pack this park's content into addressable bundles and inspect what's taking up space. "
                    + "Use this to verify the texture, audio, and animation optimizers actually shrank what "
                    + "you expected before you publish."))
            {
                DreamPark.Diagnostics.BundleSizeBreakdown.Open();
            }
        }

        private void DrawTroubleshootingSection()
        {
            EditorGUILayout.HelpBox(
                "Use these tools when a release needs a little extra inspection before you send it.",
                MessageType.None);

            bool newCleanBeforeEachTarget = EditorGUILayout.ToggleLeft("Clean Addressables Before Each Target", cleanBeforeEachTarget);
            if (newCleanBeforeEachTarget != cleanBeforeEachTarget)
            {
                cleanBeforeEachTarget = newCleanBeforeEachTarget;
                SaveBuildTargetSelection();
            }

            GUILayout.Space(6);
            bool shippable = HasShippableContent();
            GUI.enabled = !isUploading && !string.IsNullOrEmpty(contentId) && shippable;
            if (GUILayout.Button(new GUIContent(
                "Build & Inspect Groups (no upload)",
                "Runs the full bundling pipeline for the current build target (third-party sync, " +
                "addressable group update, Smart partitioning if enabled, build) without uploading. " +
                "Opens the Addressables Groups window when done."),
                GUILayout.Height(22)))
            {
                if (!SaveModifiedScenesBeforeCompile())
                {
                    EditorUtility.DisplayDialog("Cancelled", "Save all modified scenes before building.", "OK");
                }
                else
                {
                    SaveLogoSelection();
                    RunBuildAndInspect();
                }
            }
            GUI.enabled = true;
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

        internal string ContentId => contentId;
        internal string ContentName => contentName;
        internal string ContentDescription => contentDescription;
        internal bool IsUploading => isUploading;
        internal bool UploadCompleted => uploadCompleted;
        internal bool UploadSucceeded => uploadSucceeded;
        internal bool UploadBuildMode => uploadBuildMode;
        internal string UploadStatusTitle => uploadStatusTitle;
        internal string UploadStatusMessage => uploadStatusMessage;
        internal float UploadStatusProgress => uploadStatusProgress;
        internal bool UploadStatusIsError => uploadStatusIsError;
        internal int? LatestPublishedVersionNumber => latestPublishedVersionNumber;
        internal Texture2D LogoTexture => logoTexture;

        internal string ReleaseNotes
        {
            get => releaseNotes;
            set => releaseNotes = value ?? "";
        }

        internal string GetBuildTargetSummary()
        {
            var targets = new List<string>();
            targets.Add("Android");
            targets.Add("iOS");
            if (buildOsx) targets.Add("Editor (Mac)");
            if (buildWindows) targets.Add("Editor (Windows)");
            if (targets.Count == 0) return "No targets selected";
            return string.Join(" · ", targets);
        }

        internal string GetUploadModeSummary(bool build)
        {
            if (build)
            {
                return "Fresh compile, bundle build, and upload";
            }
            return "Reuse current ServerData build artifacts and upload only";
        }

        internal string GetVersionSummary()
        {
            int current = latestPublishedVersionNumber ?? 0;
            int next = current + 1;
            return current <= 0 ? $"First release → v{next}" : $"v{current} → v{next}";
        }

        private static string GetVersionSummaryAfterUpload(int uploadedVersion)
        {
            return $"v{uploadedVersion}";
        }

        internal string GetPatchEstimateSummary()
        {
            bool patchingEnabled = IsPatchUploadEnabled();
            if (patchCurrentSnapshot == null || patchCurrentSnapshot.TotalFileCount == 0)
            {
                return "No build artifacts yet. Compile & Upload will create the first bundle set.";
            }

            if (!patchingEnabled)
            {
                return $"Full upload: {patchCurrentSnapshot.TotalFileCount} files · {BuildManifestStore.FormatBytes(patchCurrentSnapshot.TotalBytes)}";
            }

            if (patchDiff == null)
            {
                return $"Build snapshot ready: {patchCurrentSnapshot.TotalFileCount} files · {BuildManifestStore.FormatBytes(patchCurrentSnapshot.TotalBytes)}";
            }

            string baselineLine;
            if (patchBaseline == null)
            {
                baselineLine = "No baseline yet. The next upload will establish the first patch baseline.";
            }
            else
            {
                string baselineWhen = "recently";
                if (System.DateTime.TryParse(patchBaseline.buildTimestampUtc, out var ts))
                    baselineWhen = ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                baselineLine = $"Last upload: v{patchBaseline.versionNumber} at {baselineWhen}.";
            }

            long changed = patchDiff.TotalChangedBytes;
            long total = patchDiff.TotalCurrentBytes;
            int changedFiles = patchDiff.TotalChangedFileCount;
            if (patchBaseline == null)
            {
                return $"{baselineLine}\nPending upload: {changedFiles} files · {BuildManifestStore.FormatBytes(changed)}";
            }

            double reduction = total > 0 ? (1.0 - (double)changed / total) * 100.0 : 0.0;
            string dirtyLine = "Source changes since last upload: none detected.";
            if (dirtyGroupsEstimate != null && dirtyGroupsEstimate.matchedGroups + dirtyGroupsEstimate.unmatchedGroupNames.Count > 0)
            {
                int totalDirty = dirtyGroupsEstimate.matchedGroups + dirtyGroupsEstimate.unmatchedGroupNames.Count;
                string sizeLabel = dirtyGroupsEstimate.isIncomplete
                    ? $"≥ {BuildManifestStore.FormatBytes(dirtyGroupsEstimate.estimatedBytes)}"
                    : $"~{BuildManifestStore.FormatBytes(dirtyGroupsEstimate.estimatedBytes)}";
                dirtyLine = $"Source changes since last upload: {totalDirty} group(s) modified · {sizeLabel}";
            }

            return $"{baselineLine}\nPending changes: {changedFiles} files · {BuildManifestStore.FormatBytes(changed)} of {BuildManifestStore.FormatBytes(total)} ({reduction:0.0}% smaller)\n{dirtyLine}";
        }

        internal List<ContentAPI.UploadProgressEntry> GetProgressEntries()
        {
            return ContentAPI.GetUploadProgressSnapshot();
        }

        internal bool BuildOsx
        {
            get => buildOsx;
            set
            {
                if (buildOsx == value) return;
                buildOsx = value;
                SaveBuildTargetSelection();
                RefreshPatchEstimate();
            }
        }

        internal bool BuildWindows
        {
            get => buildWindows;
            set
            {
                if (buildWindows == value) return;
                buildWindows = value;
                SaveBuildTargetSelection();
                RefreshPatchEstimate();
            }
        }

        internal bool CleanBeforeEachTarget => cleanBeforeEachTarget;

        internal bool BeginUploadFromPopup(bool build, UploadMode mode)
        {
            return BeginUploadFromPopup(build, mode, failedOnly: false);
        }

        // failedOnly = true comes from the "Upload Failed Bundles" choice on
        // the Try Reupload dialog. It reroutes the upload through
        // FailedBundleStore so only the bundles that failed last time get
        // re-sent. The supplied UploadMode is ignored in that case — Failed
        // Only is its own filter and stacks with neither Patch nor All.
        internal bool BeginUploadFromPopup(bool build, UploadMode mode, bool failedOnly)
        {
            if (isUploading)
            {
                return false;
            }

            // Gate at the entry point so a stale popup (e.g. user toggled
            // Smart off in the main panel while the popup was open) can't
            // sneak through with a strategy-incompatible mode. Failed-Only
            // overrides the mode entirely, so the Smart-requirement check
            // doesn't apply to it.
            if (!failedOnly
                && UploadModePrefs.RequiresSmart(mode)
                && BundlingStrategyPrefs.Current != BundlingStrategy.Smart)
            {
                EditorUtility.DisplayDialog(
                    "Upload mode requires Smart bundling",
                    $"{UploadModePrefs.ShortLabel(mode)} requires the Smart bundling strategy. " +
                    "Switch to Smart in the Bundling section before trying this mode, or pick " +
                    "Upload All / Upload Patch.",
                    "OK");
                return false;
            }

            buildAndroid = true;
            buildIos = true;
            SaveBuildTargetSelection();

            if (build && !SaveModifiedScenesBeforeCompile())
            {
                EditorUtility.DisplayDialog("Compile Cancelled", "Save all modified scenes before compiling.", "OK");
                return false;
            }

            SaveLogoSelection();
            pendingUploadMode = mode;
            pendingFailedOnly = failedOnly;
            ResetUploadPresentationState(build);
            UploadContent(build);
            return true;
        }

        // Legacy single-arg entry point kept so older menu items / scripts
        // that still call BeginUploadFromPopup(bool) keep compiling. Routes
        // through the persisted UploadModePrefs choice.
        internal bool BeginUploadFromPopup(bool build)
        {
            return BeginUploadFromPopup(build, UploadModePrefs.Current);
        }

        private void ResetUploadPresentationState(bool build)
        {
            uploadBuildMode = build;
            uploadCompleted = false;
            uploadSucceeded = false;
            uploadStatusIsError = false;
            uploadStatusProgress = 0f;
            uploadStatusTitle = build ? "Preparing compile" : "Preparing upload";
            uploadStatusMessage = build
                ? "Checking content metadata, syncing schemas, and getting the build pipeline ready."
                : "Checking content metadata and preparing the existing build artifacts for upload.";
        }

        // Called by ContentUploadFlowPopup.Show when the popup is being
        // (re)opened. The previous run's completion flags are sticky on the
        // panel — they persist until the next BeginUploadFromPopup resets
        // them inside ResetUploadPresentationState — so without this nudge
        // the popup's OnGUI early-return for UploadCompleted keeps drawing
        // the completion view from the last upload even after the user
        // closes and reopens it. Only clears state when nothing is in
        // flight, so revisiting the popup mid-upload still shows live
        // progress instead of jumping back to the prep view.
        internal void ResetCompletionStateForNextRun()
        {
            if (isUploading) return;
            if (!uploadCompleted) return;
            uploadCompleted = false;
            uploadSucceeded = false;
            uploadStatusIsError = false;
            uploadStatusProgress = 0f;
            uploadStatusTitle = "";
            uploadStatusMessage = "";
            Repaint();
        }

        private void SetUploadStatus(string title, string message, float progress = -1f, bool isError = false)
        {
            uploadStatusTitle = title ?? "";
            uploadStatusMessage = message ?? "";
            uploadStatusProgress = progress;
            uploadStatusIsError = isError;
            Repaint();
        }

        private void CompleteUploadStatus(bool success, string message)
        {
            uploadCompleted = true;
            uploadSucceeded = success;
            uploadStatusIsError = !success;
            uploadStatusProgress = success ? 1f : uploadStatusProgress;
            uploadStatusTitle = success ? "Release complete" : "Release interrupted";
            uploadStatusMessage = message ?? (success ? "Upload complete." : "Upload failed.");
            Repaint();
        }

        // ── Bundling strategy ────────────────────────────────────────────
        // Lets the user pick how assets are partitioned into bundles. The
        // toggle is persisted in EditorPrefs (see BundlingStrategyPrefs);
        // ContentProcessor reads the current value when it (re)organizes
        // addressable groups.
        private void DrawBundlingStrategySection()
        {
            EditorGUILayout.LabelField("Bundling", EditorStyles.boldLabel);

            var current = BundlingStrategyPrefs.Current;
            var values = (BundlingStrategy[])System.Enum.GetValues(typeof(BundlingStrategy));
            var labels = values.Select(v => BundlingStrategyPrefs.Label(v)).ToArray();
            int currentIdx = System.Array.IndexOf(values, current);
            if (currentIdx < 0) currentIdx = 0;

            int newIdx = EditorGUILayout.Popup("Strategy", currentIdx, labels);
            if (newIdx != currentIdx)
            {
                var picked = values[newIdx];
                if (picked == BundlingStrategy.Smart)
                {
                    bool ok = EditorUtility.DisplayDialog(
                        "Switch to Smart bundling?",
                        "Smart (dependency-aware) bundling re-partitions addressable groups so " +
                        "that single-asset edits invalidate single bundles instead of folder-" +
                        "level bundles. The first build after switching will look like a full " +
                        "re-upload because every asset moves to a new group.\n\n" +
                        "This feature is experimental. You can switch back to Legacy at any time.",
                        "Switch to Smart", "Cancel");
                    if (!ok) return;
                }
                BundlingStrategyPrefs.Current = picked;
                Debug.Log($"[ContentUploader] Bundling strategy → {picked}");
            }

            if (current == BundlingStrategy.Smart)
            {
                EditorGUILayout.HelpBox(
                    "Smart bundling is experimental. Verify the next upload behaves correctly before relying on it.",
                    MessageType.Info);
            }
        }

        // Re-walks ServerData/ for the currently-enabled platforms and rebuilds
        // the cached diff against the saved baseline. Cheap (just a directory
        // listing + size lookup), so we can call it freely on lifecycle events.
        private void RefreshPatchEstimate()
        {
            patchEstimateContentId = contentId;
            patchEstimateComputedAt = System.DateTime.UtcNow;

            if (string.IsNullOrEmpty(contentId))
            {
                patchBaseline = null;
                patchCurrentSnapshot = null;
                patchDiff = null;
                return;
            }

            try
            {
                var platforms = GetEnabledPlatformsForManifest();
                patchCurrentSnapshot = BuildManifestStore.BuildFromServerData(contentId, /*versionNumber*/ 0, platforms);
                if (IsPatchUploadEnabled())
                {
                    patchBaseline = BuildManifestStore.LoadBaseline(contentId);
                    patchDiff = BuildManifestStore.Diff(patchBaseline, patchCurrentSnapshot);

                    // Source-aware estimate: read dirty-groups (maintained
                    // real-time by the watchdog) and match them against the
                    // baseline's bundle filenames for a quick "patch size" guess
                    // that reflects current source state, not the (possibly
                    // stale) ServerData/.
                    var dirtyGroups = DirtyGroupsStore.Load(contentId);
                    dirtyGroupsEstimate = DirtyGroupsEstimator.Estimate(patchBaseline, dirtyGroups);
                }
                else
                {
                    patchBaseline = null;
                    patchDiff = BuildManifestStore.Diff(null, patchCurrentSnapshot);
                    dirtyGroupsEstimate = null;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ContentUploader] Failed to compute patch estimate: {e.Message}");
                patchCurrentSnapshot = null;
                patchDiff = null;
                dirtyGroupsEstimate = null;
            }

            Repaint();
        }

        private static bool IsPatchUploadEnabled()
        {
            return BundlingStrategyPrefs.Current == BundlingStrategy.Smart;
        }

        private static JSONObject BuildUploaderMetadata(UploadMode effectiveMode)
        {
            var bundlingStrategy = BundlingStrategyPrefs.Current;

            // patching reflects what THIS upload actually did — anything other
            // than Upload All shipped a partial payload (Patch, CodeOnly,
            // PreviewsOnly). Previously we derived this from bundling strategy
            // alone, which marked Upload-All-on-Smart-strategy releases as
            // "patch" in the admin dashboard even though every bundle re-
            // shipped. The bundling strategy is still recorded separately via
            // `packer` / `bundlingStrategy`, so the "strategy was Smart but
            // creator chose to re-upload everything" case is still
            // recoverable from the metadata.
            bool didPatch = effectiveMode != UploadMode.All;

            var uploaderMetadata = new JSONObject(JSONObject.Type.Object);
            uploaderMetadata.AddField("packer", bundlingStrategy == BundlingStrategy.Smart ? "smart" : "legacy");
            uploaderMetadata.AddField("bundlingStrategy", bundlingStrategy.ToString().ToLowerInvariant());
            uploaderMetadata.AddField("patching", didPatch ? "enabled" : "disabled");
            uploaderMetadata.AddField("patchingEnabled", didPatch);
            // The specific mode the creator picked for this upload. Lets the
            // admin dashboard distinguish a full re-upload from a Patch /
            // Code-only / Previews-only run instead of collapsing all three
            // partial modes into one "patch" pill. The invariant lowercased
            // enum name (`all` / `patch` / `codeonly` / `previewsonly`) is
            // the stable key; uploadModeLabel is the human-friendly version
            // that matches what the SDK's upload-mode picker shows.
            uploaderMetadata.AddField("uploadMode", effectiveMode.ToString().ToLowerInvariant());
            uploaderMetadata.AddField("uploadModeLabel", UploadModePrefs.ShortLabel(effectiveMode));
            // SDK version this release was built with. The web admin dashboard
            // surfaces this so support / ops can correlate creator issues with
            // a specific SDK release, and the backend can flag releases built
            // against EOL SDK versions for forced re-upload before they break.
            uploaderMetadata.AddField("sdkVersion", SDKVersion.Current ?? "");
            return uploaderMetadata;
        }

        // ── "What you're uploading" preview ──────────────────────────────
        // Walks Assets/Content/{contentId}/ for prefabs carrying any of the
        // three "root" component types — LevelTemplate (or AttractionTemplate
        // via inheritance), PropTemplate, or PlayerRig — and groups them for
        // the panel grid. Mirrors SmartBundleGrouper's IsUserFacingRoot so
        // the preview matches what actually ends up in bundles.
        //
        // ThirdPartyLocal is excluded for the same reason it's excluded from
        // bundling: it's not part of the shipping content tree.
        private const float CardWidth = 110f;
        private const float CardImageSize = 92f;
        private const float CardLabelHeight = 32f;
        private const float CardSpacing = 6f;

        private void RefreshContentRoots()
        {
            contentRoots.Clear();
            contentRootsContentId = contentId;
            contentRootsDirty = false;

            if (string.IsNullOrEmpty(contentId)) return;

            string contentRoot = "Assets/Content/" + contentId;
            if (!AssetDatabase.IsValidFolder(contentRoot)) return;

            string previewsFolder = contentRoot + "/Previews";
            bool previewsFolderExists = AssetDatabase.IsValidFolder(previewsFolder);

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ContentRootKind? kind = null;
                if (prefab.GetComponent<LevelTemplate>() != null) kind = ContentRootKind.Attraction;
                else if (prefab.GetComponent<PropTemplate>() != null) kind = ContentRootKind.Prop;
                else if (prefab.GetComponent<PlayerRig>() != null) kind = ContentRootKind.Player;

                if (kind == null) continue;

                string name = Path.GetFileNameWithoutExtension(path);
                Texture2D preview = TryLoadPreviewFromFolder(previewsFolder, previewsFolderExists, name);

                string subLabel;
                switch (kind.Value)
                {
                    case ContentRootKind.Attraction: subLabel = "Attraction"; break;
                    case ContentRootKind.Prop:       subLabel = "Prop"; break;
                    case ContentRootKind.Player:     subLabel = "Player"; break;
                    default:                         subLabel = ""; break;
                }

                contentRoots.Add(new ContentRootEntry
                {
                    assetPath = path,
                    name = name,
                    kind = kind.Value,
                    customPreview = preview,
                    cachedAsset = prefab,
                    subLabel = subLabel,
                });
            }

            // Stable ordering: kind first (Attraction, Prop, Player), then name.
            contentRoots = contentRoots
                .OrderBy(e => (int)e.kind)
                .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Make sure Unity's preview cache has room for everything we're
            // about to ask for. Default cache size (≈100) is fine for a
            // handful of roots but can churn under heavy reentrant use.
            AssetPreview.SetPreviewTextureCacheSize(Mathf.Max(256, contentRoots.Count * 4));
        }

        // Runs the project's actual preview-PNG generator
        // (ContentProcessor.GenerateAllLevelPreviews → PrefabPreviewRenderer)
        // — the same pipeline that fires during Compile & Upload — and then
        // re-walks the content tree so DrawCard picks up the freshly-
        // generated Previews/{name}.png files via the customPreview path.
        //
        // GenerateAllLevelPreviews instantiates each prefab into the editor,
        // renders it via a temporary camera + lights, and saves the result
        // to Assets/Content/{contentId}/Previews/{name}.png. That's the
        // canonical "preview" — Unity's built-in AssetPreview cache is just
        // a fallback for prefabs the renderer skips (like the PlayerRig,
        // which has no LevelTemplate/PropTemplate component).
        //
        // CRITICAL: we MUST defer the actual render off the OnGUI stack
        // via EditorApplication.delayCall. Calling cam.Render() from
        // inside a panel's OnGUI nests one render context inside another
        // and produces "EndRenderPass: Not inside a Renderpass" errors
        // under URP — the editor window's own render pass is mid-flight
        // when ours tries to start. delayCall fires after this OnGUI tick
        // returns, by which time the editor's pipeline is idle.
        private void RebuildPreviews()
        {
            if (string.IsNullOrEmpty(contentId)) return;
            string capturedContentId = contentId;
            EditorApplication.delayCall += () => RunRebuildPreviewsDeferred(capturedContentId);
        }

        private void RunRebuildPreviewsDeferred(string idAtSchedule)
        {
            // Bail if the user switched contentId between scheduling and
            // running — would otherwise generate previews for the wrong project.
            if (idAtSchedule != contentId) return;

            try
            {
                EditorUtility.DisplayProgressBar(
                    "Rebuilding Previews",
                    $"Rendering preview PNGs for {contentId}...",
                    0f);

                // The real deal: renders each Attraction/Prop prefab into a
                // PNG file at Assets/Content/{contentId}/Previews/{name}.png.
                // Logs progress to the console for individual prefabs.
                ContentProcessor.GenerateAllLevelPreviews(contentId);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ContentUploader] Preview generation failed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // Drop in-memory previews so RefreshContentRoots picks up the
            // freshly-saved PNGs from disk via TryLoadPreviewFromFolder.
            for (int i = 0; i < contentRoots.Count; i++)
            {
                contentRoots[i].customPreview = null;
                contentRoots[i].autoPreview = null;
                contentRoots[i].autoPreviewResolved = false;
                contentRoots[i].cachedAsset = null;
                contentRoots[i].firstPollTime = 0;
            }

            // Re-walk so any newly-added prefabs / Previews/ files appear.
            RefreshContentRoots();

            // Belt-and-suspenders: prime Unity's AssetPreview cache for the
            // PlayerRig (which the PNG generator skips). DrawCard will use
            // the auto-preview path for it.
            for (int i = 0; i < contentRoots.Count; i++)
            {
                var entry = contentRoots[i];
                if (entry.cachedAsset == null && !string.IsNullOrEmpty(entry.assetPath))
                {
                    entry.cachedAsset = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
                }
                if (entry.cachedAsset != null)
                {
                    AssetPreview.GetAssetPreview(entry.cachedAsset);
                }
            }

            Repaint();
        }

        // Looks for a hand-curated screenshot under Previews/{name}.png|jpg|jpeg.
        // Synchronous and cheap. AssetPreview-driven fallback happens lazily
        // at draw time inside DrawCard so RefreshContentRoots stays fast.
        private static Texture2D TryLoadPreviewFromFolder(string previewsFolder, bool previewsFolderExists, string name)
        {
            if (!previewsFolderExists) return null;
            string[] exts = { ".png", ".jpg", ".jpeg" };
            foreach (var ext in exts)
            {
                string p = previewsFolder + "/" + name + ext;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (tex != null) return tex;
            }
            return null;
        }

        // Gate for the Compile & Upload + Build & Inspect actions: a content
        // package is only meaningful if it ships at least one Attraction or
        // Prop. A bare PlayerRig isn't a complete deliverable on its own.
        private bool HasShippableContent()
        {
            for (int i = 0; i < contentRoots.Count; i++)
            {
                var k = contentRoots[i].kind;
                if (k == ContentRootKind.Attraction || k == ContentRootKind.Prop) return true;
            }
            return false;
        }

        private void DrawContentPreviewSection()
        {
            // Outer foldout for the whole "Park Assets" block. Header includes
            // a compact summary so collapsed users still know what they have.
            int attractionCount = 0, propCount = 0, playerCount = 0;
            for (int i = 0; i < contentRoots.Count; i++)
            {
                switch (contentRoots[i].kind)
                {
                    case ContentRootKind.Attraction: attractionCount++; break;
                    case ContentRootKind.Prop:       propCount++; break;
                    case ContentRootKind.Player:     playerCount++; break;
                }
            }

            string summary = contentRoots.Count == 0
                ? "Park Assets (none)"
                : $"Park Assets  ·  {attractionCount} attraction(s)  ·  {propCount} prop(s)  ·  {playerCount} player";

            // Manual rect layout so we can pin a small refresh-glyph button
            // to the top-right of the foldout header. EditorStyles.foldoutHeader
            // stretches to fill its row, which makes the standard
            // BeginHorizontal/Foldout/Button pattern push the button onto a
            // new line — Rect math is the cleanest way to reserve space.
            Rect headerRect = GUILayoutUtility.GetRect(0f, EditorGUIUtility.singleLineHeight + 4f, GUILayout.ExpandWidth(true));
            const float refreshBtnSize = 22f;
            const float refreshBtnPad = 2f;
            Rect refreshBtnRect = new Rect(
                headerRect.xMax - refreshBtnSize - refreshBtnPad,
                headerRect.y + (headerRect.height - refreshBtnSize) * 0.5f,
                refreshBtnSize, refreshBtnSize);
            Rect foldoutRect = new Rect(
                headerRect.x, headerRect.y,
                headerRect.width - refreshBtnSize - (refreshBtnPad * 2f),
                headerRect.height);

            bool newParkAssetsFold = EditorGUI.Foldout(foldoutRect, parkAssetsFold, summary, true, EditorStyles.foldoutHeader);
            if (newParkAssetsFold != parkAssetsFold)
            {
                parkAssetsFold = newParkAssetsFold;
                EditorPrefs.SetBool(ParkAssetsFoldPrefKey, parkAssetsFold);
            }

            // Refresh button — Unity's stock "Refresh" glyph + tooltip. Builds
            // a fresh GUIContent (rather than mutating the cached IconContent's
            // tooltip) so we don't pollute Unity's icon cache.
            var refreshContent = new GUIContent(
                EditorGUIUtility.IconContent("Refresh").image,
                "Rebuild Previews");
            using (new EditorGUI.DisabledScope(contentRoots.Count == 0))
            {
                if (GUI.Button(refreshBtnRect, refreshContent, EditorStyles.iconButton))
                {
                    RebuildPreviews();
                }
            }

            if (!parkAssetsFold) return;

            if (contentRoots.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"You haven't created any Attractions or Props yet. Add a prefab to Assets/Content/{contentId}/ " +
                    "with a LevelTemplate, AttractionTemplate, or PropTemplate component before uploading.",
                    MessageType.Warning);
                return;
            }

            if (!HasShippableContent())
            {
                EditorGUILayout.HelpBox(
                    "This content folder has no Attractions or Props. Uploading is disabled until you add at least one.",
                    MessageType.Warning);
            }

            DrawContentGroup("Attractions", ContentRootKind.Attraction, ref foldAttractions, ParkAssetsAttractionsPrefKey);
            DrawContentGroup("Props",       ContentRootKind.Prop,       ref foldProps,       ParkAssetsPropsPrefKey);
            DrawContentGroup("Player",      ContentRootKind.Player,     ref foldPlayer,      ParkAssetsPlayerPrefKey);

            // Keep repainting until every root has its full AssetPreview
            // resolved. Relying on AssetPreview.IsLoadingAssetPreviews()
            // alone wasn't enough — the global flag flips false between
            // frames while individual previews are still being scheduled,
            // and when an OnGUI tick happens to land in that gap we'd
            // stop polling and the cards would stay blank until the user
            // moved the mouse. Per-entry tracking guarantees we keep
            // ticking until everyone's resolved.
            bool anyUnresolved = false;
            for (int i = 0; i < contentRoots.Count; i++)
            {
                var e = contentRoots[i];
                if (e.customPreview != null) continue;       // hand-curated wins, skip
                if (!e.autoPreviewResolved) { anyUnresolved = true; break; }
            }
            if (anyUnresolved || AssetPreview.IsLoadingAssetPreviews())
            {
                Repaint();
            }
        }

        private void DrawContentGroup(string header, ContentRootKind kind, ref bool foldState, string prefKey)
        {
            var entries = contentRoots.Where(e => e.kind == kind).ToList();
            if (entries.Count == 0) return;

            GUILayout.Space(4);

            // Per-group foldout. Header doubles as the group title and as
            // the click target — Unity's standard pattern. Count appended
            // so collapsed groups still communicate volume.
            string groupHeader = $"{header} ({entries.Count})";
            bool newFold = EditorGUILayout.Foldout(foldState, groupHeader, true);
            if (newFold != foldState)
            {
                foldState = newFold;
                EditorPrefs.SetBool(prefKey, foldState);
            }
            if (!foldState) return;

            // Fit as many cards per row as the panel width allows. Falls
            // back to 1 per row on very narrow panels.
            float panelWidth = Mathf.Max(position.width - 24f, CardWidth);
            int perRow = Mathf.Max(1, Mathf.FloorToInt((panelWidth + CardSpacing) / (CardWidth + CardSpacing)));

            EditorGUI.indentLevel++;
            for (int i = 0; i < entries.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                // Manual indent: GUILayout doesn't honor EditorGUI.indentLevel
                // for raw rects from GetRect, so we pad explicitly to keep
                // cards visually nested under the foldout.
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                for (int j = 0; j < perRow && i + j < entries.Count; j++)
                {
                    DrawCard(entries[i + j]);
                    if (j < perRow - 1) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(CardSpacing);
            }
            EditorGUI.indentLevel--;
        }

        // 5 seconds is more than enough for any prefab Unity intends to
        // render. Past that, we accept the fallback icon and stop polling.
        // Empty container prefabs (LevelTemplate-only, no mesh) never
        // produce a rich AssetPreview at all — without this timeout we'd
        // repaint forever waiting for something that's never coming.
        private const double PreviewPollTimeoutSeconds = 5.0;

        private void DrawCard(ContentRootEntry entry)
        {
            // Resolve which texture to draw on this paint. Priority:
            //   1. Hand-curated Previews/{name}.png (if user provided one)
            //   2. Unity's full AssetPreview thumbnail (the rich render)
            //   3. Mini thumbnail (Unity's small icon for the asset type)
            //   4. ObjectContent's icon (matches what the Inspector shows)
            //   5. Stock "Prefab Icon" — guaranteed to exist
            //
            // We re-fetch from AssetPreview every frame instead of caching
            // the Texture2D pointer because Unity's preview cache can evict
            // entries — holding the reference would leave us pointing at a
            // destroyed texture that renders pink. The deeper fallbacks
            // exist for prefabs that have no renderable content (empty
            // GameObjects with just a LevelTemplate component), where
            // AssetPreview returns null indefinitely.
            if (entry.cachedAsset == null)
            {
                entry.cachedAsset = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
            }
            if (entry.firstPollTime <= 0)
            {
                entry.firstPollTime = EditorApplication.timeSinceStartup;
            }

            Texture drawTex = entry.customPreview;

            // Layer 2: full AssetPreview (only attempt while we're still
            // within the polling window — gives Unity time to render).
            bool stillPolling = !entry.autoPreviewResolved
                && (EditorApplication.timeSinceStartup - entry.firstPollTime) < PreviewPollTimeoutSeconds;
            if (drawTex == null && entry.cachedAsset != null && stillPolling)
            {
                var fresh = AssetPreview.GetAssetPreview(entry.cachedAsset);
                if (fresh != null)
                {
                    entry.autoPreview = fresh;
                    entry.autoPreviewResolved = true;
                }
            }
            if (drawTex == null) drawTex = entry.autoPreview;

            // Layer 3: mini thumbnail (cheap, synchronous, type-aware icon).
            if (drawTex == null && entry.cachedAsset != null)
            {
                drawTex = AssetPreview.GetMiniThumbnail(entry.cachedAsset);
            }

            // Layer 4: Inspector-style ObjectContent icon.
            if (drawTex == null && entry.cachedAsset != null)
            {
                var content = EditorGUIUtility.ObjectContent(entry.cachedAsset, entry.cachedAsset.GetType());
                if (content != null) drawTex = content.image;
            }

            // Layer 5: stock prefab icon — never null in any Unity build.
            if (drawTex == null)
            {
                drawTex = EditorGUIUtility.IconContent("Prefab Icon").image as Texture;
            }

            // Once we've timed out without a rich preview, mark resolved so
            // the OnGUI polling loop terminates. We're stuck with the icon
            // fallback for this entry — that's fine.
            if (!entry.autoPreviewResolved
                && (EditorApplication.timeSinceStartup - entry.firstPollTime) >= PreviewPollTimeoutSeconds)
            {
                entry.autoPreviewResolved = true;
            }

            float totalHeight = CardImageSize + CardLabelHeight + 2f;
            Rect cardRect = GUILayoutUtility.GetRect(CardWidth, totalHeight,
                GUILayout.Width(CardWidth), GUILayout.Height(totalHeight));

            Rect imgRect = new Rect(cardRect.x, cardRect.y, CardWidth, CardImageSize);
            Rect labelRect = new Rect(cardRect.x, cardRect.y + CardImageSize + 2f, CardWidth, CardLabelHeight);

            // Card frame so empty/loading states don't visually disappear.
            EditorGUI.DrawRect(imgRect, new Color(0f, 0f, 0f, 0.18f));

            if (drawTex != null)
            {
                GUI.DrawTexture(imgRect, drawTex, ScaleMode.ScaleToFit);
            }
            else
            {
                var placeholderStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11,
                    wordWrap = true,
                };
                GUI.Label(imgRect, entry.subLabel, placeholderStyle);
            }

            // Two-line label: name on top (bold-ish), kind on bottom.
            var nameStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
                fontStyle = FontStyle.Bold,
            };
            GUI.Label(labelRect, new GUIContent(entry.name, entry.assetPath), nameStyle);

            // Click anywhere on the card to ping + select the underlying
            // prefab in the Project window. Standard Unity feel.
            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && cardRect.Contains(Event.current.mousePosition))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }
                Event.current.Use();
            }
        }

        // The set of platforms to include in the manifest. Mirrors the build-
        // target toggles so the diff is honest about what we'll actually
        // upload. Includes "Unity" because the upload pipeline ships the
        // .unitypackage from ServerData/Unity/ as well.
        private List<string> GetEnabledPlatformsForManifest()
        {
            var list = new List<string>();
            if (buildAndroid) list.Add("Android");
            if (buildIos) list.Add("iOS");
            if (buildOsx) list.Add("StandaloneOSX");
            if (buildWindows) list.Add("StandaloneWindows");
            list.Add("Unity");
            return list;
        }

        private void LoadLogoSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                logoTexture = null;
                return;
            }

            string savedPath = EditorPrefs.GetString(LogoPrefKeyPrefix + contentId, "");
            if (!string.IsNullOrEmpty(savedPath))
            {
                logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(savedPath);
                if (logoTexture != null)
                {
                    return;
                }
            }

            logoTexture = FindAutoLogoTexture();
        }

        private Texture2D FindAutoLogoTexture()
        {
            string contentRoot = $"Assets/Content/{contentId}";
            if (!AssetDatabase.IsValidFolder(contentRoot))
            {
                return null;
            }

            var contentTextures = AssetDatabase.FindAssets("t:Texture2D", new[] { contentRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string logoNamedPath = contentTextures
                .Where(p => Path.GetFileNameWithoutExtension(p).IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.Count(c => c == '/' || c == '\\'))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(logoNamedPath))
            {
                return AssetDatabase.LoadAssetAtPath<Texture2D>(logoNamedPath);
            }

            string rootPngPath = contentTextures
                .Where(p => string.Equals(Path.GetExtension(p), ".png", StringComparison.OrdinalIgnoreCase))
                .Where(p => string.Equals(
                    Path.GetDirectoryName(p)?.Replace("\\", "/"),
                    contentRoot,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return string.IsNullOrEmpty(rootPngPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Texture2D>(rootPngPath);
        }

        private void SaveLogoSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return;
            }

            if (logoTexture == null)
            {
                EditorPrefs.DeleteKey(LogoPrefKeyPrefix + contentId);
                return;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (!string.IsNullOrEmpty(logoPath))
            {
                EditorPrefs.SetString(LogoPrefKeyPrefix + contentId, logoPath);
            }
        }

        private void RestoreContentIdSelection()
        {
            if (contentIdOptions.Count == 0)
            {
                contentId = "";
                contentIdIndex = 0;
                return;
            }

            string savedContentId = EditorPrefs.GetString(ContentIdPrefKey, "");
            if (!string.IsNullOrEmpty(savedContentId))
            {
                int savedIndex = contentIdOptions.IndexOf(savedContentId);
                if (savedIndex >= 0)
                {
                    contentIdIndex = savedIndex;
                    contentId = contentIdOptions[contentIdIndex];
                    return;
                }
            }

            // Fall back to project prefix if no saved selection exists.
            var defaultPrefix = ContentProcessor.GetGamePrefix();
            if (!string.IsNullOrEmpty(defaultPrefix))
            {
                int idx = contentIdOptions.IndexOf(defaultPrefix);
                if (idx >= 0)
                {
                    contentIdIndex = idx;
                    contentId = contentIdOptions[contentIdIndex];
                    SaveContentIdSelection();
                    return;
                }
            }

            contentIdIndex = 0;
            contentId = contentIdOptions[0];
            SaveContentIdSelection();
        }

        private void SaveContentIdSelection()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return;
            }

            EditorPrefs.SetString(ContentIdPrefKey, contentId);
        }

        private void LoadBuildTargetSelection()
        {
            buildAndroid = true;
            buildIos = true;
            buildOsx = EditorPrefs.GetBool(BuildOsxPrefKey, true);
            buildWindows = EditorPrefs.GetBool(BuildWindowsPrefKey, true);
            cleanBeforeEachTarget = EditorPrefs.GetBool(CleanBeforeEachTargetPrefKey, false);
        }

        private void SaveBuildTargetSelection()
        {
            buildAndroid = true;
            buildIos = true;
            EditorPrefs.SetBool(BuildAndroidPrefKey, true);
            EditorPrefs.SetBool(BuildIosPrefKey, true);
            EditorPrefs.SetBool(BuildOsxPrefKey, buildOsx);
            EditorPrefs.SetBool(BuildWindowsPrefKey, buildWindows);
            EditorPrefs.SetBool(CleanBeforeEachTargetPrefKey, cleanBeforeEachTarget);
        }

        private static bool SaveModifiedScenesBeforeCompile()
        {
            if (!EditorSceneManager.SaveOpenScenes())
            {
                return false;
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        private void RunPreflightTagCheck(bool autoFixMismatches = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contentId))
                {
                    EditorUtility.DisplayDialog("Preflight Tag Check", "Select a Content ID first.", "OK");
                    return;
                }

                var refresh = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(contentId);
                var snapshot = TagLayerSchemaSyncUtility.ReadLocalTagManager();

                string contentRoot = Path.Combine("Assets", "Content", contentId);
                if (!Directory.Exists(contentRoot))
                {
                    EditorUtility.DisplayDialog("Preflight Tag Check", $"Content path not found: {contentRoot}", "OK");
                    return;
                }

                string[] prefabPaths = Directory.GetFiles(contentRoot, "*.prefab", SearchOption.AllDirectories);
                int checkedCount = 0;
                int mismatchCount = 0;
                int unknownTagCount = 0;
                int fixedCount = 0;

                foreach (var rawPath in prefabPaths)
                {
                    string assetPath = rawPath.Replace("\\", "/");
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                    {
                        continue;
                    }

                    checkedCount++;
                    string resolvedTag = prefab.tag ?? "";
                    string serializedRootTag = ExtractFirstSerializedTag(assetPath);

                    if (!string.IsNullOrEmpty(resolvedTag)
                        && !snapshot.tags.Contains(resolvedTag)
                        && !BuiltInUnityTags.Contains(resolvedTag))
                    {
                        unknownTagCount++;
                        Debug.LogWarning($"[TagPreflight] Unknown resolved tag '{resolvedTag}' in {assetPath}");
                    }

                    if (!string.IsNullOrEmpty(serializedRootTag)
                        && !string.Equals(serializedRootTag, resolvedTag, StringComparison.Ordinal))
                    {
                        mismatchCount++;
                        Debug.LogWarning($"[TagPreflight] Mismatch in {assetPath} serialized='{serializedRootTag}' resolved='{resolvedTag}'");

                        if (autoFixMismatches && IsKnownTag(serializedRootTag, snapshot.tags))
                        {
                            if (TrySetPrefabRootTag(assetPath, serializedRootTag))
                            {
                                fixedCount++;
                            }
                        }
                    }
                }

                if (autoFixMismatches && fixedCount > 0)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    // Re-read once after fixes to report current truth.
                    RunPreflightTagCheck(autoFixMismatches: false);
                    Debug.Log($"[TagPreflight] Auto-fix updated {fixedCount} prefabs to match serialized root tags.");
                    return;
                }

                string message =
                    $"Prefabs refreshed: {refresh.prefabsReserialized}/{refresh.prefabsProcessed}\n" +
                    $"Prefabs checked: {checkedCount}\n" +
                    $"Serialized/Resolved mismatches: {mismatchCount}\n" +
                    $"Resolved tags missing from TagManager (non-built-in): {unknownTagCount}" +
                    (autoFixMismatches ? $"\nMismatches auto-fixed: {fixedCount}" : "");

                if (mismatchCount > 0 || unknownTagCount > 0)
                {
                    Debug.LogWarning("[TagPreflight] " + message);
                    EditorUtility.DisplayDialog("Preflight Tag Check", message + "\n\nSee Console warnings for details.", "OK");
                }
                else
                {
                    Debug.Log("[TagPreflight] " + message);
                    EditorUtility.DisplayDialog("Preflight Tag Check", message, "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TagPreflight] Failed: " + ex);
                EditorUtility.DisplayDialog("Preflight Tag Check Failed", ex.Message, "OK");
            }
        }

        private static bool IsKnownTag(string tag, List<string> tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            if (BuiltInUnityTags.Contains(tag))
            {
                return true;
            }

            return tags != null && tags.Contains(tag);
        }

        private static bool TrySetPrefabRootTag(string assetPath, string tag)
        {
            try
            {
                var root = PrefabUtility.LoadPrefabContents(assetPath);
                if (root == null)
                {
                    return false;
                }

                root.tag = tag;
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                PrefabUtility.UnloadPrefabContents(root);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TagPreflight] Failed to auto-fix {assetPath}: {e.Message}");
                return false;
            }
        }

        private static string ExtractFirstSerializedTag(string assetPath)
        {
            string text = File.ReadAllText(assetPath);
            var match = Regex.Match(text, @"^\s*m_TagString:\s*(.+)$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private string GetLogoAddress()
        {
            if (logoTexture == null || string.IsNullOrEmpty(contentId))
            {
                return null;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (string.IsNullOrEmpty(logoPath))
            {
                return null;
            }

            return $"{contentId}/Logos/{Path.GetFileNameWithoutExtension(logoPath)}";
        }

        private void FetchContentMetadata()
        {
            // Reset access state for the new contentId so the gate UI doesn't
            // briefly show stale "no access" between selections.
            isContentAccessibleByMe = null;

            if (string.IsNullOrEmpty(contentId) || !AuthAPI.isLoggedIn)
            {
                latestPublishedVersionNumber = null;
                return;
            }

            isLoadingMetadata = true;
            ContentAPI.GetContent(contentId, (success, response) =>
            {
                isLoadingMetadata = false;

                // Translate response → access state.
                //   200 → we own it (success=true), accessible.
                //   404 → content doesn't exist yet (we'd auto-create on upload), accessible.
                //   403 → someone else owns this contentId, NOT accessible.
                //   anything else → leave unknown so we don't punish transient failures.
                if (success)
                {
                    isContentAccessibleByMe = true;
                }
                else if (response != null && response.statusCode == 404)
                {
                    isContentAccessibleByMe = true;
                }
                else if (response != null && response.statusCode == 403)
                {
                    isContentAccessibleByMe = false;
                }

                if (!success || response?.json == null || !response.json.HasField("content"))
                {
                    latestPublishedVersionNumber = null;
                    Repaint();
                    return;
                }

                JSONObject content = response.json.GetField("content");
                latestPublishedVersionNumber = content.HasField("versions") && content.GetField("versions").list != null
                    ? content.GetField("versions").list.Count
                    : 0;
                if (content.HasField("contentName"))
                {
                    contentName = content.GetField("contentName").stringValue ?? contentName;
                }
                if (content.HasField("contentDescription"))
                {
                    contentDescription = content.GetField("contentDescription").stringValue ?? contentDescription;
                }
                Repaint();
            });
        }

        private void FetchContentUsers()
        {
            // Reset state for the new contentId
            teamMembers.Clear();
            teamPrimaryOwnerId = null;
            teamErrorMessage = null;

            if (string.IsNullOrEmpty(contentId) || !AuthAPI.isLoggedIn)
            {
                isLoadingTeam = false;
                Repaint();
                return;
            }

            isLoadingTeam = true;
            ContentAPI.ListContentUsers(contentId, (success, response) =>
            {
                isLoadingTeam = false;

                if (!success || response?.json == null)
                {
                    // 403 / 404 / network — show a friendly message instead of raw error
                    if (response != null && response.statusCode == 403)
                    {
                        teamErrorMessage = "You are not an owner of this content — team list is hidden.";
                    }
                    else if (response != null && response.statusCode == 404)
                    {
                        teamErrorMessage = "This content has not been uploaded yet. Compile & upload first to manage collaborators.";
                    }
                    else
                    {
                        teamErrorMessage = "Could not load team: " + (response?.error ?? "unknown error");
                    }
                    Repaint();
                    return;
                }

                if (response.json.HasField("contentOwner"))
                {
                    teamPrimaryOwnerId = response.json.GetField("contentOwner").stringValue;
                }
                if (response.json.HasField("owners"))
                {
                    var ownersField = response.json.GetField("owners");
                    if (ownersField != null && ownersField.list != null)
                    {
                        foreach (var entry in ownersField.list)
                        {
                            string userId = entry.HasField("userId") ? entry.GetField("userId").stringValue : null;
                            string emailValue = entry.HasField("email") ? entry.GetField("email").stringValue : null;
                            if (!string.IsNullOrEmpty(userId))
                            {
                                teamMembers.Add(new TeamMember { userId = userId, email = emailValue });
                            }
                        }
                    }
                }
                Repaint();
            });
        }

        // Reusable "you must log in" gate for any editor panel. Other DreamPark
        // panels (e.g. SDKPublishPanel) draw the same lockout when the user is
        // signed out.
        public static void DrawLoginGate(string reason)
        {
            EditorGUILayout.HelpBox(reason, MessageType.Info);
            GUILayout.Space(6);
            if (GUILayout.Button("Log in", GUILayout.Height(28)))
            {
                AuthPopup.Show();
            }
        }

        // Diagnostic: runs the full bundling pipeline for the currently-active
        // build target only (no per-platform sweep, no upload, no commitUpload),
        // then pops open the Addressables Groups window so the user can audit
        // what the bundling pass actually produced. Useful for validating Smart
        // partitioning before committing to a real upload — e.g. checking that
        // textures from ThirdParty actually got bundled with their consumer
        // prefabs and aren't stranded in ThirdParty/Misc/Shared groups.
        private void RunBuildAndInspect()
        {
            if (string.IsNullOrEmpty(contentId)) return;

            int currentStep = 0;
            int totalSteps = 8;
            Action<string> reportStep = (message) =>
            {
                currentStep++;
                EditorUtility.DisplayProgressBar(
                    "Build & Inspect Groups",
                    $"({currentStep}/{totalSteps}) {message}",
                    Mathf.Clamp01((float)currentStep / Mathf.Max(1, totalSteps)));
            };

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string serverDataPath = Path.Combine(projectRoot, "ServerData");

            try
            {
                isUploading = true; // gates the rest of the panel UI

                reportStep("Clearing previous build artifacts...");
                if (Directory.Exists(serverDataPath))
                {
                    Directory.Delete(serverDataPath, true);
                }
                Caching.ClearCache();
                Addressables.ClearResourceLocators();
                AssetDatabase.Refresh();

                reportStep("Configuring addressable settings...");
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
                settings.MonoScriptBundleCustomNaming = contentId + "_";
                settings.OverridePlayerVersion = contentId;
                // NonRecursiveBuilding pinned to Unity's default (true).
                //
                // We previously toggled this to false for the Smart strategy to
                // get per-bundle embedded MonoScript metadata — that would have
                // eliminated the shared monoscripts.bundle dep-hash cascade
                // (single C# edit → ~91% byte churn across bundles). But the
                // change broke production: Quest 3S started throwing "Could not
                // produce class with ID X" and iOS hit missing-StreamingAssets/
                // Addressables errors, because without the shared monoscripts
                // bundle Unity loses the implicit "preserve every MonoBehaviour
                // / ScriptableObject type" safety net that IL2CPP's stripper
                // depends on at runtime.
                //
                // Reverted while we work on a cleaner fix that doesn't change
                // the runtime bundle layout — see CASCADE_WORKAROUND_DEFERRED.md
                // for the catalog-rewrite / decompress-then-hash plan.
                settings.NonRecursiveBuilding = true;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();

                reportStep("Syncing third-party assets...");
                try
                {
                    ThirdPartySyncTool.RunSyncForContent(contentId);
                }
                catch (Exception syncEx)
                {
                    Debug.LogWarning($"[ContentUploader] Third-party sync skipped: {syncEx.Message}");
                }

                // Force AssetDatabase to fully observe everything ThirdPartySync
                // just moved. Without this explicit refresh, freshly-moved assets
                // can be invisible to the AssetDatabase.FindAssets call inside
                // ForceUpdateContent → ApplyGameIdLabelToContentEntries, which
                // strands them outside addressables.
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                reportStep("Updating addressable groups...");
                ContentProcessor.ForceUpdateContent(contentId);

                // Janitor pass — drop missing-reference entries and empty
                // stale-prefix groups (e.g. YOUR_GAME_HERE-* leftovers from
                // the SDK template after the new-park rename).
                ContentProcessor.CleanupAddressableSettings();

                reportStep("Updating logo entry...");
                SyncLogoAddressableEntry();

                reportStep("Enforcing content namespaces...");
                ContentProcessor.EnforceContentNamespaces(contentId);

                reportStep("Building scripts package...");
                ContentProcessor.BuildUnityPackage(contentId);

                // Build only the active target — keep the diagnostic fast.
                // The URL is a placeholder since these bundles will never be
                // uploaded; if the user later decides to ship, Compile & Upload
                // does its own clean build with the real per-platform URLs.
                BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
                BuildTargetGroup activeGroup = BuildPipeline.GetBuildTargetGroup(activeTarget);
                string inspectUrl = $"{DreamParkAPI.devBaseUrl}/app/content/addressables/{contentId}/inspect/{activeTarget}";
                reportStep($"Building {activeTarget} (no upload)...");
                bool buildOk = BuildForTarget(activeTarget, activeGroup, inspectUrl, contentId);

                EditorUtility.ClearProgressBar();

                if (!buildOk)
                {
                    EditorUtility.DisplayDialog(
                        "Build failed",
                        $"Build for {activeTarget} did not complete. See Console for details.",
                        "OK");
                    return;
                }

                // Open the Addressables Groups window for the user to inspect.
                // Using ExecuteMenuItem avoids hard-coding the window's class
                // name (which has moved across Addressables versions).
                EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups");

                Debug.Log("[Build & Inspect] Build complete. Addressables Groups window opened. " +
                          "ServerData/ now contains bundles for the active platform — " +
                          "use the file sizes there to validate Smart partitioning.");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[Build & Inspect] Failed: {e}");
                EditorUtility.DisplayDialog("Error", $"Build & Inspect failed: {e.Message}", "OK");
            }
            finally
            {
                isUploading = false;
                Repaint();
            }
        }

        private void DrawTeamSection()
        {
            GUILayout.Label("Team", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(contentId))
            {
                EditorGUILayout.HelpBox("Select a content folder to manage its team.", MessageType.None);
                return;
            }
            if (!AuthAPI.isLoggedIn)
            {
                EditorGUILayout.HelpBox("Log in to manage collaborators.", MessageType.None);
                return;
            }
            if (isLoadingTeam)
            {
                EditorGUILayout.HelpBox("Loading team...", MessageType.Info);
                return;
            }
            if (!string.IsNullOrEmpty(teamErrorMessage))
            {
                EditorGUILayout.HelpBox(teamErrorMessage, MessageType.Info);
                return;
            }

            if (teamMembers.Count == 0)
            {
                EditorGUILayout.HelpBox("No collaborators yet.", MessageType.None);
            }
            else
            {
                foreach (var member in teamMembers.ToArray()) // ToArray so we can mutate during iteration
                {
                    GUILayout.BeginHorizontal();
                    string label = !string.IsNullOrEmpty(member.email) ? member.email : "(unknown email — uid: " + member.userId + ")";
                    bool isPrimary = !string.IsNullOrEmpty(teamPrimaryOwnerId) && teamPrimaryOwnerId == member.userId;
                    bool isSelf = !string.IsNullOrEmpty(AuthAPI.userId) && member.userId == AuthAPI.userId;
                    if (isPrimary) label += "  (primary)";
                    if (isSelf) label += "  (you)";
                    EditorGUILayout.LabelField(label);

                    // Disable the trash button for both the primary owner AND
                    // yourself. Removing the primary always 400s server-side;
                    // removing yourself is also blocked here as a safety net so
                    // you can't accidentally orphan your own access — if you
                    // really want to leave a project, ask another owner to
                    // remove you (separate "Leave project" flow can be added
                    // later if needed).
                    bool canRemove = !isPrimary && !isSelf;
                    GUI.enabled = canRemove;
                    string tooltip = isPrimary ? "Primary owner cannot be removed."
                                   : isSelf ? "You cannot remove yourself."
                                   : "Remove collaborator";
                    // Use Unity's built-in trash icon (auto-themes light/dark)
                    // instead of a unicode glyph — renders consistently across
                    // platforms and matches the look of other editor buttons.
                    var trashIcon = EditorGUIUtility.IconContent("TreeEditor.Trash");
                    if (GUILayout.Button(new GUIContent(trashIcon.image, tooltip), GUILayout.Width(30)))
                    {
                        if (EditorUtility.DisplayDialog("Remove collaborator?",
                                $"Remove {label} from {contentId}?",
                                "Remove", "Cancel"))
                        {
                            RemoveTeamMember(member.userId);
                        }
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(4);
            if (GUILayout.Button("+ Add team member"))
            {
                AddTeamMemberPopup.Show(contentId, OnAddTeamMemberSubmitted);
            }
        }

        private void OnAddTeamMemberSubmitted(string emailToAdd)
        {
            if (string.IsNullOrEmpty(emailToAdd) || string.IsNullOrEmpty(contentId)) return;

            ContentAPI.AddContentUser(contentId, emailToAdd, (success, response) =>
            {
                if (success)
                {
                    Debug.Log($"✅ Added '{emailToAdd}' to {contentId}");
                    FetchContentUsers();
                }
                else
                {
                    string err = ExtractServerErrorMessage(response);
                    Debug.LogError($"[DreamPark] Add collaborator failed (status={response?.statusCode}, raw={response?.rawText}): {err}");
                    EditorUtility.DisplayDialog("Could not add collaborator", err, "OK");
                }
            });
        }

        private void RemoveTeamMember(string userId)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(contentId)) return;

            ContentAPI.RemoveContentUser(contentId, userId, (success, response) =>
            {
                if (success)
                {
                    Debug.Log($"✅ Removed user {userId} from {contentId}");
                    FetchContentUsers();
                }
                else
                {
                    string err = ExtractServerErrorMessage(response);
                    Debug.LogError($"[DreamPark] Remove collaborator failed (status={response?.statusCode}, raw={response?.rawText}): {err}");
                    EditorUtility.DisplayDialog("Could not remove collaborator", err, "OK");
                }
            });
        }

        // Defensive error-string extraction. The previous one-liner sometimes
        // surfaced the raw "HTTP/1.1 400 Bad Request" string instead of the
        // server's clean { error: "..." } message — that happens when the
        // pre-parsed `response.json` is null even though the body is valid JSON
        // (e.g. JSON parser hiccup on certain bodies). This re-parses rawText
        // as a fallback so we surface the server's intent whenever possible.
        private static string ExtractServerErrorMessage(DreamPark.API.DreamParkAPI.APIResponse response)
        {
            if (response == null) return "Unknown error.";

            // Path 1: pre-parsed JSON has an `error` field.
            if (response.json != null && response.json.HasField("error"))
            {
                var msg = response.json.GetField("error").stringValue;
                if (!string.IsNullOrEmpty(msg)) return msg;
            }

            // Path 2: rawText is JSON but APIResponse failed to parse it. Try once more.
            if (!string.IsNullOrEmpty(response.rawText)
                && (response.rawText.TrimStart().StartsWith("{") || response.rawText.TrimStart().StartsWith("[")))
            {
                try
                {
                    var parsed = new Defective.JSON.JSONObject(response.rawText);
                    if (parsed != null && parsed.HasField("error"))
                    {
                        var msg = parsed.GetField("error").stringValue;
                        if (!string.IsNullOrEmpty(msg)) return msg;
                    }
                }
                catch
                {
                    // Fall through to next path.
                }
            }

            // Path 3: transport-level error string from UnityWebRequest.
            return string.IsNullOrEmpty(response.error) ? "Unknown error." : response.error;
        }

        public void Login(string email, string password)
        {
            AuthAPI.Login(email, password, (success, response) =>
            {
                if (success)
                {
                    Debug.Log("Login successful: " + response.json.Print());
                }
                else
                {
                    Debug.LogError("Failed to login: " + response.error);
                }
            });
        }

        public void Logout()
        {
            AuthAPI.Logout((success, response) =>
            {
                if (success)
                {
                    Debug.Log("Logout successful: " + response.json.Print());
                }
                else
                {
                    Debug.LogError("Failed to logout: " + response.error);
                }
            });
        }

        private void UploadContent(bool build = false)
        {
            try
            {
                if (string.IsNullOrEmpty(contentId))
                {
                    Debug.LogError("❌ Game ID not detected. Make sure your content folder exists under Assets/Content.");
                    return;
                }

                isUploading = true;
                uploadCompleted = false;
                uploadSucceeded = false;
                SetUploadStatus(
                    build ? "Preparing release" : "Preparing reupload",
                    build
                        ? "Fetching metadata and staging the full compile pipeline."
                        : "Fetching metadata and getting the existing build artifacts ready.",
                    0.05f);

                Debug.Log($"🚀 Uploading content for {contentId}...");

                ContentAPI.GetContent(contentId, (exists, response) =>
                {
                    Action uploadBuiltContent = () =>
                    {
                        var contentDirectory = response != null ? response.json : null;
                        var versionNumber = contentDirectory != null && contentDirectory.HasField("content") && contentDirectory.GetField("content").HasField("versions")
                            ? contentDirectory.GetField("content").GetField("versions").list.Count + 1
                            : 1;
                        var targetUrl = $"{DreamParkAPI.devBaseUrl}/app/content/addressables/{contentId}/{versionNumber}";

                        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        string serverDataPath = Path.Combine(projectRoot, "ServerData");

                        // Step counter for the progress bar. Total reflects what we'll
                        // actually run: 7 setup steps when build == true (clear, settings,
                        // sync, groups, logo, namespaces, package), plus one per enabled
                        // platform, plus one for the manifest diff computation. Unity's
                        // own progress bars take over for SwitchActiveBuildTarget and
                        // BuildPlayerContent inside each platform step, so we don't try
                        // to slice those further.
                        int numPlatforms = (buildAndroid ? 1 : 0) + (buildIos ? 1 : 0)
                                         + (buildOsx ? 1 : 0) + (buildWindows ? 1 : 0);
                        int currentStep = 0;
                        int totalSteps = (build ? 7 + numPlatforms : 0) + 1; // +1 for manifest computation
                        Action<string> reportStep = (message) =>
                        {
                            currentStep++;
                            float stageProgress = Mathf.Clamp01((float)currentStep / Mathf.Max(1, totalSteps));
                            SetUploadStatus(
                                build ? "Compiling release" : "Preparing upload",
                                message,
                                stageProgress);
                            EditorUtility.DisplayProgressBar(
                                "Compile & Upload",
                                $"({currentStep}/{totalSteps}) {message}",
                                stageProgress);
                        };

                        bool buildSuccess = true;
                        try
                        {
                            if (build)
                            {
                                if (!buildAndroid && !buildIos && !buildOsx && !buildWindows)
                                {
                                    throw new Exception("Select at least one build target.");
                                }

                                reportStep("Clearing previous build artifacts...");
                                if (Directory.Exists(serverDataPath))
                                {
                                    Directory.Delete(serverDataPath, true);
                                    Debug.Log("ServerData folder deleted successfully.");
                                }
                                else
                                {
                                    Debug.LogWarning("ServerData folder does not exist.");
                                }
                                Caching.ClearCache();
                                Addressables.ClearResourceLocators();
                                AssetDatabase.Refresh();
                                Debug.Log("✅ Cache purge complete.");

                                reportStep("Configuring addressable settings...");
                                var settings = AddressableAssetSettingsDefaultObject.Settings;
                                settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
                                settings.MonoScriptBundleCustomNaming = contentId + "_";
                                // Pin catalog filename to the contentId so every build produces
                                // "catalog_{contentId}.json" instead of using the app bundleVersion.
                                Debug.Log($"📛 [BEFORE] OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                                settings.OverridePlayerVersion = contentId;
                                Debug.Log($"📛 [AFTER]  OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                                // Pinned to Unity's default (true) — see the
                                // longer comment in BeginUploadFromPopup's
                                // configure step for the production-crash
                                // context that drove this revert.
                                settings.NonRecursiveBuilding = true;
                                EditorUtility.SetDirty(settings);
                                AssetDatabase.SaveAssets();

                                // Move ThirdPartyLocal assets referenced by content
                                // into ThirdParty/ before the Addressables build picks
                                // them up. ThirdPartyLocal is gitignored / build-
                                // excluded, so without this step a content that
                                // references e.g. a Models or Textures asset still
                                // sitting in ThirdPartyLocal would either ship a
                                // broken bundle or skip the asset entirely. Running
                                // the sync here makes "Compile & Upload" the one-
                                // button flow it's meant to be — the previously-
                                // manual Manage Third Party Assets step is folded in.
                                reportStep("Syncing third-party assets...");
                                try
                                {
                                    ThirdPartySyncTool.RunSyncForContent(contentId);
                                }
                                catch (Exception syncEx)
                                {
                                    // Sync failures shouldn't abort the whole upload —
                                    // they typically mean "no ThirdPartyLocal folder
                                    // exists" or "no references found," both fine.
                                    // The tool logs its own details; we just note the
                                    // soft failure here.
                                    Debug.LogWarning($"[ContentUploader] Third-party sync skipped: {syncEx.Message}");
                                }
                                // ThirdPartySyncTool clears the progress bar in its
                                // own finally block. Re-display ours so the upload
                                // flow's progress stays visible to the user.

                                // Force AssetDatabase to fully observe everything
                                // ThirdPartySync just moved. Without this refresh,
                                // freshly-moved assets can be invisible to the
                                // AssetDatabase.FindAssets call inside ForceUpdateContent
                                // and never get registered as addressables (e.g., a
                                // newly-arrived texture never lands in any bundle even
                                // though a tracked material references it).
                                AssetDatabase.SaveAssets();
                                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                                reportStep("Updating addressable groups...");
                                ContentProcessor.ForceUpdateContent(contentId);

                                // Janitor pass — drop missing-reference entries and
                                // empty stale-prefix groups (e.g. YOUR_GAME_HERE-*
                                // leftovers from the SDK template after the
                                // new-park rename).
                                ContentProcessor.CleanupAddressableSettings();

                                reportStep("Updating logo entry...");
                                SyncLogoAddressableEntry();

                                reportStep("Enforcing content namespaces...");
                                ContentProcessor.EnforceContentNamespaces(contentId);

                                reportStep("Building scripts package...");
                                buildSuccess &= ContentProcessor.BuildUnityPackage(contentId);

                                if (!buildSuccess) throw new Exception("Unity package build failed");
                                if (buildAndroid)
                                {
                                    reportStep("Building Android...");
                                    buildSuccess &= BuildForTarget(BuildTarget.Android, BuildTargetGroup.Android, $"{targetUrl}/Android", contentId);
                                    if (!buildSuccess) throw new Exception("Android build failed");
                                }
                                if (buildIos)
                                {
                                    reportStep("Building iOS...");
                                    buildSuccess &= BuildForTarget(BuildTarget.iOS, BuildTargetGroup.iOS, $"{targetUrl}/iOS", contentId);
                                    if (!buildSuccess) throw new Exception("iOS build failed");
                                }
                                if (buildOsx)
                                {
                                    reportStep("Building StandaloneOSX...");
                                    buildSuccess &= BuildForTarget(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneOSX", contentId);
                                    if (!buildSuccess) throw new Exception("OSX build failed");
                                }
                                if (buildWindows)
                                {
                                    reportStep("Building StandaloneWindows...");
                                    buildSuccess &= BuildForTarget(BuildTarget.StandaloneWindows, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneWindows", contentId);
                                    if (!buildSuccess) throw new Exception("Windows build failed");
                                }
                            }

                            reportStep("Computing patch estimate...");

                            // Build a manifest of what's currently in ServerData (the just-built
                            // output, or whatever existed for "Try Reupload"), diff it against
                            // the saved baseline, and route the diff through UploadModeFilter to
                            // turn the active UploadMode into a skipSet.
                            //
                            // - All:        skipSet = null (full re-upload)
                            // - Patch:      skipSet = unchanged-file keys, server fills gaps via fallback
                            // - CodeOnly:   skipSet = everything except catalog + {gameId}-Code bundle.
                            //               Aborts here if non-Code bundles also changed (would
                            //               produce a catalog referencing local-only hashes).
                            // - PreviewsOnly: same shape as CodeOnly for the Previews bundle.
                            //
                            // Legacy bundling forces UploadMode.All regardless of UI selection
                            // because the carve-out groups for Code/Previews don't exist and
                            // baseline-driven patching wasn't validated on Legacy output.
                            BuildManifest currentManifest = null;
                            HashSet<string> skipSet = null;
                            // Populated only on the Failed-Only retry path —
                            // bundles that uploaded successfully in a previous
                            // run but never reached commitUpload. Plumbed into
                            // ContentAPI.UploadContent so commitUpload's
                            // uploadedFiles map references the full set, not
                            // just the bundles we re-sent this round.
                            List<DreamPark.API.UploadedFileRecord> preUploadedFiles = null;
                            bool patchingEnabled = IsPatchUploadEnabled();
                            UploadMode effectiveMode = patchingEnabled ? pendingUploadMode : UploadMode.All;
                            bool modeWasDowngraded = effectiveMode != pendingUploadMode;
                            UploadModeFilter.Result modeResult = null;

                            // Failed-Only short-circuit: the user clicked
                            // "Upload Failed Bundles" on the Try Reupload
                            // dialog. Build a skip-set that excludes only the
                            // bundles that failed last time, and seed the
                            // commit payload with the previous run's successes.
                            // The Failed-Only filter takes precedence over the
                            // UploadMode picker (Patch/All/Code/Previews) —
                            // mixing them doesn't have well-defined semantics.
                            if (pendingFailedOnly)
                            {
                                try
                                {
                                    var manifestPlatformsFO = GetEnabledPlatformsForManifest();
                                    currentManifest = BuildManifestStore.BuildFromServerData(contentId, versionNumber, manifestPlatformsFO);

                                    var failedRecord = FailedBundleStore.Load(contentId);
                                    if (failedRecord == null || !failedRecord.HasRetryableFailures)
                                    {
                                        // The dialog gated on Load() returning a
                                        // retryable record, so this is the "the
                                        // store got cleared between dialog and
                                        // upload" race. Fall back to Reupload
                                        // All by leaving skipSet null and
                                        // logging an explanation.
                                        Debug.LogWarning("[ContentUploader] Failed-Only requested but no failed-run record exists — falling back to Reupload All.");
                                        pendingFailedOnly = false;
                                    }
                                    else
                                    {
                                        // Build the set of "{platform}/{fileName}"
                                        // keys currently in ServerData so we can
                                        // intersect with the persisted failed
                                        // keys. A failed bundle that no longer
                                        // exists on disk (e.g. the user
                                        // rebuilt with different content) is
                                        // silently dropped — there's nothing
                                        // to retry.
                                        var currentKeys = new HashSet<string>(System.StringComparer.Ordinal);
                                        foreach (var p in currentManifest.platforms)
                                        {
                                            foreach (var f in p.files)
                                                currentKeys.Add($"{p.platform}/{f.fileName}");
                                        }

                                        var failedKeys = FailedBundleStore.FailedKeys(failedRecord);
                                        var failedKeysInBuild = new HashSet<string>(System.StringComparer.Ordinal);
                                        foreach (var k in failedKeys)
                                            if (currentKeys.Contains(k)) failedKeysInBuild.Add(k);

                                        if (failedKeysInBuild.Count == 0)
                                        {
                                            EditorUtility.ClearProgressBar();
                                            string msg = "The failed bundles from the previous run are no longer in this build — none of them matched a file currently in ServerData/. Use Reupload All to re-send everything.";
                                            Debug.LogWarning($"[ContentUploader] {msg}");
                                            EditorUtility.DisplayDialog("Nothing failed to retry", msg, "OK");
                                            CompleteUploadStatus(false, msg);
                                            isUploading = false;
                                            pendingFailedOnly = false;
                                            return;
                                        }

                                        skipSet = FailedBundleStore.BuildSkipSetForFailedOnly(currentKeys, failedKeysInBuild);

                                        // Filter the persisted succeeded list
                                        // to only entries whose file is still
                                        // in the current build — a stale
                                        // uploadPath would commit a reference
                                        // to a bundle that's no longer part
                                        // of this version.
                                        preUploadedFiles = new List<DreamPark.API.UploadedFileRecord>();
                                        foreach (var s in failedRecord.succeeded)
                                        {
                                            if (s == null || string.IsNullOrEmpty(s.platform) || string.IsNullOrEmpty(s.fileName) || string.IsNullOrEmpty(s.uploadPath))
                                                continue;
                                            string key = $"{s.platform}/{s.fileName}";
                                            if (!currentKeys.Contains(key)) continue;
                                            preUploadedFiles.Add(new DreamPark.API.UploadedFileRecord(s.platform, s.fileName, s.uploadPath));
                                        }

                                        // Snapshot baseline / diff fields for
                                        // the panel UI without affecting the
                                        // upload logic — keeps the visual
                                        // state in sync with what's about to
                                        // happen.
                                        patchBaseline = patchingEnabled ? BuildManifestStore.LoadBaseline(contentId) : null;
                                        patchCurrentSnapshot = currentManifest;
                                        patchDiff = BuildManifestStore.Diff(patchBaseline, currentManifest);
                                        dirtyGroupsEstimate = null;

                                        Debug.Log(
                                            $"📦 Failed-Only retry: re-sending {failedKeysInBuild.Count} bundle(s) that failed last run · " +
                                            $"replaying {preUploadedFiles.Count} previously-uploaded path(s) in commitUpload · " +
                                            $"{currentKeys.Count - failedKeysInBuild.Count} file(s) intentionally skipped.");
                                        Repaint();
                                    }
                                }
                                catch (Exception failedOnlyEx)
                                {
                                    Debug.LogWarning($"[ContentUploader] Failed-Only setup failed; falling back to Reupload All: {failedOnlyEx.Message}");
                                    skipSet = null;
                                    preUploadedFiles = null;
                                    currentManifest = null;
                                    pendingFailedOnly = false;
                                }
                            }

                            if (!pendingFailedOnly)
                            {
                            try
                            {
                                var manifestPlatforms = GetEnabledPlatformsForManifest();
                                currentManifest = BuildManifestStore.BuildFromServerData(contentId, versionNumber, manifestPlatforms);
                                BuildManifest baseline = patchingEnabled
                                    ? BuildManifestStore.LoadBaseline(contentId)
                                    : null;
                                var diff = BuildManifestStore.Diff(baseline, currentManifest);

                                modeResult = UploadModeFilter.Build(effectiveMode, contentId, currentManifest, patchingEnabled ? diff : null);

                                // Sanity check for Code/Previews-only: an empty target group
                                // (no Lua scripts, no preview PNGs) leaves no bundle to ship.
                                // SmartBundleGrouper's empty-group sweep removes the group,
                                // Addressables skips producing a bundle, and we'd end up
                                // uploading just a catalog — technically successful but a
                                // no-op for the runtime. Flag it instead.
                                if (string.IsNullOrEmpty(modeResult.blockingError)
                                    && (effectiveMode == UploadMode.CodeOnly || effectiveMode == UploadMode.PreviewsOnly))
                                {
                                    var targetCat = effectiveMode == UploadMode.CodeOnly
                                        ? UploadModeFilter.FileCategory.CodeBundle
                                        : UploadModeFilter.FileCategory.PreviewsBundle;
                                    bool hasTargetBundle = false;
                                    foreach (var p in currentManifest.platforms)
                                    {
                                        foreach (var f in p.files)
                                        {
                                            if (UploadModeFilter.Categorize(contentId, f.fileName) == targetCat)
                                            {
                                                hasTargetBundle = true;
                                                break;
                                            }
                                        }
                                        if (hasTargetBundle) break;
                                    }
                                    if (!hasTargetBundle)
                                    {
                                        string what = effectiveMode == UploadMode.CodeOnly
                                            ? "Lua scripts (*.lua.txt under Assets/Content/" + contentId + "/)"
                                            : "preview images (PNG/JPG under Assets/Content/" + contentId + "/Previews/)";
                                        modeResult.blockingError =
                                            $"{UploadModePrefs.ShortLabel(effectiveMode)} upload aborted: " +
                                            $"no {UploadModePrefs.ShortLabel(effectiveMode)} bundle was produced by this build. " +
                                            $"Add some {what} and rebuild, or pick a different upload mode.";
                                    }
                                }

                                // Hard-block path: a Code/Previews-only mode that would ship a
                                // broken catalog. Surface the same message to the EditorLog and
                                // a modal, then bail cleanly without touching the version
                                // counter on the backend.
                                if (!string.IsNullOrEmpty(modeResult.blockingError))
                                {
                                    Debug.LogError($"[ContentUploader] {modeResult.blockingError}");
                                    EditorUtility.ClearProgressBar();
                                    EditorUtility.DisplayDialog(
                                        $"{UploadModePrefs.ShortLabel(effectiveMode)} upload blocked",
                                        modeResult.blockingError,
                                        "OK");
                                    CompleteUploadStatus(false, modeResult.blockingError);
                                    isUploading = false;
                                    return;
                                }

                                skipSet = modeResult.skipSet;

                                if (effectiveMode == UploadMode.All)
                                {
                                    Debug.Log(
                                        $"📦 Upload All: sending full upload of {currentManifest.TotalFileCount} file(s) · " +
                                        $"{BuildManifestStore.FormatBytes(currentManifest.TotalBytes)}" +
                                        (modeWasDowngraded ? $" (Legacy bundling forced All; requested {pendingUploadMode})." : "."));
                                }
                                else if (effectiveMode == UploadMode.Patch)
                                {
                                    long changed = diff.TotalChangedBytes;
                                    long total = diff.TotalCurrentBytes;
                                    int changedFiles = diff.TotalChangedFileCount;
                                    Debug.Log(
                                        $"📦 Patch estimate: {changedFiles} changed file(s) · " +
                                        $"{BuildManifestStore.FormatBytes(changed)} of " +
                                        $"{BuildManifestStore.FormatBytes(total)} will upload " +
                                        $"({(skipSet?.Count ?? 0)} unchanged file(s) skipped).");
                                }
                                else
                                {
                                    Debug.Log(
                                        $"📦 {UploadModePrefs.ShortLabel(effectiveMode)} upload: " +
                                        $"{modeResult.filesToUpload} file(s) · " +
                                        $"{BuildManifestStore.FormatBytes(modeResult.bytesToUpload)} will upload " +
                                        $"({modeResult.filesSkipped} file(s) intentionally skipped).");
                                }

                                // Refresh the panel's cached state so the UI reflects what
                                // we're about to do.
                                patchBaseline = baseline;
                                patchCurrentSnapshot = currentManifest;
                                patchDiff = diff;
                                dirtyGroupsEstimate = null;
                                Repaint();
                            }
                            catch (Exception manifestEx)
                            {
                                Debug.LogWarning($"[ContentUploader] Patch estimate failed; falling back to full upload: {manifestEx.Message}");
                                skipSet = null;
                                currentManifest = null;
                                modeResult = null;
                            }
                            } // end !pendingFailedOnly

                            // Build the compact manifest summary that rides along on commitUpload —
                            // gives dreampark-core's content manager UI both "full content size"
                            // and "patch size" without the runtime having to walk Storage.
                            JSONObject manifestSummary = null;
                            try
                            {
                                if (currentManifest != null)
                                {
                                    var diffForSummary = patchingEnabled
                                        ? BuildManifestStore.Diff(BuildManifestStore.LoadBaseline(contentId), currentManifest)
                                        : null;
                                    manifestSummary = BuildManifestStore.BuildCommitSummary(currentManifest, diffForSummary);
                                }
                            }
                            catch (Exception summaryEx)
                            {
                                Debug.LogWarning($"[ContentUploader] Could not build manifest summary: {summaryEx.Message}");
                                manifestSummary = null;
                            }

                            try
                            {
                                if (manifestSummary == null || manifestSummary.type != JSONObject.Type.Object)
                                {
                                    manifestSummary = new JSONObject(JSONObject.Type.Object);
                                }

                                manifestSummary.AddField("uploader", BuildUploaderMetadata(effectiveMode));
                            }
                            catch (Exception uploaderMetadataEx)
                            {
                                Debug.LogWarning($"[ContentUploader] Could not attach uploader metadata: {uploaderMetadataEx.Message}");
                            }

                            // Hand off to the upload step. Clear the compile-
                            // progress bar here so the dedicated launch window
                            // owns the rest of the user-facing status display.
                            EditorUtility.ClearProgressBar();
                            SetUploadStatus(
                                "Uploading release",
                                "Bundles are ready. Secure handoff in progress...",
                                1f);

                            // Zero-change short-circuit: if the diff says nothing
                            // changed since the last successful upload, don't
                            // ping commitUpload — that would just create a new
                            // backend version with no actual content. Offer the
                            // user a "Force full reupload" escape hatch that
                            // wipes the local baseline so the next click sees
                            // every file as new. This is the recovery path for
                            // a divergent baseline (e.g. partial upload failure
                            // in an older SDK that incorrectly saved the
                            // baseline).
                            // Failed-Only uploads can legitimately have a skipSet
                            // that covers most-or-all of currentManifest — that's
                            // the whole point of "only re-send what failed." The
                            // zero-change short-circuit and its "Force full
                            // reupload" prompt are only meaningful for the Patch
                            // path, so gate this check on !pendingFailedOnly.
                            bool everythingSkipped = !pendingFailedOnly
                                && patchingEnabled
                                && currentManifest != null
                                && skipSet != null
                                && currentManifest.TotalFileCount > 0
                                && skipSet.Count >= currentManifest.TotalFileCount;
                            if (everythingSkipped)
                            {
                                Debug.Log("[ContentUploader] No content changes detected — skipping upload.");
                                bool forceReupload = EditorUtility.DisplayDialog(
                                    "Nothing to upload",
                                    $"'{contentName}' is already at the latest version — no content changes were detected since the last successful upload.\n\n" +
                                    "If you believe the server is missing files (e.g. a previous upload failed partway), use Force full reupload to clear the local baseline and re-send everything.",
                                    "Force full reupload", "OK");
                                if (forceReupload)
                                {
                                    BuildManifestStore.DeleteBaseline(contentId);
                                    patchBaseline = null;
                                    patchDiff = BuildManifestStore.Diff(null, patchCurrentSnapshot);
                                    Debug.Log("[ContentUploader] Local baseline cleared. Click Try Reupload to send everything.");
                                    Repaint();
                                }
                                CompleteUploadStatus(true, "No content changes were detected, so nothing needed to upload.");
                                isUploading = false;
                                return;
                            }

                            SetUploadStatus(
                                "Uploading release",
                                "Sending changed files to DreamPark. Live file progress will appear below.",
                                1f);
                            ContentAPI.UploadContent(contentId, releaseNotes, lastSchemaVersion, skipSet, manifestSummary, preUploadedFiles, (success, apiResponse) =>
                            {
                                if (success)
                                {
                                    Debug.Log("✅ Content uploaded successfully");

                                    // Persist the just-uploaded snapshot as the new baseline
                                    // so the *next* upload's diff is "what changed since the
                                    // last successful upload." Only save on success — a failed
                                    // upload shouldn't move the baseline forward.
                                    if (patchingEnabled && currentManifest != null)
                                    {
                                        try
                                        {
                                            BuildManifestStore.SaveBaseline(currentManifest);
                                            patchBaseline = currentManifest;
                                            // Recompute the displayed diff against the
                                            // just-saved baseline so the panel immediately
                                            // shows "no pending changes" instead of stranding
                                            // the pre-upload diff on screen until the next
                                            // user action triggers a refresh.
                                            patchDiff = BuildManifestStore.Diff(patchBaseline, patchCurrentSnapshot);
                                            Repaint();
                                        }
                                        catch (Exception saveEx)
                                        {
                                            Debug.LogWarning($"[ContentUploader] Failed to save baseline: {saveEx.Message}");
                                        }
                                    }

                                    // Server is now in sync with local state — clear the
                                    // dirty-groups set so the source-aware estimate goes
                                    // back to "no pending changes" until the watchdog
                                    // sees the next file edit.
                                    try
                                    {
                                        DirtyGroupsStore.Clear(contentId);
                                        dirtyGroupsEstimate = null;
                                    }
                                    catch (Exception dgEx)
                                    {
                                        Debug.LogWarning($"[ContentUploader] Failed to clear dirty groups: {dgEx.Message}");
                                    }

                                    latestPublishedVersionNumber = versionNumber;
                                    CompleteUploadStatus(true, $"'{contentName}' uploaded successfully as {GetVersionSummaryAfterUpload(versionNumber)}.");
                                }
                                else
                                {
                                    Debug.LogError($"❌ Content uploaded failed: {apiResponse.error}");
                                    CompleteUploadStatus(false, $"Upload failed: {apiResponse.error}");
                                    EditorUtility.DisplayDialog("Error", $"Upload failed: {apiResponse.error}", "OK");
                                }
                                // Reset the Failed-Only flag at the end of every
                                // run so a subsequent normal upload (Compile &
                                // Upload, or a Try Reupload with no failed-run
                                // record) doesn't accidentally pick up the
                                // retry-only scope. BeginUploadFromPopup re-
                                // initializes this on every call too, so this
                                // is belt-and-suspenders.
                                pendingFailedOnly = false;
                                isUploading = false;
                            });
                        }
                        catch (Exception e)
                        {
                            // Clear the compile-progress bar so the error dialog
                            // isn't competing with a stale progress overlay.
                            EditorUtility.ClearProgressBar();
                            Debug.LogError("❌ Addressable build failed: " + e);
                            CompleteUploadStatus(false, $"Release failed: {e.Message}");
                            EditorUtility.DisplayDialog("Error", $"Error: {e.Message}", "OK");
                            pendingFailedOnly = false;
                            isUploading = false;
                        }
                    };

                    Action continueAfterSchemaSync = () =>
                    {
                        SetUploadStatus(
                            "Syncing schema",
                            "Checking tags and layers so the release lands cleanly on the backend.",
                            0.12f);
                        SyncTagLayerSchema((syncSuccess, syncError) =>
                        {
                            if (!syncSuccess)
                            {
                                Debug.LogError($"❌ Schema sync failed: {syncError}");
                                CompleteUploadStatus(false, $"Schema sync failed: {syncError}");
                                EditorUtility.DisplayDialog("Schema Sync Failed", syncError, "OK");
                                isUploading = false;
                                return;
                            }
                            uploadBuiltContent();
                        });
                    };

                    if (exists)
                    {
                        Debug.Log("Content found: " + response.json.Print());
                        JSONObject metadataUpdate = new JSONObject();
                        metadataUpdate.AddField("contentName", contentName);
                        metadataUpdate.AddField("contentDescription", contentDescription);
                        string logoAddress = GetLogoAddress();
                        if (!string.IsNullOrEmpty(logoAddress))
                        {
                            metadataUpdate.AddField("logoAddress", logoAddress);
                        }

                        ContentAPI.UpdateContent(contentId, metadataUpdate, (updateSuccess, updateResponse) =>
                        {
                            if (!updateSuccess)
                            {
                                Debug.LogError($"❌ Failed to update content metadata: {updateResponse.error}");
                                CompleteUploadStatus(false, $"Metadata update failed: {updateResponse.error}");
                                EditorUtility.DisplayDialog("Error", $"Metadata update failed: {updateResponse.error}", "OK");
                                isUploading = false;
                                return;
                            }
                            continueAfterSchemaSync();
                        });
                        return;
                    }
                    else if (response.statusCode == 403)
                    {
                        Debug.LogError($"❌ Content '{contentId}' is owned by another user.");
                        CompleteUploadStatus(false, "Access denied. This content ID belongs to another owner.");
                        EditorUtility.DisplayDialog("Access Denied",
                            $"Content '{contentId}' is owned by another user. Choose a different folder name in Assets/Content/ or ask the content owner to add you as a collaborator.",
                            "OK");
                        isUploading = false;
                        return;
                    }
                    else if (response.statusCode == 404)
                    {
                        // Content doesn't exist yet — create it
                        ContentAPI.AddContent(contentId, contentName, contentDescription, GetLogoAddress(), (success, response) =>
                        {
                            if (success)
                            {
                                Debug.Log($"✅ Content '{contentName}' uploaded successfully!");
                                SetUploadStatus(
                                    "Creating release record",
                                    "Project created. Moving straight into the first release build.",
                                    0.1f);
                                UploadContent(build);
                            }
                            else
                            {
                                Debug.LogError($"❌ Failed to create new content: {response.error}");
                                CompleteUploadStatus(false, $"Failed to create content: {response.error}");
                                EditorUtility.DisplayDialog("Error", $"Failed to create new content: {response.error}", "OK");
                                isUploading = false;
                            }
                        });
                    }
                    else
                    {
                        // Other errors (401, 500, network issues)
                        Debug.LogError($"❌ Failed to check content: {response.error}. Make sure you are logged in.");
                        CompleteUploadStatus(false, $"Failed to check content: {response.error}");
                        EditorUtility.DisplayDialog("Error",
                            $"Failed to check content: {response.error}\n\nMake sure you are logged in with a valid session.",
                            "OK");
                        isUploading = false;
                    }
                });
            }
            catch (Exception e)
            {
                Debug.LogError("❌ Upload failed: " + e);
                CompleteUploadStatus(false, $"Release failed: {e.Message}");
                EditorUtility.DisplayDialog("Error", $"Error: {e.Message}", "OK");
                isUploading = false;
            }
        }

        private void SyncTagLayerSchema(Action<bool, string> callback)
        {
            try
            {
                var local = TagLayerSchemaSyncUtility.ReadLocalTagManager();
                ContentAPI.SyncTagLayerSchema(contentId, 0, local.tags, local.layers, (syncSuccess, syncResponse) =>
                {
                    // If endpoint is unavailable, allow legacy backend and continue.
                    if (!syncSuccess && syncResponse != null && (syncResponse.statusCode == 404 || syncResponse.statusCode == 405))
                    {
                        lastSchemaVersion = null;
                        Debug.LogWarning("[TagLayerSchema] Schema endpoint missing on backend; continuing without schema enforcement.");
                        callback?.Invoke(true, null);
                        return;
                    }

                    if (!syncSuccess)
                    {
                        string backendError = syncResponse?.error;
                        if (syncResponse?.json != null && syncResponse.json.HasField("error"))
                        {
                            backendError = syncResponse.json.GetField("error").stringValue ?? backendError;
                        }
                        callback?.Invoke(false, backendError ?? "Failed to sync schema");
                        return;
                    }

                    var syncResult = ContentAPI.ParseTagLayerSchemaSyncResult(syncResponse);
                    if (syncResult.layerConflicts != null && syncResult.layerConflicts.Count > 0)
                    {
                        string conflictMessage = "Layer index conflicts detected:\n" + string.Join("\n", syncResult.layerConflicts);
                        callback?.Invoke(false, conflictMessage);
                        return;
                    }

                    // Strict sync before build:
                    // remap prefab tags by index changes, then apply canonical schema exactly.
                    var targetTags = TagLayerSchemaSyncUtility.BuildTargetTagOrder(syncResult.schema.tags, local.tags, preserveLocalExtras: true);
                    var remap = TagLayerSchemaSyncUtility.BuildTagRemapByIndex(local.tags, targetTags);
                    var remapResult = TagLayerSchemaSyncUtility.RemapContentPrefabsByTagName(contentId, remap);
                    if (remapResult.replacements > 0)
                    {
                        Debug.Log($"[TagLayerSchema] Remapped prefab tags for {contentId}: {remapResult.replacements} replacements across {remapResult.filesChanged} prefabs.");
                    }

                    var applyResult = TagLayerSchemaSyncUtility.ApplyCanonicalSchema(syncResult.schema.tags, syncResult.schema.layers, preserveLocalExtras: true);
                    if (!string.IsNullOrEmpty(applyResult.error))
                    {
                        callback?.Invoke(false, applyResult.error);
                        return;
                    }

                    bool schemaChangedLocally = remapResult.replacements > 0 || applyResult.changed;
                    if (schemaChangedLocally)
                    {
                        var refreshResult = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(contentId);
                        if (refreshResult.prefabsProcessed > 0)
                        {
                            Debug.Log($"[TagLayerSchema] Force refreshed prefab imports for {contentId}: {refreshResult.prefabsReserialized}/{refreshResult.prefabsProcessed}");
                        }
                    }

                    lastSchemaVersion = syncResult.schemaVersion > 0 ? syncResult.schemaVersion : (int?)null;
                    if (syncResult.updated)
                    {
                        Debug.Log($"[TagLayerSchema] Schema updated to v{syncResult.schemaVersion}. Added tags: {syncResult.addedTags.Count}, layers: {syncResult.addedLayers.Count}");
                    }
                    else
                    {
                        Debug.Log($"[TagLayerSchema] Schema already aligned at v{syncResult.schemaVersion}.");
                    }
                    if (syncResult.proposalPending)
                    {
                        Debug.LogWarning($"[TagLayerSchema] Content '{syncResult.proposalContentId ?? contentId}' has pending schema additions awaiting acceptance by core.");
                    }
                    callback?.Invoke(true, null);
                });
            }
            catch (Exception e)
            {
                callback?.Invoke(false, e.Message);
            }
        }

        private void SyncLogoAddressableEntry()
        {
            if (logoTexture == null)
            {
                return;
            }

            string logoPath = AssetDatabase.GetAssetPath(logoTexture);
            if (string.IsNullOrEmpty(logoPath))
            {
                Debug.LogWarning("No asset path found for selected logo texture.");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(logoPath);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning("No GUID found for selected logo texture.");
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("Addressable settings not found; skipping logo addressable entry.");
                return;
            }

            string groupName = $"{contentId}-Logos";
            var group = settings.groups.FirstOrDefault(g => g != null && g.Name == groupName)
                ?? settings.CreateGroup(groupName, false, false, true, new List<AddressableAssetGroupSchema>
                {
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(BundledAssetGroupSchema)),
                    (AddressableAssetGroupSchema)Activator.CreateInstance(typeof(ContentUpdateGroupSchema))
                });

            var bag = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            bag.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            bag.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            bag.UseAssetBundleCache = true;
            bag.UseAssetBundleCrc = true;
            bag.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;

            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = GetLogoAddress();
            entry.SetLabel(contentId, true, true);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"✅ Synced logo addressable: {entry.address}");
        }
        public static bool BuildForTarget(BuildTarget target, BuildTargetGroup group, string targetUrl, string contentId = null)
        {
            try
            {

                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                {
                    throw new System.Exception($"❌ Build target {target} is not supported. Please ensure the necessary build support is installed.");
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;

                Debug.Log($"Switching build target to {target} in {group}");

                // 🔹 Switch build target (Addressables only respects the *active* target)
                if (EditorUserBuildSettings.activeBuildTarget != target)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

                Debug.Log($"Setting RemoteLoadPath to {targetUrl}");
                string profileId = settings.activeProfileId;
                string existingValue = settings.profileSettings.GetValueByName(profileId, "RemoteLoadPath");
                if (string.IsNullOrEmpty(existingValue))
                {
                    settings.profileSettings.CreateValue("RemoteLoadPath", targetUrl);
                    Debug.Log("Created RemoteLoadPath profile variable");
                }
                else
                {
                    settings.profileSettings.SetValue(profileId, "RemoteLoadPath", targetUrl);
                    Debug.Log($"🌐 RemoteLoadPath set to {targetUrl}");
                }

                // Re-apply OverridePlayerVersion right before build in case a
                // Refresh() between the initial set and now reloaded the .asset
                if (!string.IsNullOrEmpty(contentId))
                {
                    settings.OverridePlayerVersion = contentId;
                    Debug.Log($"📛 [BuildForTarget] OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                }

                // Re-apply NonRecursiveBuilding defensively — any
                // AssetDatabase.Refresh() between the configure step and the
                // build could re-read AddressableAssetSettings.asset from disk
                // and lose the value we just set. Pinned to Unity's default
                // (true); see the longer comment at the configure step for the
                // production-crash context that drove this revert.
                settings.NonRecursiveBuilding = true;

                if (EditorPrefs.GetBool(CleanBeforeEachTargetPrefKey, false))
                {
                    // Addressables cache/build artifacts are target-specific.
                    // Enable this when stale bundles are suspected.
                    CleanAddressablesPlayerContentCache();
                }

                // 🔹 Kick off the Addressables build
                AddressablesPlayerBuildResult result;
                AddressableAssetSettings.BuildPlayerContent(out result);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    throw new System.Exception($"❌ Addressables build failed for {target}: {result.Error}");
                }
                Debug.Log($"✅ Addressables build complete for {target}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ Addressables build failed for {target}: {e}");
                return false;
            }
        }

        private static void CleanAddressablesPlayerContentCache()
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    Debug.LogWarning("Addressables settings not found; skipping clean step.");
                    return;
                }

                var cleanMethods = typeof(AddressableAssetSettings)
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(m => m.Name == "CleanPlayerContent")
                    .ToList();

                bool cleaned = false;
                foreach (var method in cleanMethods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(null, null);
                        cleaned = true;
                        break;
                    }

                    if (parameters.Length == 1)
                    {
                        method.Invoke(null, new object[] { settings.ActivePlayerDataBuilder });
                        cleaned = true;
                        break;
                    }
                }

                if (cleaned)
                {
                    Debug.Log("🧹 Addressables player content cache cleaned once before platform builds.");
                }
                else
                {
                    Debug.LogWarning("Addressables clean method not found; continuing with normal build.");
                }
            }
            catch (Exception cleanEx)
            {
                Debug.LogWarning($"Addressables clean step failed, continuing build: {cleanEx.Message}");
            }
        }
    }

    public class AddTeamMemberPopup : EditorWindow
    {
        private string contentId = "";
        private string emailInput = "";
        private Action<string> onSubmit;
        private bool focusOnce = false;

        public static void Show(string contentId, Action<string> onSubmit)
        {
            var win = CreateInstance<AddTeamMemberPopup>();
            win.titleContent = new GUIContent("Add Team Member");
            win.contentId = contentId;
            win.onSubmit = onSubmit;
            win.minSize = new Vector2(360, 110);
            win.maxSize = new Vector2(360, 110);
            // Center on screen using current main window's resolution
            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(main.x + (main.width - 360) / 2f, main.y + (main.height - 110) / 2f, 360, 110);
            win.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField($"Add a collaborator to '{contentId}' by email:", EditorStyles.wordWrappedLabel);
            GUILayout.Space(6);

            GUI.SetNextControlName("AddTeamMemberEmail");
            emailInput = EditorGUILayout.TextField("Email", emailInput);
            if (!focusOnce)
            {
                EditorGUI.FocusTextInControl("AddTeamMemberEmail");
                focusOnce = true;
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                Close();
            }
            GUI.enabled = !string.IsNullOrEmpty(emailInput) && emailInput.Contains("@");
            if (GUILayout.Button("Add") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.enabled))
            {
                var trimmed = emailInput.Trim();
                onSubmit?.Invoke(trimmed);
                Close();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }
}
#endif
