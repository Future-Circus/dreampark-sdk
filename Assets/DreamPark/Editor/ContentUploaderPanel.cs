#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEngine;
using System.IO;
using DreamPark.API;
using DreamPark.Editor;
using DreamPark.ParkSim;
using DreamPark.Badges;
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
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using Unity.EditorCoroutines.Editor;

namespace DreamPark {
    public partial class ContentUploaderPanel : EditorWindow
    {
        // The public Web package document and the runtime ScriptableObject
        // manifest are separate contracts. Manifest schema v3 added packing
        // metadata, but the JSON sent to Web still has the v2 Arena shape.
        // Tying these values together made the first v3 SDK upload fail Web's
        // package validation even though both schemas were individually valid.
        internal const int PublishedPackageSchemaVersion = 2;
        private string contentId = "";
        private string contentName = "";
        private string contentDescription = "";
        private string releaseNotes = "";
        private Texture2D logoTexture = null;
        private bool isUploading = false;
        private bool isLoadingMetadata = false;
        private int? latestPublishedVersionNumber = null;
        private int? lastSchemaVersion = null;
        // The source project always remains `contentId` (the real folder under
        // Assets/Content). A beta upload switches only the target identity.
        // Keeping these separate prevents a beta id from ever being treated as
        // a local folder while still letting every built/uploaded artifact use
        // the target id end-to-end.
        private ContentUploadTarget activeUploadTarget = ContentUploadTarget.Release;
        private int? betaLatestPublishedVersionNumber = null;
        private JSONObject betaContentDirectorySnapshot = null;
        private bool isLoadingBetaTarget = false;
        private string uploadStatusTitle = "";
        private string uploadStatusMessage = "";
        private float uploadStatusProgress = -1f;
        private bool uploadStatusIsError = false;
        private bool uploadCompleted = false;
        private bool uploadSucceeded = false;
        private bool uploadBuildMode = true;

        // Suppresses UI-only dialogs/browser launches while the optional Unity
        // Pipeline integration owns the upload lifecycle. This field must live
        // in the package-independent half of the partial class: during a fresh
        // SDK import DreamParkReleaseCommands.cs is intentionally compiled out
        // until its UPM dependency has resolved.
        private bool automatedReleaseMode = false;

        // Pending test build state — populated when the user clicks "Check
        // Patch Size" from the test build dialog and a one-shot estimate
        // runs (compile + diff, no upload). The bundles sit in ServerData/
        // and the testBuildId is allocated on the backend; if the user
        // then clicks the primary upload button with the same settings,
        // ResumePendingTestBuildUpload picks up where the estimate left off
        // — skipping the compile step entirely so we don't pay the 5+ min
        // build cost twice.
        // Null when no estimate is currently pending (cleared on cancel,
        // on a fresh estimate, or once the resumed upload starts).
        private string pendingTestBuildId = null;
        private string pendingTestBuildTitle = null;
        private string pendingTestBuildNotes = null;
        private string pendingTestBuildContentId = null;
        private string pendingTestBuildLogoAddress = null;
        private string pendingTestBuildParentId = null;
        private JSONObject pendingTestBuildManifestSummary = null;
        private bool pendingTestBuildOsx = false;
        private bool pendingTestBuildWindows = false;

        // Pending production estimate state — populated when the user clicks
        // "Check Patch Size" from the main Upload Release popup. This is
        // the production analogue of the test-build estimate flow: run the
        // full compile, diff the freshly-built ServerData bundles against the
        // latest backend version, then stop before uploading any bytes. If
        // the user then clicks Start with the same settings, we reuse those
        // already-built bundles and the already-computed skipSet/summary
        // instead of paying the build cost a second time.
        private bool pendingProductionEstimateOnly = false;
        private string pendingProductionContentId = null;
        private UploadMode pendingProductionMode = UploadMode.Patch;
        private bool pendingProductionBuildOsx = false;
        private bool pendingProductionBuildWindows = false;
        private int pendingProductionVersionNumber = 0;
        private bool pendingProductionPatchingEnabled = false;
        private BuildManifest pendingProductionCurrentManifest = null;
        private HashSet<string> pendingProductionSkipSet = null;
        private JSONObject pendingProductionManifestSummary = null;
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
        private const string PackagesFoldPrefKey         = "DreamPark.ContentUploader.Fold.Packages";
        private const string ParkAssetsFoldPrefKey       = "DreamPark.ContentUploader.Fold.ParkAssets";
        private const string ParkAssetsAttractionsPrefKey = "DreamPark.ContentUploader.Fold.ParkAssets.Attractions";
        private const string ParkAssetsPropsPrefKey       = "DreamPark.ContentUploader.Fold.ParkAssets.Props";
        private const string ParkAssetsPlayerPrefKey      = "DreamPark.ContentUploader.Fold.ParkAssets.Player";
        private const string ParkAssetsBadgesPrefKey      = "DreamPark.ContentUploader.Fold.ParkAssets.Badges";
        private const string OrganizerModePrefKey         = "DreamPark.ContentUploader.OrganizerModeV2";
        private bool packagesFold = true;
        private bool parkAssetsFold = true;
        private bool foldAttractions = true;
        private bool foldProps = true;
        private bool foldPlayer = true;
        private bool foldBadges = true;

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
        private JSONObject latestContentDirectorySnapshot;

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
            public string guid;
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
            public bool requiredForGame;
            public bool sequenceCompatible;
            public bool isDreamSequence;
        }
        private List<ContentRootEntry> contentRoots = new List<ContentRootEntry>();
        private string contentRootsContentId;
        private bool contentRootsDirty;
        [SerializeField] private ContentSequenceStore.Data sequenceLayout;
        [SerializeField] private ContentSequenceStore.Data libraryLayout;
        [SerializeField] private ArenaPackageStore.Data arenaLayout;
        [SerializeField] private string sequenceUndoContentId;
        private enum OrganizerMode { Arena, Sequence, Adventure }
        [SerializeField] private OrganizerMode organizerMode = OrganizerMode.Arena;
        private string editingSequencePosition;
        private string sequencePositionText = "";
        private string pendingSequenceDrag;
        private Vector2 pendingSequenceDragStart;
        private bool sequenceDragWasStarted;
        private string lastSequenceDropSource;
        private string lastSequenceDropTarget;
        private Rect lastSequenceDropRect;
        private double lastSequenceDropTime;
        private bool sequenceScaffoldQueued;
        private bool containerScaffoldQueued;
        private readonly HashSet<string> sequencePreviewAttemptedIds =
            new HashSet<string>(StringComparer.Ordinal);
        private Texture2D sequenceTabIcon;
        private Texture2D adventureTabIcon;
        private Texture2D arenaTabIcon;
        private string firstSequenceAttractionGuid;
        private string lastSequenceAttractionGuid;

        // ── Badges ──────────────────────────────────────────────────────
        // The badge cards this content package defines. Unlike contentRoots,
        // this list is NOT purely derived from the project: it is the local
        // draft (BadgeStore, .badges.json) merged with whatever BadgeLuaScanner
        // finds in the developer's own Lua. Ids that came from Lua are locked;
        // titles, descriptions and icons are always the developer's to type.
        private List<BadgeStore.Entry> badges = new List<BadgeStore.Entry>();
        private string badgesContentId;
        private BadgeLuaScanner.Result badgeScan;
        // Which Attraction/Prop/Player roots award each badge id, for the
        // "Awarded by" tooltip on the card's status icon. Recomputed alongside
        // badgeScan in RefreshBadges — see that method's comment for why this
        // is a light enough pass to run there rather than only inside the
        // pre-upload check.
        private BadgeAttributionScanner.Result badgeAttribution;
        // Asset path -> badges awarded by that root's Lua. Shared scripts can
        // resolve to different ids on different prefab instances, so this is
        // built from BadgeAttributionScanner rather than the package-wide scan.
        private Dictionary<string, List<BadgeStore.Entry>> badgesByAssetPath =
            new Dictionary<string, List<BadgeStore.Entry>>(StringComparer.Ordinal);
        // Set when a field is edited; flushed to .badges.json on the next
        // Layout pass rather than on every keystroke.
        private bool badgesDirty;
        private bool isPushingBadges;
        // Structural mutations (adding or removing a card) are QUEUED, never
        // applied mid-frame. IMGUI hands out control ids by draw order, so a
        // list that gained or lost an element between the Layout and Repaint
        // passes shifts the id stream and every text field after the edit point
        // starts eating the wrong keystrokes. Same reason preUploadBadges is
        // swapped only at Layout.
        private bool badgeAddRequested;
        private int badgeRemoveIndex = -1;

        // Per-frame snapshot of the pre-upload findings, keyed by asset path (which is
        // what ContentRootEntry carries — the GUID is discarded during the scan).
        // Rebuilt only when the report changes, never queried live from OnGUI: the
        // Layout and Repaint passes must agree on whether each card has a badge, or
        // GUI.Button's control-id stream shifts between them.
        private Dictionary<string, KeyValuePair<PreUploadChecks.CheckSeverity, string>> preUploadBadges;
        private Dictionary<string, KeyValuePair<PreUploadChecks.CheckSeverity, string>> preUploadBadgesPending;
        // Same staged-swap rule, keyed by badge id instead of asset path — feeds
        // the checkmark/warning icon on the Badges section's own cards (see
        // PreUploadCheckRunner.BuildBadgeAwardMap for why this needs its own map
        // rather than reusing preUploadBadges).
        private Dictionary<string, KeyValuePair<PreUploadChecks.CheckSeverity, string>> preUploadBadgeAwards;
        private Dictionary<string, KeyValuePair<PreUploadChecks.CheckSeverity, string>> preUploadBadgeAwardsPending;
        // True once a report has actually completed for this content. Needed
        // because "no active finding for this badge id" is ambiguous on its
        // own — it also describes the state before the first scan has run —
        // and a false-positive checkmark on a badge that just hasn't been
        // checked yet would be worse than no icon at all.
        private bool preUploadReportEverBuilt;
        private bool preUploadAdvisoryScheduled;
        private double preUploadAdvisoryDueAt;

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
            PreviewEditorWindow.PreviewSaved += OnPreviewSaved;
            // Repaint when the admin-state probe settles so the Upload
            // Test Build button in the Troubleshooting section becomes
            // visible the moment the backend's canPublish response lands
            // (without this, the button only shows up the next time the
            // user clicks the panel and forces a repaint).
            AdminState.AdminStateChanged += Repaint;
            PreUploadChecks.PreUploadCheckRunner.ReportChanged += OnPreUploadReportChanged;
            Undo.undoRedoPerformed += OnSequenceUndoRedo;

            preUploadChecksCleared = false;

            RestoreContentIdSelection();
            LoadBuildTargetSelection();
            LoadFoldoutPrefs();
            LoadSectionFoldoutPrefs();

