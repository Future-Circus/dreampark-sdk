#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Adds a "DreamPark/Developer Portal" entry to the editor menu bar.
    // Opens the web Developer Portal — earnings, content management, and
    // the Attractions editor — the "second half" of the content creation
    // flow after uploading from the Content Uploader.
    //
    // Priority 3 slots it into the top menu cluster (Content Uploader 0,
    // Check for SDK Updates 1, Publish SDK Version 2) so it's one click
    // away, and the big gap to the next section (Multiplayer at 50) keeps
    // Unity's automatic separator below it.
    //
    // The URL derives from DreamParkAPI.baseUrl (not a hard-coded host) so
    // internal projects built with DREAMPARK_DEV_BACKEND land on the dev
    // portal that matches the backend they're actually uploading to.
    internal static class DeveloperPortalMenuItem
    {
        internal static string PortalUrl => API.DreamParkAPI.baseUrl + "/developer";

        // Deep link to a specific content package's Attractions page —
        // shared by the post-upload auto-open in ContentUploaderPanel and
        // the completion-view button in ContentUploadFlowPopup.
        internal static string AttractionsUrl(string contentId)
        {
            return API.DreamParkAPI.baseUrl
                + "/developer/content/" + System.Uri.EscapeDataString(contentId) + "/attractions";
        }

        [MenuItem("DreamPark/Developer Portal", false, 3)]
        public static void OpenDeveloperPortal()
        {
            Application.OpenURL(PortalUrl);
        }
    }
}
#endif
