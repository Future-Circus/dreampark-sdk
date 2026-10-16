#if UNITY_EDITOR
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Top-of-menu Sign In / Sign Out entries under DreamPark/. Both items are
    // always visible — Unity disables (greys out) the inactive one based on
    // AuthAPI.isLoggedIn, so users always see exactly one actionable option.
    //
    // Priority -20 / -19 puts these above Content Uploader (priority 0) and
    // — because the gap is > 10 — Unity draws a separator between them.
    internal static class AuthMenuItems
    {
        private const string SignInPath  = "DreamPark/Sign In";
        private const string SignOutPath = "DreamPark/Sign Out";

        [MenuItem(SignInPath, false, -20)]
        private static void SignIn()
        {
            AuthPopup.Show();
        }

        [MenuItem(SignInPath, true, -20)]
        private static bool ValidateSignIn()
        {
            return !AuthAPI.isLoggedIn;
        }

        [MenuItem(SignOutPath, false, -19)]
        private static void SignOut()
        {
            string who = string.IsNullOrEmpty(AuthAPI.email) ? "this account" : AuthAPI.email;
            // While agent keys are stored, Sign Out revokes the project's agent keys (one per
            // approved title; AuthAPI.Logout never sends an agent key to /auth/logout); the
            // human session is untouched.
            bool agent = AuthAPI.IsAgentKeyActive;
            string titles = string.Join(", ", AgentKeyStore.CoveredContentIds());
            bool confirm = EditorUtility.DisplayDialog(
                agent ? "Sign out the DreamPark agent?" : "Sign out of DreamPark?",
                agent
                    ? $"This project is signed in with agent keys{(titles.Length > 0 ? " for " + titles : "")}. Revoke every agent key and remove them from this project? Your own sign-in, if any, is not affected. (To sign out of one title only: dreampark_agent_signout --content_id <id>.)"
                    : $"You are signed in as {who}. Sign out?",
                agent ? "Revoke Agent Keys" : "Sign Out",
                "Cancel");
            if (!confirm) return;

            AuthAPI.Logout((success, response) =>
            {
                if (success)
                {
                    Debug.Log("[DreamPark] Signed out.");
                }
                else
                {
                    // Logout clears local session even on network failure, so the
                    // UI still flips to logged-out — but warn the user the server
                    // didn't acknowledge it.
                    string err = response?.error ?? "unknown error";
                    Debug.LogWarning($"[DreamPark] Sign-out request failed ({err}). Local session was cleared anyway.");
                }
            });
        }

        [MenuItem(SignOutPath, true, -19)]
        private static bool ValidateSignOut()
        {
            return AuthAPI.isLoggedIn;
        }
    }
}
#endif
