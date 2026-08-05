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

        public static string GetUserAuth() {
#if UNITY_EDITOR
            var sessionToken = GetAuthPref(PrefSessionToken, LegacySessionToken);
#else
            var sessionToken = _sessionToken;
#endif
            return $"Bearer {sessionToken}";
        }

        // "ApiKey <key>" header used by core's runtime to call /app/* endpoints.
        //
        // DEVICE builds: returns the per-device hsk_* key issued by
        // POST /app/device/enroll (see Assets/Scripts/Internal/DeviceKeyManager.cs).
        // Public APKs ship with NO shared secret — a leaked key burns one device,
        // not the fleet, and the backend can revoke/rate-limit per device.
        // Boot.cs runs DeviceKeyManager.EnsureEnrolledAsync() before the first
        // authenticated call.
        //
        // EDITOR: still uses the legacy shared key (Assets/Scripts/Internal/
        // CoreSecrets.cs) for core-team tooling convenience. Editor code is never
        // compiled into players, so the secret stays out of shipped binaries; the
        // backend keeps accepting it for editor + existing operator builds.
        //
        // SDK callers use GetUserAuth() (session bearer) instead — anything in the
        // public SDK that legitimately needs to hit the backend goes through user auth.
        public static string GetAPIKey() {
#if DREAMPARKCORE
#if UNITY_EDITOR
            return $"ApiKey {CoreSecrets.ApiKey}";
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
#if UNITY_EDITOR
                var sessionToken = GetAuthPref(PrefSessionToken, LegacySessionToken);
#else
                var sessionToken = _sessionToken;
#endif
                return !string.IsNullOrEmpty(sessionToken);
            }
        }
        public static string userId {
            get {
#if UNITY_EDITOR
                return GetAuthPref(PrefUserId, LegacyUserId);
#else
                return _userId;
#endif
            }
        }
        public static string sessionToken {
            get {
#if UNITY_EDITOR
                return GetAuthPref(PrefSessionToken, LegacySessionToken);
#else
                return _sessionToken;
#endif
            }
        }
        // Cached email from the most recent login response. Used purely for display.
        public static string email {
            get {
#if UNITY_EDITOR
                return GetAuthPref(PrefUserEmail, LegacyUserEmail);
#else
                return _userEmail;
#endif
            }
        }
        // Cached display name (falls back to "" when unset — callers should use email
        // as the visible label when this is empty). Hydrated via HydrateProfile().
        public static string displayName {
            get {
#if UNITY_EDITOR
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
#if UNITY_EDITOR
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
            JSONObject body = new JSONObject();
            body.AddField("session", sessionToken);
            DreamParkAPI.POST("/auth/logout", AuthAPI.GetUserAuth(), body, (success, response) => {
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
            var token = sessionToken;
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
    }
}
