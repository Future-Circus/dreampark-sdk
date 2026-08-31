#if UNITY_EDITOR
using System;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // DreamPark > Profile menu — quick triggers for exercising the
    // ProfileAPI surface in editor against your own logged-in account
    // without needing to scan a QR code on a real headset.
    //
    // BindToLoggedInUser goes through the REAL pairing-token pipeline
    // (POST /api/pairing/create → POST /api/pairing/preview-claim) so
    // any bugs in token mechanics surface in editor too.
    internal static class ProfileAPITestMenu
    {
        private const string BindPath        = "DreamPark/Profile/Bind to Logged-In User";
        private const string ClearPath       = "DreamPark/Profile/Clear Identity";
        private const string DumpPath        = "DreamPark/Profile/Dump Cache to Console";
        private const string AwardTestItem   = "DreamPark/Profile/Test Award Item (sdk_test_item)";
        private const string AwardTestBadge  = "DreamPark/Profile/Test Award Badge (sdk_test_badge)";

        [MenuItem(BindPath, false, 100)]
        private static void Bind()
        {
            if (!AuthAPI.isLoggedIn)
            {
                EditorUtility.DisplayDialog(
                    "Not signed in",
                    "Use DreamPark > Sign In first, then try again.",
                    "OK");
                return;
            }
            Debug.Log("[ProfileAPI/Test] Binding to logged-in user via pairing-token flow…");
            ProfileAPI.BindToLoggedInUser();
        }

        [MenuItem(BindPath, true, 100)]
        private static bool ValidateBind() => AuthAPI.isLoggedIn;

        [MenuItem(ClearPath, false, 101)]
        private static void Clear()
        {
            ProfileAPI.ClearIdentity();
            Debug.Log("[ProfileAPI/Test] Identity cleared.");
        }

        [MenuItem(DumpPath, false, 102)]
        private static void Dump()
        {
            Debug.Log($"[ProfileAPI/Test] Source: {ProfileAPI.Source}");
            Debug.Log($"[ProfileAPI/Test] IsBound: {ProfileAPI.IsBound}  IsLoaded: {ProfileAPI.IsLoaded}");
            Debug.Log($"[ProfileAPI/Test] Identity: {ProfileAPI.IdentitySegment() ?? "<none>"}");
            Debug.Log($"[ProfileAPI/Test] Items ({ProfileAPI.Items.Count}):");
            foreach (var it in ProfileAPI.Items)
                Debug.Log($"  - {it.itemId}  name='{it.name}'  type='{it.type}'  x{it.amount}");
            Debug.Log($"[ProfileAPI/Test] Achievements ({ProfileAPI.Achievements.Count}):");
            foreach (var a in ProfileAPI.Achievements)
                Debug.Log($"  - {a.achievementId}  progress={a.progress}  completed={a.completed}");
            Debug.Log($"[ProfileAPI/Test] Badges ({ProfileAPI.Badges.Count}):");
            foreach (var b in ProfileAPI.Badges)
                Debug.Log($"  - {b.badgeId}  name='{b.name}'");
        }

        [MenuItem(AwardTestItem, false, 200)]
        private static void AwardItem()
        {
            if (!ProfileAPI.IsBound)
            {
                EditorUtility.DisplayDialog("Not bound", "Bind to logged-in user first.", "OK");
                return;
            }
            ProfileAPI.AwardItem("sdk_test_item", 1, null, (ok, item) =>
            {
                if (ok) Debug.Log($"[ProfileAPI/Test] Awarded sdk_test_item — now have x{item?.amount}");
                else    Debug.LogWarning("[ProfileAPI/Test] Award failed");
            });
        }

        [MenuItem(AwardTestItem, true, 200)]
        private static bool ValidateAwardItem() => ProfileAPI.IsBound;

        [MenuItem(AwardTestBadge, false, 201)]
        private static void AwardBadge()
        {
            if (!ProfileAPI.IsBound)
            {
                EditorUtility.DisplayDialog("Not bound", "Bind to logged-in user first.", "OK");
                return;
            }
            ProfileAPI.AwardBadge("sdk_test_badge", (ok, badge) =>
            {
                if (ok) Debug.Log($"[ProfileAPI/Test] Awarded sdk_test_badge — name='{badge?.name}'");
                else    Debug.LogWarning("[ProfileAPI/Test] Badge award failed");
            });
        }

        [MenuItem(AwardTestBadge, true, 201)]
        private static bool ValidateAwardBadge() => ProfileAPI.IsBound;
    }
}
#endif
