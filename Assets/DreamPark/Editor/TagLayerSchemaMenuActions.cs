#if UNITY_EDITOR
using System.IO;
using System.Linq;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    internal static class TagLayerSchemaMenuActions
    {
        [MenuItem("DreamPark/Sync Tags & Layers from Core", false, 110)]
        private static void RunTagLayerSyncNow()
        {
            if (!AuthAPI.isLoggedIn)
            {
                EditorUtility.DisplayDialog("Schema Sync", "Please log in first from DreamPark Content Uploader.", "OK");
                return;
            }

            try
            {
                var local = TagLayerSchemaSyncUtility.ReadLocalTagManager();
                if (IsCoreProject())
                {
                    PublishCoreSchema(local);
                }
                else
                {
                    SyncContentSchema(local);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TagLayerSchema] Manual sync failed: {ex.Message}");
                EditorUtility.DisplayDialog("Schema Sync Failed", ex.Message, "OK");
            }
        }

        private static void PublishCoreSchema(TagLayerSchemaSyncUtility.TagLayerSnapshot local)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string contentId = Path.GetFileName(projectRoot) ?? "dreampark-core";

            ContentAPI.PublishTagLayerSchemaFromCore(contentId, local.tags, local.layers, (success, response) =>
            {
                if (!success)
                {
                    string error = ExtractError(response);
                    Debug.LogError($"[TagLayerSchema] Core publish failed: {error}");
                    EditorUtility.DisplayDialog("Schema Publish Failed", error, "OK");
                    return;
                }

                int version = response?.json != null && response.json.HasField("schemaVersion")
                    ? response.json.GetField("schemaVersion").intValue
                    : 0;

                Debug.Log($"[TagLayerSchema] Core schema published manually. version={version}");
                EditorUtility.DisplayDialog("Schema Published", $"Canonical schema published (v{version}).", "OK");
            });
        }

        private static string GetGameFolderName()
        {
            string[] possibleContentPaths = Directory.GetDirectories("Assets", "Content", SearchOption.AllDirectories);
            foreach (string contentPath in possibleContentPaths)
            {
                var subdirs = Directory.GetDirectories(contentPath);
                if (subdirs.Length > 0)
                {
                    string folderName = Path.GetFileName(subdirs[0]);
                    if (!string.IsNullOrEmpty(folderName))
                        return folderName;
                }
            }
            return "YOUR_GAME_HERE";
        }

        public static string GetGamePrefix()
        {
            return Sanitize(GetGameFolderName());
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            return name.Replace("[", "").Replace("]", "").Trim();
        }

        private static void SyncContentSchema(TagLayerSchemaSyncUtility.TagLayerSnapshot local)
        {
            // Pure download: grab canonical schema from core and apply locally.
            // Any local additions the user has made are NOT sent up as a proposal
            // here — proposal filing is the Content Uploader's job (it happens
            // when the creator actually publishes their content).
            string contentId = GetGamePrefix();
            ContentAPI.GetTagLayerSchema((success, response) =>
            {
                if (!success)
                {
                    string error = ExtractError(response);
                    Debug.LogError($"[TagLayerSchema] Fetch failed: {error}");
                    EditorUtility.DisplayDialog("Schema Sync Failed", error, "OK");
                    return;
                }

                var canonical = ContentAPI.ParseTagLayerSchema(response);
                if (canonical == null || canonical.tags == null || canonical.layers == null)
                {
                    string msg = "Canonical schema response from core was empty or unrecognised.";
                    Debug.LogError("[TagLayerSchema] " + msg);
                    EditorUtility.DisplayDialog("Schema Sync Failed", msg, "OK");
                    return;
                }

                // Strict SDK sync mode:
                // 1) Remap prefab tag strings by slot changes
                // 2) Apply canonical TagManager exactly (preserving local extras)
                var targetTags = TagLayerSchemaSyncUtility.BuildTargetTagOrder(canonical.tags, local.tags, preserveLocalExtras: true);
                var remap = TagLayerSchemaSyncUtility.BuildTagRemapByIndex(local.tags, targetTags);
                var remapResult = TagLayerSchemaSyncUtility.RemapContentPrefabsByTagName(contentId, remap);
                if (remapResult.replacements > 0)
                {
                    Debug.Log($"[TagLayerSchema] Remapped prefab tags for {contentId}: {remapResult.replacements} replacements across {remapResult.filesChanged} prefabs.");
                }

                var apply = TagLayerSchemaSyncUtility.ApplyCanonicalSchema(canonical.tags, canonical.layers, preserveLocalExtras: true);
                if (!string.IsNullOrEmpty(apply.error))
                {
                    Debug.LogError($"[TagLayerSchema] Failed to apply canonical schema: {apply.error}");
                    EditorUtility.DisplayDialog("Schema Apply Failed", apply.error, "OK");
                    return;
                }

                bool schemaChangedLocally = remapResult.replacements > 0 || apply.changed;
                if (schemaChangedLocally)
                {
                    var refresh = TagLayerSchemaSyncUtility.ForceRefreshContentPrefabs(contentId);
                    if (refresh.prefabsProcessed > 0)
                    {
                        Debug.Log($"[TagLayerSchema] Force refreshed prefab imports for {contentId}: {refresh.prefabsReserialized}/{refresh.prefabsProcessed}");
                    }
                }

                int nonEmptyLayers = canonical.layers.Where(l => !string.IsNullOrEmpty(l)).Count();
                Debug.Log($"[TagLayerSchema] Canonical schema applied for {contentId}. version={canonical.version}, tags={canonical.tags.Count}, layers={nonEmptyLayers}");
                EditorUtility.DisplayDialog("Schema Sync Complete",
                    "Canonical tags & layers have been applied. " +
                    "Any new tags or layers you've added locally are still present — " +
                    "they'll be submitted as a proposal for core to review when you next upload content.",
                    "OK");
            });
        }

        private static bool IsCoreProject()
        {
            // Only dreampark-core has DREAMPARKCORE defined. The previous
            // folder-name fallback was unsafe — an SDK user whose project
            // happened to be called "dreampark-core" would have been routed
            // into the core-only publish path by mistake.
#if DREAMPARKCORE
            return true;
#else
            return false;
#endif
        }

        private static string ExtractError(DreamParkAPI.APIResponse response)
        {
            if (response?.json != null && response.json.HasField("error"))
            {
                return response.json.GetField("error").stringValue ?? "Unknown error";
            }
            return response?.error ?? "Unknown error";
        }
    }
}
#endif
