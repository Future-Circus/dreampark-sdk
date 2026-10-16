#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// <summary>
    /// The package-wide, optional Phone Player slot.
    ///
    /// Stored as `Assets/Content/{id}/.dreampark-phone-player.json`
    /// `{ "schemaVersion": 1, "prefabGuid": "&lt;guid or empty&gt;" }` — a
    /// committed authoring file like the other `.dreampark-*.json` stores. It
    /// is one slot for the whole title, shared by Arena, Sequence and
    /// Adventure exactly like Player, so it gets its own file rather than a
    /// field in any one package's store.
    ///
    /// Empty (the default): phone users get no rig — they watch and still get
    /// the Game Manager. Set: phones spawn that PlayerRig prefab instead of
    /// Player. Headsets always use Player.
    ///
    /// The template ships `Prefabs/Phone Player.prefab`, but the slot stays
    /// empty until a creator opts in. A prefab named "Phone Player" or the
    /// slot prefab is NEVER picked as the headset Player
    /// (<see cref="IsPhonePlayerPrefab"/>).
    /// </summary>
    public static class PhonePlayerStore
    {
        public const int CurrentVersion = 1;
        public const string FileName = ".dreampark-phone-player.json";
        public const string TemplatePrefabName = "Phone Player";

        [Serializable]
        public sealed class Data
        {
            public int schemaVersion = CurrentVersion;
            public string prefabGuid = "";
        }

        public static string AssetPath(string contentId) => $"Assets/Content/{contentId}/{FileName}";

        /// <summary>Where the SDK template keeps its default phone rig.</summary>
        public static string TemplatePrefabPath(string contentId)
            => $"Assets/Content/{contentId}/Prefabs/{TemplatePrefabName}.prefab";

        static string FullPath(string contentId)
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", AssetPath(contentId)));

        public static Data Load(string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return new Data();
            string path = FullPath(contentId);
            if (!File.Exists(path)) return new Data();
            try
            {
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(path)) ?? new Data();
                if (data.schemaVersion > CurrentVersion)
                    Debug.LogWarning($"[PhonePlayerStore] {AssetPath(contentId)} has schema {data.schemaVersion}, newer than this SDK ({CurrentVersion}). Reading what it understands.");
                if (data.prefabGuid == null) data.prefabGuid = "";
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PhonePlayerStore] Could not read {AssetPath(contentId)}: {e.Message}");
                return new Data();
            }
        }

        /// <summary>The slot prefab's GUID, or "" when the slot is empty.</summary>
        public static string LoadGuid(string contentId) => Load(contentId).prefabGuid ?? "";

        /// <summary>Set (or clear, with null/"") the slot. Writes only on change.</summary>
        public static void Save(string contentId, string prefabGuid)
        {
            if (string.IsNullOrEmpty(contentId)) return;
            prefabGuid = prefabGuid ?? "";
            string path = FullPath(contentId);
            if (!File.Exists(path) && prefabGuid.Length == 0) return;   // empty is the default; no file needed
            var data = new Data { schemaVersion = CurrentVersion, prefabGuid = prefabGuid };
            string json = JsonUtility.ToJson(data, true) + "\n";
            if (File.Exists(path) && File.ReadAllText(path) == json) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
        }

        /// <summary>The slot prefab's asset path, or null when empty or missing.</summary>
        public static string SlotPrefabPath(string contentId)
        {
            string guid = LoadGuid(contentId);
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// True for a prefab that must never be chosen as the headset Player:
        /// the slot prefab, or anything named "Phone Player".
        /// </summary>
        public static bool IsPhonePlayerPrefab(string contentId, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (string.Equals(Path.GetFileNameWithoutExtension(assetPath), TemplatePrefabName,
                    StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrEmpty(contentId)) return false;
            string slot = SlotPrefabPath(contentId);
            return slot != null && string.Equals(slot, assetPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Why <paramref name="assetPath"/> cannot fill the slot of
        /// <paramref name="contentId"/>, or null when it can.
        /// </summary>
        public static string ValidateCandidate(string contentId, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "the prefab no longer exists";
            string root = $"Assets/Content/{contentId}/";
            if (!assetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return $"'{assetPath}' is not inside {root}";
            if (assetPath.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"'{assetPath}' is in ThirdPartyLocal, which never ships";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                return $"'{assetPath}' is not a prefab";
            if (prefab.GetComponent<PlayerRig>() == null)
                return $"'{assetPath}' has no PlayerRig on its root";
            return null;
        }

        /// <summary>
        /// The validated slot prefab path, or null when the slot is empty.
        /// Throws with a creator-facing message when the slot is set but invalid.
        /// </summary>
        public static string ResolveSlotOrThrow(string contentId)
        {
            string guid = LoadGuid(contentId);
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string problem = ValidateCandidate(contentId, path);
            if (problem != null)
                throw new InvalidOperationException(
                    $"Phone Player slot is invalid: {problem}. Fix or clear it in DreamPark → Content Uploader → Packages → Phone Player ({AssetPath(contentId)}).");
            return path;
        }
    }
}
#endif
