using System;
using Defective.JSON;
using UnityEngine;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    public class AuthAPI : MonoBehaviour
    {
        // Fires whenever login state may have changed (login, logout, or a refresh
        // probe revealing the session is invalid). Subscribers should call Repaint().
        // bool argument = current isLoggedIn value.
        public static event Action<bool> LoginStateChanged;

        // Fires when the cached profile (displayName / avatarUrl) is hydrated or
        // cleared. Separate from LoginStateChanged because the avatar/name arrive
        // asynchronously (a /api/user fetch) slightly after login state flips, and
        // because an avatar re-upload can change the profile without the login
        // state changing. Subscribers should re-read displayName/avatarUrl.
        public static event Action ProfileChanged;

#if !UNITY_EDITOR
        // On DEVICE, credentials are held IN MEMORY only — never written to
        // PlayerPrefs. On Android/Quest, PlayerPrefs is an unencrypted file in app
        // storage (readable via ADB / another app on a shared venue headset) AND is
        // reachable from creator Lua through CS.UnityEngine.PlayerPrefs, so
        // persisting a session bearer + email there risks cross-user session
        // hijack and PII disclosure. Keeping them in memory means a fresh app start
        // re-authenticates (via pairing/login), which is the correct behavior for a
        // shared kiosk. (Editor builds still use EditorPrefs for dev convenience —
        // a single-user dev machine, much lower risk.)
        static string _sessionToken = "";
        static string _userId       = "";
        static string _userEmail    = "";
        static string _displayName  = "";
        static string _avatarUrl    = "";
        static string _expiresAt    = "";
#endif

#if UNITY_EDITOR
        // EditorPrefs is MACHINE-GLOBAL: one flat key/value store shared by every Unity
        // project and every Unity version installed on this computer — it is NOT
        // per-project. The original keys here were bare nouns ("sessionToken", "userId",
        // "userEmail", "displayName", "avatarUrl"), so ANY other project or asset-store
        // package on the same machine could read this user's session bearer, or write
        // its own "sessionToken" and silently sign them out of DreamPark with no visible
        // cause. Namespacing under "DreamPark.Auth." makes a collision take deliberate
        // effort instead of a coincidence.
        private const string PrefSessionToken = "DreamPark.Auth.sessionToken";
        private const string PrefUserId       = "DreamPark.Auth.userId";
        private const string PrefUserEmail    = "DreamPark.Auth.userEmail";
        private const string PrefDisplayName  = "DreamPark.Auth.displayName";
        private const string PrefAvatarUrl    = "DreamPark.Auth.avatarUrl";
        // Unix-ms deadline for the stored session; "" when unknown. See sessionExpiresAt.
        // NO legacy twin: expiresAt arrived with the passwordless work and dreampark-core's
        // copy of this file has no such key, so there is nothing to stay compatible with.
        private const string PrefExpiresAt    = "DreamPark.Auth.expiresAt";

        // ── TRANSITIONAL DUAL-WRITE — READ BEFORE "TIDYING UP" ────────────────────────
        // dreampark-core ships its own copy of this file (dreampark-core/Assets/DreamPark/
        // Scripts/Core/AuthAPI.cs) and that copy still reads and writes the BARE names.
        // EditorPrefs being machine-global, the two products share one store on every dev
        // machine that has both projects checked out — so this SDK must not treat a legacy
        // key as its own to delete. Copying to the namespaced name and then deleting the
        // legacy one signs the dev out of core every time they open the SDK project; core
        // cannot sign a passwordless account back in (its popup is still the old password
        // form), so that sign-out is a dead end; and after a logout here, core's next login
        // rewrites the bare keys, which a later domain reload reads back as a silent
        // sign-in. The net effect is a sign-in/sign-out ping-pong with no way off.
        //
        // So, until core catches up: read namespaced first and FALL BACK to legacy, WRITE
        // BOTH, and never delete a legacy key except as part of a real logout — which
        // clears both sets, closing the restore-after-logout hole. This is no worse than
        // the status quo before the namespacing change (both products were already sharing
        // the global namespace) and it removes the ping-pong entirely. It also drops the
        // ordering constraint the old migration had: a fallback read needs no hook to have
        // run first, so there is nothing left for an early isLoggedIn call to race.
        //
        // WHEN dreampark-core's copy is updated to the namespaced keys: delete the Legacy*
        // constants and the three helpers below, and restore the one-time migration (copy
        // legacy -> namespaced when the namespaced key is absent, then DeleteKey the legacy
        // name) from a static constructor. Only then is the machine-global collision this
        // change is about actually closed.
        private const string LegacySessionToken = "sessionToken";
        private const string LegacyUserId       = "userId";
        private const string LegacyUserEmail    = "userEmail";
        private const string LegacyDisplayName  = "displayName";
        private const string LegacyAvatarUrl    = "avatarUrl";

        // Namespaced value wins when present, so a stale legacy value left behind by core
        // cannot override a fresher session written here. HasKey rather than a non-empty
        // check: an explicitly stored "" is still an answer, and every path that means "no
        // value" deletes the key rather than blanking it.
        private static string GetAuthPref(string namespacedKey, string legacyKey)
        {
            if (UnityEditor.EditorPrefs.HasKey(namespacedKey))
            {
                return UnityEditor.EditorPrefs.GetString(namespacedKey, "");
            }
            return UnityEditor.EditorPrefs.GetString(legacyKey, "");
        }

        // Both names, always — the legacy write is what keeps dreampark-core signed in.
        private static void SetAuthPref(string namespacedKey, string legacyKey, string value)
        {
            UnityEditor.EditorPrefs.SetString(namespacedKey, value);
            UnityEditor.EditorPrefs.SetString(legacyKey, value);
        }

        // The ONLY place a legacy key is allowed to be deleted: a deliberate logout, where
        // leaving the bare copy behind is exactly what would sign the user back in.
        private static void DeleteAuthPref(string namespacedKey, string legacyKey)
        {
            UnityEditor.EditorPrefs.DeleteKey(namespacedKey);
            UnityEditor.EditorPrefs.DeleteKey(legacyKey);
        }
#endif

        // Internal — used by AuthAPI to notify subscribers. Wrapped so callers
        // outside AuthAPI can't fire the event.
        private static void RaiseLoginStateChanged()
        {
            try { LoginStateChanged?.Invoke(isLoggedIn); }
            catch (Exception e) { Debug.LogWarning($"[AuthAPI] LoginStateChanged subscriber threw: {e}"); }
        }

        private static void RaiseProfileChanged()
        {
            try { ProfileChanged?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"[AuthAPI] ProfileChanged subscriber threw: {e}"); }
        }

        // Stores displayName + avatar from a sanitized user object (the shape
        // /auth/login and GET /api/user return). Avatar prefers `avatarUrl`, falling
        // back to the legacy `photoURL` the backend keeps in sync. Display-only —
        // stored exactly like email (EditorPrefs in-editor, in-memory on device).
        private static void ApplyProfileFields(JSONObject user)
        {
            if (user == null) return;
            string dn = user.HasField("displayName") ? user.GetField("displayName").stringValue : null;
            string av = user.HasField("avatarUrl") ? user.GetField("avatarUrl").stringValue
                      : user.HasField("photoURL")  ? user.GetField("photoURL").stringValue
                      : null;
#if UNITY_EDITOR
            if (dn != null) SetAuthPref(PrefDisplayName, LegacyDisplayName, dn);
            if (av != null) SetAuthPref(PrefAvatarUrl, LegacyAvatarUrl, av);
#else
            if (dn != null) _displayName = dn;
            if (av != null) _avatarUrl   = av;
#endif
            RaiseProfileChanged();
        }

        private static void ClearProfileFields()
        {
#if UNITY_EDITOR
            DeleteAuthPref(PrefDisplayName, LegacyDisplayName);
            DeleteAuthPref(PrefAvatarUrl, LegacyAvatarUrl);
#else
            _displayName = "";
            _avatarUrl   = "";
#endif
            RaiseProfileChanged();
        }

        // Fetches the canonical identity for the current session and caches
        // displayName + avatarUrl. Safe to call after ANY login path (email/password,
        // native handoff, pairing) since it only needs a valid session bearer. Also
        // useful to refresh after the user changes their name/avatar mid-session.
        public static void HydrateProfile(Action<bool> callback = null)
        {
            if (!isLoggedIn) { callback?.Invoke(false); return; }
            // An agent key is scoped to content routes — /api/user would 403 with
            // AGENT_KEY_SCOPE, and the cached profile is the human's anyway.
            if (IsAgentKeyActive) { callback?.Invoke(false); return; }
            DreamParkAPI.GET("/api/user/", GetUserAuth(), (success, response) => {
                if (success && response != null && response.json != null && response.json.HasField("user"))
                {
                    ApplyProfileFields(response.json.GetField("user"));
                    callback?.Invoke(true);
                }
                else
                {
                    callback?.Invoke(false);
                }
            });
        }

        // ── RELEASE TOKENS — third auth branch alongside the session bearer above
        // and GetAPIKey() below ──────────────────────────────────────────────────
        // A release token (`rlt_...`) lets unattended/CI callers (DreamParkReleaseCommands)
        // authenticate WITHOUT the human email-OTP round trip AuthPopup requires — that
        // flow is deliberately un-completable by a script (dreampark_auth_open's own doc
        // comment: "the email code stays between the human and Unity"), so release
        // automation needs a credential a human can mint once and hand to a script.
        //
        // Deliberately NOT stored in EditorPrefs like the session bearer above. EditorPrefs
        // is machine-global (see the 40 lines of comments on PrefSessionToken) — a fine
        // trade for a human's interactive session on their own dev machine, but exactly
        // the wrong store for a credential meant to sit on a CI box or a shared machine
        // that other tooling also touches. Instead this is an ambient, in-memory-only
        // override that a release command pushes for the duration of its own work via
        // WithReleaseToken() and always pops afterward, in both the editor and player.
        [ThreadStatic] private static string _releaseTokenOverride;

        // True while a release command has an active token override — used by preflight
        // checks so "authenticated" means "has EITHER a human session OR a release token"
        // without those checks needing to know which one.
        public static bool HasReleaseToken => !string.IsNullOrEmpty(_releaseTokenOverride);

        // Resolves the token a release command should use, in priority order:
        // an explicit CliArg (a caller passing a freshly minted token for one call),
        // then the DREAMPARK_RELEASE_TOKEN environment variable (the CI/headless case —
        // set once per job, reused across every command it runs). Returns null when
        // neither is present, meaning the command falls back to the human session.
        public static string ResolveReleaseToken(string explicitToken) {
            if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
            var env = Environment.GetEnvironmentVariable("DREAMPARK_RELEASE_TOKEN");
            return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
        }

        // Pushes `token` as the bearer GetUserAuth() returns for the lifetime of the
        // returned scope, then restores whatever was active before — safe to nest,
        // though release commands don't currently call each other. `token` may be
        // null/empty (ResolveReleaseToken found nothing), in which case this is a no-op
        // that falls through to the ordinary session bearer, so callers can always wrap
        // unconditionally: `using (AuthAPI.WithReleaseToken(ResolveReleaseToken(arg)))`.
        public static IDisposable WithReleaseToken(string token) => new ReleaseTokenScope(token);

        private sealed class ReleaseTokenScope : IDisposable {
            private readonly string _previous;
            private bool _disposed;
            public ReleaseTokenScope(string token) {
                _previous = _releaseTokenOverride;
                // A null/empty token means "this call isn't asking for an override" —
                // NOT "clear whatever override is active". Release commands call each
                // other directly (PublishSDK calls SDKPreflightInternal, PublishContent
                // calls ContentPreflightInternal) inside the SAME scope without
                // re-forwarding the token they already resolved; if this constructor
                // blanked the ambient value on null, the outer command would silently
                // drop back to the human session bearer partway through its own work.
                if (!string.IsNullOrEmpty(token)) _releaseTokenOverride = token;
            }
            public void Dispose() {
                if (_disposed) return;
                _disposed = true;
                _releaseTokenOverride = _previous;
            }
        }

        // ── AGENT KEYS — Editor-only, between the release token and the human session ──
        // An agent key (`agk_...`) is what a coding agent gets when a HUMAN (the content's
        // owner) reads it the 6-digit code from a POST /auth/agent/request email. It belongs
        // to the agent's OWN account (which joins that content's team), is scoped to content
        // routes + /developer and revocable at /developer → Agent keys — never the human's
        // personal session.
        // Stored project-locally in Library/DreamParkAgent/agent-key.json — see
        // AgentKeyStore at the bottom of this file for why not EditorPrefs. A project keeps
        // one key PER TITLE approval (Oct 2026); AgentKeyStore says which one a request uses.
        //
        // Precedence, everywhere below: release token > agent key > human session.
        // The release token stays first because it is pushed for one command's
        // duration on purpose; the agent key beats the human session because an agent
        // that signed in deliberately must never silently act as the human instead.
        public static bool IsAgentKeyActive {
            get {
#if UNITY_EDITOR
                return AgentKeyStore.IsActive;
#else
                return false;
#endif
            }
        }

        // The human's own session bearer, ignoring every override. Used where an
        // agent key or release token must NEVER be sent: /auth/logout (which revokes
        // ALL of the human's sessions) and the /auth/refresh probe.
        private static string HumanSessionToken {
            get {
#if UNITY_EDITOR
                return GetAuthPref(PrefSessionToken, LegacySessionToken);
#else
                return _sessionToken;
#endif
            }
        }

        // Whether a HUMAN session is stored, regardless of any agent key / release token
        // taking precedence over it. For status reporting only; never exposes the value.
        public static bool HasHumanSession => !string.IsNullOrEmpty(HumanSessionToken);

