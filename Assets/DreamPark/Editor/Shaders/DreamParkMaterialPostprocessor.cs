#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.Shaders
{
    // Repairs DreamPark materials that arrive misconfigured, at import.
    //
    // WHAT THIS CATCHES THAT THE SHADERGUI DOES NOT
    //
    // DreamParkShaderGUI only runs when a human has the material inspector open.
    // Plenty of materials never do:
    //
    //   - created by editor scripts (`new Material(shader)` — a bare Material
    //     constructor copies the shader's property defaults but sets no keywords at
    //     all, so _AlphaClip reads 1 while _ALPHATEST_ON is off);
    //   - written by the Dreamie importer / model import profiles;
    //   - hand-edited .mat YAML, or merged from a branch where it was wrong;
    //   - pulled fresh from version control on a machine that has never opened them.
    //
    // Each of those lands as an asset import, which is here.
    //
    // WHY THIS DOES NOT LOOP
    //
    // We write only when DreamParkMaterialRules.Enforce reports an actual change, and
    // Enforce is careful never to report one it did not make (it re-reads the keyword
    // after handing off to URP rather than assuming). The save that follows re-imports
    // the material, this runs again, Enforce finds the invariant already holds and
    // returns false, and it stops. An unconditional SetDirty here is an infinite
    // reimport with a Debug.Log per cycle.
    //
    // WHY THE SAVE IS INLINE AND NOT DEFERRED
    //
    // The obvious shape is to collect the repaired materials and save them from
    // EditorApplication.delayCall, to avoid saving during an import. It is wrong:
    // delayCall is a static delegate and a domain reload clears it. The single most
    // likely batch to contain broken materials — importing an asset package, or a
    // fresh clone — also contains .cs files, so the compile that follows reloads the
    // domain and drops the queue. The materials stay dirty-but-unsaved while the
    // console has already claimed they were fixed. URP's own MaterialPostprocessor
    // saves inline for the same reason; we follow it.
    internal sealed class DreamParkMaterialPostprocessor : AssetPostprocessor
    {
        // Materials under Packages/ are immutable — SetDirty succeeds, the save fails,
        // and the only output is a warning nobody can act on. Assets/ only.
        private const string AssetsPrefix = "Assets/";

        // Assets/DreamPark/ is SDK-owned and its materials are our problem, not the
        // creator's — but they are also the ones most likely to be correct already,
        // and MetaOcclusionCheck deliberately exempts them from creator-facing
        // reporting. Repairing them silently is fine; we just do not narrate it.
        private const string SdkPrefix = "Assets/DreamPark/";

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets == null || importedAssets.Length == 0) return;

            foreach (string path in importedAssets)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                Material mat;
                try
                {
                    mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                }
                catch (Exception)
                {
                    // A material mid-import can fail to load. Not worth a log line —
                    // it will come back through here when the import settles.
                    continue;
                }

                if (mat == null) continue;
                if (!DreamParkMaterialRules.IsGoverned(mat)) continue;

                try
                {
                    if (!DreamParkMaterialRules.EnforceAndDirty(mat)) continue;
                    AssetDatabase.SaveAssetIfDirty(mat);
                }
                catch (Exception e)
                {
                    // Never let this throw out of an import callback: Unity reports
                    // the exception against whatever asset happens to be next in the
                    // batch, which sends people debugging the wrong file.
                    Debug.LogWarning($"[DreamPark] Could not enforce alpha clipping on '{path}': {e.Message}");
                    continue;
                }

                if (!path.StartsWith(SdkPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log(
                        $"[DreamPark] Enabled Alpha Clipping on '{mat.name}' ({path}) — it is "
                      + "Opaque on a DreamPark shader, which requires clipping for Meta "
                      + "environment occlusion. Check it in the Scene view: if the albedo's alpha "
                      + "channel is empty the object will now be invisible, which means it could "
                      + "never have been occluded. See the Materials & Shaders section of CLAUDE.md.",
                        mat);
                }
            }
        }

        // ------------------------------------------------------------------
        // Project-wide sweep, on demand.
        //
        // Import-time repair reaches everything that gets imported — which on a fresh
        // clone or a deleted Library/ is the whole project at once. What it does NOT
        // reach is a project that is already imported and sitting there, and that is
        // the common case: the pre-upload gate catches those at ship time. This menu
        // item is for the person who would rather fix all of them now, deliberately as
        // a menu item and not as an automatic upgrade step, because reaching into
        // someone's project on an SDK update and rewriting materials they did not ask
        // about is not a thing an SDK gets to do quietly.

        [MenuItem("DreamPark/Troubleshooting/Fix Opaque Materials Missing Alpha Clipping", false, 214)]
        private static void FixAllInProject()
        {
            // Scoped to Assets/ for the same reason as the import path.
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            var fixedPaths = new List<string>();

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Checking materials",
                            path,
                            (float)i / Mathf.Max(1, guids.Length)))
                        break;

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat == null) continue;
                    if (!DreamParkMaterialRules.IsGoverned(mat)) continue;

                    if (DreamParkMaterialRules.EnforceAndDirty(mat))
                        fixedPaths.Add(path);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (fixedPaths.Count > 0)
                AssetDatabase.SaveAssets();

            string body = fixedPaths.Count == 0
                ? "Every DreamPark material in this project that is set to Opaque already has "
                + "Alpha Clipping enabled. Nothing to do."
                : $"Enabled Alpha Clipping on {fixedPaths.Count} material"
                + (fixedPaths.Count == 1 ? "" : "s")
                + ":\n\n"
                + string.Join("\n", fixedPaths.ToArray(), 0, Mathf.Min(15, fixedPaths.Count))
                + (fixedPaths.Count > 15 ? $"\n…and {fixedPaths.Count - 15} more (see the Console)." : "")
                + "\n\nLook at these in the Scene view. If an albedo's alpha channel is empty or "
                + "garbage the object will now be invisible — that is not this fix misbehaving, it "
                + "means the material could never have been occluded and the texture needs fixing."
                + "\n\nThis cannot be undone with Ctrl-Z. Use version control to review the diff.";

            if (fixedPaths.Count > 0)
                Debug.Log("[DreamPark] Alpha clipping enabled on:\n" + string.Join("\n", fixedPaths.ToArray()));

            EditorUtility.DisplayDialog("Fix Opaque Materials", body, "OK");
        }
    }
}
#endif
