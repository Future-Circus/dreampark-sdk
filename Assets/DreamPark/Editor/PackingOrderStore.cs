#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Append-only packing order for SmartBundleGrouper's chunking pass.
    //
    // Why this exists
    // ---------------
    // The chunking pass bin-packs entries into ≤40 MB chunks. If we sort
    // entries alphabetically before bin-packing, inserting a new asset
    // alphabetically *early* would push every downstream entry into a
    // different chunk slot — every chunk after the insertion point gets
    // a new content hash and re-uploads. Bad for patch size.
    //
    // This store fixes that by tracking a stable, append-only ordering of
    // GUIDs per content title:
    //
    //   - First time we see a GUID, we append it to the order list. Its
    //     position in the list becomes its permanent sort index.
    //   - Every subsequent build sorts entries by their stored index.
    //   - When a GUID's asset is deleted, its index stays in the file but
    //     it just no longer shows up in any group's entry list. Slots
    //     don't get compacted/reused — that would cause downstream
    //     reshuffles.
    //
    // Net effect:
    //   - Adding a new asset → appended to the end of the order → lands
    //     in the last chunk (or spills into a new last chunk). No other
    //     chunk changes.
    //   - Removing an asset → its chunk shrinks. No other chunk changes.
    //   - Editing an asset → its chunk's bytes change. No other chunk
    //     changes.
    //
    // Why this file MUST be checked into version control
    // --------------------------------------------------
    // Bundle filenames are content-hashed from bundle composition. If two
    // devs on the same team have different packing orders for the same
    // content (one dev appended assets in order A, B, C; another in
    // order C, B, A), they produce different chunk compositions → different
    // bundle filenames → different patches. The CDN ends up with parallel
    // versions and patch-skip logic breaks.
    //
    // Storing the file under Assets/AddressableAssetsData/ means it gets
    // a .meta file and committed alongside content changes through normal
    // Unity asset workflow.
    public static class PackingOrderStore
    {
        // Schema version for forward-compat. Bump when the file shape changes
        // in a non-backwards-compatible way.
        private const int kSchemaVersion = 1;

        [Serializable]
        private class PackingOrderData
        {
            public int schemaVersion;
            public List<string> guids = new List<string>();
        }

        public static string PathForContent(string contentId)
        {
            return $"Assets/AddressableAssetsData/{contentId}-PackingOrder.json";
        }

        // Loads the packing order for a content title. Returns an empty list
        // if no file exists yet — caller can build the order from scratch on
        // first run.
        //
        // Defensive behaviors:
        //   - Refuses to load a file whose schemaVersion is HIGHER than this
        //     SDK supports — throws, prevents silently overwriting newer data
        //     with an older format. This protects devs on an older SDK branch
        //     from clobbering teammates' newer packing orders.
        //   - Deduplicates the GUID list on load, keeping first occurrence.
        //     A manually-edited file with duplicate entries would otherwise
        //     produce inconsistent index assignments (each duplicate would
        //     overwrite the earlier index in the lookup dict). Warns when
        //     dedup happens so devs know.
        //   - Drops empty / null GUIDs.
        public static List<string> Load(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return new List<string>();

            string path = PathForContent(contentId);
            string fullPath = ToFullPath(path);
            if (!File.Exists(fullPath)) return new List<string>();

            string json;
            PackingOrderData data;
            try
            {
                json = File.ReadAllText(fullPath);
                data = JsonUtility.FromJson<PackingOrderData>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PackingOrderStore] Failed to parse {fullPath}: {e.Message}. Starting from empty order — the next build will rebuild it.");
                return new List<string>();
            }

            if (data?.guids == null) return new List<string>();

            // Schema version check. JsonUtility doesn't throw on schema
            // mismatch — it silently returns a struct with default fields,
            // so a future-version file that adds new fields would parse OK
            // but might lose those fields on the next Save (we'd write back
            // in the old format). Refuse to load if the version is ahead of
            // ours; throwing here aborts the build rather than risking data
            // loss.
            if (data.schemaVersion > kSchemaVersion)
            {
                string msg =
                    $"[PackingOrderStore] {fullPath} is schema v{data.schemaVersion}, " +
                    $"this SDK only understands v{kSchemaVersion}. Update the SDK or revert the file. " +
                    "Aborting load to avoid overwriting newer data with an older format.";
                Debug.LogError(msg);
                throw new InvalidOperationException(msg);
            }

            // Dedup: keep first occurrence of each guid. Skips empty strings.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dedup = new List<string>(data.guids.Count);
            foreach (var guid in data.guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                if (seen.Add(guid)) dedup.Add(guid);
            }
            if (dedup.Count != data.guids.Count)
            {
                int dropped = data.guids.Count - dedup.Count;
                Debug.LogWarning(
                    $"[PackingOrderStore] {fullPath} contained {dropped} duplicate or empty GUID entries. " +
                    "Deduplicated on load (kept first occurrence). The next Save will overwrite the file cleanly.");
            }

            return dedup;
        }

        // Saves the order. Always overwrites — entries are append-only at
        // the caller's discretion.
        public static void Save(string contentId, List<string> orderedGuids)
        {
            if (string.IsNullOrEmpty(contentId) || orderedGuids == null) return;

            string path = PathForContent(contentId);
            string fullPath = ToFullPath(path);

            try
            {
                var data = new PackingOrderData
                {
                    schemaVersion = kSchemaVersion,
                    guids = orderedGuids,
                };
                string json = JsonUtility.ToJson(data, prettyPrint: true);

                // Skip the write if the on-disk content already matches — avoids
                // dirtying the asset file (and triggering a Unity re-import) on
                // every grouper pass that didn't actually change the order.
                if (File.Exists(fullPath))
                {
                    string existing = File.ReadAllText(fullPath);
                    if (existing == json) return;
                }

                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(fullPath, json);

                // Tell Unity to pick up the change so the asset / .meta stay
                // in sync with disk. This is cheap (one file).
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PackingOrderStore] Failed to write {fullPath}: {e}");
            }
        }

        private static string ToFullPath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, assetPath);
        }
    }
}
#endif