#if DREAMPARKCORE
        // CORE-ONLY, internal (never reachable from creator Lua). The raw human session
        // for DreamParkAPI.AttachUserSession — the signed-in account riding BESIDE the
        // device key on /app/park reads, so the server can let the park's owner read
        // their own park from their own phone. Null when nobody is signed in.
        internal static string HumanSessionForDeviceReads =>
            string.IsNullOrEmpty(HumanSessionToken) ? null : HumanSessionToken;
#endif

        public static string GetUserAuth() => GetUserAuthFor(null);

        // GetUserAuth for a request about ONE content ID (its "<id>Beta" twin included): with
        // several agent keys stored (one per title approval), the key approved for that title
        // is sent; without one, the key in effect (the open content scope, else the newest).
        // Release token and human session behave exactly as in GetUserAuth.
        public static string GetUserAuthFor(string contentId) {
            // A release token in scope wins outright — it's a deliberate, narrow
            // override for the one command that pushed it, never a silent fallback
            // a human's interactive session could accidentally pick up.
            if (HasReleaseToken) return $"Bearer {_releaseTokenOverride}";
#if UNITY_EDITOR
            var agent = string.IsNullOrWhiteSpace(contentId)
                ? AgentKeyStore.ActiveRecord
                : (AgentKeyStore.FindFor(contentId) ?? AgentKeyStore.ActiveRecord);
            if (agent != null) return $"Bearer {agent.agentKey}";
#endif
            return $"Bearer {HumanSessionToken}";
        }

#if UNITY_EDITOR
        // Selects which stored agent key content-scoped work uses: every request made while
        // the scope is open picks the key approved for `contentId` (see AgentKeyStore). A
        // null/blank ID is a no-op, so callers can wrap unconditionally.
        public static IDisposable WithAgentContent(string contentId) => AgentKeyStore.WithContent(contentId);
#endif

        // "ApiKey <key>" header used by core's runtime to call /app/* endpoints.
        //
        // DEVICE builds: returns the per-device hsk_* key issued by
        // POST /app/device/enroll (see Assets/Scripts/Internal/DeviceKeyManager.cs).
        // Public APKs ship with NO shared secret — a leaked key burns one device,
        // not the fleet, and the backend can revoke/rate-limit per device.
        // Boot.cs runs DeviceKeyManager.EnsureEnrolledAsync() before the first
        // authenticated call.
        //
        // EDITOR (core only): core's Editor tooling registers EditorApiKeyProvider
        // at load. The credential comes from the developer's machine, never from
        // source, and nothing here is compiled into a player.
        //
        // SDK callers use GetUserAuth() (session bearer) instead — anything in the
        // public SDK that legitimately needs to hit the backend goes through user auth.
#if DREAMPARKCORE && UNITY_EDITOR
        // Registered by core-only Editor code; null in the SDK and absent from players.
        public static Func<string> EditorApiKeyProvider;
