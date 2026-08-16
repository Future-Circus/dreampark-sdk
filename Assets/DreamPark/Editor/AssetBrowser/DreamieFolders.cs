#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Where a downloaded asset lands, what it is called, and how we know we
    /// already have it.
    ///
    /// ── WHY THE FILENAMES LOOK LIKE THAT ───────────────────────────────────
    ///
    /// ContentProcessor addresses every model in a content folder as
    /// "{gameId}/Models/{filenameWithoutExtension}" and THROWS THE FOLDER
    /// STRUCTURE AWAY. Two files called shrimp.fbx in different subfolders of
    /// one content package silently collide on address — and ThirdPartyLocal's
    /// deduplicator matches on bare filename too, so it would see them as the
    /// same asset. Previews have the same problem: they are written to
    /// Previews/{name}.png.
    ///
    /// So the filename has to carry the uniqueness itself. Every asset gets a
    /// stem of "{slug}__{first 8 of assetId}", used as the folder name, the
    /// FBX name, and the prefix on every extracted material and texture. Two
    /// shrimp called "shrimp" stay distinct because their ids differ; the same
    /// shrimp downloaded twice resolves to the same stem, which is what makes
    /// re-download-in-place possible instead of minting a second GUID.
    ///
    /// The separator is a DOUBLE underscore so a single underscore inside a
    /// name can never be mistaken for the boundary when parsing a stem back.
    /// </summary>
    public static class DreamieFolders
    {
        /// <summary>Folder under a content package's ThirdPartyLocal.</summary>
        public const string RootFolderName = "Dreamie";

        /// <summary>
        /// Where assets that belong to no batch go. Leading underscore so it
        /// sorts away from the dated job folders and can never collide with a
        /// hex job slug.
        /// </summary>
        public const string SinglesFolder = "_singles";

        private const string IndexFileName = ".dreamie-index.json";
        private const int MaxSlugLength = 40;

        /* ── paths ─────────────────────────────────────────────────────── */

        public static string RootFor(string contentId)
            => $"Assets/Content/{contentId}/ThirdPartyLocal/{RootFolderName}";

        public static string JobFolderFor(string contentId, string jobSlug)
            => $"{RootFor(contentId)}/{jobSlug}";

        public static string AssetFolderFor(string contentId, string jobSlug, string stem)
            => $"{JobFolderFor(contentId, jobSlug)}/{stem}";

        public static string ModelPathFor(string contentId, string jobSlug, string stem)
            => $"{AssetFolderFor(contentId, jobSlug, stem)}/{stem}.fbx";

        public static string MaterialsFolderFor(string contentId, string jobSlug, string stem)
            => $"{AssetFolderFor(contentId, jobSlug, stem)}/Materials";

        public static string TexturesFolderFor(string contentId, string jobSlug, string stem)
            => $"{AssetFolderFor(contentId, jobSlug, stem)}/Textures";

        /* ── naming ────────────────────────────────────────────────────── */

        /// <summary>
        /// A filename-safe, address-safe form of an asset's name. Lowercase
        /// because Addressables keys are compared case-sensitively but macOS
        /// filesystems are not, and that mismatch is a real source of
        /// "works on my machine".
        /// </summary>
        public static string Slug(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "asset";
            var sb = new StringBuilder(raw.Length);
            bool lastDash = false;
            foreach (char c in raw.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            string s = sb.ToString().Trim('-');
            if (s.Length > MaxSlugLength) s = s.Substring(0, MaxSlugLength).Trim('-');
            // A name made entirely of punctuation slugs to nothing. The id
            // suffix still makes the stem unique, so a generic base is fine —
            // an empty one is not, it would produce a filename of "__a1b2c3d4".
            return s.Length == 0 ? "asset" : s;
        }

        public static string ShortId(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return "00000000";
            string clean = new string(assetId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return clean.Length <= 8 ? clean.PadRight(8, '0') : clean.Substring(0, 8);
        }

        /// <summary>The globally unique name this asset uses for everything.</summary>
        public static string StemFor(DreamieAsset asset)
            => asset == null ? null : $"{Slug(asset.name)}__{ShortId(asset.id)}";

        /// <summary>
        /// The folder a batch lands in — dated so it sorts chronologically and
        /// a creator can see at a glance which download is which.
        /// </summary>
        public static string JobSlugFor(DreamieJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.id)) return SinglesFolder;
            string date = job.createdAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(job.createdAtMs).UtcDateTime.ToString("yyyyMMdd")
                : "00000000";
            return $"{date}-{ShortId(job.id)}";
        }

        /// <summary>
        /// The folder for an asset whose job we could not resolve — it has a
        /// jobId but the job list did not include it (aged out of the page, or
        /// the jobs endpoint degraded). Still filed by id so a batch stays
        /// together; just without the date it could not know.
        /// </summary>
        public static string JobSlugForId(string jobId)
            => string.IsNullOrEmpty(jobId) ? SinglesFolder : $"job-{ShortId(jobId)}";

        /* ── the index ─────────────────────────────────────────────────────
         *
         * A CACHE, not a source of truth. Authoritative provenance lives in
         * each asset's .meta (see DreamieProvenance); this file exists so the
         * grid can answer "do I already have this?" for a page of 60 assets
         * without opening 60 importers, and so a re-download can find the
         * folder it should overwrite.
         *
         * Keyed by asset GUID rather than path, because ThirdPartySyncTool
         * moves these files out from under us on Compile & Upload and a
         * path-keyed cache would be wrong the moment it did. Deleting this
         * file is always safe — Rebuild() regenerates it from the labels.
         *
         * Dot-prefixed, so Unity's asset pipeline ignores it entirely: no
         * .meta, no GUID, never swept into a bundle. All access is System.IO,
         * never AssetDatabase. This is the same pattern (and the same
         * reasoning) as PreviewMetadataStore's .preview-overrides.json.
         * ─────────────────────────────────────────────────────────────── */

        [Serializable]
        public class IndexEntry
        {
            public string guid;
            public string assetId;
            public string jobId;
            public string name;
            public string path;      // where it was last seen; advisory only
        }

        [Serializable]
        private class IndexFile
        {
            public int version = 1;
            public List<IndexEntry> entries = new List<IndexEntry>();
        }

        private static string IndexPathFor(string contentId)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.Combine(projectRoot, RootFor(contentId).Replace('/', Path.DirectorySeparatorChar), IndexFileName);
        }

        public static List<IndexEntry> LoadIndex(string contentId)
        {
            try
            {
                string p = IndexPathFor(contentId);
                if (!File.Exists(p)) return new List<IndexEntry>();
                var parsed = JsonUtility.FromJson<IndexFile>(File.ReadAllText(p));
                return parsed?.entries ?? new List<IndexEntry>();
            }
            catch (Exception e)
            {
                // A corrupt cache is not a corrupt project. Behave as though
                // it were empty; the worst outcome is that the grid shows
                // nothing as already-imported until the next successful write.
                Debug.LogWarning($"[Dreamie] Could not read the asset index for {contentId}: {e.Message}");
                return new List<IndexEntry>();
            }
        }

        public static void SaveIndex(string contentId, List<IndexEntry> entries)
        {
            try
            {
                string p = IndexPathFor(contentId);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonUtility.ToJson(new IndexFile { entries = entries }, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dreamie] Could not write the asset index for {contentId}: {e.Message}");
            }
        }

        /// <summary>
        /// Record (or update) one downloaded asset. Keyed on assetId, so
        /// re-downloading replaces the row rather than adding a duplicate.
        /// </summary>
        public static void Record(string contentId, string assetPath, DreamieAsset asset)
        {
            if (string.IsNullOrEmpty(assetPath) || asset == null) return;
            var entries = LoadIndex(contentId);
            entries.RemoveAll(e => e.assetId == asset.id);
            entries.Add(new IndexEntry
            {
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                assetId = asset.id,
                jobId = asset.assetJobId,
                name = asset.name,
                path = assetPath,
            });
            SaveIndex(contentId, entries);
        }

        /// <summary>
        /// Where this asset already lives in this content folder, or null.
        /// Resolves through the GUID rather than the recorded path, so a file
        /// ThirdPartySyncTool has since moved is still found.
        /// </summary>
        public static string ExistingPathFor(string contentId, string assetId)
        {
            foreach (var e in LoadIndex(contentId))
            {
                if (e.assetId != assetId) continue;
                if (!string.IsNullOrEmpty(e.guid))
                {
                    string byGuid = AssetDatabase.GUIDToAssetPath(e.guid);
                    if (!string.IsNullOrEmpty(byGuid) && File.Exists(byGuid)) return byGuid;
                }
                if (!string.IsNullOrEmpty(e.path) && File.Exists(e.path)) return e.path;
            }
            return null;
        }

        /// <summary>
        /// Every assetId this content folder already has, for marking up a
        /// page of grid results in one pass.
        /// </summary>
        public static Dictionary<string, string> ImportedAssetIds(string contentId)
        {
            var map = new Dictionary<string, string>();
            foreach (var e in LoadIndex(contentId))
            {
                if (string.IsNullOrEmpty(e.assetId) || map.ContainsKey(e.assetId)) continue;
                string path = !string.IsNullOrEmpty(e.guid) ? AssetDatabase.GUIDToAssetPath(e.guid) : null;
                if (string.IsNullOrEmpty(path)) path = e.path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) map[e.assetId] = path;
            }
            return map;
        }

        /// <summary>
        /// Regenerate the index from what is actually on disk, by reading the
        /// authoritative provenance off every labelled asset. Recovers from a
        /// deleted or stale cache, and reconciles a project where somebody
        /// moved things by hand.
        /// </summary>
        public static int RebuildIndex(string contentId)
        {
            var entries = new List<IndexEntry>();
            string prefix = $"Assets/Content/{contentId}/";
            foreach (var kv in DreamieProvenance.All())
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                entries.Add(new IndexEntry
                {
                    guid = AssetDatabase.AssetPathToGUID(kv.Key),
                    assetId = kv.Value.assetId,
                    jobId = kv.Value.jobId,
                    name = kv.Value.name,
                    path = kv.Key,
                });
            }
            SaveIndex(contentId, entries);
            return entries.Count;
        }

        /* ── staging ───────────────────────────────────────────────────── */

        /// <summary>
        /// Where bytes land before they are allowed into Assets/.
        ///
        /// Under Library/ deliberately: it is already gitignored, and a
        /// half-written FBX that appears inside Assets/ gets imported by Unity
        /// the moment focus returns — producing a broken asset with a real
        /// GUID that something might reference before the download even
        /// finishes failing.
        /// </summary>
        public static string StagingRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.Combine(projectRoot, "Library", "DreamPark", "Dreamie", "staging");
        }

        public static string EnsureFolder(string unityFolderPath)
        {
            if (string.IsNullOrEmpty(unityFolderPath)) return null;
            if (AssetDatabase.IsValidFolder(unityFolderPath)) return unityFolderPath;

            var parts = unityFolderPath.Split('/');
            string acc = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = acc + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(acc, parts[i]);
                acc = next;
            }
            return acc;
        }
    }
}
#endif
