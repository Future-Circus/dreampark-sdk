#if UNITY_EDITOR && !DREAMPARKCORE && DREAMPARK_SDK_PACKAGES_READY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Defective.JSON;
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

        [CliCommand("dreampark_auth_status", "Check the local DreamPark SDK session or a release token without returning either's bearer value.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static object AuthStatus(
            [CliArg("release_token", "Release token (rlt_...) to check instead of the human session. Falls back to DREAMPARK_RELEASE_TOKEN when omitted.")] string releaseToken = null)
        {
            string resolvedToken = AuthAPI.ResolveReleaseToken(releaseToken);
            using (AuthAPI.WithReleaseToken(resolvedToken))
            {
                bool hasToken = AuthAPI.HasReleaseToken;
                bool authenticated = AuthAPI.isLoggedIn || hasToken;
                return new
                {
                    authenticated,
                    // "release-token" only means a token value is present locally — it has
                    // NOT been round-tripped to the backend. A revoked/expired/wrong-scope
                    // token still reports this until the first real call 401s/403s.
                    authMode = hasToken ? "release-token"
                        : AuthAPI.IsAgentKeyActive ? "agent-key"
                        : (AuthAPI.isLoggedIn ? "session" : "none"),
                    email = AuthAPI.isLoggedIn ? AuthAPI.email : null,
                    expiresAtUnixMs = AuthAPI.sessionExpiresAt > 0 ? (long?)AuthAPI.sessionExpiresAt : null,
                    expiresInHours = AuthAPI.sessionExpiresInHours >= 0 ? (double?)AuthAPI.sessionExpiresInHours : null,
                    action = authenticated ? "none"
                        : "Agents: run dreampark_agent_request then dreampark_agent_wait. Humans: dreampark_auth_open. CI: supply release_token / set DREAMPARK_RELEASE_TOKEN."
                };
            }
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
                return new { success = true, authenticated = false, local, remote = (object)null, action = "Run dreampark_agent_request + dreampark_agent_wait (agents) or dreampark_auth_open (humans)." };

            // With several agent keys stored (one per approved title), this title's key.
            (bool success, DreamParkAPI.APIResponse response) response;
            using (AuthAPI.WithAgentContent(contentId))
                response = await AwaitAPI(callback => ContentAPI.GetContent(contentId, callback));
            return new
            {
                success = response.success,
                authenticated = true,
                local,
                // Plain values, never the raw JSONObject: the CLI serializer must not see a
                // JSON null or a non-object body (a title with no record yet).
                remote = PlainJson(response.response?.json),
                error = response.success ? null : ErrorOf(response.response, "Could not inspect backend content.")
            };
        }

        [CliCommand("dreampark_content_preflight", "Run the strict DreamPark content release checker. Automation fails closed when a check errors, is skipped, or reports an unapproved warning/blocker.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/content", "dreampark/releases" })]
        public static DreamParkPreflightResponse ContentPreflight(
            [CliArg("content_id", "Folder name under Assets/Content and backend content ID.", Required = true)] string contentId,
            [CliArg("save_scenes", "Save open scenes/assets before checks that inspect scene YAML.")] bool saveScenes = false,
            [CliArg("allow_warnings", "Allow non-blocking warnings. Requires override_reason.")] bool allowWarnings = false,
            [CliArg("override_reason", "Human-reviewed reason for allowing warnings. Never bypasses blockers, errors, or skipped checks.")] string overrideReason = null,
            [CliArg("release_token", "Content-owner release token (rlt_...), scoped to content_id. Falls back to DREAMPARK_RELEASE_TOKEN when omitted.")] string releaseToken = null)
        {
            // Automation: nothing this command reaches may show a modal (owner, 2026-10-09).
            // The agent-content scope picks this title's key when the project stores several.
            using (DreamParkAutomation.Begin())
            using (AuthAPI.WithReleaseToken(AuthAPI.ResolveReleaseToken(releaseToken)))
            using (AuthAPI.WithAgentContent(contentId))
            {
                return ContentPreflightInternal(contentId, saveScenes, allowWarnings, overrideReason);
            }
        }

        // Split out so PublishContent can run its own preflight from inside the SAME
        // release-token scope it already opened, without re-resolving/re-pushing the
        // token a second time.
        private static DreamParkPreflightResponse ContentPreflightInternal(
            string contentId, bool saveScenes, bool allowWarnings, string overrideReason)
        {
            contentId = NormalizeContentId(contentId);
            bool authenticated = AuthAPI.isLoggedIn || AuthAPI.HasReleaseToken;
            var result = new DreamParkPreflightResponse
            {
                contentId = contentId,
                checkedAtUtc = DateTime.UtcNow.ToString("O"),
                authenticated = authenticated,
                checks = new List<DreamParkReleaseCheck>()
            };

            Add(result, "auth", authenticated ? "passed" : "failed",
                AuthAPI.HasReleaseToken ? "Release token supplied."
                    : AuthAPI.IsAgentKeyActive ? AgentKeyDetail(contentId)
                    : AuthAPI.isLoggedIn ? "DreamPark session is available for " + AuthAPI.email + "."
                    : "DreamPark sign-in is required. Run dreampark_agent_request + dreampark_agent_wait, dreampark_auth_open, or supply release_token.");

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

            // Only the scenes inside this content folder matter here: they are the ones the
            // scene-overrides check reads. Never SaveOpenScenes (an Untitled scene opens a Save-As
            // panel and hung ArcadeLand's publish on Oct 8 2026); scenes outside the folder and
            // Untitled scenes are reported and left exactly as they are. The Addressables build
            // sets their unsaved flag aside (DreamParkAutomation.SuspendSceneDirtiness), so they
            // cannot stop the build with a dialog either.
            if (saveScenes)
            {
                var saveReport = DreamParkAutomation.SaveDirtyScenesWithoutPrompt(contentPath);
                if (saveReport.Ok) AssetDatabase.SaveAssets();
                Add(result, "saved-state", saveReport.Ok ? "passed" : "failed",
                    saveReport.Ok
                        ? "Scenes in " + contentPath + " and assets were saved before inspection. " + saveReport.Summary()
                        : saveReport.Summary());
            }
            else
            {
                var dirtyScenes = DreamParkAutomation.LoadedDirtyScenes();
                var contentScenes = dirtyScenes
                    .Where(scene => !DreamParkAutomation.IsUntitled(scene) && DreamParkAutomation.IsUnder(scene.path, contentPath))
                    .Select(scene => scene.path)
                    .ToList();
                var otherScenes = dirtyScenes
                    .Where(scene => DreamParkAutomation.IsUntitled(scene) || !DreamParkAutomation.IsUnder(scene.path, contentPath))
                    .Select(DreamParkAutomation.Label)
                    .ToList();
                string others = otherScenes.Count == 0 ? ""
                    : " Not part of this upload, left as they are: " + string.Join(", ", otherScenes) + ".";
                Add(result, "saved-state", contentScenes.Count == 0 ? "passed" : "failed",
                    contentScenes.Count == 0
                        ? "Scenes in " + contentPath + " have no unsaved changes." + others
                        : "Save these open scenes before releasing (or pass save_scenes=true): " + string.Join(", ", contentScenes) + "." + others);
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

        [CliCommand("dreampark_content_publish", "Build, upload, and verify a DreamPark content release through the SDK's production uploader. Requires a clean strict preflight and non-empty release notes. Agents: pass --wait false and poll dreampark_content_publish_status (a build + upload can take far longer than one CLI call).", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/content", "dreampark/releases" })]
        public static async Task<DreamParkContentReleaseResponse> PublishContent(
            [CliArg("content_id", "Folder name under Assets/Content and backend content ID.", Required = true)] string contentId,
            [CliArg("release_notes", "Human-readable changes in this release.", Required = true)] string releaseNotes,
            [CliArg("mode", "Upload mode: all, patch, or code-only.")] string mode = "patch",
            [CliArg("include_macos", "Also build the macOS editor content target.")] bool includeMacOS = false,
            [CliArg("include_windows", "Also build the Windows editor content target.")] bool includeWindows = false,
            [CliArg("clean_each_target", "Clear target-specific Addressables cache before each target.")] bool cleanEachTarget = false,
            [CliArg("allow_warnings", "Allow non-blocking checker warnings. Requires override_reason.")] bool allowWarnings = false,
            [CliArg("override_reason", "Human-reviewed reason for allowing warnings.")] string overrideReason = null,
            [CliArg("release_token", "Content-owner release token (rlt_...), scoped to content_id. Falls back to DREAMPARK_RELEASE_TOKEN when omitted.")] string releaseToken = null,
            [CliArg("target", "release (default): the live content ID. beta: the separate <content_id>Beta content target, allocated on first use exactly like the uploader's Upload Beta button. A brand-new title may go beta-first (pass --name and --description): the title record is created without a build and the upload goes only to the beta.")] string target = "release",
            [CliArg("name", "Store Name. Used ONLY when content_id has no backend record yet (a new title, which this upload creates); an existing record's Name is always kept.")] string name = null,
            [CliArg("description", "Store Description. Used ONLY when creating a new title, like name; required then.")] string description = null,
            [CliArg("wait", "true (default): return when the release has finished and been verified. false: start it, return a jobId at once, and follow it with dreampark_content_publish_status.")] bool wait = true)
        {
            contentId = NormalizeContentId(contentId);
            string busy = ContentPublishJobs.BusyReason();
            if (busy != null) return DreamParkContentReleaseResponse.Failed(contentId, busy);

            var job = ContentPublishJobs.Begin(contentId, target, mode);
            Task<DreamParkContentReleaseResponse> run = PublishContentCore(contentId, releaseNotes, mode, includeMacOS,
                includeWindows, cleanEachTarget, allowWarnings, overrideReason, releaseToken, target, name, description);
            if (wait)
            {
                DreamParkContentReleaseResponse result;
                try { result = await run; }
                catch (Exception ex) { result = DreamParkContentReleaseResponse.Failed(contentId, ex.Message); }
                ContentPublishJobs.Finish(job, result);
                result.jobId = job.jobId;
                return result;
            }

            ContentPublishJobs.Follow(job, run);
            return new DreamParkContentReleaseResponse
            {
                success = true,
                started = true,
                jobId = job.jobId,
                contentId = contentId,
                target = job.target,
                targetContentId = job.targetContentId,
                mode = job.mode,
                message = $"Publish started (job {job.jobId}). Poll unity command --project-path . dreampark_content_publish_status every few minutes until state is succeeded or failed; don't start another publish or reload the Editor meanwhile.",
            };
        }

        // Off the main thread on purpose: an Addressables build blocks the main thread for
        // minutes, which is exactly when an agent asks how it is going.
        [CliCommand("dreampark_content_publish_status", "Progress and result of the current or most recent dreampark_content_publish: state (running / succeeded / failed / interrupted / none), current step, progress, upload bytes, elapsed time and the final result. Read-only; safe to poll, answers even while a build blocks the Editor.", MainThreadRequired = false, Tags = new[] { "dreampark", "dreampark/content", "dreampark/releases" })]
        public static DreamParkPublishStatus PublishStatus() => ContentPublishJobs.Status();

        private static async Task<DreamParkContentReleaseResponse> PublishContentCore(
            string contentId,
            string releaseNotes,
            string mode,
            bool includeMacOS,
            bool includeWindows,
            bool cleanEachTarget,
            bool allowWarnings,
            string overrideReason,
            string releaseToken,
            string target,
            string name,
            string description)
        {
            contentId = NormalizeContentId(contentId);
            if (string.IsNullOrWhiteSpace(releaseNotes))
                return DreamParkContentReleaseResponse.Failed(contentId, "Release notes are required.");

            ContentUploadTarget uploadTarget;
            switch ((target ?? "release").Trim().ToLowerInvariant())
            {
                case "":
                case "release": uploadTarget = ContentUploadTarget.Release; break;
                case "beta": uploadTarget = ContentUploadTarget.Beta; break;
                default: return DreamParkContentReleaseResponse.Failed(contentId, "target must be release or beta.");
            }
            string targetContentId = uploadTarget == ContentUploadTarget.Beta
                ? BetaContentId.DeriveFrom(contentId)
                : contentId;
            if (string.IsNullOrEmpty(targetContentId))
                return DreamParkContentReleaseResponse.Failed(contentId,
                    $"'{contentId}' cannot be used to derive a safe beta target. Rename the content folder.");

            UploadMode uploadMode;
            switch ((mode ?? "patch").Trim().ToLowerInvariant())
            {
                case "all": uploadMode = UploadMode.All; break;
                case "patch": uploadMode = UploadMode.Patch; break;
                case "code-only":
                case "codeonly": uploadMode = UploadMode.CodeOnly; break;
                default: return DreamParkContentReleaseResponse.Failed(contentId, "mode must be all, patch, or code-only.");
            }

            // One scope for preflight + upload + verify: the backend enforces the
            // token's content_id scope on every request it authenticates, so there's
            // no separate client-side ownership check to duplicate here.
            // The automation scope lasts for the whole job (build and upload included):
            // every interactive path it reaches reports into this result instead of
            // showing a modal nobody is there to answer (owner, 2026-10-09).
            // The agent-content scope stays open for the whole job too: every request the
            // build and upload make (coroutines and worker threads included) sends the key
            // approved for this title (or its Beta twin) when the project stores several.
            using (DreamParkAutomation.Begin())
            using (AuthAPI.WithReleaseToken(AuthAPI.ResolveReleaseToken(releaseToken)))
            using (AuthAPI.WithAgentContent(contentId))
            {
                ContentPublishJobs.SetPhase("Preflight", "Running the strict content preflight...");
                var preflight = ContentPreflightInternal(contentId, saveScenes: true, allowWarnings: allowWarnings,
                    overrideReason: overrideReason);
                if (!preflight.ready)
                    return DreamParkContentReleaseResponse.Failed(contentId, "Strict preflight failed.", preflight);

                var upload = await ContentUploaderPanel.RunAutomatedRelease(
                    contentId, releaseNotes.Trim(), uploadMode, includeMacOS, includeWindows, cleanEachTarget,
                    uploadTarget, name, description);
                upload.preflight = preflight;
                if (!upload.success) return upload;

                // A zero-change patch is a successful no-op: no version was created, so
                // there is nothing to look for in the backend record.
                if (upload.noChanges) return upload;

                // Verify against the content ID that was actually uploaded to: the
                // <id>Beta record for a beta upload, never the live one.
                ContentPublishJobs.SetPhase("Verifying", "Reading the backend record back for '" + targetContentId + "'...");
                var verify = await AwaitAPI(callback => ContentAPI.GetContent(targetContentId, callback));
                if (!verify.success)
                    return DreamParkContentReleaseResponse.Failed(contentId,
                        "Upload completed but backend verification of '" + targetContentId + "' failed: " + ErrorOf(verify.response, "unknown error"), preflight);

                var content = verify.response?.json?.GetField("content");
                var versions = content?.GetField("versions");
                bool versionFound = upload.versionNumber.HasValue && versions != null && versions.list != null
                    && versions.list.Any(v => v != null && v.HasField("versionNumber")
                        && v.GetField("versionNumber").intValue == upload.versionNumber.Value);
                if (!versionFound)
                    return DreamParkContentReleaseResponse.Failed(contentId,
                        "Upload returned success, but the exact committed version was not present in the backend record for '" + targetContentId + "'.", preflight);

                upload.verified = true;
                upload.backend = PlainJson(verify.response?.json);
                return upload;
            }
        }

        [CliCommand("dreampark_sdk_preflight", "Verify SDK publish auth, version ordering, release notes, and backend admin access without exporting or uploading.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/sdk", "dreampark/releases" })]
        public static async Task<DreamParkPreflightResponse> SDKPreflight(
            [CliArg("version", "New MAJOR.MINOR.PATCH SDK version.", Required = true)] string version,
            [CliArg("release_notes", "Human-readable SDK release notes.", Required = true)] string releaseNotes,
            [CliArg("release_token", "Admin release token (rlt_...) for scope sdk. Falls back to DREAMPARK_RELEASE_TOKEN when omitted.")] string releaseToken = null)
        {
            using (AuthAPI.WithReleaseToken(AuthAPI.ResolveReleaseToken(releaseToken)))
            {
                return await SDKPreflightInternal(version, releaseNotes);
            }
        }

        // Split out so PublishSDK can run its own preflight from inside the SAME
        // release-token scope it already opened, matching ContentPreflightInternal.
        private static async Task<DreamParkPreflightResponse> SDKPreflightInternal(string version, string releaseNotes)
        {
            bool authenticated = AuthAPI.isLoggedIn || AuthAPI.HasReleaseToken;
            var result = new DreamParkPreflightResponse
            {
                contentId = "sdk",
                checkedAtUtc = DateTime.UtcNow.ToString("O"),
                authenticated = authenticated,
                checks = new List<DreamParkReleaseCheck>()
            };
            Add(result, "auth", authenticated ? "passed" : "failed",
                AuthAPI.HasReleaseToken ? "Release token supplied."
                    : AuthAPI.IsAgentKeyActive ? "DreamPark agent key is active for " + AuthAPI.email + "."
                    : AuthAPI.isLoggedIn ? "DreamPark session is available for " + AuthAPI.email + "."
                    : "DreamPark sign-in is required. Run dreampark_agent_request + dreampark_agent_wait, dreampark_auth_open, or supply release_token.");
            bool validVersion = SDKVersion.TryParse(version, out _, out _, out _)
                && SDKVersion.Compare(version, SDKVersion.Current) > 0;
            Add(result, "version", validVersion ? "passed" : "failed",
                validVersion ? $"v{version} is newer than local v{SDKVersion.Current}."
                    : $"Version must be MAJOR.MINOR.PATCH and newer than local v{SDKVersion.Current}.");
            Add(result, "release-notes", string.IsNullOrWhiteSpace(releaseNotes) ? "failed" : "passed",
                string.IsNullOrWhiteSpace(releaseNotes) ? "Release notes are required." : "Release notes are present.");
            Add(result, "sdk-assets", AssetDatabase.IsValidFolder(SDKAssetPath) ? "passed" : "failed",
                AssetDatabase.IsValidFolder(SDKAssetPath) ? SDKAssetPath + " is exportable." : SDKAssetPath + " is missing.");

            bool xluaReady = XLuaGenGuard.CheckExportReadiness(out string xluaReport);
            Add(result, "xlua-codegen", xluaReady ? "passed" : "failed", xluaReport.Trim());

            int designBudget, relayCap;
            bool validNetworkBudget = PreUploadChecks.Checks.NetBudgetInvariant.IsSatisfied(
                out designBudget, out relayCap);
            Add(result, "network-budget-invariant", validNetworkBudget ? "passed" : "failed",
                validNetworkBudget
                    ? $"DreamBoxClient.DesignBudget ({designBudget}/s) is within the peer relay cap ({relayCap}/s)."
                    : PreUploadChecks.Checks.NetBudgetInvariant.FailureMessage(designBudget, relayCap));

            if (authenticated)
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
            [CliArg("release_notes", "Human-readable SDK release notes.", Required = true)] string releaseNotes,
            [CliArg("release_token", "Admin release token (rlt_...) for scope sdk. Falls back to DREAMPARK_RELEASE_TOKEN when omitted.")] string releaseToken = null)
        {
            using (AuthAPI.WithReleaseToken(AuthAPI.ResolveReleaseToken(releaseToken)))
            {
                var preflight = await SDKPreflightInternal(version, releaseNotes);
                if (!preflight.ready)
                    return DreamParkSDKReleaseResponse.Failed(version, "SDK preflight failed.", preflight);

                if (!XLuaGenGuard.PrepareSdkExport(out string xluaReport))
                    return DreamParkSDKReleaseResponse.Failed(version, xluaReport, preflight);

                string previousVersion = SDKVersion.Current;
                string tempPath = Path.Combine(Path.GetTempPath(), $"dreampark-sdk-v{version}-{Guid.NewGuid():N}.unitypackage");
                try
                {
                    WriteVersion(version);
                    using (DreamParkSdkDocs.StageForExport())
                        AssetDatabase.ExportPackage(SDKAssetPath, tempPath, ExportPackageOptions.Recurse);
                    if (!File.Exists(tempPath))
                        throw new IOException("Unity did not create the SDK package.");

                    byte[] bytes = File.ReadAllBytes(tempPath);
                    string sha256 = SHA256Hex(bytes);
                    string retiredAssets = string.Join("\n", SDKUpgradeCleanup.GetPublishedRetiredAssetPaths());
                    var published = await AwaitAPI(callback => SDKAPI.PublishVersion(
                        version, releaseNotes.Trim(), bytes, Path.GetFileName(tempPath), retiredAssets, callback));
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

        // Which stored agent key a command for `contentId` uses (masked), for the preflight.
        private static string AgentKeyDetail(string contentId)
        {
            AgentKeyRecord match = AgentKeyStore.FindFor(contentId);
            if (match != null)
                return $"DreamPark agent key {AgentKeyStore.Mask(match.agentKey)} (approved for {string.Join(", ", match.CoveredContentIds)}) is used for {contentId}.";
            AgentKeyRecord used = AgentKeyStore.SelectFor(contentId);
            return $"No agent key in this project was approved for {contentId}; the newest key {AgentKeyStore.Mask(used?.agentKey)} (approved for {string.Join(", ", used != null ? used.CoveredContentIds : new string[0])}) is used, which the server refuses unless that agent is on {contentId}'s team. If a publish fails with 403, run dreampark_agent_request --content_id {contentId}, then dreampark_agent_wait.";
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

        internal static string ErrorOf(DreamParkAPI.APIResponse response, string fallback)
        {
            if (response == null) return fallback;
            if (response.json != null && response.json.HasField("error"))
            {
                string message = response.json.GetField("error").stringValue;
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            return string.IsNullOrWhiteSpace(response.error) ? fallback : response.error;
        }

        /// <summary>
        /// A JSONObject as plain values (Dictionary / List / string / number / bool / null)
        /// that any serializer handles. Null, a JSON null and an unparseable body all
        /// become null, never something a caller has to type-check before iterating.
        /// </summary>
        internal static object PlainJson(JSONObject json)
        {
            if (json == null) return null;
            switch (json.type)
            {
                case JSONObject.Type.Object:
                {
                    var map = new Dictionary<string, object>();
                    if (json.keys != null && json.list != null)
                        for (int i = 0; i < json.keys.Count && i < json.list.Count; i++)
                            map[json.keys[i]] = PlainJson(json.list[i]);
                    return map;
                }
                case JSONObject.Type.Array:
                    return json.list == null ? new List<object>() : json.list.Select(PlainJson).ToList();
                case JSONObject.Type.String: return json.stringValue;
                case JSONObject.Type.Number: return json.isInteger ? (object)json.longValue : json.doubleValue;
                case JSONObject.Type.Bool: return json.boolValue;
                default: return null;
            }
        }

        internal static Task<(bool success, DreamParkAPI.APIResponse response)> AwaitAPI(
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
        internal static async Task<DreamParkContentReleaseResponse> RunAutomatedRelease(
            string contentId, string notes, UploadMode mode, bool includeMacOS,
            bool includeWindows, bool cleanEachTarget,
            ContentUploadTarget target = ContentUploadTarget.Release,
            string newTitleName = null, string newTitleDescription = null)
        {
            string targetContentId = target == ContentUploadTarget.Beta ? BetaContentId.DeriveFrom(contentId) : contentId;
            string targetName = target == ContentUploadTarget.Beta ? "beta" : "release";
            Func<string, DreamParkContentReleaseResponse> fail = message =>
            {
                var failed = DreamParkContentReleaseResponse.Failed(contentId, message);
                failed.target = targetName;
                failed.targetContentId = targetContentId;
                return failed;
            };

            var panel = CreateInstance<ContentUploaderPanel>();
            ActiveAutomatedPanel = panel;
            try
            {
                panel.automatedReleaseMode = true;
                panel.automationFailureMessage = null;
                panel.uploadSkippedNoChanges = false;
                panel.contentId = contentId;
                panel.releaseNotes = notes;
                panel.buildOsx = includeMacOS;
                panel.buildWindows = includeWindows;
                panel.cleanBeforeEachTarget = cleanEachTarget;
                panel.preUploadChecksCleared = true; // strict command preflight just ran successfully

                // Store-facing metadata. UploadContent re-sends contentName AND
                // contentDescription to /api/content/:id/update on EVERY upload (as
                // "<name> (Beta)" for beta), so they must be the live record's current
                // values. This used to send contentName = contentId and an empty
                // description, overwriting the store Name/Description on every automated
                // release.
                var current = await DreamParkReleaseCommands.AwaitAPI(callback => ContentAPI.GetContent(contentId, callback));
                long currentStatus = current.response != null ? current.response.statusCode : 0;
                // A record exists only when "content" is a JSON OBJECT. A null/absent content
                // (or a non-JSON body) means no record yet, the same as a 404.
                JSONObject currentContent = current.response?.json?.GetField("content");
                bool hasRecord = current.success && currentContent != null
                    && currentContent.type == JSONObject.Type.Object;
                if (!hasRecord && current.success) currentStatus = 404;
                if (hasRecord)
                {
                    JSONObject content = currentContent;
                    string storeName = content.HasField("contentName") ? content.GetField("contentName").stringValue : null;
                    string storeDescription = content.HasField("contentDescription") ? content.GetField("contentDescription").stringValue : null;
                    if (string.IsNullOrWhiteSpace(storeName))
                        return fail($"'{contentId}' has an empty store Name. Set Name (and Description) in DreamPark > Content Uploader before releasing; an empty Name blocks upload.");
                    panel.contentName = storeName;
                    panel.contentDescription = storeDescription ?? "";
                }
                else if (currentStatus == 404)
                {
                    // A NEW title. With an agent key the server makes the approving human its
                    // owner and puts the agent on its team.
                    if (string.IsNullOrWhiteSpace(newTitleName) || string.IsNullOrWhiteSpace(newTitleDescription))
                        return fail($"'{contentId}' is a new title and this upload creates it. Pass --name and --description: they are the store Name and Description guests see (an empty Name blocks upload).");
                    panel.contentName = newTitleName.Trim();
                    panel.contentDescription = newTitleDescription.Trim();
                    if (target == ContentUploadTarget.Beta)
                    {
                        // BETA FIRST (Oct 2026): new titles normally ship to their beta target
                        // before any release. The beta hangs off the parent record, so create
                        // that record now (the same POST /api/content/add a first release makes)
                        // WITHOUT uploading to it; the build then goes only to <id>Beta. A record
                        // with no versions serves nothing, exactly like a first release whose
                        // upload has not landed yet.
                        var created = await DreamParkReleaseCommands.AwaitAPI(callback =>
                            ContentAPI.AddContent(contentId, panel.contentName, panel.contentDescription, null, callback));
                        if (!created.success)
                            return fail($"Could not create the title record for '{contentId}' (needed before its first beta): "
                                + DreamParkReleaseCommands.ErrorOf(created.response, "unknown error"));
                    }
                }
                else
                {
                    string why;
                    if (currentStatus == 403)
                        why = AuthAPI.IsAgentKeyActive
                            ? (AgentKeyStore.FindFor(contentId) == null
                                ? $"No agent key in this project was approved for {contentId} (stored keys cover: {(AgentKeyStore.CoveredContentIds().Length > 0 ? string.Join(", ", AgentKeyStore.CoveredContentIds()) : "none")}). Run dreampark_agent_request --content_id {contentId} and have its owner approve it once; the other titles' keys are kept. ({DreamParkReleaseCommands.ErrorOf(current.response, "403")})"
                                : $"The agent key approved for {contentId} was refused: this agent isn't on {contentId}'s team. Run dreampark_agent_request --content_id {contentId} and have its owner approve it. ({DreamParkReleaseCommands.ErrorOf(current.response, "403")})")
                            : $"This account is not an owner or collaborator of '{contentId}'.";
                    else if (currentStatus == 401)
                        why = "DreamPark rejected the credential (expired or revoked). Agents: run dreampark_agent_status, then dreampark_agent_request + dreampark_agent_wait.";
                    else
                        why = $"Could not read the current record for '{contentId}': "
                            + DreamParkReleaseCommands.ErrorOf(current.response, "unknown error");
                    return fail(why);
                }
                // Without this the hidden panel has no logo, and SaveLogoSelection() inside
                // BeginUploadFromPopup would DELETE the creator's saved logo choice.
                panel.LoadLogoSelection();

                if (target == ContentUploadTarget.Beta)
                {
                    // Same allocation + identity check as ShowBetaUploadFlow, without dialogs.
                    if (string.IsNullOrEmpty(targetContentId) || !BetaContentId.IsPathSafe(targetContentId))
                        return fail($"'{contentId}' cannot be used to derive a safe beta target. Rename the content folder.");

                    var allocated = await DreamParkReleaseCommands.AwaitAPI(callback => ContentAPI.EnsureBetaContentTarget(contentId, callback));
                    if (!allocated.success)
                    {
                        JSONObject allocatedJson = allocated.response != null ? allocated.response.json : null;
                        string serverMessage = allocatedJson != null && allocatedJson.HasField("message")
                            ? allocatedJson.GetField("message").stringValue
                            : null;
                        return fail("Beta target unavailable: " + (string.IsNullOrWhiteSpace(serverMessage)
                            ? DreamParkReleaseCommands.ErrorOf(allocated.response, "The beta target could not be allocated.")
                            : serverMessage));
                    }

                    JSONObject allocation = allocated.response != null ? allocated.response.json : null;
                    string allocatedId = allocation != null && allocation.HasField("contentId")
                        ? allocation.GetField("contentId").stringValue
                        : null;
                    if (!string.Equals(allocatedId, targetContentId, StringComparison.Ordinal))
                        return fail($"The backend returned beta target '{allocatedId ?? "(missing)"}', but the SDK expected '{targetContentId}'. Upload was stopped before building anything.");

                    var betaLoaded = await DreamParkReleaseCommands.AwaitAPI(callback => ContentAPI.GetContent(allocatedId, callback));
                    if (!betaLoaded.success || betaLoaded.response == null || betaLoaded.response.json == null
                        || !betaLoaded.response.json.HasField("content"))
                        return fail("Beta target unavailable: "
                            + DreamParkReleaseCommands.ErrorOf(betaLoaded.response, "The allocated beta target could not be loaded."));
                    panel.betaContentDirectorySnapshot = betaLoaded.response.json;
                    panel.betaLatestPublishedVersionNumber = MaxCommittedVersion(betaLoaded.response.json.GetField("content"));
                }

                if (!panel.BeginUploadFromPopup(build: true, mode: mode, failedOnly: false, uploadTarget: target))
                    return fail(panel.automationFailureMessage
                        ?? "The SDK uploader refused to start. Inspect the Unity Console for the exact gate.");

                DateTime deadline = DateTime.UtcNow.AddHours(4);
                while (!panel.UploadCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(250);

                if (!panel.UploadCompleted)
                    return fail("Content release timed out after four hours.");
                if (!panel.UploadSucceeded)
                    return fail(!string.IsNullOrWhiteSpace(panel.UploadStatusMessage)
                        ? panel.UploadStatusMessage
                        : (panel.automationFailureMessage ?? "Content upload failed."));

                var platforms = new List<string> { "Android", "iOS" }
                    .Concat(includeMacOS ? new[] { "StandaloneOSX" } : Array.Empty<string>())
                    .Concat(includeWindows ? new[] { "StandaloneWindows" } : Array.Empty<string>())
                    .ToList();

                if (panel.uploadSkippedNoChanges)
                {
                    return new DreamParkContentReleaseResponse
                    {
                        success = true,
                        noChanges = true,
                        contentId = contentId,
                        target = targetName,
                        targetContentId = targetContentId,
                        message = $"No content changes since the last successful {targetName} upload of '{targetContentId}', so nothing was uploaded and no version was created. Use --mode all to force a full re-upload.",
                        mode = mode.ToString(),
                        modeForcedReason = panel.modeForcedReason,
                        platforms = platforms
                    };
                }

                return new DreamParkContentReleaseResponse
                {
                    success = true,
                    contentId = contentId,
                    target = targetName,
                    targetContentId = targetContentId,
                    versionNumber = panel.ActiveLatestPublishedVersionNumber,
                    message = panel.modeForcedReason != null
                        ? panel.UploadStatusMessage + " " + panel.modeForcedReason
                        : panel.UploadStatusMessage,
                    mode = panel.modeForcedReason != null ? UploadMode.All.ToString() : mode.ToString(),
                    modeForcedReason = panel.modeForcedReason,
                    platforms = platforms
                };
            }
            catch (Exception ex)
            {
                return fail(ex.Message);
            }
            finally
            {
                ActiveAutomatedPanel = null;
                EditorUtility.ClearProgressBar();
                DestroyImmediate(panel);
            }
        }

        // The hidden panel of the automated release in flight, for dreampark_content_publish_status.
        internal static ContentUploaderPanel ActiveAutomatedPanel { get; private set; }
        internal string AutomationStatusTitle => uploadStatusTitle;
        internal float AutomationStatusProgress => uploadStatusProgress;
    }

    /// <summary>
    /// The one automated publish an Editor runs at a time, and the record of the last
    /// one. Persisted to Library/DreamParkAgent/publish-job.json so a status call after
    /// an Editor reload/restart can say the job was interrupted instead of "none".
    /// </summary>
    internal static class ContentPublishJobs
    {
        [Serializable]
        internal sealed class Job
        {
            public string jobId;
            public string contentId;
            public string target;
            public string targetContentId;
            public string mode;
            public string state;        // running | succeeded | failed
            public string phase;
            public string detail;
            public string startedAtUtc;
            public string finishedAtUtc;
            // Outcome summary (persisted), plus the full response while this Editor session lives.
            public bool verified;
            public bool noChanges;
            public int versionNumber;   // 0 = none
            [NonSerialized] public DreamParkContentReleaseResponse result;
        }

        private static Job current;
        // Unity's working directory is the project root; Application.dataPath is main-thread only.
        private static string FilePath => Path.Combine(Directory.GetCurrentDirectory(),
            "Library", "DreamParkAgent", "publish-job.json");

        internal static string BusyReason() => current != null && current.state == "running"
            ? $"A publish is already running (job {current.jobId}, {current.contentId} → {current.targetContentId}). Check it with dreampark_content_publish_status; start another only after it finishes."
            : null;

        internal static Job Begin(string contentId, string target, string mode)
        {
            bool beta = string.Equals((target ?? "").Trim(), "beta", StringComparison.OrdinalIgnoreCase);
            current = new Job
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                contentId = contentId,
                target = beta ? "beta" : "release",
                targetContentId = beta ? BetaContentId.DeriveFrom(contentId) : contentId,
                mode = string.IsNullOrWhiteSpace(mode) ? "patch" : mode.Trim().ToLowerInvariant(),
                state = "running",
                phase = "Starting",
                startedAtUtc = DateTime.UtcNow.ToString("o"),
            };
            Save();
            return current;
        }

        internal static void SetPhase(string phase, string detail)
        {
            if (current == null || current.state != "running") return;
            current.phase = phase;
            current.detail = detail;
            Save();
        }

        internal static void Follow(Job job, Task<DreamParkContentReleaseResponse> run)
        {
            run.ContinueWith(t =>
            {
                DreamParkContentReleaseResponse result = t.IsFaulted || t.IsCanceled
                    ? DreamParkContentReleaseResponse.Failed(job.contentId,
                        t.Exception?.GetBaseException().Message ?? "The publish was cancelled.")
                    : t.Result;
                Finish(job, result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        internal static void Finish(Job job, DreamParkContentReleaseResponse result)
        {
            if (job == null) return;
            if (result != null) result.jobId = job.jobId;
            job.state = result != null && result.success ? "succeeded" : "failed";
            job.phase = result != null && result.noChanges ? "No changes" : (job.state == "succeeded" ? "Done" : "Failed");
            job.detail = result?.message;
            job.finishedAtUtc = DateTime.UtcNow.ToString("o");
            job.result = result;
            job.verified = result != null && result.verified;
            job.noChanges = result != null && result.noChanges;
            job.versionNumber = result?.versionNumber ?? 0;
            if (current == job) Save();
            // The job (and its result) is recorded: now make the script moves the
            // publish's ThirdParty sync held back. They recompile and reload the
            // Editor, which is harmless from here on.
            EditorApplication.delayCall += global::DreamPark.Editor.ThirdPartySyncTool.FlushDeferredCompiledCodeMoves;
        }

        internal static DreamParkPublishStatus Status()
        {
            Job job = current ?? Load();
            if (job == null)
                return new DreamParkPublishStatus { state = "none", next = "No publish has run in this project yet." };

            var status = new DreamParkPublishStatus
            {
                jobId = job.jobId,
                state = job.state,
                contentId = job.contentId,
                target = job.target,
                targetContentId = job.targetContentId,
                mode = job.mode,
                phase = job.phase,
                detail = job.detail,
                startedAtUtc = job.startedAtUtc,
                finishedAtUtc = job.finishedAtUtc,
                agentKey = MaskedAgentKeyFor(job.targetContentId ?? job.contentId),
            };
            if (DateTime.TryParse(job.startedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime started))
            {
                DateTime end = DateTime.TryParse(job.finishedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime finished)
                    ? finished : DateTime.UtcNow;
                status.elapsedSeconds = (int)(end - started).TotalSeconds;
            }

            if (job.state == "running" && current != job)
            {
                // Recorded as running, but nothing in this Editor session owns it: the
                // Editor reloaded or restarted mid-publish, which ends the upload.
                status.state = "interrupted";
                status.next = "The Editor reloaded or restarted during this publish, so it did not finish. Check the backend with dreampark_content_inspect, then publish again.";
                return status;
            }

            if (job.state == "running")
            {
                var panel = ContentUploaderPanel.ActiveAutomatedPanel;
                if (panel != null)
                {
                    if (!string.IsNullOrWhiteSpace(panel.AutomationStatusTitle)) status.phase = panel.AutomationStatusTitle;
                    if (!string.IsNullOrWhiteSpace(panel.UploadStatusMessage)) status.detail = panel.UploadStatusMessage;
                    if (panel.AutomationStatusProgress >= 0f) status.progress = (float)Math.Round(panel.AutomationStatusProgress, 3);
                }
                var files = ContentAPI.GetUploadProgressSnapshot();
                if (files.Count > 0)
                {
                    status.filesTotal = files.Count;
                    status.filesCompleted = files.Count(f => f.completed);
                    status.filesFailed = files.Count(f => f.failed);
                    status.bytesTotal = files.Sum(f => f.totalBytes);
                    status.bytesUploaded = files.Sum(f => f.uploadedBytes);
                }
                status.next = "Still running. Poll again in a few minutes; don't start another publish or reload the Editor.";
                return status;
            }

            status.verified = job.verified;
            status.noChanges = job.noChanges;
            if (job.versionNumber > 0) status.versionNumber = job.versionNumber;
            status.result = job.result;
            status.next = job.state == "succeeded"
                ? (job.noChanges
                    ? "Finished: nothing had changed, so no version was created for " + job.targetContentId + "."
                    : $"Finished: {job.targetContentId} v{job.versionNumber} is uploaded" + (job.verified ? " and verified." : "."))
                : "Failed. Read detail/result, fix the cause, then publish again.";
            return status;
        }

        // The stored agent key (masked) the job's title uses, or null. Status makes no
        // authenticated call itself; this only shows which key the publish sends.
        private static string MaskedAgentKeyFor(string contentId)
        {
            try
            {
                var record = AgentKeyStore.SelectFor(contentId);
                return record != null ? AgentKeyStore.Mask(record.agentKey) : null;
            }
            catch (Exception) { return null; }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(current, true));
            }
            catch (Exception e) { Debug.LogWarning("[DreamPark] Could not record publish status: " + e.Message); }
        }

        private static Job Load()
        {
            try { return File.Exists(FilePath) ? JsonUtility.FromJson<Job>(File.ReadAllText(FilePath)) : null; }
            catch { return null; }
        }
    }

    [Serializable]
    public sealed class DreamParkPublishStatus
    {
        public string state;            // none | running | succeeded | failed | interrupted
        public string jobId;
        public string contentId;
        public string target;
        public string targetContentId;
        public string mode;
        public string phase;            // e.g. "Preflight", "Building Android", "Uploading", "Verifying"
        public string detail;
        public float? progress;         // 0..1 overall, when the uploader reports it
        public int? filesCompleted;
        public int? filesTotal;
        public int? filesFailed;
        public long? bytesUploaded;
        public long? bytesTotal;
        public int? elapsedSeconds;
        public string startedAtUtc;
        public string finishedAtUtc;
        public bool verified;
        public bool noChanges;
        public int? versionNumber;      // the version created, once succeeded
        public string agentKey;         // masked: the stored agent key this title uses, if any
        public DreamParkContentReleaseResponse result;   // full result (same Editor session only)
        public string next;             // what the agent should do now
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
        // --wait false: the publish was started (not finished); follow jobId with dreampark_content_publish_status.
        public bool started;
        public string jobId;
        public bool verified;
        // True when a Patch upload found nothing changed: success, but no version was created.
        public bool noChanges;
        public string contentId;
        // "release" or "beta", and the backend content ID uploaded to (<id>Beta for beta).
        public string target;
        public string targetContentId;
        public int? versionNumber;
        public string mode;
        // Set when the requested mode was upgraded to All because the patch
        // parent was built with an older (or unknown) bundle layout.
        public string modeForcedReason;
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
