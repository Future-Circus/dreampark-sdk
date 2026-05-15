#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DreamPark
{
    // Persistent record of the most recent *failed* upload run for a given
    // contentId. Powers the "Reupload All vs. Upload Failed Bundles" dialog
    // on the Try Reupload button: when the user clicks Try Reupload after a
    // partial failure, we want to skip re-sending the bundles that already
    // landed in Storage during the previous run and only retry the ones that
    // didn't.
    //
    // Two file lists are persisted:
    //   - succeeded: every (platform, fileName, uploadPath) that *did* upload
    //     in the failed run. uploadPath is the storage key the backend hands
    //     back from /uploadUrl; it has to be replayed into commitUpload's
    //     uploadedFiles map on a Failed-Only retry so the resulting version
    //     references the full set (succeeded-then + succeeded-now), not just
    //     the bundles uploaded in this retry.
    //   - failed: every (platform, fileName) that exhausted its 3 PUT retries.
    //     These are the ones we'll actually re-send on a Failed-Only retry.
    //
    // Lives in Scripts/Core/ (not Editor/) so ContentAPI — which is part of
    // the runtime assembly Assembly-CSharp — can reference these types from
    // inside its own #if UNITY_EDITOR guards. The whole file is gated on
    // UNITY_EDITOR so it compiles out cleanly in player builds; the
    // !DREAMPARKCORE side keeps it out of dreampark-core editor builds where
    // ContentUploaderPanel doesn't compile either.
    //
    // Stored alongside the BuildManifest baseline so a future "blow away
    // everything for this content" tool only has to wipe one directory:
    //   <ProjectRoot>/Library/DreamParkBuildManifests/{contentId}.failed.json
    // The path is duplicated here (rather than reusing BuildManifestStore.
    // ManifestRoot) because BuildManifestStore lives in the editor assembly
    // and the runtime assembly can't reach across the boundary.
    //
    // The store is cleared at the start of every upload run so a stale entry
    // from a long-ago failure can't ambush a fresh run, and on every
    // successful commit so the panel knows there's nothing to retry.

    [Serializable]
    public class FailedBundleEntry
    {
        public string platform;
        public string fileName;
        // Only populated for entries in `succeeded`. The upload key the
        // backend assigned via /uploadUrl — we replay it into commitUpload
        // on a Failed-Only retry so the resulting version references the
        // bundles that were uploaded in the previous run.
        public string uploadPath;
    }

    [Serializable]
    public class FailedBundleRecord
    {
        public int schemaVersion = 1;
        public string contentId;
        public string failedAtUtc;          // ISO-8601, for "X hours ago" display.
        public int totalFiles;              // Total file count of the failed run (for the dialog).
        public List<FailedBundleEntry> succeeded = new List<FailedBundleEntry>();
        public List<FailedBundleEntry> failed = new List<FailedBundleEntry>();

        public int FailedCount => failed != null ? failed.Count : 0;
        public int SucceededCount => succeeded != null ? succeeded.Count : 0;

        // True when there's something to retry. An empty failed list means
        // either a fresh run with no failures (cleared) or the data is
        // corrupt; either way, the dialog should not appear.
        public bool HasRetryableFailures => FailedCount > 0;
    }

    public static class FailedBundleStore
    {
        // Mirrors BuildManifestStore.ManifestRoot. Duplicated rather than
        // referenced because BuildManifestStore is in the editor assembly
        // and this class compiles into the runtime assembly. If you ever
        // change the manifests directory, change it in both places.
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestRoot =>
            Path.Combine(ProjectRoot, "Library", "DreamParkBuildManifests");

        public static string PathFor(string contentId)
        {
            return Path.Combine(ManifestRoot, $"{contentId}.failed.json");
        }

        public static FailedBundleRecord Load(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return null;
            string path = PathFor(contentId);
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                var record = JsonUtility.FromJson<FailedBundleRecord>(json);
                // Schema-mismatched / partially-deserialized records are
                // treated as missing — better to fall back to the existing
                // Reupload All path than to drive a Failed-Only retry off
                // garbage data.
                if (record == null || string.IsNullOrEmpty(record.contentId))
                    return null;
                return record;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FailedBundleStore] Failed to read {path}: {e.Message}. Treating as no record.");
                return null;
            }
        }

        public static void Save(FailedBundleRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.contentId)) return;
            try
            {
                Directory.CreateDirectory(ManifestRoot);
                string path = PathFor(record.contentId);
                string json = JsonUtility.ToJson(record, prettyPrint: true);
                File.WriteAllText(path, json);
                Debug.Log(
                    $"[FailedBundleStore] Recorded failed run for '{record.contentId}': " +
                    $"{record.FailedCount} failed, {record.SucceededCount} succeeded → {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[FailedBundleStore] Failed to save record: {e}");
            }
        }

        public static void Clear(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            try
            {
                string path = PathFor(contentId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log($"[FailedBundleStore] Cleared failed-run record at {path}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FailedBundleStore] Failed to delete {PathFor(contentId)}: {e.Message}");
            }
        }

        // Builds a skip-set for ContentAPI.UploadContent that retains *only*
        // the failed bundles. The skip-set semantic is "files NOT to upload",
        // so this returns "every file in the current ServerData EXCEPT the
        // failed ones." Pass it as ContentAPI.UploadContent's skipFileKeys.
        //
        // currentFileKeys: the full set of "{platform}/{fileName}" keys that
        // are presently in ServerData/. failedKeysToRetry: the keys the user
        // has chosen to retry (typically record.failed converted to keys,
        // then intersected with what's actually in ServerData so a stale
        // failed entry that no longer exists on disk is silently dropped).
        public static HashSet<string> BuildSkipSetForFailedOnly(
            IEnumerable<string> currentFileKeys, HashSet<string> failedKeysToRetry)
        {
            var skip = new HashSet<string>(StringComparer.Ordinal);
            if (currentFileKeys == null) return skip;
            if (failedKeysToRetry == null || failedKeysToRetry.Count == 0)
            {
                // No specific files to retry — skip everything (the upload
                // will end up with zero files and ContentAPI's "no changed
                // files" path will short-circuit cleanly).
                foreach (var k in currentFileKeys) skip.Add(k);
                return skip;
            }
            foreach (var k in currentFileKeys)
            {
                if (!failedKeysToRetry.Contains(k))
                    skip.Add(k);
            }
            return skip;
        }

        // Convenience for converting a record's failed entries into the
        // "{platform}/{fileName}" key format ContentAPI uses.
        public static HashSet<string> FailedKeys(FailedBundleRecord record)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (record?.failed == null) return set;
            foreach (var e in record.failed)
            {
                if (string.IsNullOrEmpty(e.platform) || string.IsNullOrEmpty(e.fileName)) continue;
                set.Add($"{e.platform}/{e.fileName}");
            }
            return set;
        }

        // Convenience for replaying the previously-succeeded uploadPaths
        // into commitUpload's uploadedFiles map. Returned shape matches what
        // ContentAPI.UploadFlowAsync builds locally so the merge is a one-
        // liner on the consuming side.
        public static Dictionary<string, List<string>> SucceededUploadPathsByPlatform(FailedBundleRecord record)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (record?.succeeded == null) return map;
            foreach (var e in record.succeeded)
            {
                if (string.IsNullOrEmpty(e.platform) || string.IsNullOrEmpty(e.uploadPath)) continue;
                if (!map.TryGetValue(e.platform, out var list))
                {
                    list = new List<string>();
                    map[e.platform] = list;
                }
                list.Add(e.uploadPath);
            }
            return map;
        }
    }
}
#endif