            LoadLogoSelection();
            FetchContentMetadata();
            FetchContentUsers();
            RefreshPatchEstimate();
            RefreshContentRoots();
            RefreshBadges();
            RebuildPreUploadBadges();
            ScheduleAdvisoryPreUploadScan();
        }

        private void LoadFoldoutPrefs()
        {
            packagesFold    = EditorPrefs.GetBool(PackagesFoldPrefKey,          true);
            parkAssetsFold  = EditorPrefs.GetBool(ParkAssetsFoldPrefKey,        true);
            foldAttractions = EditorPrefs.GetBool(ParkAssetsAttractionsPrefKey, true);
            foldProps       = EditorPrefs.GetBool(ParkAssetsPropsPrefKey,       true);
            foldPlayer      = EditorPrefs.GetBool(ParkAssetsPlayerPrefKey,      true);
            foldBadges      = EditorPrefs.GetBool(ParkAssetsBadgesPrefKey,      true);
            organizerMode = (OrganizerMode)Mathf.Clamp(
                EditorPrefs.GetInt(OrganizerModePrefKey, (int)OrganizerMode.Arena),
                (int)OrganizerMode.Arena, (int)OrganizerMode.Adventure);
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
            PreviewEditorWindow.PreviewSaved -= OnPreviewSaved;
            AdminState.AdminStateChanged -= Repaint;
            PreUploadChecks.PreUploadCheckRunner.ReportChanged -= OnPreUploadReportChanged;
            Undo.undoRedoPerformed -= OnSequenceUndoRedo;
            EditorApplication.update -= PumpAdvisoryPreUploadScan;
            preUploadAdvisoryScheduled = false;
        }

        private void OnManifestUpdated() => Repaint();

        // ------------------------------------------------------------------
        // Pre-upload checks (advisory pass — the blocking gate lives in
        // BeginUploadFromPopup).

        private void OnPreUploadReportChanged()
        {
            RebuildPreUploadBadges();
            Repaint();
        }

        // Staged, not applied. The report can change on an editor tick that lands
        // between this frame's Layout and Repaint events; swapping the map right then
        // adds or removes a GUI.Button in the card grid, which shifts every subsequent
        // control id and misroutes clicks. The swap happens at the top of the next
        // Layout (see OnGUI) so both passes always agree.
        private void RebuildPreUploadBadges()
        {
            var report = PreUploadChecks.PreUploadCheckRunner.CachedReportFor(contentId);
            preUploadBadgesPending = PreUploadChecks.PreUploadCheckRunner.BuildBadgeMap(report);
            preUploadBadgeAwardsPending = PreUploadChecks.PreUploadCheckRunner.BuildBadgeAwardMap(report);
            if (report != null) preUploadReportEverBuilt = true;
        }

        // Runs the cheap checks so the Park Assets tiles can carry warning badges the
        // moment the panel is looked at. Deferred, never inside OnGUI, and never while
        // the editor is busy — the scene-override check is deliberately excluded from
        // this pass because it opens scenes.
        private void ScheduleAdvisoryPreUploadScan()
        {
            if (string.IsNullOrEmpty(contentId)) return;

            // Debounced. projectChanged fires on EVERY asset import, and the advisory
            // pass loads prefab contents for each content root — running it per import
            // froze the editor for seconds every time someone saved a file with the
            // uploader open.
            preUploadAdvisoryDueAt = EditorApplication.timeSinceStartup + 1.5;

            if (preUploadAdvisoryScheduled) return;
            preUploadAdvisoryScheduled = true;
            EditorApplication.update += PumpAdvisoryPreUploadScan;
        }

        private void PumpAdvisoryPreUploadScan()
        {
            if (this == null)
            {
                EditorApplication.update -= PumpAdvisoryPreUploadScan;
                return;
            }

            if (EditorApplication.timeSinceStartup < preUploadAdvisoryDueAt) return;
            if (EditorApplication.isCompiling) return;
            if (EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (isUploading) return;

            EditorApplication.update -= PumpAdvisoryPreUploadScan;
            preUploadAdvisoryScheduled = false;

            try
            {
                PreUploadChecks.PreUploadCheckRunner.RunAdvisory(contentId);
            }
            catch (System.Exception e)
            {
                // Advisory only. It must never be able to disturb the panel.
                Debug.LogWarning($"[DreamPark] Advisory pre-upload scan failed: {e.Message}");
            }
        }

        // The Preview Editor just re-baked one preview PNG. Refresh only that
        // card: clearing every cached thumbnail here made the whole grid appear
        // to lose its previews after saving a single asset.
        private void OnPreviewSaved(string savedContentId, string savedAssetPath)
        {
            if (savedContentId != contentId) return;
            ContentRootEntry entry = contentRoots.FirstOrDefault(root =>
                string.Equals(root.assetPath, savedAssetPath, StringComparison.Ordinal));
            if (entry == null) return;

            string previewsFolder = $"Assets/Content/{contentId}/Previews";
            entry.customPreview = TryLoadPreviewFromFolder(
                previewsFolder, AssetDatabase.IsValidFolder(previewsFolder), entry.name);
            entry.autoPreview = null;
            entry.autoPreviewResolved = entry.customPreview != null;
            entry.firstPollTime = 0;
            // AssetDatabase.Refresh inside the renderer raises projectChanged.
            // That change is fully handled above; avoid replacing the whole card
            // list on the next OnGUI pass and invalidating unrelated textures.
            contentRootsDirty = false;
            Repaint();
        }

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
            // Force fresh metadata + team list for the new id. Clear the
            // per-content name/description too, otherwise a brand-new (or
            // not-yet-named) content keeps the previously selected content's
            // values and uploads them as its own identity.
            releaseNotes = "";
            contentName = "";
            contentDescription = "";
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
            string previouslySelectedId = contentId;
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
            else if (!string.IsNullOrEmpty(previouslySelectedId))
            {
                int existingIndex = contentIdOptions.IndexOf(previouslySelectedId);
                if (existingIndex >= 0) contentIdIndex = existingIndex;
                else RestoreContentIdSelection();
            }
        }

        private void OnGUI()
        {
            // Swap in a new badge map ONLY at the start of a Layout pass. IMGUI hands
            // out control ids by draw order, so if the map gained or lost an entry
            // between Layout and Repaint the card grid would allocate a different
            // number of controls in each pass and every click after that point would
            // land on the wrong control.
            if (Event.current.type == EventType.Layout && preUploadBadgesPending != null)
            {
                preUploadBadges = preUploadBadgesPending;
                preUploadBadgesPending = null;
            }
            if (Event.current.type == EventType.Layout && preUploadBadgeAwardsPending != null)
            {
                preUploadBadgeAwards = preUploadBadgeAwardsPending;
                preUploadBadgeAwardsPending = null;
            }

            // Same rule for the badge list: add/remove only ever happens between
            // frames, so Layout and Repaint always agree on how many cards (and
            // therefore how many control ids) the grid draws.
            if (Event.current.type == EventType.Layout)
            {
                ApplyQueuedBadgeMutations();
                FlushBadgeDraft();
            }

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

            // Session-expiry nudge. /auth/refresh validates a session but never extends
            // it, so the ~14-day deadline set at sign-in is final — and the failure mode
            // without this notice is a creator finding out when a 40-minute upload 401s at
            // the commit step, with the build already spent and nothing to resume from.
            // sessionExpiresInHours returns -1 when the expiry is unknown (a session
            // stored by an SDK older than this field); that must stay silent rather than
            // nag every existing user forever.
            double sessionHoursLeft = AuthAPI.sessionExpiresInHours;
            if (sessionHoursLeft >= 0 && sessionHoursLeft <= 48)
            {
                EditorGUILayout.HelpBox(
                    sessionHoursLeft < 1
                        ? "Your DreamPark session expires within the hour. Log out and back in before starting an upload."
                        : $"Your DreamPark session expires in about {Mathf.CeilToInt((float)sessionHoursLeft)}h. Log out and back in before starting a long upload.",
                    MessageType.Warning);
            }

            GUILayout.Space(10);
            GUILayout.Label("Content Uploader", EditorStyles.boldLabel);

            // ContentId dropdown
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Content ID", GUILayout.Width(EditorGUIUtility.labelWidth));
            int prevIndex = contentIdIndex;
            string previousContentId = contentId;

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
            if (prevIndex != contentIdIndex || previousContentId != contentId)
            {
                // Clear per-content fields on switch so the new selection can't
                // inherit the previous content's name/description. FetchContentMetadata
                // repopulates them from the server if this content already has them.
                releaseNotes = "";
                contentName = "";
                contentDescription = "";
                SaveContentIdSelection();
                LoadLogoSelection();
                FetchContentMetadata();
                FetchContentUsers();
                RefreshPatchEstimate();
                RefreshContentRoots();
                RefreshBadges();
            }

            // Deferred refresh: the projectChanged callback only sets a
            // dirty flag — we coalesce the rebuild to one pass here per
            // OnGUI tick so a big import doesn't thrash the preview list.
            // Also re-syncs if the cached content id drifts from the
            // selected one (e.g. dropdown restored from prefs).
            bool sequenceDragActive = pendingSequenceDrag != null
                || DragAndDrop.GetGenericData(SequenceDragKey) is string;
            if ((contentRootsDirty || contentRootsContentId != contentId) && !isUploading && !sequenceDragActive)
            {
                RefreshContentRoots();

                // Badges ride the same debounce: a new .lua.txt, a changed @var
                // default, or a badgeId typed into a LuaBehaviour's Inspector all
                // arrive as projectChanged, and all three change what the scan
                // should find.
                RefreshBadges();

                // Piggyback: this block already fires exactly when the root set
                // changed (content-id switch, projectChanged, preview save) and is
                // already debounced, so the advisory findings stay in lockstep with
                // the tiles for free.
                RebuildPreUploadBadges();
                ScheduleAdvisoryPreUploadScan();
            }

            if (contentIdOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No content folders found under Assets/Content. Please create at least one game/content folder.", MessageType.Warning);
            }

            // ── Sample notice. DELIBERATELY NOT A GATE.
            //
            //    Sample exists to be read. It is the one fully wired example a
            //    new creator has — real attractions, real props, previews,
            //    dimensions, a logo, the lot — so the panel stays completely
            //    browsable while it is selected. Returning out of the draw here
            //    (as Gate 1 does for the untouched template) would hide the
            //    exact thing they came to look at.
            //
            //    What IS off is every action that pushes to the backend; see
            //    UploadsBlocked. Sample ships with every copy of the SDK, so
            //    "upload to Sample" means publishing over a contentId that
            //    exists identically in everyone's install — the first person to
            //    do it would take ownership of an SDK default for everybody
            //    else. The backend refuses it outright
            //    (lib/reservedContentIds.js); this is the local half, so the
            //    refusal is explained up front rather than discovered as a
            //    failed upload after a full compile.
            if (ContentFolders.IsSample(contentId))
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "'Sample' is the worked example bundled with the SDK — browse it freely to see " +
                    "how content is set up.\n\n" +
                    "Upload actions are disabled: this content ID ships with every copy of the SDK, " +
                    "so it is reserved and the backend will refuse it. Pick your own folder from the " +
                    "dropdown above when you are ready to publish.",
                    MessageType.Info);

                // Only offered when the template folder is actually on disk —
                // the popup renames an existing folder, it cannot create one.
                if (!ContentFolders.HasNamedGame() && ContentFolders.PlaceholderExists()
                    && GUILayout.Button("Set Up My Game Folder", GUILayout.Height(24)))
                {
                    ContentIdSetupPopup.Show(ContentFolders.PlaceholderName, OnContentFolderRenamed);
                }
                GUILayout.Space(4);
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
                contentDescription = EditorGUILayout.TextArea(
                    contentDescription,
                    WrappedTextAreaStyle,
                    GUILayout.MinHeight(52),
                    GUILayout.ExpandWidth(true));
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
                DrawLegacyBundlingNotice();
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
                    // Route through the same manual-check path as
                    // DreamPark ▸ Check for SDK Updates instead of calling
                    // UpdateAvailablePopup.Show ourselves.
                    //
                    // This panel is a pure READER of SDKUpdateChecker's cache,
                    // and that cache is written once per domain generation by
                    // the [InitializeOnLoad] check at editor load / login. So
                    // LatestDownloadUrl here is as old as the Unity session.
                    // The manifest's downloadUrl is a signed storage link that
                    // UpdateAvailablePopup GETs with no Authorization header —
                    // once the signature expires the download comes back 400.
                    // That is why the identical popup worked from the menu item
                    // (CheckForUpdateManual re-fetches the manifest immediately
                    // before showing it) and 400'd from here.
                    //
                    // CheckForUpdateManual re-fetches the manifest, calls
                    // SDKVersion.Reload(), bypasses skip/remind state and then
                    // shows the popup with a seconds-old URL. It also fixes the
                    // stale LatestVersion this warning renders when a release
                    // ships mid-session.
                    SDKUpdateChecker.CheckForUpdateManual();
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

            if (BeginSectionBox(ref foldReleaseLaunch, SectionReleaseLaunchPrefKey, "Release Launch", null))
            {
                DrawLaunchActions(sdkOutOfDate);
                EndSectionBox();
            }

            CommitCachedSequenceDropOnRelease();
            EditorGUILayout.EndScrollView();
        }

        private bool BeginSectionBox(ref bool foldState, string prefKey, string title, string iconName)
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUIContent icon = string.IsNullOrEmpty(iconName) ? null : EditorGUIUtility.IconContent(iconName);
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

        // EditorGUILayout.TextArea() without an explicit style falls back to
        // EditorStyles.textField, which has wordWrap = false. A non-wrapping
        // control reports its min layout width as the full pixel width of its
        // text, so one long description line forces the whole window wider —
        // and because the window's width feeds back into the layout next
        // frame, it just keeps growing. Wrapping fixes the root cause: a
        // wrapping style's min width is a single character, so the control
        // fills the available width instead of demanding more.
        private static GUIStyle wrappedTextAreaStyle;
        private static GUIStyle WrappedTextAreaStyle
        {
            get
            {
                if (wrappedTextAreaStyle == null)
                {
                    wrappedTextAreaStyle = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = true
                    };
                }
                return wrappedTextAreaStyle;
            }
        }

        internal void SetActiveUploadTarget(ContentUploadTarget target)
        {
            if (activeUploadTarget == target) return;
            activeUploadTarget = target;
            ClearPendingProductionEstimateState();
            patchBaseline = null;
            patchCurrentSnapshot = null;
            patchDiff = null;
            dirtyGroupsEstimate = null;
            InvalidatePatchBaselineCache();
            Repaint();
        }

        private void ShowReleaseUploadFlow(bool build, bool failedOnly = false)
        {
            SetActiveUploadTarget(ContentUploadTarget.Release);
            ContentUploadFlowPopup.Show(this, build, failedOnly, ContentUploadTarget.Release);
        }

        private void ShowBetaUploadFlow()
        {
            if (isLoadingBetaTarget || isUploading) return;

            string expectedBetaId = BetaContentId.DeriveFrom(contentId);
            if (string.IsNullOrEmpty(expectedBetaId) || !BetaContentId.IsPathSafe(expectedBetaId))
            {
                EditorUtility.DisplayDialog(
                    "Beta target unavailable",
                    $"'{contentId}' cannot be used to derive a safe beta target. Rename the content folder and try again.",
                    "OK");
                return;
            }

            isLoadingBetaTarget = true;
            SetUploadStatus(
                "Preparing beta target",
                $"Allocating the separate content target '{expectedBetaId}' and loading its version history.",
                0.02f);
            Repaint();

            ContentAPI.EnsureBetaContentTarget(contentId, (allocated, allocateResponse) =>
            {
                if (!allocated)
                {
                    isLoadingBetaTarget = false;
                    string error = allocateResponse?.json?.GetField("message")?.stringValue
                        ?? allocateResponse?.error
                        ?? "The beta target could not be allocated.";
                    CompleteUploadStatus(false, error);
                    EditorUtility.DisplayDialog("Beta target unavailable", error, "OK");
                    return;
                }

                string allocatedId = allocateResponse?.json?.GetField("contentId")?.stringValue;
                if (!string.Equals(allocatedId, expectedBetaId, StringComparison.Ordinal))
                {
                    isLoadingBetaTarget = false;
                    string error = $"The backend returned beta target '{allocatedId ?? "(missing)"}', but the SDK expected '{expectedBetaId}'. Upload was stopped before building anything.";
                    CompleteUploadStatus(false, error);
                    EditorUtility.DisplayDialog("Beta target mismatch", error, "OK");
                    return;
                }

                ContentAPI.GetContent(allocatedId, (loaded, loadResponse) =>
                {
                    isLoadingBetaTarget = false;
                    if (!loaded || loadResponse?.json == null || !loadResponse.json.HasField("content"))
                    {
                        string error = loadResponse?.error ?? "The allocated beta target could not be loaded.";
                        CompleteUploadStatus(false, error);
                        EditorUtility.DisplayDialog("Beta target unavailable", error, "OK");
                        return;
                    }

                    betaContentDirectorySnapshot = loadResponse.json;
                    JSONObject betaContent = loadResponse.json.GetField("content");
                    betaLatestPublishedVersionNumber = betaContent.HasField("versions")
                        && betaContent.GetField("versions").list != null
                        ? betaContent.GetField("versions").list.Count
                        : 0;
                    SetActiveUploadTarget(ContentUploadTarget.Beta);
                    ResetCompletionStateForNextRun();
                    ContentUploadFlowPopup.Show(this, true, false, ContentUploadTarget.Beta);
                });
            });
        }

        private void DrawLaunchActions(bool sdkOutOfDate)
        {
            bool shippable = HasShippableContent();
            bool hasBuildArtifacts = patchCurrentSnapshot != null && patchCurrentSnapshot.TotalFileCount > 0;
            bool canLaunch = !isUploading
                             && !UploadsBlocked
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
                    ContentUploadFlowPopup.Show(this, uploadBuildMode, false, activeUploadTarget);
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
                ? "Upload Release"
                : "Upload Release (add an Attraction or Prop first)";
            if (GUILayout.Button(compileLabel, GUILayout.Height(34)))
            {
                SaveLogoSelection();
                // Fresh version check at click time — the passive sdkOutOfDate
                // gate above reads the once-per-session manifest cache, which
                // goes stale if the editor stays open across an SDK release.
                // Out of date → routes to UpdateAvailablePopup instead of the
                // upload popup.
                SDKUpdateChecker.EnsureUpToDateThen(() => ShowReleaseUploadFlow(true));
            }
            GUI.enabled = true;

            string betaTargetId = BetaContentId.DeriveFrom(contentId);
            GUI.enabled = canLaunch && !isLoadingBetaTarget && !string.IsNullOrEmpty(betaTargetId);
            string betaLabel = isLoadingBetaTarget
                ? "Preparing Beta Target…"
                : $"Upload Beta ({betaTargetId})";
            if (GUILayout.Button(new GUIContent(
                betaLabel,
                "Build and upload to a fully separate content target. Its catalog, bundles, versions, schema attribution, metadata, and attraction rows do not touch the release target."),
                GUILayout.Height(34)))
            {
                SaveLogoSelection();
                SDKUpdateChecker.EnsureUpToDateThen(ShowBetaUploadFlow);
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(betaTargetId))
            {
                EditorGUILayout.LabelField(
                    $"Beta is isolated from release as content ID '{betaTargetId}'.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            GUI.enabled = canLaunch && hasBuildArtifacts;
            string reuploadLabel = hasBuildArtifacts
                ? "Try Reupload"
                : "Try Reupload (no build artifacts)";
            if (GUILayout.Button(new GUIContent(reuploadLabel,
                hasBuildArtifacts
                    ? "Re-upload the contents of ServerData/ without rebuilding."
                    : "Run Upload Release first — ServerData/ is empty."),
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

                // Same click-time version gate as Upload Release — a reupload
                // still publishes bundles built against the stale SDK.
                bool failedOnlyFinal = useFailedOnly;
                SDKUpdateChecker.EnsureUpToDateThen(() => ShowReleaseUploadFlow(false, failedOnlyFinal));
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
                "Correctness checks run automatically when you upload. The optimizers below are "
                + "optional — they shrink the bundle size your players download and speed up "
                + "first-launch attraction load times.",
                MessageType.None);

            GUILayout.Space(4);

            // The correctness gate, surfaced as a button so it can be run deliberately
            // rather than only discovered at upload time. Framed apart from the
            // optimizers below: those are about size, this is about whether the content
            // is correct at all.
            {
                var reviewIcon = EditorGUIUtility.IconContent("console.warnicon");
                var report = PreUploadChecks.PreUploadCheckRunner.CachedReportFor(contentId);

                string suffix = "";
                if (report != null)
                {
                    if (report.BlockingCount > 0) suffix = $"  ({report.BlockingCount} blocking)";
                    else if (report.WarningCount > 0) suffix = $"  ({report.WarningCount} warning{(report.WarningCount == 1 ? "" : "s")})";
                }

                var reviewContent = new GUIContent(
                    " Review Pre-Upload Checks..." + suffix,
                    reviewIcon != null ? reviewIcon.image : null,
                    "Duplicate prefab names, directional lights in content, materials missing Meta "
                    + "occlusion, unapplied scene overrides, and dependencies living outside this "
                    + "content folder. Runs automatically before every upload; this runs the full "
                    + "suite now, including the scene scan.");

                if (GUILayout.Button(reviewContent, GUILayout.Height(28)))
                {
                    PreUploadChecks.PreUploadChecksPopup.ShowForReview(this, contentId);
                }
            }

            GUILayout.Space(6);

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
            GUI.enabled = !isUploading && !UploadsBlocked && !string.IsNullOrEmpty(contentId) && shippable;
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

            // ─── Force upload all previews ───────────────────────
            // Regenerates every attraction/prop preview PNG and pushes each to
            // this content's attractions catalog (POST /api/content/:id/
            // attractions/preview), refreshing previews even for assets that
            // didn't change — a manual repair path independent of a version
            // upload. Uses the same session auth as the normal upload flow.
            GUILayout.Space(6);
            GUI.enabled = !isUploading && !UploadsBlocked && !string.IsNullOrEmpty(contentId);
            if (GUILayout.Button(new GUIContent(
                "Force Upload All Previews",
                "Regenerates and uploads a preview image for every attraction and prop in this " +
                "content, updating the attractions catalog even if the asset didn't change. " +
                "Use this to repair or refresh previews without publishing a new version."),
                GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog(
                    "Force Upload All Previews",
                    "Regenerate and upload preview images for every attraction and prop in \"" + contentId + "\"?",
                    "Upload Previews", "Cancel"))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(ForceUploadAllPreviewsRoutine(contentId));
                }
            }
            GUI.enabled = true;

            // ─── Update attraction dimensions ────────────────────
            // Reads every attraction's authored footprint (LevelTemplate
            // size/customSize, in feet) and pushes the batch to this
            // content's attractions catalog (POST /api/content/:id/
            // attractions/dimensions), where the backend derives each
            // attraction's size-reference tag ("fits a Basketball Court").
            // A manual repair/backfill path — the same push runs silently
            // after every upload. Also available for ALL content folders at
            // once via DreamPark → Troubleshooting → Update Attraction
            // Dimensions.
            GUILayout.Space(6);
            GUI.enabled = !isUploading && !UploadsBlocked && !string.IsNullOrEmpty(contentId);
            if (GUILayout.Button(new GUIContent(
                "Update Attraction Dimensions",
                "Rebakes flexible packing, then uploads every attraction's authored dimensions, " +
                "shrink/grow ranges, safe area, and essential-prop profile to the catalog. Use " +
                "this to backfill content published before flexible packing metadata existed."),
                GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog(
                    "Update Attraction Dimensions",
                    "Rebake and upload dimensions plus flexible packing for every attraction in \"" + contentId + "\"?",
                    "Upload Dimensions", "Cancel"))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(UploadAttractionDimensionsRoutine(contentId));
                }
            }
            GUI.enabled = true;

            // ─── Re-upload Logo ──────────────────────────────────
            // Pushes the selected Logo texture straight to the backend
            // (POST /api/content/:id/logo), refreshing the web-renderable
            // logoImageUrl without publishing a new version. The bundled
            // logoAddress the VR client uses is unaffected.
            GUILayout.Space(6);
            GUI.enabled = !isUploading && !UploadsBlocked && !string.IsNullOrEmpty(contentId) && logoTexture != null;
            if (GUILayout.Button(new GUIContent(
                "Re-upload Logo",
                "Uploads the selected Logo image directly to DreamPark so web, iOS, and admin " +
                "surfaces show the latest logo — no version upload needed."),
                GUILayout.Height(22)))
            {
                UploadLogoImage(contentId, interactive: true);
            }
            GUI.enabled = true;

            // ─── Test Channel upload ─────────────────────────────
            // Admin / dreampark.app teammates only. Pushes the bundles
            // currently sitting in ServerData/ to the Test Channel in
            // dreampark-core's Content Manager — a separate listing
            // outside the Beta/Release versioning flow that auto-expires
            // after 7 days. Useful for handing an in-progress test
            // build to internal SDK / smartpacker development without
            // burning a real version number.
            //
            // AdminState.IsAdmin is sourced from /api/sdk/canPublish on
            // the backend, which calls getAdminAccessForEmail — the same
            // primitive the test-content backend gates with. So if this
            // button is rendered, the upload will succeed; if IsAdmin
            // hasn't probed yet (null) the button stays hidden rather
            // than disabled, to keep the section uncluttered for non-team
            // users.
            if (AdminState.IsAdmin == true)
            {
                GUILayout.Space(6);
                bool testShippable = HasShippableContent();
                GUI.enabled = !isUploading && !UploadsBlocked && !string.IsNullOrEmpty(contentId) && testShippable;
                if (GUILayout.Button(new GUIContent(
                    "Upload Test Build (Test Channel)",
                    "DreamPark teammates only.\n\n" +
                    "Runs a fresh editor-only compile (Mac and/or Windows — picked in the dialog) " +
                    "and pushes the resulting bundles to the Test Channel in dreampark-core's " +
                    "Content Manager. Test builds live in their own listing, separate from " +
                    "Beta/Release, and auto-expire after 7 days. iOS and Android are skipped " +
                    "because test builds are meant for previewing inside the dreampark-core " +
                    "Unity editor."),
                    GUILayout.Height(22)))
                {
                    BeginTestBuildUpload();
                }
                GUI.enabled = true;
            }
        }

        // ── Force Upload All Previews ────────────────────────────────
        // Regenerates preview PNGs, then uploads one per attraction/prop to the
        // content's attractions catalog (POST /api/content/:id/attractions/
        // preview). Sequential so a big catalog doesn't fire hundreds of
        // concurrent requests. A repair path, independent of a version upload.
        private class PreviewUploadRoot
        {
            public string name;
            public string resourceName;
            public byte[] previewBytes;
        }

        // ─── Logo image upload (direct to backend) ─────────────────────
        // The logo has always shipped inside the Unity bundle (logoAddress —
        // that's what the VR client loads and it is untouched here). This
        // ALSO pushes the raw image file to the backend
        // (POST /api/content/:id/logo → content.logoImageUrl) so iOS, web,
        // and admin render the logo without touching Unity bundles — same
        // pattern as the attraction preview uploads. Runs fire-and-forget
        // inside the normal upload flow; manual repair lives in
        // Troubleshooting → "Re-upload Logo".
        private byte[] ReadLogoImageBytes(out string fileName, out string mimeType)
        {
            fileName = null; mimeType = null;
            if (logoTexture == null) return null;

            // Prefer the source asset file on disk — original bytes, no
            // Read/Write import requirement.
            string path = AssetDatabase.GetAssetPath(logoTexture);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".png") mimeType = "image/png";
                else if (ext == ".jpg" || ext == ".jpeg") mimeType = "image/jpeg";
                else if (ext == ".webp") mimeType = "image/webp";
                if (mimeType != null)
                {
                    try
                    {
                        fileName = Path.GetFileName(path);
                        return File.ReadAllBytes(path);
                    }
                    catch (Exception e) { Debug.LogWarning("[Logo] source read failed: " + e.Message); }
                }
            }

            // Fallback (PSD/TGA sources, unreadable textures): blit to a
            // readable copy and encode PNG.
            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(logoTexture.width, logoTexture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(logoTexture, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D readable = new Texture2D(logoTexture.width, logoTexture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                byte[] png = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);
                fileName = logoTexture.name + ".png";
                mimeType = "image/png";
                return png;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Logo] PNG encode fallback failed: " + e.Message);
                return null;
            }
        }

        private void UploadLogoImage(string idForUpload, bool interactive)
        {
            string auth = AuthAPI.GetUserAuth();
            if (string.IsNullOrEmpty(auth))
            {
                if (interactive) EditorUtility.DisplayDialog("Not signed in", "Sign in to DreamPark before uploading the logo.", "OK");
                return;
            }
            if (logoTexture == null)
            {
                if (interactive) EditorUtility.DisplayDialog("No logo selected", "Pick a Logo texture in the uploader first.", "OK");
                return;
            }
            string fileName, mimeType;
            byte[] bytes = ReadLogoImageBytes(out fileName, out mimeType);
            if (bytes == null || bytes.Length == 0)
            {
                if (interactive) EditorUtility.DisplayDialog("Logo unreadable", "Couldn't read the logo image bytes from the selected texture.", "OK");
                return;
            }

            string endpoint = "/api/content/" + Uri.EscapeDataString(idForUpload) + "/logo";
            UploadContentData image = new UploadContentData(fileName, bytes);
            image.mimeType = mimeType;
            List<KeyValuePair<string, UploadContentData>> files = new List<KeyValuePair<string, UploadContentData>>
            {
                new KeyValuePair<string, UploadContentData>("image", image)
            };
            DreamParkAPI.POST(endpoint, auth, files, (s, resp) =>
            {
                if (s)
                {
                    Debug.Log("[Logo] logo image uploaded to backend for " + idForUpload);
                    if (interactive) EditorUtility.DisplayDialog("Logo uploaded", "Logo pushed to DreamPark — web, iOS, and admin surfaces now use it.", "OK");
                }
                else
                {
                    string err = (resp != null && !string.IsNullOrEmpty(resp.error)) ? resp.error : "upload failed";
                    Debug.LogWarning("[Logo] backend logo upload failed: " + err);
                    if (interactive) EditorUtility.DisplayDialog("Logo upload failed", err, "OK");
                }
            });
        }

        // interactive: true = Troubleshooting button (dialogs + progress bar +
        // forced preview regeneration). false = silent post-upload push — runs
        // automatically after a successful commit, because the backend's
        // preview endpoint is attach-only against rows the server's commit-
        // time catalog sync just created. Silent mode only fills in missing
        // preview PNGs (the compile pipeline already generated them).
        private IEnumerator ForceUploadAllPreviewsRoutine(
            string idForUpload,
            bool interactive = true,
            string sourceContentId = null)
        {
            string localContentId = string.IsNullOrEmpty(sourceContentId) ? idForUpload : sourceContentId;
            string auth = AuthAPI.GetUserAuth();
            if (string.IsNullOrEmpty(auth))
            {
                if (interactive) EditorUtility.DisplayDialog("Not signed in", "Sign in to DreamPark before uploading previews.", "OK");
                else Debug.LogWarning("[Previews] auto-push skipped: not signed in.");
                yield break;
            }

            // 1) Regenerate the preview PNGs (same generator the compile pipeline runs).
            if (interactive) EditorUtility.DisplayProgressBar("Force Upload All Previews", "Regenerating preview images…", 0f);
            try { ContentProcessor.GenerateAllLevelPreviews(localContentId, forceRegenerate: interactive); }
            catch (Exception e) { Debug.LogWarning("[Previews] regenerate failed: " + e.Message); }
            AssetDatabase.Refresh();

            // 2) Collect attraction/prop roots + their preview bytes.
            List<PreviewUploadRoot> roots = CollectPreviewUploadRoots(localContentId);
            if (roots.Count == 0)
            {
                if (interactive)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("No attractions", "No attractions or props were found under Assets/Content/" + localContentId + ".", "OK");
                }
                yield break;
            }

            // 3) Upload each preview sequentially.
            int ok = 0, missing = 0, failed = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                PreviewUploadRoot r = roots[i];
                if (interactive) EditorUtility.DisplayProgressBar("Force Upload All Previews", r.name + " (" + (i + 1) + "/" + roots.Count + ")", (float)i / roots.Count);

                if (r.previewBytes == null || r.previewBytes.Length == 0) { missing++; continue; }

                // resourceName is the ONLY parameter the endpoint reads (it looks
                // the row up by slug and attaches the image; it never creates one).
                // `name` and `category` used to ride along here and were never
                // read on either side — and category is now portal state, assigned
                // after upload against the server-owned taxonomy, so sending the
                // SDK's frozen PropTemplate.category would have been actively
                // misleading had anything started reading it.
                string endpoint = "/api/content/" + Uri.EscapeDataString(idForUpload) + "/attractions/preview"
                    + "?resourceName=" + Uri.EscapeDataString(r.resourceName);

                UploadContentData image = new UploadContentData(r.name + ".png", r.previewBytes);
                image.mimeType = "image/png";
                List<KeyValuePair<string, UploadContentData>> files = new List<KeyValuePair<string, UploadContentData>>
                {
                    new KeyValuePair<string, UploadContentData>("image", image)
                };

                bool done = false, success = false;
                string err = null;
                DreamParkAPI.POST(endpoint, auth, files, (s, resp) =>
                {
                    success = s;
                    if (!s) err = (resp != null && !string.IsNullOrEmpty(resp.error)) ? resp.error : "upload failed";
                    done = true;
                });
                while (!done) yield return null;

                if (success) ok++;
                else { failed++; Debug.LogWarning("[Previews] " + r.name + " failed: " + err); }
            }

            string summary = ok + " uploaded" +
                (missing > 0 ? ", " + missing + " with no preview file" : "") +
                (failed > 0 ? ", " + failed + " failed" : "") + ".";
            if (interactive)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Previews uploaded", summary, "OK");
            }
            else
            {
                Debug.Log("[Previews] auto-push: " + summary);
            }
        }

        // Walks Assets/Content/{id} for Attraction (LevelTemplate) and Prop
        // (PropTemplate) prefabs, derives each one's addressable resourceName
        // using the same convention ContentProcessor bakes into the catalog,
        // and loads its preview PNG from the Previews/ folder.
        private List<PreviewUploadRoot> CollectPreviewUploadRoots(string idForUpload)
        {
            List<PreviewUploadRoot> list = new List<PreviewUploadRoot>();
            string contentRoot = "Assets/Content/" + idForUpload;
            if (!AssetDatabase.IsValidFolder(contentRoot)) return list;
            string previewsFolder = contentRoot + "/Previews";

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (DreamSequenceGenerator.IsSpecialLevelPath(idForUpload, path)) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                string name = Path.GetFileNameWithoutExtension(path);

                LevelTemplate level = prefab.GetComponent<LevelTemplate>();
                PropTemplate prop = prefab.GetComponent<PropTemplate>();
                if (level == null && prop == null) continue; // attractions + props only, never the player rig

                // resourceName must match the backend catalog key exactly: the asset
                // path with the leading "Assets/" and the file extension stripped
                // (see leafStem() in lib/addressablesCatalog.js) — e.g.
                // "Content/SuperAdventureLand/.../A_BeachParty". This keeps the
                // "Sync Attractions" (catalog scrape) and preview uploads on one key.
                string resourceName = path;
                if (resourceName.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    resourceName = resourceName.Substring("Assets/".Length);
                int extDot = resourceName.LastIndexOf('.');
                if (extDot >= 0) resourceName = resourceName.Substring(0, extDot);

                list.Add(new PreviewUploadRoot
                {
                    name = name,
                    resourceName = resourceName,
                    previewBytes = ReadPreviewBytes(previewsFolder, name),
                });
            }
            return list;
        }

        // ── Update Dimensions ────────────────────────────────────────
        // Collects every PLACEABLE's footprint — attractions from LevelTemplate
        // size/customSize (authored, FEET, custom-aware) and props from
        // PropTemplate.FootprintMeters (measured, converted) — and pushes the
        // batch to the backend catalog in ONE request
        // (POST /api/content/:id/attractions/dimensions; props share the
        // endpoint because they share the collection —
        // content/{id}/attractions/{slug} holds attraction, prop AND level
        // rows). Attach-only server-side, exactly like previews: rows are
        // created by the commit-time catalog sync; this only fills them in.
        //
        // THE TWO KINDS ARE NOT INTERCHANGEABLE and the wire does not pretend
        // they are. An attraction's footprint is authored and is shown to
        // operators as a measurement; a prop's is inferred from collider
        // geometry and exists only to answer "is this bigger than that" for
        // relative marker scale on the 2D park maps. The server keeps them
        // apart by refusing to stamp a size-reference tag on a prop row —
        // which is also why props are not logged with one below. See the
        // FootprintMeters docblock in PropTemplate for the longer version.
        //
        // Runs: silently after every successful upload (auto-push), from the
        // panel's Troubleshooting section, and from DreamPark →
        // Troubleshooting → Update Attraction Dimensions (all content folders).
        private class DimensionUploadRoot
        {
            public string guid;
            public string name;
            public string resourceName;
            public float widthFt;
            public float lengthFt;
            // Suppresses the size-reference tag in logs. The server makes the
            // same call independently off the row's own kind — this flag is a
            // console nicety, never the authority.
            public bool isProp;
            // LevelTemplate.WallsWireValue / PropTemplate.WallsWireValue
            // — comma-joined axis tokens ("+z,-x") in the prefab's own local
            // frame, "" when no side is toggled. GENERATE_LAYOUT (dreampark-core
            // SpaceMapPacker) parses this to auto-route items into its wall
            // pass instead of needing them pre-sorted by the caller.
            public string walls = "";
            // Feet, only meaningful when walls is non-empty. 0 means "not
            // applicable" (no wall declared) rather than "authored zero
            // height" — omitted from the wire entirely in that case (see the
            // AddField below) so the server's own 10ft default stays in
            // control rather than a sentinel value trying to mean two things.
            public float wallHeightFt;
            // Optional flexible-packing metadata. Kept off prop rows: this is
            // an AttractionTemplate layout family, not a generic collider size.
            public AttractionPackingBake packingBake;
            public float safeArea;
            public bool requiredForGame;
            public bool sequenceCompatible;
            public bool hidden;
            public bool participatesInSequence;
        }

        // Backend catalog key derivation, shared with the preview walk: the
        // asset path minus the leading "Assets/" and the extension (see
        // leafStem() in lib/addressablesCatalog.js).
        private static string ResourceNameForAssetPath(string path)
        {
            string resourceName = path;
            if (resourceName.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                resourceName = resourceName.Substring("Assets/".Length);
            int extDot = resourceName.LastIndexOf('.');
            if (extDot >= 0) resourceName = resourceName.Substring(0, extDot);
            return resourceName;
        }

        // Feet per metre — the SDK's own constant, matching
        // GameLevelDimensions.GetDimensionsInMeters' 0.3048 the other way
        // round. FEET is the wire unit for this endpoint (the server's
        // attractionSizes.normalizeDimensions takes widthFt/lengthFt), so a
        // prop measured in metres converts HERE rather than teaching the
        // endpoint a second unit — one unit on the wire, one place to be
        // wrong.
        private const float FeetPerMeter = 1f / 0.3048f;

        // Every placeable with a readable footprint: LevelTemplate roots
        // (attractions and legacy levels) plus PropTemplate roots. A prefab
        // carrying BOTH is a LevelTemplate first — PropTemplate suppresses
        // itself under a template parent anyway, and the address namespace
        // that the catalog keys on is /Levels/.
        private static List<DimensionUploadRoot> CollectDimensionRoots(string idForUpload)
        {
            List<DimensionUploadRoot> list = new List<DimensionUploadRoot>();
            string contentRoot = "Assets/Content/" + idForUpload;
            if (!AssetDatabase.IsValidFolder(contentRoot)) return list;

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (DreamSequenceGenerator.IsSpecialLevelPath(idForUpload, path)) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                float widthFt;
                float lengthFt;
                bool isProp;
                string walls;
                float wallHeightFt = 0f;
                AttractionPackingBake packingBake = null;
                float safeArea = 0f;
                bool requiredForGame = false;
                bool sequenceCompatible = false;
                bool participatesInSequence = false;

                LevelTemplate level = prefab.GetComponent<LevelTemplate>();
                if (level != null)
                {
                    Vector2 feet = level.DimensionsInFeet;
                    if (!(feet.x > 0f) || !(feet.y > 0f)) continue; // unset custom size etc.
                    widthFt = feet.x;
                    lengthFt = feet.y;
                    isProp = false;
                    walls = level.WallsWireValue;
                    if (!string.IsNullOrEmpty(walls)) wallHeightFt = level.GetWallHeightMeters() * FeetPerMeter;
                    var attraction = level as AttractionTemplate;
                    if (attraction != null && attraction.HasPackingBake)
                    {
                        packingBake = attraction.PackingBake;
                        safeArea = attraction.safeArea;
                    }
                    if (attraction != null)
                    {
                        requiredForGame = attraction.gameRequiresAttraction;
                        sequenceCompatible = DreamSequenceCompatibility.IsCompatible(attraction);
                        participatesInSequence = prefab.GetComponent<DreamSequenceTemplate>() == null;
                    }
                }
                else
                {
                    PropTemplate prop = prefab.GetComponent<PropTemplate>();
                    if (prop == null) continue;

                    // Always positive — FootprintMeters sanitizes to the 1 m
                    // fallback rather than returning something unusable, so a
                    // prop is never silently dropped from the batch. A prop
                    // missing from the catalog is indistinguishable from one
                    // whose developer has not re-uploaded yet, and the map
                    // would draw both at the fallback size regardless; sending
                    // the row makes the state legible in the response summary.
                    Vector2 meters = prop.FootprintMeters;
                    widthFt = meters.x * FeetPerMeter;
                    lengthFt = meters.y * FeetPerMeter;
                    isProp = true;
                    participatesInSequence = true;
                    walls = prop.WallsWireValue;
                    if (!string.IsNullOrEmpty(walls)) wallHeightFt = prop.GetWallHeightMeters() * FeetPerMeter;
                }

                list.Add(new DimensionUploadRoot
                {
                    guid = guid,
                    name = Path.GetFileNameWithoutExtension(path),
                    resourceName = ResourceNameForAssetPath(path),
                    widthFt = widthFt,
                    wallHeightFt = wallHeightFt,
                    lengthFt = lengthFt,
                    isProp = isProp,
                    walls = walls,
                    packingBake = packingBake,
                    safeArea = safeArea,
                    requiredForGame = requiredForGame,
                    sequenceCompatible = sequenceCompatible,
                    participatesInSequence = participatesInSequence,
                });
            }

            var orderedPackageGuids = list.Where(root => root.participatesInSequence)
                    .OrderBy(root => root.isProp ? 1 : 0)
                    .ThenBy(root => root.name, StringComparer.OrdinalIgnoreCase)
                    .Select(root => root.guid).ToList();
            var layout = ContentSequenceStore.LoadAndReconcile(idForUpload,
                orderedPackageGuids, list.Where(root => !root.isProp).Select(root => root.guid), false);
            ContentSequenceStore.Save(idForUpload, layout);
            ContentSequenceStore.Data library = ContentSequenceStore.LoadLibraryAndReconcile(
                idForUpload, orderedPackageGuids, layout);
            ContentSequenceStore.SaveLibrary(idForUpload, library);
            foreach (DimensionUploadRoot root in list.Where(root => root.participatesInSequence))
            {
                root.hidden = ContentSequenceStore.IsHidden(library, root.guid)
                    || library.items.Any(item => item != null && item.IsWorld && item.hidden
                        && (item.attractionGuids ?? new List<string>()).Contains(root.guid));
                if (!root.isProp)
                    root.requiredForGame = root.requiredForGame
                        || root.guid == layout.startGuid || root.guid == layout.endGuid;
            }
            return list;
        }

        // interactive: true = Troubleshooting buttons (dialogs + summary).
        // false = silent post-upload push, mirroring the previews auto-push —
        // must run AFTER the commit because the endpoint is attach-only
        // against rows the server's commit-time catalog sync creates.
        private static IEnumerator UploadAttractionDimensionsRoutine(
            string idForUpload,
            bool interactive = true,
            bool rebakeBeforeUpload = true,
            string sourceContentId = null)
        {
            string localContentId = string.IsNullOrEmpty(sourceContentId) ? idForUpload : sourceContentId;
            string auth = AuthAPI.GetUserAuth();
            if (string.IsNullOrEmpty(auth))
            {
                if (interactive) EditorUtility.DisplayDialog("Not signed in", "Sign in to DreamPark before uploading dimensions.", "OK");
                else Debug.LogWarning("[Dimensions] auto-push skipped: not signed in.");
                yield break;
            }

            if (rebakeBeforeUpload)
            {
                try
                {
                    int baked = AttractionPackingBaker.BakeAllInContent(localContentId);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[Dimensions] refreshed packing data for " + baked + " attraction prefab(s) before catalog upload.");
                }
                catch (Exception e)
                {
                    Debug.LogError("[Dimensions] packing bake failed for " + localContentId + ": " + e.Message);
                    if (interactive) EditorUtility.DisplayDialog("Packing bake failed", e.Message, "OK");
                    yield break;
                }
            }

            List<DimensionUploadRoot> roots = CollectDimensionRoots(localContentId);
            if (roots.Count == 0)
            {
                if (interactive)
                    EditorUtility.DisplayDialog("Nothing to measure", "No attractions or props with a footprint were found under Assets/Content/" + localContentId + ".", "OK");
                else
                    Debug.Log("[Dimensions] auto-push: nothing to send for " + localContentId + ".");
                yield break;
            }

            // ONE batched request — dims are tiny, unlike preview PNGs.
            JSONObject payload = new JSONObject(JSONObject.Type.Object);
            JSONObject arr = new JSONObject(JSONObject.Type.Array);
            foreach (DimensionUploadRoot r in roots)
            {
                JSONObject row = new JSONObject(JSONObject.Type.Object);
                row.AddField("resourceName", r.resourceName);
                row.AddField("widthFt", r.widthFt);
                row.AddField("lengthFt", r.lengthFt);
                row.AddField("walls", r.walls);
                // Omitted (not zero) when there's no wall to measure, so the
                // server's own 10ft default stays in control — see the field's
                // own comment on DimensionUploadRoot.
                if (r.wallHeightFt > 0f) row.AddField("wallHeightFt", r.wallHeightFt);
                if (!r.isProp)
                {
                    // Presence is authoritative. An explicit null clears a
                    // catalog bake if flexible packing was removed from the
                    // prefab; omitting the field would leave stale database
                    // capability data behind forever.
                    row.AddField("packing", r.packingBake != null && r.packingBake.IsValid
                        ? BuildPackingUpload(r.packingBake, r.safeArea)
                        : new JSONObject(JSONObject.Type.Null));
                }
                if (!r.isProp)
                {
                    row.AddField("requiredForGame", r.requiredForGame);
                    row.AddField("sequenceCompatible", r.sequenceCompatible);
                }
                row.AddField("hidden", r.hidden);
                arr.Add(row);
                // The size-reference ladder bottoms out at a 4 x 4 ft phone booth,
                // so EVERY prop would tag "fits a Phone Booth" — a line that reads
                // like a measurement and carries no information. Props log their
                // metres instead, which is the unit they were authored in.
                if (r.isProp)
                {
                    Debug.Log("[Dimensions] " + r.name + " (prop): "
                        + (r.widthFt / FeetPerMeter).ToString("0.##") + " × "
                        + (r.lengthFt / FeetPerMeter).ToString("0.##") + " m");
                }
                else
                {
                    var reference = AttractionSizeReference.Compute(r.widthFt, r.lengthFt);
                    Debug.Log("[Dimensions] " + r.name + ": " + r.widthFt.ToString("0.#") + " × " + r.lengthFt.ToString("0.#")
                        + " ft" + (reference != null ? " (fits a " + reference.Value.label + ")" : ""));
                }
            }
            payload.AddField("attractions", arr);

            bool done = false, success = false;
            string err = null;
            JSONObject result = null;
            DreamParkAPI.POST("/api/content/" + Uri.EscapeDataString(idForUpload) + "/attractions/dimensions", auth, payload, (s, resp) =>
            {
                success = s && resp != null && resp.statusCode == 200;
                result = resp != null ? resp.json : null;
                if (!success) err = (resp != null && !string.IsNullOrEmpty(resp.error)) ? resp.error : "upload failed";
                done = true;
            });
            while (!done) yield return null;

            if (success)
            {
                int updated = result != null && result.GetField("updated") != null ? result.GetField("updated").intValue : roots.Count;
                int skipped = result != null && result.GetField("skipped") != null ? result.GetField("skipped").intValue : 0;
                int invalidPacking = result != null && result.GetField("invalidPacking") != null ? result.GetField("invalidPacking").intValue : 0;
                // Non-zero only if the SDK and server disagree about the wall
                // token vocabulary — the one failure mode of a two-vocabulary
                // field, and otherwise silent everywhere (the row still
                // uploads, just with the offending side quietly gone). Surfaced
                // rather than logged-only so it's not missed in the common
                // (interactive) path.
                int droppedWallSides = result != null && result.GetField("droppedWallSides") != null ? result.GetField("droppedWallSides").intValue : 0;
                string summary = updated + " footprint" + (updated == 1 ? "" : "s") + " updated" +
                    (skipped > 0 ? ", " + skipped + " not in the catalog yet (upload a build first)" : "") +
                    (invalidPacking > 0 ? ", " + invalidPacking + " packing profile" + (invalidPacking == 1 ? "" : "s") + " rejected (last good database bake preserved)" : "") +
                    (droppedWallSides > 0 ? ", " + droppedWallSides + " wall side" + (droppedWallSides == 1 ? "" : "s") + " rejected by the server (vocabulary mismatch — check for an SDK/backend version skew)" : "") + ".";
                if (interactive) EditorUtility.DisplayDialog("Dimensions uploaded", summary, "OK");
                else Debug.Log("[Dimensions] auto-push: " + summary);
                if (droppedWallSides > 0)
                    Debug.LogWarning("[Dimensions] " + droppedWallSides + " wall side(s) were rejected by the server — the SDK and backend disagree on the wall token vocabulary.");
                if (invalidPacking > 0)
                    Debug.LogWarning("[Dimensions] " + invalidPacking + " packing profile(s) were rejected by the server; the last good database bake was preserved. Check for an SDK/backend version skew.");
            }
            else
            {
                Debug.LogWarning("[Dimensions] upload failed for " + idForUpload + ": " + err);
                if (interactive) EditorUtility.DisplayDialog("Dimensions upload failed", err, "OK");
            }
        }

        private static JSONObject BuildPackingUpload(AttractionPackingBake bake, float safeArea)
        {
            var packing = new JSONObject(JSONObject.Type.Object);
            packing.AddField("schemaVersion", AttractionPackingBake.CatalogSchemaVersion);
            packing.AddField("safeAreaInset", Mathf.Clamp(safeArea, 0f, 0.49f));
            packing.AddField("authored", PackingDimensions(bake.AuthoredFootprintMeters));
            packing.AddField("safe", PackingDimensions(bake.SafeFootprintMeters));
            packing.AddField("shrink", PackingDimensions(bake.ShrinkFootprintMeters));
            packing.AddField("grow", PackingDimensions(bake.GrowFootprintMeters));
            packing.AddField("essentialShrink", PackingDimensions(bake.EssentialShrinkFootprintMeters));
            packing.AddField("shrinkScale", PackingScale(bake.ShrinkScale));
            packing.AddField("growScale", PackingScale(bake.GrowScale));
            packing.AddField("essentialShrinkScale", PackingScale(bake.EssentialShrinkScale));

            var essential = new JSONObject(JSONObject.Type.Array);
            var props = bake.Props;
            for (int i = 0; i < props.Count; i++)
            {
                AttractionPropPackingPose prop = props[i];
                if (prop == null || !prop.Essential) continue;
                var item = new JSONObject(JSONObject.Type.Object);
                item.AddField("name", prop.DisplayName);
                item.AddField("resourceName", prop.ResourceName);
                item.AddField("hierarchyPath", prop.HierarchyPath);
                item.AddField("widthFt", prop.FootprintMeters.x * FeetPerMeter);
                item.AddField("lengthFt", prop.FootprintMeters.y * FeetPerMeter);
                essential.Add(item);
            }
            packing.AddField("essentialProps", essential);
            return packing;
        }

        private static JSONObject PackingDimensions(Vector2 meters)
        {
            var dimensions = new JSONObject(JSONObject.Type.Object);
            dimensions.AddField("widthFt", meters.x * FeetPerMeter);
            dimensions.AddField("lengthFt", meters.y * FeetPerMeter);
            return dimensions;
        }

        private static JSONObject PackingScale(Vector2 scale)
        {
            var value = new JSONObject(JSONObject.Type.Object);
            value.AddField("x", scale.x);
            value.AddField("z", scale.y);
            return value;
        }

        // DreamPark → Troubleshooting: push dimensions for EVERY content
        // folder under Assets/Content — the quick backfill path for parks
        // published before dimensions existed. (The panel's Troubleshooting
        // section has the same action scoped to the selected content.)
        [MenuItem("DreamPark/Troubleshooting/Update Attraction Dimensions", false, 208)]
        private static void UpdateAttractionDimensionsMenu()
        {
            string contentRoot = "Assets/Content";
            if (!AssetDatabase.IsValidFolder(contentRoot))
            {
                EditorUtility.DisplayDialog("No content", "No Assets/Content folder found.", "OK");
                return;
            }

            List<string> contentIds = new List<string>();
            foreach (string dir in Directory.GetDirectories(contentRoot))
            {
                string id = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(id)) contentIds.Add(id);
            }
            if (contentIds.Count == 0)
            {
                EditorUtility.DisplayDialog("No content", "No content folders found under Assets/Content.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                "Update Attraction Dimensions",
                "Upload authored attraction dimensions for: " + string.Join(", ", contentIds) + "?",
                "Upload Dimensions", "Cancel"))
                return;

            EditorCoroutineUtility.StartCoroutineOwnerless(UploadDimensionsForContentsRoutine(contentIds));
        }

        private static IEnumerator UploadDimensionsForContentsRoutine(List<string> contentIds)
        {
            foreach (string id in contentIds)
            {
                yield return UploadAttractionDimensionsRoutine(id, interactive: contentIds.Count == 1, rebakeBeforeUpload: true);
                if (contentIds.Count > 1) Debug.Log("[Dimensions] finished " + id);
            }
            if (contentIds.Count > 1)
                EditorUtility.DisplayDialog("Dimensions uploaded", "Pushed attraction dimensions for " + contentIds.Count + " content folders. See Console for per-content results.", "OK");
        }

        private byte[] ReadPreviewBytes(string previewsFolder, string name)
        {
            string[] exts = { ".png", ".jpg", ".jpeg" };
            foreach (string ext in exts)
            {
                string p = previewsFolder + "/" + name + ext;
                if (File.Exists(p))
                {
                    try { return File.ReadAllBytes(p); } catch { }
                }
            }
            return null;
        }

        // Entry point for the Test Channel upload button. Opens the
        // title/release-notes/platforms dialog, then on confirm runs the
        // full compile-and-upload pipeline:
        //
        //   1. Allocate a testBuildId via POST /api/test-content/create.
        //      The ID has to exist BEFORE the addressables build runs so
        //      the build can bake the test-channel URL pattern into the
        //      catalog's RemoteLoadPath. Without that step, Unity's
        //      Caching layer would key bundles against a stale production
        //      URL and the editor would re-fetch every bundle on each
        //      load instead of hitting the cache.
        //   2. Run the same compile pipeline production uses — clear
        //      ServerData, configure addressable settings, third-party
        //      sync, group update, logo entry, namespace enforcement,
        //      Unity package build — but ONLY for the editor targets the
        //      user picked (Mac / Windows). iOS and Android are
        //      intentionally skipped: test builds exist to preview in
        //      dreampark-core's editor, which runs on Mac or Windows.
        //   3. Upload everything in ServerData/ to the pre-allocated
        //      test_build doc via ContentAPI.UploadTestBuildArtifacts
        //      and commit with the final metadata + manifest.
        //
        // Wraps the whole sequence in the same isUploading guard the
        // regular Upload Release flow uses, so the rest of the panel
        // stays disabled while a test compile is in flight.
        private void BeginTestBuildUpload()
        {
            if (string.IsNullOrEmpty(contentId))
            {
                EditorUtility.DisplayDialog("No content selected", "Select a content folder before uploading a test build.", "OK");
                return;
            }

            // Step 0: fetch recent test builds first so the dialog can show
            // a patch-base picker. Filter to this content's prior builds so
            // a stale upload for a different park doesn't pollute the
            // dropdown. The default selection in the dialog is the most
            // recent surviving build for this content — patches against
            // your own last test build is the dominant workflow and we
            // want it to be one click.
            DreamPark.API.ContentAPI.GetTestBuilds((listOk, listResp) =>
            {
                var candidates = new List<TestBuildUploadDialog.PatchBaseOption>();
                if (listOk && listResp?.json != null)
                {
                    var buildsArr = listResp.json.GetField("builds");
                    if (buildsArr != null && buildsArr.type == JSONObject.Type.Array && buildsArr.list != null)
                    {
                        foreach (var b in buildsArr.list)
                        {
                            if (b == null) continue;
                            string id = b.GetField("testBuildId")?.stringValue;
                            string buildTitle = b.GetField("title")?.stringValue ?? "";
                            string buildContentName = b.GetField("contentName")?.stringValue ?? "";
                            if (string.IsNullOrEmpty(id)) continue;
                            // Only offer same-content builds as patch bases.
                            // Patching a CarnivalPub bundle onto a Cauldron
                            // base would be nonsensical (different bundle
                            // sets, ~0% filename overlap), so we filter
                            // upfront rather than surfacing useless options.
                            if (!string.Equals(buildContentName, contentId, StringComparison.OrdinalIgnoreCase))
                                continue;
                            long createdAt = 0;
                            var createdAtField = b.GetField("createdAt");
                            if (createdAtField != null && createdAtField.type == JSONObject.Type.Number)
                                createdAt = createdAtField.longValue;
                            string when = createdAt > 0
                                ? DateTimeOffset.FromUnixTimeMilliseconds(createdAt).LocalDateTime.ToString("MMM d h:mm tt")
                                : "unknown time";
                            string label = $"{buildTitle}  ·  {when}";
                            candidates.Add(new TestBuildUploadDialog.PatchBaseOption(id, label));
                        }
                    }
                }

                ShowTestBuildDialog(candidates);
            });
        }

        private void ShowTestBuildDialog(List<TestBuildUploadDialog.PatchBaseOption> candidates)
        {
            TestBuildUploadDialog.Show(contentId, candidates, (title, releaseNotes, _, doBuildOsx, doBuildWindows, parentTestBuildId, estimateOnly) =>
            {
                if (!doBuildOsx && !doBuildWindows)
                {
                    EditorUtility.DisplayDialog("No platforms selected",
                        "Pick at least one editor target (Mac or Windows) before uploading a test build.",
                        "OK");
                    return;
                }
                if (estimateOnly && string.IsNullOrEmpty(parentTestBuildId))
                {
                    EditorUtility.DisplayDialog("No patch base",
                        "Check Patch Size needs a patch base — pick one from the dropdown, or use Compile & Upload for a full upload.",
                        "OK");
                    return;
                }
                if (!SaveModifiedScenesBeforeCompile())
                {
                    EditorUtility.DisplayDialog("Cancelled", "Save all modified scenes before compiling a test build.", "OK");
                    return;
                }
                SaveLogoSelection();

                // Clear any prior estimate state before kicking off a new
                // run. Important when the user did one estimate, decided
                // not to upload, and is now running another estimate — we
                // don't want the resumed-upload path referencing
                // the old testBuildId.
                ClearPendingTestBuildState();

                isUploading = true;
                uploadStatusTitle = estimateOnly ? "Estimating patch size..." : "Compiling test build...";
                uploadStatusMessage = string.IsNullOrEmpty(parentTestBuildId)
                    ? $"Allocating Test Channel ID for \"{title}\""
                    : (estimateOnly
                        ? $"Allocating Test Channel ID (estimate vs {parentTestBuildId}) for \"{title}\""
                        : $"Allocating Test Channel ID (patch of {parentTestBuildId}) for \"{title}\"");
                uploadStatusProgress = 0.02f;
                uploadStatusIsError = false;
                uploadCompleted = false;
                uploadSucceeded = false;
                Repaint();

                // Step 1: allocate testBuildId first so the build can
                // bake the test-channel URL into the catalog. parentTestBuildId
                // is passed through so the backend marks this build as a
                // patch of the chosen parent at create time (the commit step
                // later uses that ref to authorize server-side copies).
                DreamPark.API.ContentAPI.CreateTestBuild(title, releaseNotes, contentId, parentTestBuildId, (createOk, testBuildId, createResp) =>
                {
                    if (!createOk || string.IsNullOrEmpty(testBuildId))
                    {
                        FinishTestBuildUpload(success: false, testBuildId: null, title: title,
                            errorMessage: createResp?.error ?? "Could not allocate test build ID");
                        return;
                    }

                    // Step 2: run the compile pipeline with the test URL
                    // baked in. Build any platforms the user picked; iOS
                    // and Android are intentionally never on for test
                    // builds. Returns true iff every requested target
                    // compiled cleanly.
                    bool buildOk = RunTestBuildCompile(testBuildId, doBuildOsx, doBuildWindows, (stepLabel, stepProgress) =>
                    {
                        uploadStatusTitle = "Compiling test build...";
                        uploadStatusMessage = stepLabel;
                        uploadStatusProgress = stepProgress;
                        Repaint();
                    });

                    if (!buildOk)
                    {
                        FinishTestBuildUpload(success: false, testBuildId: testBuildId, title: title,
                            errorMessage: "Test build compile failed — see Console for details. The allocated test build will auto-expire in 7 days.");
                        return;
                    }

                    // Step 3: build the manifest summary from what we
                    // just compiled (so per-platform size shows up in
                    // dreampark-core's Test Channel table) and upload.
                    JSONObject testManifestSummary = null;
                    try
                    {
                        // Match GetEnabledPlatformsForManifest — Unity is
                        // always included so the Content Manager's
                        // Supported Platforms table renders the
                        // unitypackage size alongside the bundle
                        // platforms. Without Unity here, that row shows
                        // "—" even though the .unitypackage is in the
                        // upload (because BuildUnityPackage writes to
                        // ServerData/Unity/ and the request harvester
                        // picks it up regardless of this list).
                        var platformsForManifest = new List<string>();
                        if (doBuildOsx) platformsForManifest.Add("StandaloneOSX");
                        if (doBuildWindows) platformsForManifest.Add("StandaloneWindows");
                        platformsForManifest.Add("Unity");
                        if (platformsForManifest.Count > 0)
                        {
                            var manifest = BuildManifestStore.BuildFromServerData(contentId, 1, platformsForManifest);
                            testManifestSummary = BuildManifestStore.BuildCommitSummary(manifest, diff: null);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[TestBuild] Could not build manifest summary: {ex.Message}");
                        testManifestSummary = null;
                    }

                    uploadStatusTitle = "Uploading test build...";
                    uploadStatusMessage = $"Pushing bundles to Test Channel ({title})";
                    uploadStatusProgress = 0.85f;
                    Repaint();

                    // Test builds no longer carry a logo Addressables key:
                    // the logo isn't bundled (July 2026), so core reads
                    // content.logoImageUrl from the backend instead. Null
                    // here means "no bundled logo", which is now always true.
                    string testLogoAddress = null;

                    if (estimateOnly)
                    {
                        // Estimate path — run the same diff the upload
                        // path would, but stop before pushing any bytes.
                        // Stash all the info we need so ResumePendingTestBuildUpload
                        // can finish the job later if the user clicks the
                        // primary upload button with unchanged settings.
                        pendingTestBuildId = testBuildId;
                        pendingTestBuildTitle = title;
                        pendingTestBuildNotes = releaseNotes;
                        pendingTestBuildContentId = contentId;
                        pendingTestBuildLogoAddress = testLogoAddress;
                        pendingTestBuildParentId = parentTestBuildId;
                        pendingTestBuildManifestSummary = testManifestSummary;
                        pendingTestBuildOsx = doBuildOsx;
                        pendingTestBuildWindows = doBuildWindows;

                        uploadStatusTitle = "Estimating patch size...";
                        uploadStatusMessage = $"Diffing local bundles against parent {parentTestBuildId}";
                        uploadStatusProgress = 0.92f;
                        Repaint();

                        DreamPark.API.ContentAPI.ComputePatchPlan(parentTestBuildId, (planOk, stats, planErr) =>
                        {
                            isUploading = false;
                            uploadStatusProgress = -1f;
                            if (!planOk || stats == null)
                            {
                                ClearPendingTestBuildState();
                                FinishTestBuildUpload(success: false, testBuildId: testBuildId, title: title,
                                    errorMessage: planErr ?? "Could not compute patch plan");
                                return;
                            }
                            uploadStatusTitle = "Patch estimate ready";
                            uploadStatusMessage =
                                $"{stats.newFiles} new bundle(s), {stats.inheritedFiles} inherited  ·  " +
                                $"Bundle patch size: {BuildManifestStore.FormatBytes(stats.patchSizeBytes)} out of {BuildManifestStore.FormatBytes(stats.totalSizeBytes)} total  ·  " +
                                $"click \"Compile & Upload\" with the same settings to commit without rebuilding.";
                            uploadStatusIsError = false;
                            uploadCompleted = false;
                            // Stay in the Upload Test Build dialog. The
                            // checked-patch resume path now lives on that
                            // dialog's main "Compile & Upload" button, so
                            // we should not bounce the user into the
                            // Try Reupload / launch popup.
                            Repaint();
                        });
                        return;
                    }

                    DreamPark.API.ContentAPI.UploadTestBuildArtifacts(testBuildId, title, releaseNotes, contentId, testLogoAddress, parentTestBuildId, testManifestSummary,
                        (uploadOk, uploadedTestBuildId, uploadResp) =>
                        {
                            FinishTestBuildUpload(uploadOk, uploadedTestBuildId, title,
                                uploadOk ? null : (uploadResp?.error ?? "Unknown error"));
                        });
                });
            });
        }

        // Discards the cached estimate state. Called when starting a fresh
        // estimate, when the resumed upload actually fires, or when the user
        // explicitly cancels.
        private void ClearPendingTestBuildState()
        {
            pendingTestBuildId = null;
            pendingTestBuildTitle = null;
            pendingTestBuildNotes = null;
            pendingTestBuildContentId = null;
            pendingTestBuildLogoAddress = null;
            pendingTestBuildParentId = null;
            pendingTestBuildManifestSummary = null;
            pendingTestBuildOsx = false;
            pendingTestBuildWindows = false;
        }

        private void ClearPendingProductionEstimateState()
        {
            pendingProductionEstimateOnly = false;
            pendingProductionContentId = null;
            pendingProductionMode = UploadMode.Patch;
            pendingProductionBuildOsx = false;
            pendingProductionBuildWindows = false;
            pendingProductionVersionNumber = 0;
            pendingProductionPatchingEnabled = false;
            pendingProductionCurrentManifest = null;
            pendingProductionSkipSet = null;
            pendingProductionManifestSummary = null;
        }

        // Exposed to ContentUploadFlowPopup — true iff there's a built-but-
        // unuploaded test build sitting in ServerData/, with the testBuildId
        // already allocated server-side, waiting on the user to commit.
        // The test-build UI uses this to decide whether a checked patch is
        // waiting and can be reused by the primary upload button.
        internal bool HasPendingTestBuildUpload => !string.IsNullOrEmpty(pendingTestBuildId);

        // True when the current Upload Test Build dialog state still matches
        // the already-built patch estimate sitting in ServerData/. In that
        // case the primary upload button can safely reuse the checked patch
        // instead of recompiling.
        internal bool PendingTestBuildMatches(
            string title,
            string releaseNotes,
            string contentName,
            bool doBuildOsx,
            bool doBuildWindows,
            string parentTestBuildId)
        {
            if (!HasPendingTestBuildUpload) return false;
            return string.Equals(pendingTestBuildTitle ?? "", title ?? "", StringComparison.Ordinal)
                && string.Equals(pendingTestBuildNotes ?? "", releaseNotes ?? "", StringComparison.Ordinal)
                && string.Equals(pendingTestBuildContentId ?? "", contentName ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(pendingTestBuildParentId ?? "", parentTestBuildId ?? "", StringComparison.Ordinal)
                && pendingTestBuildOsx == doBuildOsx
                && pendingTestBuildWindows == doBuildWindows;
        }

        // Cancels a pending estimate without uploading. The allocated
        // testBuildId on the backend auto-expires after 7 days, and the
        // bundles in ServerData/ stay on disk (no harm — they'll get
        // overwritten by the next compile). Also clears the patch stats
        // so the popup stops showing the estimate breakdown.
        internal void DiscardPendingTestBuild()
        {
            ClearPendingTestBuildState();
            uploadStatusTitle = "";
            uploadStatusMessage = "Estimate discarded. Run Compile & Upload or Check Patch Size again when ready.";
            uploadStatusProgress = -1f;
            uploadStatusIsError = false;
            uploadCompleted = false;
            uploadSucceeded = false;
            // Clearing CurrentPatchStats hides the patch breakdown in the
            // popup; otherwise the user sees stale "0 of N uploaded" numbers.
            DreamPark.API.ContentAPI.ClearCurrentPatchStats();
            Repaint();
        }

        // Resumes a paused estimate: takes the bundles that the estimate
        // step already wrote to ServerData/ (no recompile, that's the whole
        // point) and pushes them through the standard test-build upload
        // path using the testBuildId we allocated earlier. Triggered by the
        // primary upload button. The user's only acceptable click path here
        // is "I saw the estimate, I want to ship it as-is" —
        // any change to title / notes / platforms / patch base requires
        // re-running the estimate flow.
        internal void ResumePendingTestBuildUpload()
        {
            if (!HasPendingTestBuildUpload)
            {
                EditorUtility.DisplayDialog("Nothing to upload",
                    "No estimate is currently waiting. Run \"Check Patch Size\" first.",
                    "OK");
                return;
            }

            // This path bypasses BeginUploadFromPopup entirely — it goes straight to
            // ContentAPI.UploadTestBuildArtifacts — and it has two callers
            // (ContentUploadFlowPopup's Start button when an estimate is pending, and
            // TestBuildUploadDialog). Without this, running an estimate and then
            // pressing Start would skip every pre-upload check. The estimate compiled
            // the same assets, so there is nothing different to validate.
            if (!preUploadChecksCleared)
            {
                bool passed = PreUploadChecks.PreUploadChecksGate.Passes(
                    this, contentId,
                    onCleared: () =>
                    {
                        preUploadChecksCleared = true;
                        EditorApplication.delayCall += () =>
                        {
                            if (this == null) return;
                            try { ResumePendingTestBuildUpload(); }
                            finally { preUploadChecksCleared = false; }
                        };
                    },
                    // This path never calls SaveModifiedScenesBeforeCompile, so the
                    // scene-override check must not assume disk is current.
                    scenesAreSaved: false);
                if (!passed) return;
            }
            preUploadChecksCleared = false;

            // Snapshot the pending values into locals before we kick off
            // the upload — once isUploading is true the user might cancel
            // or another flow might overwrite pendingTestBuildId.
            string testBuildId = pendingTestBuildId;
            string title = pendingTestBuildTitle;
            string releaseNotes = pendingTestBuildNotes;
            string pendingContentId = pendingTestBuildContentId;
            string logoAddress = pendingTestBuildLogoAddress;
            string parentId = pendingTestBuildParentId;
            JSONObject manifestSummary = pendingTestBuildManifestSummary;
            ClearPendingTestBuildState();

            isUploading = true;
            uploadStatusTitle = "Uploading test build...";
            uploadStatusMessage = $"Pushing bundles to Test Channel ({title})";
            uploadStatusProgress = 0.85f;
            uploadStatusIsError = false;
            uploadCompleted = false;
            uploadSucceeded = false;
            Repaint();

            DreamPark.API.ContentAPI.UploadTestBuildArtifacts(
                testBuildId, title, releaseNotes, pendingContentId, logoAddress, parentId, manifestSummary,
                (uploadOk, uploadedTestBuildId, uploadResp) =>
                {
                    FinishTestBuildUpload(uploadOk, uploadedTestBuildId, title,
                        uploadOk ? null : (uploadResp?.error ?? "Unknown error"));
                });
        }

        internal void ResumePendingProductionUpload()
        {
            if (!HasPendingProductionEstimate)
            {
                EditorUtility.DisplayDialog("Nothing to upload",
                    "No production patch estimate is currently waiting. Run \"Check Patch Size\" first.",
                    "OK");
                return;
            }

            string estimateContentId = pendingProductionContentId;
            bool patchingEnabledForEstimate = pendingProductionPatchingEnabled;
            int versionNumber = pendingProductionVersionNumber;
            var currentManifest = pendingProductionCurrentManifest;
            var skipSet = pendingProductionSkipSet != null
                ? new HashSet<string>(pendingProductionSkipSet, StringComparer.Ordinal)
                : null;
            var manifestSummary = pendingProductionManifestSummary;

            ClearPendingProductionEstimateState();

            isUploading = true;
            uploadCompleted = false;
            uploadSucceeded = false;
            uploadStatusIsError = false;
            uploadStatusProgress = 1f;
            SetUploadStatus(
                "Uploading release",
                "Using the checked patch estimate and existing bundles in ServerData. No rebuild needed.",
                1f);

            StartPreparedProductionUpload(
                estimateContentId,
                releaseNotes,
                versionNumber,
                patchingEnabledForEstimate,
                currentManifest,
                skipSet,
                manifestSummary,
                preUploadedFiles: null);
        }

        // Common landing pad for both the create-failed and
        // upload-finished branches — keeps the panel's progress/spinner
        // state consistent regardless of which step bailed out, and
        // always clears the EditorUtility progress bar (which the
        // compile pipeline may have shown via reportStep).
        private void FinishTestBuildUpload(bool success, string testBuildId, string title, string errorMessage)
        {
            EditorUtility.ClearProgressBar();
            isUploading = false;
            uploadCompleted = true;
            uploadSucceeded = success;
            uploadStatusProgress = success ? 1f : -1f;
            uploadStatusIsError = !success;
            if (success)
            {
                uploadStatusTitle = "Test build uploaded";
                uploadStatusMessage = string.IsNullOrEmpty(testBuildId)
                    ? $"Pushed to Test Channel as \"{title}\". Auto-expires in 7 days."
                    : $"Pushed to Test Channel as \"{title}\" ({testBuildId}). Auto-expires in 7 days.";
            }
            else
            {
                uploadStatusTitle = "Test build failed";
                uploadStatusMessage = errorMessage ?? "Unknown error";
            }
            Repaint();
        }

        // Runs the compile half of an Upload Release, scoped to editor
        // targets only and with the catalog's RemoteLoadPath pointed at
        // /api/test-content/addressables/{testBuildId} so the bundles
        // baked into the catalog match the URLs dreampark-core's
        // BuildEditorBundleUrl helper rewrites to. Returns true iff every
        // selected target compiled.
        //
        // Deliberately a mirror of UploadContent's build steps (lines
        // ~2710-2828 in the production path) rather than calling into
        // that method directly:
        //   • UploadContent is welded to the full Upload Release flow
        //     (manifest diff → skipSet → commit metadata → schema sync
        //     etc.) and reusing it would require threading a "test build"
        //     flag through dozens of conditionals.
        //   • Test builds don't need iOS / Android, don't need the
        //     manifest diff or skipSet logic (every test push is a full
        //     compile), and don't run through the version-approval
        //     pipeline at all.
        // Keeping this as a focused mirror means production stays
        // untouched and test-channel behavior is easy to read in one
        // place.
        private bool RunTestBuildCompile(string testBuildId, bool doBuildOsx, bool doBuildWindows, Action<string, float> reportProgress)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string serverDataPath = Path.Combine(projectRoot, "ServerData");

            // RemoteLoadPath baked into the catalog. Must match the URL
            // dreampark-core/Assets/Editor/ContentManagerPanelWindow.cs's
            // BuildEditorBundleUrl produces for test-addressables/ paths
            // (see TestAddressablesStoragePrefix routing there) — when
            // they match, Unity's Caching layer keys bundles against
            // the same URL the editor download flow downloads them at,
            // so test build loads hit the cache instead of redownloading.
            string testTargetUrl = $"{DreamParkAPI.baseUrl}/api/test-content/addressables/{testBuildId}";

            int numPlatforms = (doBuildOsx ? 1 : 0) + (doBuildWindows ? 1 : 0);
            int currentStep = 0;
            int totalSteps = 7 + numPlatforms;
            Action<string> reportStep = (message) =>
            {
                currentStep++;
                float stageProgress = Mathf.Clamp01((float)currentStep / Mathf.Max(1, totalSteps));
                EditorUtility.DisplayProgressBar(
                    "Compile Test Build",
                    $"({currentStep}/{totalSteps}) {message}",
                    stageProgress);
                // Map the compile steps into the [0.05, 0.80] band of the
                // panel-wide progress bar; upload fills the remaining
                // [0.80, 1.00] band.
                reportProgress?.Invoke(message, 0.05f + (stageProgress * 0.75f));
            };

            try
            {
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
                // Pinned to Unity's default per the production comment
                // about IL2CPP / Quest crashes when this is false.
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
                    Debug.LogWarning($"[TestBuild] Third-party sync skipped: {syncEx.Message}");
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                reportStep("Updating addressable groups...");
                ContentProcessor.ForceUpdateContent(contentId);
                ContentProcessor.CleanupAddressableSettings();

                reportStep("Updating logo entry...");
                SyncLogoAddressableEntry();

                reportStep("Enforcing content namespaces...");
                ContentProcessor.EnforceContentNamespaces(contentId);

                reportStep("Building scripts package...");
                if (!ContentProcessor.BuildUnityPackage(contentId))
                {
                    Debug.LogError("[TestBuild] Unity package build failed");
                    return false;
                }

                if (doBuildOsx)
                {
                    reportStep("Building StandaloneOSX...");
                    if (!BuildForTarget(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone,
                        $"{testTargetUrl}/StandaloneOSX", contentId))
                    {
                        Debug.LogError("[TestBuild] OSX build failed");
                        return false;
                    }
                }
                if (doBuildWindows)
                {
                    reportStep("Building StandaloneWindows...");
                    if (!BuildForTarget(BuildTarget.StandaloneWindows, BuildTargetGroup.Standalone,
                        $"{testTargetUrl}/StandaloneWindows", contentId))
                    {
                        Debug.LogError("[TestBuild] Windows build failed");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TestBuild] Compile failed: {ex}");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
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

        private struct EstimateBuildTargetInfo
        {
            public BuildTarget target;
            public BuildTargetGroup group;
            public string platformName;
            public string label;
        }

        private bool TryGetSinglePlatformEstimateTarget(out EstimateBuildTargetInfo info)
        {
            bool Supports(BuildTarget target)
            {
                switch (target)
                {
                    case BuildTarget.Android: return buildAndroid;
                    case BuildTarget.iOS: return buildIos;
                    case BuildTarget.StandaloneOSX: return buildOsx;
                    case BuildTarget.StandaloneWindows:
                    case BuildTarget.StandaloneWindows64: return buildWindows;
                    default: return false;
                }
            }

            EstimateBuildTargetInfo Make(BuildTarget target)
            {
                switch (target)
                {
                    case BuildTarget.Android:
                        return new EstimateBuildTargetInfo
                        {
                            target = BuildTarget.Android,
                            group = BuildTargetGroup.Android,
                            platformName = "Android",
                            label = "Android"
                        };
                    case BuildTarget.iOS:
                        return new EstimateBuildTargetInfo
                        {
                            target = BuildTarget.iOS,
                            group = BuildTargetGroup.iOS,
                            platformName = "iOS",
                            label = "iOS"
                        };
                    case BuildTarget.StandaloneOSX:
                        return new EstimateBuildTargetInfo
                        {
                            target = BuildTarget.StandaloneOSX,
                            group = BuildTargetGroup.Standalone,
                            platformName = "StandaloneOSX",
                            label = "Editor (Mac)"
                        };
                    default:
                        return new EstimateBuildTargetInfo
                        {
                            target = BuildTarget.StandaloneWindows,
                            group = BuildTargetGroup.Standalone,
                            platformName = "StandaloneWindows",
                            label = "Editor (Windows)"
                        };
                }
            }

            var active = EditorUserBuildSettings.activeBuildTarget;
            if (Supports(active))
            {
                info = Make(active);
                return true;
            }

            BuildTarget[] fallbackOrder =
            {
                BuildTarget.StandaloneOSX,
                BuildTarget.StandaloneWindows,
                BuildTarget.Android,
                BuildTarget.iOS
            };

            foreach (var candidate in fallbackOrder)
            {
                if (Supports(candidate))
                {
                    info = Make(candidate);
                    return true;
                }
            }

            info = default;
            return false;
        }

        internal string ContentId => contentId;
        internal ContentUploadTarget ActiveUploadTarget => activeUploadTarget;
        internal string ActiveUploadContentId => ContentIdForTarget(activeUploadTarget);
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
        internal int? ActiveLatestPublishedVersionNumber => activeUploadTarget == ContentUploadTarget.Beta
            ? betaLatestPublishedVersionNumber
            : latestPublishedVersionNumber;
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
            int current = ActiveLatestPublishedVersionNumber ?? 0;
            int next = current + 1;
            return current <= 0 ? $"First release → v{next}" : $"v{current} → v{next}";
        }

        internal string GetUploadTargetSummary()
        {
            return activeUploadTarget == ContentUploadTarget.Beta
                ? $"Beta · {ActiveUploadContentId}"
                : $"Release · {contentId}";
        }

        private string ContentIdForTarget(ContentUploadTarget target)
        {
            return target == ContentUploadTarget.Beta ? BetaContentId.DeriveFrom(contentId) : contentId;
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
                return "No build artifacts yet. Upload Release will create the first bundle set.";
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
                if (System.DateTime.TryParse(patchBaseline.buildTimestampUtc, out var ts))
                {
                    string baselineWhen = ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    baselineLine = $"Backend parent: v{patchBaseline.versionNumber} at {baselineWhen}.";
                }
                else
                {
                    baselineLine = $"Backend parent: v{patchBaseline.versionNumber}.";
                }
            }

            long changed = patchDiff.TotalChangedBytes;
            long total = patchDiff.TotalCurrentBytes;
            int changedFiles = patchDiff.TotalChangedFileCount;
            if (patchBaseline == null)
            {
                return $"{baselineLine}\nPending upload: {changedFiles} files · {BuildManifestStore.FormatBytes(changed)}";
            }

            double reduction = total > 0 ? (1.0 - (double)changed / total) * 100.0 : 0.0;
            return $"{baselineLine}\nPending changes: {changedFiles} files · {BuildManifestStore.FormatBytes(changed)} of {BuildManifestStore.FormatBytes(total)} ({reduction:0.0}% smaller)";
        }

        internal bool HasCheckedProductionPatchEstimate => HasPendingProductionEstimate && patchDiff != null;

        internal int GetCurrentChangedFileCount()
        {
            if (patchDiff?.platforms == null) return 0;
            int count = 0;
            foreach (var platform in patchDiff.platforms)
                count += platform?.changedFiles?.Count ?? 0;
            return count;
        }

        internal int GetCurrentUnchangedFileCount()
        {
            if (patchDiff?.platforms == null) return 0;
            int count = 0;
            foreach (var platform in patchDiff.platforms)
                count += platform?.unchangedFiles?.Count ?? 0;
            return count;
        }

        internal List<string> GetChangedFileBreakdownLines()
        {
            return BuildPatchEstimateFileBreakdownLines(changed: true);
        }

        internal List<string> GetUnchangedFileBreakdownLines()
        {
            return BuildPatchEstimateFileBreakdownLines(changed: false);
        }

        private List<string> BuildPatchEstimateFileBreakdownLines(bool changed)
        {
            var lines = new List<string>();
            if (patchDiff?.platforms == null) return lines;

            var sizeLookup = new Dictionary<string, long>(StringComparer.Ordinal);
            if (patchCurrentSnapshot?.platforms != null)
            {
                foreach (var platform in patchCurrentSnapshot.platforms)
                {
                    if (platform?.files == null) continue;
                    foreach (var file in platform.files)
                    {
                        if (file == null) continue;
                        sizeLookup[$"{platform.platform}/{file.fileName}"] = file.sizeBytes;
                    }
                }
            }

            foreach (var platform in patchDiff.platforms)
            {
                if (platform == null) continue;
                var files = changed ? platform.changedFiles : platform.unchangedFiles;
                if (files == null) continue;

                foreach (var fileName in files)
                {
                    string key = $"{platform.platform}/{fileName}";
                    string sizeLabel = sizeLookup.TryGetValue(key, out long size)
                        ? BuildManifestStore.FormatBytes(size)
                        : "--";
                    lines.Add($"{platform.platform} / {fileName}  ·  {sizeLabel}");
                }
            }

            return lines;
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

        internal bool HasPendingProductionEstimate => pendingProductionCurrentManifest != null;

        internal bool CanCheckProductionPatchSize(UploadMode mode, bool build, bool failedOnly)
        {
            if (!build) return false;
            if (failedOnly) return false;
            if (BundlingStrategyPrefs.Current != BundlingStrategy.Smart) return false;
            if (mode == UploadMode.All) return false;

            // Keep this gate tied to the actual patch parent we can diff
            // against right now, not just the cached version number field.
            // latestPublishedVersionNumber can lag briefly behind a fresh
            // metadata fetch or a just-completed upload, while the backend
            // snapshot/local fallback already has everything we need to run a
            // real estimate.
            return GetPreferredPatchBaseline(ActiveContentDirectorySnapshot) != null;
        }

        internal bool PendingProductionEstimateMatches(UploadMode mode, bool doBuildOsx, bool doBuildWindows)
        {
            if (!HasPendingProductionEstimate) return false;
            return string.Equals(pendingProductionContentId ?? "", ActiveUploadContentId ?? "", StringComparison.OrdinalIgnoreCase)
                && pendingProductionMode == mode
                && pendingProductionBuildOsx == doBuildOsx
                && pendingProductionBuildWindows == doBuildWindows;
        }

        internal bool CleanBeforeEachTarget => cleanBeforeEachTarget;

        internal bool BeginUploadFromPopup(bool build, UploadMode mode)
        {
            return BeginUploadFromPopup(build, mode, failedOnly: false, ContentUploadTarget.Release);
        }

        // failedOnly = true comes from the "Upload Failed Bundles" choice on
        // the Try Reupload dialog. It reroutes the upload through
        // FailedBundleStore so only the bundles that failed last time get
        // re-sent. The supplied UploadMode is ignored in that case — Failed
        // Only is its own filter and stacks with neither Patch nor All.
        internal bool BeginUploadFromPopup(bool build, UploadMode mode, bool failedOnly)
        {
            return BeginUploadFromPopup(build, mode, failedOnly, ContentUploadTarget.Release);
        }

        internal bool BeginUploadFromPopup(
            bool build,
            UploadMode mode,
            bool failedOnly,
            ContentUploadTarget uploadTarget)
        {
            if (isUploading)
            {
                return false;
            }

            SetActiveUploadTarget(uploadTarget);
            string uploadContentId = ActiveUploadContentId;
            if (string.IsNullOrEmpty(uploadContentId)
                || (uploadTarget == ContentUploadTarget.Beta && !BetaContentId.IsPathSafe(uploadContentId)))
            {
                EditorUtility.DisplayDialog("Invalid upload target", "The selected upload target does not have a safe content ID.", "OK");
                return false;
            }

            // Lua surface gate. Content ships OVER THE AIR, but the XLua wrappers it
            // needs are AOT code compiled into the app — so a Unity type nobody
            // registered cannot be fixed by publishing content again, it needs an app
            // rebuild and a store release. The same call works fine here in the Editor,
            // because Mono reflects where IL2CPP cannot.
            //
            // LuaSurfaceScanner has caught exactly this since it was written and was
            // wired to nothing: not this upload, not the player build, not the docs. A
            // creator who never opened DreamPark > Troubleshooting shipped blind. This
            // is the trigger it was missing. Sandbox-denied types hard-stop; merely
            // unregistered ones warn and let the human decide.
            if (!LuaSurfaceGate.PassesPreUploadCheck(interactive: !automatedReleaseMode))
            {
                return false;
            }

            // Gate at the entry point so a stale popup (e.g. someone took the
            // Troubleshooting Legacy escape hatch while the popup was open)
            // can't sneak through with a strategy-incompatible mode. Failed-Only
            // overrides the mode entirely, so the Smart-requirement check
            // doesn't apply to it.
            if (!failedOnly
                && UploadModePrefs.RequiresSmart(mode)
                && BundlingStrategyPrefs.Current != BundlingStrategy.Smart)
            {
                if (!automatedReleaseMode)
                {
                    EditorUtility.DisplayDialog(
                        "Upload mode requires Smart bundling",
                        $"{UploadModePrefs.ShortLabel(mode)} requires the Smart bundling strategy, " +
                        "and this machine is on deprecated Legacy bundling. Turn Legacy off via " +
                        "DreamPark \u25b8 Troubleshooting \u25b8 Use Legacy Bundling (deprecated), or pick " +
                        "Upload All / Upload Patch.",
                        "OK");
                }
                return false;
            }

            // Scene save moved UP from below, so it runs before the pre-upload checks
            // rather than after. The scene-override check reads what is on disk; with
            // the save happening afterwards it would report stale YAML, or skip.
            //
            // Consequence to know about: a user who then hits the pre-upload gate and
            // cancels will have had their scenes saved anyway. That's the trade for
            // checking the right bytes.
            if (build && !SaveModifiedScenesBeforeCompile())
            {
                if (!automatedReleaseMode)
                    EditorUtility.DisplayDialog("Compile Cancelled", "Save all modified scenes before compiling.", "OK");
                return false;
            }

            if (build)
            {
                // Compile the package recipe only at the upload boundary.
                // Organizer edits never rebuild a prefab during IMGUI redraw.
                CompileDreamSequencePackage(contentId);
            }

            // Creator-facing pre-upload suite. Registration and display order live in
            // PreUploadCheckRunner so this call site cannot drift every time a check is
            // added or moved to release-only validation.
            //
            // This is INVISIBLE when nothing is wrong — no window, no dialog, no extra
            // click. Only an actual finding interrupts. A gate that fires on every
            // upload is a gate people learn to click through, and then the one that
            // mattered gets clicked through too.
            //
            // Asynchronous, because it opens a real window rather than a
            // DisplayDialog: EditorWindow.ShowUtility is non-modal, so it returns false
            // here and re-enters through the continuation if the user chooses to
            // proceed. Same shape as SDKUpdateChecker.EnsureUpToDateThen.
            if (!preUploadChecksCleared)
            {
                bool passed = PreUploadChecks.PreUploadChecksGate.Passes(
                    this, contentId,
                    onCleared: () => ResumeUploadAfterPreUploadChecks(build, mode, failedOnly, uploadTarget),
                    scenesAreSaved: build);
                if (!passed) return false;
            }
            preUploadChecksCleared = false;

            buildAndroid = true;
            buildIos = true;
            SaveBuildTargetSelection();

            SaveLogoSelection();
            pendingUploadMode = mode;
            pendingFailedOnly = failedOnly;
            pendingProductionEstimateOnly = false;
            ClearPendingProductionEstimateState();
            ResetUploadPresentationState(build);
            UploadContent(build);
            return true;
        }

        // One-shot token. Set only for the single re-entry that follows the user
        // pressing Continue in the Pre-Upload Checks window, and cleared as soon as
        // the gate is passed, so it can never leave the gate permanently open.
        private bool preUploadChecksCleared;

        private void ResumeUploadAfterPreUploadChecks(
            bool build,
            UploadMode mode,
            bool failedOnly,
            ContentUploadTarget uploadTarget)
        {
            preUploadChecksCleared = true;

            // delayCall gets us off the popup's OnGUI stack before re-entering the
            // upload flow — the established idiom in this file.
            EditorApplication.delayCall += () =>
            {
                // The panel can be closed while the checks window is open. Touching a
                // destroyed EditorWindow throws MissingReferenceException and would
                // start an upload with no UI attached to report it.
                if (this == null) return;

                try { BeginUploadFromPopup(build, mode, failedOnly, uploadTarget); }
                finally { preUploadChecksCleared = false; }
            };
        }

        internal bool BeginPatchEstimateFromPopup(UploadMode mode)
        {
            if (isUploading)
            {
                return false;
            }

            if (!CanCheckProductionPatchSize(mode, build: true, failedOnly: false))
            {
                EditorUtility.DisplayDialog(
                    "Check Patch Size unavailable",
                    "Check Patch Size is only available for Smart production patch modes after the first published release.",
                    "OK");
                return false;
            }

            if (!SaveModifiedScenesBeforeCompile())
            {
                EditorUtility.DisplayDialog("Compile Cancelled", "Save all modified scenes before compiling.", "OK");
                return false;
            }

            SaveLogoSelection();
            pendingUploadMode = mode;
            pendingFailedOnly = false;
            ClearPendingProductionEstimateState();
            pendingProductionEstimateOnly = true;
            ResetUploadPresentationState(build: true);
            uploadStatusTitle = "Estimating patch size...";
            uploadStatusMessage = "Compiling the current platform and diffing it against the latest backend version.";
            uploadStatusProgress = 0.02f;
            UploadContent(build: true);
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

        private static JSONObject GetContentPayload(JSONObject contentDirectory)
        {
            if (contentDirectory == null) return null;
            if (contentDirectory.HasField("content") && contentDirectory.GetField("content")?.type == JSONObject.Type.Object)
                return contentDirectory.GetField("content");
            return contentDirectory.type == JSONObject.Type.Object ? contentDirectory : null;
        }

        private static JSONObject GetLatestBackendVersionRecord(JSONObject contentDirectory)
        {
            var contentPayload = GetContentPayload(contentDirectory);
            var versions = contentPayload?.GetField("versions");
            if (versions == null || versions.type != JSONObject.Type.Array || versions.list == null || versions.list.Count == 0)
                return null;

            JSONObject latest = null;
            int bestVersion = int.MinValue;
            foreach (var versionNode in versions.list)
            {
                if (versionNode == null || versionNode.type != JSONObject.Type.Object) continue;
                int candidate = versionNode.HasField("versionNumber") ? versionNode.GetField("versionNumber").intValue : 0;
                if (latest == null || candidate >= bestVersion)
                {
                    latest = versionNode;
                    bestVersion = candidate;
                }
            }

            return latest;
        }

        private JSONObject ActiveContentDirectorySnapshot => activeUploadTarget == ContentUploadTarget.Beta
            ? betaContentDirectorySnapshot
            : latestContentDirectorySnapshot;

        // Cache for the patch baseline. CanCheckProductionPatchSize() calls this
        // from OnGUI (every repaint frame), and the fetch is now a blocking
        // network round-trip — so without caching the Editor freezes. Keyed by
        // content + published version; re-fetched only when either changes. A
        // failed/slow attempt is cached too (with a cooldown) so an unreachable
        // or slow endpoint can't re-trigger a blocking fetch every frame.
        private BuildManifest cachedPatchBaseline;
        private string cachedPatchBaselineKey;
        private double cachedPatchBaselineAttemptTime;
        private const double PatchBaselineRetryCooldownSeconds = 30.0;

        private BuildManifest GetPreferredPatchBaseline(JSONObject contentDirectory = null)
        {
            string activeContentId = ActiveUploadContentId;
            string key = $"{activeContentId ?? ""}@{(ActiveLatestPublishedVersionNumber?.ToString() ?? "?")}";
            bool keyMatches = string.Equals(key, cachedPatchBaselineKey, StringComparison.Ordinal);

            // Hit for this content+version → return cached, no network.
            if (keyMatches && cachedPatchBaseline != null)
                return cachedPatchBaseline;

            // Same key but the last attempt produced nothing (failed/empty):
            // only retry after the cooldown so OnGUI can't hammer the network.
            if (keyMatches && cachedPatchBaseline == null
                && (EditorApplication.timeSinceStartup - cachedPatchBaselineAttemptTime) < PatchBaselineRetryCooldownSeconds)
                return null;

            cachedPatchBaselineKey = key;
            cachedPatchBaselineAttemptTime = EditorApplication.timeSinceStartup;
            cachedPatchBaseline = FetchBackendBaselineForUpload(contentDirectory);
            return cachedPatchBaseline;
        }

        // Force the next GetPreferredPatchBaseline() call to re-fetch (e.g. after
        // an upload completes or the user explicitly refreshes).
        private void InvalidatePatchBaselineCache()
        {
            cachedPatchBaselineKey = null;
            cachedPatchBaseline = null;
        }

        private static string BuildBundleManifestUrl(string contentId, int versionNumber, string platform)
        {
            string escapedContentId = UnityWebRequest.EscapeURL(contentId ?? "");
            string escapedPlatform = UnityWebRequest.EscapeURL(platform ?? "");
            // Developer surface: /api/content (session + content-owner gated),
            // NOT /app/content (device, API-key gated). The uploader already
            // authenticates the creator's session against /api/content for
            // uploadUrl/commitUpload, so the baseline fetch belongs here too.
            return $"{DreamParkAPI.baseUrl.TrimEnd('/')}/api/content/{escapedContentId}/bundle-manifest/{versionNumber}/{escapedPlatform}";
        }

        // Hard cap so a slow or unreachable endpoint can never freeze the Editor.
        private const int BaselineRequestTimeoutSeconds = 10;

        private static JSONObject GetJsonSync(string url, string authorizationHeader, out string error)
        {
            error = null;
            using (var req = UnityWebRequest.Get(url))
            {
                if (!string.IsNullOrWhiteSpace(authorizationHeader))
                    req.SetRequestHeader("Authorization", authorizationHeader);

                req.timeout = BaselineRequestTimeoutSeconds;

                var op = req.SendWebRequest();
                // Backstop wall-clock deadline: the busy-wait must never spin
                // forever on the main thread, and Sleep keeps it off the CPU.
                double deadline = EditorApplication.timeSinceStartup + BaselineRequestTimeoutSeconds + 2;
                while (!op.isDone
                    && req.result != UnityWebRequest.Result.ConnectionError
                    && EditorApplication.timeSinceStartup < deadline)
                {
                    System.Threading.Thread.Sleep(10);
                }

                if (!op.isDone)
                {
                    error = "Baseline request timed out";
                    return null;
                }

                bool success = !(req.result == UnityWebRequest.Result.ConnectionError
                    || req.result == UnityWebRequest.Result.ProtocolError);
                if (!success)
                {
                    error = !string.IsNullOrWhiteSpace(req.downloadHandler?.text) ? req.downloadHandler.text : req.error;
                    return null;
                }

                string raw = req.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    error = "Empty response body";
                    return null;
                }

                try
                {
                    return new JSONObject(raw);
                }
                catch (Exception ex)
                {
                    error = $"Malformed JSON: {ex.Message}";
                    return null;
                }
            }
        }

        private static long ExtractManifestPlatformBytes(JSONObject versionJson, string platform)
        {
            return versionJson?.GetField("manifest")
                ?.GetField("fullContent")
                ?.GetField("byPlatform")
                ?.GetField(platform)
                ?.GetField("bytes")
                ?.longValue ?? -1L;
        }

        private BuildManifest FetchBackendBaselineForUpload(JSONObject contentDirectory = null)
        {
            string targetContentId = ActiveUploadContentId;
            if (string.IsNullOrEmpty(targetContentId))
                return BuildManifestStore.LoadBaseline(targetContentId);

            var latestVersion = GetLatestBackendVersionRecord(contentDirectory ?? ActiveContentDirectorySnapshot);
            if (latestVersion == null)
                return BuildManifestStore.LoadBaseline(targetContentId);

            int versionNumber = latestVersion.HasField("versionNumber") ? latestVersion.GetField("versionNumber").intValue : 0;
            if (versionNumber <= 0)
                return BuildManifestStore.LoadBaseline(targetContentId);

            var platformTargets = latestVersion.GetField("platformTargets")?.list?
                .Select(node => node?.stringValue)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (platformTargets.Count == 0)
                return BuildManifestStore.LoadBaseline(targetContentId);

            // Use the session bearer, NOT GetAPIKey(): GetAPIKey() is core-only
            // (#if DREAMPARKCORE) and returns "" in SDK/creator projects, which
            // silently disabled the backend (md5-vs-server) baseline and fell back
            // to the local manifest for every creator. GetUserAuth() is the
            // SDK-correct method (session token from EditorPrefs) and also works in
            // core. If the backend rejects it, the GetJsonSync failure path below
            // still falls back to the local baseline — so this can't regress.
            string authHeader = AuthAPI.GetUserAuth();
            if (string.IsNullOrWhiteSpace(authHeader))
            {
                Debug.LogWarning("[ContentUploader] No session available for backend baseline fetch (log in via the uploader panel); falling back to local baseline.");
                return BuildManifestStore.LoadBaseline(targetContentId);
            }

            var manifest = new BuildManifest
            {
                contentId = targetContentId,
                versionNumber = versionNumber,
                buildTimestampUtc =
                    latestVersion.GetField("createdAt")?.stringValue
                    ?? latestVersion.GetField("uploadedAt")?.stringValue
                    ?? latestVersion.GetField("updatedAt")?.stringValue
                    ?? string.Empty,
                sdkVersion = latestVersion.GetField("sdkVersion")?.stringValue ?? string.Empty,
            };

            foreach (string platform in platformTargets)
            {
                if (string.Equals(platform, "Unity", StringComparison.OrdinalIgnoreCase))
                {
                    var unityPlatform = new BuildManifestPlatform { platform = "Unity" };
                    long unityBytes = ExtractManifestPlatformBytes(latestVersion, "Unity");
                    unityPlatform.files.Add(new BuildManifestFile
                    {
                        fileName = $"{targetContentId}.unitypackage",
                        sizeBytes = unityBytes,
                    });
                    manifest.platforms.Add(unityPlatform);
                    continue;
                }

                string url = BuildBundleManifestUrl(targetContentId, versionNumber, platform);
                JSONObject responseJson = GetJsonSync(url, authHeader, out string error);
                if (responseJson == null || responseJson.GetField("success")?.boolValue != true)
                {
                    string responseError = responseJson?.GetField("error")?.stringValue ?? "Unknown error";
                    string baselineError = error ?? responseError;
                    Debug.LogWarning($"[ContentUploader] Could not fetch backend baseline for {platform}: {baselineError}. Falling back to local baseline.");
                    return BuildManifestStore.LoadBaseline(targetContentId);
                }

                var platformManifest = new BuildManifestPlatform { platform = platform };
                var bundles = responseJson.GetField("bundles");
                if (bundles?.type == JSONObject.Type.Array && bundles.list != null)
                {
                    foreach (var fileNode in bundles.list)
                    {
                        if (fileNode == null || fileNode.type != JSONObject.Type.Object) continue;
                        string fileName = fileNode.GetField("name")?.stringValue;
                        if (string.IsNullOrWhiteSpace(fileName)) continue;

                        // The bundle-manifest endpoint returns each bundle's GCS md5
                        // (hex) in "hash" — this is the server's authoritative bytes,
                        // so carrying it into the baseline makes Diff a true
                        // md5-vs-server comparison (catches same-name/different-bytes).
                        string serverMd5 = fileNode.GetField("hash")?.stringValue;

                        platformManifest.files.Add(new BuildManifestFile
                        {
                            fileName = fileName,
                            sizeBytes = fileNode.GetField("size")?.longValue ?? -1L,
                            md5 = string.IsNullOrEmpty(serverMd5) ? null : serverMd5.ToLowerInvariant(),
                        });
                    }
                }

                platformManifest.files.Sort((a, b) => string.CompareOrdinal(a.fileName, b.fileName));
                manifest.platforms.Add(platformManifest);
            }

            return manifest.platforms.Count > 0 ? manifest : BuildManifestStore.LoadBaseline(targetContentId);
        }

        private void CompleteUploadStatus(bool success, string message)
        {
            uploadCompleted = true;
            uploadSucceeded = success;
            uploadStatusIsError = !success;
            uploadStatusProgress = success ? 1f : uploadStatusProgress;
            string targetLabel = activeUploadTarget == ContentUploadTarget.Beta ? "Beta upload" : "Release";
            uploadStatusTitle = success ? $"{targetLabel} complete" : $"{targetLabel} interrupted";
            uploadStatusMessage = message ?? (success ? "Upload complete." : "Upload failed.");
            // A successful upload publishes a new version → the cached baseline is
            // now stale. Drop it so the next estimate re-fetches against the new
            // server state.
            if (success) InvalidatePatchBaselineCache();
            Repaint();
        }

        private void StartPreparedProductionUpload(
            string uploadContentId,
            string uploadReleaseNotes,
            int versionNumber,
            bool patchingEnabled,
            BuildManifest currentManifest,
            HashSet<string> skipSet,
            JSONObject manifestSummary,
            List<DreamPark.API.UploadedFileRecord> preUploadedFiles)
        {
            SetUploadStatus(
                activeUploadTarget == ContentUploadTarget.Beta ? "Uploading beta" : "Uploading release",
                $"Sending changed files to the isolated '{uploadContentId}' target. Live file progress will appear below.",
                1f);

            ContentAPI.UploadContent(uploadContentId, uploadReleaseNotes, lastSchemaVersion, skipSet, manifestSummary, preUploadedFiles, (success, apiResponse) =>
            {
                if (success)
                {
                    Debug.Log("✅ Content uploaded successfully");

                    if (patchingEnabled && currentManifest != null)
                    {
                        try
                        {
                            BuildManifestStore.SaveBaseline(currentManifest);
                            patchBaseline = currentManifest;
                            patchDiff = BuildManifestStore.Diff(patchBaseline, patchCurrentSnapshot);
                            Repaint();
                        }
                        catch (Exception saveEx)
                        {
                            Debug.LogWarning($"[ContentUploader] Failed to save baseline: {saveEx.Message}");
                        }
                    }

                    try
                    {
                        DirtyGroupsStore.Clear(uploadContentId);
                        dirtyGroupsEstimate = null;
                    }
                    catch (Exception dgEx)
                    {
                        Debug.LogWarning($"[ContentUploader] Failed to clear dirty groups: {dgEx.Message}");
                    }

                    if (activeUploadTarget == ContentUploadTarget.Beta)
                        betaLatestPublishedVersionNumber = versionNumber;
                    else
                        latestPublishedVersionNumber = versionNumber;

                    // Push attraction preview PNGs to the backend catalog —
                    // identical to Troubleshooting → "Force Upload All
                    // Previews" but silent. Must run AFTER the commit: the
                    // preview endpoint is attach-only, and the attraction
                    // rows it attaches to are created by the server's
                    // commit-time catalog sync. Fire-and-forget; failures
                    // log warnings and never affect the upload result.
                    try
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            ForceUploadAllPreviewsRoutine(uploadContentId, interactive: false, sourceContentId: contentId));
                    }
                    catch (Exception pvEx)
                    {
                        Debug.LogWarning("[Previews] auto-push failed to start: " + pvEx.Message);
                    }

                    // Push authored attraction dimensions (feet) the same way —
                    // silent, attach-only, after the commit-time catalog sync
                    // has created the rows. Every upload keeps dimensions in
                    // lockstep with the build; the backend derives each
                    // attraction's size-reference tag from them.
                    try
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            UploadAttractionDimensionsRoutine(uploadContentId, interactive: false, rebakeBeforeUpload: false, sourceContentId: contentId));
                    }
                    catch (Exception dimEx)
                    {
                        Debug.LogWarning("[Dimensions] auto-push failed to start: " + dimEx.Message);
                    }

                    string targetName = activeUploadTarget == ContentUploadTarget.Beta ? "beta" : "release";
                    CompleteUploadStatus(true,
                        $"'{contentName}' uploaded successfully to {targetName} target '{uploadContentId}' as {GetVersionSummaryAfterUpload(versionNumber)}.");

                    // Bridge into the second half of the content creation
                    // flow: the freshly-committed version just (re)synced this
                    // content's attractions catalog server-side, so pop the
                    // Developer Portal's Attractions page where every uploaded
                    // attraction sits ready to be titled/described/priced.
                    // Only fires on a real successful commit — never for the
                    // zero-change short-circuit or Test Channel uploads.
                    if (!automatedReleaseMode) try
                    {
                        Application.OpenURL(DeveloperPortalMenuItem.AttractionsUrl(uploadContentId));
                    }
                    catch (Exception portalEx)
                    {
                        Debug.LogWarning("[ContentUploader] Could not open Developer Portal: " + portalEx.Message);
                    }
                }
                else
                {
                    Debug.LogError($"❌ Content uploaded failed: {apiResponse.error}");
                    CompleteUploadStatus(false, $"Upload failed: {apiResponse.error}");
                    if (!automatedReleaseMode)
                        EditorUtility.DisplayDialog("Error", $"Upload failed: {apiResponse.error}", "OK");
                }

                pendingFailedOnly = false;
                isUploading = false;
            });
        }

        // ── Bundling strategy ────────────────────────────────────────────
        // There is no picker here any more. Smart (dependency-aware) bundling
        // is the default and the only strategy the shipping path expects; see
        // BundlingStrategy.cs for why Legacy is deprecated and how the
        // one-time migration moves existing machines across.
        //
        // All that survives is a notice for the rare machine still on Legacy
        // — someone who took the Troubleshooting escape hatch, or whose
        // migration hasn't run yet. Without it, Legacy is invisible from the
        // panel while quietly forcing every upload to ship everything, which
        // is exactly the confusion this change exists to end.
        private void DrawLegacyBundlingNotice()
        {
            if (BundlingStrategyPrefs.Current != BundlingStrategy.Legacy) return;

            EditorGUILayout.HelpBox(
                "Legacy bundling is active (deprecated). Every upload from this machine is a full " +
                "re-upload — the Upload Scope picker won't appear, and Patch / Code-only uploads are " +
                "unavailable. Turn it off via DreamPark \u25b8 Troubleshooting \u25b8 Use Legacy " +
                "Bundling (deprecated).",
                MessageType.Warning);
            GUILayout.Space(6);
        }

        // Re-walks ServerData/ for the currently-enabled platforms and rebuilds
        // the cached diff against the latest backend version when available
        // (falling back to the local cached baseline). Cheap once metadata is
        // loaded, so we can call it freely on lifecycle events.
        private void RefreshPatchEstimate()
        {
            string targetContentId = ActiveUploadContentId;
            patchEstimateContentId = targetContentId;
            patchEstimateComputedAt = System.DateTime.UtcNow;

            if (string.IsNullOrEmpty(targetContentId))
            {
                patchBaseline = null;
                patchCurrentSnapshot = null;
                patchDiff = null;
                return;
            }

            try
            {
                var platforms = GetEnabledPlatformsForManifest();
                patchCurrentSnapshot = BuildManifestStore.BuildFromServerData(targetContentId, /*versionNumber*/ 0, platforms);
                if (IsPatchUploadEnabled())
                {
                    patchBaseline = GetPreferredPatchBaseline(ActiveContentDirectorySnapshot);
                    patchDiff = BuildManifestStore.Diff(patchBaseline, patchCurrentSnapshot);

                    // Source-aware estimate: read dirty-groups (maintained
                    // real-time by the watchdog) and match them against the
                    // baseline's bundle filenames for a quick "patch size" guess
                    // that reflects current source state, not the (possibly
                    // stale) ServerData/.
                    var dirtyGroups = DirtyGroupsStore.Load(targetContentId);
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

        private JSONObject BuildUploaderMetadata(UploadMode effectiveMode)
        {
            var bundlingStrategy = BundlingStrategyPrefs.Current;

            // patching reflects what THIS upload actually did — anything other
            // than Upload All shipped a partial payload (Patch, CodeOnly).
            // Previously we derived this from bundling strategy
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
            uploaderMetadata.AddField(
                "releaseTarget",
                activeUploadTarget == ContentUploadTarget.Beta ? "beta" : "main");
            uploaderMetadata.AddField("sourceContentId", contentId ?? "");
            uploaderMetadata.AddField("uploadContentId", ActiveUploadContentId ?? "");
            uploaderMetadata.AddField("sequenceLayout", BuildSequenceLayoutJson());
            // Web's packages field is a versioned release contract. The richer
            // GUID-backed organizer snapshot remains available separately.
            uploaderMetadata.AddField("packageLayout", BuildPackageLayoutJson());
            JSONObject publishedPackages = BuildPublishedPackagesJson();
            if (publishedPackages != null) uploaderMetadata.AddField("packages", publishedPackages);
            uploaderMetadata.AddField("parkAssets", BuildParkAssetsMetadataJson());
            return uploaderMetadata;
        }

        private void AttachUploaderAndPackages(JSONObject manifestSummary, UploadMode effectiveMode)
        {
            JSONObject uploader = BuildUploaderMetadata(effectiveMode);
            manifestSummary.AddField("uploader", uploader);
            // ContentAPI forwards manifestSummary.packages to Web's
            // commitUpload; nesting this only under `uploader` is insufficient.
            JSONObject packages = uploader.GetField("packages");
            if (packages != null) manifestSummary.AddField("packages", packages);
        }

        private JSONObject BuildSequenceLayoutJson()
        {
            ContentSequenceStore.Data layout = LoadPackageLayoutForMetadata(false);
            return BuildLayoutMetadataJson(layout, "unsorted");
        }

        private JSONObject BuildPackageLayoutJson()
        {
            var result = new JSONObject(JSONObject.Type.Object);
            result.AddField("arena", BuildArenaLayoutMetadataJson());
            JSONObject adventure = BuildLayoutMetadataJson(LoadPackageLayoutForMetadata(false), "unused");
            adventure.AddField("containerPath", DreamSequenceGenerator.ContainerPrefabPath(contentId));
            adventure.AddField("gameManagerPath", DreamSequenceGenerator.ContainerPrefabPath(contentId));
            result.AddField("adventure", adventure);
            JSONObject sequence = BuildLayoutMetadataJson(LoadPackageLayoutForMetadata(true), "unused");
            sequence.AddField("definitionAddress", DreamSequencePackageDefinition.AddressFor(contentId));
            sequence.AddField("containerPath", DreamSequenceGenerator.ContainerPrefabPath(contentId));
            sequence.AddField("gameManagerPath", DreamSequenceGenerator.ContainerPrefabPath(contentId));
            sequence.AddField("startLevelPath", DreamSequenceGenerator.StartLevelPath(contentId));
            sequence.AddField("overlayLevelPath", DreamSequenceGenerator.OverlayLevelPath(contentId));
            sequence.AddField("gameOverLevelPath", DreamSequenceGenerator.GameOverLevelPath(contentId));
            sequence.AddField("widthFeet", DreamSequenceTemplate.StandardWidthFeet);
            sequence.AddField("lengthFeet", DreamSequenceTemplate.StandardLengthFeet);
            result.AddField("sequence", sequence);
            return result;
        }

        private JSONObject BuildArenaLayoutMetadataJson()
        {
            ArenaPackageStore.Data layout = arenaLayout
                ?? ArenaPackageStore.LoadAndReconcile(contentId, ArenaCandidates());
            var result = new JSONObject(JSONObject.Type.Object);
            result.AddField("schemaVersion", ArenaPackageStore.CurrentVersion);
            var buckets = new JSONObject(JSONObject.Type.Array);
            foreach (ArenaPackageStore.Bucket source in layout.buckets
                ?? new List<ArenaPackageStore.Bucket>())
            {
                if (source == null || source.guids == null || source.guids.Count == 0) continue;
                var bucket = new JSONObject(JSONObject.Type.Object);
                bucket.AddField("widthFeet", source.widthFeet);
                bucket.AddField("lengthFeet", source.lengthFeet);
                var items = new JSONObject(JSONObject.Type.Array);
                foreach (string guid in source.guids) items.Add(SequenceAttractionJson(guid, false, null));
                bucket.AddField("items", items);
                buckets.Add(bucket);
            }
            result.AddField("buckets", buckets);
            var excluded = new JSONObject(JSONObject.Type.Array);
            foreach (string guid in layout.excludedGuids ?? new List<string>())
                excluded.Add(SequenceAttractionJson(guid, false, null));
            result.AddField("excluded", excluded);
            return result;
        }

        private JSONObject BuildPublishedPackagesJson()
        {
            DreamParkPackageManifest manifest = AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(
                DreamParkPackageCompiler.ManifestPath(contentId));
            if (manifest == null || (manifest.arena == null
                && manifest.adventure == null && manifest.sequence == null))
                return null;
            var packages = new JSONObject(JSONObject.Type.Object);
            packages.AddField("schemaVersion", PublishedPackageSchemaVersion);
            if (manifest.arena != null)
                packages.AddField("arena", BuildPublishedPackageJson(manifest.arena, contentId));
            if (manifest.adventure != null)
                packages.AddField("adventure", BuildPublishedPackageJson(manifest.adventure, contentId));
            if (manifest.sequence != null)
                packages.AddField("sequence", BuildPublishedPackageJson(manifest.sequence, contentId));
            return packages;
        }

        internal static JSONObject BuildPublishedPackageJson(PackageRecipe recipe, string sourceContentId)
        {
            // Web catalogs use the prefab's asset-path stem as resourceName
            // when that path exists in the catalog. An Addressable loading
            // address is a different key and fails Web's release validation.
            // Keep the runtime address in the manifest, and resolve the Web
            // key against the exact authored prefab here.
            var catalogNames = new Dictionary<string, string>(StringComparer.Ordinal);
            string root = $"Assets/Content/{sourceContentId}";
            string targetId = recipe.occurrences.FirstOrDefault(o => o != null)?.resourceAddress?.Split('/')[0];
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                string leaf = Path.GetFileNameWithoutExtension(path);
                LevelTemplate level = prefab.GetComponent<LevelTemplate>();
                PropTemplate prop = prefab.GetComponent<PropTemplate>();
                string address = level != null
                    ? $"{targetId}/Levels/{level.size}/{leaf}"
                    : prop != null ? $"{targetId}/Props/{prop.category}/{leaf}" : null;
                if (address == null) continue;
                string catalogName = ResourceNameForAssetPath(path);
                if (catalogNames.TryGetValue(address, out string existing)
                    && !string.Equals(existing, catalogName, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Package assets '{existing}' and '{catalogName}' share Addressable address '{address}'.");
                catalogNames[address] = catalogName;
            }
            if (string.Equals(recipe.kind, "arena", StringComparison.OrdinalIgnoreCase))
            {
                var arenaResult = new JSONObject(JSONObject.Type.Object);
                var buckets = new JSONObject(JSONObject.Type.Array);
                var byId = (recipe.occurrences ?? new List<PackageOccurrence>())
                    .Where(occurrence => occurrence != null && !string.IsNullOrEmpty(occurrence.occurrenceId))
                    .ToDictionary(occurrence => occurrence.occurrenceId,
                        occurrence => occurrence, StringComparer.Ordinal);
                foreach (ArenaSizeBucket source in recipe.arenaBuckets ?? new List<ArenaSizeBucket>())
                {
                    if (source == null || source.occurrenceIds == null
                        || source.occurrenceIds.Count == 0) continue;
                    var bucket = new JSONObject(JSONObject.Type.Object);
                    bucket.AddField("widthFeet", source.widthFeet);
                    bucket.AddField("lengthFeet", source.lengthFeet);
                    var candidates = new JSONObject(JSONObject.Type.Array);
                    foreach (string id in source.occurrenceIds)
                    {
                        if (!byId.TryGetValue(id, out PackageOccurrence occurrence))
                            throw new InvalidOperationException("Arena bucket references a missing occurrence: " + id);
                        if (!catalogNames.TryGetValue(occurrence.resourceAddress, out string catalogName))
                            throw new InvalidOperationException("Arena occurrence has no catalog prefab for '"
                                + occurrence.resourceAddress + "'.");
                        var item = new JSONObject(JSONObject.Type.Object);
                        item.AddField("resourceName", catalogName);
                        item.AddField("role", occurrence.role);
                        item.AddField("required", occurrence.required);
                        item.AddField("widthFeet", occurrence.widthFeet);
                        item.AddField("lengthFeet", occurrence.lengthFeet);
                        candidates.Add(item);
                    }
                    bucket.AddField("attractions", candidates);
                    buckets.Add(bucket);
                }
                arenaResult.AddField("buckets", buckets);
                return arenaResult;
            }
            var result = new JSONObject(JSONObject.Type.Object);
            var groups = new JSONObject(JSONObject.Type.Array);
            var groupIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PackageGroupOccurrence occurrence in recipe.groups ?? new List<PackageGroupOccurrence>())
            {
                if (occurrence == null) continue;
                groupIndices[occurrence.occurrenceId] = groupIndices.Count;
                var group = new JSONObject(JSONObject.Type.Object);
                group.AddField("name", occurrence.name);
                groups.Add(group);
            }
            var attractions = new JSONObject(JSONObject.Type.Array);
            foreach (PackageOccurrence occurrence in recipe.occurrences ?? new List<PackageOccurrence>())
            {
                if (occurrence == null) continue;
                var item = new JSONObject(JSONObject.Type.Object);
                if (!catalogNames.TryGetValue(occurrence.resourceAddress, out string catalogName))
                    throw new InvalidOperationException("Package occurrence has no catalog prefab for '"
                        + occurrence.resourceAddress + "'.");
                item.AddField("resourceName", catalogName);
                if (!string.IsNullOrEmpty(occurrence.groupOccurrenceId))
                {
                    if (!groupIndices.TryGetValue(occurrence.groupOccurrenceId, out int index))
                        throw new InvalidOperationException("Package occurrence references a missing Group.");
                    item.AddField("groupIndex", index);
                }
                item.AddField("role", occurrence.role);
                item.AddField("required", occurrence.required);
                if (occurrence.widthFeet > 0) item.AddField("widthFt", occurrence.widthFeet);
                if (occurrence.lengthFeet > 0) item.AddField("lengthFt", occurrence.lengthFeet);
                attractions.Add(item);
            }
            result.AddField("groups", groups);
            result.AddField("attractions", attractions);
            return result;
        }

        private JSONObject BuildParkAssetsMetadataJson()
        {
            ContentSequenceStore.Data layout = libraryLayout ?? ContentSequenceStore.LoadLibraryAndReconcile(
                contentId, ParkAssetEntries().Select(e => e.guid));
            return BuildLayoutMetadataJson(layout, "hidden");
        }

        private ContentSequenceStore.Data LoadPackageLayoutForMetadata(bool sequenceMode)
        {
            if (sequenceMode == (organizerMode == OrganizerMode.Sequence) && sequenceLayout != null)
                return sequenceLayout;
            // Reconciliation must retain every asset already carried by a Group.
            // Sequence compatibility restricts new individual drops and upload,
            // but must not silently erase Group membership from saved metadata.
            List<ContentRootEntry> entries = ParkAssetEntries();
            return ContentSequenceStore.LoadAndReconcile(contentId, entries.Select(e => e.guid),
                entries.Where(e => e.kind == ContentRootKind.Attraction).Select(e => e.guid), sequenceMode);
        }

        private JSONObject BuildLayoutMetadataJson(ContentSequenceStore.Data layout, string remainderField)
        {
            var result = new JSONObject(JSONObject.Type.Object);
            result.AddField("schemaVersion", ContentSequenceStore.CurrentVersion);
            result.AddField("startPoint", SequenceAttractionJson(layout?.startGuid, false, layout));
            result.AddField("endPoint", SequenceAttractionJson(layout?.endGuid, false, layout));
            var items = new JSONObject(JSONObject.Type.Array);
            foreach (var item in layout?.items ?? new List<ContentSequenceStore.Entry>())
            {
                if (item == null) continue;
                var node = new JSONObject(JSONObject.Type.Object);
                node.AddField("kind", item.IsWorld ? "group" : SequenceContentKind(item.attractionGuid));
                if (item.IsWorld)
                {
                    node.AddField("id", item.id ?? "");
                    node.AddField("sourceGroupId", item.sourceGroupId ?? "");
                    node.AddField("name", item.name ?? "");
                    node.AddField("hidden", item.hidden);
                    var children = new JSONObject(JSONObject.Type.Array);
                    var legacyAttractions = new JSONObject(JSONObject.Type.Array);
                    for (int childIndex = 0; childIndex < (item.attractionGuids ?? new List<string>()).Count; childIndex++)
                    {
                        string guid = item.attractionGuids[childIndex];
                        string placementId = item.attractionIds != null && childIndex < item.attractionIds.Count
                            ? item.attractionIds[childIndex] : "";
                        children.Add(SequenceAttractionJson(guid, false, layout, placementId));
                        legacyAttractions.Add(SequenceAttractionJson(guid, false, layout));
                    }
                    node.AddField("items", children);
                    node.AddField("attractions", legacyAttractions);
                }
                else
                {
                    node.AddField("id", item.id ?? "");
                    node.AddField("attraction", SequenceAttractionJson(
                        item.attractionGuid, item.hidden, layout, item.id));
                }
                items.Add(node);
            }
            result.AddField("items", items);
            var unsorted = new JSONObject(JSONObject.Type.Array);
            foreach (string guid in layout?.unsortedGuids ?? new List<string>())
                unsorted.Add(SequenceAttractionJson(guid,
                    (layout.hiddenGuids ?? new List<string>()).Contains(guid), layout));
            result.AddField(remainderField, unsorted);
            return result;
        }

        private JSONObject SequenceAttractionJson(string guid, bool hidden,
            ContentSequenceStore.Data layout, string placementId = null)
        {
            var node = new JSONObject(JSONObject.Type.Object);
            ContentRootEntry entry = contentRoots.FirstOrDefault(e => e.guid == guid);
            node.AddField("kind", SequenceContentKind(guid));
            node.AddField("guid", guid ?? "");
            if (!string.IsNullOrEmpty(placementId)) node.AddField("placementId", placementId);
            node.AddField("name", entry?.name ?? "");
            node.AddField("assetPath", entry?.assetPath ?? AssetDatabase.GUIDToAssetPath(guid));
            node.AddField("hidden", hidden);
            node.AddField("required", entry != null && (entry.requiredForGame
                || guid == layout?.startGuid || guid == layout?.endGuid));
            node.AddField("sequenceCompatible", entry != null && entry.sequenceCompatible);
            return node;
        }

        private string SequenceContentKind(string guid)
        {
            ContentRootEntry entry = contentRoots.FirstOrDefault(e => e.guid == guid);
            return entry != null && entry.kind == ContentRootKind.Prop ? "prop" : "attraction";
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
                if (DreamSequenceGenerator.IsSpecialLevelPath(contentId, path)) continue;
                if (string.Equals(path, DreamSequenceGenerator.SequencePrefabPath(contentId),
                    StringComparison.OrdinalIgnoreCase)) continue; // retired generated artifact

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
                    guid = guid,
                    assetPath = path,
                    name = name,
                    kind = kind.Value,
                    customPreview = preview,
                    cachedAsset = prefab,
                    subLabel = subLabel,
                    requiredForGame = prefab.GetComponent<AttractionTemplate>()?.gameRequiresAttraction ?? false,
                    sequenceCompatible = DreamSequenceCompatibility.IsCompatible(prefab.GetComponent<AttractionTemplate>()),
                    isDreamSequence = prefab.GetComponent<DreamSequenceTemplate>() != null,
                });
            }

            // Stable ordering: kind first (Attraction, Prop, Player), then name.
            contentRoots = contentRoots
                .OrderBy(e => (int)e.kind)
                .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<ContentRootEntry> organizerEntries = OrganizerEntries().ToList();
            List<ContentRootEntry> packageAssets = ParkAssetEntries();
            IEnumerable<string> attractionGuids = packageAssets
                .OrderBy(e => e.kind == ContentRootKind.Prop ? 1 : 0)
                .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.guid);
            IEnumerable<string> endpointCandidates = organizerEntries
                .Where(e => e.kind == ContentRootKind.Attraction && !e.isDreamSequence).Select(e => e.guid);
            arenaLayout = ArenaPackageStore.LoadAndReconcile(contentId, ArenaCandidates());
            ArenaPackageStore.Save(contentId, arenaLayout);
            sequenceLayout = sequenceLayout != null && string.Equals(sequenceUndoContentId, contentId, StringComparison.Ordinal)
                ? ContentSequenceStore.Reconcile(sequenceLayout, attractionGuids)
                : ContentSequenceStore.LoadAndReconcile(contentId, attractionGuids, endpointCandidates,
                    organizerMode == OrganizerMode.Sequence);
            if (!sequenceLayout.hasExplicitEndpoints)
                ContentSequenceStore.MigrateToExplicitEndpoints(sequenceLayout, endpointCandidates,
                    organizerMode == OrganizerMode.Sequence);
            sequenceUndoContentId = contentId;
            ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);

            var allLibraryGuids = contentRoots.Where(e => (e.kind == ContentRootKind.Attraction && !e.isDreamSequence)
                || e.kind == ContentRootKind.Prop)
                .OrderBy(e => e.kind == ContentRootKind.Prop ? 1 : 0)
                .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.guid).ToList();
            ContentSequenceStore.Data adventureFallback = organizerMode == OrganizerMode.Adventure
                ? sequenceLayout
                : ContentSequenceStore.LoadAndReconcile(contentId, allLibraryGuids,
                    contentRoots.Where(e => e.kind == ContentRootKind.Attraction && !e.isDreamSequence)
                        .Select(e => e.guid), false);
            libraryLayout = ContentSequenceStore.LoadLibraryAndReconcile(
                contentId, allLibraryGuids, adventureFallback);
            ContentSequenceStore.SaveLibrary(contentId, libraryLayout);
            if (SyncPackageGroupSources())
                ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);

            // Make sure Unity's preview cache has room for everything we're
            // about to ask for. Default cache size (≈100) is fine for a
            // handful of roots but can churn under heavy reentrant use.
            AssetPreview.SetPreviewTextureCacheSize(Mathf.Max(256, contentRoots.Count * 4));
        }

        // Runs the project's actual preview-PNG generator
        // (ContentProcessor.GenerateAllLevelPreviews → PrefabPreviewRenderer)
        // — the same pipeline that fires during Upload Release — and then
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
                    $"Baking packing variants for {contentId}...",
                    0f);

                int baked = AttractionPackingBaker.BakeAllInContent(contentId);
                Debug.Log($"[ContentUploader] Refreshed packing data for {baked} attraction prefab(s).");
                EditorUtility.DisplayProgressBar(
                    "Rebuilding Previews",
                    $"Rendering preview PNGs for {contentId}...",
                    0.15f);

                // The real deal: renders each Attraction/Prop prefab into a
                // PNG file at Assets/Content/{contentId}/Previews/{name}.png.
                // Logs progress to the console for individual prefabs. This is
                // the manual "Rebuild Previews" button, so force-regenerate —
                // the upload path only fills in missing previews now.
                ContentProcessor.GenerateAllLevelPreviews(contentId, forceRegenerate: true);
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

        // Gate for the Upload Release + Build & Inspect actions: a content
        // package is only meaningful if it ships at least one Attraction or
        // Prop. A bare PlayerRig isn't a complete deliverable on its own.
        /// True when the selected content ID is one the SDK ships with, so no
        /// creator may publish under it (see lib/reservedContentIds.js on the
        /// backend, which refuses these outright).
        ///
        /// This gates ACTIONS, not the panel. Sample stays fully browsable on
        /// purpose — it is the reference a new creator learns from — and only
        /// the buttons that would push to the backend are turned off.
        private bool UploadsBlocked
        {
            get { return ContentFolders.IsReserved(contentId); }
        }

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
            DrawPackagesSection();
            GUILayout.Space(10f);

            // Outer foldout for the reusable Park Assets library.
            int attractionCount = 0, propCount = 0;
            for (int i = 0; i < contentRoots.Count; i++)
            {
                switch (contentRoots[i].kind)
                {
                    case ContentRootKind.Attraction:
                        if (!contentRoots[i].isDreamSequence) attractionCount++;
                        break;
                    case ContentRootKind.Prop: propCount++; break;
                }
            }

            int groupCount = libraryLayout?.items?.Count(item => item != null && item.IsWorld) ?? 0;
            string badgeSummary = badges.Count > 0 ? $"  ·  {badges.Count} badge(s)" : "";
            string summary = (attractionCount == 0 && propCount == 0 && groupCount == 0 && badges.Count == 0)
                ? "Park Assets (none)"
                : $"Park Assets  ·  {attractionCount} attraction(s)  ·  {propCount} prop(s)  ·  {groupCount} group(s){badgeSummary}";

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

            bool newParkAssetsFold = DrawOrganizerSectionHeader(headerRect,
                foldoutRect, parkAssetsFold, summary);
            if (newParkAssetsFold != parkAssetsFold)
            {
                parkAssetsFold = newParkAssetsFold;
                EditorPrefs.SetBool(ParkAssetsFoldPrefKey, parkAssetsFold);
            }

            var refreshContent = new GUIContent(EditorGUIUtility.IconContent("Refresh").image, "Rebuild Previews");
            using (new EditorGUI.DisabledScope(contentRoots.Count == 0))
                if (GUI.Button(refreshBtnRect, refreshContent, EditorStyles.iconButton)) RebuildPreviews();

            if (!parkAssetsFold) return;

            if (!HasShippableContent())
                EditorGUILayout.HelpBox(
                    $"Add Attractions or Props under Assets/Content/{contentId}/. New Park Assets enter Arena automatically and remain reusable in Sequence and Adventure.",
                    MessageType.Warning);
            else DrawParkAssetLibrary();

            DrawBadgesGroup();

            if (contentRoots.Count == 0) return;
            bool anyUnresolved = contentRoots.Any(e => e.customPreview == null && !e.autoPreviewResolved);
            if (anyUnresolved || AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        private void DrawPackagesSection()
        {
            EnsureContainerDeferred();
            Rect headerRect = GUILayoutUtility.GetRect(0f,
                EditorGUIUtility.singleLineHeight + 4f, GUILayout.ExpandWidth(true));
            bool expanded = DrawOrganizerSectionHeader(headerRect, headerRect,
                packagesFold, "Packages");
            if (expanded != packagesFold)
            {
                packagesFold = expanded;
                EditorPrefs.SetBool(PackagesFoldPrefKey, packagesFold);
            }
            if (!packagesFold) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            LoadPackageIcons();
            Rect packageTabsRect = GUILayoutUtility.GetRect(0f, 38f, GUILayout.ExpandWidth(true));
            var packageTabs = new[] { GUIContent.none, GUIContent.none, GUIContent.none };
            int selectedMode = GUI.Toolbar(packageTabsRect, (int)organizerMode, packageTabs);
            float packageTabWidth = packageTabsRect.width / 3f;
            DrawPackageTabContent(new Rect(packageTabsRect.x, packageTabsRect.y,
                    packageTabWidth, packageTabsRect.height), arenaTabIcon, "Arena",
                "An instant package that selects the highest-priority content fitting the available space.");
            DrawPackageTabContent(new Rect(packageTabsRect.x + packageTabWidth, packageTabsRect.y,
                    packageTabWidth, packageTabsRect.height), sequenceTabIcon, "Sequence",
                "A single-room 12 × 18 ft playable package.");
            DrawPackageTabContent(new Rect(packageTabsRect.x + packageTabWidth * 2f, packageTabsRect.y,
                    packageTabsRect.width - packageTabWidth * 2f, packageTabsRect.height), adventureTabIcon, "Adventure",
                "A spatial package that can expand across a venue.");
            if (selectedMode != (int)organizerMode)
            {
                if (organizerMode == OrganizerMode.Arena)
                    ArenaPackageStore.Save(contentId, arenaLayout);
                else
                    ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);
                organizerMode = (OrganizerMode)selectedMode;
                EditorPrefs.SetInt(OrganizerModePrefKey, selectedMode);
                sequenceLayout = null;
                arenaLayout = null;
                RefreshContentRoots();
                if (organizerMode == OrganizerMode.Sequence)
                {
                    EnsureSequenceScaffoldDeferred();
                }
            }
            var descriptionStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 12,
                padding = new RectOffset(8, 8, 8, 8),
            };
            string description = organizerMode == OrganizerMode.Arena
                ? "ARENA packages make every title useful as soon as space is found. Every Attraction and Prop is included automatically in its smallest whole-foot size; drag cards within a size to set priority or into another size to override the measured fit. Exclude anything unsuitable. The app chooses the largest occupied size that fits, then shows its first entry."
                : organizerMode == OrganizerMode.Sequence
                    ? "SEQUENCE packages make the game playable in one flexible room, authored at 12 × 18 ft. Start, Overlay, and Game Over are editable spatial levels. The persistent Game Manager controls your routing, rules, score, hubs, or randomizers; the listed order is only the working default."
                    : "ADVENTURE packages arrange a clear Start Point, Groups and Attractions through the venue, and a required End Point. The persistent Game Manager holds game rules and state; it is not a spatial container.";
            GUILayout.Label(description, descriptionStyle);
            string testLabel = organizerMode == OrganizerMode.Arena
                ? "Test Arena in Park Simulator"
                : organizerMode == OrganizerMode.Sequence
                    ? "Test Sequence in Park Simulator" : "Test Adventure in Park Simulator";
            if (GUILayout.Button(new GUIContent("▶ " + testLabel,
                organizerMode == OrganizerMode.Arena
                    ? "Preview the first-priority entry from the largest occupied Arena size."
                    : organizerMode == OrganizerMode.Sequence
                        ? "Build the Sequence from its editable parts on the basketball court blacktop."
                        : "Play this Adventure as an ordered walking route through the scanned park."),
                GUILayout.Height(30f))) QueuePackageTest();
            if (organizerMode == OrganizerMode.Sequence)
            {
                EnsureSequenceScaffoldDeferred();
            }
            DrawPackageManagerAndPlayerRow();
            if (organizerMode == OrganizerMode.Arena) DrawArenaOrganizer();
            else DrawAttractionSequenceGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawArenaOrganizer()
        {
            List<ArenaPackageStore.Candidate> candidates = ArenaCandidates();
            arenaLayout = ArenaPackageStore.Reconcile(arenaLayout
                ?? ArenaPackageStore.Load(contentId), candidates);
            var entries = ParkAssetEntries().ToDictionary(entry => entry.guid,
                entry => entry, StringComparer.Ordinal);
            List<ArenaPackageStore.Bucket> buckets = arenaLayout.buckets
                ?? new List<ArenaPackageStore.Bucket>();

            int maxRecommendedEdge = buckets.Count == 0 ? 1 : buckets.Max(bucket => bucket.lengthFeet);
            var occupiedSquares = new HashSet<int>(buckets
                .Where(bucket => bucket.widthFeet == bucket.lengthFeet)
                .Select(bucket => bucket.widthFeet));
            List<int> missingSquares = Enumerable.Range(1, maxRecommendedEdge)
                .Where(size => !occupiedSquares.Contains(size)).ToList();
            if (missingSquares.Count > 0)
            {
                string shown = string.Join(", ", missingSquares.Take(12).Select(size => $"{size}×{size}"));
                if (missingSquares.Count > 12) shown += $", +{missingSquares.Count - 12} more";
                EditorGUILayout.HelpBox("Recommended square-size gaps: " + shown
                    + ". Add a small Prop or Attraction to cover a missing size.", MessageType.Info);
            }

            if (buckets.Count == 0)
                EditorGUILayout.HelpBox("Arena has no included Attractions or Props.", MessageType.Warning);

            foreach (ArenaPackageStore.Bucket bucket in buckets)
                DrawArenaBucketSection(bucket, candidates, entries);

            List<string> excluded = arenaLayout.excludedGuids ?? new List<string>();
            if (excluded.Count == 0) return;
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Excluded from Arena", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Excluded assets remain in Park Assets. Right-click a dimmed card to include it in Arena again.",
                MessageType.None);
            int perRow = SequenceCardsPerRow();
            for (int start = 0; start < excluded.Count; start += perRow)
            {
                EditorGUILayout.BeginHorizontal();
                int rowEnd = Mathf.Min(start + perRow, excluded.Count);
                for (int index = start; index < rowEnd; index++)
                {
                    string guid = excluded[index];
                    entries.TryGetValue(guid, out ContentRootEntry entry);
                    if (entry != null)
                    {
                        Color previous = GUI.color;
                        GUI.color = new Color(previous.r, previous.g, previous.b,
                            previous.a * 0.5f);
                        Rect card = DrawPackageAssetCard(entry);
                        GUI.color = previous;
                        DrawArenaContextMenu(card, entry, guid, candidates, excluded: true);
                    }
                    if (index + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(CardSpacing);
            }
        }

        /// <summary>
        /// Arena uses the same foldout headers, preview cards, position badges,
        /// grid rhythm and context-menu interactions as Sequence and Adventure.
        /// The size bucket replaces their progression Group; only the data model
        /// is Arena-specific.
        /// </summary>
        private void DrawArenaBucketSection(ArenaPackageStore.Bucket bucket,
            List<ArenaPackageStore.Candidate> candidates,
            Dictionary<string, ContentRootEntry> entries)
        {
            if (bucket == null || bucket.guids == null || bucket.guids.Count == 0) return;
            string foldKey = $"DreamPark.ContentUploader.Arena.{contentId}."
                + bucket.widthFeet + "x" + bucket.lengthFeet;
            bool expanded = EditorPrefs.GetBool(foldKey, true);

            GUILayout.Space(4f);
            Rect header = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            GUI.Box(header, GUIContent.none, EditorStyles.helpBox);
            Rect foldRect = new Rect(header.x + 8f, header.y + 5f, 92f, 20f);
            bool nextExpanded = EditorGUI.Foldout(foldRect, expanded, "SIZE", true,
                EditorStyles.foldout);
            if (nextExpanded != expanded) EditorPrefs.SetBool(foldKey, nextExpanded);
            Rect labelRect = new Rect(foldRect.xMax + 4f, header.y + 4f,
                Mathf.Max(80f, header.width - foldRect.width - 100f), 21f);
            GUI.Label(labelRect, bucket.Label, EditorStyles.boldLabel);
            Rect countRect = new Rect(header.xMax - 90f, header.y + 5f, 82f, 20f);
            GUI.Label(countRect,
                new GUIContent(bucket.guids.Count + (bucket.guids.Count == 1 ? " option" : " options"),
                    "The first card is this size's highest-priority Arena choice. Drag a card onto another size header to override its Arena size."),
                EditorStyles.centeredGreyMiniLabel);
            HandleArenaBucketHeaderDrop(header, bucket);
            if (!nextExpanded) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            int perRow = SequenceCardsPerRow();
            for (int start = 0; start < bucket.guids.Count; start += perRow)
            {
                DrawArenaInsertionZone(bucket, bucket.guids[start], after: false);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                int rowEnd = Mathf.Min(start + perRow, bucket.guids.Count);
                for (int index = start; index < rowEnd; index++)
                {
                    string guid = bucket.guids[index];
                    entries.TryGetValue(guid, out ContentRootEntry entry);
                    if (entry != null)
                    {
                        Rect card = DrawPackageAssetCard(entry);
                        Rect badge = new Rect(card.x + 4f, card.y + 4f, 24f, 24f);
                        DrawPositionEditor(badge, ArenaPositionToken(bucket, guid), index + 1);
                        DrawSequenceDrag(card, ArenaPositionToken(bucket, guid));
                        HandleArenaCardDrop(card, bucket, guid);
                        DrawArenaContextMenu(card, entry, guid, candidates, excluded: false);
                    }
                    if (index + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                DrawArenaInsertionZone(bucket, bucket.guids[rowEnd - 1], after: true);
            }
            EditorGUILayout.EndVertical();
        }

        private static string ArenaPositionToken(ArenaPackageStore.Bucket bucket, string guid)
            => $"arena:{bucket.widthFeet}:{bucket.lengthFeet}:{guid}";

        private static bool TryParseArenaPositionToken(string token, out int widthFeet,
            out int lengthFeet, out string guid)
        {
            widthFeet = 0;
            lengthFeet = 0;
            guid = null;
            if (string.IsNullOrEmpty(token) || !token.StartsWith("arena:", StringComparison.Ordinal))
                return false;
            string[] parts = token.Split(':');
            if (parts.Length != 4 || !int.TryParse(parts[1], out widthFeet)
                || !int.TryParse(parts[2], out lengthFeet) || string.IsNullOrEmpty(parts[3]))
                return false;
            guid = parts[3];
            return true;
        }

        private void HandleArenaCardDrop(Rect card, ArenaPackageStore.Bucket bucket,
            string targetGuid)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (!TryParseArenaPositionToken(source, out _, out _, out _)) return;
            bool after = Event.current.mousePosition.x >= card.center.x;
            HandleSequenceDrop(card, ArenaDropTarget(bucket, targetGuid, after));
            DrawSequenceInsertionCue(card, after, false);
        }

        private void DrawArenaInsertionZone(ArenaPackageStore.Bucket bucket,
            string targetGuid, bool after)
        {
            Rect zone = GUILayoutUtility.GetRect(0f, 2f, GUILayout.ExpandWidth(true));
            Rect hitZone = new Rect(zone.x, zone.center.y - 6f, zone.width, 12f);
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (!TryParseArenaPositionToken(source, out _, out _, out _)) return;
            if (hitZone.Contains(Event.current.mousePosition)
                && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(zone.x, zone.center.y - 1.5f, zone.width, 3f),
                    new Color(0.2f, 0.7f, 1f, 0.95f));
            HandleSequenceDrop(hitZone, ArenaDropTarget(bucket, targetGuid, after));
        }

        private void HandleArenaBucketHeaderDrop(Rect header, ArenaPackageStore.Bucket bucket)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (!TryParseArenaPositionToken(source, out int sourceWidth,
                    out int sourceLength, out _)) return;
            bool differentBucket = sourceWidth != bucket.widthFeet
                || sourceLength != bucket.lengthFeet;
            if (differentBucket && header.Contains(Event.current.mousePosition)
                && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(header.x, header.yMax - 3f, header.width, 3f),
                    new Color(0.2f, 0.7f, 1f, 0.95f));
            HandleSequenceDrop(header,
                $"arena-append:{bucket.widthFeet}:{bucket.lengthFeet}");
        }

        private static string ArenaDropTarget(ArenaPackageStore.Bucket bucket,
            string targetGuid, bool after)
            => $"arena-{(after ? "after" : "before")}:{bucket.widthFeet}:"
                + $"{bucket.lengthFeet}:{targetGuid}";

        private void DrawArenaContextMenu(Rect card, ContentRootEntry entry, string guid,
            List<ArenaPackageStore.Candidate> candidates, bool excluded)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !card.Contains(evt.mousePosition)) return;
            var menu = new GenericMenu();
            AddRegeneratePreviewMenuItem(menu, entry);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(excluded ? "Include in Arena" : "Exclude from Arena"),
                false, () =>
                {
                    BeginSequenceChange(excluded ? "Include in Arena" : "Exclude from Arena");
                    ArenaPackageStore.SetExcluded(arenaLayout, candidates, guid, !excluded);
                    SaveArenaLayout();
                });
            if (!excluded && ArenaPackageStore.HasSizeOverride(arenaLayout, guid))
            {
                menu.AddItem(new GUIContent("Use Measured Size"), false, () =>
                {
                    BeginSequenceChange("Reset Arena Size");
                    ArenaPackageStore.ClearSizeOverride(arenaLayout, candidates, guid);
                    SaveArenaLayout();
                });
            }
            menu.ShowAsContext();
            evt.Use();
        }

        private void SaveArenaLayout()
        {
            ArenaPackageStore.Save(contentId, arenaLayout);
            EditorUtility.SetDirty(this);
            Repaint();
        }

        private static bool DrawOrganizerSectionHeader(Rect backgroundRect, Rect clickRect,
            bool expanded, string label)
        {
            bool hovered = clickRect.Contains(Event.current.mousePosition);
            Color background = EditorGUIUtility.isProSkin
                ? (hovered ? new Color32(49, 49, 49, 255) : new Color32(56, 56, 56, 255))
                : (hovered ? new Color32(205, 205, 205, 255) : new Color32(220, 220, 220, 255));
            EditorGUI.DrawRect(backgroundRect, background);

            Rect arrow = new Rect(clickRect.x + 5f, clickRect.y + 1f,
                17f, clickRect.height - 2f);
            GUI.Label(arrow, expanded ? "▼" : "▶", EditorStyles.miniLabel);
            Rect textRect = new Rect(clickRect.x + 25f, clickRect.y,
                Mathf.Max(0f, clickRect.width - 25f), clickRect.height);
            GUI.Label(textRect, label, EditorStyles.boldLabel);

            return GUI.Button(clickRect, GUIContent.none, GUIStyle.none) ? !expanded : expanded;
        }

        private void QueuePackageTest()
        {
            string targetContentId = contentId;
            OrganizerMode mode = organizerMode;
            ArenaPackageStore.Data arena = arenaLayout != null
                ? ArenaPackageStore.Clone(arenaLayout) : ArenaPackageStore.Load(targetContentId);
            ContentSequenceStore.Data layout = sequenceLayout != null
                ? ContentSequenceStore.Clone(sequenceLayout)
                : ContentSequenceStore.Load(targetContentId, mode == OrganizerMode.Sequence);
            EditorApplication.delayCall += () =>
            {
                if (string.IsNullOrEmpty(targetContentId)) return;
                try
                {
                    if (mode == OrganizerMode.Arena)
                    {
                        ArenaPackageStore.Save(targetContentId, arena);
                        DreamSequenceGenerator.EnsureContainer(targetContentId);
                    }
                    else ContentSequenceStore.Save(targetContentId, layout, mode == OrganizerMode.Sequence);
                    if (mode == OrganizerMode.Sequence)
                    {
                        AttractionPackingBaker.BakeAllInContent(targetContentId);
                        CompileDreamSequencePackage(targetContentId);
                        if (AssetDatabase.LoadAssetAtPath<DreamSequencePackageDefinition>(
                            DreamSequencePackageCompiler.DefinitionPath(targetContentId)) == null)
                            throw new InvalidOperationException("The Sequence package definition was not created.");
                    }
                    else if (mode == OrganizerMode.Adventure)
                    {
                        if (layout == null || string.IsNullOrEmpty(layout.startGuid)
                            || string.IsNullOrEmpty(layout.endGuid))
                        {
                            EditorUtility.DisplayDialog("Adventure needs endpoints",
                                "Set both Start Point and End Point before testing the Adventure.", "OK");
                            return;
                        }
                        DreamSequenceGenerator.EnsureContainer(targetContentId);
                    }
                    ParkSimPackageTest.Launch(targetContentId,
                        mode == OrganizerMode.Arena ? ParkSimPackageKind.Arena
                        : mode == OrganizerMode.Sequence ? ParkSimPackageKind.Sequence
                        : ParkSimPackageKind.Adventure);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Package test could not start", e.Message, "OK");
                }
            };
        }

        private void LoadPackageIcons()
        {
            // Unity hot reload preserves private EditorWindow fields, so the old
            // built-in Grid.BoxTool texture can survive after adding this asset.
            // Replace any cached fallback instead of only checking for null.
            if (arenaTabIcon == null || arenaTabIcon.name != "icon_arena")
                arenaTabIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/DreamPark/Editor/Icons/icon_arena.png");
            if (sequenceTabIcon == null)
                sequenceTabIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/DreamPark/Editor/Icons/icon_sequence.png");
            if (adventureTabIcon == null)
                adventureTabIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/DreamPark/Editor/Icons/icon_adventure.png");
        }

        private static void DrawPackageTabContent(Rect rect, Texture icon, string label, string tooltip)
        {
            var labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Normal,
            };
            Vector2 textSize = labelStyle.CalcSize(new GUIContent(label));
            const float iconWidth = 28f;
            const float iconHeight = 20f;
            const float gap = 5f;
            float totalWidth = (icon != null ? iconWidth + gap : 0f) + textSize.x;
            float x = rect.center.x - totalWidth * 0.5f;
            if (icon != null)
            {
                Rect iconRect = new Rect(x, rect.center.y - iconHeight * 0.5f, iconWidth, iconHeight);
                Color prior = GUI.color;
                GUI.color = EditorGUIUtility.isProSkin ? Color.white : Color.black;
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
                GUI.color = prior;
                x += iconWidth + gap;
            }
            GUI.Label(new Rect(x, rect.y, textSize.x, rect.height), new GUIContent(label, tooltip), labelStyle);
        }

        private void EnsureSequenceScaffoldDeferred()
        {
            if (sequenceScaffoldQueued || string.IsNullOrEmpty(contentId)) return;
            bool scaffoldNeeded = DreamSequenceGenerator.NeedsScaffoldRefresh(contentId);
            bool previewsMissing = !ContentProcessor.HasSequenceLevelPreviews(contentId);
            if (!scaffoldNeeded && (!previewsMissing || sequencePreviewAttemptedIds.Contains(contentId))) return;
            if (previewsMissing) sequencePreviewAttemptedIds.Add(contentId);
            sequenceScaffoldQueued = true;
            string scheduledContentId = contentId;
            EditorApplication.delayCall += () =>
            {
                sequenceScaffoldQueued = false;
                if (this == null || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                bool refreshed = DreamSequenceGenerator.NeedsScaffoldRefresh(scheduledContentId);
                if (refreshed) DreamSequenceGenerator.EnsureScaffold(scheduledContentId);
                if (refreshed || !ContentProcessor.HasSequenceLevelPreviews(scheduledContentId))
                    ContentProcessor.GenerateSequenceLevelPreviews(scheduledContentId, refreshed);
                Repaint();
            };
        }

        private void EnsureContainerDeferred()
        {
            if (containerScaffoldQueued || string.IsNullOrEmpty(contentId)
                || AssetDatabase.LoadAssetAtPath<GameObject>(
                    DreamSequenceGenerator.ContainerPrefabPath(contentId)) != null) return;
            containerScaffoldQueued = true;
            string scheduledContentId = contentId;
            EditorApplication.delayCall += () =>
            {
                containerScaffoldQueued = false;
                if (this == null || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                DreamSequenceGenerator.EnsureContainer(scheduledContentId);
                Repaint();
            };
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
                    ContentRootEntry entry = entries[i + j];
                    Rect card = DrawCard(entry);
                    if (kind == ContentRootKind.Prop)
                        DrawPreviewContextMenu(card, entry);
                    if (j < perRow - 1) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(CardSpacing);
            }
            EditorGUI.indentLevel--;
        }

        private const string SequenceDragKey = "DreamPark.SequenceDrag";

        private IEnumerable<ContentRootEntry> OrganizerEntries()
        {
            IEnumerable<ContentRootEntry> entries = contentRoots.Where(e =>
                (e.kind == ContentRootKind.Attraction && !e.isDreamSequence) || e.kind == ContentRootKind.Prop);
            if (organizerMode == OrganizerMode.Sequence)
                entries = entries.Where(e => e.kind == ContentRootKind.Attraction && e.sequenceCompatible);
            return entries;
        }

        private List<ContentRootEntry> ParkAssetEntries()
            => contentRoots.Where(e => (e.kind == ContentRootKind.Attraction && !e.isDreamSequence)
                || e.kind == ContentRootKind.Prop).ToList();

        private List<ArenaPackageStore.Candidate> ArenaCandidates()
            => ParkAssetEntries()
                .Select(entry => ArenaPackageStore.CandidateForPrefab(
                    entry.guid, entry.cachedAsset as GameObject))
                .ToList();

        private void DrawParkAssetLibrary()
        {
            var entries = ParkAssetEntries();
            if (libraryLayout == null)
            {
                libraryLayout = ContentSequenceStore.LoadLibraryAndReconcile(
                    contentId, entries.Select(e => e.guid));
                ContentSequenceStore.SaveLibrary(contentId, libraryLayout);
            }

            EditorGUILayout.HelpBox(
                "Park Assets are your reusable library. Arena includes Attractions and Props automatically unless you exclude them; drag assets into Sequence or Adventure to author those packages. Create Groups to organize reusable sets.",
                MessageType.None);

            var run = new List<int>();
            for (int i = 0; i < libraryLayout.items.Count; i++)
            {
                ContentSequenceStore.Entry item = libraryLayout.items[i];
                if (item == null || item.hidden) continue;
                if (item.IsWorld)
                {
                    DrawLibraryGridRun(run, entries);
                    run.Clear();
                    DrawLibraryGroup(item, i, entries, false);
                }
                else run.Add(i);
            }
            DrawLibraryGridRun(run, entries);
            DrawLibraryAddTiles();

            List<ContentSequenceStore.Entry> hidden = libraryLayout.items
                .Where(item => item != null && item.hidden).ToList();
            if (hidden.Count > 0)
            {
                GUILayout.Space(8f);
                EditorGUILayout.LabelField($"Hidden ({hidden.Count})", EditorStyles.boldLabel);
                var hiddenLeaves = new List<ContentSequenceStore.Entry>();
                foreach (ContentSequenceStore.Entry item in hidden)
                {
                    if (item.IsWorld)
                    {
                        DrawLibraryHiddenLeaves(hiddenLeaves, entries);
                        hiddenLeaves.Clear();
                        DrawLibraryGroup(item, libraryLayout.items.IndexOf(item), entries, true);
                    }
                    else hiddenLeaves.Add(item);
                }
                DrawLibraryHiddenLeaves(hiddenLeaves, entries);
            }
        }

        private void DrawLibraryGridRun(List<int> indices, List<ContentRootEntry> entries)
        {
            if (indices == null || indices.Count == 0) return;
            int perRow = SequenceCardsPerRow();
            EditorGUI.indentLevel++;
            for (int start = 0; start < indices.Count; start += perRow)
            {
                ContentSequenceStore.Entry first = libraryLayout.items[indices[start]];
                DrawLibraryInsertionZone("top-before|" + ContentSequenceStore.EntryToken(first));
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                int end = Mathf.Min(start + perRow, indices.Count);
                for (int slot = start; slot < end; slot++)
                {
                    ContentSequenceStore.Entry item = libraryLayout.items[indices[slot]];
                    ContentRootEntry entry = entries.FirstOrDefault(e => e.guid == item.attractionGuid);
                    if (entry != null)
                    {
                        Rect card = DrawCard(entry, true);
                        DrawSequenceDrag(card, "a:" + entry.guid);
                        string anchor = ContentSequenceStore.EntryToken(item);
                        bool after = LibrarySourceComesBeforeTarget(
                            DragAndDrop.GetGenericData(SequenceDragKey) as string, anchor);
                        HandleLibraryDrop(card, (after ? "top-after|" : "top-before|") + anchor);
                        DrawSequenceInsertionCue(card, after, false);
                        DrawLibraryAssetContextMenu(card, entry, false);
                    }
                    if (slot + 1 < end) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                ContentSequenceStore.Entry last = libraryLayout.items[indices[end - 1]];
                DrawLibraryInsertionZone("top-after|" + ContentSequenceStore.EntryToken(last));
            }
            EditorGUI.indentLevel--;
        }

        private void DrawLibraryGroup(ContentSequenceStore.Entry group, int topIndex,
            List<ContentRootEntry> entries, bool hidden)
        {
            string foldKey = "DreamPark.ContentUploader.Group.Library." + contentId + "." + group.id;
            bool expanded = EditorPrefs.GetBool(foldKey, true);
            if (!hidden) DrawLibraryInsertionZone("top-before|w:" + group.id);

            Color prior = GUI.color;
            if (hidden) GUI.color = new Color(prior.r, prior.g, prior.b, prior.a * 0.5f);
            Rect header = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            GUI.Box(header, GUIContent.none, EditorStyles.helpBox);
            DrawSequenceDrag(header, "lw:" + group.id);
            Rect foldRect = new Rect(header.x + 8f, header.y + 5f, 82f, 20f);
            bool nextExpanded = EditorGUI.Foldout(foldRect, expanded, "GROUP", true, EditorStyles.foldout);
            if (nextExpanded != expanded) EditorPrefs.SetBool(foldKey, nextExpanded);
            Rect menuRect = new Rect(header.xMax - 28f, header.y + 5f, 24f, 20f);
            Rect nameRect = new Rect(foldRect.xMax + 4f, header.y + 4f,
                Mathf.Max(60f, menuRect.x - foldRect.xMax - 8f), 21f);
            string nextName = EditorGUI.TextField(nameRect, group.name ?? "New Group");
            if (nextName != group.name)
            {
                BeginSequenceChange("Rename Group");
                group.name = nextName;
                SaveLibraryLayout();
            }
            GUI.Label(menuRect, new GUIContent("≡", "Drag this Group into either Package or reorder it in Park Assets."),
                EditorStyles.centeredGreyMiniLabel);
            GUI.color = prior;
            DrawLibraryGroupContextMenu(header, group, hidden);

            if (nextExpanded)
            {
                Rect body = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                List<string> children = group.attractionGuids ?? (group.attractionGuids = new List<string>());
                int perRow = SequenceCardsPerRow();
                for (int start = 0; start < children.Count; start += perRow)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(12f);
                    int end = Mathf.Min(start + perRow, children.Count);
                    for (int child = start; child < end; child++)
                    {
                        ContentRootEntry entry = entries.FirstOrDefault(e => e.guid == children[child]);
                        if (entry != null)
                        {
                            Color childPrior = GUI.color;
                            if (hidden) GUI.color = new Color(childPrior.r, childPrior.g, childPrior.b, childPrior.a * 0.5f);
                            Rect card = DrawCard(entry, true);
                            GUI.color = childPrior;
                            DrawSequenceDrag(card, "a:" + entry.guid);
                            if (!hidden)
                            {
                                bool after = LibrarySourceComesBeforeTarget(
                                    DragAndDrop.GetGenericData(SequenceDragKey) as string, "a:" + entry.guid);
                                HandleLibraryDrop(card, $"world|{group.id}|{child + (after ? 1 : 0)}");
                                DrawSequenceInsertionCue(card, after, false);
                            }
                            DrawLibraryAssetContextMenu(card, entry, false);
                        }
                        if (child + 1 < end) GUILayout.Space(CardSpacing);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(CardSpacing);
                }
                if (!hidden)
                {
                    Rect endDrop = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
                    GUI.Label(endDrop, "Drop here to add at the end of this Group", EditorStyles.centeredGreyMiniLabel);
                    HandleLibraryDrop(endDrop, $"world|{group.id}|{children.Count}");
                }
                EditorGUILayout.EndVertical();
            }
            if (!hidden)
            {
                DrawLibraryInsertionZone("top-after|w:" + group.id);
                GUILayout.Space(CardSpacing);
            }
        }

        private void DrawLibraryHiddenLeaves(List<ContentSequenceStore.Entry> hidden,
            List<ContentRootEntry> entries)
        {
            if (hidden == null || hidden.Count == 0) return;
            int perRow = SequenceCardsPerRow();
            for (int start = 0; start < hidden.Count; start += perRow)
            {
                GUILayout.BeginHorizontal();
                int end = Mathf.Min(start + perRow, hidden.Count);
                for (int i = start; i < end; i++)
                {
                    ContentRootEntry entry = entries.FirstOrDefault(e => e.guid == hidden[i].attractionGuid);
                    if (entry != null)
                    {
                        Color prior = GUI.color;
                        GUI.color = new Color(prior.r, prior.g, prior.b, prior.a * 0.5f);
                        Rect card = DrawCard(entry, true);
                        GUI.color = prior;
                        DrawSequenceDrag(card, "a:" + entry.guid);
                        DrawLibraryAssetContextMenu(card, entry, true);
                    }
                    if (i + 1 < end) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(CardSpacing);
            }
        }

        private void DrawLibraryAddTiles()
        {
            int perRow = SequenceCardsPerRow();
            GUILayout.BeginHorizontal();
            Rect attraction = DrawAddGridTile("Add Attraction", "Create a new Attraction prefab.");
            if (GUI.Button(attraction, GUIContent.none, GUIStyle.none)) CreateAttractionPrefab();
            if (perRow > 1) GUILayout.Space(CardSpacing);
            Rect group = DrawAddGridTile("Add Group", "Create a reusable Group in Park Assets.");
            if (GUI.Button(group, GUIContent.none, GUIStyle.none)) AddLibraryGroup();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void AddLibraryGroup()
        {
            BeginSequenceChange("Add Group");
            int firstHidden = libraryLayout.items.FindIndex(item => item != null && item.hidden);
            var group = new ContentSequenceStore.Entry
            {
                kind = "world", id = Guid.NewGuid().ToString("N"), name = "New Group"
            };
            if (firstHidden >= 0) libraryLayout.items.Insert(firstHidden, group);
            else libraryLayout.items.Add(group);
            SaveLibraryLayout();
        }

        private void DrawLibraryGroupContextMenu(Rect rect, ContentSequenceStore.Entry group, bool hidden)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !rect.Contains(evt.mousePosition)) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(hidden ? "Unhide" : "Hidden"), false, () =>
            {
                BeginSequenceChange(hidden ? "Unhide Group" : "Hide Group");
                if (ContentSequenceStore.SetGroupHidden(libraryLayout, group.id, !hidden)) SaveLibraryLayout();
            });
            menu.AddItem(new GUIContent("Remove Group"), false, () =>
            {
                BeginSequenceChange("Remove Group");
                int at = libraryLayout.items.IndexOf(group);
                libraryLayout.items.Remove(group);
                foreach (string guid in group.attractionGuids ?? new List<string>())
                    libraryLayout.items.Insert(Mathf.Clamp(at++, 0, libraryLayout.items.Count),
                        new ContentSequenceStore.Entry { attractionGuid = guid, hidden = hidden });
                SaveLibraryLayout();
            });
            menu.ShowAsContext();
            evt.Use();
        }

        private bool LibrarySourceComesBeforeTarget(string source, string target)
        {
            string normalized = source != null && source.StartsWith("lw:", StringComparison.Ordinal)
                ? "w:" + source.Substring(3) : source;
            if (string.IsNullOrEmpty(normalized) || normalized == target) return false;
            int from = libraryLayout.items.FindIndex(x => ContentSequenceStore.EntryToken(x) == normalized);
            int to = libraryLayout.items.FindIndex(x => ContentSequenceStore.EntryToken(x) == target);
            return from >= 0 && to >= 0 && from < to;
        }

        private void DrawLibraryInsertionZone(string target)
        {
            Rect zone = GUILayoutUtility.GetRect(0f, 6f, GUILayout.ExpandWidth(true));
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source)) return;
            if (zone.Contains(Event.current.mousePosition) && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(zone.x, zone.center.y - 1.5f, zone.width, 3f),
                    new Color(0.2f, 0.7f, 1f, 0.95f));
            HandleLibraryDrop(zone, target);
        }

        private void HandleLibraryDrop(Rect rect, string target)
            => HandleSequenceDrop(rect, "library:" + target);

        private void SaveLibraryLayout()
        {
            ContentSequenceStore.SaveLibrary(contentId, libraryLayout);
            if (SyncPackageGroupSources())
                ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);
            EditorUtility.SetDirty(this);
            Repaint();
        }

        private bool SyncPackageGroupSources()
        {
            if (sequenceLayout?.items == null || libraryLayout?.items == null) return false;

            List<ContentSequenceStore.Entry> sources = libraryLayout.items
                .Where(item => item != null && item.IsWorld).ToList();
            var available = new HashSet<string>(ParkAssetEntries().Select(entry => entry.guid), StringComparer.Ordinal);
            bool changed = false;
            foreach (ContentSequenceStore.Entry packageGroup in sequenceLayout.items
                .Where(item => item != null && item.IsWorld))
            {
                ContentSequenceStore.Entry source = !string.IsNullOrEmpty(packageGroup.sourceGroupId)
                    ? sources.FirstOrDefault(item => item.id == packageGroup.sourceGroupId)
                    : sources.FirstOrDefault(item => string.Equals(item.name, packageGroup.name,
                        StringComparison.Ordinal));
                if (source == null) continue;

                if (!string.Equals(packageGroup.sourceGroupId, source.id, StringComparison.Ordinal))
                {
                    packageGroup.sourceGroupId = source.id;
                    changed = true;
                }
                if (!string.Equals(packageGroup.name, source.name, StringComparison.Ordinal))
                {
                    packageGroup.name = source.name;
                    changed = true;
                }

                // Repair package Groups created by older builds that copied the
                // header but filtered every child out. Non-empty package Groups
                // retain their independently authored order and membership.
                packageGroup.attractionGuids = packageGroup.attractionGuids ?? new List<string>();
                packageGroup.attractionIds = packageGroup.attractionIds ?? new List<string>();
                if (packageGroup.attractionGuids.Count == 0)
                {
                    List<string> children = (source.attractionGuids ?? new List<string>())
                        .Where(available.Contains).ToList();
                    if (children.Count > 0)
                    {
                        packageGroup.attractionGuids.AddRange(children);
                        packageGroup.attractionIds.Clear();
                        packageGroup.attractionIds.AddRange(children.Select(_ => Guid.NewGuid().ToString("N")));
                        changed = true;
                    }
                }
            }
            return changed;
        }

        private void DrawAttractionSequenceGroup()
        {
            // Package Groups may contain the complete reusable Group, including
            // Props. Individual Sequence drops are still restricted separately.
            var attractions = ParkAssetEntries();
            if (sequenceLayout == null)
            {
                sequenceLayout = ContentSequenceStore.LoadAndReconcile(contentId, attractions
                    .OrderBy(e => e.kind == ContentRootKind.Prop ? 1 : 0)
                    .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.guid), attractions.Where(e => e.kind == ContentRootKind.Attraction)
                    .Select(e => e.guid), organizerMode == OrganizerMode.Sequence);
                sequenceUndoContentId = contentId;
            }
            firstSequenceAttractionGuid = organizerMode == OrganizerMode.Adventure ? sequenceLayout.startGuid : null;
            lastSequenceAttractionGuid = organizerMode == OrganizerMode.Adventure ? sequenceLayout.endGuid : null;

            if (organizerMode == OrganizerMode.Sequence)
            {
                DrawGeneratedPrefabSlotsRow(
                    "START LEVEL", DreamSequenceGenerator.StartLevelPath(contentId),
                    "Your editable spatial introduction. The default start button asks the Game Manager to begin.",
                    "OVERLAY LEVEL", DreamSequenceGenerator.OverlayLevelPath(contentId),
                    "Your persistent level UI. Visible over ordinary levels by default, not Start or Game Over.",
                    null, null, null);
            }
            else
            {
                DrawEndpointSlot("START POINT", sequenceLayout.startGuid, "start-slot", attractions);
            }

            var gridRun = new List<int>();
            for (int i = 0; i < sequenceLayout.items.Count; i++)
            {
                var item = sequenceLayout.items[i];
                if (item.hidden) continue;
                if (item.IsWorld)
                {
                    DrawTopLevelGridRun(gridRun, attractions);
                    gridRun.Clear();
                    DrawWorldSection(item, i, attractions);
                }
                else gridRun.Add(i);
            }
            DrawTopLevelGridRun(gridRun, attractions);
            if (sequenceLayout.items.Count == 0) DrawTopLevelInsertionZone("top-end");

            if (organizerMode == OrganizerMode.Sequence)
            {
                DrawGeneratedPrefabSlot("GAME OVER LEVEL", DreamSequenceGenerator.GameOverLevelPath(contentId),
                    "The specialized 12 × 18 ft final level reached after the last compiled attraction.");
            }
            else
                DrawEndpointSlot("END POINT", sequenceLayout.endGuid, "end-slot", attractions);
        }

        private int SequenceCardsPerRow()
        {
            float panelWidth = Mathf.Max(position.width - 24f, CardWidth);
            return Mathf.Max(1, Mathf.FloorToInt((panelWidth + CardSpacing) / (CardWidth + CardSpacing)));
        }

        private void DrawGeneratedPrefabSlot(string label, string assetPath, string tooltip)
        {
            GUILayout.Space(2f);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            DrawGeneratedPrefabCard(assetPath, tooltip);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawGeneratedPrefabSlotsRow(string firstLabel, string firstAssetPath,
            string firstTooltip, string secondLabel, string secondAssetPath, string secondTooltip,
            string thirdLabel, string thirdAssetPath, string thirdTooltip)
        {
            GUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            DrawGeneratedPrefabColumn(firstLabel, firstAssetPath, firstTooltip);
            GUILayout.Space(CardSpacing);
            DrawGeneratedPrefabColumn(secondLabel, secondAssetPath, secondTooltip);
            if (!string.IsNullOrEmpty(thirdAssetPath))
            {
                GUILayout.Space(CardSpacing);
                DrawGeneratedPrefabColumn(thirdLabel, thirdAssetPath, thirdTooltip);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawGeneratedPrefabColumn(string label, string assetPath, string tooltip)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(CardWidth));
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(CardWidth));
            DrawGeneratedPrefabCard(assetPath, tooltip);
            EditorGUILayout.EndVertical();
        }

        private void DrawGeneratedPrefabCard(string assetPath, string tooltip)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            float totalHeight = CardImageSize + CardLabelHeight + 2f;
            Rect card = GUILayoutUtility.GetRect(CardWidth, totalHeight,
                GUILayout.Width(CardWidth), GUILayout.Height(totalHeight));
            Rect imageRect = new Rect(card.x, card.y, CardWidth, CardImageSize);
            Rect labelRect = new Rect(card.x, card.y + CardImageSize + 2f, CardWidth, CardLabelHeight);
            EditorGUI.DrawRect(imageRect, new Color(0f, 0f, 0f, 0.18f));
            string previewPath = prefab != null
                ? $"Assets/Content/{contentId}/Previews/{prefab.name}.png" : null;
            Texture preview = previewPath != null
                ? AssetDatabase.LoadAssetAtPath<Texture2D>(previewPath) : null;
            if (preview == null && prefab != null) preview = AssetPreview.GetAssetPreview(prefab);
            if (preview == null && prefab != null) preview = AssetPreview.GetMiniThumbnail(prefab);
            if (preview == null) preview = EditorGUIUtility.IconContent("Prefab Icon").image;
            GUI.DrawTexture(imageRect, preview, ScaleMode.ScaleToFit);
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            string cardName = string.Equals(assetPath,
                DreamSequenceGenerator.ContainerPrefabPath(contentId), StringComparison.OrdinalIgnoreCase)
                ? "Game Manager" : prefab != null ? prefab.name : "Generating…";
            GUI.Label(labelRect, new GUIContent(cardName, tooltip), style);
            if (GUI.Button(card, new GUIContent("", tooltip), GUIStyle.none) && prefab != null)
            {
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }
            if (prefab != null && Event.current.type == EventType.ContextClick
                && card.Contains(Event.current.mousePosition))
            {
                string previewContentId = contentId;
                string previewAssetPath = assetPath;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Regenerate Preview"), false, () =>
                {
                    ContentProcessor.RegeneratePreviewForPrefab(previewContentId, previewAssetPath);
                    Repaint();
                });
                menu.ShowAsContext();
                Event.current.Use();
            }
        }

        private void DrawPackageManagerAndPlayerRow()
        {
            ContentRootEntry player = contentRoots.FirstOrDefault(e => e.kind == ContentRootKind.Player);
            GUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            DrawGeneratedPrefabColumn("GAME MANAGER", DreamSequenceGenerator.ContainerPrefabPath(contentId),
                "Persistent game rules, progression and state for both Sequence and Adventure. Edit this prefab to customize the experience.");
            GUILayout.Space(CardSpacing);
            EditorGUILayout.BeginVertical(GUILayout.Width(CardWidth));
            EditorGUILayout.LabelField("PLAYER", EditorStyles.boldLabel, GUILayout.Width(CardWidth));
            if (player != null) DrawCard(player, showAssetWarning: false);
            else DrawAddGridTile("Player Required", "Add a Player prefab to this content package.");
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEndpointSlot(string label, string guid, string target,
            List<ContentRootEntry> entries)
        {
            GUILayout.Space(2f);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 12f);
            ContentRootEntry entry = entries.FirstOrDefault(e => e.guid == guid);
            Rect rect;
            if (entry != null)
            {
                rect = DrawCard(entry, true, false);
                int position = target == "start-slot" ? 1 : Mathf.Max(2, ContentSequenceStore.Flatten(sequenceLayout).Count());
                var badge = new Rect(rect.x + 4f, rect.y + 4f, 24f, 24f);
                DrawPositionEditor(badge, "endpoint:" + target, position,
                    target == "start-slot", target == "end-slot", true, false);
                DrawAttractionContextMenu(rect, entry, false);
            }
            else
            {
                rect = DrawAddGridTile(target == "start-slot" ? "Assign Start Point" : "Assign End Point",
                    "Drag an attraction here. This slot is required.");
            }
            HandleEndpointDrop(rect, target);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        private void HandleEndpointDrop(Rect rect, string target)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source) || source.StartsWith("w:", StringComparison.Ordinal)
                || source.StartsWith("lw:", StringComparison.Ordinal)) return;
            string guid = source.StartsWith("a:", StringComparison.Ordinal) ? source.Substring(2)
                : source.StartsWith("p:", StringComparison.Ordinal)
                    ? ContentSequenceStore.PlacementGuid(sequenceLayout, source.Substring(2)) : null;
            ContentRootEntry entry = contentRoots.FirstOrDefault(e => e.guid == guid);
            if (entry == null || entry.kind != ContentRootKind.Attraction) return;
            HandleSequenceDrop(rect, target);
            if (Event.current.type == EventType.Repaint && rect.Contains(Event.current.mousePosition))
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 3f), new Color(0.95f, 0.7f, 0.08f, 0.95f));
        }

        private void DrawUnsortedGrid(List<ContentRootEntry> entries, string label)
        {
            GUILayout.Space(8f);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            var hiddenSet = new HashSet<string>(sequenceLayout.hiddenGuids ?? new List<string>(), StringComparer.Ordinal);
            var ordered = (sequenceLayout.unsortedGuids ?? new List<string>())
                .Where(g => !hiddenSet.Contains(g)).Concat((sequenceLayout.unsortedGuids ?? new List<string>())
                    .Where(hiddenSet.Contains)).ToList();
            int total = ordered.Count + 2;
            int perRow = SequenceCardsPerRow();
            EditorGUI.indentLevel++;
            for (int start = 0; start < total; start += perRow)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                int rowEnd = Mathf.Min(start + perRow, total);
                for (int slot = start; slot < rowEnd; slot++)
                {
                    if (slot < ordered.Count)
                    {
                        string guid = ordered[slot];
                        ContentRootEntry entry = entries.FirstOrDefault(e => e.guid == guid);
                        if (entry != null)
                        {
                            bool hidden = hiddenSet.Contains(guid);
                            Color prior = GUI.color;
                            if (hidden) GUI.color = new Color(prior.r, prior.g, prior.b, prior.a * 0.5f);
                            Rect card = DrawPackageAssetCard(entry);
                            GUI.color = prior;
                            if (!hidden) DrawSequenceDrag(card, "a:" + guid);
                            DrawAttractionContextMenu(card, entry, hidden);
                            HandleSequenceItemDrop(card, "unsorted-end");
                        }
                    }
                    else if (slot == ordered.Count)
                    {
                        Rect tile = DrawAddGridTile("Add Attraction", "Create a new Attraction prefab.");
                        if (GUI.Button(tile, GUIContent.none, GUIStyle.none)) CreateAttractionPrefab();
                    }
                    else
                    {
                        Rect tile = DrawAddGridTile("Add Group", "Create a Group between Start and End.");
                        if (GUI.Button(tile, GUIContent.none, GUIStyle.none)) AddWorldAtEnd();
                    }
                    if (slot + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(CardSpacing);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawTopLevelGridRun(List<int> itemIndices, List<ContentRootEntry> attractions)
        {
            if (itemIndices == null || itemIndices.Count == 0) return;
            int perRow = SequenceCardsPerRow();
            EditorGUI.indentLevel++;
            for (int start = 0; start < itemIndices.Count; start += perRow)
            {
                ContentSequenceStore.Entry firstInRow = sequenceLayout.items[itemIndices[start]];
                DrawTopLevelInsertionZone("top-before|" + ContentSequenceStore.PlacementToken(firstInRow));
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                int rowEnd = Mathf.Min(start + perRow, itemIndices.Count);
                for (int slot = start; slot < rowEnd; slot++)
                {
                    int itemIndex = itemIndices[slot];
                    var item = sequenceLayout.items[itemIndex];
                    ContentRootEntry entry = attractions.FirstOrDefault(e => e.guid == item.attractionGuid);
                    if (entry != null)
                    {
                        Rect card = DrawPackageAssetCard(entry);
                        string placementToken = ContentSequenceStore.PlacementToken(item);
                        DrawSequenceCardDecorations(card,
                            itemIndex + (organizerMode == OrganizerMode.Adventure ? 2 : 1), entry, placementToken);
                        DrawSequenceDrag(card, placementToken);
                        HandleTopLevelCardDrop(card, placementToken);
                        DrawPackageContextMenu(card, entry, item.id);
                    }
                    if (slot + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                ContentSequenceStore.Entry lastInRow = sequenceLayout.items[itemIndices[rowEnd - 1]];
                DrawTopLevelInsertionZone("top-after|" + ContentSequenceStore.PlacementToken(lastInRow));
            }
            EditorGUI.indentLevel--;
        }

        private void DrawWorldSection(ContentSequenceStore.Entry world, int topIndex, List<ContentRootEntry> attractions)
        {
            string foldKey = "DreamPark.ContentUploader.World." + contentId + "." + world.id;
            bool expanded = EditorPrefs.GetBool(foldKey, true);

            DrawTopLevelInsertionZone("top-before|w:" + world.id);

            Rect header = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            GUI.Box(header, GUIContent.none, EditorStyles.helpBox);
            // Register the whole header before its controls draw. A click still
            // edits/toggles the control under it, while a 3 px movement turns
            // the same gesture into a World drag from anywhere on the bar.
            DrawSequenceDrag(header, "w:" + world.id);
            Rect numberRect = new Rect(header.x + 5f, header.y + 3f, 24f, 24f);
            DrawPositionEditor(numberRect, "w:" + world.id,
                topIndex + (organizerMode == OrganizerMode.Adventure ? 2 : 1));

            Rect foldRect = new Rect(header.x + 34f, header.y + 5f, 80f, 20f);
            bool nextExpanded = EditorGUI.Foldout(foldRect, expanded, "GROUP", true, EditorStyles.foldout);
            if (nextExpanded != expanded) EditorPrefs.SetBool(foldKey, nextExpanded);

            Rect removeRect = new Rect(header.xMax - 58f, header.y + 5f, 52f, 20f);
            Rect dragRect = new Rect(removeRect.x - 28f, header.y + 5f, 24f, 20f);
            GUI.Label(dragRect, new GUIContent("≡", "Drag to reorder this Group"), EditorStyles.centeredGreyMiniLabel);
            Rect nameRect = new Rect(foldRect.xMax + 4f, header.y + 4f,
                Mathf.Max(60f, dragRect.x - foldRect.xMax - 8f), 21f);
            GUI.Label(nameRect, new GUIContent(world.name ?? "Group",
                "Rename this Group in Park Assets."), EditorStyles.boldLabel);

            if (GUI.Button(removeRect, new GUIContent("Remove", "Remove the Group from this Package."), EditorStyles.miniButton))
            {
                BeginSequenceChange("Remove Group");
                ContentSequenceStore.RemoveGroupFromPackage(sequenceLayout, world.id);
                SaveSequenceLayout();
                return;
            }

            // A World is a full-width line break. Attractions dropped on its header
            // enter at the start; a dragged World reorders at the same top-level slot.
            HandleWorldHeaderDrop(header, world.id, topIndex);

            if (!nextExpanded)
            {
                DrawTopLevelInsertionZone("top-after|w:" + world.id);
                return;
            }

            var children = world.attractionGuids ?? (world.attractionGuids = new List<string>());
            Rect worldBody = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool pointerOverCard = false;
            if (children.Count == 0)
            {
                Rect empty = GUILayoutUtility.GetRect(0f, 52f, GUILayout.ExpandWidth(true));
                GUI.Label(empty, "Drop Park Assets anywhere in this Group", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                pointerOverCard = DrawWorldGrid(world, topIndex, attractions);
                Rect dropRect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
                GUI.Label(dropRect, new GUIContent("Drop anywhere here to add at the end", "Drop an attraction or prop directly on a card to insert it at that exact position."), EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.EndVertical();
            if (!pointerOverCard)
            {
                HandleSequenceItemDrop(worldBody, $"world|{world.id}|{children.Count}");
                DrawWorldEndInsertionCue(worldBody);
            }
            DrawTopLevelInsertionZone("top-after|w:" + world.id);
        }

        private bool DrawWorldGrid(ContentSequenceStore.Entry world, int topIndex, List<ContentRootEntry> attractions)
        {
            List<string> children = world.attractionGuids;
            // Sequence-incompatible entries remain visible for feedback, but they
            // are not compiled as levels. Keep the visible order honest by showing
            // the compiled entries first (in their authored relative order), then
            // the excluded entries as an unnumbered tail.
            var displayedChildren = children
                .Select((guid, sourceIndex) => new
                {
                    sourceIndex,
                    entry = attractions.FirstOrDefault(candidate => candidate.guid == guid),
                })
                .Where(child => child.entry != null)
                .OrderBy(child => IsPackageEntryCompatible(child.entry) ? 0 : 1)
                .ToList();
            int perRow = SequenceCardsPerRow();
            bool pointerOverCard = false;
            int compatiblePosition = 0;
            for (int start = 0; start < displayedChildren.Count; start += perRow)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                int rowEnd = Mathf.Min(start + perRow, displayedChildren.Count);
                for (int displayIndex = start; displayIndex < rowEnd; displayIndex++)
                {
                    var child = displayedChildren[displayIndex];
                    int childIndex = child.sourceIndex;
                    ContentRootEntry entry = child.entry;
                    if (entry != null)
                    {
                        Rect card = DrawPackageAssetCard(entry);
                        pointerOverCard |= card.Contains(Event.current.mousePosition);
                        string placementToken = ContentSequenceStore.ChildPlacementToken(world, childIndex);
                        if (IsPackageEntryCompatible(entry))
                            DrawSequenceCardDecorations(card, ++compatiblePosition, entry, placementToken);
                        DrawSequenceDrag(card, placementToken);
                        HandleWorldCardDrop(card, world.id, childIndex, placementToken);
                        DrawPackageContextMenu(card, entry, placementToken.Substring(2));
                    }
                    if (displayIndex + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(2f);
            }
            return pointerOverCard;
        }

        private void DrawAttractionGridTail(List<int> activeItemIndices, List<ContentRootEntry> attractions)
        {
            List<ContentRootEntry> hidden = sequenceLayout.items
                .Where(item => item != null && !item.IsWorld && item.hidden)
                .Select(item => attractions.FirstOrDefault(entry => entry.guid == item.attractionGuid))
                .Where(entry => entry != null).ToList();
            int activeCount = activeItemIndices?.Count ?? 0;
            int total = activeCount + hidden.Count + 2;
            int perRow = SequenceCardsPerRow();
            EditorGUI.indentLevel++;
            for (int start = 0; start < total; start += perRow)
            {
                if (start < activeCount)
                {
                    ContentSequenceStore.Entry firstInRow = sequenceLayout.items[activeItemIndices[start]];
                    DrawTopLevelInsertionZone("top-before|" + ContentSequenceStore.PlacementToken(firstInRow));
                }
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                int rowEnd = Mathf.Min(start + perRow, total);
                for (int slot = start; slot < rowEnd; slot++)
                {
                    if (slot < activeCount)
                    {
                        int itemIndex = activeItemIndices[slot];
                        ContentSequenceStore.Entry item = sequenceLayout.items[itemIndex];
                        ContentRootEntry entry = attractions.FirstOrDefault(e => e.guid == item.attractionGuid);
                        if (entry != null)
                        {
                            Rect card = DrawPackageAssetCard(entry);
                            string placementToken = ContentSequenceStore.PlacementToken(item);
                            DrawSequenceCardDecorations(card, itemIndex + 1, entry, placementToken);
                            DrawSequenceDrag(card, placementToken);
                            HandleTopLevelCardDrop(card, placementToken);
                            DrawAttractionContextMenu(card, entry, false);
                        }
                    }
                    else if (slot < activeCount + hidden.Count)
                    {
                        ContentRootEntry entry = hidden[slot - activeCount];
                        Color previous = GUI.color;
                        GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.5f);
                        Rect card = DrawCard(entry, true, false);
                        GUI.color = previous;
                        DrawAttractionContextMenu(card, entry, true);
                    }
                    else if (slot == activeCount + hidden.Count)
                    {
                        Rect tile = DrawAddGridTile("Add Attraction", "Create a new Attraction prefab.");
                        if (GUI.Button(tile, GUIContent.none, GUIStyle.none))
                            CreateAttractionPrefab();
                        HandleSequenceDrop(tile, "top-end");
                    }
                    else
                    {
                        Rect tile = DrawAddGridTile("Add Group", "Create a new Group at the end of the progression.");
                        if (GUI.Button(tile, GUIContent.none, GUIStyle.none))
                            AddWorldAtEnd();
                        HandleSequenceDrop(tile, "top-end");
                    }
                    if (slot + 1 < rowEnd) GUILayout.Space(CardSpacing);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                int lastActiveSlot = Mathf.Min(rowEnd, activeCount) - 1;
                if (lastActiveSlot >= start)
                {
                    ContentSequenceStore.Entry lastInRow = sequenceLayout.items[activeItemIndices[lastActiveSlot]];
                    DrawTopLevelInsertionZone("top-after|" + ContentSequenceStore.PlacementToken(lastInRow));
                }
                else GUILayout.Space(CardSpacing);
            }
            EditorGUI.indentLevel--;
        }

        private Rect DrawAddGridTile(string label, string tooltip)
        {
            float totalHeight = CardImageSize + CardLabelHeight + 2f;
            Rect card = GUILayoutUtility.GetRect(CardWidth, totalHeight,
                GUILayout.Width(CardWidth), GUILayout.Height(totalHeight));
            Rect image = new Rect(card.x, card.y, CardWidth, CardImageSize);
            Rect text = new Rect(card.x, card.y + CardImageSize + 2f, CardWidth, CardLabelHeight);
            GUI.Box(image, new GUIContent("", tooltip), EditorStyles.helpBox);
            var plus = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 28,
            };
            GUI.Label(image, new GUIContent("+", tooltip), plus);
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            GUI.Label(text, new GUIContent(label, tooltip), labelStyle);
            return card;
        }

        private void AddWorldAtEnd()
        {
            BeginSequenceChange("Add Group");
            int firstHidden = sequenceLayout.items.FindIndex(item => item.hidden);
            var world = new ContentSequenceStore.Entry
            {
                kind = "world", id = Guid.NewGuid().ToString("N"), name = "New Group"
            };
            if (firstHidden >= 0) sequenceLayout.items.Insert(firstHidden, world);
            else sequenceLayout.items.Add(world);
            SaveSequenceLayout();
        }

        private void CreateAttractionPrefab()
        {
            string folder = "Assets/Content/" + contentId;
            string path = EditorUtility.SaveFilePanelInProject(
                "Add Attraction", "A_", "prefab", "Choose a name for the new Attraction.", folder);
            if (string.IsNullOrEmpty(path)) return;

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            root.AddComponent<AttractionTemplate>();
            GameObject prefab = null;
            try
            {
                prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                DestroyImmediate(root);
            }
            AssetDatabase.SaveAssets();
            RefreshContentRoots();
            if (prefab != null)
            {
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }
        }

        private void DrawAttractionContextMenu(Rect card, ContentRootEntry entry, bool hidden)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !card.Contains(evt.mousePosition)) return;

            var menu = new GenericMenu();
            AddRegeneratePreviewMenuItem(menu, entry);
            menu.AddSeparator("");
            bool isEndpoint = organizerMode == OrganizerMode.Adventure
                && (entry.guid == sequenceLayout?.startGuid || entry.guid == sequenceLayout?.endGuid);
            if (isEndpoint) menu.AddDisabledItem(new GUIContent("Hidden"));
            else
                menu.AddItem(new GUIContent(hidden ? "Unhide" : "Hidden"), false, () =>
                {
                    BeginSequenceChange(hidden ? "Unhide Sequence Item" : "Hide Sequence Item");
                    if (ContentSequenceStore.SetHidden(sequenceLayout, entry.guid, !hidden)) SaveSequenceLayout();
                });
            if (entry.kind == ContentRootKind.Attraction)
            {
                bool positionRequiresAttraction = !hidden
                    && (entry.guid == firstSequenceAttractionGuid || entry.guid == lastSequenceAttractionGuid);
                if (positionRequiresAttraction)
                    menu.AddDisabledItem(new GUIContent("Is Required"), true);
                else if (entry.requiredForGame)
                    menu.AddItem(new GUIContent("Make Optional"), false, () => SetManualRequired(entry, false));
                else
                    menu.AddItem(new GUIContent("Make Required"), false, () => SetManualRequired(entry, true));
            }
            menu.ShowAsContext();
            evt.Use();
        }

        private void DrawLibraryAssetContextMenu(Rect card, ContentRootEntry entry, bool hidden)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !card.Contains(evt.mousePosition)) return;
            var menu = new GenericMenu();
            AddRegeneratePreviewMenuItem(menu, entry);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(hidden ? "Unhide" : "Hidden"), false, () =>
            {
                BeginSequenceChange(hidden ? "Unhide Park Asset" : "Hide Park Asset");
                if (ContentSequenceStore.SetHidden(libraryLayout, entry.guid, !hidden)) SaveLibraryLayout();
            });
            if (entry.kind == ContentRootKind.Attraction)
            {
                if (entry.requiredForGame)
                    menu.AddItem(new GUIContent("Make Optional"), false, () => SetManualRequired(entry, false));
                else menu.AddItem(new GUIContent("Make Required"), false, () => SetManualRequired(entry, true));
            }
            menu.ShowAsContext();
            evt.Use();
        }

        private void DrawPackageContextMenu(Rect card, ContentRootEntry entry, string placementId)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !card.Contains(evt.mousePosition)) return;
            var menu = new GenericMenu();
            AddRegeneratePreviewMenuItem(menu, entry);
            bool endpoint = organizerMode == OrganizerMode.Adventure
                && (entry.guid == sequenceLayout?.startGuid || entry.guid == sequenceLayout?.endGuid);
            if (endpoint) menu.AddDisabledItem(new GUIContent("Remove from Package"));
            else
                menu.AddItem(new GUIContent("Remove from Package"), false, () =>
                {
                    BeginSequenceChange("Remove from Package");
                    if (ContentSequenceStore.RemovePlacementFromPackage(sequenceLayout, placementId)) SaveSequenceLayout();
                });
            if (entry.kind == ContentRootKind.Attraction)
            {
                if (endpoint) menu.AddDisabledItem(new GUIContent("Is Required"), true);
                else if (entry.requiredForGame)
                    menu.AddItem(new GUIContent("Make Optional"), false, () => SetManualRequired(entry, false));
                else menu.AddItem(new GUIContent("Make Required"), false, () => SetManualRequired(entry, true));
            }
            menu.ShowAsContext();
            evt.Use();
        }

        private void DrawPreviewContextMenu(Rect card, ContentRootEntry entry)
        {
            Event evt = Event.current;
            if (evt.type != EventType.ContextClick || !card.Contains(evt.mousePosition)) return;

            var menu = new GenericMenu();
            AddRegeneratePreviewMenuItem(menu, entry);
            menu.ShowAsContext();
            evt.Use();
        }

        private void AddRegeneratePreviewMenuItem(GenericMenu menu, ContentRootEntry entry)
        {
            menu.AddItem(new GUIContent("Regenerate Preview"), false,
                () => QueueRegeneratePreview(entry));
        }

        private void QueueRegeneratePreview(ContentRootEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.assetPath) || string.IsNullOrEmpty(contentId)) return;
            string scheduledContentId = contentId;
            string scheduledAssetPath = entry.assetPath;
            string displayName = entry.name;
            EditorApplication.delayCall += () =>
            {
                if (this == null || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                try
                {
                    EditorUtility.DisplayProgressBar(
                        "Regenerating Preview", $"Rendering {displayName}...", 0.5f);
                    if (!ContentProcessor.RegeneratePreviewForPrefab(scheduledContentId, scheduledAssetPath))
                    {
                        EditorUtility.DisplayDialog(
                            "Preview Generation Failed",
                            $"DreamPark could not generate a preview for {displayName}. Check the Console for details.",
                            "OK");
                        return;
                    }
                    OnPreviewSaved(scheduledContentId, scheduledAssetPath);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            };
        }

        private void SetManualRequired(ContentRootEntry entry, bool required)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            AttractionTemplate template = prefab != null ? prefab.GetComponent<AttractionTemplate>() : null;
            if (template == null) return;
            Undo.RegisterCompleteObjectUndo(template, required ? "Make Attraction Required" : "Make Attraction Optional");
            template.gameRequiresAttraction = required;
            EditorUtility.SetDirty(template);
            AssetDatabase.SaveAssets();
            entry.requiredForGame = required;
            Repaint();
        }

        private bool IsEffectivelyRequired(ContentRootEntry entry)
        {
            if (entry == null || entry.kind != ContentRootKind.Attraction) return false;
            if (entry.requiredForGame) return true;
            return organizerMode == OrganizerMode.Adventure
                && (entry.guid == sequenceLayout?.startGuid || entry.guid == sequenceLayout?.endGuid);
        }

        private static void MoveListItem<T>(List<T> list, int from, int to)
        {
            if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return;
            T value = list[from]; list.RemoveAt(from); list.Insert(to, value);
        }

        private void DrawSequenceCardDecorations(Rect card, int position, ContentRootEntry entry, string token)
        {
            if (!IsPackageEntryCompatible(entry)) return;
            var numberRect = new Rect(card.x + 4f, card.y + 4f, 24f, 24f);
            DrawPositionEditor(numberRect, token, position,
                entry.guid == firstSequenceAttractionGuid,
                entry.guid == lastSequenceAttractionGuid,
                IsEffectivelyRequired(entry));
        }

        private Rect DrawPackageAssetCard(ContentRootEntry entry)
        {
            string incompatibility = SequenceIncompatibilityMessage(entry);
            Color previous = GUI.color;
            if (!string.IsNullOrEmpty(incompatibility))
                GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * 0.5f);
            Rect card = DrawCard(entry, true, false);
            GUI.color = previous;

            if (!string.IsNullOrEmpty(incompatibility))
            {
                Rect image = new Rect(card.x, card.y, CardWidth, CardImageSize);
                EditorGUI.DrawRect(image, new Color(0.08f, 0.08f, 0.08f, 0.18f));
                GUIContent icon = EditorGUIUtility.IconContent("console.warnicon.sml");
                Rect warning = new Rect(image.xMax - 22f, image.yMax - 22f, 20f, 20f);
                GUI.Label(warning, new GUIContent(icon != null ? icon.image : null, incompatibility));
                GUI.Label(card, new GUIContent(string.Empty, incompatibility), GUIStyle.none);
            }
            return card;
        }

        private string SequenceIncompatibilityMessage(ContentRootEntry entry)
        {
            if (organizerMode != OrganizerMode.Sequence || entry == null) return null;
            if (entry.kind == ContentRootKind.Prop)
                return "Not Sequence compatible: Props cannot run as Sequence levels and will not be included in the generated Dream Sequence.";
            if (entry.kind == ContentRootKind.Attraction && !entry.sequenceCompatible)
                return "Not Sequence compatible: this Attraction does not fit inside 12 × 18 ft at its smallest shrink size and will not be included in the generated Dream Sequence.";
            return null;
        }

        private bool IsPackageEntryCompatible(ContentRootEntry entry)
        {
            if (organizerMode != OrganizerMode.Sequence) return true;
            return entry != null
                && entry.kind == ContentRootKind.Attraction
                && entry.sequenceCompatible;
        }

        private void DrawPositionEditor(Rect rect, string token, int currentPosition,
            bool isStarter = false, bool isFinal = false, bool isRequired = false, bool allowEdit = true)
        {
            if (editingSequencePosition == token)
            {
                GUI.SetNextControlName("SequencePositionField");
                sequencePositionText = GUI.TextField(rect, sequencePositionText, 3);
                EditorGUI.FocusTextInControl("SequencePositionField");
                if (Event.current.type == EventType.KeyDown
                    && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                {
                    CommitSequencePosition(token);
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseDown && !rect.Contains(Event.current.mousePosition))
                {
                    CommitSequencePosition(token);
                }
                return;
            }

            Handles.BeginGUI();
            Color badgeColor = isFinal
                ? new Color(0.78f, 0.16f, 0.16f, 0.98f)
                : isStarter || isRequired
                    ? new Color(0.95f, 0.7f, 0.08f, 0.98f)
                    : new Color(0.38f, 0.4f, 0.43f, 0.96f);
            Handles.color = badgeColor;
            if (isRequired) DrawSequenceStar(rect);
            else Handles.DrawSolidDisc(rect.center, Vector3.forward, rect.width * 0.5f);
            Handles.EndGUI();
            var numberStyle = new GUIStyle(EditorStyles.whiteBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = currentPosition >= 100 ? 8 : 10,
            };
            string label = currentPosition.ToString();
            string tooltip = isStarter && isFinal
                ? "This is both the starting area and final screen, and is required"
                : isStarter
                ? "This is the starting area for the game and is required"
                : isFinal
                    ? "This is the final screen and is required"
                    : isRequired
                        ? "This attraction is required for the game to function"
                    : "Click to enter a new position";
            GUI.Label(rect, new GUIContent(label, tooltip), numberStyle);
            if (allowEdit && GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none))
            {
                editingSequencePosition = token;
                sequencePositionText = currentPosition.ToString();
                EditorGUI.FocusTextInControl("SequencePositionField");
                Repaint();
            }
        }

        private static void DrawSequenceStar(Rect rect)
        {
            const int pointCount = 10;
            var points = new Vector3[pointCount];
            float outerRadius = Mathf.Min(rect.width, rect.height) * 0.62f;
            float innerRadius = outerRadius * 0.46f;
            for (int i = 0; i < pointCount; i++)
            {
                float angle = -Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                float radius = (i & 1) == 0 ? outerRadius : innerRadius;
                points[i] = new Vector3(
                    rect.center.x + Mathf.Cos(angle) * radius,
                    rect.center.y + Mathf.Sin(angle) * radius,
                    0f);
            }

            // Draw as ten center triangles so the concave five-point outline
            // remains exact; Handles.DrawAAConvexPolygon expects convex input.
            Vector3 center = rect.center;
            for (int i = 0; i < pointCount; i++)
                Handles.DrawAAConvexPolygon(center, points[i], points[(i + 1) % pointCount]);
        }

        private void CommitSequencePosition(string token)
        {
            if (int.TryParse(sequencePositionText, out int requested))
            {
                requested = Mathf.Max(1, requested);
                if (token.StartsWith("arena:", StringComparison.Ordinal))
                {
                    string[] parts = token.Split(':');
                    if (parts.Length == 4
                        && int.TryParse(parts[1], out int widthFeet)
                        && int.TryParse(parts[2], out int lengthFeet))
                    {
                        ArenaPackageStore.Bucket bucket = arenaLayout?.buckets?.FirstOrDefault(item =>
                            item != null && item.widthFeet == widthFeet
                            && item.lengthFeet == lengthFeet);
                        int from = bucket?.guids?.IndexOf(parts[3]) ?? -1;
                        if (from >= 0)
                        {
                            BeginSequenceChange("Change Arena Priority");
                            MoveListItem(bucket.guids, from,
                                Mathf.Clamp(requested - 1, 0, bucket.guids.Count - 1));
                            SaveArenaLayout();
                        }
                    }
                }
                else
                {
                    BeginSequenceChange("Change Sequence Position");
                    if (token.StartsWith("w:", StringComparison.Ordinal))
                    {
                        int from = sequenceLayout.items.FindIndex(x => x.IsWorld && x.id == token.Substring(2));
                        int offset = organizerMode == OrganizerMode.Adventure ? 2 : 1;
                        if (from >= 0) MoveListItem(sequenceLayout.items, from,
                            Mathf.Clamp(requested - offset, 0, sequenceLayout.items.Count - 1));
                    }
                    else if (token.StartsWith("p:", StringComparison.Ordinal))
                    {
                        string placementId = token.Substring(2);
                        int top = sequenceLayout.items.FindIndex(x => !x.IsWorld && x.id == placementId);
                        int offset = organizerMode == OrganizerMode.Adventure ? 2 : 1;
                        if (top >= 0) MoveListItem(sequenceLayout.items, top,
                            Mathf.Clamp(requested - offset, 0, sequenceLayout.items.Count - 1));
                        else
                        {
                            var world = sequenceLayout.items.FirstOrDefault(x => x.IsWorld
                                && (x.attractionIds ?? new List<string>()).Contains(placementId));
                            if (world != null)
                            {
                                int from = world.attractionIds.IndexOf(placementId);
                                MoveListItem(world.attractionGuids, from, Mathf.Clamp(requested - 1, 0, world.attractionGuids.Count - 1));
                                MoveListItem(world.attractionIds, from, Mathf.Clamp(requested - 1, 0, world.attractionIds.Count - 1));
                            }
                        }
                    }
                    SaveSequenceLayout();
                }
            }
            editingSequencePosition = null;
            sequencePositionText = "";
            GUI.FocusControl(null);
        }

        private void DrawSequenceDrag(Rect rect, string source)
        {
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                pendingSequenceDrag = source;
                pendingSequenceDragStart = evt.mousePosition;
                sequenceDragWasStarted = false;
            }
            else if (evt.type == EventType.MouseDrag
                && pendingSequenceDrag == source
                && Vector2.Distance(pendingSequenceDragStart, evt.mousePosition) >= 3f)
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(SequenceDragKey, source);
                lastSequenceDropSource = null;
                lastSequenceDropTarget = null;
                lastSequenceDropRect = default;
                DragAndDrop.StartDrag(source.StartsWith("w:", StringComparison.Ordinal)
                    || source.StartsWith("lw:", StringComparison.Ordinal)
                    ? "Move Group" : "Move Park Asset");
                pendingSequenceDrag = null;
                sequenceDragWasStarted = true;
                evt.Use();
            }
            else if (evt.rawType == EventType.MouseUp
                && (pendingSequenceDrag == source || sequenceDragWasStarted))
            {
                pendingSequenceDrag = null;
                // Keep the drag-click suppression true for the rest of this IMGUI
                // event; later cards have not drawn yet and must not open Preview.
                EditorApplication.delayCall += () => sequenceDragWasStarted = false;
            }
        }

        private void HandleTopLevelCardDrop(Rect rect, string anchor)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source)) return;
            bool after = Event.current.mousePosition.x >= rect.center.x;
            HandleSequenceDrop(rect, (after ? "top-after|" : "top-before|") + anchor);
            DrawSequenceInsertionCue(rect, after, true);
        }

        private void DrawTopLevelInsertionZone(string target)
        {
            Rect zone = GUILayoutUtility.GetRect(0f, 2f, GUILayout.ExpandWidth(true));
            Rect hitZone = new Rect(zone.x, zone.center.y - 6f, zone.width, 12f);
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source)) return;

            if (hitZone.Contains(Event.current.mousePosition) && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(zone.x, zone.center.y - 1.5f, zone.width, 3f),
                    new Color(0.2f, 0.7f, 1f, 0.95f));
            HandleSequenceDrop(hitZone, target);
        }

        private void HandleWorldCardDrop(Rect rect, string worldId, int childIndex, string targetToken)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (source != null && source.StartsWith("w:", StringComparison.Ordinal)) return;
            bool after = Event.current.mousePosition.x >= rect.center.x;
            HandleSequenceItemDrop(rect, $"world|{worldId}|{childIndex + (after ? 1 : 0)}");
            DrawSequenceInsertionCue(rect, after, false);
        }

        private void HandleSequenceItemDrop(Rect rect, string target)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source) || source.StartsWith("w:", StringComparison.Ordinal)
                || source.StartsWith("lw:", StringComparison.Ordinal)) return;
            HandleSequenceDrop(rect, target);
        }

        private void DrawWorldEndInsertionCue(Rect rect)
        {
            if (Event.current.type != EventType.Repaint || !rect.Contains(Event.current.mousePosition)) return;
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source) || source.StartsWith("w:", StringComparison.Ordinal)) return;
            EditorGUI.DrawRect(new Rect(rect.x + 2f, rect.yMax - 4f, rect.width - 4f, 3f),
                new Color(0.2f, 0.7f, 1f, 0.95f));
        }

        private bool SourceComesBeforeTarget(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target) return false;

            if (source.StartsWith("w:", StringComparison.Ordinal))
            {
                string worldId = source.Substring(2);
                int sourceTop = sequenceLayout.items.FindIndex(item => item.IsWorld && item.id == worldId);
                int targetTop = sequenceLayout.items.FindIndex(item => ContentSequenceStore.PlacementToken(item) == target);
                return sourceTop >= 0 && targetTop >= 0 && sourceTop < targetTop;
            }

            if (source.StartsWith("p:", StringComparison.Ordinal) && target.StartsWith("p:", StringComparison.Ordinal))
            {
                List<string> flattened = sequenceLayout.items.SelectMany(item => item.IsWorld
                    ? (IEnumerable<string>)(item.attractionIds ?? new List<string>())
                    : new[] { item.id }).ToList();
                int sourceIndex = flattened.IndexOf(source.Substring(2));
                int targetIndex = flattened.IndexOf(target.Substring(2));
                return sourceIndex >= 0 && targetIndex >= 0 && sourceIndex < targetIndex;
            }
            return false;
        }

        private void DrawSequenceInsertionCue(Rect rect, bool after, bool acceptsWorld)
        {
            if (Event.current.type != EventType.Repaint || !rect.Contains(Event.current.mousePosition)) return;
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (string.IsNullOrEmpty(source) || (!acceptsWorld && source.StartsWith("w:", StringComparison.Ordinal))) return;
            float x = after ? rect.xMax - 2f : rect.x;
            EditorGUI.DrawRect(new Rect(x, rect.y, 3f, rect.height), new Color(0.2f, 0.7f, 1f, 0.95f));
        }

        private void HandleSequenceDrop(Rect rect, string target)
        {
            Event evt = Event.current;
            if ((evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) || !rect.Contains(evt.mousePosition)) return;
            if (!(DragAndDrop.GetGenericData(SequenceDragKey) is string source)) return;
            DragAndDrop.visualMode = source.StartsWith("a:", StringComparison.Ordinal)
                || source.StartsWith("lw:", StringComparison.Ordinal)
                ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Move;
            lastSequenceDropSource = source;
            lastSequenceDropTarget = target;
            lastSequenceDropRect = rect;
            lastSequenceDropTime = EditorApplication.timeSinceStartup;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                CommitSequenceDrop(source, target);
            }
            evt.Use();
        }

        private void CommitCachedSequenceDropOnRelease()
        {
            Event evt = Event.current;
            if (evt.rawType != EventType.DragPerform && evt.rawType != EventType.MouseUp) return;
            if (!(DragAndDrop.GetGenericData(SequenceDragKey) is string source)) return;
            if (!string.Equals(source, lastSequenceDropSource, StringComparison.Ordinal)
                || string.IsNullOrEmpty(lastSequenceDropTarget)
                || !lastSequenceDropRect.Contains(evt.mousePosition)
                || EditorApplication.timeSinceStartup - lastSequenceDropTime > 2d) return;

            if (evt.rawType == EventType.DragPerform) DragAndDrop.AcceptDrag();
            CommitSequenceDrop(source, lastSequenceDropTarget);
            if (evt.type != EventType.Used) evt.Use();
        }

        private void CommitSequenceDrop(string source, string target)
        {
            if (source.StartsWith("arena:", StringComparison.Ordinal))
                QueueArenaMove(source, target);
            else if (target.StartsWith("library:", StringComparison.Ordinal))
                QueueLibraryMove(source, target.Substring("library:".Length));
            else QueueSequenceMove(source, target);
            DragAndDrop.SetGenericData(SequenceDragKey, null);
            lastSequenceDropSource = null;
            lastSequenceDropTarget = null;
            lastSequenceDropRect = default;
        }

        private void QueueArenaMove(string source, string target)
        {
            if (!TryParseArenaPositionToken(source, out int sourceWidth,
                    out int sourceLength, out string sourceGuid)) return;
            bool after = target.StartsWith("arena-after:", StringComparison.Ordinal);
            bool before = target.StartsWith("arena-before:", StringComparison.Ordinal);
            bool append = target.StartsWith("arena-append:", StringComparison.Ordinal);
            if (!after && !before && !append) return;
            string[] parts = target.Split(':');
            int expectedParts = append ? 3 : 4;
            if (parts.Length != expectedParts || !int.TryParse(parts[1], out int targetWidth)
                || !int.TryParse(parts[2], out int targetLength)) return;
            string targetGuid = append ? null : parts[3];
            string scheduledContentId = contentId;
            EditorApplication.delayCall += () =>
            {
                if (this == null || arenaLayout == null
                    || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                ArenaPackageStore.Data next = ArenaPackageStore.Clone(arenaLayout);
                bool moved = append
                    ? ArenaPackageStore.MoveToBucketEnd(next, sourceWidth, sourceLength,
                        targetWidth, targetLength, sourceGuid)
                    : ArenaPackageStore.MoveRelative(next, sourceWidth, sourceLength,
                        targetWidth, targetLength, sourceGuid, targetGuid, after);
                if (!moved) return;
                BeginSequenceChange(sourceWidth == targetWidth && sourceLength == targetLength
                    ? "Change Arena Priority" : "Override Arena Size");
                arenaLayout = next;
                SaveArenaLayout();
            };
        }

        private void HandleWorldHeaderDrop(Rect rect, string worldId, int topIndex)
        {
            string source = DragAndDrop.GetGenericData(SequenceDragKey) as string;
            if (source != null && source.StartsWith("lw:", StringComparison.Ordinal)) return;
            string target;
            if (source != null && source.StartsWith("w:", StringComparison.Ordinal))
            {
                string anchor = "w:" + worldId;
                bool after = SourceComesBeforeTarget(source, anchor);
                target = (after ? "top-after|" : "top-before|") + anchor;
                if (rect.Contains(Event.current.mousePosition) && Event.current.type == EventType.Repaint)
                {
                    float y = after ? rect.yMax - 2f : rect.y;
                    EditorGUI.DrawRect(new Rect(rect.x, y, rect.width, 3f),
                        new Color(0.2f, 0.7f, 1f, 0.95f));
                }
            }
            else target = "world|" + worldId + "|0";
            HandleSequenceDrop(rect, target);
        }

        private void QueueSequenceMove(string source, string target)
        {
            // Changing the serialized collection during an IMGUI traversal leaves
            // the remainder of that frame holding stale indices. That was the cause
            // of Worlds apparently scrambling attractions when dropped.
            string scheduledContentId = contentId;
            EditorApplication.delayCall += () =>
            {
                if (this == null || sequenceLayout == null
                    || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                ContentSequenceStore.Data nextLayout = ContentSequenceStore.Clone(sequenceLayout);
                if (source.StartsWith("lw:", StringComparison.Ordinal))
                {
                    if (target.StartsWith("world|", StringComparison.Ordinal)) return;
                    string groupId = source.Substring(3);
                    ContentSequenceStore.Entry libraryGroup = libraryLayout?.items
                        .FirstOrDefault(x => x.IsWorld && x.id == groupId);
                    if (libraryGroup == null) return;
                    var allowed = new HashSet<string>(ParkAssetEntries().Select(e => e.guid), StringComparer.Ordinal);
                    var copiedGuids = (libraryGroup.attractionGuids ?? new List<string>())
                        .Where(allowed.Contains).ToList();
                    string packageGroupId = Guid.NewGuid().ToString("N");
                    var copy = new ContentSequenceStore.Entry
                    {
                        kind = "world",
                        id = packageGroupId,
                        sourceGroupId = libraryGroup.id,
                        name = libraryGroup.name,
                        attractionGuids = copiedGuids,
                        attractionIds = copiedGuids.Select(_ => Guid.NewGuid().ToString("N")).ToList(),
                    };
                    nextLayout.items.Add(copy);
                    source = "w:" + packageGroupId;
                }
                else if (source.StartsWith("a:", StringComparison.Ordinal))
                {
                    string guid = source.Substring(2);
                    // Keep incompatible assets visible in Sequence packages so the
                    // organizer can explain why they are excluded at compile time.
                    if (!ParkAssetEntries().Any(entry => entry.guid == guid)) return;
                }
                if (!ContentSequenceStore.TryMove(nextLayout, source, target)) return;
                BeginSequenceChange(source.StartsWith("w:", StringComparison.Ordinal)
                    ? "Place Group" : source.StartsWith("a:", StringComparison.Ordinal)
                        ? "Place Park Asset" : "Move Park Asset");
                sequenceLayout = nextLayout;
                SaveSequenceLayout();
            };
        }

        private void QueueLibraryMove(string source, string target)
        {
            string scheduledContentId = contentId;
            EditorApplication.delayCall += () =>
            {
                if (this == null || libraryLayout == null
                    || !string.Equals(contentId, scheduledContentId, StringComparison.Ordinal)) return;
                string normalized = source.StartsWith("lw:", StringComparison.Ordinal)
                    ? "w:" + source.Substring(3) : source;
                ContentSequenceStore.Data next = ContentSequenceStore.Clone(libraryLayout);
                if (!ContentSequenceStore.TryMove(next, normalized, target)) return;
                BeginSequenceChange(normalized.StartsWith("w:", StringComparison.Ordinal)
                    ? "Reorder Group" : "Reorder Park Asset");
                libraryLayout = next;
                SaveLibraryLayout();
            };
        }

        private void BeginSequenceChange(string undoName)
        {
            Undo.RegisterCompleteObjectUndo(this, undoName);
        }

        private void OnSequenceUndoRedo()
        {
            if (organizerMode == OrganizerMode.Arena)
            {
                if (arenaLayout != null && !string.IsNullOrEmpty(contentId))
                    ArenaPackageStore.Save(contentId, arenaLayout);
                Repaint();
                return;
            }
            if (sequenceLayout == null || string.IsNullOrEmpty(contentId)) return;
            if (!string.Equals(sequenceUndoContentId, contentId, StringComparison.Ordinal))
            {
                RefreshContentRoots();
                return;
            }
            ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);
            Repaint();
        }

        private void SaveSequenceLayout()
        {
            ContentSequenceStore.Save(contentId, sequenceLayout, organizerMode == OrganizerMode.Sequence);
            EditorUtility.SetDirty(this);
            Repaint();
        }

        private static void CompileDreamSequencePackage(string targetContentId)
        {
            // This saves only an addressable recipe. The package root is built
            // from its editable parts when the Sequence is actually loaded.
            DreamSequencePackageCompiler.Compile(targetContentId);
            DreamParkPackageCompiler.Compile(targetContentId);
        }

        // ── Badges ──────────────────────────────────────────────────────
        //
        // Same chrome as the Attraction/Prop/Player groups — an EditorPrefs-
        // backed foldout over a wrapping card grid — but backed by
        // BadgeStore.Entry rather than by contentRoots, because a badge is a
        // data record (id / title / description / icon), not a prefab. Trying to
        // push it through ContentRootKind would have meant inventing a fake
        // "root" with no asset behind it.
        //
        // The cards are WIDER than the prefab cards on purpose: a prefab card is
        // a thumbnail plus a name, a badge card is three editable fields, and at
        // the 110px prefab width the id field would be too narrow to read the id
        // it is supposed to be preventing you from mistyping.
        private const float BadgeCardWidth = 320f;
        private const float BadgeCardHeight = 90f;
        private const float BadgeIconSize = 64f;

        private void RefreshBadges()
        {
            // Never lose a half-typed title to a background project change.
            FlushBadgeDraft();

            badgesContentId = contentId;
            badgeAddRequested = false;
            badgeRemoveIndex = -1;
            badgesDirty = false;
            badgeScan = null;
            badgeAttribution = null;
            badgesByAssetPath = new Dictionary<string, List<BadgeStore.Entry>>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(contentId))
            {
                badges = new List<BadgeStore.Entry>();
                return;
            }

            try
            {
                badgeScan = BadgeLuaScanner.Scan(contentId);
                badges = BadgeStore.Merge(BadgeStore.Load(contentId), badgeScan.discoveries);
            }
            catch (Exception e)
            {
                // A scan failure must not cost the developer their saved draft —
                // the draft is the part with hand-typed titles in it.
                Debug.LogWarning("[Badges] Lua scan failed: " + e.Message);
                badges = BadgeStore.Load(contentId);
            }

            try
            {
                // Independent of the pre-upload report cache on purpose: the
                // "Awarded by" tooltip should be current the moment a rescan
                // runs, not wait on the (debounced) advisory check pass.
                badgeAttribution = BadgeAttributionScanner.Scan(contentId, PreUploadChecks.ContentRootScanner.Scan(contentId));
                RebuildBadgePreviewIndex();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Badges] Attribution scan failed: " + e.Message);
                badgeAttribution = null;
            }
        }

        private void RebuildBadgePreviewIndex()
        {
            badgesByAssetPath = BuildBadgePreviewIndex(badges, badgeAttribution);
        }

        internal static Dictionary<string, List<BadgeStore.Entry>> BuildBadgePreviewIndex(
            IReadOnlyList<BadgeStore.Entry> badgeEntries, BadgeAttributionScanner.Result attribution)
        {
            var result = new Dictionary<string, List<BadgeStore.Entry>>(StringComparer.Ordinal);
            if (attribution == null) return result;

            var byId = new Dictionary<string, BadgeStore.Entry>(StringComparer.Ordinal);
            if (badgeEntries != null)
                foreach (BadgeStore.Entry badge in badgeEntries)
                    if (badge != null && !string.IsNullOrEmpty(badge.badgeId)
                        && !byId.ContainsKey(badge.badgeId))
                        byId.Add(badge.badgeId, badge);

            foreach (var award in attribution.awardedByRoot.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(award.Key) || award.Value == null) continue;
                // An attribution can exist before the badge has a completed
                // card. Still show a named placeholder for the Lua id.
                if (!byId.TryGetValue(award.Key, out BadgeStore.Entry badge))
                    badge = new BadgeStore.Entry { badgeId = award.Key };
                foreach (var root in award.Value)
                {
                    if (root == null || string.IsNullOrEmpty(root.assetPath)
                        || root.kind == PreUploadChecks.ContentRootKindPublic.Player) continue;
                    if (!result.TryGetValue(root.assetPath, out List<BadgeStore.Entry> assetBadges))
                    {
                        assetBadges = new List<BadgeStore.Entry>();
                        result.Add(root.assetPath, assetBadges);
                    }
                    if (!assetBadges.Any(existing => existing.badgeId == award.Key))
                        assetBadges.Add(badge);
                }
            }
            return result;
        }

        // "Awarded by Fountain Quest (Attraction), the Player rig" — or "" when
        // nothing (yet) awards this id, in which case the card falls back to
        // whatever preUploadBadgeAwards says instead.
        private string AwardedBySummary(string badgeId)
        {
            if (badgeAttribution == null || string.IsNullOrEmpty(badgeId)) return "";

            var parts = new List<string>();
            if (badgeAttribution.awardedByRoot.TryGetValue(badgeId, out var roots))
            {
                foreach (var r in roots) parts.Add(r.name + " (" + r.KindLabel + ")");
            }
            if (badgeAttribution.awardedByPlayer.Contains(badgeId)) parts.Add("the Player rig");

            return parts.Count == 0 ? "" : "Awarded by " + string.Join(", ", parts);
        }

        private void ApplyQueuedBadgeMutations()
        {
            if (badgeAddRequested)
            {
                badgeAddRequested = false;
                badges.Add(new BadgeStore.Entry());
                badgesDirty = true;
                RebuildBadgePreviewIndex();
                Repaint();
            }

            if (badgeRemoveIndex >= 0)
            {
                if (badgeRemoveIndex < badges.Count) badges.RemoveAt(badgeRemoveIndex);
                badgeRemoveIndex = -1;
                badgesDirty = true;
                RebuildBadgePreviewIndex();
                Repaint();
            }
        }

        // Writes .badges.json for whichever content the list currently belongs
        // to — badgesContentId, NOT contentId. They differ for exactly one frame
        // when the developer changes the dropdown, and saving to the new id there
        // would copy the old package's badges into the new one.
        private void FlushBadgeDraft()
        {
            if (!badgesDirty) return;
            badgesDirty = false;
            if (string.IsNullOrEmpty(badgesContentId)) return;
            BadgeStore.Save(badgesContentId, badges);
        }

        private void DrawBadgesGroup()
        {
            GUILayout.Space(4);

            bool newFold = EditorGUILayout.Foldout(foldBadges, $"Badges ({badges.Count})", true);
            if (newFold != foldBadges)
            {
                foldBadges = newFold;
                EditorPrefs.SetBool(ParkAssetsBadgesPrefKey, foldBadges);
            }
            if (!foldBadges) return;

            EditorGUI.indentLevel++;

            if (badges.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                EditorGUILayout.HelpBox(
                    "No badges yet. Call dp.profile.awardBadge(\"some_id\") from your Lua and it appears here " +
                    "automatically with the ID filled in — or press Add Badge to define one by hand.",
                    MessageType.Info);
                GUILayout.EndHorizontal();
            }
            else
            {
                float panelWidth = Mathf.Max(position.width - 24f, BadgeCardWidth);
                int perRow = Mathf.Max(1, Mathf.FloorToInt((panelWidth + CardSpacing) / (BadgeCardWidth + CardSpacing)));

                for (int i = 0; i < badges.Count; i += perRow)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUI.indentLevel * 12f);
                    for (int j = 0; j < perRow && i + j < badges.Count; j++)
                    {
                        DrawBadgeCard(badges[i + j], i + j);
                        if (j < perRow - 1) GUILayout.Space(CardSpacing);
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(CardSpacing);
                }
            }

            // What the scan could NOT work out. Reported rather than swallowed:
            // a badge id built by concatenation or handed through a helper is
            // invisible to a text scan, and a developer who sees nothing appear
            // would reasonably conclude the feature is broken rather than that
            // their id is out of reach.
            if (badgeScan != null && badgeScan.unresolved.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 12f);
                var names = badgeScan.unresolved
                    .Take(4)
                    .Select(u => $"{Path.GetFileName(u.scriptPath)}: {u.expression}")
                    .ToArray();
                EditorGUILayout.HelpBox(
                    "Some badge calls name an ID this scan can't read (it isn't a literal or an @var), "
                        + "so you'll need to add those by hand:\n  "
                        + string.Join("\n  ", names)
                        + (badgeScan.unresolved.Count > names.Length
                            ? $"\n  ...and {badgeScan.unresolved.Count - names.Length} more"
                            : ""),
                    MessageType.Info);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 12f);

            if (GUILayout.Button(new GUIContent("Add Badge",
                    "Define a badge that isn't referenced from Lua yet. You can type its ID."),
                    GUILayout.Width(100), GUILayout.Height(22)))
            {
                badgeAddRequested = true;   // applied on the next Layout pass
            }

            if (GUILayout.Button(new GUIContent("Rescan Lua",
                    "Re-read this content folder's .lua/.lua.txt files and prefabs for badge IDs."),
                    GUILayout.Width(100), GUILayout.Height(22)))
            {
                RefreshBadges();
            }

            GUILayout.FlexibleSpace();

            // The push is deliberately available WITHOUT a full Upload Release.
            // Badge text is metadata on the backend, not bundle content, so
            // making a developer pay a multi-minute build to fix a typo in a
            // badge description would be the same mistake the logo re-upload
            // button exists to undo.
            using (new EditorGUI.DisabledScope(isPushingBadges || UploadsBlocked
                                               || string.IsNullOrEmpty(contentId) || badges.Count == 0))
            {
                if (GUILayout.Button(new GUIContent(
                        isPushingBadges ? "Pushing..." : "Push Badges to Portal",
                        "Saves every badge above to your developer portal (POST /admin/content/:id/badges/save). "
                            + "No build required."),
                        GUILayout.Width(160), GUILayout.Height(22)))
                {
                    PushBadges(interactive: true);
                }
            }
            GUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private void DrawBadgeCard(BadgeStore.Entry entry, int index)
        {
            Rect card = GUILayoutUtility.GetRect(BadgeCardWidth, BadgeCardHeight,
                GUILayout.Width(BadgeCardWidth), GUILayout.Height(BadgeCardHeight));

            EditorGUI.DrawRect(card, new Color(0f, 0f, 0f, 0.18f));

            // EditorGUI.* with an explicit Rect still offsets prefix labels by
            // the ambient indent level, which would push these fields off the
            // right edge of a card that is already exactly as wide as it needs
            // to be. The grid rows do their own indenting with GUILayout.Space.
            int prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 34f;

            var iconRect = new Rect(card.x + 6f, card.y + 6f, BadgeIconSize, BadgeIconSize);
            float fx = card.x + 6f + BadgeIconSize + 8f;
            float fw = card.xMax - fx - 6f;

            EditorGUI.BeginChangeCheck();

            var icon = (Texture2D)EditorGUI.ObjectField(
                iconRect,
                string.IsNullOrEmpty(entry.iconAssetPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Texture2D>(entry.iconAssetPath),
                typeof(Texture2D), false);

            var titleRect = new Rect(fx, card.y + 6f, fw, 18f);
            var idRect    = new Rect(fx, card.y + 28f, fw, 18f);
            var descRect  = new Rect(fx, card.y + 50f, fw, 18f);

            string newTitle = EditorGUI.TextField(titleRect, "Title", entry.name ?? "");

            // A locked id is drawn, not hidden: the developer needs to SEE the
            // string their Lua passes so they can confirm it's the badge they
            // meant. Disabled-and-visible reads as "this came from your code";
            // an empty or absent field would read as a bug.
            string newId;
            using (new EditorGUI.DisabledScope(entry.IdLocked))
            {
                newId = EditorGUI.TextField(
                    idRect,
                    new GUIContent("ID", entry.IdLocked
                        ? entry.discoveredIn + "\n\nThis ID comes from your Lua, so it can't be edited here — "
                          + "change it in the script and it updates on the next scan."
                        : "The ID your Lua passes to dp.profile.awardBadge(). Letters, numbers, _ and - only."),
                    entry.badgeId ?? "");
            }

            string newDesc = EditorGUI.TextField(descRect, "Desc", entry.description ?? "");

            if (EditorGUI.EndChangeCheck())
            {
                entry.name = newTitle;
                entry.description = newDesc;
                // Ignore any write to a locked field. DisabledScope already stops
                // the keyboard, but a scripted or accidental change must not be
                // able to break the id/Lua correspondence either.
                if (!entry.IdLocked) entry.badgeId = newId;
                entry.iconAssetPath = icon != null ? AssetDatabase.GetAssetPath(icon) : "";
                badgesDirty = true;
                RebuildBadgePreviewIndex();
            }

            var sourceRect = new Rect(fx, card.y + 70f, fw - 56f, 14f);
            GUI.Label(sourceRect,
                new GUIContent(
                    entry.IdLocked ? "● From your Lua" : "○ Added by hand",
                    entry.IdLocked ? entry.discoveredIn : "Not referenced from Lua in this content folder."),
                EditorStyles.miniLabel);

            // Readiness icon, top-right corner of the card: is this badge
            // actually awarded anywhere, and ready to ship? Backed by
            // BadgeAwardCheck (see PreUploadChecks/Checks/BadgeAwardCheck.cs)
            // rather than computed inline here, so the same finding also shows
            // up in the full Pre-Upload Checks popup and can be ignored per
            // badge through the same ignore-store every other check uses.
            //
            // GUI.Label, not GUI.Button: it consumes no control id, so it is
            // safe to draw even though its content (warning vs. checkmark vs.
            // "not scanned yet") can legitimately differ between this frame's
            // Layout and Repaint passes — unlike GUI.Button, there is no id
            // stream here to shift.
            if (!string.IsNullOrEmpty(entry.badgeId))
            {
                var statusRect = new Rect(card.xMax - 20f, card.y + 4f, 16f, 16f);

                KeyValuePair<PreUploadChecks.CheckSeverity, string> award = default;
                bool hasWarning = preUploadBadgeAwards != null
                                && preUploadBadgeAwards.TryGetValue(entry.badgeId, out award);

                if (hasWarning)
                {
                    var warnIcon = EditorGUIUtility.IconContent("console.warnicon.sml");
                    GUI.Label(statusRect, new GUIContent(warnIcon != null ? warnIcon.image : null, award.Value));
                }
                else if (!preUploadReportEverBuilt)
                {
                    // Neither "warning" nor "clean" is known yet — the advisory
                    // scan is debounced ~1.5s behind opening the panel. Say so
                    // rather than guess.
                    GUI.Label(statusRect, new GUIContent("…",
                        "Pre-upload checks haven't run for this content yet."), EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    string summary = AwardedBySummary(entry.badgeId);
                    var checkStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 13,
                    };
                    checkStyle.normal.textColor = new Color(0.35f, 0.82f, 0.4f);
                    GUI.Label(statusRect, new GUIContent("✓",
                        string.IsNullOrEmpty(summary) ? "Awarded somewhere in this package's scripts." : summary),
                        checkStyle);
                }
            }

            // Removing a discovered badge would be a lie: the next scan puts it
            // straight back, because the reason it is here is a line of the
            // developer's own code. Delete the call, then rescan.
            var removeRect = new Rect(card.xMax - 56f, card.y + 69f, 50f, 16f);
            using (new EditorGUI.DisabledScope(entry.IdLocked))
            {
                if (GUI.Button(removeRect,
                        new GUIContent("Remove", entry.IdLocked
                            ? "This badge is referenced from your Lua. Remove the call and rescan."
                            : "Remove this badge card. (It is not deleted from the developer portal.)"),
                        EditorStyles.miniButton))
                {
                    badgeRemoveIndex = index;   // applied on the next Layout pass
                }
            }

            EditorGUIUtility.labelWidth = prevLabelWidth;
            EditorGUI.indentLevel = prevIndent;
        }

        private void PushBadges(bool interactive)
        {
            if (string.IsNullOrEmpty(contentId) || isPushingBadges) return;

            // Save the draft first so what lands on the backend and what is in
            // .badges.json can never disagree about what was pushed.
            badgesDirty = true;
            FlushBadgeDraft();

            isPushingBadges = true;
            var snapshot = new List<BadgeStore.Entry>(badges);
            BadgeUploader.UploadAll(contentId, snapshot, interactive, report =>
            {
                isPushingBadges = false;
                Repaint();
            });
        }

        // Fire-and-forget push that rides the normal upload flow, exactly like
        // UploadLogoImage: it must never fail or delay a content upload, because
        // the bundles are the release and the badge text is metadata that can be
        // re-pushed from the panel in one click.
        private void PushBadgesSilently(string idForUpload, string sourceContentId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(idForUpload)) return;
                string localContentId = string.IsNullOrEmpty(sourceContentId) ? idForUpload : sourceContentId;
                var toPush = BadgeStore.Load(localContentId);
                if (toPush == null || toPush.Count == 0) return;
                BadgeUploader.UploadAll(idForUpload, toPush, interactive: false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Badges] upload skipped: " + e.Message);
            }
        }

        // 5 seconds is more than enough for any prefab Unity intends to
        // render. Past that, we accept the fallback icon and stop polling.
        // Empty container prefabs (LevelTemplate-only, no mesh) never
        // produce a rich AssetPreview at all — without this timeout we'd
        // repaint forever waiting for something that's never coming.
        private const double PreviewPollTimeoutSeconds = 5.0;

        private Rect DrawCard(ContentRootEntry entry, bool sequenceInteraction = false,
            bool showAssetWarning = true)
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

            // Pre-upload warning badge, top-right of the thumbnail.
            //
            // badgeRect is computed UNCONDITIONALLY and the badge map is a per-frame
            // snapshot: GUI.Button consumes a control id, so a badge that exists in the
            // Layout pass but not the Repaint pass (or vice versa) shifts the id stream
            // and corrupts every control drawn after it in this card.
            //
            // IMGUI paints in draw order, so this must come after GUI.DrawTexture to
            // sit on top of the thumbnail.
            var badgeRect = new Rect(imgRect.xMax - 20f, imgRect.y + 2f, 18f, 18f);
            // `= default` is load-bearing: the `&&` below short-circuits, so the
            // compiler cannot prove TryGetValue ran and reports CS0165 on badge.Key.
            KeyValuePair<PreUploadChecks.CheckSeverity, string> badge = default;
            bool hasBadge = showAssetWarning
                         && preUploadBadges != null
                         && preUploadBadges.TryGetValue(entry.assetPath, out badge);
            if (hasBadge)
            {
                var badgeIcon = badge.Key == PreUploadChecks.CheckSeverity.Blocking
                    ? EditorGUIUtility.IconContent("console.erroricon.sml")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                if (GUI.Button(badgeRect, new GUIContent(badgeIcon != null ? badgeIcon.image : null, badge.Value),
                               EditorStyles.iconButton))
                {
                    PreUploadChecks.PreUploadChecksPopup.ShowForAsset(this, contentId, entry.assetPath);
                }
            }

            // Awarded badge artwork sits in the opposite corner from the
            // pre-upload warning. Labels (not buttons) keep the card's control
            // ids stable and leave the whole tile clickable for Preview.
            if (entry.kind != ContentRootKind.Player
                && badgesByAssetPath.TryGetValue(entry.assetPath, out List<BadgeStore.Entry> awarded))
                DrawAwardedBadgeIcons(imgRect, awarded);

            // Two-line label: name on top (bold-ish), kind on bottom.
            var nameStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
                fontStyle = FontStyle.Bold,
            };
            GUI.Label(labelRect, new GUIContent(entry.name, $"Click to open the Preview Editor\n{entry.assetPath}"), nameStyle);

            // Click anywhere on the card to open the Preview Editor for this
            // prefab, where the camera angle and zoom of its generated preview
            // can be tuned and saved. Also pings the underlying prefab in the
            // Project window for context.
            //
            // The badge is excluded by rect rather than by relying on event
            // consumption. GUI.Button consumes the event on MouseDown and returns true
            // on MouseUp — two different passes — so a Use() inside its true branch
            // would never run during the pass this handler reads. Rect exclusion is
            // event-phase-independent.
            EventType previewClickEvent = sequenceInteraction ? EventType.MouseUp : EventType.MouseDown;
            var sequenceOrderRect = new Rect(cardRect.x + 2f, cardRect.y + 2f, 30f, 30f);
            if (Event.current.type == previewClickEvent
                && Event.current.button == 0
                && (!sequenceInteraction || !sequenceDragWasStarted)
                && cardRect.Contains(Event.current.mousePosition)
                && (!sequenceInteraction || !sequenceOrderRect.Contains(Event.current.mousePosition))
                && !(hasBadge && badgeRect.Contains(Event.current.mousePosition)))
            {
                PreviewEditorWindow.Open(contentId, entry.assetPath, entry.name, entry.subLabel);
                var asset = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
                if (asset != null) EditorGUIUtility.PingObject(asset);
                Event.current.Use();
            }
            return cardRect;
        }

        private static void DrawAwardedBadgeIcons(Rect imageRect, IReadOnlyList<BadgeStore.Entry> awarded)
        {
            if (awarded == null || awarded.Count == 0) return;
            const float iconSize = 25f;
            const float gap = 2f;
            int visible = Mathf.Min(awarded.Count, 3);
            int actualIcons = awarded.Count > 3 ? 2 : visible;
            float x = imageRect.xMax - 3f - visible * iconSize - (visible - 1) * gap;
            float y = imageRect.yMax - iconSize - 3f;
            var iconStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
            };
            iconStyle.normal.textColor = new Color(1f, 0.82f, 0.25f);

            for (int i = 0; i < actualIcons; i++)
            {
                BadgeStore.Entry badge = awarded[i];
                Rect slot = new Rect(x + i * (iconSize + gap), y, iconSize, iconSize);
                EditorGUI.DrawRect(slot, new Color(0.95f, 0.7f, 0.2f, 0.95f));
                Rect inside = new Rect(slot.x + 1f, slot.y + 1f, slot.width - 2f, slot.height - 2f);
                EditorGUI.DrawRect(inside, new Color(0.15f, 0.14f, 0.13f, 0.96f));
                Texture2D icon = string.IsNullOrEmpty(badge.iconAssetPath)
                    ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(badge.iconAssetPath);
                if (icon != null) GUI.DrawTexture(inside, icon, ScaleMode.ScaleToFit);
                else GUI.Label(inside, "★", iconStyle);
                GUI.Label(slot, new GUIContent("", BadgePreviewTooltip(badge)), GUIStyle.none);
            }

            if (awarded.Count <= 3) return;
            Rect more = new Rect(x + 2f * (iconSize + gap), y, iconSize, iconSize);
            EditorGUI.DrawRect(more, new Color(0.15f, 0.14f, 0.13f, 0.96f));
            string tooltip = string.Join("\n", awarded.Skip(2).Select(BadgePreviewTooltip));
            GUI.Label(more, new GUIContent("+" + (awarded.Count - 2), tooltip), iconStyle);
        }

        private static string BadgePreviewTooltip(BadgeStore.Entry badge)
        {
            string title = string.IsNullOrWhiteSpace(badge.name) ? badge.badgeId : badge.name;
            return "Awards badge: " + title + " (" + badge.badgeId + ")";
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

            string savedContentId = EditorPrefs.GetString(ProjectContentIdPrefKey, "");
            // Migrate the original machine-global preference only when it
            // names content in this project. Other Unity projects must not
            // overwrite one another's uploader selection.
            if (string.IsNullOrEmpty(savedContentId))
                savedContentId = EditorPrefs.GetString(ContentIdPrefKey, "");
            if (!string.IsNullOrEmpty(savedContentId))
            {
                int savedIndex = contentIdOptions.IndexOf(savedContentId);
                if (savedIndex >= 0)
                {
                    contentIdIndex = savedIndex;
                    contentId = contentIdOptions[contentIdIndex];
                    SaveContentIdSelection();
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

            EditorPrefs.SetString(ProjectContentIdPrefKey, contentId);
        }

        internal static string ContentIdPrefKeyForProject(string assetsPath) =>
            ContentIdPrefKey + "." + Hash128.Compute(Path.GetFullPath(assetsPath)).ToString();

        private static string ProjectContentIdPrefKey => ContentIdPrefKeyForProject(Application.dataPath);

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
            betaLatestPublishedVersionNumber = null;
            betaContentDirectorySnapshot = null;
            if (!isUploading) activeUploadTarget = ContentUploadTarget.Release;

            if (string.IsNullOrEmpty(contentId) || !AuthAPI.isLoggedIn)
            {
                latestPublishedVersionNumber = null;
                latestContentDirectorySnapshot = null;
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
                    latestContentDirectorySnapshot = null;
                    Repaint();
                    return;
                }

                latestContentDirectorySnapshot = response.json;
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
                RefreshPatchEstimate();
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
                // uploaded; if the user later decides to ship, Upload Release
                // does its own clean build with the real per-platform URLs.
                BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
                BuildTargetGroup activeGroup = BuildPipeline.GetBuildTargetGroup(activeTarget);
                string inspectUrl = $"{DreamParkAPI.baseUrl}/app/content/addressables/{contentId}/inspect/{activeTarget}";
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
                    Debug.LogError($"[DreamPark] Add collaborator failed (status={response?.statusCode}): {err}");
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
                    Debug.LogError($"[DreamPark] Remove collaborator failed (status={response?.statusCode}): {err}");
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

        // Kept only so an existing integration that calls this still compiles. DreamPark
        // has been passwordless since July 2026 — there is no password to pass — and the
        // in-editor sign-in path is AuthPopup (email, then an emailed 6-digit code).
        // Obsolete rather than deleted because it is public API on a public panel type;
        // being Obsolete itself is also what stops the AuthAPI.Login call below (now
        // Obsolete too) from raising a warning here.
        [Obsolete("DreamPark is passwordless as of July 2026 — sign in via AuthPopup, or call AuthAPI.RequestLoginCode/VerifyLoginCode.")]
        public void Login(string email, string password)
        {
            AuthAPI.Login(email, password, (success, response) =>
            {
                if (success)
                {
                    Debug.Log("Login successful.");
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
                    Debug.Log("Logout successful.");
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

                string sourceContentId = contentId;
                string uploadContentId = ActiveUploadContentId;
                ContentUploadTarget uploadTarget = activeUploadTarget;
                if (string.IsNullOrEmpty(uploadContentId))
                {
                    Debug.LogError("❌ Upload target ID could not be derived.");
                    CompleteUploadStatus(false, "Upload target ID could not be derived.");
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

                Debug.Log($"🚀 Uploading source {sourceContentId} to target {uploadContentId}...");

                ContentAPI.GetContent(uploadContentId, (exists, response) =>
                {
                    Action uploadBuiltContent = () =>
                    {
                        var contentDirectory = response != null ? response.json : null;
                        if (uploadTarget == ContentUploadTarget.Beta)
                            betaContentDirectorySnapshot = contentDirectory;
                        else
                            latestContentDirectorySnapshot = contentDirectory;
                        // Note the `.list != null` guard: a freshly-registered
                        // content can come back with `versions: null`, where
                        // HasField() is true but the JSON node has no list.
                        var versionNumber = contentDirectory != null && contentDirectory.HasField("content")
                                            && contentDirectory.GetField("content").HasField("versions")
                                            && contentDirectory.GetField("content").GetField("versions").list != null
                            ? contentDirectory.GetField("content").GetField("versions").list.Count + 1
                            : 1;
                        var targetUrl = $"{DreamParkAPI.baseUrl}/app/content/addressables-v2/{uploadContentId}/{versionNumber}";
                        bool singlePlatformEstimate = pendingProductionEstimateOnly;
                        EstimateBuildTargetInfo estimateTargetInfo = default;
                        if (singlePlatformEstimate && !TryGetSinglePlatformEstimateTarget(out estimateTargetInfo))
                        {
                            throw new Exception("Could not determine a platform for Check Patch Size. Switch Unity to a supported target and try again.");
                        }

                        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                        string serverDataPath = Path.Combine(projectRoot, "ServerData");

                        // Step counter for the progress bar. Total reflects what we'll
                        // actually run: 7 setup steps when build == true (clear, settings,
                        // sync, groups, logo, namespaces, package), plus one per enabled
                        // platform, plus one for the manifest diff computation. Unity's
                        // own progress bars take over for SwitchActiveBuildTarget and
                        // BuildPlayerContent inside each platform step, so we don't try
                        // to slice those further.
                        int numPlatforms = singlePlatformEstimate
                            ? 1
                            : ((buildAndroid ? 1 : 0) + (buildIos ? 1 : 0)
                             + (buildOsx ? 1 : 0) + (buildWindows ? 1 : 0));
                        int targetRestoreSteps = build
                            && !string.Equals(sourceContentId, uploadContentId, StringComparison.Ordinal)
                            ? 1
                            : 0;
                        int currentStep = 0;
                        int totalSteps = (build ? 9 + numPlatforms + targetRestoreSteps : 0) + 1; // +1 for manifest computation
                        Action<string> reportStep = (message) =>
                        {
                            currentStep++;
                            float stageProgress = Mathf.Clamp01((float)currentStep / Mathf.Max(1, totalSteps));
                            SetUploadStatus(
                                build ? "Compiling release" : "Preparing upload",
                                message,
                                stageProgress);
                            EditorUtility.DisplayProgressBar(
                                uploadTarget == ContentUploadTarget.Beta ? "Upload Beta" : "Upload Release",
                                $"({currentStep}/{totalSteps}) {message}",
                                stageProgress);
                        };

                        bool buildSuccess = true;
                        bool targetIdentityApplied = false;
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
                                settings.MonoScriptBundleCustomNaming = uploadContentId + "_";
                                // Pin catalog filename to the contentId so every build produces
                                // "catalog_{contentId}.json" instead of using the app bundleVersion.
                                Debug.Log($"📛 [BEFORE] OverridePlayerVersion = '{settings.OverridePlayerVersion}', PlayerBuildVersion = '{settings.PlayerBuildVersion}'");
                                settings.OverridePlayerVersion = uploadContentId;
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
                                // the sync here makes "Upload Release" the one-
                                // button flow it's meant to be — the previously-
                                // manual Manage Third Party Assets step is folded in.
                                reportStep("Syncing third-party assets...");
                                try
                                {
                                    ThirdPartySyncTool.RunSyncForContent(sourceContentId);
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

                                // Bake before Addressables are gathered so the
                                // prefab shipped in this release and the static
                                // packing profile uploaded to Firestore describe
                                // the exact same child poses and ranges.
                                reportStep("Baking flexible attraction layouts...");
                                int packingBakes = AttractionPackingBaker.BakeAllInContent(sourceContentId);
                                Debug.Log($"[ContentUploader] Refreshed packing data for {packingBakes} attraction prefab(s) before build.");
                                AssetDatabase.SaveAssets();

                                reportStep("Compiling Sequence package definition...");
                                CompileDreamSequencePackage(sourceContentId);

                                reportStep("Updating addressable groups...");
                                targetIdentityApplied = !string.Equals(
                                    sourceContentId, uploadContentId, StringComparison.Ordinal);
                                ContentProcessor.ForceUpdateContent(sourceContentId, uploadContentId);

                                // Janitor pass — drop missing-reference entries and
                                // empty stale-prefix groups (e.g. YOUR_GAME_HERE-*
                                // leftovers from the SDK template after the
                                // new-park rename).
                                ContentProcessor.CleanupAddressableSettings();

                                reportStep("Updating logo entry...");
                                SyncLogoAddressableEntry();

                                reportStep("Enforcing content namespaces...");
                                ContentProcessor.EnforceContentNamespaces(sourceContentId);

                                if (singlePlatformEstimate)
                                {
                                    // Production patch estimates are just a
                                    // quick "how much of the current platform
                                    // changed?" check. Skip the Unity package
                                    // entirely here so we don't spend time on
                                    // non-bundle output that won't make this
                                    // estimate materially better.
                                    reportStep($"Building {estimateTargetInfo.label}...");
                                    buildSuccess &= BuildForTarget(
                                        estimateTargetInfo.target,
                                        estimateTargetInfo.group,
                                        $"{targetUrl}/{estimateTargetInfo.platformName}",
                                        uploadContentId);
                                    if (!buildSuccess) throw new Exception($"{estimateTargetInfo.label} build failed");
                                }
                                else
                                {
                                    reportStep("Building scripts package...");
                                    buildSuccess &= ContentProcessor.BuildUnityPackage(sourceContentId, uploadContentId);

                                    if (!buildSuccess) throw new Exception("Unity package build failed");
                                    if (buildAndroid)
                                    {
                                        reportStep("Building Android...");
                                        buildSuccess &= BuildForTarget(BuildTarget.Android, BuildTargetGroup.Android, $"{targetUrl}/Android", uploadContentId);
                                        if (!buildSuccess) throw new Exception("Android build failed");
                                    }
                                    if (buildIos)
                                    {
                                        reportStep("Building iOS...");
                                        buildSuccess &= BuildForTarget(BuildTarget.iOS, BuildTargetGroup.iOS, $"{targetUrl}/iOS", uploadContentId);
                                        if (!buildSuccess) throw new Exception("iOS build failed");
                                    }
                                    if (buildOsx)
                                    {
                                        reportStep("Building StandaloneOSX...");
                                        buildSuccess &= BuildForTarget(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneOSX", uploadContentId);
                                        if (!buildSuccess) throw new Exception("OSX build failed");
                                    }
                                    if (buildWindows)
                                    {
                                        reportStep("Building StandaloneWindows...");
                                        buildSuccess &= BuildForTarget(BuildTarget.StandaloneWindows, BuildTargetGroup.Standalone, $"{targetUrl}/StandaloneWindows", uploadContentId);
                                        if (!buildSuccess) throw new Exception("Windows build failed");
                                    }
                                }
                            }

                            if (targetIdentityApplied)
                            {
                                reportStep("Restoring release authoring state...");
                                ContentProcessor.RestoreContentAfterTargetBuild(sourceContentId, uploadContentId);
                                targetIdentityApplied = false;
                            }

                            reportStep("Computing patch estimate...");

                            // Build a manifest of what's currently in ServerData (the just-built
                            // output, or whatever existed for "Try Reupload"), diff it against
                            // the latest backend version metadata, and route the diff through
                            // UploadModeFilter to turn the active UploadMode into a skipSet.
                            //
                            // - All:        skipSet = null (full re-upload)
                            // - Patch:      skipSet = unchanged-file keys, server fills gaps via fallback
                            // - CodeOnly:   skipSet = everything except catalog + {gameId}-Code bundle.
                            //               Aborts here if non-Code bundles also changed (would
                            //               produce a catalog referencing local-only hashes).
                            //
                            // Legacy bundling forces UploadMode.All regardless of UI selection
                            // because the Code carve-out group doesn't exist there and
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
                            BuildManifest backendBaselineForUpload = patchingEnabled ? FetchBackendBaselineForUpload(contentDirectory) : null;

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
                                    currentManifest = BuildManifestStore.BuildFromServerData(uploadContentId, versionNumber, manifestPlatformsFO);

                                    var failedRecord = FailedBundleStore.Load(uploadContentId);
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
                                            preUploadedFiles.Add(new DreamPark.API.UploadedFileRecord(s.platform, s.fileName, s.uploadPath, failedRecord.releaseId));
                                        }

                                        // Snapshot baseline / diff fields for
                                        // the panel UI without affecting the
                                        // upload logic — keeps the visual
                                        // state in sync with what's about to
                                        // happen.
                                        patchBaseline = backendBaselineForUpload;
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
                                var manifestPlatforms = singlePlatformEstimate
                                    ? new List<string> { estimateTargetInfo.platformName }
                                    : GetEnabledPlatformsForManifest();
                                currentManifest = BuildManifestStore.BuildFromServerData(uploadContentId, versionNumber, manifestPlatforms);
                                BuildManifest baseline = backendBaselineForUpload;
                                var diff = BuildManifestStore.Diff(baseline, currentManifest);

                                modeResult = UploadModeFilter.Build(effectiveMode, uploadContentId, currentManifest, patchingEnabled ? diff : null);

                                // Sanity check for Code-only: an empty Code group (no Lua
                                // scripts) leaves no bundle to ship. SmartBundleGrouper's
                                // empty-group sweep removes the group, Addressables skips
                                // producing a bundle, and we'd end up uploading just a
                                // catalog — technically successful but a no-op for the
                                // runtime. Flag it instead.
                                if (string.IsNullOrEmpty(modeResult.blockingError)
                                    && effectiveMode == UploadMode.CodeOnly)
                                {
                                    const UploadModeFilter.FileCategory targetCat =
                                        UploadModeFilter.FileCategory.CodeBundle;
                                    bool hasTargetBundle = false;
                                    foreach (var p in currentManifest.platforms)
                                    {
                                        foreach (var f in p.files)
                                        {
                                            if (UploadModeFilter.Categorize(uploadContentId, f.fileName) == targetCat)
                                            {
                                                hasTargetBundle = true;
                                                break;
                                            }
                                        }
                                        if (hasTargetBundle) break;
                                    }
                                    if (!hasTargetBundle)
                                    {
                                        string what = "Lua scripts (*.lua.txt under Assets/Content/" + sourceContentId + "/)";
                                        modeResult.blockingError =
                                            $"{UploadModePrefs.ShortLabel(effectiveMode)} upload aborted: " +
                                            $"no {UploadModePrefs.ShortLabel(effectiveMode)} bundle was produced by this build. " +
                                            $"Add some {what} and rebuild, or pick a different upload mode.";
                                    }
                                }

                                // Hard-block path: a Code-only upload that would ship a
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
                                if (pendingProductionEstimateOnly)
                                {
                                    Debug.LogError($"[ContentUploader] Check Patch Size failed: {manifestEx.Message}");
                                    EditorUtility.ClearProgressBar();
                                    pendingProductionEstimateOnly = false;
                                    CompleteUploadStatus(false, $"Check Patch Size failed: {manifestEx.Message}");
                                    EditorUtility.DisplayDialog("Check Patch Size Failed", manifestEx.Message, "OK");
                                    isUploading = false;
                                    return;
                                }

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
                                        ? BuildManifestStore.Diff(backendBaselineForUpload, currentManifest)
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

                                AttachUploaderAndPackages(manifestSummary, effectiveMode);
                            }
                            catch (Exception uploaderMetadataEx)
                            {
                                Debug.LogWarning($"[ContentUploader] Could not attach uploader metadata: {uploaderMetadataEx.Message}");
                            }

                            if (pendingProductionEstimateOnly)
                            {
                                pendingProductionContentId = uploadContentId;
                                pendingProductionMode = effectiveMode;
                                pendingProductionBuildOsx = buildOsx;
                                pendingProductionBuildWindows = buildWindows;
                                pendingProductionVersionNumber = versionNumber;
                                pendingProductionPatchingEnabled = patchingEnabled;
                                pendingProductionCurrentManifest = currentManifest;
                                pendingProductionSkipSet = skipSet != null
                                    ? new HashSet<string>(skipSet, StringComparer.Ordinal)
                                    : null;
                                pendingProductionManifestSummary = manifestSummary;
                                pendingProductionEstimateOnly = false;

                                long estimateBytes = 0L;
                                int estimateFiles = 0;
                                if (effectiveMode == UploadMode.Patch && patchDiff != null)
                                {
                                    estimateBytes = patchDiff.TotalChangedBytes;
                                    estimateFiles = patchDiff.TotalChangedFileCount;
                                }
                                else if (modeResult != null)
                                {
                                    estimateBytes = modeResult.bytesToUpload;
                                    estimateFiles = modeResult.filesToUpload;
                                }
                                else if (currentManifest != null)
                                {
                                    estimateBytes = currentManifest.TotalBytes;
                                    estimateFiles = currentManifest.TotalFileCount;
                                }

                                EditorUtility.ClearProgressBar();
                                isUploading = false;
                                uploadStatusProgress = -1f;
                                uploadStatusIsError = false;
                                uploadCompleted = false;
                                uploadSucceeded = false;
                                uploadStatusTitle = "Patch estimate ready";
                                uploadStatusMessage =
                                    $"{UploadModePrefs.ShortLabel(effectiveMode)} will upload {estimateFiles} file(s) · " +
                                    $"{BuildManifestStore.FormatBytes(estimateBytes)}. Click Start with the same settings to upload without rebuilding.";
                                Repaint();
                                return;
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
                            // user an "Upload All Now" escape hatch when they
                            // intentionally want to publish a full resend even
                            // though the backend parent comparison found no
                            // changes.
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
                                bool uploadAllNow = EditorUtility.DisplayDialog(
                                    "Nothing to upload",
                                    $"'{contentName}' is already at the latest version — no content changes were detected since the last successful upload.\n\n" +
                                    "If you still want to publish this build anyway, you can force a full reupload right now.",
                                    "Upload All Now", "OK");
                                if (uploadAllNow)
                                {
                                    patchBaseline = null;
                                    patchDiff = BuildManifestStore.Diff(null, patchCurrentSnapshot);
                                    skipSet = null;
                                    effectiveMode = UploadMode.All;
                                    try
                                    {
                                        manifestSummary = BuildManifestStore.BuildCommitSummary(currentManifest, diff: null);
                                        if (manifestSummary == null || manifestSummary.type != JSONObject.Type.Object)
                                            manifestSummary = new JSONObject(JSONObject.Type.Object);
                                        AttachUploaderAndPackages(manifestSummary, effectiveMode);
                                    }
                                    catch (Exception forceSummaryEx)
                                    {
                                        Debug.LogWarning($"[ContentUploader] Could not rebuild full-upload summary: {forceSummaryEx.Message}");
                                    }
                                    Debug.Log("[ContentUploader] Creator chose Upload All Now after zero-diff backend compare.");
                                    Repaint();
                                }
                                else
                                {
                                    CompleteUploadStatus(true, "No content changes were detected, so nothing needed to upload.");
                                    isUploading = false;
                                    return;
                                }
                            }

                            StartPreparedProductionUpload(
                                uploadContentId,
                                releaseNotes,
                                versionNumber,
                                patchingEnabled,
                                currentManifest,
                                skipSet,
                                manifestSummary,
                                preUploadedFiles);
                        }
                        catch (Exception e)
                        {
                            if (targetIdentityApplied)
                            {
                                try
                                {
                                    ContentProcessor.RestoreContentAfterTargetBuild(sourceContentId, uploadContentId);
                                }
                                catch (Exception restoreEx)
                                {
                                    Debug.LogError($"[ContentUploader] Failed to restore release authoring state after target build: {restoreEx}");
                                }
                            }
                            // Clear the compile-progress bar so the error dialog
                            // isn't competing with a stale progress overlay.
                            EditorUtility.ClearProgressBar();
                            Debug.LogError("❌ Addressable build failed: " + e);
                            CompleteUploadStatus(false,
                                $"{(uploadTarget == ContentUploadTarget.Beta ? "Beta upload" : "Release")} failed: {e.Message}");
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
                        SyncTagLayerSchema(sourceContentId, uploadContentId, (syncSuccess, syncError) =>
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
                        Debug.Log($"Content found for {uploadContentId}.");
                        JSONObject metadataUpdate = new JSONObject();
                        metadataUpdate.AddField(
                            "contentName",
                            uploadTarget == ContentUploadTarget.Beta ? $"{contentName} (Beta)" : contentName);
                        metadataUpdate.AddField("contentDescription", contentDescription);
                        metadataUpdate.AddField("sequenceLayout", BuildSequenceLayoutJson());
                        // logoAddress (the Addressables key) is deliberately NOT
                        // sent any more — July 2026 the logo stopped shipping in
                        // a bundle. content.logoImageUrl, pushed by
                        // UploadLogoImage below, is the one source of truth and
                        // every surface (iOS, web, admin, the VR client) reads
                        // it. Sending a key that no longer resolves would just
                        // hand clients a dead address.

                        ContentAPI.UpdateContent(uploadContentId, metadataUpdate, (updateSuccess, updateResponse) =>
                        {
                            if (!updateSuccess)
                            {
                                Debug.LogError($"❌ Failed to update content metadata: {updateResponse.error}");
                                CompleteUploadStatus(false, $"Metadata update failed: {updateResponse.error}");
                                EditorUtility.DisplayDialog("Error", $"Metadata update failed: {updateResponse.error}", "OK");
                                isUploading = false;
                                return;
                            }
                            // Fire-and-forget: push the raw logo image to the
                            // backend alongside the metadata (never blocks or
                            // fails the upload — repair via Troubleshooting).
                            try { UploadLogoImage(uploadContentId, interactive: false); }
                            catch (Exception e) { Debug.LogWarning("[Logo] upload skipped: " + e.Message); }
                            // Badges ride alongside the logo, and for the same
                            // reason: they are backend metadata on the content
                            // doc, not bundle payload, so this is the moment the
                            // doc is known to exist and to be ours.
                            PushBadgesSilently(uploadContentId, sourceContentId);
                            continueAfterSchemaSync();
                        });
                        return;
                    }
                    else if (response.statusCode == 403)
                    {
                        Debug.LogError($"❌ Content '{uploadContentId}' is owned by another user.");
                        CompleteUploadStatus(false, "Access denied. This content ID belongs to another owner.");
                        EditorUtility.DisplayDialog("Access Denied",
                            $"Content '{uploadContentId}' is owned by another user. Choose a different folder name in Assets/Content/ or ask the content owner to add you as a collaborator.",
                            "OK");
                        isUploading = false;
                        return;
                    }
                    else if (response.statusCode == 404)
                    {
                        if (uploadTarget == ContentUploadTarget.Beta)
                        {
                            const string betaMissing = "The beta target no longer exists. Close this window and click Upload Beta again to re-allocate it safely.";
                            Debug.LogError("❌ " + betaMissing);
                            CompleteUploadStatus(false, betaMissing);
                            EditorUtility.DisplayDialog("Beta target missing", betaMissing, "OK");
                            isUploading = false;
                            return;
                        }

                        // Content doesn't exist yet — create it
                        // logoAddress: null — the logo isn't in a bundle any
                        // more (July 2026), so a key would be a dead pointer.
                        // UploadLogoImage pushes the image itself and the
                        // backend serves it as content.logoImageUrl.
                        ContentAPI.AddContent(sourceContentId, contentName, contentDescription, null, (success, response) =>
                        {
                            if (success)
                            {
                                Debug.Log($"✅ Content '{contentName}' uploaded successfully!");
                                // First upload is exactly when the logo should
                                // land on the backend too (fire-and-forget).
                                try { UploadLogoImage(sourceContentId, interactive: false); }
                                catch (Exception e) { Debug.LogWarning("[Logo] upload skipped: " + e.Message); }
                                // First upload: the content doc has just been
                                // created, so this is the earliest point at which
                                // /admin/content/:id/badges/save can authorize us
                                // as its owner. Pushing any earlier 403s.
                                PushBadgesSilently(sourceContentId, sourceContentId);
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
                CompleteUploadStatus(false,
                    $"{(activeUploadTarget == ContentUploadTarget.Beta ? "Beta upload" : "Release")} failed: {e.Message}");
                EditorUtility.DisplayDialog("Error", $"Error: {e.Message}", "OK");
                isUploading = false;
            }
        }

        private void SyncTagLayerSchema(
            string sourceContentId,
            string uploadContentId,
            Action<bool, string> callback)
        {
            try
            {
                var local = TagLayerSchemaSyncUtility.ReadLocalTagManager();
                ContentAPI.SyncTagLayerSchema(uploadContentId, 0, local.tags, local.layers, (syncSuccess, syncResponse) =>
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
                    var remapResult = TagLayerSchemaSyncUtility.RemapContentPrefabsByTagName(sourceContentId, remap);
                    if (remapResult.replacements > 0)
                    {
                        Debug.Log($"[TagLayerSchema] Remapped prefab tags for {sourceContentId}: {remapResult.replacements} replacements across {remapResult.filesChanged} prefabs.");
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
                        var refreshResult = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(sourceContentId);
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
                        Debug.LogWarning($"[TagLayerSchema] Content '{syncResult.proposalContentId ?? uploadContentId}' has pending schema additions awaiting acceptance by core.");
                    }
                    callback?.Invoke(true, null);
                });
            }
            catch (Exception e)
            {
                callback?.Invoke(false, e.Message);
            }
        }

        // Keeps the logo's addressable ENTRY (address + label + GUID
        // bookkeeping) while making sure the group never builds. July 2026:
        // the logo ships to the backend as an image
        // (POST /api/content/:id/logo → content.logoImageUrl) and no runtime
        // path loads it out of a bundle any more, so building a per-content
        // logo bundle was pure upload weight. The entry stays put so the
        // texture isn't re-harvested into a gameplay bundle by the next
        // grouping pass — and so re-enabling is one flag.
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
                    ScriptableObject.CreateInstance<BundledAssetGroupSchema>(),
                    ScriptableObject.CreateInstance<ContentUpdateGroupSchema>()
                });

            var bag = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            bag.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
            bag.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
            bag.UseAssetBundleCache = true;
            bag.UseAssetBundleCrc = true;
            bag.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bag.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            // The one behavioural line: no logo bundle is produced or uploaded.
            bag.IncludeInBuild = false;

            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = GetLogoAddress();
            entry.SetLabel(contentId, true, true);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"✅ Synced logo addressable (build-excluded): {entry.address}");
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

                // Strip the editor version from bundle headers. If left off, the
                // Unity version string is baked into every bundle, so a Unity
                // upgrade re-hashes ALL bundles → every content does a full
                // re-download instead of a patch. There's no public C# property
                // for this in this Addressables version, so set the serialized
                // field directly. Enforced here (not just in the .asset) so every
                // creator project builds patch-stable bundles regardless of their
                // local Addressables settings.
                {
                    var settingsSO = new SerializedObject(settings);
                    var stripProp = settingsSO.FindProperty("m_StripUnityVersionFromBundleBuild");
                    if (stripProp != null && !stripProp.boolValue)
                    {
                        stripProp.boolValue = true;
                        settingsSO.ApplyModifiedProperties();
                        EditorUtility.SetDirty(settings);
                        Debug.Log("🧱 [BuildForTarget] Forced StripUnityVersionFromBundleBuild = true (serialized field) for patch-stable bundles.");
                    }
                }

                // Bake a CRC into the catalog for every bundle and validate it BOTH
                // on download AND when loading from cache. Download-validation stops a
                // corrupt download from being cached; cached-validation rejects a
                // corrupt/wrong bundle that's ALREADY cached (re-downloads instead of
                // crashing) — which is exactly the failure we hit. Enforced on every
                // group so a creator project can't ship one without it (settings drift
                // was why a corrupt bundle slipped through). Cost: a CRC pass when a
                // cached bundle loads — async (adds load latency, not frame hitches).
                int crcFixed = 0;
                foreach (var grp in settings.groups)
                {
                    if (grp == null) continue;
                    var bundledSchema = grp.GetSchema<UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema>();
                    if (bundledSchema == null) continue;

                    bool schemaChanged = false;
                    if (!bundledSchema.UseAssetBundleCrc)
                    {
                        bundledSchema.UseAssetBundleCrc = true;                 // validate on download
                        schemaChanged = true;
                    }
                    if (!bundledSchema.UseAssetBundleCrcForCachedBundles)
                    {
                        bundledSchema.UseAssetBundleCrcForCachedBundles = true; // ALSO validate cached bundles on load
                        schemaChanged = true;
                    }
                    if (schemaChanged)
                    {
                        EditorUtility.SetDirty(bundledSchema);
                        crcFixed++;
                    }
                }
                if (crcFixed > 0)
                    Debug.Log($"🔒 [BuildForTarget] Normalized CRC (download + cached validation) on {crcFixed} group schema(s).");

                if (EditorPrefs.GetBool(CleanBeforeEachTargetPrefKey, false))
                {
                    // Addressables cache/build artifacts are target-specific.
                    // Enable this when stale bundles are suspected.
                    CleanAddressablesPlayerContentCache();
                }

                // DeliveryIndexGenerator consumes Addressables' actual SBP bundle
                // graph, so force the machine-independent JSON build layout for this
                // build even when the developer has the optional report UI disabled.
                bool priorGenerateBuildLayout = ProjectConfigData.GenerateBuildLayout;
                ProjectConfigData.ReportFileFormat priorBuildLayoutFormat = ProjectConfigData.BuildLayoutReportFileFormat;
                AddressablesPlayerBuildResult result;
                try
                {
                    ProjectConfigData.GenerateBuildLayout = true;
                    ProjectConfigData.BuildLayoutReportFileFormat = ProjectConfigData.ReportFileFormat.JSON;
                    AddressableAssetSettings.BuildPlayerContent(out result);
                }
                finally
                {
                    ProjectConfigData.GenerateBuildLayout = priorGenerateBuildLayout;
                    ProjectConfigData.BuildLayoutReportFileFormat = priorBuildLayoutFormat;
                }

                if (!string.IsNullOrEmpty(result.Error))
                {
                    throw new System.Exception($"❌ Addressables build failed for {target}: {result.Error}");
                }
                if (!string.IsNullOrEmpty(contentId))
                    DeliveryIndexGenerator.Generate(contentId, DeliveryPlatformName(target));
                Debug.Log($"✅ Addressables build complete for {target}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ Addressables build failed for {target}: {e}");
                return false;
            }
        }

        private static string DeliveryPlatformName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android: return "Android";
                case BuildTarget.iOS: return "iOS";
                case BuildTarget.StandaloneOSX: return "StandaloneOSX";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "StandaloneWindows";
                default: return target.ToString();
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
