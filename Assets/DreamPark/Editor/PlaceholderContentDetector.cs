#if UNITY_EDITOR && !DREAMPARKCORE
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // On editor load, checks whether the SDK template's placeholder content
    // folder (Assets/Content/YOUR_GAME_HERE) is still present. If it is, opens
    // ContentIdSetupPopup so creators are guided to rename it — without this,
    // they'd carry the placeholder all the way to their first upload attempt
    // before discovering they need a real content ID.
    //
    // Shown at most once per editor session (tracked via SessionState) so
    // dismissing the popup doesn't cause it to re-stack on every script
    // recompile / domain reload. A new session (closing and re-opening Unity)
    // will prompt again, which is the desired nag-cadence: the rename is a
    // one-time setup step, but if the user genuinely closes Unity without
    // doing it we want them prompted again next time.
    [InitializeOnLoad]
    internal static class PlaceholderContentDetector
    {
        private const string SessionFlag = "DreamPark.PlaceholderDetector.ShownThisSession";

        static PlaceholderContentDetector()
        {
            // Defer past initial editor startup so the popup doesn't try to
            // open while Unity is still compiling scripts or refreshing the
            // AssetDatabase on first launch.
            EditorApplication.delayCall += MaybeShow;
        }

        private static void MaybeShow()
        {
            if (SessionState.GetBool(SessionFlag, false)) return;
            if (EditorApplication.isCompiling) return;
            if (EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            string placeholderPath = Path.Combine(
                Application.dataPath, "Content", ContentIdSetupPopup.PlaceholderName);
            if (!Directory.Exists(placeholderPath)) return;

            // Mark *before* showing — if the popup throws or the user closes
            // it, we don't want to re-pop on the very next domain reload.
            SessionState.SetBool(SessionFlag, true);
            ContentIdSetupPopup.Show(ContentIdSetupPopup.PlaceholderName, null);
        }
    }
}
#endif
