#if !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DreamPark
{
    // Reads/writes per-prefab PreviewSettings overrides for a content folder.
    //
    // Storage: a single JSON file per content package at
    //   Assets/Content/{contentId}/Previews/.preview-overrides.json
    // keyed by prefab name (the same name used for Previews/{name}.png).
    //
    // Why a dot-prefixed file: Unity's asset pipeline ignores files and
    // folders whose name starts with a dot. That means no .meta is emitted,
    // no GUID is minted, and the file is NEVER swept into an addressable
    // bundle — it's pure author-time metadata that drives PNG generation and
    // has no business shipping to the runtime. It is still an ordinary file
    // on disk, so git tracks it and the whole team shares the same overrides.
    //
    // All access goes through System.IO (not AssetDatabase) precisely because
    // the file is invisible to AssetDatabase.
    public static class PreviewMetadataStore
    {
        [Serializable]
        private struct Entry
        {
            public string name;
            public PreviewSettings settings;
        }

        [Serializable]
        private class FileModel
        {
            public int version = 1;
            public List<Entry> entries = new List<Entry>();
        }

        private const int kCurrentVersion = 1;
        private const string kFileName = ".preview-overrides.json";

        public static string PathFor(string contentId)
            => $"Assets/Content/{contentId}/Previews/{kFileName}";

        // True if an explicit override exists for this prefab (as opposed to
        // falling back to PreviewSettings.Default).
        public static bool Has(string contentId, string prefabName)
            => TryGet(contentId, prefabName, out _);

        public static bool TryGet(string contentId, string prefabName, out PreviewSettings settings)
        {
            var model = Load(contentId);
            for (int i = 0; i < model.entries.Count; i++)
            {
                if (model.entries[i].name == prefabName)
                {
                    settings = model.entries[i].settings.Sanitized();
                    return true;
                }
            }
            settings = PreviewSettings.Default;
            return false;
        }

        // The settings the renderer should use for this prefab — the stored
        // override if one exists, otherwise the angle-locked default.
        public static PreviewSettings GetOrDefault(string contentId, string prefabName)
            => TryGet(contentId, prefabName, out var s) ? s : PreviewSettings.Default;

        public static void Set(string contentId, string prefabName, PreviewSettings settings)
        {
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(prefabName)) return;

            var model = Load(contentId);
            var sanitized = settings.Sanitized();
            bool found = false;
            for (int i = 0; i < model.entries.Count; i++)
            {
                if (model.entries[i].name == prefabName)
                {
                    model.entries[i] = new Entry { name = prefabName, settings = sanitized };
                    found = true;
                    break;
                }
            }
            if (!found)
                model.entries.Add(new Entry { name = prefabName, settings = sanitized });

            Save(contentId, model);
        }

        // Removes the override for a prefab so it falls back to default
        // framing on the next batch. No-op (and no rewrite) if none exists.
        public static void Clear(string contentId, string prefabName)
        {
            var model = Load(contentId);
            int removed = model.entries.RemoveAll(e => e.name == prefabName);
            if (removed > 0) Save(contentId, model);
        }

        private static FileModel Load(string contentId)
        {
            try
            {
                string path = PathFor(contentId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var model = JsonUtility.FromJson<FileModel>(json);
                    if (model != null)
                    {
                        model.entries ??= new List<Entry>();
                        return model;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PreviewMetadataStore] Failed to read overrides for '{contentId}': {e.Message}");
            }
            return new FileModel();
        }

        private static void Save(string contentId, FileModel model)
        {
            try
            {
                model.version = kCurrentVersion;
                string path = PathFor(contentId);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonUtility.ToJson(model, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[PreviewMetadataStore] Failed to write overrides for '{contentId}': {e.Message}");
            }
        }
    }
}
#endif
