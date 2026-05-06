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
            bool confirm = EditorUtility.DisplayDialog(
                "Sign out of DreamPark?",
                $"You are signed in as {who}. Sign out?",
                "Sign Out",
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
