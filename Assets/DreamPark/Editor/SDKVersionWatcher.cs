#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Invalidates SDKVersion's process-static cache whenever the version file is
    // (re)imported.
    //
    // WHY THIS FILE EXISTS
    //
    // SDKVersion latches the installed version on first read and, before this, was
    // invalidated by exactly ONE consumer-side call site: UpdateAvailablePopup's
    // importPackageCompleted handler. That covered a single install route and covered
    // it unreliably. Every other route left the cache stale for the rest of the
    // editor session:
    //
    //   - double-clicking a .unitypackage
    //   - drag-and-drop into the Project window
    //   - Assets ▸ Import Package ▸ Custom Package…
    //   - PackageImporter.Import / ImportMany (the scripted path)
    //   - a locally exported package hand-carried into a content project
    //
    // Meanwhile SDKUpdateChecker re-fetches the REMOTE manifest before every
    // comparison — deliberately, because the once-per-session manifest cache goes
    // stale — but then compared it against the stale LOCAL number. Refreshing one
    // side and trusting the other is what made a freshly installed SDK still report
    // as out of date, and what made developers install it a second time.
    //
    // An AssetPostprocessor catches every route uniformly, because they all end in
    // the same place: the version JSON gets imported.
    //
    // This handles the case where the import does NOT trigger a domain reload (a
    // JSON-only or partial import). The reload case is covered from the other side:
    // SDKUpdateChecker's [InitializeOnLoad] static constructor re-runs after every
    // reload and calls ReconcileAfterInstall itself.
    internal class SDKVersionWatcher : AssetPostprocessor
    {
        private const string VersionFileName = "DreamParkSDKVersion.json";

        // SDKVersion.Reload() forces a synchronous reimport of the version file,
        // which fires THIS callback again. Without a guard that is an infinite loop.
        // The reimport is synchronous, so the flag is reliably held for the whole
        // window in which the re-entrant callback can arrive.
        private static bool suppressReentrancy;

        // Must be static — Unity binds it by name, not by override. Using the 4-arg
        // form to match ContentFolderWatchdog.AssetPostprocessorWatcher, the existing
        // in-repo precedent; we have no use for `didDomainReload` here.
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (suppressReentrancy) return;

            if (!Touches(importedAssets) && !Touches(movedAssets)) return;

            // Defer: doing AssetDatabase work from inside an import callback invites
            // reentrancy, and Reload() forces a synchronous reimport.
            EditorApplication.delayCall += Settle;
        }

        private static bool Touches(string[] paths)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (!string.IsNullOrEmpty(p) && p.EndsWith(VersionFileName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void Settle()
        {
            // Re-arm rather than return. A .unitypackage import ALWAYS leaves the
            // editor compiling, which is exactly when this fires — dropping the work
            // here would discard the one callback this whole file exists to deliver.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Settle;
                return;
            }

            suppressReentrancy = true;
            try
            {
                SDKVersion.Reload();

                // No-ops unless an install was actually in flight. When one was, this
                // is what turns "silently re-nag the user" into either a success log
                // or an explicit dialog naming both versions.
                SDKUpdateChecker.ReconcileAfterInstall();
            }
            catch (Exception e)
            {
                // A version check must never be able to take out the import pipeline.
                Debug.LogWarning($"[DreamPark] SDK version reconcile failed: {e.Message}");
            }
            finally
            {
                suppressReentrancy = false;
            }
        }
    }
}
#endif
