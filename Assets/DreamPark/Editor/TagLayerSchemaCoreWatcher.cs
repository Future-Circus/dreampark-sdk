#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using DreamPark.API;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    [InitializeOnLoad]
    internal static class TagLayerSchemaCoreWatcher
    {
        private static FileSystemWatcher _watcher;
        private static bool _publishQueued;

        static TagLayerSchemaCoreWatcher()
        {
            if (!IsCoreProject())
            {
                return;
            }

            SetupWatcher();
            // Publish once on startup so backend catches manual edits done while watcher was offline.
            QueuePublish("startup");
        }

        // Only register the menu item when building/running inside dreampark-core.
        // The SDK distribution doesn't define DREAMPARKCORE, so this menu vanishes there.
#if DREAMPARKCORE
        [MenuItem("DreamPark/Publish Core Tag-Layer Schema", false, 111)]
#endif
        private static void PublishCoreSchemaMenu()
        {
            if (!IsCoreProject())
            {
                EditorUtility.DisplayDialog("Not Core Project", "Schema publish is only enabled in dreampark-core.", "OK");
                return;
            }
            QueuePublish("manual");
        }

        private static bool IsCoreProject()
        {
#if DREAMPARKCORE
            return true;
#else
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string projectName = Path.GetFileName(projectRoot)?.ToLowerInvariant() ?? "";
            return projectName == "dreampark-core";
#endif
        }

        private static void SetupWatcher()
        {
            string tagManagerPath = TagLayerSchemaSyncUtility.TagManagerPath;
            string folder = Path.GetDirectoryName(tagManagerPath);
            string file = Path.GetFileName(tagManagerPath);

            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(file) || !Directory.Exists(folder))
            {
                Debug.LogWarning("[TagLayerSchema] Could not start core watcher: TagManager path missing.");
                return;
            }

            _watcher = new FileSystemWatcher(folder, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += (_, __) => QueuePublish("file-changed");
            _watcher.Created += (_, __) => QueuePublish("file-created");
            _watcher.Renamed += (_, __) => QueuePublish("file-renamed");
            _watcher.Deleted += (_, __) => QueuePublish("file-deleted");
        }

        private static void QueuePublish(string reason)
        {
            if (_publishQueued)
            {
                return;
            }
            _publishQueued = true;
            EditorApplication.delayCall += () =>
            {
                _publishQueued = false;
                PublishSchema(reason);
            };
        }

        private static void PublishSchema(string reason)
        {
            if (!AuthAPI.isLoggedIn)
            {
                Debug.Log("[TagLayerSchema] Core watcher skipped publish because no editor login session is available.");
                return;
            }

            try
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string projectId = Path.GetFileName(projectRoot) ?? "dreampark-core";
                var local = TagLayerSchemaSyncUtility.ReadLocalTagManager();
                var tags = local.tags;
                var layers = local.layers;

                ContentAPI.PublishTagLayerSchemaFromCore(projectId, tags, layers, (success, response) =>
                {
                    if (!success)
                    {
                        Debug.LogError($"[TagLayerSchema] Core publish failed ({reason}): {response?.error}");
                        return;
                    }

                    int schemaVersion = 0;
                    if (response?.json != null && response.json.HasField("schemaVersion"))
                    {
                        schemaVersion = response.json.GetField("schemaVersion").intValue;
                    }

                    Debug.Log($"[TagLayerSchema] Core schema published ({reason}). version={schemaVersion}, tags={tags.Count}, layers={layers.Count(l => !string.IsNullOrEmpty(l))}");
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TagLayerSchema] Core publish exception ({reason}): {ex.Message}");
            }
        }
    }
}
#endif
