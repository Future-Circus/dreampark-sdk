#if UNITY_EDITOR
using System.IO;
using System.Linq;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Editor entry point for testing the screenshot capture + upload flow
    /// end-to-end without needing a paired headset. Requires Play mode
    /// because ScreenshotAPI uses WaitForEndOfFrame + Camera.main.Render(),
    /// neither of which behaves usefully in edit mode.
    ///
    /// Auth re-uses the SDK's stored session token (EditorPrefs "sessionToken").
    /// SessionContext-supplied parkId / contentIds are used when paired;
    /// otherwise we tag the upload with "editor-test-*" so production queries
    /// can trivially filter test rows out (or include them on purpose).
    /// </summary>
    internal static class ScreenshotEditorMenu
    {
        private const string EditorTestParkId    = "editor-test-park";
        private const string EditorTestContentId = "editor-test-content";

        [MenuItem("DreamPark/Troubleshooting/Take Screenshot", false, 210)]
        private static void TakeScreenshot()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog(
                    "Take Screenshot",
                    "Enter Play mode first.\n\nScreenshotAPI captures Camera.main at end-of-frame, which only runs in Play mode. " +
                    "Open a scene with a Main Camera, press Play, then run this menu again.",
                    "OK");
                return;
            }

            if (Camera.main == null)
            {
                EditorUtility.DisplayDialog(
                    "Take Screenshot",
                    "No Camera tagged MainCamera in the active scene. Tag one as MainCamera and try again.",
                    "OK");
                return;
            }

            if (!AuthAPI.isLoggedIn)
            {
                EditorUtility.DisplayDialog(
                    "Take Screenshot",
                    "Not logged in. Use the DreamPark SDK login panel to sign in, then try again.",
                    "OK");
                return;
            }

            // Resolve parkId/contentIds in order of decreasing accuracy:
            //   1) SessionContext — editor is paired with a real DreamBox.
            //   2) ContentUploader's LastContentId — the project's canonical
            //      server-registered contentId, set when the SDK creator
            //      last uploaded a build (ContentUploaderPanel writes it).
            //   3) Assets/Content/ folder convention — every folder there
            //      maps 1:1 to a contentId (see ContentProcessor.cs:130).
            //      We skip the SDK template stub "YOUR_GAME_HERE".
            //   4) Hardcoded "editor-test-*" so test rows are trivially
            //      filterable out of production queries.
            string parkId = !string.IsNullOrEmpty(SessionContext.LocationId)
                ? SessionContext.LocationId
                : EditorTestParkId;

            string[] contentIds;
            if (SessionContext.SelectedContentIds != null && SessionContext.SelectedContentIds.Length > 0)
            {
                contentIds = SessionContext.SelectedContentIds;
            }
            else
            {
                string resolved = ResolveProjectContentId();
                contentIds = new[] { string.IsNullOrEmpty(resolved) ? EditorTestContentId : resolved };
            }

            Debug.Log($"[ScreenshotAPI Editor] Capturing… parkId={parkId}, contentIds=[{string.Join(",", contentIds)}]");

            ScreenshotAPI.CaptureAndUpload(
                parkId: parkId,
                contentIds: contentIds,
                primaryContentId: contentIds[0],
                caption: "Editor test capture",
                onComplete: (ok, mediaUrl) =>
                {
                    if (ok)
                    {
                        Debug.Log($"[ScreenshotAPI Editor] ✅ Uploaded → {mediaUrl}");
                        // Two-button form returns true on the first button.
                        // Opening the URL is the most useful action — it
                        // lets the tester eyeball the upload immediately.
                        bool openIt = EditorUtility.DisplayDialog(
                            "Screenshot Uploaded",
                            "Upload succeeded.\n\n" + (mediaUrl ?? "(no URL returned)"),
                            "Open in Browser",
                            "Close");
                        if (openIt && !string.IsNullOrEmpty(mediaUrl))
                        {
                            Application.OpenURL(mediaUrl);
                        }
                    }
                    else
                    {
                        Debug.LogError("[ScreenshotAPI Editor] ❌ Upload failed — see prior console errors for the step that broke (presign / PUT / commit).");
                        EditorUtility.DisplayDialog(
                            "Upload Failed",
                            "Screenshot upload failed. Check the Console for the failing step (presign / PUT / commit).",
                            "OK");
                    }
                });
        }

        // Greys the menu item out when Play mode isn't running so the user
        // gets a faster signal than the dialog above.
        [MenuItem("DreamPark/Troubleshooting/Take Screenshot", true)]
        private static bool TakeScreenshotValidate()
        {
            return Application.isPlaying;
        }

        /// <summary>
        /// Best-effort guess at the contentId of the project being worked on
        /// in the editor. Mirrors the convention used by ContentProcessor:
        /// folder names directly under Assets/Content/ are content IDs.
        /// </summary>
        private static string ResolveProjectContentId()
        {
            // 1) Last uploaded contentId — most authoritative because the
            //    creator already registered this ID with the backend.
            string last = EditorPrefs.GetString("DreamPark.ContentUploader.LastContentId", "");
            if (!string.IsNullOrEmpty(last)) return last;

            // 2) Single folder under Assets/Content/ that isn't the
            //    untouched template stub. If there's more than one, we
            //    don't try to guess — fall through to the test fallback.
            try
            {
                if (Directory.Exists("Assets/Content"))
                {
                    var folders = Directory.GetDirectories("Assets/Content")
                        .Select(p => Path.GetFileName(p))
                        .Where(n => !string.IsNullOrEmpty(n) && n != "YOUR_GAME_HERE")
                        .ToArray();
                    if (folders.Length == 1) return folders[0];
                    if (folders.Length > 1)
                    {
                        Debug.LogWarning($"[ScreenshotAPI Editor] Multiple content folders found ({string.Join(",", folders)}); set DreamPark.ContentUploader.LastContentId or pair a session to disambiguate.");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ScreenshotAPI Editor] Content folder probe failed: {e.Message}");
            }
            return null;
        }
    }
}
#endif
