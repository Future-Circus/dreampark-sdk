#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    // The upload-path gate. Shaped after LuaSurfaceGate — a static entry point that
    // shows its own UI and reports whether the upload may proceed — with one
    // difference: this one is ASYNCHRONOUS, because it opens a real window rather
    // than a DisplayDialog.
    //
    // EditorWindow.ShowUtility() is non-modal, so Show() returns immediately and a
    // synchronous `bool` gate cannot learn the user's answer. The shape used here is
    // the same one SDKUpdateChecker.EnsureUpToDateThen already uses: return false to
    // stop the current attempt, and invoke a continuation only on the happy path.
    //
    // The most important property: when nothing is wrong, this is INVISIBLE. No
    // window, no dialog, no extra click. A clean project uploads with exactly the
    // friction it had before this suite existed. A gate that interrupts on every
    // upload is a gate people learn to click through, and then the one that mattered
    // gets clicked through too.
    public static class PreUploadChecksGate
    {
        // scenesAreSaved: pass true ONLY if the caller has just run
        // SaveModifiedScenesBeforeCompile. The scene-override check reads it to decide
        // whether opening and restoring scenes is safe; a false positive there costs
        // somebody their unsaved work.
        public static bool Passes(EditorWindow owner, string contentId, Action onCleared,
                                  bool scenesAreSaved)
        {
            if (string.IsNullOrEmpty(contentId)) return true;

            PreUploadReport report;
            try
            {
                report = PreUploadCheckRunner.RunAll(contentId, ReportProgress, scenesAreSaved);
            }
            catch (Exception e)
            {
                // Fail open, deliberately and loudly. The runner already catches
                // per-check failures; reaching here means the harness itself broke,
                // and a broken harness must not be able to stop a release.
                Debug.LogWarning($"[DreamPark] Pre-upload checks could not run — continuing: {e}");
                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!report.HasActionableFindings) return true;

            PreUploadChecksPopup.Show(owner, contentId, report, proceed =>
            {
                if (proceed && onCleared != null) onCleared();
            });

            return false;
        }

        private static void ReportProgress(float t, string message)
        {
            EditorUtility.DisplayProgressBar("DreamPark", message ?? "Running pre-upload checks…",
                                             Mathf.Clamp01(t));
        }
    }
}
#endif
