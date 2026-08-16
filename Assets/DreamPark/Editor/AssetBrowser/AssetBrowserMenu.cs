#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Right-click entry point for the import pipeline.
    ///
    /// This is what makes the explicit-pass design complete rather than
    /// partial: the Asset Browser handles everything it downloads, and this
    /// handles everything a creator dragged in by hand or wants re-processed
    /// after tweaking a texture. Between them there is no need for an
    /// AssetPostprocessor watching the whole project — which is the point,
    /// because a postprocessor could not tell a Dreamie mesh from the
    /// creator's own art without a path predicate that has to stay correct
    /// forever.
    ///
    /// Shape follows MaterialConverter's right-click items: Selection.objects
    /// (never activeObject), a validator that HIDES rather than greys out, one
    /// SaveAssets at the end, one summary dialog.
    /// </summary>
    public static class AssetBrowserMenu
    {
        private static readonly HashSet<string> ModelExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".fbx", ".obj", ".dae", ".blend" };

        private const string MenuPath = "Assets/DreamPark/Apply DreamPark Model Import Settings";

        [MenuItem(MenuPath, false, 52)]
        public static void ApplyToSelection()
        {
            var paths = SelectedModelPaths().ToList();
            if (paths.Count == 0) return;

            int ok = 0, failed = 0;
            var warnings = new List<string>();

            ContentProcessor.ExecuteWithWatchdogPaused(() =>
            {
                try
                {
                    for (int i = 0; i < paths.Count; i++)
                    {
                        EditorUtility.DisplayProgressBar(
                            "DreamPark model import",
                            $"{Path.GetFileName(paths[i])} ({i + 1}/{paths.Count})",
                            (float)i / paths.Count);

                        var result = DreamieImportPass.Reapply(paths[i]);
                        if (result.ok) ok++; else failed++;
                        foreach (var w in result.warnings)
                            warnings.Add($"{Path.GetFileName(paths[i])}: {w}");
                        if (!result.ok)
                            Debug.LogWarning($"[Dreamie] {paths[i]}: {result.error}");
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
            });

            foreach (var w in warnings) Debug.LogWarning("[Dreamie] " + w);

            EditorUtility.DisplayDialog(
                "DreamPark model import",
                $"{ok} model{(ok == 1 ? "" : "s")} processed"
                + (failed > 0 ? $", {failed} failed" : "")
                + "."
                + (warnings.Count > 0 ? "\n\nSome came in with warnings — see the Console." : ""),
                "OK");
        }

        /// <summary>
        /// Returning false HIDES the item rather than greying it out, so it
        /// never appears on an unrelated selection. Short-circuits on the
        /// first model found.
        /// </summary>
        [MenuItem(MenuPath, true)]
        public static bool ValidateApplyToSelection() => SelectedModelPaths().Any();

        private static IEnumerable<string> SelectedModelPaths()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    // A folder means "everything under here", which is how a
                    // creator asks to re-process a whole batch after changing
                    // something in the settings table.
                    foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { path }))
                    {
                        string p = AssetDatabase.GUIDToAssetPath(guid);
                        if (ModelExtensions.Contains(Path.GetExtension(p))) yield return p;
                    }
                    continue;
                }

                if (ModelExtensions.Contains(Path.GetExtension(path))) yield return path;
            }
        }

        /* ── maintenance ───────────────────────────────────────────────── */

        [MenuItem("DreamPark/Troubleshooting/Rebuild Dreamie Asset Index", false, 212)]
        public static void RebuildIndex()
        {
            var folders = ContentFolders.UserContentFolderNames();
            int total = 0;
            foreach (var contentId in folders) total += DreamieFolders.RebuildIndex(contentId);

            EditorUtility.DisplayDialog(
                "Dreamie asset index",
                $"Rebuilt from what is on disk: {total} asset{(total == 1 ? "" : "s")} across "
                + $"{folders.Count} content folder{(folders.Count == 1 ? "" : "s")}.\n\n"
                + "The index is only a cache — provenance itself lives in each model's .meta, so this "
                + "is always safe to run.",
                "OK");
        }

        [MenuItem("DreamPark/Troubleshooting/Clear Asset Browser Thumbnail Cache", false, 213)]
        public static void ClearThumbnails()
        {
            ThumbnailCache.ClearDiskCache();
            Debug.Log("[Dreamie] Thumbnail cache cleared.");
        }
    }
}
#endif