#endif
        public static string GetAPIKey() {
#if DREAMPARKCORE
#if UNITY_EDITOR
            string editorKey = EditorApiKeyProvider?.Invoke();
            return string.IsNullOrEmpty(editorKey) ? "" : $"ApiKey {editorKey}";
#else
            return DeviceKeyManager.GetAuthHeader();
#endif
#else
            Debug.LogError("[AuthAPI] GetAPIKey() is core-only. SDK builds should use GetUserAuth() (session) instead.");
            return "";
#endif
        }

        public static bool isLoggedIn {
            get {
                if (HasReleaseToken) return true;
                if (IsAgentKeyActive) return true;
                return !string.IsNullOrEmpty(HumanSessionToken);
            }
        }
        public static string userId {
            get {
#if UNITY_EDITOR
                var agent = AgentKeyStore.ActiveRecord;
                if (agent != null) return agent.uid ?? "";
                return GetAuthPref(PrefUserId, LegacyUserId);
#else
                return _userId;
#endif
            }
        }
        public static string sessionToken {
            get {
                if (HasReleaseToken) return _releaseTokenOverride;
#if UNITY_EDITOR
                var agent = AgentKeyStore.ActiveRecord;
                if (agent != null) return agent.agentKey;
#endif
                return HumanSessionToken;
            }
        }
        // Cached email from the most recent login response. Used purely for display.
        public static string email {
            get {
                if (HasReleaseToken) return "Release agent";
#if UNITY_EDITOR
                var agent = AgentKeyStore.ActiveRecord;
                if (agent != null) return agent.DisplayName;
                return GetAuthPref(PrefUserEmail, LegacyUserEmail);
#else
                return _userEmail;
#endif
            }
        }
        // Cached display name (falls back to "" when unset — callers should use email
        // as the visible label when this is empty). Hydrated via HydrateProfile().
        // Blank while an agent key is active: the cached profile belongs to whichever
        // HUMAN last signed in here, which need not be the account the key acts for.
        public static string displayName {
            get {
#if UNITY_EDITOR
                if (IsAgentKeyActive) return "";
                return GetAuthPref(PrefDisplayName, LegacyDisplayName);
#else
                return _displayName;
#endif
            }
        }
        // Cached avatar image URL (Firebase Storage public URL). "" when the user
        // has no avatar — callers should show their placeholder in that case.
        public static string avatarUrl {
            get {
#if UNITY_EDITOR
                if (IsAgentKeyActive) return "";
                return GetAuthPref(PrefAvatarUrl, LegacyAvatarUrl);
#else
                return _avatarUrl;
#endif
            }
        }

        // Unix-ms instant at which the stored session stops being accepted, or 0 when we
        // don't know (a session stored by an older SDK, or the native handoff, which
        // carries no expiry).
        //
        // Worth surfacing because /auth/refresh only VALIDATES a session — it verifies
        // the cookie and returns the user, and never re-mints or extends it (see
        // routes/auth.routes.js POST /refresh). So the ~14-day deadline the login
        // response reported is the real one, and nothing the editor does moves it. A
        // creator who is not told will find out when a 40-minute upload 401s at the end.
        public static long sessionExpiresAt {
            get {
                if (HasReleaseToken) return 0;
#if UNITY_EDITOR
                var agent = AgentKeyStore.ActiveRecord;
                if (agent != null) return agent.expiresAt > 0 ? agent.expiresAt : 0;
                var raw = UnityEditor.EditorPrefs.GetString(PrefExpiresAt, "");
#else
                var raw = _expiresAt;
#endif
                long parsed;
                return long.TryParse(raw, out parsed) ? parsed : 0;
            }
        }

        // Hours left on the stored session, or -1 when the expiry is unknown. Callers MUST
        // treat -1 as "say nothing" rather than "expired" — an unknown expiry is the
        // normal state for a session that predates this field, and warning on it would
        // nag every existing user once, forever.
        public static double sessionExpiresInHours {
            get {
                long expiresAt = sessionExpiresAt;
                if (expiresAt <= 0) return -1;
                // DateTimeOffset rather than DateTime.UtcNow arithmetic so the Unix-ms the
                // server sent is compared in the same units it was minted in, with no
                // local-timezone step in between.
                double msLeft = expiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return msLeft <= 0 ? 0 : msLeft / (1000.0 * 60.0 * 60.0);
            }
        }

#if DREAMPARKCORE
        // CORE-ONLY (compiled out of the public SDK, like GetAPIKey). Injects a session
        // that the native layer already holds securely into AuthAPI. This is the host
        // app's DEVICE login path: the iOS app authenticates, persists the session in the
        // iOS Keychain (encrypted at rest), and hands the bearer to Unity via the
        // NativeInterfaceManager "LOGIN" message. SDK creators never do this — they use
        // Login()/pairing — so it stays behind DREAMPARKCORE rather than being a public
        // SDK surface for injecting arbitrary sessions.
        //
        // Stored exactly like Login(): IN MEMORY ONLY on device, never in PlayerPrefs
        // (unencrypted + reachable from creator Lua; the only encrypted, persisted copy is
        // the iOS Keychain). EditorPrefs in-editor for dev convenience. GetUserAuth() reads
        // this for the "Authorization: Bearer <token>" header.
        //
        // Prior to this, the "LOGIN" handoff wrote to PlayerPrefs while GetUserAuth() read
        // the in-memory field (post security-hardening), so on device the bearer was always
        // empty → backend returned 401 on every session-authed call (e.g. park save).
        public static void SetSessionFromNative(string token, string uid = null, string userEmail = null) {
            token = token ?? "";
#if UNITY_EDITOR
            SetAuthPref(PrefSessionToken, LegacySessionToken, token);
            if (uid != null) SetAuthPref(PrefUserId, LegacyUserId, uid);
            if (userEmail != null) SetAuthPref(PrefUserEmail, LegacyUserEmail, userEmail);
            // The native handoff carries no expiresAt — the iOS Keychain holds the
            // session's lifetime, not Unity. Clear any stale value from a previous
            // sign-in so sessionExpiresInHours reports "unknown" rather than the old
            // session's deadline.
            UnityEditor.EditorPrefs.DeleteKey(PrefExpiresAt);
#else
            _sessionToken = token;
            if (uid != null) _userId = uid;
            if (userEmail != null) _userEmail = userEmail;
            _expiresAt = "";
#endif
            RaiseLoginStateChanged();
            // Native handoff only carries email — pull displayName + avatar from the
            // backend now that the session bearer is set.
            HydrateProfile();
        }
