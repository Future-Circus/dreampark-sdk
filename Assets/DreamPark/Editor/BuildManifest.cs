#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Snapshot of what was just built: per-platform list of files (filename +
    // size). Two manifests are compared by *filename equality* — Addressables
    // embeds the content hash in bundle filenames via the AppendHash naming
    // style, so identical filenames imply identical contents. catalog_*.json
    // and catalog_*.hash always change between builds (they record metadata)
    // and will always show up in the changed-files set; that's correct, they're
    // tiny.
    //
    // The manifest baseline is the snapshot of the most recent *successful*
    // upload for a given contentId. Stored at:
    //   <ProjectRoot>/Library/DreamParkBuildManifests/{contentId}.json
    // Library/ is git-ignored and per-machine, which is fine for solo / small
    // teams. A future enhancement would sync baselines via the backend so a
    // teammate's first upload after pulling matches another teammate's last.

    [Serializable]
    public class BuildManifestFile
    {
        // Path relative to ServerData/{platform}/, forward-slash normalized.
        public string fileName;
        public long sizeBytes;
    }

    [Serializable]
    public class BuildManifestPlatform
    {
        public string platform;
        public List<BuildManifestFile> files = new List<BuildManifestFile>();

        public long TotalBytes
        {
            get
            {
                long sum = 0;
                foreach (var f in files) sum += f.sizeBytes;
                return sum;
            }
        }

        public int FileCount => files != null ? files.Count : 0;
    }

    [Serializable]
    public class BuildManifest
    {
        public int schemaVersion = 1;
        public string contentId;
        public int versionNumber;            // Backend version this build was uploaded under (e.g. 12).
        public string buildTimestampUtc;     // ISO-8601.
        public string sdkVersion;
        public List<BuildManifestPlatform> platforms = new List<BuildManifestPlatform>();

        public BuildManifestPlatform GetPlatform(string platform)
        {
            if (platforms == null) return null;
            foreach (var p in platforms) if (p.platform == platform) return p;
            return null;
        }

        public long TotalBytes
        {
            get
            {
                long sum = 0;
                if (platforms != null)
                    foreach (var p in platforms) sum += p.TotalBytes;
                return sum;
            }
        }

        public int TotalFileCount
        {
            get
            {
                int count = 0;
                if (platforms != null)
                    foreach (var p in platforms) count += p.FileCount;
                return count;
            }
        }
    }

    // Per-platform diff between a baseline and a current manifest.
    public class PlatformDiff
    {
        public string platform;
        public List<string> changedFiles = new List<string>();    // Upload these (new or contents differ).
        public List<string> unchangedFiles = new List<string>();  // Skip these (already on server).
        public List<string> removedFiles = new List<string>();    // Present in baseline only; player stops referencing.
        public long changedBytes;
        public long unchangedBytes;
        public long totalBytesCurrent;
    }

    public class BuildManifestDiff
    {
        public List<PlatformDiff> platforms = new List<PlatformDiff>();

        public long TotalChangedBytes
        {
            get { long s = 0; foreach (var p in platforms) s += p.changedBytes; return s; }
        }

        public long TotalUnchangedBytes
        {
            get { long s = 0; foreach (var p in platforms) s += p.unchangedBytes; return s; }
        }

        public long TotalCurrentBytes
        {
            get { long s = 0; foreach (var p in platforms) s += p.totalBytesCurrent; return s; }
        }

        public int TotalChangedFileCount
        {
            get { int c = 0; foreach (var p in platforms) c += p.changedFiles.Count; return c; }
        }

        public PlatformDiff GetPlatform(string platform)
        {
            foreach (var p in platforms) if (p.platform == platform) return p;
            return null;
        }
    }

    public static class BuildManifestStore
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // Public so DirtyGroupsStore (and any future sibling) can persist to
        // the same directory without each one having to recompute the path.
        public static string ManifestRoot =>
            Path.Combine(ProjectRoot, "Library", "DreamParkBuildManifests");

        public static string ServerDataRoot =>
            Path.Combine(ProjectRoot, "ServerData");

        public static string ManifestPathForContent(string contentId)
        {
            return Path.Combine(ManifestRoot, $"{contentId}.json");
        }

        // Walks ServerData/{platform}/ recursively for each platform and
        // produces a fresh manifest of what's currently on disk. Doesn't
        // touch the saved baseline.
        public static BuildManifest BuildFromServerData(string contentId, int versionNumber, IEnumerable<string> platformsToInclude)
        {
            var manifest = new BuildManifest
            {
                contentId = contentId,
                versionNumber = versionNumber,
                buildTimestampUtc = DateTime.UtcNow.ToString("o"),
                sdkVersion = SDKVersion.Current,
            };

            foreach (string platform in platformsToInclude)
            {
                var pm = new BuildManifestPlatform { platform = platform };
                string platformDir = Path.Combine(ServerDataRoot, platform);
                if (Directory.Exists(platformDir))
                {
                    var files = Directory.GetFiles(platformDir, "*", SearchOption.AllDirectories);
                    foreach (var filePath in files)
                    {
                        string rel = Path.GetRelativePath(platformDir, filePath).Replace('\\', '/');
                        long size = new FileInfo(filePath).Length;
                        pm.files.Add(new BuildManifestFile { fileName = rel, sizeBytes = size });
                    }
                    // Stable ordering for deterministic comparisons / diffs.
                    pm.files.Sort((a, b) => string.CompareOrdinal(a.fileName, b.fileName));
                }
                manifest.platforms.Add(pm);
            }

            return manifest;
        }

        public static BuildManifest LoadBaseline(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            string path = ManifestPathForContent(contentId);
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                return JsonUtility.FromJson<BuildManifest>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BuildManifest] Failed to load baseline at {path}: {e.Message}");
                return null;
            }
        }

        public static void SaveBaseline(BuildManifest manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.contentId)) return;
            try
            {
                Directory.CreateDirectory(ManifestRoot);
                string path = ManifestPathForContent(manifest.contentId);
                string json = JsonUtility.ToJson(manifest, prettyPrint: true);
                File.WriteAllText(path, json);
                Debug.Log($"[BuildManifest] Saved baseline ({manifest.TotalFileCount} files, {FormatBytes(manifest.TotalBytes)}) → {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuildManifest] Failed to save baseline: {e}");
            }
        }

        public static void DeleteBaseline(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            try
            {
                string path = ManifestPathForContent(contentId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log($"[BuildManifest] Deleted baseline at {path}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuildManifest] Failed to delete baseline: {e}");
            }
        }

        // Compares a current manifest against a baseline. Filename equality
        // implies content equality for bundles (AppendHash naming embeds the
        // content hash in the filename). Catalog files always change and will
        // appear in the changed set — correct behavior.
        public static BuildManifestDiff Diff(BuildManifest baseline, BuildManifest current)
        {
            var diff = new BuildManifestDiff();
            if (current == null) return diff;

            foreach (var currPlatform in current.platforms)
            {
                var basePlatform = baseline?.GetPlatform(currPlatform.platform);
                var baseFiles = new Dictionary<string, long>();
                if (basePlatform?.files != null)
                {
                    foreach (var f in basePlatform.files)
                        baseFiles[f.fileName] = f.sizeBytes;
                }

                var pd = new PlatformDiff { platform = currPlatform.platform };
                var currFileNames = new HashSet<string>();

                foreach (var f in currPlatform.files)
                {
                    currFileNames.Add(f.fileName);
                    if (baseFiles.TryGetValue(f.fileName, out var baseSize) && baseSize == f.sizeBytes)
                    {
                        pd.unchangedFiles.Add(f.fileName);
                        pd.unchangedBytes += f.sizeBytes;
                    }
                    else
                    {
                        pd.changedFiles.Add(f.fileName);
                        pd.changedBytes += f.sizeBytes;
                    }
                }

                foreach (var kv in baseFiles)
                {
                    if (!currFileNames.Contains(kv.Key))
                        pd.removedFiles.Add(kv.Key);
                }

                pd.totalBytesCurrent = currPlatform.TotalBytes;
                diff.platforms.Add(pd);
            }

            return diff;
        }

        // Builds the skip-set ContentAPI.UploadContent uses to short-circuit
        // re-uploading unchanged bundles. Keys are "{platform}/{relativePath}"
        // so they can be matched against UploadContentData's platform + fileName.
        public static HashSet<string> BuildSkipSet(BuildManifestDiff diff)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (diff?.platforms == null) return set;
            foreach (var p in diff.platforms)
            {
                foreach (var f in p.unchangedFiles)
                    set.Add($"{p.platform}/{f}");
            }
            return set;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024d && u < units.Length - 1) { v /= 1024d; u++; }
            return $"{v:0.##} {units[u]}";
        }

        // Builds the compact summary that rides along on the commitUpload
        // request. Two figures per platform:
        //   - deltaUploaded: bytes actually uploaded for this version (= the
        //     patch size that the SDK uploader is shipping over the wire).
        //   - fullContent:   effective total content size at this version,
        //     including bundles that weren't uploaded because they were
        //     unchanged from a prior version (those get served via the
        //     addressables-routes version fallback).
        // dreampark-core's content manager reads these to display
        // "Total: X MB" and "Last patch: Y MB" on each version card.
        public static Defective.JSON.JSONObject BuildCommitSummary(
            BuildManifest current, BuildManifestDiff diff)
        {
            if (current == null) return null;

            var summary = new Defective.JSON.JSONObject();

            // fullContent — totals from the just-built manifest.
            var fullBlock = new Defective.JSON.JSONObject();
            fullBlock.AddField("totalBytes", current.TotalBytes);
            var fullByPlatform = new Defective.JSON.JSONObject();
            foreach (var p in current.platforms)
            {
                var pj = new Defective.JSON.JSONObject();
                pj.AddField("bytes", p.TotalBytes);
                pj.AddField("fileCount", p.FileCount);
                fullByPlatform.AddField(p.platform, pj);
            }
            fullBlock.AddField("byPlatform", fullByPlatform);
            summary.AddField("fullContent", fullBlock);

            // deltaUploaded — totals from the diff (fall back to "everything
            // is changed" when there's no baseline yet, which is correct for
            // first uploads).
            var deltaBlock = new Defective.JSON.JSONObject();
            var deltaByPlatform = new Defective.JSON.JSONObject();
            long deltaTotal = 0;
            if (diff != null && diff.platforms != null && diff.platforms.Count > 0)
            {
                foreach (var p in diff.platforms)
                {
                    var pj = new Defective.JSON.JSONObject();
                    pj.AddField("bytes", p.changedBytes);
                    pj.AddField("fileCount", p.changedFiles.Count);
                    deltaByPlatform.AddField(p.platform, pj);
                    deltaTotal += p.changedBytes;
                }
            }
            else
            {
                // No diff available — treat the full build as the patch.
                foreach (var p in current.platforms)
                {
                    var pj = new Defective.JSON.JSONObject();
                    pj.AddField("bytes", p.TotalBytes);
                    pj.AddField("fileCount", p.FileCount);
                    deltaByPlatform.AddField(p.platform, pj);
                    deltaTotal += p.TotalBytes;
                }
            }
            deltaBlock.AddField("totalBytes", deltaTotal);
            deltaBlock.AddField("byPlatform", deltaByPlatform);
            summary.AddField("deltaUploaded", deltaBlock);

            return summary;
        }
    }
}
#endif
