#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Defective.JSON;

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
    // The manifest baseline is the snapshot we diff against when estimating
    // or shipping a patch. We still cache the most recent successful local
    // upload under:
    //   <ProjectRoot>/Library/DreamParkBuildManifests/{contentId}.json
    // but Smart patching prefers the latest backend version metadata when it
    // is available, so cross-machine uploads compare against the same parent.

    [Serializable]
    public class BuildManifestFile
    {
        // Path relative to ServerData/{platform}/, forward-slash normalized.
        public string fileName;
        public long sizeBytes;

        // Hex MD5 of the file's actual bytes. This is the authoritative
        // change signal — filename+size is NOT sufficient because the
        // AppendHash bundle name is Unity's *content* Hash128 (asset graph),
        // not a hash of the compiled file. Two builds of the same assets
        // (e.g. legacy vs smart packer, or a Unity version bump) can produce
        // the SAME filename/hash but DIFFERENT bytes. Diffing on md5 is what
        // stops the patcher from skip-uploading a byte-changed bundle.
        // Empty when unknown (legacy baseline without md5) → Diff falls back
        // to the size heuristic for that entry only.
        public string md5;
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
                foreach (var f in files)
                    if (f != null && f.sizeBytes > 0) sum += f.sizeBytes;
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
        private const long UnknownFileSize = -1;

        private static bool ShouldAlwaysUploadInPatch(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string ext = Path.GetExtension(fileName);
            return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".hash", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractRelativeBackendFilename(string contentId, int versionNumber, string platform, string catalogPath)
        {
            if (string.IsNullOrEmpty(catalogPath) || string.IsNullOrEmpty(platform))
                return null;

            string expectedPrefix = $"addressables/{contentId}/{versionNumber}/{platform}/";
            if (catalogPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return catalogPath.Substring(expectedPrefix.Length);

            string platformMarker = $"/{platform}/";
            int platformIdx = catalogPath.IndexOf(platformMarker, StringComparison.Ordinal);
            if (platformIdx >= 0)
                return catalogPath.Substring(platformIdx + platformMarker.Length);

            return catalogPath.Replace('\\', '/');
        }

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
                        // Compute md5 only for bundles — catalog_*.json/.hash are in
                        // ShouldAlwaysUploadInPatch and never skip, so their md5 is
                        // irrelevant and we skip the hashing cost.
                        string md5 = ShouldAlwaysUploadInPatch(rel) ? null : ComputeFileMd5(filePath);
                        pm.files.Add(new BuildManifestFile { fileName = rel, sizeBytes = size, md5 = md5 });
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

        public static BuildManifest BuildFromBackendVersion(string contentId, JSONObject versionJson)
        {
            if (string.IsNullOrEmpty(contentId) || versionJson == null || versionJson.type != JSONObject.Type.Object)
                return null;

            int versionNumber = versionJson.HasField("versionNumber")
                ? versionJson.GetField("versionNumber").intValue
                : 0;

            var manifest = new BuildManifest
            {
                contentId = contentId,
                versionNumber = versionNumber,
                buildTimestampUtc =
                    versionJson.GetField("createdAt")?.stringValue
                    ?? versionJson.GetField("uploadedAt")?.stringValue
                    ?? versionJson.GetField("updatedAt")?.stringValue
                    ?? string.Empty,
                sdkVersion = versionJson.GetField("sdkVersion")?.stringValue ?? string.Empty,
            };

            var sizeLookup = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            var md5Lookup = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var manifestJson = versionJson.GetField("manifest");
            var filesArr = manifestJson?.GetField("files");
            if (filesArr != null && filesArr.type == JSONObject.Type.Array && filesArr.list != null)
            {
                foreach (var fileNode in filesArr.list)
                {
                    if (fileNode == null || fileNode.type != JSONObject.Type.Object) continue;
                    string platform = fileNode.GetField("platform")?.stringValue;
                    string filename = fileNode.GetField("filename")?.stringValue;
                    if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(filename)) continue;

                    long size = UnknownFileSize;
                    var sizeNode = fileNode.GetField("size");
                    if (sizeNode != null && sizeNode.type == JSONObject.Type.Number)
                        size = sizeNode.longValue;

                    if (!sizeLookup.TryGetValue(platform, out var byFile))
                    {
                        byFile = new Dictionary<string, long>(StringComparer.Ordinal);
                        sizeLookup[platform] = byFile;
                    }
                    byFile[filename] = size;

                    // Authoritative server md5 (accept "md5" or legacy "hash" field).
                    // This is what makes the diff a true md5-vs-server comparison.
                    string md5 = fileNode.GetField("md5")?.stringValue
                                 ?? fileNode.GetField("hash")?.stringValue;
                    if (!string.IsNullOrEmpty(md5))
                    {
                        if (!md5Lookup.TryGetValue(platform, out var byFileMd5))
                        {
                            byFileMd5 = new Dictionary<string, string>(StringComparer.Ordinal);
                            md5Lookup[platform] = byFileMd5;
                        }
                        byFileMd5[filename] = md5.ToLowerInvariant();
                    }
                }
            }

            var catalog = versionJson.GetField("catalog");
            if (catalog != null && catalog.type == JSONObject.Type.Object && catalog.keys != null)
            {
                foreach (string platform in catalog.keys)
                {
                    var platformManifest = new BuildManifestPlatform { platform = platform };
                    var pathsArr = catalog.GetField(platform);
                    if (pathsArr != null && pathsArr.type == JSONObject.Type.Array && pathsArr.list != null)
                    {
                        foreach (var pathNode in pathsArr.list)
                        {
                            string catalogPath = pathNode?.stringValue;
                            string relativeFileName = ExtractRelativeBackendFilename(contentId, versionNumber, platform, catalogPath);
                            if (string.IsNullOrEmpty(relativeFileName)) continue;

                            long size = UnknownFileSize;
                            if (sizeLookup.TryGetValue(platform, out var byFile)
                                && byFile.TryGetValue(relativeFileName, out long knownSize))
                            {
                                size = knownSize;
                            }

                            string md5 = null;
                            if (md5Lookup.TryGetValue(platform, out var byFileMd5))
                                byFileMd5.TryGetValue(relativeFileName, out md5);

                            platformManifest.files.Add(new BuildManifestFile
                            {
                                fileName = relativeFileName,
                                sizeBytes = size,
                                md5 = md5,
                            });
                        }
                    }

                    platformManifest.files.Sort((a, b) => string.CompareOrdinal(a.fileName, b.fileName));
                    manifest.platforms.Add(platformManifest);
                }
            }

            return manifest.platforms.Count > 0 ? manifest : null;
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
                var baseFiles = new Dictionary<string, BuildManifestFile>();
                if (basePlatform?.files != null)
                {
                    foreach (var f in basePlatform.files)
                        baseFiles[f.fileName] = f;
                }

                var pd = new PlatformDiff { platform = currPlatform.platform };
                var currFileNames = new HashSet<string>();

                foreach (var f in currPlatform.files)
                {
                    currFileNames.Add(f.fileName);
                    bool alwaysUpload = ShouldAlwaysUploadInPatch(f.fileName);
                    bool filenameMatch = baseFiles.TryGetValue(f.fileName, out var baseFile);

                    // Decide "unchanged" by CONTENT, not by filename.
                    //   - If both sides have an md5 → that's the authoritative
                    //     check (catches same-name/same-AppendHash/different-bytes,
                    //     which filename+size cannot).
                    //   - If md5 is missing on either side (legacy baseline that
                    //     predates md5 capture) → fall back to the old size test
                    //     so we don't needlessly re-upload everything once.
                    bool unchanged = false;
                    if (!alwaysUpload && filenameMatch)
                    {
                        bool haveBothMd5 = !string.IsNullOrEmpty(f.md5)
                                           && !string.IsNullOrEmpty(baseFile.md5);
                        if (haveBothMd5)
                        {
                            unchanged = string.Equals(f.md5, baseFile.md5, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            unchanged = baseFile.sizeBytes == UnknownFileSize
                                        || baseFile.sizeBytes == f.sizeBytes;
                        }
                    }

                    if (unchanged)
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

        // Hex MD5 of a file's bytes. Matches the GCS object md5 the backend
        // exposes (Buffer.from(metadata.md5Hash,'base64').toString('hex')), so
        // the server baseline and the local build are directly comparable.
        public static string ComputeFileMd5(string filePath)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BuildManifest] Could not md5 {filePath}: {e.Message}");
                return null;
            }
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

            // Per-bundle metadata (size + md5) for every built bundle, so the
            // backend can bake it into resolved-bundles.json. That makes the
            // artifact a fully self-contained static file the patch uploader can
            // diff against with zero GCS metadata reads. Catalog files have no
            // md5 (they always re-upload) and are skipped.
            var bundleMetadata = new Defective.JSON.JSONObject();
            foreach (var p in current.platforms)
            {
                var platformObj = new Defective.JSON.JSONObject();
                int n = 0;
                foreach (var f in p.files)
                {
                    if (f == null || string.IsNullOrEmpty(f.fileName) || string.IsNullOrEmpty(f.md5)) continue;
                    var fileObj = new Defective.JSON.JSONObject();
                    fileObj.AddField("size", f.sizeBytes);
                    fileObj.AddField("md5", f.md5);
                    platformObj.AddField(f.fileName, fileObj);
                    n++;
                }
                if (n > 0) bundleMetadata.AddField(p.platform, platformObj);
            }
            summary.AddField("bundleMetadata", bundleMetadata);

            return summary;
        }
    }
}
#endif
