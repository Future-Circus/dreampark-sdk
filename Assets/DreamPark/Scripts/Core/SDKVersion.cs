using System;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    // Reads Assets/DreamPark/Resources/DreamParkSDKVersion.json — the source of
    // truth for "what SDK version is installed in this project". The file ships
    // *with* the SDK (gets bundled into the .unitypackage), so importing a new
    // version overwrites this file, atomically updating the local version.
    //
    // Strict semver only (MAJOR.MINOR.PATCH). Compare returns negative if `a` is
    // older, 0 if equal, positive if `a` is newer.
    public static class SDKVersion
    {
        private const string ResourcePath = "DreamParkSDKVersion";
        private static string cachedVersion;
        private static bool loaded;

        public static string Current
        {
            get
            {
                EnsureLoaded();
                return cachedVersion ?? "0.0.0";
            }
        }

        // Force a reload (e.g. after the publish flow rewrites the JSON on disk
        // and we need to read the new value back without an editor restart).
        public static void Reload()
        {
            loaded = false;
            cachedVersion = null;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;

            var asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset == null)
            {
                Debug.LogWarning($"[DreamPark] SDK version file missing at Resources/{ResourcePath}.json — assuming 0.0.0.");
                cachedVersion = "0.0.0";
                return;
            }

            try
            {
                var json = new JSONObject(asset.text);
                if (json != null && json.HasField("version"))
                {
                    cachedVersion = json.GetField("version").stringValue;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Failed to parse SDK version JSON: {e.Message}");
                cachedVersion = "0.0.0";
            }
        }

        // Returns negative / 0 / positive following standard comparator semantics.
        // Returns 0 for any pair where either side fails to parse — fail open so
        // network blips / malformed manifests don't trigger forced updates.
        public static int Compare(string a, string b)
        {
            if (!TryParse(a, out var aMajor, out var aMinor, out var aPatch)) return 0;
            if (!TryParse(b, out var bMajor, out var bMinor, out var bPatch)) return 0;
            if (aMajor != bMajor) return aMajor.CompareTo(bMajor);
            if (aMinor != bMinor) return aMinor.CompareTo(bMinor);
            return aPatch.CompareTo(bPatch);
        }

        public static bool TryParse(string version, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Trim().Split('.');
            if (parts.Length != 3) return false;
            return int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor)
                && int.TryParse(parts[2], out patch);
        }
    }
}
