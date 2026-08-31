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

        // Editor-only: the asset path behind ResourcePath. Resources.Load hands back
        // Unity's CACHED object for a resource path — content only refreshes when the
        // AssetDatabase reimports the asset. A .unitypackage import overwrites this
        // file on disk, so in the editor we force the reimport ourselves before
        // reading rather than trusting whatever Unity happens to have cached.
        // Loading through AssetDatabase also removes the dependency on the file
        // living under a Resources/ folder at all.
        public const string EditorAssetPath = "Assets/DreamPark/Resources/DreamParkSDKVersion.json";

        private static string cachedVersion;
        private static bool loaded;

        // A failed load leaves `loaded` false so the next read retries — but we must
        // not force an AssetDatabase reimport (and re-log the same warning) on every
        // single Current read in a project where the file is genuinely absent. After
        // the first failure we degrade to a plain Resources.Load and stay quiet;
        // Reload() clears this, so the explicit "something just changed" paths still
        // get the full treatment.
        private static bool loadFailed;

        public static string Current
        {
            get
            {
                // Do NOT retry after a failure. A missing/corrupt version file would
                // otherwise make every repaint of the uploader panel (which reads this
                // from OnGUI) hit the AssetDatabase again. Reload() clears loadFailed,
                // so the explicit "something changed" paths still retry.
                if (!loaded && !loadFailed) EnsureLoaded();
                return cachedVersion ?? "0.0.0";
            }
        }

        // Force a reload (e.g. after the publish flow rewrites the JSON on disk
        // and we need to read the new value back without an editor restart).
        public static void Reload()
        {
            loaded = false;
            loadFailed = false;
            cachedVersion = null;
            EnsureLoaded(forceReimport: true);
        }

        // Only latches on SUCCESS. The previous version set `loaded = true` before
        // attempting the load, which meant a transient miss — Resources.Load returning
        // null while the AssetDatabase is mid-refresh, exactly the window a package
        // import creates — pinned the project at "0.0.0" for the rest of the editor
        // session. "0.0.0" compares older than every real release, so the upload gate
        // went permanently red and reinstalling could not clear it; only an editor
        // restart could. Failing to load now simply leaves us unloaded, so the next
        // read retries.
        private static void EnsureLoaded(bool forceReimport = false)
        {
            if (loaded) return;

            bool firstAttempt = !loadFailed;

            // Only an explicit Reload() forces the reimport. A lazy first read can come
            // from OnGUI or from Play Mode, and ForceSynchronousImport there stalls the
            // frame and re-enters the import callback.
            var asset = LoadVersionAsset(forceReimport);
            if (asset == null)
            {
                if (firstAttempt)
                {
                    Debug.LogWarning($"[DreamPark] SDK version file missing at Resources/{ResourcePath}.json — assuming 0.0.0 until it can be read.");
                }
                loadFailed = true;
                return;
            }

            try
            {
                var json = new JSONObject(asset.text);
                if (json != null && json.HasField("version"))
                {
                    string parsed = json.GetField("version").stringValue;
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        cachedVersion = parsed;
                        loaded = true;
                        loadFailed = false;
                        return;
                    }
                }

                // Parsed fine but carries no usable `version`. Previously this fell
                // through silently with cachedVersion == null and loaded == true, so
                // Current returned "0.0.0" forever and NOTHING was logged — neither
                // the missing-asset warning nor the parse-exception warning is on
                // this path. Say so out loud instead.
                if (firstAttempt)
                {
                    Debug.LogWarning($"[DreamPark] SDK version file at {EditorAssetPath} has no usable \"version\" field — assuming 0.0.0.");
                }
            }
            catch (Exception e)
            {
                if (firstAttempt)
                {
                    Debug.LogWarning($"[DreamPark] Failed to parse SDK version JSON: {e.Message}");
                }
            }

            loadFailed = true;
        }

        private static TextAsset LoadVersionAsset(bool forceReimport)
        {
#if UNITY_EDITOR
            // Force the reimport so we read what is on disk RIGHT NOW, not what Unity
            // cached before the package import overwrote it. SDKPublishPanel already
            // does this on the publish side; the consumer side never did, which is why
            // a freshly installed SDK could report the old version.
            try
            {
                if (forceReimport)
                {
                    UnityEditor.AssetDatabase.ImportAsset(
                        EditorAssetPath,
                        UnityEditor.ImportAssetOptions.ForceUpdate |
                        UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                }

                var viaDb = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(EditorAssetPath);
                if (viaDb != null) return viaDb;
            }
            catch (Exception e)
            {
                // Never let an AssetDatabase hiccup take out version reporting.
                if (forceReimport)
                {
                    Debug.LogWarning($"[DreamPark] Could not reimport the SDK version file, falling back to Resources.Load: {e.Message}");
                }
            }
#endif
            return Resources.Load<TextAsset>(ResourcePath);
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