#endif

        // Turns a successful auth response into the stored session. /auth/login and
        // /auth/otp/verify return the SAME body — { session, uid, email, expiresAt,
        // user, isNewUser, ageVerified } — so both paths land here rather than each
        // keeping a private copy of the storage code. Two copies is exactly how the
        // legacy path and the passwordless path would quietly drift apart (one storing
        // expiresAt, the other not; one applying the profile, the other not) and the
        // symptom would be a subtly wrong signed-in state, not a compile error.
        //
        // `fallbackEmail` is what the user typed, used only when the server omits the
        // canonical address.
        private static void StoreSessionResponse(JSONObject json, string fallbackEmail) {
            if (json == null) return;

            string canonicalEmail = json.HasField("email")
                ? json.GetField("email").stringValue
                : fallbackEmail;
            string session = json.HasField("session") ? json.GetField("session").stringValue : "";
            string uid     = json.HasField("uid")     ? json.GetField("uid").stringValue     : "";

            // expiresAt is a Unix-ms NUMBER. Kept as a STRING in prefs because EditorPrefs
            // has no 64-bit integer type — SetInt would silently truncate a millisecond
            // timestamp. Absent (older backend, or a shape we didn't expect) means we
            // record nothing rather than guessing, and sessionExpiresInHours reports -1.
            var expiresNode = json.GetField("expiresAt");
            string expiresAt = expiresNode != null && expiresNode.type == JSONObject.Type.Number
                ? expiresNode.longValue.ToString()
                : "";

#if UNITY_EDITOR
            SetAuthPref(PrefSessionToken, LegacySessionToken, session);
            SetAuthPref(PrefUserId, LegacyUserId, uid);
            SetAuthPref(PrefUserEmail, LegacyUserEmail, canonicalEmail ?? "");
            if (string.IsNullOrEmpty(expiresAt)) {
                UnityEditor.EditorPrefs.DeleteKey(PrefExpiresAt);
            } else {
                UnityEditor.EditorPrefs.SetString(PrefExpiresAt, expiresAt);
            }
#else
            _sessionToken = session;
            _userId       = uid;
            _userEmail    = canonicalEmail ?? "";
            _expiresAt    = expiresAt;
#endif
            RaiseLoginStateChanged();
            // Both endpoints already return a sanitized `user` (displayName + photoURL) —
            // apply it directly, no extra round trip needed.
            if (json.HasField("user")) {
                ApplyProfileFields(json.GetField("user"));
            }
        }

        [Obsolete("DreamPark is passwordless as of July 2026 — use RequestLoginCode/VerifyLoginCode.")]
        public static void Login(string email, string password, Action<bool, APIResponse> callback) {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("email", email);
            body.AddField("password", password);
            DreamParkAPI.POST("/auth/login", "", body, (success, response) => {
                if (success) {
                    StoreSessionResponse(response != null ? response.json : null, email);
                }
                callback?.Invoke(success, response);
            });
        }

        // Step 1 of the passwordless flow: ask the backend to email a 6-digit code.
        //
        // POST /auth/otp/request {email} ALWAYS answers 200 for a plausible address —
        // unknown address, throttled address (3 sends per email per 10 minutes), and
        // suppressed address are deliberately indistinguishable, so that this endpoint
        // can never be used to ask "does alice@example.com have a DreamPark account?".
        // Callers MUST NOT branch on anything but success/failure here, or they rebuild
        // that oracle in the client. A failure means a malformed address (400) or a
        // transport error — never "no such user".
        //
        // No auth header: by definition there is no session yet.
        public static void RequestLoginCode(string email, Action<bool, APIResponse> callback) {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("email", email);
            DreamParkAPI.POST("/auth/otp/request", "", body, (success, response) => {
                callback?.Invoke(success, response);
            });
        }

        // Step 2 of the passwordless flow: exchange the emailed code for a session.
        //
        // POST /auth/otp/verify {email, code} is get-or-create — it signs an existing
        // user in and CREATES the account when the address is new (possessing the code
        // is the email verification), which is why the SDK no longer has a separate
        // signup path. On success the body is byte-identical in shape to /auth/login's,
        // so StoreSessionResponse handles both.
        //
        // Failures the caller should expect:
        //   401 + reason: "expired" | "too-many-attempts" | (wrong code)
        //       Codes live 10 minutes and die after 5 wrong guesses.
        //   403 + code: "AGE_UNVERIFIED" for a NEW account with no age band recorded,
        //       or "AGE_BLOCKED_UNDER_13" for a refusal. These are the exact strings
        //       lib/ageGate.js assertMayCreateAccount() sets on the error; "AGE_GATE_*"
        //       appears only in prose comments in routes/auth.routes.js and matching on
        //       that prefix matches NOTHING the server actually sends.
        //
        //       AGE_UNVERIFIED is recoverable: the server checks the code BEFORE the age
        //       gate and deliberately does not consume it, so the 403 itself costs the
        //       user nothing. It does NOT follow that the same code survives the recovery
        //       — lib/authOtp.js keeps ONE live code per address (requestOtp tx.set()
        //       overwrites the doc) and a successful verify deletes it, so answering the
        //       birthday question on the web necessarily destroys the code sitting in the
        //       SDK. Callers must offer a fresh send ("Resend code"), not "enter the same
        //       code again".
        //
        //       AGE_BLOCKED_UNDER_13 is NOT recoverable — it is assertMayCreateAccount
        //       refusing outright, and the web routes it to a parent-invite flow. Callers
        //       must surface the server's message and offer no retry.
        public static void VerifyLoginCode(string email, string code, Action<bool, APIResponse> callback) {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("email", email);
            body.AddField("code", code);
            DreamParkAPI.POST("/auth/otp/verify", "", body, (success, response) => {
                if (success) {
                    StoreSessionResponse(response != null ? response.json : null, email);
                }
                callback?.Invoke(success, response);
            });
        }

        // Local-only logout: clears stored credentials and notifies subscribers without
        // hitting the network. Used by Refresh() when the server reports the session
        // is invalid (avoids a redundant /auth/logout call on a token that's already dead).
        private static void ClearLocalSession()
        {
#if UNITY_EDITOR
            DeleteAuthPref(PrefSessionToken, LegacySessionToken);
            DeleteAuthPref(PrefUserId, LegacyUserId);
            DeleteAuthPref(PrefUserEmail, LegacyUserEmail);
            UnityEditor.EditorPrefs.DeleteKey(PrefExpiresAt);
#else
            _sessionToken = "";
            _userId       = "";
            _userEmail    = "";
            _expiresAt    = "";
#endif
            RaiseLoginStateChanged();
            ClearProfileFields();
        }

        public static void Logout(Action<bool, APIResponse> callback) {
#if UNITY_EDITOR
            // NEVER send an agent key to /auth/logout: that route revokes ALL of the
            // human's sessions. "Sign out" while an agent key is active means signing
            // the AGENT out — revoke every stored agent key (each on its own revoke
            // route) and delete them locally; the human's own session (if any) is left
            // exactly as it was.
            if (IsAgentKeyActive) {
                RevokeAgentKey((revoked, revokeResponse) => callback?.Invoke(revoked, revokeResponse));
                return;
            }
#endif
            // The human's own bearer, explicitly — never GetUserAuth(), which may be
            // carrying a release token for the duration of a command.
            string humanToken = HumanSessionToken;
            JSONObject body = new JSONObject();
            body.AddField("session", humanToken);
            DreamParkAPI.POST("/auth/logout", $"Bearer {humanToken}", body, (success, response) => {
                if (success) {
                    callback?.Invoke(success, response);
                } else {
                    callback?.Invoke(success, response);
                }
#if UNITY_EDITOR
                    DeleteAuthPref(PrefSessionToken, LegacySessionToken);
                    DeleteAuthPref(PrefUserId, LegacyUserId);
                    DeleteAuthPref(PrefUserEmail, LegacyUserEmail);
                    UnityEditor.EditorPrefs.DeleteKey(PrefExpiresAt);
#else
                    _sessionToken = "";
                    _userId       = "";
                    _userEmail    = "";
                    _expiresAt    = "";
#endif
                RaiseLoginStateChanged();
                ClearProfileFields();
            });
        }

        // Probes /auth/refresh with the stored session. Returns true if the session
        // is still valid; on 401 (or any auth error), clears the local session and
        // fires LoginStateChanged so panels re-render the logged-out state.
        public static void Refresh(Action<bool> callback)
        {
#if UNITY_EDITOR
            // An active agent key is probed on its own endpoint and NEVER through
            // /auth/refresh, whose 401 path below wipes the HUMAN session. A 401 here
            // means the key itself was revoked (or expired server-side): drop only the
            // local key file, which hands the editor back to the human session, if any.
            var agent = AgentKeyStore.ActiveRecord;
            if (agent != null)
            {
                DreamParkAPI.GET("/auth/agent/me", $"Bearer {agent.agentKey}", (ok, response) => {
                    if (ok) { callback?.Invoke(true); return; }
                    if (response != null && response.statusCode == 401)
                    {
                        // Only THAT key: the project's keys for other titles are unaffected.
                        Debug.LogWarning($"[AuthAPI] The DreamPark agent key {AgentKeyStore.Mask(agent.agentKey)} was rejected (revoked or expired). Removed it from this project.");
                        try { AgentKeyStore.RemoveKey(agent.agentKey); }
                        catch (Exception e) { Debug.LogWarning("[AuthAPI] Could not update " + AgentKeyStore.RelativePath + ": " + e.Message); }
                        RaiseLoginStateChanged();
                        callback?.Invoke(false);
                        return;
                    }
                    // Network blip, 5xx, or a backend without the route yet: leave
                    // the key alone, same leniency as the session probe below.
                    callback?.Invoke(true);
                });
                return;
            }
#endif
            // The human's own bearer, explicitly: a release token in scope must never be
            // what decides whether the human session gets wiped.
            var token = HumanSessionToken;
            if (string.IsNullOrEmpty(token))
            {
                callback?.Invoke(false);
                return;
            }
            JSONObject body = new JSONObject();
            body.AddField("session", token);
            DreamParkAPI.POST("/auth/refresh", "", body, (success, response) => {
                if (success)
                {
                    callback?.Invoke(true);
                    return;
                }
                // 401 means the cookie is dead — wipe local state. Other failures
                // (network blip, 500) we leave the session alone so a temporary
                // outage doesn't kick the user out.
                if (response != null && response.statusCode == 401)
                {
                    ClearLocalSession();
                }
                callback?.Invoke(false);
            });
        }

