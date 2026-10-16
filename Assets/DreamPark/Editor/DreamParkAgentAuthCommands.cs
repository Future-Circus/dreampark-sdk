#if UNITY_EDITOR && !DREAMPARKCORE && DREAMPARK_SDK_PACKAGES_READY
using System;
using System.IO;
using System.Threading.Tasks;
using Defective.JSON;
using DreamPark.API;
using Unity.Pipeline.Commands;

namespace DreamPark
{
    /// <summary>
    /// Agent sign-in for the Unity CLI. A coding agent asks for access to one content ID with
    /// dreampark_agent_request; DreamPark emails that content's owner (or, for a new title, the
    /// human named by --email, who will own it) an Approve link. dreampark_agent_wait polls until
    /// they click it and then receives the agent key (agk_...); the agent's own account joins
    /// that content's team. No code passes through the agent. Fallback: the same email has a
    /// 6-digit code the OWNER can enter themselves with dreampark_agent_verify. There is no
    /// approval without a human.
    ///
    /// The agent key is NOT a human's session: it belongs to the agent's own account, is
    /// scoped to content routes and the developer portal, revocable on its own (/developer → Agent keys, or
    /// dreampark_agent_signout), and stored only in this project's
    /// Library/DreamParkAgent/agent-key.json (gitignored). Results never contain the key —
    /// only a mask (agk_…last4). No command here opens a dialog.
    ///
    /// One key per title (Oct 2026): each approval covers one content ID, so a project with
    /// several content folders keeps one entry per approved title and every content-scoped
    /// command uses the entry for its --content_id (and that title's Beta twin). Each title
    /// is approved once; requests for several titles may be pending at the same time.
    /// </summary>
    public static class DreamParkAgentAuthCommands
    {
        [CliCommand("dreampark_agent_request", "Ask a human to approve this project's agent for one content ID. DreamPark emails the content's owner (or, for a new title, --email: the human who will own it) an Approve link. Then run dreampark_agent_wait, which finishes when they click it. Each title is approved once and its key is remembered alongside the others; requests for several titles may be pending at the same time.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentRequest(
            [CliArg("content_id", "Content ID (folder under Assets/Content). Default: the only content folder besides Sample and YOUR_GAME_HERE.")] string contentId = null,
            [CliArg("email", "Only for a NEW title: email of the human who will own it and approve this agent. Ignored for existing content (its owner is emailed).")] string email = null,
            [CliArg("label", "Name shown to the approver and in /developer → Agent keys. Default: \"Claude Code – <project folder>\".")] string label = null)
        {
            if (string.IsNullOrWhiteSpace(contentId))
            {
                string detectError;
                contentId = DetectContentId(out detectError);
                if (contentId == null) return Failure(detectError);
            }
            contentId = contentId.Trim();
            if (contentId.IndexOfAny(new[] { '/', '\\' }) >= 0)
                return Failure("content_id must be one folder name under Assets/Content.");
            email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
            if (email != null && (email.Length < 3 || !email.Contains("@")))
                return Failure("email must be an email address.");

            string project = ProjectFolderName();
            if (string.IsNullOrWhiteSpace(label)) label = "Claude Code – " + project;
            string machine = Environment.MachineName + " / " + project;

            var result = await DreamParkReleaseCommands.AwaitAPI(callback =>
                AuthAPI.RequestAgentKey(contentId, label.Trim(), machine, email, callback));
            if (!result.success)
            {
                string code = ErrorCode(result.response);
                if (code == "APPROVER_EMAIL_REQUIRED")
                    return new
                    {
                        success = false,
                        code,
                        contentId,
                        error = $"{contentId} is a new title — pass --email <the human who should own it>."
                    };
                // Server code + message verbatim (INVALID_REQUEST, INVALID_CONTENT_ID,
                // OWNER_UNREACHABLE, CONTENT_RESERVED, RATE_LIMITED, ...).
                return new
                {
                    success = false,
                    code,
                    contentId,
                    error = DreamParkReleaseCommands.ErrorOf(result.response, "Could not request agent access (network error).")
                };
            }

            JSONObject json = result.response != null ? result.response.json : null;
            string requestId = AuthAPI.AgentJsonString(json, "requestId");
            string sentTo = AuthAPI.AgentJsonString(json, "sentTo");
            string pollToken = AuthAPI.AgentJsonString(json, "pollToken");
            string approval = AuthAPI.AgentJsonString(json, "approval");
            long expiresAt = AuthAPI.AgentJsonMillis(json, "expiresAt");
            if (string.IsNullOrEmpty(requestId))
                return Failure("DreamPark accepted the request but returned no requestId. Is the backend up to date?");

            string recipient = string.IsNullOrEmpty(sentTo) ? "" : $" ({sentTo})";
            string fallback = $"If the Approve link can't be used, the owner can enter the 6-digit code from the same email themselves, as ONE line in Claude Code: ! unity command --project-path . dreampark_agent_verify --request_id {requestId} --code <code>";
            AgentKeyRecord existing = AgentKeyStore.FindFor(contentId);
            string existingNote = existing == null ? null
                : $"A key for {contentId} is already stored ({AgentKeyStore.Mask(existing.agentKey)}); this approval replaces only that entry.";

            if (string.IsNullOrEmpty(pollToken))
            {
                // A backend without approve-by-link: only the owner-entered code path exists.
                return new
                {
                    success = true,
                    status = AuthAPI.AgentJsonString(json, "status") ?? "pending",
                    approval = approval ?? "code",
                    requestId,
                    contentId,
                    sentTo,
                    expiresAt = expiresAt > 0 ? (long?)expiresAt : null,
                    expiresAtUtc = IsoOrNull(expiresAt),
                    label = label.Trim(),
                    machine,
                    existingKey = existingNote,
                    message = $"An approval email was sent to the owner of {contentId}{recipient}, but this DreamPark server does not support approve-by-link yet. {fallback}",
                    fallback
                };
            }

            try
            {
                // One pending request per content ID: this replaces an earlier request for
                // the same title only, so other titles' approvals can be pending in parallel.
                AgentKeyStore.SavePending(new PendingAgentRequest
                {
                    requestId = requestId,
                    pollToken = pollToken,
                    contentId = contentId,
                    sentTo = sentTo,
                    expiresAt = expiresAt,
                    approval = approval
                });
            }
            catch (Exception ex)
            {
                return Failure("The approval email was sent, but " + AgentKeyStore.PendingRelativePath + " could not be written: " + ex.Message);
            }

            var otherPending = new System.Collections.Generic.List<string>();
            foreach (var p in AgentKeyStore.LoadAllPending())
                if (!string.Equals(p.contentId, contentId, StringComparison.Ordinal) && !p.IsExpired)
                    otherPending.Add(p.contentId);

            return new
            {
                success = true,
                status = AuthAPI.AgentJsonString(json, "status") ?? "pending",
                approval = approval ?? "email_link",
                requestId,
                contentId,
                sentTo,
                expiresAt = expiresAt > 0 ? (long?)expiresAt : null,
                expiresAtUtc = IsoOrNull(expiresAt),
                label = label.Trim(),
                machine,
                existingKey = existingNote,
                otherPending = otherPending.Count > 0 ? otherPending.ToArray() : null,
                message = $"Approval email sent to the owner of {contentId}{recipient}. Run unity command --project-path . dreampark_agent_wait — it finishes on its own when they click Approve (expires {LocalTimeOrUnknown(expiresAt)})."
                    + (otherPending.Count > 0 ? $" Also pending: {string.Join(", ", otherPending)}; without --content_id the wait covers every pending title." : ""),
                fallback
            };
        }

        [CliCommand("dreampark_agent_wait", "Wait for the owner to click Approve on pending dreampark_agent_request(s), then store each agent key as its own entry (one per title; other titles' keys are kept). Without --content_id it waits on every pending request. Polls every 3 s; returns status pending at the timeout (just run it again). Never needs a code.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentWait(
            [CliArg("timeout_seconds", "How long to wait before returning status pending. Default 480, max 540.")] int timeoutSeconds = 480,
            [CliArg("content_id", "Wait only for this title's request. Default: every pending request.")] string contentId = null)
        {
            contentId = string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim();
            var outstanding = new System.Collections.Generic.List<PendingAgentRequest>();
            if (contentId != null)
            {
                PendingAgentRequest one = AgentKeyStore.LoadPending(contentId);
                if (one == null)
                {
                    var key = AgentKeyStore.FindFor(contentId);
                    if (key != null)
                        return new
                        {
                            success = true,
                            status = "approved",
                            agentKey = AgentKeyStore.Mask(key.agentKey),
                            displayName = key.DisplayName,
                            key.contentId,
                            contentIds = key.CoveredContentIds,
                            message = $"No request is pending for {contentId} and its agent key is already stored. Continue."
                        };
                    return new { success = false, status = "none", contentId, error = $"No pending request for {contentId}. Run unity command --project-path . dreampark_agent_request --content_id {contentId} first." };
                }
                outstanding.Add(one);
            }
            else
            {
                outstanding.AddRange(AgentKeyStore.LoadAllPending());
                if (outstanding.Count == 0)
                {
                    var active = AgentKeyStore.Newest;
                    if (active != null)
                        return new
                        {
                            success = true,
                            status = "approved",
                            agentKey = AgentKeyStore.Mask(active.agentKey),
                            displayName = active.DisplayName,
                            active.contentId,
                            coveredContentIds = AgentKeyStore.CoveredContentIds(),
                            message = "No request is pending and agent keys are already stored (see coveredContentIds). Continue."
                        };
                    return new { success = false, status = "none", error = "No pending request. Run unity command --project-path . dreampark_agent_request first." };
                }
            }
            int total = outstanding.Count;

            if (timeoutSeconds <= 0) timeoutSeconds = 480;
            if (timeoutSeconds > 540) timeoutSeconds = 540;
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            int transientFailures = 0;
            string lastTransient = null;
            var results = new System.Collections.Generic.List<object>();
            var approvedIds = new System.Collections.Generic.List<string>();
            var endedIds = new System.Collections.Generic.List<string>();

            while (true)
            {
                int delayMs = 3000;
                foreach (PendingAgentRequest pending in outstanding.ToArray())
                {
                    // Resolved by another process meanwhile (e.g. the owner ran
                    // dreampark_agent_verify, which removes the pending entry).
                    if (AgentKeyStore.FindPendingByRequestId(pending.requestId) == null)
                    {
                        outstanding.Remove(pending);
                        var stored = AgentKeyStore.FindFor(pending.contentId);
                        if (stored != null) { results.Add(Approved(stored, stored.approvedByEmail)); approvedIds.Add(pending.contentId); }
                        else { results.Add(RequestEnded(pending, "gone", "This request is no longer pending here and no key for it is stored (another wait or verify resolved it).")); endedIds.Add(pending.contentId); }
                        continue;
                    }
                    if (pending.IsExpired)
                    {
                        outstanding.Remove(pending);
                        TryDeletePending(pending);
                        results.Add(RequestEnded(pending, "expired", "The approval request expired before the owner approved it."));
                        endedIds.Add(pending.contentId);
                        continue;
                    }

                    var completion = new TaskCompletionSource<(bool success, DreamParkAPI.APIResponse response, AgentKeyRecord record)>();
                    try
                    {
                        AuthAPI.PollAgentRequest(pending.requestId, pending.pollToken, pending.contentId,
                            (ok, response, record) => completion.TrySetResult((ok, response, record)));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetResult((false, null, null));
                        lastTransient = ex.Message;
                    }
                    var poll = await completion.Task;
                    long status = poll.response != null ? poll.response.statusCode : 0;
                    string code = ErrorCode(poll.response);

                    object ended = null;
                    if (poll.success && poll.record != null)
                    {
                        outstanding.Remove(pending);
                        TryDeletePending(pending);
                        results.Add(Approved(poll.record, poll.record.approvedByEmail));
                        approvedIds.Add(pending.contentId);
                        continue;
                    }
                    if (poll.success)
                    {
                        transientFailures = 0; // still pending
                    }
                    else if (status == 200 || status == 201)
                    {
                        // Approved, but the key could not be stored. It is minted once, so polling
                        // again would only report "consumed".
                        ended = new
                        {
                            success = false,
                            status = "error",
                            requestId = pending.requestId,
                            contentId = pending.contentId,
                            error = (poll.response != null ? poll.response.error : null) ?? "Approved, but the agent key could not be stored.",
                            next = "Revoke the lost key at " + DreamParkAPI.baseUrl + "/developer (Agent keys), then run unity command --project-path . dreampark_agent_request --content_id " + (pending.contentId ?? "<id>") + " again."
                        };
                    }
                    else if (status == 410)
                    {
                        string serverStatus = AuthAPI.AgentJsonString(poll.response.json, "status") ?? code;
                        if (string.Equals(serverStatus, "consumed", StringComparison.OrdinalIgnoreCase))
                        {
                            var stored = AgentKeyStore.FindFor(pending.contentId);
                            if (stored != null)
                            {
                                outstanding.Remove(pending);
                                TryDeletePending(pending);
                                results.Add(Approved(stored, stored.approvedByEmail));
                                approvedIds.Add(pending.contentId);
                                continue;
                            }
                            ended = RequestEnded(pending, "consumed", "This approval was already collected, but no agent key for it is stored here (an earlier wait may have been interrupted).");
                        }
                        else ended = RequestEnded(pending, "expired", "The approval request expired before the owner approved it.");
                    }
                    else if (status == 403)
                        ended = RequestEnded(pending, "denied", "The owner denied this agent's request.");
                    else if (status == 404)
                        ended = RequestEnded(pending, "invalid", "DreamPark doesn't know this request (" + (code ?? "INVALID_REQUEST") + ").");
                    else if (status == 429)
                    {
                        long retryAfter = AuthAPI.AgentJsonMillis(poll.response.json, "retryAfterMs");
                        if (retryAfter > 0) delayMs = Math.Max(delayMs, (int)Math.Min(Math.Max(retryAfter, 1000), 60000));
                    }
                    else
                    {
                        // Network blip / 5xx / unexpected: keep the request and keep polling.
                        transientFailures++;
                        lastTransient = ErrorWithCode(poll.response, lastTransient ?? "network error");
                        delayMs = Math.Max(delayMs, Math.Min(3000 * transientFailures, 15000));
                    }

                    if (ended != null)
                    {
                        outstanding.Remove(pending);
                        TryDeletePending(pending);
                        results.Add(ended);
                        endedIds.Add(pending.contentId);
                    }
                }

                if (outstanding.Count == 0) break;
                double remainingMs = (deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) break;
                await Task.Delay((int)Math.Min(delayMs, Math.Max(remainingMs, 0)));
            }

            var stillPending = new System.Collections.Generic.List<object>();
            foreach (var pending in outstanding)
                stillPending.Add(new
                {
                    success = true,
                    status = "pending",
                    requestId = pending.requestId,
                    contentId = pending.contentId,
                    sentTo = pending.sentTo,
                    expiresAtUtc = IsoOrNull(pending.expiresAt),
                    lastError = transientFailures > 0 ? lastTransient : null,
                    message = "Not approved yet — run dreampark_agent_wait again."
                });

            // One request: the same shape as before multi-title keys.
            if (total == 1)
                return results.Count == 1 ? results[0] : stillPending[0];

            var pendingIds = new System.Collections.Generic.List<string>();
            foreach (var pending in outstanding) pendingIds.Add(pending.contentId);
            string overall = outstanding.Count > 0 ? "pending"
                : endedIds.Count == 0 ? "approved"
                : approvedIds.Count == 0 ? "ended" : "partial";
            results.AddRange(stillPending);
            return new
            {
                success = endedIds.Count == 0,
                status = overall,
                approved = approvedIds.ToArray(),
                pending = pendingIds.ToArray(),
                ended = endedIds.ToArray(),
                results,
                lastError = transientFailures > 0 ? lastTransient : null,
                message = (approvedIds.Count > 0 ? $"Approved and stored: {string.Join(", ", approvedIds)}. " : "")
                    + (pendingIds.Count > 0 ? $"Still waiting for: {string.Join(", ", pendingIds)} — run dreampark_agent_wait again. " : "")
                    + (endedIds.Count > 0 ? $"Ended without a key: {string.Join(", ", endedIds)} (see results[].next). " : "")
                    + "Each title's key is kept; content commands pick the one for their --content_id."
            };
        }

        [CliCommand("dreampark_agent_verify", "Fallback for when the Approve link can't be used: the OWNER enters the 6-digit code from the approval email (run by the owner, e.g. with ! in Claude Code; agents never ask for or relay the code). Stores the agent key in Library/DreamParkAgent/agent-key.json as that title's entry (other titles' keys are kept) and returns the identity with the key masked.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentVerify(
            [CliArg("request_id", "requestId returned by dreampark_agent_request.", Required = true)] string requestId,
            [CliArg("code", "The 6-digit code from the approval email, entered by the owner.", Required = true)] string code)
        {
            requestId = (requestId ?? "").Trim();
            string digits = (code ?? "").Replace(" ", "").Replace("-", "").Trim();
            if (requestId.Length == 0)
                return Failure("request_id is required (from dreampark_agent_request).");
            if (digits.Length != 6 || !IsAllDigits(digits))
                return Failure("code must be the 6-digit number from the owner's email.");

            var completion = new TaskCompletionSource<(bool success, DreamParkAPI.APIResponse response, AgentKeyRecord record)>();
            try
            {
                AuthAPI.VerifyAgentKey(requestId, digits,
                    (success, response, record) => completion.TrySetResult((success, response, record)));
            }
            catch (Exception ex)
            {
                return Failure("Could not verify the code: " + ex.Message);
            }
            var result = await completion.Task;

            if (!result.success || result.record == null)
            {
                string errorCode = ErrorCode(result.response);
                string hint;
                switch (errorCode)
                {
                    case "INVALID_CODE":
                        hint = "The code is wrong. Ask the owner to re-read it (attempts are limited).";
                        break;
                    case "EXPIRED":
                        hint = "This request expired. Run dreampark_agent_request again for a fresh code.";
                        break;
                    case "TOO_MANY_ATTEMPTS":
                        hint = "Too many wrong codes for this request. Run dreampark_agent_request again.";
                        break;
                    case "INVALID_REQUEST":
                        hint = "Unknown or already-used request_id. Run dreampark_agent_request again.";
                        break;
                    default:
                        hint = ErrorWithCode(result.response, "Verification failed.");
                        break;
                }
                return new { success = false, code = errorCode, error = hint };
            }

            var rec = result.record;
            // That request is resolved; other titles' pending requests stay.
            try { AgentKeyStore.DeletePendingRequest(requestId); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[DreamPark] Could not update " + AgentKeyStore.PendingRelativePath + ": " + e.Message); }
            string[] covered = rec.CoveredContentIds;
            return new
            {
                success = true,
                identity = "agent-key",
                agentKey = AgentKeyStore.Mask(rec.agentKey),
                rec.keyId,
                rec.uid,
                rec.email,
                displayName = rec.DisplayName,
                rec.label,
                rec.contentId,
                contentIds = covered,
                approvedBy = new { uid = rec.approvedByUid, email = rec.approvedByEmail },
                expiresAt = rec.expiresAt > 0 ? (long?)rec.expiresAt : null,
                expiresAtUtc = IsoOrNull(rec.expiresAt),
                rec.scopes,
                replaced = rec.replacedKeys != null && rec.replacedKeys.Length > 0 ? rec.replacedKeys : null,
                storedAt = AgentKeyStore.RelativePath,
                keysStored = AgentKeyStore.LoadAll().Count,
                message = $"Signed in as {rec.DisplayName}"
                    + (covered.Length == 0 ? "" : $", on the team of {string.Join(", ", covered)}")
                    + $". Content commands for {(covered.Length == 0 ? "this title" : string.Join(", ", covered))} now use this agent key; keys for other titles are kept. Revoke it any time with dreampark_agent_signout --content_id <id> or at {DreamParkAPI.baseUrl}/developer (Agent keys)."
            };
        }

        [CliCommand("dreampark_agent_status", "Report which DreamPark identity this project's commands use (release token, agent keys, human session, or none): every stored agent key (masked) with the titles it was approved for and its expiry, confirmed with the server, plus every pending approval request. Never returns a credential.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentStatus(
            [CliArg("content_id", "Also report which stored key commands for this title would use.")] string contentId = null)
        {
            contentId = string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim();
            var stored = AgentKeyStore.LoadAll();
            var pendings = AgentKeyStore.LoadAllPending();
            AgentKeyRecord fallbackKey = AgentKeyStore.Newest;
            bool agentActive = AuthAPI.IsAgentKeyActive;
            bool humanSession = AuthAPI.HasHumanSession;
            bool releaseTokenEnv = AuthAPI.ResolveReleaseToken(null) != null;

            string identity = agentActive ? "agent-key" : humanSession ? "session" : "none";

            var keys = new System.Collections.Generic.List<object>();
            int confirmed = 0, rejected = 0, unreachable = 0;
            foreach (var entry in stored)
            {
                object server = null;
                bool? serverConfirmed = null;
                string serverError = null;
                if (entry.IsUsable)
                {
                    var me = await DreamParkReleaseCommands.AwaitAPI(callback => AuthAPI.GetAgentIdentity(entry, callback));
                    serverConfirmed = me.success;
                    if (me.success)
                    {
                        confirmed++;
                        JSONObject json = me.response != null ? me.response.json : null;
                        long serverExpires = AuthAPI.AgentJsonMillis(json, "expiresAt");
                        string[] serverIds = AuthAPI.AgentJsonStrings(json, "contentIds");
                        server = new
                        {
                            keyId = AuthAPI.AgentJsonString(json, "keyId"),
                            uid = AuthAPI.AgentJsonString(json, "uid"),
                            email = AuthAPI.AgentJsonString(json, "email"),
                            label = AuthAPI.AgentJsonString(json, "label"),
                            contentId = AuthAPI.AgentJsonString(json, "contentId"),
                            contentIds = serverIds.Length > 0 ? serverIds : null,
                            approvedBy = ApprovedBy(json),
                            expiresAt = serverExpires > 0 ? (long?)serverExpires : null,
                            expiresAtUtc = IsoOrNull(serverExpires),
                            scopes = AuthAPI.AgentJsonStrings(json, "scopes")
                        };
                    }
                    else
                    {
                        long status = me.response != null ? me.response.statusCode : 0;
                        if (status == 401) rejected++; else unreachable++;
                        string id = string.IsNullOrEmpty(entry.contentId) ? "<id>" : entry.contentId;
                        serverError = status == 401
                            ? $"The server rejected this key (revoked or expired). Run dreampark_agent_signout --content_id {id}, then dreampark_agent_request --content_id {id} + dreampark_agent_wait."
                            : ErrorWithCode(me.response, "Could not reach DreamPark to confirm the agent key.");
                    }
                }
                keys.Add(new
                {
                    key = AgentKeyStore.Mask(entry.agentKey),
                    entry.keyId,
                    entry.uid,
                    entry.email,
                    displayName = entry.DisplayName,
                    entry.label,
                    entry.contentId,
                    contentIds = entry.CoveredContentIds,
                    approvedBy = new { uid = entry.approvedByUid, email = entry.approvedByEmail },
                    expiresAt = entry.expiresAt > 0 ? (long?)entry.expiresAt : null,
                    expiresAtUtc = IsoOrNull(entry.expiresAt),
                    expired = entry.IsExpired,
                    entry.scopes,
                    storedAtUtc = IsoOrNull(entry.storedAt),
                    fallback = fallbackKey != null && string.Equals(entry.agentKey, fallbackKey.agentKey, StringComparison.Ordinal),
                    serverConfirmed,
                    serverError,
                    server
                });
            }

            var pendingList = new System.Collections.Generic.List<object>();
            var livePendingIds = new System.Collections.Generic.List<string>();
            foreach (var pending in pendings)
            {
                if (!pending.IsExpired) livePendingIds.Add(pending.contentId);
                pendingList.Add(new
                {
                    pending.requestId,
                    pending.contentId,
                    pending.sentTo,
                    expiresAt = pending.expiresAt > 0 ? (long?)pending.expiresAt : null,
                    expiresAtUtc = IsoOrNull(pending.expiresAt),
                    expired = pending.IsExpired,
                    next = pending.IsExpired
                        ? $"Expired: run dreampark_agent_request --content_id {pending.contentId} again."
                        : "Run unity command --project-path . dreampark_agent_wait (it waits on every pending request)."
                });
            }

            object selected = null;
            if (contentId != null)
            {
                AgentKeyRecord match = AgentKeyStore.FindFor(contentId);
                AgentKeyRecord used = match ?? fallbackKey;
                selected = new
                {
                    contentId,
                    key = used != null ? AgentKeyStore.Mask(used.agentKey) : null,
                    approvedForThisTitle = match != null,
                    note = match != null ? $"Commands for {contentId} use the key approved for it."
                        : used != null ? $"No key here was approved for {contentId}; commands for it fall back to the newest key ({AgentKeyStore.Mask(used.agentKey)}, approved for {string.Join(", ", used.CoveredContentIds)}), which the server will refuse unless that agent is on {contentId}'s team."
                        : stored.Count > 0 ? $"Every stored agent key has expired; commands for {contentId} use the human session if there is one."
                        : $"No agent key is stored; commands for {contentId} use the human session if there is one."
                };
            }

            string[] covered = AgentKeyStore.CoveredContentIds();
            string action;
            if (contentId != null && AgentKeyStore.FindFor(contentId) == null)
            {
                // Approvals are per title; a beta target is covered by its title's key.
                string title = contentId.Length > AgentKeyStore.BetaSuffix.Length && contentId.EndsWith(AgentKeyStore.BetaSuffix, StringComparison.Ordinal)
                    ? contentId.Substring(0, contentId.Length - AgentKeyStore.BetaSuffix.Length) : contentId;
                var p = AgentKeyStore.LoadPending(title) ?? AgentKeyStore.LoadPending(contentId);
                bool expiredKey = AgentKeyStore.FindAllFor(contentId).Count > 0;
                action = p != null && !p.IsExpired
                    ? $"Approval pending for {p.contentId}: run unity command --project-path . dreampark_agent_wait."
                    : (expiredKey ? $"The key for {contentId} has expired" : $"No key for {contentId} is stored here")
                        + $": run dreampark_agent_request --content_id {title}, then dreampark_agent_wait.";
            }
            else if (livePendingIds.Count > 0)
                action = $"Approval pending for {string.Join(", ", livePendingIds)}: run unity command --project-path . dreampark_agent_wait.";
            else if (agentActive)
                action = rejected > 0 || unreachable > 0 ? "See serverError on the listed keys." : "none";
            else if (stored.Count > 0)
                action = "Every stored agent key has expired. Run dreampark_agent_request --content_id <id>, then dreampark_agent_wait.";
            else if (humanSession)
                action = "Commands are using the human's own session. For agent work, run dreampark_agent_request, then dreampark_agent_wait.";
            else
                action = "Run dreampark_agent_request [--content_id <id>] (new title: also --email <future owner>), then dreampark_agent_wait.";

            return new
            {
                success = true,
                identity,
                agentKeys = keys,
                coveredContentIds = covered,
                // The key used by commands that name no title (the newest usable entry).
                fallbackKey = fallbackKey != null ? AgentKeyStore.Mask(fallbackKey.agentKey) : null,
                selected,
                pendingRequests = pendingList,
                serverConfirmed = stored.Count == 0 ? (bool?)null : rejected == 0 && unreachable == 0 && confirmed > 0,
                humanSessionPresent = humanSession,
                // Release commands given --release_token or DREAMPARK_RELEASE_TOKEN use that
                // token for their own duration, ahead of the agent key.
                releaseTokenInEnvironment = releaseTokenEnv,
                file = AgentKeyStore.RelativePath,
                precedence = "release token (per release command) > agent key (the one approved for the command's content_id, else the newest) > human session",
                action
            };
        }

        [CliCommand("dreampark_agent_signout", "Revoke agent keys on the server and remove them from Library/DreamParkAgent/agent-key.json. With --content_id: only the key(s) approved for that title; without: every stored key. Local entries are removed even when the server cannot be reached. Never touches the human's own session.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentSignOut(
            [CliArg("content_id", "Sign out of this title only (its key is revoked and removed; other titles' keys stay). Default: every stored key.")] string contentId = null)
        {
            contentId = string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim();
            if (contentId != null)
            {
                var targets = AgentKeyStore.FindAllFor(contentId);
                if (targets.Count == 0)
                    return new
                    {
                        success = true,
                        contentId,
                        revokedOnServer = false,
                        removed = new string[0],
                        remainingContentIds = AgentKeyStore.CoveredContentIds(),
                        message = $"No agent key for {contentId} is stored in this project."
                    };

                var outcome = new TaskCompletionSource<(bool ok, DreamParkAPI.APIResponse response, System.Collections.Generic.List<AgentKeyRecord> removed)>();
                AuthAPI.RevokeAgentKeyFor(contentId, (ok, response, removedRecords) => outcome.TrySetResult((ok, response, removedRecords)));
                var done = await outcome.Task;

                var removedMasks = new System.Collections.Generic.List<string>();
                var alsoCovered = new System.Collections.Generic.List<string>();
                foreach (var r in done.removed)
                {
                    removedMasks.Add(AgentKeyStore.Mask(r.agentKey));
                    foreach (string id in r.CoveredContentIds)
                        if (!string.Equals(id, contentId, StringComparison.Ordinal) && !alsoCovered.Contains(id)) alsoCovered.Add(id);
                }
                bool gone = AgentKeyStore.FindAllFor(contentId).Count == 0;
                string masked = string.Join(", ", removedMasks);
                string msg = !gone ? $"Could not update {AgentKeyStore.RelativePath}. Remove the {contentId} entry by hand."
                    : done.ok ? $"Agent key {masked} for {contentId} revoked and removed; keys for other titles are kept."
                    : $"Agent key {masked} for {contentId} removed from this project, but the server did not confirm revocation ({ErrorWithCode(done.response, "no response")}). Revoke it at {DreamParkAPI.baseUrl}/developer (Agent keys) to be sure.";
                if (alsoCovered.Count > 0)
                    msg += $" That key also covered {string.Join(", ", alsoCovered)}, which now need their own approval.";
                return new
                {
                    success = gone,
                    contentId,
                    removed = removedMasks.ToArray(),
                    alsoCovered = alsoCovered.Count > 0 ? alsoCovered.ToArray() : null,
                    revokedOnServer = done.ok,
                    serverError = done.ok ? null : ErrorWithCode(done.response, "no response"),
                    remainingContentIds = AgentKeyStore.CoveredContentIds(),
                    message = msg
                };
            }

            var all = AgentKeyStore.LoadAll();
            if (all.Count == 0 && !File.Exists(AgentKeyStore.FilePath))
                return new { success = true, revokedOnServer = false, deletedLocalFile = false, message = "No agent key is stored in this project." };

            var masks = new System.Collections.Generic.List<string>();
            foreach (var r in all) masks.Add(AgentKeyStore.Mask(r.agentKey));
            string maskedAll = masks.Count == 0 ? "(unreadable file)" : string.Join(", ", masks);
            var result = await DreamParkReleaseCommands.AwaitAPI(callback => AuthAPI.RevokeAgentKey(callback));
            bool fileGone = !File.Exists(AgentKeyStore.FilePath);
            bool revoked = result.success && (all.Count == 0 || result.response != null);

            string message;
            if (revoked && fileGone)
                message = $"Agent key(s) {maskedAll} revoked and removed from this project.";
            else if (fileGone)
                message = $"Agent key(s) {maskedAll} removed from this project, but the server did not confirm every revocation ({ErrorWithCode(result.response, "no response")}). Revoke them at {DreamParkAPI.baseUrl}/developer (Agent keys) to be sure.";
            else
                message = $"Could not delete {AgentKeyStore.RelativePath}. Delete it by hand.";

            return new
            {
                success = fileGone,
                agentKey = masks.Count > 0 ? masks[0] : null,
                agentKeys = masks.ToArray(),
                revokedOnServer = revoked,
                deletedLocalFile = fileGone,
                serverError = revoked ? null : ErrorWithCode(result.response, "no response"),
                message
            };
        }

        [CliCommand("dreampark_agent_web_login", "Mint a single-use link that signs a browser into the DreamPark website as an agent key's account (default page /developer). Uses the key approved for --content_id (or for a title named in --next); otherwise the newest key. Requires an active agent key.", MainThreadRequired = true, Tags = new[] { "dreampark", "dreampark/auth" })]
        public static async Task<object> AgentWebLogin(
            [CliArg("next", "Website path to open after sign-in. Default /developer.")] string next = "/developer",
            [CliArg("content_id", "Sign in as the agent approved for this title (to edit its info, badges, items or achievements). Default: a title named in --next, else the newest key.")] string contentId = null)
        {
            if (!AuthAPI.IsAgentKeyActive)
                return Failure("No active agent key. Run dreampark_agent_request + dreampark_agent_wait first (a human session is deliberately never turned into a web link).");

            next = string.IsNullOrWhiteSpace(next) ? "/developer" : next.Trim();
            if (!next.StartsWith("/", StringComparison.Ordinal) || next.StartsWith("//", StringComparison.Ordinal))
                return Failure("next must be a site-relative path such as /developer.");

            contentId = string.IsNullOrWhiteSpace(contentId) ? ContentIdNamedIn(next) : contentId.Trim();
            AgentKeyRecord agent = AgentKeyStore.SelectFor(contentId);
            if (agent == null)
                return Failure("No active agent key. Run dreampark_agent_request + dreampark_agent_wait first.");
            string warning = contentId != null && AgentKeyStore.FindFor(contentId) == null
                ? $"No key here was approved for {contentId}; signed in with the newest key ({AgentKeyStore.Mask(agent.agentKey)}, approved for {string.Join(", ", agent.CoveredContentIds)}). Run dreampark_agent_request --content_id {contentId} to edit that title."
                : null;

            var result = await DreamParkReleaseCommands.AwaitAPI(callback => AuthAPI.RequestAgentWebHandoff(agent, next, callback));
            if (!result.success)
                return Failure("Could not create a website sign-in link: " + ErrorWithCode(result.response, "network error"));

            JSONObject json = result.response != null ? result.response.json : null;
            string token = AuthAPI.AgentJsonString(json, "token");
            string serverUrl = AuthAPI.AgentJsonString(json, "url");
            string landedNext = AuthAPI.AgentJsonString(json, "next") ?? next;
            long expiresAt = AuthAPI.AgentJsonMillis(json, "expiresAt");

            // Build from the token against OUR base URL when we can, as the server's own
            // comment asks (routes/auth.routes.js handoffOrigin): the response's url is
            // derived from server config or the request Host. Fall back to the url field,
            // prefixing the base when it is relative.
            string url;
            if (!string.IsNullOrEmpty(token))
                url = DreamParkAPI.baseUrl + "/auth/handoff?t=" + Uri.EscapeDataString(token)
                    + (string.IsNullOrEmpty(landedNext) ? "" : "&next=" + Uri.EscapeDataString(landedNext));
            else if (!string.IsNullOrEmpty(serverUrl) && serverUrl.StartsWith("/", StringComparison.Ordinal))
                url = DreamParkAPI.baseUrl + serverUrl;
            else
                url = serverUrl;

            if (string.IsNullOrEmpty(url))
                return Failure("DreamPark returned no handoff token or url.");

            return new
            {
                success = true,
                url,
                agentKey = AgentKeyStore.Mask(agent.agentKey),
                contentIds = agent.CoveredContentIds,
                warning,
                expiresAt = expiresAt > 0 ? (long?)expiresAt : null,
                expiresAtUtc = IsoOrNull(expiresAt),
                note = "single-use, expires in ~2 minutes; open it in a browser"
            };
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static object Failure(string error) => new { success = false, error };

        // Removes ONE resolved request; other titles' pending requests stay.
        private static void TryDeletePending(PendingAgentRequest pending)
        {
            if (pending == null) return;
            try { AgentKeyStore.DeletePendingRequest(pending.requestId); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[DreamPark] Could not update " + AgentKeyStore.PendingRelativePath + ": " + e.Message); }
        }

        // A content ID some stored key covers that appears as a path segment of `next`
        // (e.g. /developer/content/LaserTag), or null.
        private static string ContentIdNamedIn(string next)
        {
            if (string.IsNullOrEmpty(next)) return null;
            string path = next;
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);
            foreach (string raw in path.Split('/'))
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string segment;
                try { segment = Uri.UnescapeDataString(raw); }
                catch (Exception) { segment = raw; }
                if (AgentKeyStore.FindFor(segment) != null) return segment;
            }
            return null;
        }

        private static object Approved(AgentKeyRecord rec, string approvedByEmail)
        {
            string by = string.IsNullOrEmpty(approvedByEmail) ? "the owner" : approvedByEmail;
            return new
            {
                success = true,
                status = "approved",
                identity = "agent-key",
                agentKey = AgentKeyStore.Mask(rec.agentKey),
                rec.keyId,
                rec.uid,
                displayName = rec.DisplayName,
                rec.label,
                rec.contentId,
                contentIds = rec.CoveredContentIds,
                approvedBy = new { uid = rec.approvedByUid, email = rec.approvedByEmail },
                expiresAt = rec.expiresAt > 0 ? (long?)rec.expiresAt : null,
                expiresAtUtc = IsoOrNull(rec.expiresAt),
                rec.scopes,
                replaced = rec.replacedKeys != null && rec.replacedKeys.Length > 0 ? rec.replacedKeys : null,
                storedAt = AgentKeyStore.RelativePath,
                keysStored = AgentKeyStore.LoadAll().Count,
                message = $"Approved by {by}"
                    + (string.IsNullOrEmpty(rec.contentId) ? "" : $" for {string.Join(", ", rec.CoveredContentIds)}")
                    + "; stored alongside this project's other title keys. Continue."
            };
        }

        private static object RequestEnded(PendingAgentRequest pending, string status, string why)
        {
            string id = string.IsNullOrEmpty(pending.contentId) ? "<id>" : pending.contentId;
            return new
            {
                success = false,
                status,
                requestId = pending.requestId,
                contentId = pending.contentId,
                error = why,
                next = $"Run unity command --project-path . dreampark_agent_request --content_id {id} to ask again, then dreampark_agent_wait."
            };
        }

        private static string LocalTimeOrUnknown(long unixMs)
        {
            if (unixMs <= 0) return "in about 30 minutes";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm") + " local time"; }
            catch (ArgumentOutOfRangeException) { return "in about 30 minutes"; }
        }

        private static string ProjectFolderName()
        {
            string root = AgentKeyStore.ProjectRoot ?? "";
            string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? "Unity project" : name;
        }

        // approvedBy:{uid, email} from a server response, or null.
        private static object ApprovedBy(JSONObject json)
        {
            if (json == null || !json.HasField("approvedBy")) return null;
            JSONObject node = json.GetField("approvedBy");
            if (node == null || node.type != JSONObject.Type.Object) return null;
            return new { uid = AuthAPI.AgentJsonString(node, "uid"), email = AuthAPI.AgentJsonString(node, "email") };
        }

        // The single creator content folder, ignoring the bundled Sample and the unrenamed
        // YOUR_GAME_HERE placeholder (and hidden / "~" staging folders).
        private static string DetectContentId(out string error)
        {
            string contentRoot = Path.Combine(AgentKeyStore.ProjectRoot, "Assets", "Content");
            if (!Directory.Exists(contentRoot))
            {
                error = "No Assets/Content folder. Pass --content_id.";
                return null;
            }
            var candidates = new System.Collections.Generic.List<string>();
            foreach (string dir in Directory.GetDirectories(contentRoot))
            {
                string name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".") || name.EndsWith("~")) continue;
                if (name == "Sample" || name == "YOUR_GAME_HERE") continue;
                candidates.Add(name);
            }
            if (candidates.Count == 1)
            {
                error = null;
                return candidates[0];
            }
            error = candidates.Count == 0
                ? "No content folder under Assets/Content besides Sample/YOUR_GAME_HERE. Rename YOUR_GAME_HERE or pass --content_id."
                : "Several content folders (" + string.Join(", ", candidates) + "). Pass --content_id.";
            return null;
        }

        private static bool IsAllDigits(string value)
        {
            foreach (char c in value)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static string IsoOrNull(long unixMs)
        {
            if (unixMs <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("O"); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static string ErrorCode(DreamParkAPI.APIResponse response)
        {
            JSONObject json = response != null ? response.json : null;
            return AuthAPI.AgentJsonString(json, "code");
        }

        // The server's {error} message plus its {code} and HTTP status, for agents to act on.
        private static string ErrorWithCode(DreamParkAPI.APIResponse response, string fallback)
        {
            if (response == null) return fallback;
            string message = DreamParkReleaseCommands.ErrorOf(response, fallback);
            string code = ErrorCode(response);
            string status = response.statusCode > 0 ? "HTTP " + response.statusCode : null;
            string suffix = code != null && status != null ? $" ({status}, {code})"
                : code != null ? $" ({code})"
                : status != null ? $" ({status})"
                : "";
            return message + suffix;
        }
    }
}
#endif
