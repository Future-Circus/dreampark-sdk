// ─────────────────────────────────────────────────────────────────────
//  ContentFolders.cs — which folder under Assets/Content is the creator's?
//
//  THE SDK ASKED THIS QUESTION IN SIX PLACES AND GOT TWO DIFFERENT ANSWERS.
//  ContentProcessor and TagLayerSchemaMenuActions each took the FIRST
//  subfolder of Assets/Content. The four optimizer windows each took the
//  first subfolder that is NOT the placeholder. Both were fine while the
//  template shipped exactly one folder, and both broke the moment the
//  Sample project landed beside it:
//
//    "first subfolder"          -> Sample sorts before YOUR_GAME_HERE, so
//                                  the game prefix became "Sample". That
//                                  names addressable groups Sample-*, sets
//                                  the global label to Sample, and points
//                                  the uploader's default selection at the
//                                  sample instead of the creator's folder —
//                                  which in turn means ContentUploaderPanel's
//                                  "give your game an ID" gate compares
//                                  against Sample, never fires, and the
//                                  creator is silently never asked to name
//                                  their game.
//    "first non-placeholder"    -> the optimizer windows pointed at Sample's
//                                  textures and audio rather than the
//                                  creator's.
//
//  So this is the one place that answers it, and every caller delegates.
//
//  SAMPLE IS NOT USER CONTENT; THE PLACEHOLDER IS. That asymmetry is
//  deliberate and is the whole design. YOUR_GAME_HERE *is* the creator's
//  folder — they simply have not renamed it yet — so it must keep flowing
//  through as the game folder, or the rename nag and the upload gates that
//  compare against it would stop firing. Sample is ours: it ships with the
//  SDK, it is never what the creator is building, and no amount of it being
//  alphabetically first should make it the answer.
//
//  WHAT THIS DELIBERATELY DOES NOT FILTER. "Enumerate every content folder"
//  is a different question from "which one is the creator's", and Sample is
//  a legitimate answer to the first. ContentProcessor.CleanupAddressableSettings
//  builds its snapshot of live folders straight from disk on purpose — if
//  Sample were filtered out of THAT, the janitor would decide Sample-Root
//  is a stale group left over from a deleted contentId and delete it.
//  ForceUpdateAllContent and EnforceContentNamespaces are the same story.
//  Filter with IsUserContent only where the question is "whose game is this".
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace DreamPark
{
    public static class ContentFolders
    {
        public const string Root = "Assets/Content";

        /// The folder name the SDK template ships with. Creators are prompted
        /// to rename it; until they do, it still counts as THEIR folder.
        public const string PlaceholderName = "YOUR_GAME_HERE";

        /// The bundled example project. Ships with the SDK, is never the
        /// creator's own content, and must never be mistaken for it.
        public const string SampleName = "Sample";

        public static bool IsSample(string nameOrPath)
        {
            return NameOf(nameOrPath).Equals(SampleName, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPlaceholder(string nameOrPath)
        {
            return NameOf(nameOrPath).Equals(PlaceholderName, StringComparison.OrdinalIgnoreCase);
        }

        /// True for anything that belongs to the creator — which includes the
        /// un-renamed placeholder and excludes only the bundled Sample.
        public static bool IsUserContent(string nameOrPath)
        {
            string name = NameOf(nameOrPath);
            return !string.IsNullOrEmpty(name) && !IsSample(name);
        }

        /// Every content folder that is the creator's, in on-disk order.
        public static List<string> UserContentFolderNames()
        {
            var result = new List<string>();
            if (!Directory.Exists(Root)) return result;

            foreach (var dir in Directory.GetDirectories(Root))
            {
                string name = Path.GetFileName(dir);
                if (IsUserContent(name)) result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// True for a folder name the SDK owns and no creator may publish
        /// under: the bundled Sample, and the un-renamed template placeholder.
        /// Both are shipped defaults present in every install, so a contentId
        /// of either is by definition not the caller's own game.
        /// </summary>
        public static bool IsReserved(string nameOrPath)
        {
            return IsSample(nameOrPath) || IsPlaceholder(nameOrPath);
        }

        /// True when the un-renamed SDK template folder is still on disk, so
        /// callers can tell "rename this" apart from "you have no folder at
        /// all" — ContentIdSetupPopup renames an EXISTING folder and cannot
        /// create one.
        public static bool PlaceholderExists()
        {
            return Directory.Exists(Root + "/" + PlaceholderName);
        }

        /// True when the creator has a folder of their own that is not the
        /// untouched template — i.e. they have actually started a game.
        public static bool HasNamedGame()
        {
            foreach (var name in UserContentFolderNames())
            {
                if (!IsPlaceholder(name)) return true;
            }
            return false;
        }

        /// <summary>
        /// The creator's game folder. Skips Sample; falls back to the
        /// placeholder name so every downstream gate that compares against it
        /// keeps working on a fresh, un-renamed project.
        /// </summary>
        public static string GameFolderName()
        {
            // Searched rather than hardcoded to Assets/Content because the
            // original implementations did, and a project may nest a Content
            // folder deeper (PackageRelocator moves things around).
            string[] contentPaths;
            try {
                contentPaths = Directory.GetDirectories("Assets", "Content", SearchOption.AllDirectories);
            } catch (Exception) {
                return PlaceholderName;
            }

            foreach (string contentPath in contentPaths)
            {
                foreach (string dir in Directory.GetDirectories(contentPath))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsSample(name)) continue;
                    return name;
                }
            }
            return PlaceholderName;
        }

        public static string GamePrefix()
        {
            return Sanitize(GameFolderName());
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            return name.Replace("[", "").Replace("]", "").Trim();
        }

        /// <summary>
        /// Default scan root for the optimizer windows: the creator's real
        /// game folder if they have one, otherwise all of Assets/Content.
        ///
        /// Returning the Root rather than the placeholder folder when they
        /// have not renamed yet preserves the behaviour these windows already
        /// had — the only change is that Sample can no longer win.
        /// </summary>
        public static string AutoPickContentFolder()
        {
            if (!AssetDatabase.IsValidFolder(Root)) return Root;

            foreach (var sub in AssetDatabase.GetSubFolders(Root))
            {
                if (IsSample(sub)) continue;
                if (IsPlaceholder(sub)) continue;
                return sub;
            }
            return Root;
        }

        /// True when an asset path lives inside the bundled Sample project.
        /// Used by the Park Simulator to keep example content out of a
        /// creator's park when they have asked it to.
        public static bool IsUnderSample(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string norm = assetPath.Replace('\\', '/');
            return norm.StartsWith(Root + "/" + SampleName + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The content folder an asset lives in — the segment straight after
        /// Assets/Content/ — or empty for anything outside it.
        ///
        /// This is the unit a PlayerRig is keyed by: ContentProcessor stamps
        /// gameId from the folder name, and PlayerRig.instances is a
        /// Dictionary&lt;gameId, PlayerRig&gt;. So "which content package does
        /// this belong to" and "which rig serves it" are the same question.
        /// </summary>
        public static string FolderOfAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            string norm = assetPath.Replace('\\', '/');
            string prefix = Root + "/";
            if (!norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string rest = norm.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            return slash >= 0 ? rest.Substring(0, slash) : rest;
        }

        private static string NameOf(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return string.Empty;
            string trimmed = nameOrPath.Replace('\\', '/').TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }
    }
}
#endif
