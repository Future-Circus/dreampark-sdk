#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    // Enumerates the "user-facing roots" of a content package: the prefabs whose root
    // carries LevelTemplate (attractions — AttractionTemplate derives from it),
    // PropTemplate (props), or PlayerRig (the player rig).
    //
    // This predicate is already copy-pasted at four call sites, each with a comment
    // saying they must stay in lockstep:
    //
    //   SmartBundleGrouper.IsUserFacingRoot
    //   ContentUploaderPanel.RefreshContentRoots
    //   ContentProcessor.GenerateAllLevelPreviews
    //   LevelObjectManager
    //
    // …and one of them has already drifted: ContentProcessor's copy lost its
    // ThirdPartyLocal filter. Rather than adding a fifth copy, this is written to be
    // the shared one, and ContentUploaderPanel.RefreshContentRoots should be
    // refactored onto it.
    public static class ContentRootScanner
    {
        public const string ContentFolder = "Assets/Content";

        public static string RootFor(string contentId)
        {
            return ContentFolder + "/" + contentId;
        }

        // The content folders available in this project. Matches
        // ContentUploaderPanel.RefreshContentIdOptions.
        public static List<string> ContentIds()
        {
            var result = new List<string>();
            try
            {
                string abs = Path.Combine(Application.dataPath, "Content");
                if (!Directory.Exists(abs)) return result;

                result.AddRange(Directory.GetDirectories(abs)
                    .Select(Path.GetFileName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not enumerate content folders: {e.Message}");
            }
            return result;
        }

        // The content folder the Content Uploader is currently pointed at. Read from
        // the same EditorPref the panel persists to, so a check running outside the
        // panel instance agrees with what the dev is looking at.
        public static string CurrentContentId()
        {
            string saved = EditorPrefs.GetString("DreamPark.ContentUploader.LastContentId", "");
            if (!string.IsNullOrEmpty(saved) && AssetDatabase.IsValidFolder(RootFor(saved)))
                return saved;

            var all = ContentIds();
            return all.Count > 0 ? all[0] : "";
        }

        public static List<ContentRootInfo> Scan(string contentId)
        {
            var roots = new List<ContentRootInfo>();
            if (string.IsNullOrEmpty(contentId)) return roots;

            string contentRoot = RootFor(contentId);
            if (!AssetDatabase.IsValidFolder(contentRoot)) return roots;

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                // ThirdPartyLocal is the gitignored staging area for imported vendor
                // packages. Its prefabs never ship, and they frequently contain demo
                // content with missing-script references that blows up prefab editing.
                if (IsThirdPartyLocal(path)) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ContentRootKindPublic kind;
                // LevelTemplate FIRST: AttractionTemplate derives from LevelTemplate,
                // and an attraction root also auto-gets a GameArea, so order matters
                // for classification.
                if (prefab.GetComponent<LevelTemplate>() != null)
                    kind = ContentRootKindPublic.Attraction;
                else if (prefab.GetComponent<PropTemplate>() != null)
                    kind = ContentRootKindPublic.Prop;
                else if (prefab.GetComponent<PlayerRig>() != null)
                    kind = ContentRootKindPublic.Player;
                else
                    continue;

                roots.Add(new ContentRootInfo
                {
                    assetPath = path,
                    name = Path.GetFileNameWithoutExtension(path),
                    guid = guid,
                    kind = kind,
                });
            }

            roots.Sort((a, b) =>
            {
                int k = ((int)a.kind).CompareTo((int)b.kind);
                if (k != 0) return k;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });

            return roots;
        }

        // Note the separators. "/ThirdParty/" deliberately does NOT match
        // "/ThirdPartyLocal/" — ThirdParty is tracked, shipped content; ThirdPartyLocal
        // is gitignored staging. A naive Contains("ThirdParty") conflates them, which
        // is why every call site in this codebase uses the slash-delimited form.
        public static bool IsThirdPartyLocal(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && assetPath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsUnderContentRoot(string assetPath, string contentRoot)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(contentRoot)) return false;
            return assetPath.StartsWith(contentRoot + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif
