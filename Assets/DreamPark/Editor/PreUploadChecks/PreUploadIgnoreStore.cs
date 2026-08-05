#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks
{
    // Per-content-package record of "we looked at this finding and it's fine".
    //
    // Stored at Assets/Content/{contentId}/.preupload-ignores.json
    //
    // Why a dot-prefixed file, copied wholesale from PreviewMetadataStore's rationale:
    // Unity's asset pipeline ignores files and folders whose name starts with a dot.
    // No .meta is emitted, no GUID is minted, and the file can NEVER be swept into an
    // addressable bundle — it is pure author-time metadata with no business shipping
    // to the runtime. It is still an ordinary file on disk, so git tracks it and the
    // whole team shares one triage decision instead of each person re-triaging the
    // same false positive. All access goes through System.IO, not AssetDatabase,
    // precisely because the file is invisible to AssetDatabase.
    //
    // Keyed by GUID rather than asset path so an ignore survives a move or rename —
    // the two operations most likely to happen between someone triaging a finding and
    // someone else hitting upload. assetPath rides along for diff legibility only.
    //
    // Blocking findings CAN be ignored. That is deliberate: making them un-ignorable
    // produces worse workarounds than the finding itself (people disable the check,
    // or stop using the uploader). Instead, ignoring requires a typed reason and the
    // record lands in a git-tracked file where a reviewer can see it.
    public static class PreUploadIgnoreStore
    {
        private const int kCurrentVersion = 1;
        private const string kFileName = ".preupload-ignores.json";

        [Serializable]
        public struct Entry
        {
            public string checkId;
            public string assetGuid;
            public string assetPath;    // human-readable; identity is the GUID
            public string subKey;
            public string reason;
            public string ignoredBy;
            public string ignoredAtUtc;
        }

        [Serializable]
        private class FileModel
        {
            public int version = kCurrentVersion;
            public List<Entry> entries = new List<Entry>();
        }

        public static string PathFor(string contentId)
        {
            return $"{ContentRootScanner.ContentFolder}/{contentId}/{kFileName}";
        }

        public static bool IsIgnored(string contentId, string checkId, string assetGuid, string subKey)
        {
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(checkId)) return false;

            var model = Load(contentId);
            return model.entries.Any(e => Matches(e, checkId, assetGuid, subKey));
        }

        public static void Ignore(string contentId, string checkId, string assetGuid,
                                  string assetPath, string subKey, string reason)
        {
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(checkId)) return;

            var model = Load(contentId);
            if (model.entries.Any(e => Matches(e, checkId, assetGuid, subKey))) return;

            model.entries.Add(new Entry
            {
                checkId = checkId,
                assetGuid = assetGuid ?? "",
                assetPath = assetPath ?? "",
                subKey = subKey ?? "",
                reason = reason ?? "",
                ignoredBy = SafeUserName(),
                ignoredAtUtc = DateTime.UtcNow.ToString("o"),
            });

            Save(contentId, model);
        }

        public static void Unignore(string contentId, string checkId, string assetGuid, string subKey)
        {
            if (string.IsNullOrEmpty(contentId)) return;

            var model = Load(contentId);
            int removed = model.entries.RemoveAll(e => Matches(e, checkId, assetGuid, subKey));

            // Nothing to do and nothing to rewrite. Storing a no-op is just churn in
            // someone's diff — same rule PreviewMetadataStore.Clear follows.
            if (removed == 0) return;

            Save(contentId, model);
        }

        public static IReadOnlyList<Entry> All(string contentId)
        {
            return Load(contentId).entries;
        }

        // Drops entries whose asset no longer exists. Called after a successful save
        // so the file doesn't accumulate records for deleted prefabs forever.
        public static int PruneMissing(string contentId)
        {
            var model = Load(contentId);
            int before = model.entries.Count;

            model.entries.RemoveAll(e =>
                !string.IsNullOrEmpty(e.assetGuid) &&
                string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(e.assetGuid)));

            int pruned = before - model.entries.Count;
            if (pruned > 0)
            {
                Debug.Log($"[DreamPark] Pruned {pruned} pre-upload ignore(s) whose asset no longer exists.");
                Save(contentId, model);
            }
            return pruned;
        }

        private static bool Matches(Entry e, string checkId, string assetGuid, string subKey)
        {
            return string.Equals(e.checkId, checkId, StringComparison.Ordinal)
                && string.Equals(e.assetGuid ?? "", assetGuid ?? "", StringComparison.Ordinal)
                && string.Equals(e.subKey ?? "", subKey ?? "", StringComparison.Ordinal);
        }

        private static string SafeUserName()
        {
            try
            {
                // Best effort only — this is a convenience field in a diff, never an
                // identity claim, so a null here must not cost us the whole write.
                //
                // global:: is required. dreampark-core declares `class DreamPark`
                // INSIDE `namespace DreamPark`, so an unqualified `DreamPark.API.…`
                // from inside namespace DreamPark.PreUploadChecks resolves against
                // that class and fails to compile once this file is synced to core.
                string name = global::DreamPark.API.AuthAPI.displayName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return Environment.UserName ?? "";
        }

        private static FileModel Load(string contentId)
        {
            string path = PathFor(contentId);
            try
            {
                if (!File.Exists(path)) return new FileModel();

                string json = File.ReadAllText(path);
                var model = JsonUtility.FromJson<FileModel>(json);
                if (model == null) return new FileModel();
                if (model.entries == null) model.entries = new List<Entry>();

                if (model.version > kCurrentVersion)
                {
                    // Written by a newer SDK. Read what we can rather than refusing —
                    // an ignore list is advisory, and losing it just means someone
                    // re-triages. (PackingOrderStore takes the stricter line and
                    // throws, because a mis-read packing order silently changes what
                    // ships. Different stakes, different call.)
                    Debug.LogWarning(
                        $"[DreamPark] {path} was written by a newer SDK (schema v{model.version}). " +
                        "Reading it anyway; update the SDK if ignores behave oddly.");
                }

                return model;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read {path}: {e.Message}. Treating as empty.");
                return new FileModel();
            }
        }

        private static void Save(string contentId, FileModel model)
        {
            string path = PathFor(contentId);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                model.version = kCurrentVersion;

                // Stable ordering so the file produces readable diffs instead of
                // reshuffling every time someone ignores something.
                model.entries = model.entries
                    .OrderBy(e => e.checkId, StringComparer.Ordinal)
                    .ThenBy(e => e.assetPath, StringComparer.Ordinal)
                    .ThenBy(e => e.subKey, StringComparer.Ordinal)
                    .ToList();

                File.WriteAllText(path, JsonUtility.ToJson(model, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not write {path}: {e.Message}");
            }
        }
    }
}
#endif
