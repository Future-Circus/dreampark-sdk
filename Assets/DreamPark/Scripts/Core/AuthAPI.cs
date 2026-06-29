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
            if (dn != null) UnityEditor.EditorPrefs.SetString("displayName", dn);
            if (av != null) UnityEditor.EditorPrefs.SetString("avatarUrl", av);
#else
            if (dn != null) _displayName = dn;
            if (av != null) _avatarUrl   = av;
#endif
            RaiseProfileChanged();
        }

        private static void ClearProfileFields()
        {
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.DeleteKey("displayName");
            UnityEditor.EditorPrefs.DeleteKey("avatarUrl");
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
            var sessionToken = UnityEditor.EditorPrefs.GetString("sessionToken", "");
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
                var sessionToken = UnityEditor.EditorPrefs.GetString("sessionToken", "");
#else
                var sessionToken = _sessionToken;
#endif
                return !string.IsNullOrEmpty(sessionToken);
            }
        }
        public static string userId {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("userId", "");
#else
                return _userId;
#endif
            }
        }
        public static string sessionToken {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("sessionToken", "");
#else
                return _sessionToken;
#endif
            }
        }
        // Cached email from the most recent login response. Used purely for display.
        public static string email {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("userEmail", "");
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
                return UnityEditor.EditorPrefs.GetString("displayName", "");
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
                return UnityEditor.EditorPrefs.GetString("avatarUrl", "");
#else
                return _avatarUrl;
#endif
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
            UnityEditor.EditorPrefs.SetString("sessionToken", token);
            if (uid != null) UnityEditor.EditorPrefs.SetString("userId", uid);
            if (userEmail != null) UnityEditor.EditorPrefs.SetString("userEmail", userEmail);
#else
            _sessionToken = token;
            if (uid != null) _userId = uid;
            if (userEmail != null) _userEmail = userEmail;
#endif
            RaiseLoginStateChanged();
            // Native handoff only carries email — pull displayName + avatar from the
            // backend now that the session bearer is set.
            HydrateProfile();
        }
#endif

        public static void Login(string email, string password, Action<bool, APIResponse> callback) {
            var body = new JSONObject(JSONObject.Type.Object);
            body.AddField("email", email);
            body.AddField("password", password);
            DreamParkAPI.POST("/auth/login", "", body, (success, response) => {
                if (success) {
                    string canonicalEmail = response.json.HasField("email")
                        ? response.json.GetField("email").stringValue
                        : email;
#if UNITY_EDITOR
                    UnityEditor.EditorPrefs.SetString("sessionToken", response.json.GetField("session").stringValue);
                    UnityEditor.EditorPrefs.SetString("userId", response.json.GetField("uid").stringValue);
                    UnityEditor.EditorPrefs.SetString("userEmail", canonicalEmail ?? "");
#else
                    _sessionToken = response.json.GetField("session").stringValue;
                    _userId       = response.json.GetField("uid").stringValue;
                    _userEmail    = canonicalEmail ?? "";
#endif
                    RaiseLoginStateChanged();
                    // /auth/login already returns a sanitized `user` (displayName +
                    // photoURL) — apply it directly, no extra round trip needed.
                    if (response.json != null && response.json.HasField("user")) {
                        ApplyProfileFields(response.json.GetField("user"));
                    }
                    callback?.Invoke(success, response);
                } else {
                    callback?.Invoke(success, response);
                }
            });
        }

        // Local-only logout: clears stored credentials and notifies subscribers without
        // hitting the network. Used by Refresh() when the server reports the session
        // is invalid (avoids a redundant /auth/logout call on a token that's already dead).
        private static void ClearLocalSession()
        {
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.DeleteKey("sessionToken");
            UnityEditor.EditorPrefs.DeleteKey("userId");
            UnityEditor.EditorPrefs.DeleteKey("userEmail");
#else
            _sessionToken = "";
            _userId       = "";
            _userEmail    = "";
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
                    UnityEditor.EditorPrefs.DeleteKey("sessionToken");
                    UnityEditor.EditorPrefs.DeleteKey("userId");
                    UnityEditor.EditorPrefs.DeleteKey("userEmail");
#else
                    _sessionToken = "";
                    _userId       = "";
                    _userEmail    = "";
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
