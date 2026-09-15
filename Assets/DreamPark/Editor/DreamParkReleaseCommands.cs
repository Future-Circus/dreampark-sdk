#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DreamPark.API;
using DreamPark.PreUploadChecks;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Stable, machine-readable release surface for Unity CLI and release agents.
    /// DreamPark semantics live here beside the SDK rather than in a host-specific shell script.
    /// Mutating commands are explicit, require release notes, and return structured verification data.
    /// </summary>
    public static class DreamParkReleaseCommands
    {
        private const string SDKAssetPath = "Assets/DreamPark";
        private const string VersionResourcePath = "Assets/DreamPark/Resources/DreamParkSDKVersion.json";

        [CliCommand("dreampark_auth_status", "Check the local DreamPark SDK session without returning its bearer token.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static object AuthStatus()
        {
            return new
            {
                authenticated = AuthAPI.isLoggedIn,
                email = AuthAPI.isLoggedIn ? AuthAPI.email : null,
                expiresAtUnixMs = AuthAPI.sessionExpiresAt > 0 ? (long?)AuthAPI.sessionExpiresAt : null,
                expiresInHours = AuthAPI.sessionExpiresInHours >= 0 ? (double?)AuthAPI.sessionExpiresInHours : null,
                action = AuthAPI.isLoggedIn ? "none" : "Run dreampark_auth_open, then complete the email-code sign-in in Unity."
            };
        }

        [CliCommand("dreampark_auth_open", "Open DreamPark's passwordless sign-in window in this exact Unity Editor. The email code stays between the human and Unity and is never returned to the caller.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static object OpenAuth()
        {
            if (AuthAPI.isLoggedIn)
                return new { opened = false, authenticated = true, email = AuthAPI.email };

            AuthPopup.Show();
            return new
            {
                opened = true,
                authenticated = false,
                message = "Complete the email-code sign-in in the Unity window, then run dreampark_auth_status again."
            };
        }

        [CliCommand("dreampark_content_inspect", "Inspect a local DreamPark content package and, when signed in, its current backend version. Read-only.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/content" })]
        public static async Task<object> InspectContent(
            [CliArg("content_id", "Folder name under Assets/Content and backend content ID.", Required = true)] string contentId)
        {
            contentId = NormalizeContentId(contentId);
            string assetPath = "Assets/Content/" + contentId;
            var local = new
            {
                exists = AssetDatabase.IsValidFolder(assetPath),
                assetPath,
                prefabCount = AssetDatabase.IsValidFolder(assetPath)
                    ? AssetDatabase.FindAssets("t:Prefab", new[] { assetPath }).Length
                    : 0,
                sceneCount = AssetDatabase.IsValidFolder(assetPath)
                    ? AssetDatabase.FindAssets("t:Scene", new[] { assetPath }).Length
                    : 0,
                luaScriptCount = Directory.Exists(assetPath)
                    ? Directory.GetFiles(assetPath, "*.lua.txt", SearchOption.AllDirectories).Length
                    : 0
            };

            if (!AuthAPI.isLoggedIn)
                return new { success = true, authenticated = false, local, remote = (object)null, action = "Run dreampark_auth_open." };

            var response = await AwaitAPI(callback => ContentAPI.GetContent(contentId, callback));
            return new
            {
                success = response.success,
                authenticated = true,
                local,
                remote = response.response != null ? response.response.json : null,
                error = response.success ? null : ErrorOf(response.response, "Could not inspect backend content.")
            };
        }

        [CliCommand("dreampark_content_preflight", "Run the strict DreamPark content release checker. Automation fails closed when a check errors, is skipped, or reports an unapproved warning/blocker.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/content", "dreampark/releases" })]
        public static DreamParkPreflightResponse ContentPreflight(
            [CliArg("content_id", "Folder name under Assets/Content and backend content ID.", Required = true)] string contentId,
            [CliArg("save_scenes", "Save open scenes/assets before checks that inspect scene YAML.")] bool saveScenes = false,
            [CliArg("allow_warnings", "Allow non-blocking warnings. Requires override_reason.")] bool allowWarnings = false,
            [CliArg("override_reason", "Human-reviewed reason for allowing warnings. Never bypasses blockers, errors, or skipped checks.")] string overrideReason = null)
        {
            contentId = NormalizeContentId(contentId);
            var result = new DreamParkPreflightResponse
            {
                contentId = contentId,
                checkedAtUtc = DateTime.UtcNow.ToString("O"),
                authenticated = AuthAPI.isLoggedIn,
                checks = new List<DreamParkReleaseCheck>()
            };

            Add(result, "auth", AuthAPI.isLoggedIn ? "passed" : "failed",
                AuthAPI.isLoggedIn ? "DreamPark session is available for " + AuthAPI.email + "." : "DreamPark sign-in is required. Run dreampark_auth_open.");

            string contentPath = "Assets/Content/" + contentId;
            bool folderExists = AssetDatabase.IsValidFolder(contentPath);
            Add(result, "content-folder", folderExists ? "passed" : "failed",
                folderExists ? contentPath + " exists." : contentPath + " does not exist.");

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                Add(result, "editor-state", "failed", "Unity is compiling or updating. Wait for the Editor to become idle.");
            else if (EditorApplication.isPlayingOrWillChangePlaymode)
                Add(result, "editor-state", "failed", "Exit Play Mode before preparing a release.");
            else
                Add(result, "editor-state", "passed", "Unity Editor is idle.");

            if (!folderExists)
            {
                result.ready = false;
                return result;
            }

            if (saveScenes)
            {
                bool saved = EditorSceneManager.SaveOpenScenes();
                if (saved) AssetDatabase.SaveAssets();
                Add(result, "saved-state", saved ? "passed" : "failed",
                    saved ? "Open scenes and assets were saved before inspection." : "Unity could not save every open scene.");
            }
            else
            {
                var dirtyScenes = Enumerable.Range(0, EditorSceneManager.sceneCount)
                    .Select(EditorSceneManager.GetSceneAt)
                    .Where(scene => scene.isLoaded && scene.isDirty)
                    .Select(scene => string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path)
                    .ToList();
                Add(result, "saved-state", dirtyScenes.Count == 0 ? "passed" : "failed",
                    dirtyScenes.Count == 0
                        ? "Open scenes have no unsaved changes."
                        : "Save these open scenes before releasing: " + string.Join(", ", dirtyScenes));
            }

            try
            {
                var lua = LuaSurfaceScanner.Analyze();
                if (lua == null || lua.IsClean)
                    Add(result, "lua-surface", "passed", "No blocked Lua API usage was found.");
                else if (lua.HasBlocked)
                    Add(result, "lua-surface", "failed", lua.report);
                else
                    Add(result, "lua-surface", "warning", lua.report);
            }
            catch (Exception ex)
            {
                Add(result, "lua-surface", "failed", "Lua surface scan errored: " + ex.Message);
            }

            try
            {
                var report = PreUploadCheckRunner.RunAll(contentId, null, scenesAreSaved: saveScenes);
                foreach (var check in report.results)
                {
                    string status;
                    switch (check.outcome)
                    {
                        case CheckOutcome.Clean:
                            status = "passed";
                            break;
                        case CheckOutcome.HasFindings:
                            status = check.findings.Any(f => !f.isIgnored && f.severity == CheckSeverity.Blocking)
                                ? "failed"
                                : "warning";
                            break;
                        case CheckOutcome.Errored:
                        case CheckOutcome.Skipped:
                            status = "failed";
                            break;
                        default:
                            status = "failed";
                            break;
                    }

                    var details = new List<string>();
                    if (!string.IsNullOrWhiteSpace(check.errorMessage)) details.Add(check.errorMessage);
                    if (!string.IsNullOrWhiteSpace(check.skipReason)) details.Add("Skipped: " + check.skipReason);
                    details.AddRange((check.findings ?? new List<Finding>())
                        .Where(f => !f.isIgnored)
                        .Select(f => $"{f.severity}: {f.title} — {f.detail}"));
                    Add(result, "preupload/" + check.checkId, status,
                        details.Count == 0 ? check.outcome.ToString() : string.Join("\n", details));
                }
            }
            catch (Exception ex)
            {
                // Human UI historically fails open here. Release automation intentionally does not.
                Add(result, "preupload-runner", "failed", "Pre-upload checker harness errored: " + ex.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            bool hasFailure = result.checks.Any(c => c.status == "failed");
            bool hasWarning = result.checks.Any(c => c.status == "warning");
            bool warningsApproved = allowWarnings && !string.IsNullOrWhiteSpace(overrideReason);
            result.warningOverride = warningsApproved ? overrideReason.Trim() : null;
            result.ready = !hasFailure && (!hasWarning || warningsApproved);
            if (hasWarning && allowWarnings && !warningsApproved)
                Add(result, "warning-override", "failed", "override_reason is required when allow_warnings=true.");
            result.ready = !result.checks.Any(c => c.status == "failed")
                && (!result.checks.Any(c => c.status == "warning") || warningsApproved);
            return result;
        }

        [CliCommand("dreampark_content_publish", "Build, upload, and verify a DreamPark content release through the SDK's production uploader. Requires a clean strict preflight and non-empty release notes.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/content", "dreampark/releases" })]
        public static async Task<DreamParkContentReleaseResponse> PublishContent(
            [CliArg("content_id", "Folder name under Assets/Content and backend content ID.", Required = true)] string contentId,
            [CliArg("release_notes", "Human-readable changes in this release.", Required = true)] string releaseNotes,
            [CliArg("mode", "Upload mode: all, patch, or code-only.")] string mode = "patch",
            [CliArg("include_macos", "Also build the macOS editor content target.")] bool includeMacOS = false,
            [CliArg("include_windows", "Also build the Windows editor content target.")] bool includeWindows = false,
            [CliArg("clean_each_target", "Clear target-specific Addressables cache before each target.")] bool cleanEachTarget = false,
            [CliArg("allow_warnings", "Allow non-blocking checker warnings. Requires override_reason.")] bool allowWarnings = false,
            [CliArg("override_reason", "Human-reviewed reason for allowing warnings.")] string overrideReason = null)
        {
            contentId = NormalizeContentId(contentId);
            if (string.IsNullOrWhiteSpace(releaseNotes))
                return DreamParkContentReleaseResponse.Failed(contentId, "Release notes are required.");

            UploadMode uploadMode;
            switch ((mode ?? "patch").Trim().ToLowerInvariant())
            {
                case "all": uploadMode = UploadMode.All; break;
                case "patch": uploadMode = UploadMode.Patch; break;
                case "code-only":
                case "codeonly": uploadMode = UploadMode.CodeOnly; break;
                default: return DreamParkContentReleaseResponse.Failed(contentId, "mode must be all, patch, or code-only.");
            }

            var preflight = ContentPreflight(contentId, saveScenes: true, allowWarnings: allowWarnings,
                overrideReason: overrideReason);
            if (!preflight.ready)
                return DreamParkContentReleaseResponse.Failed(contentId, "Strict preflight failed.", preflight);

            var upload = await ContentUploaderPanel.RunAutomatedRelease(
                contentId, releaseNotes.Trim(), uploadMode, includeMacOS, includeWindows, cleanEachTarget);
            upload.preflight = preflight;
            if (!upload.success) return upload;

            var verify = await AwaitAPI(callback => ContentAPI.GetContent(contentId, callback));
            if (!verify.success)
                return DreamParkContentReleaseResponse.Failed(contentId,
                    "Upload completed but backend verification failed: " + ErrorOf(verify.response, "unknown error"), preflight);

            var content = verify.response?.json?.GetField("content");
            var versions = content?.GetField("versions");
            bool versionFound = upload.versionNumber.HasValue && versions != null && versions.list != null
                && versions.list.Any(v => v != null && v.HasField("versionNumber")
                    && v.GetField("versionNumber").intValue == upload.versionNumber.Value);
            if (!versionFound)
                return DreamParkContentReleaseResponse.Failed(contentId,
                    "Upload returned success, but the exact committed version was not present in the backend record.", preflight);

            upload.verified = true;
            upload.backend = verify.response != null ? verify.response.json : null;
            return upload;
        }

        [CliCommand("dreampark_sdk_preflight", "Verify SDK publish auth, version ordering, release notes, and backend admin access without exporting or uploading.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/sdk", "dreampark/releases" })]
        public static async Task<DreamParkPreflightResponse> SDKPreflight(
            [CliArg("version", "New MAJOR.MINOR.PATCH SDK version.", Required = true)] string version,
            [CliArg("release_notes", "Human-readable SDK release notes.", Required = true)] string releaseNotes)
        {
            var result = new DreamParkPreflightResponse
            {
                contentId = "sdk",
                checkedAtUtc = DateTime.UtcNow.ToString("O"),
                authenticated = AuthAPI.isLoggedIn,
                checks = new List<DreamParkReleaseCheck>()
            };
            Add(result, "auth", AuthAPI.isLoggedIn ? "passed" : "failed",
                AuthAPI.isLoggedIn ? "DreamPark session is available for " + AuthAPI.email + "." : "DreamPark sign-in is required. Run dreampark_auth_open.");
            bool validVersion = SDKVersion.TryParse(version, out _, out _, out _)
                && SDKVersion.Compare(version, SDKVersion.Current) > 0;
            Add(result, "version", validVersion ? "passed" : "failed",
                validVersion ? $"v{version} is newer than local v{SDKVersion.Current}."
                    : $"Version must be MAJOR.MINOR.PATCH and newer than local v{SDKVersion.Current}.");
            Add(result, "release-notes", string.IsNullOrWhiteSpace(releaseNotes) ? "failed" : "passed",
                string.IsNullOrWhiteSpace(releaseNotes) ? "Release notes are required." : "Release notes are present.");
            Add(result, "sdk-assets", AssetDatabase.IsValidFolder(SDKAssetPath) ? "passed" : "failed",
                AssetDatabase.IsValidFolder(SDKAssetPath) ? SDKAssetPath + " is exportable." : SDKAssetPath + " is missing.");

            if (AuthAPI.isLoggedIn)
            {
                var admin = await AwaitAPI(callback => SDKAPI.CheckCanPublish(callback));
                bool canPublish = admin.success && admin.response != null && admin.response.json != null
                    && admin.response.json.HasField("canPublish") && admin.response.json.GetField("canPublish").boolValue;
                Add(result, "admin-access", canPublish ? "passed" : "failed",
                    canPublish ? "Backend confirmed SDK publish access."
                        : ErrorOf(admin.response, "This account is not authorized to publish SDK releases."));
            }

            result.ready = !result.checks.Any(c => c.status != "passed");
            return result;
        }

        [CliCommand("dreampark_sdk_publish", "Version, export, upload, and verify a DreamPark SDK unitypackage. Rolls the local version file back if publish or verification fails.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/sdk", "dreampark/releases" })]
        public static async Task<DreamParkSDKReleaseResponse> PublishSDK(
            [CliArg("version", "New MAJOR.MINOR.PATCH SDK version.", Required = true)] string version,
            [CliArg("release_notes", "Human-readable SDK release notes.", Required = true)] string releaseNotes)
        {
            var preflight = await SDKPreflight(version, releaseNotes);
            if (!preflight.ready)
                return DreamParkSDKReleaseResponse.Failed(version, "SDK preflight failed.", preflight);

            string previousVersion = SDKVersion.Current;
            string tempPath = Path.Combine(Path.GetTempPath(), $"dreampark-sdk-v{version}-{Guid.NewGuid():N}.unitypackage");
            try
            {
                WriteVersion(version);
                AssetDatabase.ExportPackage(SDKAssetPath, tempPath, ExportPackageOptions.Recurse);
                if (!File.Exists(tempPath))
                    throw new IOException("Unity did not create the SDK package.");

                byte[] bytes = File.ReadAllBytes(tempPath);
                string sha256 = SHA256Hex(bytes);
                var published = await AwaitAPI(callback => SDKAPI.PublishVersion(
                    version, releaseNotes.Trim(), bytes, Path.GetFileName(tempPath), callback));
                if (!published.success)
                    throw new InvalidOperationException(ErrorOf(published.response, "SDK publish failed."));

                var status = await AwaitAPI(callback => SDKAPI.GetReleaseStatus(version, callback));
                var release = status.response?.json?.GetField("release");
                bool exact = status.success && release != null
                    && string.Equals(release.GetField("status")?.stringValue, "completed", StringComparison.Ordinal)
                    && string.Equals(release.GetField("sha256")?.stringValue, sha256, StringComparison.OrdinalIgnoreCase);
                if (!exact)
                    throw new InvalidOperationException("Upload returned success, but the exact SDK release hash/status could not be verified.");

                var verified = await AwaitAPI(callback => SDKAPI.GetManifest(callback));
                bool latest = verified.success && verified.response?.json != null
                    && verified.response.json.HasField("latest")
                    && string.Equals(verified.response.json.GetField("latest").stringValue, version, StringComparison.Ordinal);
                if (!latest)
                    throw new InvalidOperationException("Exact release exists, but the SDK manifest did not report v" + version + " as latest.");

                return new DreamParkSDKReleaseResponse
                {
                    success = true,
                    verified = true,
                    version = version,
                    sizeBytes = bytes.LongLength,
                    sha256 = sha256,
                    preflight = preflight,
                    backend = verified.response.json,
                    message = "SDK v" + version + " published and verified. Commit the version resource change."
                };
            }
            catch (Exception ex)
            {
                WriteVersion(previousVersion);
                return DreamParkSDKReleaseResponse.Failed(version, ex.Message, preflight);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static void WriteVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || version == "0.0.0") return;
            File.WriteAllText(VersionResourcePath, "{\n  \"version\": \"" + version + "\",\n  \"builtAt\": null\n}\n");
            AssetDatabase.ImportAsset(VersionResourcePath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.SaveAssets();
            SDKVersion.Reload();
        }

        private static string SHA256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string NormalizeContentId(string contentId)
        {
            string value = (contentId ?? "").Trim();
            if (value.Length == 0 || value.IndexOfAny(new[] { '/', '\\' }) >= 0 || value == "." || value == "..")
                throw new ArgumentException("content_id must be one folder name under Assets/Content.");
            return value;
        }

        private static void Add(DreamParkPreflightResponse result, string id, string status, string detail)
        {
            result.checks.Add(new DreamParkReleaseCheck { id = id, status = status, detail = detail ?? "" });
        }

        private static string ErrorOf(DreamParkAPI.APIResponse response, string fallback)
        {
            if (response == null) return fallback;
            if (response.json != null && response.json.HasField("error"))
            {
                string message = response.json.GetField("error").stringValue;
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            return string.IsNullOrWhiteSpace(response.error) ? fallback : response.error;
        }

        private static Task<(bool success, DreamParkAPI.APIResponse response)> AwaitAPI(
            Action<Action<bool, DreamParkAPI.APIResponse>> begin)
        {
            var completion = new TaskCompletionSource<(bool, DreamParkAPI.APIResponse)>();
            try { begin((success, response) => completion.TrySetResult((success, response))); }
            catch (Exception ex) { completion.TrySetException(ex); }
            return completion.Task;
        }
    }

    public partial class ContentUploaderPanel
    {
        // Suppresses UI-only dialogs/browser launches while a CLI command owns the lifecycle.
        // The command returns every failure as structured JSON instead.
        private bool automatedReleaseMode;

        internal static async Task<DreamParkContentReleaseResponse> RunAutomatedRelease(
            string contentId, string notes, UploadMode mode, bool includeMacOS,
            bool includeWindows, bool cleanEachTarget)
        {
            var panel = CreateInstance<ContentUploaderPanel>();
            try
            {
                panel.automatedReleaseMode = true;
                panel.contentId = contentId;
                panel.contentName = contentId;
                panel.releaseNotes = notes;
                panel.buildOsx = includeMacOS;
                panel.buildWindows = includeWindows;
                panel.cleanBeforeEachTarget = cleanEachTarget;
                panel.preUploadChecksCleared = true; // strict command preflight just ran successfully

                if (!panel.BeginUploadFromPopup(build: true, mode: mode, failedOnly: false))
                    return DreamParkContentReleaseResponse.Failed(contentId,
                        "The SDK uploader refused to start. Inspect the Unity Console for the exact gate.");

                DateTime deadline = DateTime.UtcNow.AddHours(4);
                while (!panel.UploadCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(250);

                if (!panel.UploadCompleted)
                    return DreamParkContentReleaseResponse.Failed(contentId, "Content release timed out after four hours.");
                if (!panel.UploadSucceeded)
                    return DreamParkContentReleaseResponse.Failed(contentId,
                        string.IsNullOrWhiteSpace(panel.UploadStatusMessage) ? "Content upload failed." : panel.UploadStatusMessage);

                return new DreamParkContentReleaseResponse
                {
                    success = true,
                    contentId = contentId,
                    versionNumber = panel.LatestPublishedVersionNumber,
                    message = panel.UploadStatusMessage,
                    mode = mode.ToString(),
                    platforms = new List<string> { "Android", "iOS" }
                        .Concat(includeMacOS ? new[] { "StandaloneOSX" } : Array.Empty<string>())
                        .Concat(includeWindows ? new[] { "StandaloneWindows" } : Array.Empty<string>())
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                return DreamParkContentReleaseResponse.Failed(contentId, ex.Message);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(panel);
            }
        }
    }

    [Serializable]
    public sealed class DreamParkReleaseCheck
    {
        public string id;
        public string status;
        public string detail;
    }

    [Serializable]
    public sealed class DreamParkPreflightResponse
    {
        public bool ready;
        public bool authenticated;
        public string contentId;
        public string checkedAtUtc;
        public string warningOverride;
        public List<DreamParkReleaseCheck> checks;
    }

    [Serializable]
    public sealed class DreamParkContentReleaseResponse
    {
        public bool success;
        public bool verified;
        public string contentId;
        public int? versionNumber;
        public string mode;
        public List<string> platforms;
        public string message;
        public DreamParkPreflightResponse preflight;
        public object backend;

        public static DreamParkContentReleaseResponse Failed(string contentId, string message,
            DreamParkPreflightResponse preflight = null)
        {
            return new DreamParkContentReleaseResponse
            {
                success = false,
                verified = false,
                contentId = contentId,
                message = message,
                preflight = preflight
            };
        }
    }

    [Serializable]
    public sealed class DreamParkSDKReleaseResponse
    {
        public bool success;
        public bool verified;
        public string version;
        public long sizeBytes;
        public string sha256;
        public string message;
        public DreamParkPreflightResponse preflight;
        public object backend;

        public static DreamParkSDKReleaseResponse Failed(string version, string message,
            DreamParkPreflightResponse preflight = null)
        {
            return new DreamParkSDKReleaseResponse
            {
                success = false,
                verified = false,
                version = version,
                message = message,
                preflight = preflight
            };
        }
    }
}
#endif