#if UNITY_EDITOR
        // ── AGENT KEY FLOW (Editor-only) ────────────────────────────────────────────
        // Backend contract (DreamPark-Web routes/auth.routes.js):
        //   POST /auth/agent/request {contentId, label, machine, email?} → 200 {status:"pending",
        //        requestId, expiresAt, sentTo(masked)}. Existing content: emails its OWNER. New
        //        content: `email` (the human who will own it) is required, else
        //        400 APPROVER_EMAIL_REQUIRED. Also 400 INVALID_REQUEST/INVALID_CONTENT_ID,
        //        409 OWNER_UNREACHABLE/CONTENT_RESERVED, 429 RATE_LIMITED.
        //   POST /auth/agent/verify  {requestId, code} → 200 {agentKey, keyId, uid (the AGENT's
        //        own account), email (may be null), label, expiresAt(ms), scopes, contentId,
        //        approvedBy:{uid, email(masked)}} | 400/401 {error, code: INVALID_REQUEST|
        //        INVALID_CODE|EXPIRED|TOO_MANY_ATTEMPTS}. The agent joins that content's team.
        //   The pending response also carries pollToken ("akp_...", secret) and
        //        approval:"email_link"; the owner approves by clicking a link (request TTL 30 min)
        //        and the agent collects its key with POST /auth/agent/poll (PollAgentRequest).
        //   GET  /auth/agent/me      (Bearer agk_) → {keyId, uid, email, label, expiresAt, scopes}
        //   POST /auth/agent/revoke  (Bearer agk_) → revokes ONLY this key
        //   POST /auth/handoff {next}(Bearer agk_) → {token, url, next, expiresAt}
        // Never route an agent key to /auth/logout — see Logout().

        // Asks a HUMAN to approve this agent for ONE content ID. Always answers
        // {status:"pending", requestId, expiresAt, sentTo(masked)} on success: for an existing
        // content ID the server emails its owner (email is ignored); for a NEW content ID
        // `email` names the human who will own it and is required (400 APPROVER_EMAIL_REQUIRED
        // otherwise). There is no auto-approval: the key only comes from VerifyAgentKey.
        public static void RequestAgentKey(string contentId, string label, string machine, string email,
            Action<bool, APIResponse> callback) {
            byte[] body = AgentJsonBody(
                "contentId", string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim(),
                "label", label,
                "machine", machine,
                "email", string.IsNullOrWhiteSpace(email) ? null : email.Trim());
            DreamParkAPI.POST("/auth/agent/request", "", body, (success, response) => callback?.Invoke(success, response));
        }

        // On success the key is added to Library/DreamParkAgent/agent-key.json as the entry
        // for the request's content ID (replacing only an earlier entry for that title) and
        // LoginStateChanged fires. The third callback argument is the stored record, or null
        // on failure. The raw key is never logged.
        public static void VerifyAgentKey(string requestId, string code,
            Action<bool, APIResponse, AgentKeyRecord> callback) {
            byte[] body = AgentJsonBody("requestId", requestId, "code", code);
            var pending = AgentKeyStore.FindPendingByRequestId(requestId);
            string requestedContentId = pending != null ? pending.contentId : null;
            DreamParkAPI.POST("/auth/agent/verify", "", body, (success, response) => {
                if (!success) { callback?.Invoke(false, response, null); return; }
                AgentKeyRecord record;
                string error;
                if (!TryStoreAgentKey(response != null ? response.json : null, requestedContentId, out record, out error)) {
                    if (response != null) response.error = error;
                    callback?.Invoke(false, response, null);
                    return;
                }
                callback?.Invoke(true, response, record);
            });
        }

        // Parse, validate, persist and announce a verify/poll response. The entry covers the
        // content ID the request was made for plus whatever the server says (contentId,
        // contentIds[]). Unknown fields are ignored.
        private static bool TryStoreAgentKey(JSONObject json, string requestedContentId, out AgentKeyRecord record, out string error) {
            record = new AgentKeyRecord {
                agentKey     = AgentJsonString(json, "agentKey"),
                keyId        = AgentJsonString(json, "keyId"),
                uid          = AgentJsonString(json, "uid"),
                email        = AgentJsonString(json, "email"),
                label        = AgentJsonString(json, "label"),
                expiresAt    = AgentJsonMillis(json, "expiresAt"),
                scopes       = AgentJsonStrings(json, "scopes"),
                contentId    = AgentJsonString(json, "contentId"),
            };
            var covered = new System.Collections.Generic.List<string>();
            foreach (string id in new[] { requestedContentId, record.contentId })
                if (!string.IsNullOrWhiteSpace(id) && !covered.Contains(id.Trim())) covered.Add(id.Trim());
            foreach (string id in AgentJsonStrings(json, "contentIds"))
                if (!string.IsNullOrWhiteSpace(id) && !covered.Contains(id.Trim())) covered.Add(id.Trim());
            record.contentIds = covered.ToArray();
            if (string.IsNullOrWhiteSpace(record.contentId) && covered.Count > 0) record.contentId = covered[0];
            var approvedBy = json != null && json.HasField("approvedBy") ? json.GetField("approvedBy") : null;
            if (approvedBy != null && approvedBy.type == JSONObject.Type.Object) {
                record.approvedByUid   = AgentJsonString(approvedBy, "uid");
                record.approvedByEmail = AgentJsonString(approvedBy, "email");
            }
            if (string.IsNullOrEmpty(record.agentKey) || !record.agentKey.StartsWith(AgentKeyStore.KeyPrefix, StringComparison.Ordinal)) {
                error = "The server's response did not contain an agk_ agent key.";
                record = null;
                return false;
            }
            try {
                AgentKeyStore.Save(record);
            } catch (Exception e) {
                error = "Got an agent key, but it could not be saved to " + AgentKeyStore.RelativePath + ": " + e.Message;
                record = null;
                return false;
            }
            error = null;
            RaiseLoginStateChanged();
            return true;
        }

        // One poll of an approve-by-link request: POST /auth/agent/poll {requestId, pollToken}.
        //   202 {status:"pending"} → (true, response, null)
        //   200 {status:"approved", agentKey, ...same fields as verify} → the key is stored
        //       exactly as VerifyAgentKey stores it → (true, response, record). Minted ONCE.
        //   410 consumed|expired, 403 DENIED, 404 INVALID_REQUEST, 429 {retryAfterMs}, network
        //       → (false, response, null); the caller reads statusCode / code.
        public static void PollAgentRequest(string requestId, string pollToken,
            Action<bool, APIResponse, AgentKeyRecord> callback) {
            var pending = AgentKeyStore.FindPendingByRequestId(requestId);
            PollAgentRequest(requestId, pollToken, pending != null ? pending.contentId : null, callback);
        }

        // Same, for a request made for `contentId`: the stored entry covers that title.
        public static void PollAgentRequest(string requestId, string pollToken, string contentId,
            Action<bool, APIResponse, AgentKeyRecord> callback) {
            byte[] body = AgentJsonBody("requestId", requestId, "pollToken", pollToken);
            DreamParkAPI.POST("/auth/agent/poll", "", body, (success, response) => {
                if (!success) { callback?.Invoke(false, response, null); return; }
                var json = response != null ? response.json : null;
                if (!string.Equals(AgentJsonString(json, "status"), "approved", StringComparison.Ordinal)) {
                    callback?.Invoke(true, response, null);
                    return;
                }
                AgentKeyRecord record;
                string error;
                if (!TryStoreAgentKey(json, contentId, out record, out error)) {
                    if (response != null) response.error = error;
                    callback?.Invoke(false, response, null);
                    return;
                }
                callback?.Invoke(true, response, record);
            });
        }

        // Server-side view of the key in effect. Calls back (false, null) with no request when
        // there is no active (present + unexpired) key.
        public static void GetAgentIdentity(Action<bool, APIResponse> callback) {
            GetAgentIdentity(AgentKeyStore.ActiveRecord, callback);
        }

        // Server-side view of one stored key (dreampark_agent_status checks each entry).
        public static void GetAgentIdentity(AgentKeyRecord agent, Action<bool, APIResponse> callback) {
            if (agent == null || !agent.IsUsable) { callback?.Invoke(false, null); return; }
            DreamParkAPI.GET("/auth/agent/me", $"Bearer {agent.agentKey}", (success, response) => callback?.Invoke(success, response));
        }

        // Revokes EVERY stored agent key on the server (each with its own revoke call, never
        // /auth/logout), then deletes the local file — ALWAYS, even when a request fails (a
        // key the caller asked to forget must not stay usable from this machine). success is
        // true only when the server confirmed every revocation; response is the last answer.
        // A call with no stored key reports (true, null).
        public static void RevokeAgentKey(Action<bool, APIResponse> callback) {
            var all = AgentKeyStore.LoadAll(); // expired ones too: revoking is harmless
            RevokeEach(all, 0, true, null, (ok, response) => {
                try { AgentKeyStore.Delete(); }
                catch (Exception e) { Debug.LogWarning("[AuthAPI] Could not delete " + AgentKeyStore.RelativePath + ": " + e.Message); }
                RaiseLoginStateChanged();
                callback?.Invoke(ok, response);
            });
        }

        // Revokes and removes ONLY the entries approved for `contentId` (its beta twin
        // included); other titles' keys stay. The third argument lists what was removed.
        public static void RevokeAgentKeyFor(string contentId,
            Action<bool, APIResponse, System.Collections.Generic.List<AgentKeyRecord>> callback) {
            var targets = AgentKeyStore.FindAllFor(contentId);
            RevokeEach(targets, 0, true, null, (ok, response) => {
                RaiseLoginStateChanged();
                callback?.Invoke(ok, response, targets);
            });
        }

        // Sequential: revoke records[index], drop it locally whatever the answer, recurse.
        private static void RevokeEach(System.Collections.Generic.List<AgentKeyRecord> records, int index,
            bool allOk, APIResponse last, Action<bool, APIResponse> done) {
            if (records == null || index >= records.Count) { done(allOk, last); return; }
            var record = records[index];
            if (record == null || string.IsNullOrEmpty(record.agentKey)) {
                RevokeEach(records, index + 1, allOk, last, done);
                return;
            }
            DreamParkAPI.POST("/auth/agent/revoke", $"Bearer {record.agentKey}", AgentJsonBody(), (success, response) => {
                try { AgentKeyStore.RemoveKey(record.agentKey); }
                catch (Exception e) { Debug.LogWarning("[AuthAPI] Could not update " + AgentKeyStore.RelativePath + ": " + e.Message); }
                RevokeEach(records, index + 1, allOk && success && response != null, response, done);
            });
        }

        // Mints a single-use website sign-in link for the ACTIVE AGENT KEY's account. Agent
        // keys only — a human session is deliberately not used, so an agent can never turn
        // the owner's personal session into a browser login.
        public static void RequestAgentWebHandoff(string next, Action<bool, APIResponse> callback) {
            RequestAgentWebHandoff(AgentKeyStore.ActiveRecord, next, callback);
        }

        // Same, as one stored key's account (the key approved for the title being opened).
        public static void RequestAgentWebHandoff(AgentKeyRecord agent, string next, Action<bool, APIResponse> callback) {
            if (agent == null || !agent.IsUsable) { callback?.Invoke(false, null); return; }
            DreamParkAPI.POST("/auth/handoff", $"Bearer {agent.agentKey}", AgentJsonBody("next", next), (success, response) => callback?.Invoke(success, response));
        }

        // Key/value pairs → UTF-8 JSON object; null values are omitted. Hand-rolled
        // because Defective.JSON does not escape backslashes or control characters, and a
        // machine name or label is free text.
        private static byte[] AgentJsonBody(params string[] keyValues) {
            var sb = new System.Text.StringBuilder("{");
            bool first = true;
            for (int i = 0; i + 1 < keyValues.Length; i += 2) {
                if (keyValues[i + 1] == null) continue;
                if (!first) sb.Append(',');
                first = false;
                AppendJsonString(sb, keyValues[i]);
                sb.Append(':');
                AppendJsonString(sb, keyValues[i + 1]);
            }
            sb.Append('}');
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static void AppendJsonString(System.Text.StringBuilder sb, string value) {
            sb.Append('"');
            foreach (char c in value) {
                switch (c) {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string AgentJsonString(JSONObject json, string field) {
            if (json == null || !json.HasField(field)) return null;
            var node = json.GetField(field);
            if (node == null || node.type != JSONObject.Type.String) return null;
            return node.stringValue;
        }

        // Unix-ms from a Number (the contract) or, defensively, a numeric/ISO-8601 string.
        public static long AgentJsonMillis(JSONObject json, string field) {
            if (json == null || !json.HasField(field)) return 0;
            var node = json.GetField(field);
            if (node == null) return 0;
            if (node.type == JSONObject.Type.Number)
                return node.isInteger ? node.longValue : (long)node.doubleValue;
            if (node.type == JSONObject.Type.String && !string.IsNullOrEmpty(node.stringValue)) {
                long parsed;
                if (long.TryParse(node.stringValue, out parsed)) return parsed;
                DateTimeOffset when;
                if (DateTimeOffset.TryParse(node.stringValue, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out when))
                    return when.ToUnixTimeMilliseconds();
            }
            return 0;
        }

        public static string[] AgentJsonStrings(JSONObject json, string field) {
            if (json == null || !json.HasField(field)) return new string[0];
            var node = json.GetField(field);
            if (node == null || node.type != JSONObject.Type.Array || node.list == null) return new string[0];
            var result = new System.Collections.Generic.List<string>();
            foreach (var item in node.list)
                if (item != null && item.type == JSONObject.Type.String) result.Add(item.stringValue);
            return result.ToArray();
        }
#endif
    }

#if UNITY_EDITOR
    // One approved agent key. Library/DreamParkAgent/agent-key.json holds a SET of these
    // (AgentKeyFile), one per title approval: a project with several content folders
    // remembers every approval instead of each new one overwriting the last (owner,
    // 2026-10-09). JsonUtility-serialized (handles escaping and 64-bit expiresAt correctly,
    // and is safe off the main thread).
    [Serializable]
    public sealed class AgentKeyRecord
    {
        public string agentKey;
        public string keyId;
        public string uid;
        public string email;
        public string label;
        public long expiresAt; // Unix ms; 0 = unknown (the server still enforces expiry)
        public string[] scopes;
        public string contentId;       // the content the owner approved this agent for
        public string[] contentIds;    // every content ID this key covers: the approval's own
                                       // contentId plus any the server listed. "<id>Beta" twins
                                       // are implied (Covers), never stored.
        public string approvedByUid;   // the human who approved it
        public string approvedByEmail; // masked by the server (a***@…)
        public long storedAt;          // Unix ms this entry was stored here; the newest entry
                                       // is the fallback key

        // Masked keys of the entries this one replaced when it was stored. Not persisted.
        [NonSerialized] public string[] replacedKeys;

        // What to show where an email would go: agent identities have no email of their own.
        public string DisplayName => !string.IsNullOrEmpty(email) ? email
            : "agent (approved by " + (string.IsNullOrEmpty(approvedByEmail) ? "the owner" : approvedByEmail) + ")";

        public bool IsExpired => expiresAt > 0 && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Present, agk_-prefixed and not yet expired.
        public bool IsUsable => !string.IsNullOrEmpty(agentKey)
            && agentKey.StartsWith(AgentKeyStore.KeyPrefix, StringComparison.Ordinal)
            && !IsExpired;

        // contentId ∪ contentIds: non-empty, trimmed, distinct, approval's own ID first.
        public string[] CoveredContentIds
        {
            get
            {
                var result = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(contentId)) result.Add(contentId.Trim());
                if (contentIds != null)
                    foreach (string id in contentIds)
                        if (!string.IsNullOrWhiteSpace(id) && !result.Contains(id.Trim())) result.Add(id.Trim());
                return result.ToArray();
            }
        }

        // True when this key was approved for `id`, or for the title whose beta twin `id` is
        // ("LaserTagBeta" is covered by a LaserTag approval: the beta hangs off the title).
        // This only picks which stored key to SEND; the server decides what it may do.
        public bool Covers(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();
            foreach (string covered in CoveredContentIds)
            {
                if (string.Equals(covered, id, StringComparison.Ordinal)) return true;
                if (string.Equals(covered + AgentKeyStore.BetaSuffix, id, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    // Library/DreamParkAgent/agent-key.json since schema 2 (Oct 2026). Schema 1 (the file IS
    // one AgentKeyRecord) is still read, as a one-entry set, and rewritten as schema 2 on the
    // next change.
    [Serializable]
    public sealed class AgentKeyFile
    {
        public int schemaVersion; // set on write; 0 = a legacy single-record file
        public System.Collections.Generic.List<AgentKeyRecord> keys = new System.Collections.Generic.List<AgentKeyRecord>();
    }

    // One approval request between dreampark_agent_request and dreampark_agent_wait.
    // Library/DreamParkAgent/pending-request.json holds one per content ID (PendingAgentRequestFile).
    [Serializable]
    public sealed class PendingAgentRequest
    {
        public string requestId;
        public string pollToken; // akp_..., secret
        public string contentId;
        public string sentTo;    // masked by the server
        public long expiresAt;   // Unix ms; 0 = unknown
        public string approval;  // "email_link" when the owner approves by clicking a link
        public long requestedAt; // Unix ms this request was stored here

        public bool IsExpired => expiresAt > 0 && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // Library/DreamParkAgent/pending-request.json since schema 2: one pending request PER
    // content ID, so several titles can be approved in parallel. A legacy single-request file
    // is read as a one-entry set.
    [Serializable]
    public sealed class PendingAgentRequestFile
    {
        public int schemaVersion; // set on write; 0 = a legacy single-request file
        public System.Collections.Generic.List<PendingAgentRequest> requests = new System.Collections.Generic.List<PendingAgentRequest>();
    }

    // Project-local storage for agent keys. Deliberately NOT EditorPrefs: EditorPrefs is
    // machine-global (shared by every Unity project on the computer — see PrefSessionToken),
    // while an agent key is issued to ONE project's agent. Library/ is per-project and
    // gitignored, so a key can never be committed. Files are chmod 0600 (dir 0700) on
    // macOS/Linux and written atomically (temp file + rename); on Windows they inherit the
    // project folder's ACL.
    //
    // ONE KEY PER TITLE (Oct 2026). Each owner approval is for one content ID and yields a key
    // whose account is on that title's team only, so a project with several content folders
    // keeps one entry per approval. Which entry a request uses:
    //   1. an explicit content ID (AuthAPI.GetUserAuthFor(id): ContentAPI routes, badges), else
    //   2. the innermost open content scope (WithContent: every content-scoped CLI command), else
    //   3. nothing: the newest usable entry (the fallback; also what non-content calls use).
    // With an ID, the newest usable entry that Covers it wins (its "<id>Beta" twin included);
    // when none does, the newest usable entry is used.
    //
    // Never log a key: use Mask() for anything a human or a command result will see.
    public static class AgentKeyStore
    {
        public const string KeyPrefix = "agk_";
        public const string BetaSuffix = "Beta"; // BetaContentId.Suffix (Editor assembly)
        public const int SchemaVersion = 2;
        public const string RelativePath = "Library/DreamParkAgent/agent-key.json";

        private static readonly object Gate = new object();
        private static string _projectRoot;
        private static System.Collections.Generic.List<AgentKeyRecord> _cached;
        private static long _cachedStampTicks;
        private static long _cachedLength = -1;
        private static bool _cacheValid;

        // Application.dataPath is main-thread-only; resolve it once at load so a call from
        // a worker thread (e.g. GetUserAuth inside a background upload step) still works.
        [UnityEditor.InitializeOnLoadMethod]
        private static void ResolveProjectRootOnLoad()
        {
            var unused = ProjectRoot;
        }

        public static string ProjectRoot
        {
            get
            {
                if (_projectRoot == null)
                {
                    try { _projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")); }
                    catch (Exception) { _projectRoot = System.IO.Directory.GetCurrentDirectory(); }
                }
                return _projectRoot;
            }
        }

        // Tests only: point the store at another folder (a temp dir) until disposed. The
        // project's real Library/DreamParkAgent is never touched while it is in effect.
        public static IDisposable UseProjectRootForTests(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentNullException(nameof(root));
            lock (Gate)
            {
                var restore = new RootOverride(ProjectRoot);
                _projectRoot = System.IO.Path.GetFullPath(root);
                InvalidateUnlocked();
                return restore;
            }
        }

        private sealed class RootOverride : IDisposable
        {
            private readonly string _previous;
            private bool _disposed;
            public RootOverride(string previous) { _previous = previous; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (Gate)
                {
                    _projectRoot = _previous;
                    InvalidateUnlocked();
                }
            }
        }

        public static string FilePath => System.IO.Path.Combine(ProjectRoot, "Library", "DreamParkAgent", "agent-key.json");

        // ── Reading ──

        // Every stored entry, expired or not, NEWEST FIRST (storedAt). Empty when the file is
        // absent or unreadable. Cached against the file's mtime + length, so per-repaint
        // callers (isLoggedIn) cost one stat().
        public static System.Collections.Generic.List<AgentKeyRecord> LoadAll()
        {
            lock (Gate)
            {
                return new System.Collections.Generic.List<AgentKeyRecord>(ReadUnlocked());
            }
        }

        // The newest stored entry, expired or not, or null when there is none.
        public static AgentKeyRecord Load()
        {
            lock (Gate)
            {
                var all = ReadUnlocked();
                return all.Count > 0 ? all[0] : null;
            }
        }

        // The newest USABLE entry approved for `contentId` (or the title whose beta twin it
        // is), or null. No fallback.
        public static AgentKeyRecord FindFor(string contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId)) return null;
            lock (Gate)
            {
                foreach (var record in ReadUnlocked())
                    if (record.IsUsable && record.Covers(contentId)) return record;
                return null;
            }
        }

        // Every stored entry covering `contentId`, expired or not, newest first.
        public static System.Collections.Generic.List<AgentKeyRecord> FindAllFor(string contentId)
        {
            var result = new System.Collections.Generic.List<AgentKeyRecord>();
            if (string.IsNullOrWhiteSpace(contentId)) return result;
            lock (Gate)
            {
                foreach (var record in ReadUnlocked())
                    if (record.Covers(contentId)) result.Add(record);
            }
            return result;
        }

        // The newest usable entry of all, or null.
        public static AgentKeyRecord Newest
        {
            get
            {
                lock (Gate)
                {
                    foreach (var record in ReadUnlocked())
                        if (record.IsUsable) return record;
                    return null;
                }
            }
        }

        // The key a request about `contentId` sends: the entry approved for it, else (none
        // matches, or no ID) the newest usable entry. Null only when no usable key is stored.
        public static AgentKeyRecord SelectFor(string contentId)
        {
            return FindFor(contentId) ?? Newest;
        }

        // The key in effect right now: SelectFor(the innermost open content scope).
        public static AgentKeyRecord ActiveRecord => SelectFor(CurrentContentScope);

        public static bool IsActive => ActiveRecord != null;

        // The content IDs some stored entry covers, for messages ("this project has keys for …").
        public static string[] CoveredContentIds()
        {
            var result = new System.Collections.Generic.List<string>();
            foreach (var record in LoadAll())
                foreach (string id in record.CoveredContentIds)
                    if (!result.Contains(id)) result.Add(id);
            return result.ToArray();
        }

        // ── Content scope ──
        // Content-scoped commands (preflight, publish, inspect, web login for a title) open a
        // scope for their whole run so every request inside them, including ones started from
        // EditorCoroutines and worker threads, picks that title's key. Not a stack: disposing
        // a scope removes exactly that scope, so overlapping async commands (a publish running
        // in the background while an inspect comes and goes) cannot restore the wrong title.
        private static readonly System.Collections.Generic.List<ContentScope> Scopes = new System.Collections.Generic.List<ContentScope>();

        public static string CurrentContentScope
        {
            get
            {
                lock (Gate) return Scopes.Count == 0 ? null : Scopes[Scopes.Count - 1].ContentId;
            }
        }

        // A null/blank content ID is a no-op scope, so callers can always wrap unconditionally.
        public static IDisposable WithContent(string contentId)
        {
            var scope = new ContentScope(string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim());
            if (scope.ContentId != null)
                lock (Gate) Scopes.Add(scope);
            return scope;
        }

        private sealed class ContentScope : IDisposable
        {
            public readonly string ContentId;
            private bool _disposed;
            public ContentScope(string contentId) { ContentId = contentId; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (ContentId != null)
                    lock (Gate) Scopes.Remove(this);
            }
        }

        // ── Writing ──

        // Stores `record` as its own entry and returns the entries it REPLACED: an earlier
        // approval for any of the same content IDs, or the same key stored again. Entries for
        // other titles are kept. An older entry that also covers other titles keeps those and
        // loses only the overlap. The replaced keys are NOT revoked here (no network); their
        // masks are left on record.replacedKeys for the caller to report.
        public static System.Collections.Generic.List<AgentKeyRecord> Save(AgentKeyRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            lock (Gate)
            {
                var existing = ReadUnlocked();
                Normalize(record);
                long newest = 0;
                foreach (var e in existing) newest = Math.Max(newest, e.storedAt);
                // Strictly newer than every stored entry, so "newest" is never a tie.
                record.storedAt = Math.Max(Math.Max(record.storedAt, NowMs()), newest + 1);

                string[] newIds = record.CoveredContentIds;
                var replaced = new System.Collections.Generic.List<AgentKeyRecord>();
                var kept = new System.Collections.Generic.List<AgentKeyRecord>();
                foreach (var e in existing)
                {
                    bool sameKey = string.Equals(e.agentKey, record.agentKey, StringComparison.Ordinal)
                        || (!string.IsNullOrEmpty(e.keyId) && string.Equals(e.keyId, record.keyId, StringComparison.Ordinal));
                    if (sameKey) { replaced.Add(e); continue; }
                    string[] ids = e.CoveredContentIds;
                    if (ids.Length > 0 && newIds.Length > 0)
                    {
                        var remaining = new System.Collections.Generic.List<string>();
                        foreach (string id in ids)
                            if (Array.IndexOf(newIds, id) < 0) remaining.Add(id);
                        if (remaining.Count == 0) { replaced.Add(e); continue; }
                        if (remaining.Count < ids.Length)
                        {
                            e.contentId = remaining.Contains(e.contentId) ? e.contentId : remaining[0];
                            e.contentIds = remaining.ToArray();
                        }
                    }
                    kept.Add(e);
                }
                kept.Add(record);
                WriteAllUnlocked(kept);

                var masks = new System.Collections.Generic.List<string>();
                foreach (var r in replaced)
                    if (!string.Equals(r.agentKey, record.agentKey, StringComparison.Ordinal)) masks.Add(Mask(r.agentKey));
                record.replacedKeys = masks.ToArray();
                return replaced;
            }
        }

        // Removes every entry covering `contentId` (its beta twin included), expired or not.
        // Returns the removed entries. Other titles' entries are kept.
        public static System.Collections.Generic.List<AgentKeyRecord> RemoveFor(string contentId)
        {
            var removed = new System.Collections.Generic.List<AgentKeyRecord>();
            if (string.IsNullOrWhiteSpace(contentId)) return removed;
            lock (Gate)
            {
                var kept = new System.Collections.Generic.List<AgentKeyRecord>();
                foreach (var e in ReadUnlocked())
                    (e.Covers(contentId) ? removed : kept).Add(e);
                if (removed.Count > 0) WriteAllUnlocked(kept);
            }
            return removed;
        }

        // Removes the entry holding exactly this key (e.g. after the server rejected it).
        public static bool RemoveKey(string agentKey)
        {
            if (string.IsNullOrEmpty(agentKey)) return false;
            lock (Gate)
            {
                var kept = new System.Collections.Generic.List<AgentKeyRecord>();
                bool found = false;
                foreach (var e in ReadUnlocked())
                {
                    if (string.Equals(e.agentKey, agentKey, StringComparison.Ordinal)) { found = true; continue; }
                    kept.Add(e);
                }
                if (found) WriteAllUnlocked(kept);
                return found;
            }
        }

        // Removes the whole file (every entry). True when a file was removed.
        public static bool Delete()
        {
            lock (Gate)
            {
                InvalidateUnlocked();
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return false;
                System.IO.File.Delete(path);
                return true;
            }
        }

        // ── Pending approval requests (approve-by-link) ──
        // dreampark_agent_request adds one per content ID; dreampark_agent_wait polls them and
        // removes each as it resolves. All wait state lives in this file, so an Editor domain
        // reload mid-wait loses nothing: run dreampark_agent_wait again. A pollToken is a
        // secret (it can collect the key once approved): owner-only file, never printed.
        public const string PendingRelativePath = "Library/DreamParkAgent/pending-request.json";

        public static string PendingFilePath => System.IO.Path.Combine(ProjectRoot, "Library", "DreamParkAgent", "pending-request.json");

        // Adds `pending`, replacing an earlier pending request for the SAME content ID only.
        public static void SavePending(PendingAgentRequest pending)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            lock (Gate)
            {
                var all = ReadPendingUnlocked();
                long newest = 0;
                foreach (var p in all) newest = Math.Max(newest, p.requestedAt);
                pending.requestedAt = Math.Max(Math.Max(pending.requestedAt, NowMs()), newest + 1);
                all.RemoveAll(p => SameContent(p.contentId, pending.contentId)
                    || string.Equals(p.requestId, pending.requestId, StringComparison.Ordinal));
                all.Add(pending);
                WritePendingUnlocked(all);
            }
        }

        // Every pending request, expired or not, newest first. Empty when there are none.
        public static System.Collections.Generic.List<PendingAgentRequest> LoadAllPending()
        {
            lock (Gate) return ReadPendingUnlocked();
        }

        // The newest pending request, or null.
        public static PendingAgentRequest LoadPending()
        {
            var all = LoadAllPending();
            return all.Count > 0 ? all[0] : null;
        }

        // The pending request for `contentId`, or null.
        public static PendingAgentRequest LoadPending(string contentId)
        {
            foreach (var p in LoadAllPending())
                if (SameContent(p.contentId, contentId)) return p;
            return null;
        }

        public static PendingAgentRequest FindPendingByRequestId(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return null;
            foreach (var p in LoadAllPending())
                if (string.Equals(p.requestId, requestId, StringComparison.Ordinal)) return p;
            return null;
        }

        // Removes the pending request for `contentId`. True when one was removed.
        public static bool DeletePending(string contentId)
        {
            lock (Gate)
            {
                var all = ReadPendingUnlocked();
                int removed = all.RemoveAll(p => SameContent(p.contentId, contentId));
                if (removed > 0) WritePendingUnlocked(all);
                return removed > 0;
            }
        }

        // Removes the pending request with this requestId. True when one was removed.
        public static bool DeletePendingRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return false;
            lock (Gate)
            {
                var all = ReadPendingUnlocked();
                int removed = all.RemoveAll(p => string.Equals(p.requestId, requestId, StringComparison.Ordinal));
                if (removed > 0) WritePendingUnlocked(all);
                return removed > 0;
            }
        }

        // Removes every pending request. True when a file was removed.
        public static bool DeletePending()
        {
            lock (Gate)
            {
                string path = PendingFilePath;
                if (!System.IO.File.Exists(path)) return false;
                System.IO.File.Delete(path);
                return true;
            }
        }

        // "agk_…wxyz" — enough to tell keys apart, never enough to use one.
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string last4 = key.Length > KeyPrefix.Length + 4 ? key.Substring(key.Length - 4) : "";
            return KeyPrefix + "…" + last4;
        }

        // ── internals (callers hold Gate) ──

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static bool SameContent(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

        private static void InvalidateUnlocked()
        {
            _cacheValid = false;
            _cached = null;
            _cachedLength = -1;
        }

        private static void Normalize(AgentKeyRecord record)
        {
            string[] ids = record.CoveredContentIds;
            record.contentIds = ids;
            if (string.IsNullOrWhiteSpace(record.contentId) && ids.Length > 0) record.contentId = ids[0];
            else if (record.contentId != null) record.contentId = record.contentId.Trim();
        }

        private static System.Collections.Generic.List<AgentKeyRecord> ReadUnlocked()
        {
            string path = FilePath;
            var info = new System.IO.FileInfo(path);
            if (!info.Exists)
            {
                _cached = new System.Collections.Generic.List<AgentKeyRecord>();
                _cacheValid = false;
                return _cached;
            }
            long stamp = info.LastWriteTimeUtc.Ticks;
            if (_cacheValid && _cached != null && stamp == _cachedStampTicks && info.Length == _cachedLength)
                return _cached;

            System.Collections.Generic.List<AgentKeyRecord> records;
            try
            {
                long stampMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                records = ParseKeyFile(System.IO.File.ReadAllText(path), stampMs);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AuthAPI] Ignoring unreadable " + RelativePath + ": " + e.Message);
                records = new System.Collections.Generic.List<AgentKeyRecord>();
            }
            _cached = records;
            _cachedStampTicks = stamp;
            _cachedLength = info.Length;
            _cacheValid = true;
            return records;
        }

        // Schema 2 ({schemaVersion, keys[]}) or the legacy single record; newest first.
        // Public for tests: never logs or returns anything but the parsed records.
        public static System.Collections.Generic.List<AgentKeyRecord> ParseKeyFile(string json, long legacyStoredAt)
        {
            var records = new System.Collections.Generic.List<AgentKeyRecord>();
            if (string.IsNullOrWhiteSpace(json)) return records;
            var file = JsonUtility.FromJson<AgentKeyFile>(json);
            if (file != null && file.schemaVersion >= 2)
            {
                if (file.keys != null)
                    foreach (var record in file.keys)
                        if (record != null && !string.IsNullOrEmpty(record.agentKey))
                        {
                            Normalize(record);
                            records.Add(record);
                        }
            }
            else
            {
                // Legacy (before Oct 9 2026): the file IS one record. Migrated in memory;
                // written back as schema 2 by the next change.
                var legacy = JsonUtility.FromJson<AgentKeyRecord>(json);
                if (legacy != null && !string.IsNullOrEmpty(legacy.agentKey))
                {
                    if (legacy.storedAt <= 0) legacy.storedAt = legacyStoredAt;
                    Normalize(legacy);
                    records.Add(legacy);
                }
            }
            // Newest first; a stable sort keeps file order among equal stamps.
            var ordered = new System.Collections.Generic.List<AgentKeyRecord>();
            foreach (var record in records)
            {
                int at = 0;
                while (at < ordered.Count && ordered[at].storedAt >= record.storedAt) at++;
                ordered.Insert(at, record);
            }
            return ordered;
        }

        private static void WriteAllUnlocked(System.Collections.Generic.List<AgentKeyRecord> records)
        {
            InvalidateUnlocked();
            string path = FilePath;
            if (records.Count == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            // Oldest first on disk (append order); readers sort.
            var ordered = new System.Collections.Generic.List<AgentKeyRecord>(records);
            ordered.Sort((a, b) => a.storedAt.CompareTo(b.storedAt));
            var file = new AgentKeyFile { schemaVersion = SchemaVersion, keys = ordered };
            WriteOwnerOnlyAtomic(path, JsonUtility.ToJson(file, true));
            InvalidateUnlocked();
        }

        private static System.Collections.Generic.List<PendingAgentRequest> ReadPendingUnlocked()
        {
            var result = new System.Collections.Generic.List<PendingAgentRequest>();
            string path = PendingFilePath;
            if (!System.IO.File.Exists(path)) return result;
            try
            {
                string json = System.IO.File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return result;
                var file = JsonUtility.FromJson<PendingAgentRequestFile>(json);
                if (file != null && file.schemaVersion >= 2)
                {
                    if (file.requests != null)
                        foreach (var p in file.requests)
                            if (p != null && !string.IsNullOrEmpty(p.requestId)) result.Add(p);
                }
                else
                {
                    // Legacy single-request file.
                    var legacy = JsonUtility.FromJson<PendingAgentRequest>(json);
                    if (legacy != null && !string.IsNullOrEmpty(legacy.requestId)) result.Add(legacy);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AuthAPI] Ignoring unreadable " + PendingRelativePath + ": " + e.Message);
                return new System.Collections.Generic.List<PendingAgentRequest>();
            }
            result.Sort((a, b) => b.requestedAt.CompareTo(a.requestedAt));
            return result;
        }

        private static void WritePendingUnlocked(System.Collections.Generic.List<PendingAgentRequest> all)
        {
            string path = PendingFilePath;
            if (all.Count == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            var ordered = new System.Collections.Generic.List<PendingAgentRequest>(all);
            ordered.Sort((a, b) => a.requestedAt.CompareTo(b.requestedAt));
            var file = new PendingAgentRequestFile { schemaVersion = SchemaVersion, requests = ordered };
            WriteOwnerOnlyAtomic(path, JsonUtility.ToJson(file, true));
        }

        // Atomic and owner-only: the secret goes into a temp file in the same folder that is
        // created empty and restricted BEFORE anything is written, then renamed over the
        // target, so a reader sees the old file or the new one and never a torn write.
        private static void WriteOwnerOnlyAtomic(string path, string contents)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            System.IO.Directory.CreateDirectory(dir);
            RestrictToOwner(dir, 0x1C0); // 0700
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (System.IO.File.Create(temp)) { }
                RestrictToOwner(temp, 0x180); // 0600
                System.IO.File.WriteAllText(temp, contents, new System.Text.UTF8Encoding(false));
                if (System.IO.File.Exists(path))
                {
                    try { System.IO.File.Replace(temp, path, null); }
                    catch (Exception e) when (e is PlatformNotSupportedException || e is NotSupportedException)
                    {
                        // No atomic replace on this platform: copy over (still owner-only).
                        System.IO.File.Copy(temp, path, true);
                    }
                }
                else
                {
                    System.IO.File.Move(temp, path);
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
                catch (Exception) { }
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "chmod")]
        private static extern int chmod(string pathname, uint mode);

        private static void RestrictToOwner(string path, uint mode)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) return;
            try
            {
                if (chmod(path, mode) != 0)
                    Debug.LogWarning("[AuthAPI] chmod failed on " + path + " (errno " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AuthAPI] Could not restrict permissions on " + path + ": " + e.Message);
            }
        }
    }
#endif
}
