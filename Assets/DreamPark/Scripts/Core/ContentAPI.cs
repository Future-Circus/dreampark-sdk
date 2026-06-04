using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Defective.JSON;
using UnityEngine;
using UnityEngine.Networking;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{  
    // Snapshot of a bundle that uploaded successfully in a *previous* run but
    // never made it to commitUpload because some other bundle in the same run
    // failed. Used to plumb the prior run's uploadPaths back into the retry's
    // commitUpload payload so the resulting version references the full set
    // of files, not just the ones we re-sent this time.
    //
    // Editor code (ContentUploaderPanel) builds these from FailedBundleStore
    // before calling UploadContent on a "Failed Only" retry; runtime callers
    // can ignore the new parameter (it has a null default).
    public class UploadedFileRecord {
        public string platform;
        public string fileName;   // Relative to ServerData/{platform}/, forward-slash normalized.
        public string uploadPath; // Storage key the backend handed back from /uploadUrl in the prior run.

        public UploadedFileRecord() {}
        public UploadedFileRecord(string platform, string fileName, string uploadPath) {
            this.platform = platform;
            this.fileName = fileName;
            this.uploadPath = uploadPath;
        }
    }

    public class InheritedBundleRecord {
        public string platform;
        public string fileName;

        public InheritedBundleRecord() {}
        public InheritedBundleRecord(string platform, string fileName) {
            this.platform = platform;
            this.fileName = fileName;
        }
    }

    public class UploadContentData {
        public string filePath = null;
        // fileName is what gets sent as the "filename" field on the presigned-URL
        // request, so it needs to match what the catalog expects. When a
        // platformRoot is provided, this is the path *relative to that root* with
        // forward slashes — that way bundles produced by PackSeparately groups
        // (which live in subdirectories like
        // <gameid>-models_assets_<gameid>/models/foo.bundle) preserve their
        // relative path through the upload step instead of getting flattened by
        // Path.GetFileName. When platformRoot is null we fall back to the leaf
        // filename, which preserves the legacy PackTogether behavior.
        public string fileName = null;
        public string mimeType = null;
        public byte[] data = null;

        public UploadContentData(string filePath, byte[] data)
            : this(filePath, data, null) { }

        public UploadContentData(string filePath, byte[] data, string platformRoot) {
            this.filePath = filePath;
            if (!string.IsNullOrEmpty(platformRoot)) {
                this.fileName = Path.GetRelativePath(platformRoot, filePath).Replace('\\', '/');
            } else {
                this.fileName = Path.GetFileName(filePath);
            }
            string ext = Path.GetExtension(filePath);
            if (ext == ".json") {
                mimeType = "application/json";
            } else if (ext == ".hash") {
                mimeType = "text/plain";
            } else {
                mimeType = "application/octet-stream";
            }
            this.data = data;
        }
    }
    public class PlatformContentData {
        public string platform = null;
        public List<UploadContentData> data = null;

        public PlatformContentData(string platform) {
            this.platform = platform;
            string directory = Path.Combine("ServerData", this.platform);
            if (Directory.Exists(directory))
            {
                data = new List<UploadContentData>();
                // Walk the whole ServerData/<platform>/ tree, not just the top
                // level — bundles produced by PackSeparately groups land in
                // per-group subdirectories (e.g. ServerData/StandaloneOSX/
                // <gameid>-models_assets_<gameid>/models/foo.bundle) and
                // were previously skipped by the non-recursive Directory.GetFiles
                // call, which is what caused 404s at runtime for those bundles.
                var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                foreach (var filePath in files)
                {
                    byte[] fileBytes = File.ReadAllBytes(filePath);
                    // Pass the platform root so UploadContentData stores the
                    // relative path (e.g. "<gameid>-models_assets_<gameid>/
                    // models/foo.bundle") as the filename, preserving the
                    // subdirectory structure the catalog references.
                    data.Add(new UploadContentData(filePath, fileBytes, directory));
                }
            }
            else
            {
                // Not an error — just means this platform wasn't built (the
                // user can disable platforms in the Build Targets section).
                // UploadContentRequest constructs one PlatformContentData
                // per known platform regardless of which were enabled, so
                // the missing directory is the expected case for skipped
                // platforms.
                Debug.Log($"[ContentAPI] {this.platform} not built — skipping (no ServerData/{this.platform}/ directory).");
            }
        }

        public List<KeyValuePair<string, UploadContentData>> ToList() {
            if (data == null) {
                return null;
            }
            List<KeyValuePair<string, UploadContentData>> list = new List<KeyValuePair<string, UploadContentData>>();
            foreach (var d in data) {
                list.Add(new KeyValuePair<string, UploadContentData>(platform, d));
            }
            return list;
        }
    }
    public class UploadContentRequest {
        public PlatformContentData ios;
        public PlatformContentData android;
        public PlatformContentData mac;
        public PlatformContentData windows;
        public PlatformContentData unity;
        public UploadContentRequest() {
            ios = new PlatformContentData("iOS");
            android = new PlatformContentData("Android");
            mac = new PlatformContentData("StandaloneOSX");
            windows = new PlatformContentData("StandaloneWindows");
            unity = new PlatformContentData("Unity");
        }

        public List<KeyValuePair<string, UploadContentData>> ToList() {
            List<KeyValuePair<string, UploadContentData>> list = new List<KeyValuePair<string, UploadContentData>>();
            
            var iosData = ios.ToList();
            var androidData = android.ToList();
            var macData = mac.ToList();
            var windowsData = windows.ToList();
            var unityData = unity.ToList();

            if (androidData != null) {
                list.AddRange(androidData);
            }
            if (iosData != null) {
                list.AddRange(iosData);
            }
            if (macData != null) {
                list.AddRange(macData);
            }
            if (windowsData != null) {
                list.AddRange(windowsData);
            }
            if (unityData != null) {
                list.AddRange(unityData);
            }
            return list;
        }
    }

    public class ContentAPI
    {
        private static bool IsBundleFileName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                && fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
        }

        public class UploadProgressEntry
        {
            public string id;
            public string platform;
            public string fileName;
            public long totalBytes;
            public long uploadedBytes;
            public float progress;
            public bool completed;
            public bool failed;
        }

        private static readonly object UploadProgressLock = new object();
        private static readonly Dictionary<string, UploadProgressEntry> UploadProgressById = new Dictionary<string, UploadProgressEntry>();
        public static event Action UploadProgressChanged;

        public static List<UploadProgressEntry> GetUploadProgressSnapshot()
        {
            lock (UploadProgressLock)
            {
                var snapshot = new List<UploadProgressEntry>();
                foreach (var e in UploadProgressById.Values)
                {
                    snapshot.Add(new UploadProgressEntry
                    {
                        id = e.id,
                        platform = e.platform,
                        fileName = e.fileName,
                        totalBytes = e.totalBytes,
                        uploadedBytes = e.uploadedBytes,
                        progress = e.progress,
                        completed = e.completed,
                        failed = e.failed,
                    });
                }

                snapshot.Sort((a, b) =>
                {
                    int platformCompare = string.Compare(a.platform, b.platform, StringComparison.OrdinalIgnoreCase);
                    if (platformCompare != 0)
                    {
                        return platformCompare;
                    }
                    return string.Compare(a.fileName, b.fileName, StringComparison.OrdinalIgnoreCase);
                });

                return snapshot;
            }
        }

        private static string BuildProgressId(string platform, string fileName) => $"{platform}/{fileName}";

        private static void InitializeUploadProgress(List<KeyValuePair<string, UploadContentData>> files)
        {
            lock (UploadProgressLock)
            {
                UploadProgressById.Clear();
                foreach (var kvp in files)
                {
                    var platform = kvp.Key;
                    var file = kvp.Value;
                    string id = BuildProgressId(platform, file.fileName);
                    UploadProgressById[id] = new UploadProgressEntry
                    {
                        id = id,
                        platform = platform,
                        fileName = file.fileName,
                        totalBytes = file.data != null ? file.data.LongLength : 0,
                        uploadedBytes = 0,
                        progress = 0f,
                        completed = false,
                        failed = false,
                    };
                }
            }
            // Clear any leftover patch stats from a prior test-channel run so
            // the popup's DrawPatchStatsBlock doesn't display stale numbers
            // during a subsequent upload (test OR production). Test uploads
            // re-populate stats after the parent-diff phase; production
            // uploads leave it null and the block stays hidden.
            CurrentPatchStats = null;
            UploadProgressChanged?.Invoke();
        }

        private static void UpdateUploadProgress(string platform, string fileName, float progress)
        {
            lock (UploadProgressLock)
            {
                string id = BuildProgressId(platform, fileName);
                if (!UploadProgressById.TryGetValue(id, out var entry))
                {
                    return;
                }
                entry.progress = Mathf.Clamp01(progress);
                long total = entry.totalBytes > 0 ? entry.totalBytes : 0;
                entry.uploadedBytes = total > 0 ? (long)(total * entry.progress) : 0;
            }
            UploadProgressChanged?.Invoke();
        }

        private static void MarkUploadComplete(string platform, string fileName, bool success)
        {
            lock (UploadProgressLock)
            {
                string id = BuildProgressId(platform, fileName);
                if (!UploadProgressById.TryGetValue(id, out var entry))
                {
                    return;
                }
                entry.completed = true;
                entry.failed = !success;
                entry.progress = success ? 1f : entry.progress;
                if (success)
                {
                    entry.uploadedBytes = entry.totalBytes;
                }
            }
            UploadProgressChanged?.Invoke();
        }

        public class TagLayerSchemaSnapshot {
            public int version;
            public List<string> tags = new List<string>();
            public List<string> layers = new List<string>();
        }

        public class TagLayerSchemaSyncResult {
            public bool updated;
            public int schemaVersion;
            public List<string> addedTags = new List<string>();
            public List<string> addedLayers = new List<string>();
            public List<string> layerConflicts = new List<string>();
            public bool proposalPending;
            public string proposalContentId;
            public TagLayerSchemaSnapshot schema = new TagLayerSchemaSnapshot();
        }

        public class TagLayerSchemaLayerProposal
        {
            public int index = -1;
            public string name = "";
        }

        public class TagLayerSchemaProposal
        {
            public string contentId = "";
            public string status = "";
            public int baseVersion;
            public List<string> proposedTags = new List<string>();
            public List<TagLayerSchemaLayerProposal> proposedLayers = new List<TagLayerSchemaLayerProposal>();
            public List<string> layerConflicts = new List<string>();
        }

        public static void GetContents(Action<bool, APIResponse> callback) {
            Debug.Log("ContentAPI: Getting content catalog");
            DreamParkAPI.GET($"/api/content/", AuthAPI.GetUserAuth(), (success, response) => {
                if (success) {
                    Debug.Log("Content got successfully: " + response.json.Print());
                    callback?.Invoke(true, response);
                } else {
                    Debug.LogError("Failed to get content: " + response.error);
                    callback?.Invoke(false, response);
                }
            });
        }
        public static void GetContent(string contentId, Action<bool, APIResponse> callback) {
            DreamParkAPI.GET($"/api/content/{contentId}", AuthAPI.GetUserAuth(), (success, response) => {
                callback?.Invoke(success, response);
            });
        }
        public static void UpdateContent(string contentId, JSONObject update, Action<bool, APIResponse> callback) {
            DreamParkAPI.POST($"/api/content/{contentId}/update", AuthAPI.GetUserAuth(), update, (success, response) => {
                callback?.Invoke(success, response);
            });
        }
        public static void AddContent(string contentId, string contentName, string contentDescription, string logoAddress, Action<bool, APIResponse> callback) {
            JSONObject body = new JSONObject();
            body.AddField("contentId", contentId);
            body.AddField("contentName", contentName);
            body.AddField("contentDescription", contentDescription);
            if (!string.IsNullOrEmpty(logoAddress)) {
                body.AddField("logoAddress", logoAddress);
            }
            DreamParkAPI.POST($"/api/content/add", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        // List collaborators (owners) on a content record. Server returns:
        //   { success, contentOwner, contentOwners: [uid...], owners: [{userId, email}...] }
        public static void ListContentUsers(string contentId, Action<bool, APIResponse> callback) {
            DreamParkAPI.GET($"/api/content/{contentId}/users/", AuthAPI.GetUserAuth(), (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        // Add a collaborator by email. Server resolves email -> uid via the users collection.
        public static void AddContentUser(string contentId, string email, Action<bool, APIResponse> callback) {
            JSONObject body = new JSONObject();
            body.AddField("email", email);
            DreamParkAPI.POST($"/api/content/{contentId}/users/add", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        // Remove a collaborator by uid. The primary contentOwner cannot be removed.
        // Body must be a proper empty JSON object `{}`, not the default `new JSONObject()`
        // which serializes to `null` — Express's body-parser rejects `null` bodies on
        // application/json with a 400 HTML page before our route handler even runs.
        public static void RemoveContentUser(string contentId, string ownerId, Action<bool, APIResponse> callback) {
            var body = new JSONObject(JSONObject.Type.Object);
            DreamParkAPI.POST($"/api/content/{contentId}/users/{ownerId}/remove", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        public static void UploadContent(string contentId, string releaseNotes, Action<bool, APIResponse> callback) {
            UploadContent(contentId, releaseNotes, null, null, null, callback);
        }

        public static void UploadContent(string contentId, string releaseNotes, int? schemaVersion, Action<bool, APIResponse> callback) {
            UploadContent(contentId, releaseNotes, schemaVersion, null, null, callback);
        }

        public static void UploadContent(string contentId, string releaseNotes, int? schemaVersion, HashSet<string> skipFileKeys, Action<bool, APIResponse> callback) {
            UploadContent(contentId, releaseNotes, schemaVersion, skipFileKeys, null, callback);
        }

        // skipFileKeys lets the caller short-circuit re-uploading bundles whose
        // content didn't change since the last successful upload — entries are
        // matched by "{platform}/{relativePath}" against the file's platform +
        // UploadContentData.fileName. Stack with the BuildManifest diff in
        // ContentUploaderPanel to ship KB-scale patches and MB-scale new
        // content. Pass null to upload everything (legacy behavior).
        //
        // manifestSummary is an optional compact summary of the build's
        // per-platform totals — the patch-size (deltaUploaded) and the
        // effective full-content size (fullContent). It rides along on the
        // commitUpload request and gets persisted on the version entry in
        // Firestore so the dreampark-core content manager can display
        // "Total: X MB" and "Last patch: Y MB" without walking Storage.
        public static void UploadContent(string contentId, string releaseNotes, int? schemaVersion, HashSet<string> skipFileKeys, JSONObject manifestSummary, Action<bool, APIResponse> callback) {
            UploadContent(contentId, releaseNotes, schemaVersion, skipFileKeys, manifestSummary, null, callback);
        }

        // preUploadedFiles seeds commitUpload's uploadedFiles map with bundles
        // that already landed in Storage during a *previous* run that failed
        // before commit. This is the "Upload Failed Bundles" code path on the
        // Try Reupload button: we only re-send the bundles that actually
        // failed last time, but the version we commit has to reference *all*
        // the files (succeeded-before + succeeded-now), or the player would
        // pull a catalog that's missing entries.
        //
        // Pass null (or an empty list) for the normal "Reupload All" / first-
        // upload path.
        public static void UploadContent(string contentId, string releaseNotes, int? schemaVersion, HashSet<string> skipFileKeys, JSONObject manifestSummary, List<UploadedFileRecord> preUploadedFiles, Action<bool, APIResponse> callback) {
            // 1️⃣ Collect local files for all platforms
            UploadContentRequest data = new UploadContentRequest();
            var files = data.ToList();
            var inheritedBundles = new List<InheritedBundleRecord>();

            if (files == null || files.Count == 0)
            {
                Debug.LogWarning("No files found to upload.");
                callback?.Invoke(false, new DreamParkAPI.APIResponse(false, 0, "No files found"));
                return;
            }

            int totalCollected = files.Count;
            if (skipFileKeys != null && skipFileKeys.Count > 0)
            {
                var replayedKeys = new HashSet<string>(StringComparer.Ordinal);
                if (preUploadedFiles != null)
                {
                    foreach (var rec in preUploadedFiles)
                    {
                        if (rec == null || string.IsNullOrEmpty(rec.platform) || string.IsNullOrEmpty(rec.fileName))
                            continue;
                        replayedKeys.Add($"{rec.platform}/{rec.fileName}");
                    }
                }

                foreach (var kv in files)
                {
                    string key = $"{kv.Key}/{kv.Value.fileName}";
                    if (!skipFileKeys.Contains(key) || replayedKeys.Contains(key) || !IsBundleFileName(kv.Value.fileName))
                        continue;

                    inheritedBundles.Add(new InheritedBundleRecord(kv.Key, kv.Value.fileName));
                }

                files = files
                    .Where(kv => !skipFileKeys.Contains($"{kv.Key}/{kv.Value.fileName}"))
                    .ToList();
                int skipped = totalCollected - files.Count;
                if (skipped > 0)
                {
                    Debug.Log($"[ContentAPI] Skipping {skipped} unchanged file(s); uploading {files.Count}/{totalCollected}.");
                }
            }

            if (files.Count == 0)
            {
                // Nothing changed — there's still metadata-side work the caller
                // expects (release notes, version bump on the backend), so we
                // can't short-circuit the entire flow. But there are no files
                // to upload. Treat as success and let the caller proceed.
                Debug.Log("[ContentAPI] No changed files to upload; proceeding to finalize.");
                InitializeUploadProgress(files);
            #if UNITY_EDITOR
                Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, inheritedBundles, preUploadedFiles, callback));
            #else
                CoroutineRunner.Run(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, inheritedBundles, preUploadedFiles, callback));
            #endif
                return;
            }

            InitializeUploadProgress(files);

            int preCount = preUploadedFiles != null ? preUploadedFiles.Count : 0;
            if (preCount > 0)
            {
                Debug.Log($"[ContentAPI] Uploading {files.Count} files for content {contentId} (replaying {preCount} previously-uploaded file(s) in commitUpload).");
            }
            else
            {
                Debug.Log($"[ContentAPI] Uploading {files.Count} files for content {contentId}");
            }

        #if UNITY_EDITOR
            Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, inheritedBundles, preUploadedFiles, callback));
        #else
            CoroutineRunner.Run(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, inheritedBundles, preUploadedFiles, callback));
        #endif
        }

#if DREAMPARKCORE
        // Core/admin only: content version approval. Backend authorizes too;
        // compiled out of the SDK so it isn't part of the third-party surface
        // (no SDK callers).
        public static void ApproveContent(string contentId, int versionNumber, bool requiresUpdate, Action<bool, APIResponse> callback) {
            JSONObject body = new JSONObject();
            body.AddField("versionNumber", versionNumber);
            body.AddField("requiresUpdate", requiresUpdate);
            DreamParkAPI.POST($"/api/content/{contentId}/approve", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }
#endif

        // ─── Test Channel ────────────────────────────────────────────
        // Dump-and-forget uploads for internal SDK testing. Distinct from
        // UploadContent (production versioning flow) in three ways:
        //   1. No contentId — each upload allocates a fresh testBuildId
        //      server-side, so test bundles can't collide with the
        //      production content/{contentId}/versions[] array.
        //   2. No skipFileKeys / preUploadedFiles plumbing — test runs
        //      are full uploads every time. Incremental upload makes no
        //      sense for one-off test bundles.
        //   3. Admin-gated on the backend (verifyAdmin middleware) so
        //      non-team users hitting these routes get 403.
        // Backend auto-expires test builds after 7 days
        // (lib/testBuildCleanup.js), so callers don't need to clean up.
        //
        // Two-step API:
        //   1. CreateTestBuild   → returns testBuildId. Call this BEFORE
        //      running the addressables build so the build can bake the
        //      test-channel URL pattern into the catalog (RemoteLoadPath
        //      = /api/test-content/addressables/{testBuildId}). Without
        //      that, Unity's Caching layer would key bundles against the
        //      production URL and the editor would re-download every
        //      time it loaded the test build.
        //   2. UploadTestBuildArtifacts → uploads everything in
        //      ServerData/ to the test_build doc allocated in step 1,
        //      then commits the catalog + manifest.
        //
        // Callbacks receive (success, testBuildId, response). The
        // testBuildId is included even on failure paths where it was
        // allocated, so the UI can surface "your test build {id} failed
        // to upload" rather than dropping it on the floor — and the
        // backend cleanup loop reaps the orphaned doc within 7 days.

        // Step 1 — allocate a new test_build doc and return its ID.
        // Idempotent only at the doc-creation level; calling twice gives
        // two distinct testBuildIds (each gets its own storage prefix and
        // its own 7-day TTL). The metadata fields written here (title,
        // releaseNotes, contentName) can all be overwritten at commit
        // time, so callers don't need to know the final values yet — the
        // important thing is allocating the ID before the bundle build
        // runs.
        //
        // parentTestBuildId (optional): when non-null, marks this build as
        // a PATCH of the named parent. The SDK will diff its local bundle
        // set against the parent's catalog (filename match, which is a
        // content-hash match because Smart bundling uses AppendHash), only
        // upload bundles whose filenames don't appear in the parent, and
        // tell the backend at commit to server-side-copy the rest from
        // the parent's storage prefix. Net effect: a patch upload's
        // wire-bytes match the actual content delta, not the full bundle
        // set. Pass null / empty to do a full upload (legacy behavior).
        public static void CreateTestBuild(string title, string releaseNotes, string contentName, string parentTestBuildId, Action<bool, string, APIResponse> callback)
        {
            JSONObject createBody = new JSONObject();
            createBody.AddField("title", string.IsNullOrEmpty(title) ? "Untitled test build" : title);
            createBody.AddField("releaseNotes", releaseNotes ?? "");
            createBody.AddField("contentName", contentName ?? "");
            if (!string.IsNullOrEmpty(parentTestBuildId))
                createBody.AddField("parentTestBuildId", parentTestBuildId);

            DreamParkAPI.POST("/api/test-content/create", AuthAPI.GetUserAuth(), createBody, (createSuccess, createResp) =>
            {
                if (!createSuccess || createResp?.json == null)
                {
                    Debug.LogError($"[TestBuild] Failed to create test build: {createResp?.error ?? "unknown"}");
                    callback?.Invoke(false, null, createResp);
                    return;
                }
                string testBuildId = createResp.json.GetField("testBuildId")?.stringValue;
                if (string.IsNullOrEmpty(testBuildId))
                {
                    Debug.LogError("[TestBuild] Backend returned no testBuildId");
                    callback?.Invoke(false, null, new DreamParkAPI.APIResponse(false, 0, "No testBuildId returned"));
                    return;
                }
                Debug.Log(string.IsNullOrEmpty(parentTestBuildId)
                    ? $"[TestBuild] Created {testBuildId}"
                    : $"[TestBuild] Created {testBuildId} as patch of {parentTestBuildId}");
                callback?.Invoke(true, testBuildId, createResp);
            });
        }

        // Backward-compat overload — full upload, no parent.
        public static void CreateTestBuild(string title, string releaseNotes, string contentName, Action<bool, string, APIResponse> callback)
        {
            CreateTestBuild(title, releaseNotes, contentName, parentTestBuildId: null, callback);
        }

        // Step 2 — uploads every file currently in ServerData/ to the
        // pre-allocated test_build doc, then commits with the final
        // metadata. The caller is expected to have already done a full
        // compile pipeline pass with RemoteLoadPath baked to the test
        // URL pattern (see ContentUploaderPanel.RunTestBuildPipeline)
        // before invoking this — otherwise the bundle URLs inside the
        // catalog won't match the URLs the editor download flow uses
        // and Unity's Caching layer will treat every load as a miss.
        //
        // parentTestBuildId (optional): when non-null, this method fetches
        // the parent's catalog from the backend, computes a filename-level
        // diff against the local file list, and uploads ONLY the files
        // whose filenames don't appear in the parent's catalog. The
        // remaining files get listed in `inheritedFiles` on the commit
        // payload — the backend then does GCS server-side copies from
        // the parent's storage prefix into this build's prefix, keeping
        // this build self-contained (it can survive the parent expiring).
        public static void UploadTestBuildArtifacts(string testBuildId, string title, string releaseNotes, string contentName, string logoAddress, string parentTestBuildId, JSONObject manifestSummary, Action<bool, string, APIResponse> callback)
        {
            if (string.IsNullOrEmpty(testBuildId))
            {
                callback?.Invoke(false, null, new DreamParkAPI.APIResponse(false, 0, "Missing testBuildId"));
                return;
            }

            UploadContentRequest data = new UploadContentRequest();
            var files = data.ToList();
            if (files == null || files.Count == 0)
            {
                Debug.LogWarning("[TestBuild] No files found to upload (ServerData/ empty).");
                callback?.Invoke(false, testBuildId, new DreamParkAPI.APIResponse(false, 0, "No files found to upload"));
                return;
            }

            InitializeUploadProgress(files);

            // Clear any leftover patch stats from a previous run so the UI
            // doesn't briefly flash old numbers before this run's diff
            // completes and re-populates them.
            CurrentPatchStats = null;

            // Fast path: no parent → behave like the original full-upload
            // flow. We don't fetch a catalog and don't construct an
            // inheritedFiles list.
            if (string.IsNullOrEmpty(parentTestBuildId))
            {
                Debug.Log($"[TestBuild] Uploading {files.Count} file(s) to {testBuildId} (full upload)");
                StartTestBuildUploadFlow(testBuildId, title, releaseNotes, contentName, logoAddress, parentTestBuildId: null, parentInfo: null, files, manifestSummary, callback);
                return;
            }

            // Patch path: fetch parent's catalog + manifest, then start the
            // upload flow with the filename + size diff. If the parent fetch
            // fails we fall back to full upload — better to ship more bytes
            // than fail an otherwise-valid build on a transient backend hiccup.
            GetTestBuild(parentTestBuildId, (getOk, getResp) =>
            {
                ParentBundleInfo parentInfo = null;
                if (getOk && getResp?.json != null)
                {
                    parentInfo = ParseParentBundleInfo(
                        getResp.json.GetField("build"), parentTestBuildId);
                    int parentFileCount = 0;
                    int parentSizedCount = 0;
                    foreach (var kv in parentInfo.filenamesByPlatform) parentFileCount += kv.Value.Count;
                    foreach (var kv in parentInfo.sizesByPlatform) parentSizedCount += kv.Value.Count;
                    Debug.Log($"[TestBuild] Patch base: {parentTestBuildId} ({parentFileCount} files across {parentInfo.filenamesByPlatform.Count} platform(s); size cross-check available for {parentSizedCount})");
                }
                else
                {
                    Debug.LogWarning($"[TestBuild] Could not fetch parent {parentTestBuildId}: {getResp?.error ?? "unknown"} — falling back to full upload");
                }
                StartTestBuildUploadFlow(testBuildId, title, releaseNotes, contentName, logoAddress,
                    parentInfo != null ? parentTestBuildId : null,
                    parentInfo, files, manifestSummary, callback);
            });
        }

        // Backward-compat overload — full upload, no parent.
        public static void UploadTestBuildArtifacts(string testBuildId, string title, string releaseNotes, string contentName, string logoAddress, JSONObject manifestSummary, Action<bool, string, APIResponse> callback)
        {
            UploadTestBuildArtifacts(testBuildId, title, releaseNotes, contentName, logoAddress, parentTestBuildId: null, manifestSummary, callback);
        }

        // Computes a "what would the patch upload look like" plan without
        // actually uploading anything. The caller (typically the editor's
        // "Check Patch Size" button) uses this to show the user the
        // expected patch size + bundle count BEFORE they commit to a full
        // upload — so they can decide whether to proceed or rebuild
        // differently.
        //
        // Side effect: populates ContentAPI.CurrentPatchStats so the
        // popup's DrawPatchStatsBlock renders the estimate without any
        // extra wiring. Subsequent UploadTestBuildArtifacts calls reuse
        // the same ServerData/ files on disk — no rebuild needed — and
        // the stats live in CurrentPatchStats until the next upload
        // starts (which clears them in InitializeUploadProgress).
        //
        // Expects ServerData/ to already contain the just-built bundles
        // for the platforms the caller cares about. Fetches the parent
        // catalog + manifest once, runs the same filename + size diff as
        // the upload path, and returns the plan via callback.
        public static void ComputePatchPlan(string parentTestBuildId, Action<bool, TestBuildPatchStats, string> callback)
        {
            if (string.IsNullOrEmpty(parentTestBuildId))
            {
                callback?.Invoke(false, null, "Missing parentTestBuildId — estimate requires a patch base");
                return;
            }

            UploadContentRequest data = new UploadContentRequest();
            var files = data.ToList();
            if (files == null || files.Count == 0)
            {
                callback?.Invoke(false, null, "No files in ServerData/ to estimate against — build first");
                return;
            }

            GetTestBuild(parentTestBuildId, (getOk, getResp) =>
            {
                if (!getOk || getResp?.json == null)
                {
                    callback?.Invoke(false, null, $"Could not fetch parent {parentTestBuildId}: {getResp?.error ?? "unknown"}");
                    return;
                }

                var parentInfo = ParseParentBundleInfo(getResp.json.GetField("build"), parentTestBuildId);
                if (parentInfo == null || parentInfo.filenamesByPlatform.Count == 0)
                {
                    callback?.Invoke(false, null, $"Parent {parentTestBuildId} has an empty / unreadable catalog");
                    return;
                }

                // Same partitioning rules as TestBuildUploadFlowAsync —
                // filename + size check, with size cross-check graceful-
                // degrading when parent manifest lacks sizes. Kept inline
                // here (rather than refactored into a shared helper) to
                // avoid an extra abstraction layer for a 30-line loop.
                int newCount = 0, inheritedCount = 0;
                long newSize = 0, inheritedSize = 0;
                int sizeMismatchCount = 0;
                foreach (var kvp in files)
                {
                    string platform = kvp.Key;
                    UploadContentData file = kvp.Value;
                    long localSize = file.data?.Length ?? 0;

                    // Catalog JSON / hash files use stable filenames and
                    // are tiny. Treat them as always-fresh so the patch
                    // tool never inherits a stale catalog that points at
                    // old bundle addresses.
                    if (ShouldAlwaysUploadInPatch(file.fileName))
                    {
                        newCount++;
                        newSize += localSize;
                        continue;
                    }

                    bool filenameMatch = parentInfo.filenamesByPlatform.TryGetValue(platform, out var parentFilenames)
                        && parentFilenames.Contains(file.fileName);

                    bool sizeMatch = true;
                    if (filenameMatch
                        && parentInfo.sizesByPlatform.TryGetValue(platform, out var sizeDict)
                        && sizeDict.TryGetValue(file.fileName, out long parentSize))
                    {
                        sizeMatch = (parentSize == localSize);
                    }

                    if (filenameMatch && sizeMatch)
                    {
                        inheritedCount++;
                        inheritedSize += localSize;
                    }
                    else
                    {
                        if (filenameMatch && !sizeMatch) sizeMismatchCount++;
                        newCount++;
                        newSize += localSize;
                    }
                }

                var stats = new TestBuildPatchStats
                {
                    parentTestBuildId = parentTestBuildId,
                    newFiles = newCount,
                    inheritedFiles = inheritedCount,
                    patchSizeBytes = newSize,
                    inheritedSizeBytes = inheritedSize,
                    totalSizeBytes = newSize + inheritedSize,
                    uploadedSoFar = 0,
                };
                CurrentPatchStats = stats;
                UploadProgressChanged?.Invoke();

                string note = sizeMismatchCount > 0 ? $" (incl. {sizeMismatchCount} size-mismatch override(s))" : "";
                Debug.Log($"[TestBuild] Estimate vs {parentTestBuildId}: {newCount} new ({FormatBytes(newSize)}){note}, {inheritedCount} inherited ({FormatBytes(inheritedSize)})");
                callback?.Invoke(true, stats, null);
            });
        }

        // Bundles together everything we extract from a parent build for the
        // diff phase. filenamesByPlatform is the primary index — populated
        // from the parent's `catalog` field which is always present.
        // sizesByPlatform is the optional belt-and-suspenders index, populated
        // from the parent's `manifest.files[]` array (only present on parents
        // uploaded by patch-aware SDKs — legacy parents lack this entirely,
        // in which case the size check gracefully degrades to "skip and
        // trust the filename" behavior).
        private class ParentBundleInfo
        {
            public Dictionary<string, HashSet<string>> filenamesByPlatform;
            // platform → (filename → byte size). When a platform appears in
            // filenamesByPlatform but not here, no per-file size data was
            // available from the parent's manifest — size check is skipped
            // for that platform.
            public Dictionary<string, Dictionary<string, long>> sizesByPlatform;
        }

        // Parses both the {catalog} and {manifest.files[]} sub-objects of a
        // /api/test-content/{id} response. The catalog gives us the
        // authoritative list of files in the parent (used for the primary
        // filename-equality match), and the manifest provides per-file sizes
        // when available (used as a defensive cross-check before we trust the
        // filename-equality result).
        //
        // Path format the catalog stores: "test-addressables/{parentId}/{platform}/{filename}".
        // The {filename} portion can contain forward slashes (e.g. nested
        // bundle layouts under PackSeparately), so we strip the known
        // "test-addressables/{parentId}/{platform}/" prefix rather than
        // splitting on '/' — only the prefix is fixed.
        private static ParentBundleInfo ParseParentBundleInfo(JSONObject buildJson, string parentTestBuildId)
        {
            var info = new ParentBundleInfo
            {
                filenamesByPlatform = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                sizesByPlatform = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase),
            };
            if (buildJson == null || string.IsNullOrEmpty(parentTestBuildId)) return info;

            // 1. Primary index: filenames per platform from `catalog`.
            var catalog = buildJson.GetField("catalog");
            if (catalog != null && catalog.type == JSONObject.Type.Object && catalog.keys != null)
            {
                foreach (string platform in catalog.keys)
                {
                    var pathsArr = catalog.GetField(platform);
                    if (pathsArr == null || pathsArr.type != JSONObject.Type.Array) continue;

                    string platformPrefix = $"test-addressables/{parentTestBuildId}/{platform}/";
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    if (pathsArr.list != null)
                    {
                        foreach (var pathNode in pathsArr.list)
                        {
                            string p = pathNode?.stringValue;
                            if (string.IsNullOrEmpty(p)) continue;
                            if (p.StartsWith(platformPrefix, StringComparison.Ordinal))
                            {
                                string filename = p.Substring(platformPrefix.Length);
                                if (!string.IsNullOrEmpty(filename))
                                    set.Add(filename);
                            }
                        }
                    }
                    if (set.Count > 0)
                        info.filenamesByPlatform[platform] = set;
                }
            }

            // 2. Cross-check index: sizes per platform from `manifest.files[]`.
            // The manifest's `files` array is only written by patch-aware
            // SDKs (committed after this feature shipped). Legacy parents
            // have manifest == null or an older shape without the files
            // sub-array; we simply skip them and the size check becomes a
            // no-op for those parents — defensive degradation, not failure.
            var manifest = buildJson.GetField("manifest");
            if (manifest != null && manifest.type == JSONObject.Type.Object)
            {
                var filesArr = manifest.GetField("files");
                if (filesArr != null && filesArr.type == JSONObject.Type.Array && filesArr.list != null)
                {
                    foreach (var f in filesArr.list)
                    {
                        if (f == null || f.type != JSONObject.Type.Object) continue;
                        string platform = f.GetField("platform")?.stringValue;
                        string filename = f.GetField("filename")?.stringValue;
                        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(filename)) continue;
                        var sizeNode = f.GetField("size");
                        if (sizeNode == null || sizeNode.type != JSONObject.Type.Number) continue;
                        long size = sizeNode.longValue;
                        if (!info.sizesByPlatform.TryGetValue(platform, out var dict))
                        {
                            dict = new Dictionary<string, long>(StringComparer.Ordinal);
                            info.sizesByPlatform[platform] = dict;
                        }
                        dict[filename] = size;
                    }
                }
            }

            return info;
        }

        private static void StartTestBuildUploadFlow(
            string testBuildId, string title, string releaseNotes, string contentName, string logoAddress,
            string parentTestBuildId, ParentBundleInfo parentInfo,
            List<KeyValuePair<string, UploadContentData>> files,
            JSONObject manifestSummary,
            Action<bool, string, APIResponse> callback)
        {
        #if UNITY_EDITOR
            Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(
                TestBuildUploadFlow(testBuildId, title, releaseNotes, contentName, logoAddress, parentTestBuildId, parentInfo, files, manifestSummary, callback));
        #else
            CoroutineRunner.Run(
                TestBuildUploadFlow(testBuildId, title, releaseNotes, contentName, logoAddress, parentTestBuildId, parentInfo, files, manifestSummary, callback));
        #endif
        }

        // Legacy convenience wrapper — kept for any caller that still
        // wants the all-in-one flow (no build, just push what's in
        // ServerData). Internal callers should prefer the two-step
        // CreateTestBuild + UploadTestBuildArtifacts so they can bake
        // the test URL into the build between the two calls.
        public static void UploadTestBuild(string title, string releaseNotes, string contentName, JSONObject manifestSummary, Action<bool, string, APIResponse> callback)
        {
            CreateTestBuild(title, releaseNotes, contentName, (createOk, testBuildId, createResp) =>
            {
                if (!createOk)
                {
                    callback?.Invoke(false, testBuildId, createResp);
                    return;
                }
                // logoAddress is optional on this convenience wrapper —
                // callers that need it use the two-step CreateTestBuild
                // + UploadTestBuildArtifacts directly.
                UploadTestBuildArtifacts(testBuildId, title, releaseNotes, contentName, logoAddress: null, manifestSummary, callback);
            });
        }

        public static void GetTestBuilds(Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET("/api/test-content/list", AuthAPI.GetUserAuth(), (success, response) =>
            {
                callback?.Invoke(success, response);
            });
        }

        public static void GetTestBuild(string testBuildId, Action<bool, APIResponse> callback)
        {
            DreamParkAPI.GET($"/api/test-content/{testBuildId}", AuthAPI.GetUserAuth(), (success, response) =>
            {
                callback?.Invoke(success, response);
            });
        }

        private static IEnumerator TestBuildUploadFlow(string testBuildId, string title, string releaseNotes, string contentName, string logoAddress, string parentTestBuildId, ParentBundleInfo parentInfo, List<KeyValuePair<string, UploadContentData>> files, JSONObject manifestSummary, Action<bool, string, DreamParkAPI.APIResponse> callback)
        {
            TestBuildUploadFlowAsync(testBuildId, title, releaseNotes, contentName, logoAddress, parentTestBuildId, parentInfo, files, manifestSummary, callback).Forget();
            yield break;
        }

        // Represents one file the SDK is going to ask the backend to copy
        // server-side from the parent test build's storage prefix instead of
        // re-uploading. The triple {platform, filename, sourceTestBuildId}
        // matches the backend's commit-time validator (which checks that
        // sourceTestBuildId equals the doc's stored parentTestBuildId).
        private struct InheritedFileEntry
        {
            public string platform;
            public string filename;
            public long sizeBytes;
        }

        // Live snapshot of the current (or most recently computed) test-build
        // patch plan. Set as soon as the SDK finishes diffing the local build
        // against the parent's catalog, then updated as files complete during
        // the upload phase. The editor's ContentUploaderPanel reads this
        // during its status repaint loop so the user can see, live:
        //   • how many bundles are actually being uploaded vs. inherited
        //   • how big the patch is on the wire vs. the total content size
        //   • whether the upload is a no-parent full push (CurrentPatchStats
        //     is null) or a true patch
        // Reset to null at the start of every new upload so a previous run's
        // numbers don't leak into the next session's UI.
        public class TestBuildPatchStats
        {
            public string parentTestBuildId;        // null = full upload (no patch base)
            public int newFiles;                    // files this run must actually upload
            public int inheritedFiles;              // files server-copied from parent
            public long patchSizeBytes;             // sum of newFiles sizes (what hits the wire)
            public long inheritedSizeBytes;         // sum of inheritedFiles sizes
            public long totalSizeBytes;             // patchSize + inheritedSize
            public int uploadedSoFar;               // mutable: incremented per completed newFile
            // Convenience: percentage of the total payload the user is
            // actually pushing over the wire. 0% = perfect cache hit, 100% = full upload.
            public float PatchFraction => totalSizeBytes <= 0 ? 0f : (float)patchSizeBytes / (float)totalSizeBytes;
        }
        public static TestBuildPatchStats CurrentPatchStats { get; private set; }

        // Editor-facing reset for the patch stats. The setter on
        // CurrentPatchStats is private so callers can't accidentally
        // poison the shared state — but the ContentUploaderPanel does
        // legitimately need to clear it when the user discards a pending
        // estimate, so this thin helper exposes that capability.
        public static void ClearCurrentPatchStats()
        {
            CurrentPatchStats = null;
            UploadProgressChanged?.Invoke();
        }

        private static async UniTaskVoid TestBuildUploadFlowAsync(string testBuildId, string title, string releaseNotes, string contentName, string logoAddress, string parentTestBuildId, ParentBundleInfo parentInfo, List<KeyValuePair<string, UploadContentData>> files, JSONObject manifestSummary, Action<bool, string, DreamParkAPI.APIResponse> callback)
        {
            // Partition the local file list into newFiles (must upload) and
            // inheritedFiles (will be server-side-copied from parent at
            // commit). Match criteria, in order:
            //
            //   1. Same filename — Smart Bundling appends a content hash to
            //      every bundle filename, so a filename match is transitively
            //      a content-hash match. This is the primary signal.
            //
            //   2. Same size — defensive belt-and-suspenders. If the parent's
            //      manifest exposed per-file sizes (patch-aware SDK uploaded
            //      it), we cross-check the local file's size against the
            //      parent's recorded size. Any mismatch flips the entry to
            //      "new" and the bundle gets re-uploaded. This catches the
            //      pathological case where a GCS object got truncated /
            //      replaced out-of-band, where two bundles happened to land
            //      at identical hash-suffixed paths via filename collision,
            //      or where the AppendHash invariant was broken. Legacy
            //      parents without manifest sizes skip this check silently
            //      and rely on filename match alone.
            //
            // We track inherited entries separately so we can mark them
            // 100%-complete in the progress UI immediately (no spinner
            // sitting at 0% on bundles the backend will fulfill from
            // existing storage).
            var newFiles = new List<KeyValuePair<string, UploadContentData>>();
            var inheritedFiles = new List<InheritedFileEntry>();
            long newSizeTotal = 0;
            long inheritedSizeTotal = 0;
            int sizeMismatchCount = 0;
            if (parentInfo != null && parentInfo.filenamesByPlatform.Count > 0)
            {
                foreach (var kvp in files)
                {
                    string platform = kvp.Key;
                    UploadContentData file = kvp.Value;
                    long localSize = file.data?.Length ?? 0;

                    // Catalog JSON / hash files use stable filenames and
                    // are tiny. Always upload them fresh so the committed
                    // test build can't inherit a catalog that references
                    // superseded bundle filenames.
                    if (ShouldAlwaysUploadInPatch(file.fileName))
                    {
                        newFiles.Add(kvp);
                        newSizeTotal += localSize;
                        continue;
                    }

                    bool filenameMatch = parentInfo.filenamesByPlatform.TryGetValue(platform, out var parentFilenames)
                        && parentFilenames.Contains(file.fileName);

                    // Size cross-check (only meaningful when both filename
                    // match AND parent has size data for this entry).
                    bool sizeMatch = true;
                    bool sizeCheckPerformed = false;
                    if (filenameMatch
                        && parentInfo.sizesByPlatform.TryGetValue(platform, out var sizeDict)
                        && sizeDict.TryGetValue(file.fileName, out long parentSize))
                    {
                        sizeCheckPerformed = true;
                        sizeMatch = (parentSize == localSize);
                    }

                    if (filenameMatch && sizeMatch)
                    {
                        inheritedFiles.Add(new InheritedFileEntry
                        {
                            platform = platform,
                            filename = file.fileName,
                            sizeBytes = localSize,
                        });
                        inheritedSizeTotal += localSize;
                        MarkUploadComplete(platform, file.fileName, true);
                    }
                    else
                    {
                        if (filenameMatch && !sizeMatch)
                        {
                            sizeMismatchCount++;
                            Debug.LogWarning($"[TestBuild] Size mismatch on {platform}/{file.fileName} — local {localSize}B vs parent record — uploading fresh");
                        }
                        newFiles.Add(kvp);
                        newSizeTotal += localSize;
                    }
                }
                string sizeNote = sizeMismatchCount > 0 ? $" (incl. {sizeMismatchCount} size-mismatch override(s))" : "";
                Debug.Log($"[TestBuild] Patch diff vs {parentTestBuildId}: {newFiles.Count} new ({FormatBytes(newSizeTotal)}){sizeNote}, {inheritedFiles.Count} inherited ({FormatBytes(inheritedSizeTotal)})");
            }
            else
            {
                newFiles.AddRange(files);
                foreach (var kvp in files) newSizeTotal += kvp.Value.data?.Length ?? 0;
            }

            // Publish live patch stats. ContentUploaderPanel polls this on
            // its repaint loop to render "X of Y bundles · Z MB patch / N MB
            // total" in the upload status row.
            CurrentPatchStats = new TestBuildPatchStats
            {
                parentTestBuildId = parentTestBuildId,
                newFiles = newFiles.Count,
                inheritedFiles = inheritedFiles.Count,
                patchSizeBytes = newSizeTotal,
                inheritedSizeBytes = inheritedSizeTotal,
                totalSizeBytes = newSizeTotal + inheritedSizeTotal,
                uploadedSoFar = 0,
            };
            UploadProgressChanged?.Invoke();

            var uploadTasks = new List<UniTask>();
            var uploadedFilesDict = new Dictionary<string, List<string>>();
            // Parallel-tracker lists for parity with the production flow;
            // test builds don't replay failed-only retries, but we still
            // count failures to short-circuit before commit if anything
            // went wrong (a committed test build with missing bundles
            // would 404 mid-download from the Test Channel UI).
            var thisRunSucceeded = new List<UploadedFileRecord>();
            var thisRunFailed = new List<UploadedFileRecord>();

            string presignPath = $"/api/test-content/{testBuildId}/uploadUrl";

            // Iterate newFiles (not the full files list) so we don't push
            // bytes for entries we'll inherit from the parent at commit.
            using (var uploadGate = new SemaphoreSlim(MaxConcurrentUploads, MaxConcurrentUploads))
            {
                foreach (var kvp in newFiles)
                {
                    string platform = kvp.Key;
                    UploadContentData file = kvp.Value;
                    uploadTasks.Add(TestBuildGatedUpload(uploadGate, presignPath, platform, file,
                        uploadedFilesDict, thisRunSucceeded, thisRunFailed));
                }
                await UniTask.WhenAll(uploadTasks);
            }

            int uploaded = thisRunSucceeded.Count;
            int failed = thisRunFailed.Count;
            Debug.Log($"[TestBuild] Uploaded {uploaded}/{newFiles.Count} new ({failed} failed), {inheritedFiles.Count} inherited from {parentTestBuildId ?? "(none)"} → committing {testBuildId}");

            if (failed > 0)
            {
                string errorMsg = $"{failed} of {files.Count} file(s) failed to upload — test build not committed.";
                Debug.LogError($"[TestBuild] {errorMsg}");
                callback?.Invoke(false, testBuildId, new DreamParkAPI.APIResponse(false, 0, errorMsg));
                return;
            }

            // Build commit body. Matches the production commitUpload shape
            // for uploadedFiles (per-platform string-array of storage
            // paths) so the backend's path-validation logic is the same
            // primitive both ends — see api.testContent.routes.js's
            // cleanedCatalog construction.
            JSONObject commitBody = new JSONObject(JSONObject.Type.Object);
            JSONObject uploadedFilesJson = new JSONObject(JSONObject.Type.Object);
            foreach (var kvp in uploadedFilesDict)
            {
                JSONObject arr = new JSONObject(JSONObject.Type.Array);
                foreach (var path in kvp.Value)
                    arr.Add(path);
                uploadedFilesJson.AddField(kvp.Key, arr);
            }
            commitBody.AddField("uploadedFiles", uploadedFilesJson);
            commitBody.AddField("title", title ?? "");
            commitBody.AddField("releaseNotes", releaseNotes ?? "");
            commitBody.AddField("contentName", contentName ?? "");
            if (!string.IsNullOrEmpty(logoAddress))
            {
                // Mirrors production /api/content/ contentItem.logoAddress.
                // Stored on the test_build doc so the Content Manager's
                // DrawContentLogo can pull the logo Texture2D from the
                // loaded catalog using this addressable key.
                commitBody.AddField("logoAddress", logoAddress);
            }

            // inheritedFiles → backend performs GCS server-side copy from
            // {parentTestBuildId}/{platform}/{filename} into this build's
            // prefix, then adds those paths to the catalog so download URLs
            // resolve as if everything had been uploaded directly.
            if (inheritedFiles.Count > 0 && !string.IsNullOrEmpty(parentTestBuildId))
            {
                JSONObject inheritedJson = new JSONObject(JSONObject.Type.Array);
                foreach (var inh in inheritedFiles)
                {
                    JSONObject entry = new JSONObject(JSONObject.Type.Object);
                    entry.AddField("platform", inh.platform);
                    entry.AddField("filename", inh.filename);
                    entry.AddField("sourceTestBuildId", parentTestBuildId);
                    inheritedJson.Add(entry);
                }
                commitBody.AddField("inheritedFiles", inheritedJson);
            }

            // Manifest carries the patch breakdown the viewer renders.
            // Per-file source labels let the UI show "X bundles uploaded
            // this round, Y bundles inherited from parent" with drill-down.
            JSONObject finalManifest = manifestSummary != null && manifestSummary.type == JSONObject.Type.Object
                ? manifestSummary
                : new JSONObject(JSONObject.Type.Object);
            if (!string.IsNullOrEmpty(parentTestBuildId))
                finalManifest.AddField("parentTestBuildId", parentTestBuildId);
            JSONObject stats = new JSONObject(JSONObject.Type.Object);
            stats.AddField("newFiles", newFiles.Count);
            stats.AddField("inheritedFiles", inheritedFiles.Count);
            stats.AddField("patchSizeBytes", newSizeTotal);
            stats.AddField("inheritedSizeBytes", inheritedSizeTotal);
            stats.AddField("totalSizeBytes", newSizeTotal + inheritedSizeTotal);
            finalManifest.AddField("stats", stats);
            // Per-file source list. "self" means uploaded directly in this
            // build; "test_..." means server-side-copied from that parent.
            JSONObject filesArr = new JSONObject(JSONObject.Type.Array);
            foreach (var kvp in newFiles)
            {
                JSONObject entry = new JSONObject(JSONObject.Type.Object);
                entry.AddField("platform", kvp.Key);
                entry.AddField("filename", kvp.Value.fileName);
                entry.AddField("size", kvp.Value.data?.Length ?? 0);
                entry.AddField("source", "self");
                filesArr.Add(entry);
            }
            foreach (var inh in inheritedFiles)
            {
                JSONObject entry = new JSONObject(JSONObject.Type.Object);
                entry.AddField("platform", inh.platform);
                entry.AddField("filename", inh.filename);
                entry.AddField("size", inh.sizeBytes);
                entry.AddField("source", parentTestBuildId ?? "");
                filesArr.Add(entry);
            }
            finalManifest.AddField("files", filesArr);
            commitBody.AddField("manifest", finalManifest);

            DreamParkAPI.POST($"/api/test-content/{testBuildId}/commit", AuthAPI.GetUserAuth(), commitBody, (success, response) =>
            {
                if (success)
                    Debug.Log($"[TestBuild] ✅ Committed {testBuildId} ({newFiles.Count} new + {inheritedFiles.Count} inherited)");
                else
                    Debug.LogError($"[TestBuild] ❌ Commit failed: {response?.error}");
                callback?.Invoke(success, testBuildId, response);
            });
        }

        // Helper for the patch-mode log line. Kept private + small to avoid
        // pulling in a utility class for a one-off formatter.
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        }

        // Stable-name metadata files should never be inherited across patch
        // builds. The catalog and hash are tiny, and uploading them fresh
        // guarantees the build always points at the current bundle set.
        private static bool ShouldAlwaysUploadInPatch(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".hash", StringComparison.OrdinalIgnoreCase);
        }

        // Test-build per-file pipeline — gated on the same SemaphoreSlim
        // production uploads use (MaxConcurrentUploads) so a test upload
        // running alongside a separate user's production upload can't
        // double the dev server's concurrent handler count.
        private static async UniTask TestBuildGatedUpload(
            SemaphoreSlim gate,
            string presignPath,
            string platform,
            UploadContentData file,
            Dictionary<string, List<string>> uploadedFilesDict,
            List<UploadedFileRecord> thisRunSucceeded,
            List<UploadedFileRecord> thisRunFailed)
        {
            await gate.WaitAsync();
            try
            {
                var result = await TestBuildHandleFileUpload(presignPath, platform, file);
                if (result.success)
                {
                    lock (uploadedFilesDict)
                    {
                        if (!uploadedFilesDict.ContainsKey(platform))
                            uploadedFilesDict[platform] = new List<string>();
                        uploadedFilesDict[platform].Add(result.uploadPath);
                        thisRunSucceeded.Add(new UploadedFileRecord(platform, file.fileName, result.uploadPath));
                    }
                    // Tick the patch-stats counter so the editor's status row
                    // can display live "uploaded X of Y" progress. Snapshot
                    // the reference once to avoid a NullReferenceException
                    // if CurrentPatchStats gets reset mid-upload by an
                    // overlapping call (shouldn't happen in normal flow but
                    // cheaper to guard than to debug).
                    var statsSnapshot = CurrentPatchStats;
                    if (statsSnapshot != null)
                    {
                        System.Threading.Interlocked.Increment(ref statsSnapshot.uploadedSoFar);
                        UploadProgressChanged?.Invoke();
                    }
                }
                else
                {
                    lock (uploadedFilesDict)
                    {
                        thisRunFailed.Add(new UploadedFileRecord(platform, file.fileName, null));
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static async UniTask<(bool success, string uploadPath)> TestBuildHandleFileUpload(string presignPath, string platform, UploadContentData file)
        {
            for (int attempt = 1; attempt <= MaxFileUploadAttempts; attempt++)
            {
                try
                {
                    var (ok, path) = await TestBuildTryUploadOnce(presignPath, platform, file, attempt);
                    if (ok)
                    {
                        MarkUploadComplete(platform, file.fileName, true);
                        return (true, path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠️ {file.fileName} test upload threw on attempt {attempt}: {ex.Message}");
                }
                if (attempt < MaxFileUploadAttempts)
                {
                    int backoffMs = (int)Math.Pow(2, attempt) * 500;
                    UpdateUploadProgress(platform, file.fileName, 0f);
                    await UniTask.Delay(backoffMs);
                }
            }
            MarkUploadComplete(platform, file.fileName, false);
            return (false, null);
        }

        private static async UniTask<(bool success, string uploadPath)> TestBuildTryUploadOnce(string presignPath, string platform, UploadContentData file, int attempt)
        {
            // Presign request: identical body shape to the production
            // /api/content/:contentId/uploadUrl endpoint so the backend
            // can use the same field-extraction code.
            var body = new JSONObject();
            body.AddField("platform", platform);
            body.AddField("filename", file.fileName);
            body.AddField("contentType", file.mimeType);

            var tcs = new UniTaskCompletionSource<(bool success, string url, string uploadPath)>();
            DreamParkAPI.POST(presignPath, AuthAPI.GetUserAuth(), body, (success, response) =>
            {
                if (success && response.json != null)
                {
                    var uploadUrl = response.json.GetField("uploadUrl")?.stringValue;
                    var uploadPath = response.json.GetField("uploadPath")?.stringValue;
                    tcs.TrySetResult((true, uploadUrl, uploadPath));
                }
                else
                {
                    if (attempt == 1)
                        Debug.LogWarning($"⚠️ Failed to get presigned URL for {file.fileName} (test build)");
                    tcs.TrySetResult((false, null, null));
                }
            });

            var (ok, uploadUrl, uploadPath) = await tcs.Task;
            if (!ok || string.IsNullOrEmpty(uploadUrl))
            {
                return (false, null);
            }

            var uploadTcs = new UniTaskCompletionSource<bool>();
            DreamParkAPI.PUT(uploadUrl, "", file.data, file.mimeType, (progress) =>
            {
                UpdateUploadProgress(platform, file.fileName, progress);
            }, (uploadSuccess, _) =>
            {
                uploadTcs.TrySetResult(uploadSuccess);
            });

            bool successUpload = await uploadTcs.Task;
            return (successUpload, successUpload ? uploadPath : null);
        }

        private static IEnumerator UploadFlow(string contentId, List<KeyValuePair<string, UploadContentData>> files, string releaseNotes, int? schemaVersion, JSONObject manifestSummary, List<InheritedBundleRecord> inheritedBundles, List<UploadedFileRecord> preUploadedFiles, Action<bool, APIResponse> callback)
        {
            // Convert coroutine to async UniTask for concurrency
            UploadFlowAsync(contentId, files, releaseNotes, schemaVersion, manifestSummary, inheritedBundles, preUploadedFiles, callback).Forget();
            yield break;
        }

        private static async UniTaskVoid UploadFlowAsync(string contentId, List<KeyValuePair<string, UploadContentData>> files, string releaseNotes, int? schemaVersion, JSONObject manifestSummary, List<InheritedBundleRecord> inheritedBundles, List<UploadedFileRecord> preUploadedFiles, Action<bool, DreamParkAPI.APIResponse> callback)
        {
            int uploaded = 0;
            int failed = 0;
            int versionNumber = 1;
            var uploadTasks = new List<UniTask>();
            var uploadedFilesDict = new Dictionary<string, List<string>>();

            // Track per-(platform, fileName) success so we can record the
            // full set of uploaded bundles into FailedBundleStore on a
            // partial-failure run. uploadedFilesDict only carries uploadPaths,
            // which is enough for commitUpload but loses the (platform,
            // fileName) identity we need to know *which* bundles can be
            // skipped on a Failed-Only retry.
            var thisRunSucceeded = new List<UploadedFileRecord>();
            var thisRunFailed = new List<UploadedFileRecord>();

            // 🔹 Seed the commit payload with previously-uploaded bundles
            // (the "Upload Failed Bundles" retry path). Those uploadPaths
            // came from /uploadUrl on a prior run; we never re-upload their
            // bytes here, we just make sure they show up in commitUpload's
            // uploadedFiles map.
            if (preUploadedFiles != null && preUploadedFiles.Count > 0)
            {
                foreach (var rec in preUploadedFiles)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.platform) || string.IsNullOrEmpty(rec.uploadPath))
                        continue;
                    if (!uploadedFilesDict.ContainsKey(rec.platform))
                        uploadedFilesDict[rec.platform] = new List<string>();
                    uploadedFilesDict[rec.platform].Add(rec.uploadPath);
                }
            }

            // 🔹 Create a parallel upload task for each file, gated by a
            // SemaphoreSlim so only MaxConcurrentUploads pipelines run at
            // once. Without this throttle, a 600-bundle upload fires 600
            // parallel /uploadUrl requests at the backend; that flood
            // (each call holds 2-5 MB of working memory in Express +
            // Firebase Admin SDK state) drives the dyno into R14 and the
            // worker gets killed mid-request, surfacing as H18s on the
            // router. Throttling the *whole* per-file unit (presign + PUT
            // to GCS) keeps the server's queue depth bounded and also
            // avoids blowing past your client uplink — past 6 concurrent
            // PUTs you're just splitting the same pipe more ways anyway.
            //
            // The semaphore wraps HandleFileUpload, which internally does
            // presign → PUT → retry-up-to-3-times. Slot stays held for the
            // full lifetime of a file's attempt(s), then releases.
            using (var uploadGate = new SemaphoreSlim(MaxConcurrentUploads, MaxConcurrentUploads))
            {
                foreach (var kvp in files)
                {
                    string platform = kvp.Key;
                    UploadContentData file = kvp.Value;

                    uploadTasks.Add(GatedUpload(uploadGate, contentId, platform, file,
                        uploadedFilesDict, thisRunSucceeded, thisRunFailed));
                }

                // 🔹 Wait for all uploads to complete concurrently
                await UniTask.WhenAll(uploadTasks);
            }

            // Counts are derived from the per-result lists rather than
            // tracked in shared int counters — the lists are written under
            // the same lock that gates uploadedFilesDict, so once WhenAll
            // returns the sizes are authoritative without needing
            // Interlocked plumbing (which doesn't work for captured locals
            // in C# anyway).
            uploaded = thisRunSucceeded.Count;
            failed = thisRunFailed.Count;

            bool overallSuccess = failed == 0;
            string summary = $"Uploaded {uploaded}/{files.Count} files ({failed} failed)";
            Debug.Log($"[ContentUploader] {summary}");

            // ⚠️ Abort the commit if ANY file uploads failed. Calling
            // commitUpload with a partial uploadedFiles map would create a
            // backend version that references files that aren't actually in
            // Storage — broken for players, AND the panel would save the
            // local manifest baseline as if everything succeeded, leaving
            // a mismatched state where the next upload's diff says "nothing
            // changed" even though most bundles are missing on the server.
            // Surfacing the failure here lets the panel skip baseline save
            // and the user simply Try Reupload to retry the whole batch (or
            // just the failed bundles — see FailedBundleStore + the dialog
            // on the Try Reupload button).
            if (!overallSuccess)
            {
            #if UNITY_EDITOR && !DREAMPARKCORE
                // Persist a record of this failed run so the Try Reupload
                // dialog can offer "Upload Failed Bundles (X of Y)" next
                // time. succeeded = preUploadedFiles (carried in from a
                // prior failed run, if any) ∪ this run's successes; failed
                // = this run's failures. Wrapped in #if UNITY_EDITOR
                // because the store itself is editor-only (UNITY_EDITOR
                // gate in FailedBundleStore.cs) and runtime builds have no
                // use for the record. FailedBundleStore is in namespace
                // DreamPark (a parent of this file's DreamPark.API),
                // qualified explicitly here to keep the cross-namespace
                // lookup unambiguous for anyone scanning the diff later.
                try
                {
                    int totalFiles = files.Count + (preUploadedFiles?.Count ?? 0);
                    var record = new global::DreamPark.FailedBundleRecord
                    {
                        contentId = contentId,
                        failedAtUtc = DateTime.UtcNow.ToString("o"),
                        totalFiles = totalFiles,
                    };
                    if (preUploadedFiles != null)
                    {
                        foreach (var p in preUploadedFiles)
                        {
                            if (p == null) continue;
                            record.succeeded.Add(new global::DreamPark.FailedBundleEntry
                            {
                                platform = p.platform,
                                fileName = p.fileName,
                                uploadPath = p.uploadPath,
                            });
                        }
                    }
                    foreach (var s in thisRunSucceeded)
                    {
                        record.succeeded.Add(new global::DreamPark.FailedBundleEntry
                        {
                            platform = s.platform,
                            fileName = s.fileName,
                            uploadPath = s.uploadPath,
                        });
                    }
                    foreach (var f in thisRunFailed)
                    {
                        record.failed.Add(new global::DreamPark.FailedBundleEntry
                        {
                            platform = f.platform,
                            fileName = f.fileName,
                            uploadPath = null,
                        });
                    }
                    global::DreamPark.FailedBundleStore.Save(record);
                }
                catch (Exception storeEx)
                {
                    Debug.LogWarning($"[ContentUploader] Could not record failed-run state: {storeEx.Message}");
                }
            #endif

                string errorMsg = $"{failed} of {files.Count} file(s) failed to upload — version not committed. Click Try Reupload to retry.";
                Debug.LogError($"[ContentUploader] {errorMsg}");
                callback?.Invoke(false, new DreamParkAPI.APIResponse(false, 0, errorMsg));
                return;
            }

            // 🔹 Build commit body. Both nested JSONObjects are explicitly
            // created as Type.Object so they always serialize as `{}` rather
            // than as JSON `null` when empty — Express's body-parser would
            // reject `null` here with a 400 before our route ever runs.
            JSONObject commitBody = new JSONObject(JSONObject.Type.Object);
            JSONObject uploadedFilesJson = new JSONObject(JSONObject.Type.Object);

            foreach (var kvp in uploadedFilesDict)
            {
                JSONObject arr = new JSONObject(JSONObject.Type.Array);
                foreach (var path in kvp.Value)
                    arr.Add(path);
                uploadedFilesJson.AddField(kvp.Key, arr);
            }

            commitBody.AddField("uploadedFiles", uploadedFilesJson);
            commitBody.AddField("versionNumber", versionNumber);
            commitBody.AddField("releaseNotes", releaseNotes ?? "");
            if (inheritedBundles != null && inheritedBundles.Count > 0) {
                JSONObject inheritedJson = new JSONObject(JSONObject.Type.Array);
                foreach (var rec in inheritedBundles)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.platform) || string.IsNullOrEmpty(rec.fileName))
                        continue;
                    JSONObject entry = new JSONObject(JSONObject.Type.Object);
                    entry.AddField("platform", rec.platform);
                    entry.AddField("filename", rec.fileName);
                    inheritedJson.Add(entry);
                }
                if (inheritedJson.list != null && inheritedJson.list.Count > 0)
                {
                    commitBody.AddField("inheritedFiles", inheritedJson);
                }
            }
            if (schemaVersion.HasValue) {
                commitBody.AddField("schemaVersion", schemaVersion.Value);
            }
            if (manifestSummary != null) {
                commitBody.AddField("manifest", manifestSummary);
            }

            // 🔹 Commit upload version metadata to server
            DreamParkAPI.POST($"/api/content/{contentId}/commitUpload", AuthAPI.GetUserAuth(), commitBody,
                (success, response) =>
                {
                    Debug.Log(success ? "✅ Version committed!" : $"❌ Commit failed: {response.error}");
                #if UNITY_EDITOR && !DREAMPARKCORE
                    if (success)
                    {
                        // Everything for this contentId is now committed — the
                        // failed-run record (if any) is stale and would only
                        // confuse the Try Reupload dialog if left around.
                        try { global::DreamPark.FailedBundleStore.Clear(contentId); }
                        catch (Exception clearEx)
                        {
                            Debug.LogWarning($"[ContentUploader] Could not clear failed-run record: {clearEx.Message}");
                        }
                    }
                #endif
                    callback?.Invoke(success, response);
                });
        }

        // Number of times we retry the GCS PUT before giving up. The whole
        // upload aborts if even one file ultimately fails, so retries here
        // are about absorbing transient network blips (SSL drops, DNS
        // hiccups, brief 5xx) without forcing the user to start over.
        private const int MaxFileUploadAttempts = 3;

        // How many file pipelines (presign + PUT to GCS, including retries)
        // run in parallel. Six is the sweet spot: enough to saturate a
        // typical home/office uplink, low enough that the dev server's
        // /uploadUrl handler doesn't pile up Express + Firebase Admin SDK
        // state across hundreds of concurrent requests and OOM the dyno.
        // Bump higher (12-16) once a server-side /uploadUrls plural
        // endpoint exists that signs a batch of URLs in one round trip.
        private const int MaxConcurrentUploads = 6;

        // Per-file upload pipeline gated by a SemaphoreSlim. Only N of these
        // run at a time across the whole upload run; the rest queue waiting
        // for a slot. The slot is held for the *entire* HandleFileUpload
        // duration (presign + PUT + any retries) so we throttle the actual
        // resource we care about — concurrent server-side handler depth —
        // not just the moment a request starts.
        private static async UniTask GatedUpload(
            SemaphoreSlim gate,
            string contentId,
            string platform,
            UploadContentData file,
            Dictionary<string, List<string>> uploadedFilesDict,
            List<UploadedFileRecord> thisRunSucceeded,
            List<UploadedFileRecord> thisRunFailed)
        {
            await gate.WaitAsync();
            try
            {
                var result = await HandleFileUpload(contentId, platform, file);
                if (result.success)
                {
                    lock (uploadedFilesDict)
                    {
                        if (!uploadedFilesDict.ContainsKey(platform))
                            uploadedFilesDict[platform] = new List<string>();
                        uploadedFilesDict[platform].Add(result.uploadPath);
                        thisRunSucceeded.Add(new UploadedFileRecord(platform, file.fileName, result.uploadPath));
                    }
                }
                else
                {
                    lock (uploadedFilesDict)
                    {
                        thisRunFailed.Add(new UploadedFileRecord(platform, file.fileName, null));
                    }
                }
            }
            finally
            {
                // Always release, even if HandleFileUpload threw — without
                // this, one unexpected exception starves the rest of the
                // queue and the upload hangs forever waiting for slots.
                gate.Release();
            }
        }

        private static async UniTask<(bool success, string uploadPath)> HandleFileUpload(string contentId, string platform, UploadContentData file)
        {
            for (int attempt = 1; attempt <= MaxFileUploadAttempts; attempt++)
            {
                try
                {
                    var (ok, path) = await TryUploadOnce(contentId, platform, file, attempt);
                    if (ok)
                    {
                        if (attempt > 1)
                            Debug.Log($"✅ Uploaded {file.fileName} ({platform}) on retry {attempt}/{MaxFileUploadAttempts}.");
                        else
                            Debug.Log($"✅ Uploaded {file.fileName} ({platform})");
                        MarkUploadComplete(platform, file.fileName, true);
                        return (true, path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠️ {file.fileName} upload threw on attempt {attempt}/{MaxFileUploadAttempts}: {ex.Message}");
                }

                // Failed this attempt. Wait with exponential backoff before
                // the next, unless this was the final attempt.
                if (attempt < MaxFileUploadAttempts)
                {
                    int backoffMs = (int)Math.Pow(2, attempt) * 500; // 1s, 2s, 4s, …
                    Debug.LogWarning($"⚠️ {file.fileName} failed (attempt {attempt}/{MaxFileUploadAttempts}). Retrying in {backoffMs}ms...");
                    // Reset progress so the row reflects the retry instead of leaving the failed run on screen.
                    UpdateUploadProgress(platform, file.fileName, 0f);
                    await UniTask.Delay(backoffMs);
                }
            }

            Debug.LogError($"❌ Upload failed for {file.fileName} after {MaxFileUploadAttempts} attempts.");
            MarkUploadComplete(platform, file.fileName, false);
            return (false, null);
        }

        // One attempt at the two-step "request presigned URL → PUT to GCS"
        // dance. Returns (success, uploadPath) — uploadPath is the storage
        // key the backend records for commitUpload.
        private static async UniTask<(bool success, string uploadPath)> TryUploadOnce(string contentId, string platform, UploadContentData file, int attempt)
        {
            // Step 1: request presigned URL
            var body = new JSONObject();
            body.AddField("platform", platform);
            body.AddField("filename", file.fileName);
            body.AddField("contentType", file.mimeType);

            var tcs = new UniTaskCompletionSource<(bool success, string url, string uploadPath)>();
            DreamParkAPI.POST($"/api/content/{contentId}/uploadUrl", AuthAPI.GetUserAuth(), body, (success, response) =>
            {
                if (success && response.json != null)
                {
                    var uploadUrl = response.json.GetField("uploadUrl")?.stringValue;
                    var uploadPath = response.json.GetField("uploadPath")?.stringValue;
                    tcs.TrySetResult((true, uploadUrl, uploadPath));
                }
                else
                {
                    if (attempt == 1)
                        Debug.LogWarning($"⚠️ Failed to get presigned URL for {file.fileName}");
                    tcs.TrySetResult((false, null, null));
                }
            });

            var (ok, uploadUrl, uploadPath) = await tcs.Task;
            if (!ok || string.IsNullOrEmpty(uploadUrl))
            {
                return (false, null);
            }

            // Step 2: upload to Firebase
            var uploadTcs = new UniTaskCompletionSource<bool>();
            DreamParkAPI.PUT(uploadUrl, "", file.data, file.mimeType, (progress) =>
            {
                UpdateUploadProgress(platform, file.fileName, progress);
            }, (success, _) =>
            {
                uploadTcs.TrySetResult(success);
            });

            bool successUpload = await uploadTcs.Task;
            return (successUpload, successUpload ? uploadPath : null);
        }

        public static void GetTagLayerSchema(Action<bool, APIResponse> callback) {
            DreamParkAPI.GET("/api/content/schema", AuthAPI.GetUserAuth(), (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        public static void SyncTagLayerSchema(string contentId, int baseVersion, List<string> tags, List<string> layers, Action<bool, APIResponse> callback) {
            var body = new JSONObject();
            body.AddField("contentId", contentId ?? "");
            body.AddField("baseVersion", baseVersion);

            var observed = new JSONObject();
            var tagsArray = new JSONObject(JSONObject.Type.Array);
            foreach (var tag in tags ?? new List<string>()) {
                tagsArray.Add(tag ?? "");
            }
            observed.AddField("tags", tagsArray);

            var layersArray = new JSONObject(JSONObject.Type.Array);
            foreach (var layer in layers ?? new List<string>()) {
                layersArray.Add(layer ?? "");
            }
            observed.AddField("layers", layersArray);
            body.AddField("observed", observed);

            DreamParkAPI.POST("/api/content/schema/sync", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

#if DREAMPARKCORE
        // Core/admin only: publishing the canonical tag/layer schema and
        // accepting creator proposals are platform-wide actions. Backend now
        // enforces admin (requireAdminJson). Compiled out of the SDK so a
        // third-party build can't call them. Creators use SyncTagLayerSchema
        // (propose), which remains available below.
        public static void PublishTagLayerSchemaFromCore(string contentId, List<string> tags, List<string> layers, Action<bool, APIResponse> callback)
        {
            var body = new JSONObject();
            body.AddField("contentId", contentId ?? "");

            var observed = new JSONObject();
            var tagsArray = new JSONObject(JSONObject.Type.Array);
            foreach (var tag in tags ?? new List<string>()) {
                tagsArray.Add(tag ?? "");
            }
            observed.AddField("tags", tagsArray);

            var layersArray = new JSONObject(JSONObject.Type.Array);
            foreach (var layer in layers ?? new List<string>()) {
                layersArray.Add(layer ?? "");
            }
            observed.AddField("layers", layersArray);
            body.AddField("observed", observed);

            DreamParkAPI.POST("/api/content/schema/publish-core", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        public static void AcceptTagLayerSchemaProposal(string contentId, Action<bool, APIResponse> callback)
        {
            var body = new JSONObject();
            body.AddField("contentId", contentId ?? "");
            DreamParkAPI.POST("/api/content/schema/accept", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }
#endif

        public static void GetTagLayerSchemaProposal(string contentId, Action<bool, APIResponse> callback)
        {
            string encodedContentId = UnityWebRequest.EscapeURL(contentId ?? "");

            DreamParkAPI.GET($"/api/content/schema/proposal/{encodedContentId}", AuthAPI.GetUserAuth(), (success, response) =>
            {
                if (success || response == null || (response.statusCode != 404 && response.statusCode != 405))
                {
                    callback?.Invoke(success, response);
                    return;
                }

                // Backward compatibility with older backend shape.
                DreamParkAPI.GET($"/api/content/schema/proposal?contentId={encodedContentId}", AuthAPI.GetUserAuth(), (fallbackSuccess, fallbackResponse) =>
                {
                    callback?.Invoke(fallbackSuccess, fallbackResponse);
                });
            });
        }

        public static TagLayerSchemaSyncResult ParseTagLayerSchemaSyncResult(APIResponse response)
        {
            var result = new TagLayerSchemaSyncResult();
            if (response?.json == null) {
                return result;
            }

            var json = response.json;
            result.updated = json.HasField("updated") && json.GetField("updated").boolValue;
            if (json.HasField("schemaVersion")) {
                result.schemaVersion = json.GetField("schemaVersion").intValue;
            }

            if (json.HasField("addedTags") && json.GetField("addedTags").list != null) {
                result.addedTags = new List<string>();
                foreach (var v in json.GetField("addedTags").list)
                {
                    var value = v.stringValue ?? "";
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.addedTags.Add(value);
                    }
                }
            }
            if (json.HasField("addedLayers") && json.GetField("addedLayers").list != null) {
                result.addedLayers = new List<string>();
                foreach (var v in json.GetField("addedLayers").list)
                {
                    var value = v.stringValue ?? "";
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.addedLayers.Add(value);
                    }
                }
            }
            if (json.HasField("layerConflicts") && json.GetField("layerConflicts").list != null) {
                result.layerConflicts = new List<string>();
                foreach (var v in json.GetField("layerConflicts").list)
                {
                    var value = v.stringValue ?? "";
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.layerConflicts.Add(value);
                    }
                }
            }
            result.proposalPending = json.HasField("proposalPending") && json.GetField("proposalPending").boolValue;
            if (json.HasField("proposal") && json.GetField("proposal").HasField("contentId")) {
                result.proposalContentId = json.GetField("proposal").GetField("contentId").stringValue ?? "";
            }

            if (json.HasField("schema")) {
                var schemaObj = json.GetField("schema");
                var schema = new TagLayerSchemaSnapshot();
                if (schemaObj.HasField("version")) {
                    schema.version = schemaObj.GetField("version").intValue;
                }
                if (schemaObj.HasField("tags") && schemaObj.GetField("tags").list != null) {
                    schema.tags = new List<string>();
                    foreach (var v in schemaObj.GetField("tags").list)
                    {
                        var value = v.stringValue ?? "";
                        if (!string.IsNullOrEmpty(value))
                        {
                            schema.tags.Add(value);
                        }
                    }
                }
                if (schemaObj.HasField("layers") && schemaObj.GetField("layers").list != null) {
                    schema.layers = new List<string>();
                    foreach (var v in schemaObj.GetField("layers").list)
                    {
                        schema.layers.Add(v.stringValue ?? "");
                    }
                }
                result.schema = schema;
            }

            if (result.schemaVersion == 0 && result.schema != null && result.schema.version > 0) {
                result.schemaVersion = result.schema.version;
            }

            return result;
        }

        /// <summary>
        /// Parse the response from GET /api/content/schema into a snapshot.
        /// Tries the nested {schema: {tags, layers}} shape first (matching the
        /// POST /sync response's subfield), then falls back to top-level
        /// {tags, layers}. Returns null if neither shape is present.
        /// </summary>
        public static TagLayerSchemaSnapshot ParseTagLayerSchema(APIResponse response)
        {
            if (response?.json == null) return null;
            var json = response.json;

            // Shape A: { schema: { version, tags, layers } }
            if (json.HasField("schema"))
            {
                return ReadSnapshot(json.GetField("schema"));
            }

            // Shape B: { tags: [...], layers: [...], version: N }
            if (json.HasField("tags") || json.HasField("layers"))
            {
                return ReadSnapshot(json);
            }

            return null;
        }

        private static TagLayerSchemaSnapshot ReadSnapshot(JSONObject obj)
        {
            if (obj == null) return null;
            var snap = new TagLayerSchemaSnapshot();

            if (obj.HasField("version"))
            {
                snap.version = obj.GetField("version").intValue;
            }

            if (obj.HasField("tags") && obj.GetField("tags").list != null)
            {
                snap.tags = new List<string>();
                foreach (var v in obj.GetField("tags").list)
                {
                    var value = v.stringValue ?? "";
                    if (!string.IsNullOrEmpty(value))
                    {
                        snap.tags.Add(value);
                    }
                }
            }

            if (obj.HasField("layers") && obj.GetField("layers").list != null)
            {
                snap.layers = new List<string>();
                foreach (var v in obj.GetField("layers").list)
                {
                    snap.layers.Add(v.stringValue ?? "");
                }
            }

            return snap;
        }

        public static TagLayerSchemaProposal ParseTagLayerSchemaProposal(APIResponse response)
        {
            var result = new TagLayerSchemaProposal();
            if (response?.json == null)
            {
                return result;
            }

            var json = response.json;
            if (json.HasField("proposal"))
            {
                json = json.GetField("proposal");
            }

            if (json.HasField("contentId"))
            {
                result.contentId = json.GetField("contentId").stringValue ?? "";
            }
            if (json.HasField("status"))
            {
                result.status = json.GetField("status").stringValue ?? "";
            }
            if (json.HasField("baseVersion"))
            {
                result.baseVersion = json.GetField("baseVersion").intValue;
            }

            if (json.HasField("proposedTags") && json.GetField("proposedTags").list != null)
            {
                foreach (var tagObj in json.GetField("proposedTags").list)
                {
                    var tag = tagObj.stringValue ?? "";
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        result.proposedTags.Add(tag);
                    }
                }
            }

            if (json.HasField("proposedLayers") && json.GetField("proposedLayers").list != null)
            {
                foreach (var layerObj in json.GetField("proposedLayers").list)
                {
                    if (layerObj == null)
                    {
                        continue;
                    }

                    var proposal = new TagLayerSchemaLayerProposal();
                    bool hasValue = false;

                    if (layerObj.HasField("index"))
                    {
                        proposal.index = layerObj.GetField("index").intValue;
                        hasValue = true;
                    }
                    else if (layerObj.HasField("i"))
                    {
                        proposal.index = layerObj.GetField("i").intValue;
                        hasValue = true;
                    }

                    if (layerObj.HasField("name"))
                    {
                        proposal.name = layerObj.GetField("name").stringValue ?? "";
                        hasValue = true;
                    }
                    else if (layerObj.HasField("n"))
                    {
                        proposal.name = layerObj.GetField("n").stringValue ?? "";
                        hasValue = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(layerObj.stringValue))
                    {
                        proposal.name = layerObj.stringValue;
                        hasValue = true;
                    }

                    if (hasValue && !string.IsNullOrWhiteSpace(proposal.name))
                    {
                        result.proposedLayers.Add(proposal);
                    }
                }
            }

            if (json.HasField("layerConflicts") && json.GetField("layerConflicts").list != null)
            {
                foreach (var conflictObj in json.GetField("layerConflicts").list)
                {
                    var conflict = conflictObj.stringValue ?? "";
                    if (!string.IsNullOrWhiteSpace(conflict))
                    {
                        result.layerConflicts.Add(conflict);
                    }
                }
            }

            return result;
        }

#if DREAMPARKCORE
        // Runtime content fetch — used by the consumer app to download an installed
        // attraction's manifest. Hits /app/content/* which requires the static API key,
        // so this is core-only. SDK creators upload content via the user-auth flow
        // (GetContent / commitUpload above) and never call GetAppContent directly.
        public static void GetAppContent(string contentId, string platform, int installedVersion, Action<bool, APIResponse> callback) {
            GetAppContent(contentId, platform, installedVersion, null, callback);
        }

        public static void GetAppContent(string contentId, string platform, int installedVersion, bool? forceBetaOverride, Action<bool, APIResponse> callback) {
            // Check if beta mode is enabled
            bool betaMode;
            if (forceBetaOverride.HasValue) {
                betaMode = forceBetaOverride.Value;
            } else {
#if UNITY_IOS
                betaMode = NativeInterfaceManager.BetaMode;
#elif DREAMPARK_FORCE_BETA_CONTENT
                betaMode = true;
#else
                betaMode = false;
#endif
            }
            string url = betaMode
                ? $"/app/content/{contentId}/{platform}/{installedVersion}?beta=true"
                : $"/app/content/{contentId}/{platform}/{installedVersion}";

            Debug.Log($"[ContentAPI] GetAppContent - contentId: {contentId}, beta: {betaMode}, url: {url}");
            DreamParkAPI.GET(url, AuthAPI.GetAPIKey(), (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        public static async Task<JSONObject> GetAppContentAsync(string contentId, string platform, int installedVersion) {
            return await GetAppContentAsync(contentId, platform, installedVersion, null);
        }

        public static async Task<JSONObject> GetAppContentAsync(string contentId, string platform, int installedVersion, bool? forceBetaOverride) {
            var tcs = new UniTaskCompletionSource<JSONObject>();
            GetAppContent(contentId, platform, installedVersion, forceBetaOverride, (success, response) => {
                if (success) {
                    tcs.TrySetResult(response.json);
                } else {
                    Debug.LogError("Failed to get content: " + response.error);
                    tcs.TrySetException(new Exception(response.error));
                }
            });
            return await tcs.Task;
        }
#endif // DREAMPARKCORE
    }
}
