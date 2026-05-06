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

        // Internal — used by AuthAPI to notify subscribers. Wrapped so callers
        // outside AuthAPI can't fire the event.
        private static void RaiseLoginStateChanged()
        {
            try { LoginStateChanged?.Invoke(isLoggedIn); }
            catch (Exception e) { Debug.LogWarning($"[AuthAPI] LoginStateChanged subscriber threw: {e}"); }
        }

        public static string GetUserAuth() {
#if UNITY_EDITOR
            var sessionToken = UnityEditor.EditorPrefs.GetString("sessionToken", "");
#else
            var sessionToken = PlayerPrefs.GetString("sessionToken", "");
#endif
            return $"Bearer {sessionToken}";
        }

        // Static "ApiKey <secret>" header used by core's runtime to call /app/* endpoints.
        // The secret value lives ONLY in dreampark-core (Assets/Scripts/Internal/CoreSecrets.cs)
        // and is excluded from the public SDK distribution via #if DREAMPARKCORE.
        //
        // SDK callers should use GetUserAuth() (session bearer) instead — anything in the
        // public SDK that legitimately needs to hit the backend goes through user auth.
        public static string GetAPIKey() {
#if DREAMPARKCORE
            return $"ApiKey {CoreSecrets.ApiKey}";
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
                var sessionToken = PlayerPrefs.GetString("sessionToken", "");
#endif
                return !string.IsNullOrEmpty(sessionToken);
            }
        }
        public static string userId {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("userId", "");
#else
                return PlayerPrefs.GetString("userId", "");
#endif            
            }
        }
        public static string sessionToken {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("sessionToken", "");
#else
                return PlayerPrefs.GetString("sessionToken", "");
#endif
            }
        }
        // Cached email from the most recent login response. Used purely for display.
        public static string email {
            get {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetString("userEmail", "");
#else
                return PlayerPrefs.GetString("userEmail", "");
#endif
            }
        }
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
                    PlayerPrefs.SetString("sessionToken", response.json.GetField("session").stringValue);
                    PlayerPrefs.SetString("userId", response.json.GetField("uid").stringValue);
                    PlayerPrefs.SetString("userEmail", canonicalEmail ?? "");
                    PlayerPrefs.Save();
#endif
                    RaiseLoginStateChanged();
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
            PlayerPrefs.DeleteKey("sessionToken");
            PlayerPrefs.DeleteKey("userId");
            PlayerPrefs.DeleteKey("userEmail");
            PlayerPrefs.Save();
#endif
            RaiseLoginStateChanged();
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
                    PlayerPrefs.DeleteKey("sessionToken");
                    PlayerPrefs.DeleteKey("userId");
                    PlayerPrefs.DeleteKey("userEmail");
                    PlayerPrefs.Save();
#endif
                RaiseLoginStateChanged();
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