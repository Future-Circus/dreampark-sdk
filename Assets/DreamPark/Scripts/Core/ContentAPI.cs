using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Defective.JSON;
using UnityEngine;
using UnityEngine.Networking;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{  
    public class UploadContentData {
        public string filePath = null;
        public string fileName = null;
        public string mimeType = null;
        public byte[] data = null;

        public UploadContentData(string filePath, byte[] data) {
            this.filePath = filePath;
            this.fileName = Path.GetFileName(filePath);
            if (Path.GetExtension(fileName) == ".json") {
                mimeType = "application/json";
            } else if (Path.GetExtension(fileName) == ".hash") {
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
                var files = Directory.GetFiles(directory);
                foreach (var filePath in files)
                {
                    byte[] fileBytes = File.ReadAllBytes(filePath);
                    data.Add(new UploadContentData(filePath, fileBytes));
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
            // 1️⃣ Collect local files for all platforms
            UploadContentRequest data = new UploadContentRequest();
            var files = data.ToList();

            if (files == null || files.Count == 0)
            {
                Debug.LogWarning("No files found to upload.");
                callback?.Invoke(false, new DreamParkAPI.APIResponse(false, 0, "No files found"));
                return;
            }

            int totalCollected = files.Count;
            if (skipFileKeys != null && skipFileKeys.Count > 0)
            {
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
                Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, callback));
            #else
                CoroutineRunner.Run(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, callback));
            #endif
                return;
            }

            InitializeUploadProgress(files);

            Debug.Log($"[ContentAPI] Uploading {files.Count} files for content {contentId}");

        #if UNITY_EDITOR
            Unity.EditorCoroutines.Editor.EditorCoroutineUtility.StartCoroutineOwnerless(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, callback));
        #else
            CoroutineRunner.Run(UploadFlow(contentId, files, releaseNotes, schemaVersion, manifestSummary, callback));
        #endif
        }

        public static void ApproveContent(string contentId, int versionNumber, bool requiresUpdate, Action<bool, APIResponse> callback) {
            JSONObject body = new JSONObject();
            body.AddField("versionNumber", versionNumber);
            body.AddField("requiresUpdate", requiresUpdate);
            DreamParkAPI.POST($"/api/content/{contentId}/approve", AuthAPI.GetUserAuth(), body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        private static IEnumerator UploadFlow(string contentId, List<KeyValuePair<string, UploadContentData>> files, string releaseNotes, int? schemaVersion, JSONObject manifestSummary, Action<bool, APIResponse> callback)
        {
            // Convert coroutine to async UniTask for concurrency
            UploadFlowAsync(contentId, files, releaseNotes, schemaVersion, manifestSummary, callback).Forget();
            yield break;
        }

        private static async UniTaskVoid UploadFlowAsync(string contentId, List<KeyValuePair<string, UploadContentData>> files, string releaseNotes, int? schemaVersion, JSONObject manifestSummary, Action<bool, DreamParkAPI.APIResponse> callback)
        {
            int uploaded = 0;
            int failed = 0;
            int versionNumber = 1;
            var uploadTasks = new List<UniTask>();
            var uploadedFilesDict = new Dictionary<string, List<string>>();

            // 🔹 Create a parallel upload task for each file
            foreach (var kvp in files)
            {
                string platform = kvp.Key;
                UploadContentData file = kvp.Value;

                uploadTasks.Add(HandleFileUpload(contentId, platform, file)
                    .ContinueWith(result =>
                    {
                        if (result.success)
                        {
                            uploaded++;
                            lock (uploadedFilesDict)
                            {
                                if (!uploadedFilesDict.ContainsKey(platform))
                                    uploadedFilesDict[platform] = new List<string>();
                                uploadedFilesDict[platform].Add(result.uploadPath);
                            }
                        }
                        else
                        {
                            failed++;
                        }
                    }));
            }

            // 🔹 Wait for all uploads to complete concurrently
            await UniTask.WhenAll(uploadTasks);

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
            // and the user simply Try Reupload to retry the whole batch.
            if (!overallSuccess)
            {
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
                    callback?.Invoke(success, response);
                });
        }

        // Number of times we retry the GCS PUT before giving up. The whole
        // upload aborts if even one file ultimately fails, so retries here
        // are about absorbing transient network blips (SSL drops, DNS
        // hiccups, brief 5xx) without forcing the user to start over.
        private const int MaxFileUploadAttempts = 3;

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
