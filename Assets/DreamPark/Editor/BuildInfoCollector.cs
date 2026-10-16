#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DreamPark
{
    /// <summary>
    /// Oct 10 2026: build metadata recorded on every content version
    /// ("buildInfo"), so an engine, package or settings change can be traced to
    /// the exact version it shipped in (the Unity-upgrade checklist).
    ///
    /// It is written into the BuildManifest (local baseline), each platform's
    /// delivery-index.json, and the commitUpload body (Web stores a sanitized
    /// copy on the version record). Additive everywhere: older clients and
    /// backends ignore it.
    ///
    /// PRIVACY: never a machine name, user name, file path, key, token or email.
    /// Only versions, settings, counts, a git commit hash and a dirty flag.
    /// <see cref="IsSafeValue"/> is the last line of defence and is tested.
    /// </summary>
    public sealed class BuildInfo
    {
        public string unityVersion;
        public string sdkVersion;
        public string contentCommit;
        public bool? contentDirty;
        public int bundleLayoutVersion;
        public string uploadMode;
        public string requestedUploadMode;
        public string modeForcedReason;
        public string uploaderOrigin;          // "gui" | "cli"
        public double buildDurationSeconds;
        public int bundleCount;
        public long totalBytes;
        public int bundlesUploaded;
        public int bundlesReused;
        public long bytesUploaded;
        public long bytesReused;
        public readonly SortedDictionary<string, string> packages = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, PlatformBuildInfo> platforms = new SortedDictionary<string, PlatformBuildInfo>(StringComparer.Ordinal);

        public JSONObject ToJson(string onlyPlatform = null)
        {
            var json = new JSONObject(JSONObject.Type.Object);
            AddString(json, "unityVersion", unityVersion);
            AddString(json, "sdkVersion", sdkVersion);
            AddString(json, "contentCommit", contentCommit);
            if (contentDirty.HasValue) json.AddField("contentDirty", contentDirty.Value);
            if (bundleLayoutVersion > 0) json.AddField("bundleLayoutVersion", bundleLayoutVersion);
            AddString(json, "uploadMode", uploadMode);
            AddString(json, "requestedUploadMode", requestedUploadMode);
            AddString(json, "modeForcedReason", modeForcedReason);
            AddString(json, "uploaderOrigin", uploaderOrigin);
            if (buildDurationSeconds > 0) json.AddField("buildDurationSeconds", (float)Math.Round(buildDurationSeconds, 1));
            if (bundleCount > 0) json.AddField("bundleCount", bundleCount);
            if (totalBytes > 0) json.AddField("totalBytes", totalBytes);
            if (bundlesUploaded > 0 || bundlesReused > 0)
            {
                json.AddField("bundlesUploaded", bundlesUploaded);
                json.AddField("bundlesReused", bundlesReused);
                json.AddField("bytesUploaded", bytesUploaded);
                json.AddField("bytesReused", bytesReused);
            }
            if (packages.Count > 0)
            {
                var p = new JSONObject(JSONObject.Type.Object);
                foreach (var pair in packages) AddString(p, pair.Key, pair.Value);
                json.AddField("packages", p);
            }
            var plats = new JSONObject(JSONObject.Type.Object);
            foreach (var pair in platforms)
                if (onlyPlatform == null || pair.Key == onlyPlatform) plats.AddField(pair.Key, pair.Value.ToJson());
            if (plats.count > 0) json.AddField("platforms", plats);
            return json;
        }

        private static void AddString(JSONObject json, string key, string value)
        {
            if (IsSafeValue(value)) json.AddField(key, value.Length > 200 ? value.Substring(0, 200) : value);
        }

        /// False for anything that could identify a person or a machine: file
        /// paths, emails, the current user or machine name. Such values are
        /// simply left out.
        public static bool IsSafeValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.Contains("/Users/") || value.Contains("/home/") || value.Contains(":\\") || value.StartsWith("/", StringComparison.Ordinal)) return false;
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"[^\s@]+@[^\s@]+\.[^\s@]+")) return false;
            foreach (string personal in new[] { Environment.UserName, Environment.MachineName, SystemInfo.deviceName })
                if (!string.IsNullOrEmpty(personal) && personal.Length >= 3
                    && value.IndexOf(personal, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
    }

    public sealed class PlatformBuildInfo
    {
        public string buildTarget;
        public List<string> graphicsApis = new List<string>();
        public string colorSpace;
        public string textureCompression;
        public bool? stripUnityVersion;
        public bool? contiguousBundles;
        public bool? nonRecursiveBuilding;
        public bool? crc;
        public bool? crcCachedBundles;
        public string compression;
        public int bundleCount;
        public long totalBytes;

        public JSONObject ToJson()
        {
            var json = new JSONObject(JSONObject.Type.Object);
            if (BuildInfo.IsSafeValue(buildTarget)) json.AddField("buildTarget", buildTarget);
            var apis = new JSONObject(JSONObject.Type.Array);
            foreach (string api in graphicsApis.Where(BuildInfo.IsSafeValue)) apis.Add(api);
            if (apis.count > 0) json.AddField("graphicsApis", apis);
            if (BuildInfo.IsSafeValue(colorSpace)) json.AddField("colorSpace", colorSpace);
            if (BuildInfo.IsSafeValue(textureCompression)) json.AddField("textureCompression", textureCompression);
            if (stripUnityVersion.HasValue) json.AddField("stripUnityVersion", stripUnityVersion.Value);
            if (contiguousBundles.HasValue) json.AddField("contiguousBundles", contiguousBundles.Value);
            if (nonRecursiveBuilding.HasValue) json.AddField("nonRecursiveBuilding", nonRecursiveBuilding.Value);
            if (crc.HasValue) json.AddField("crc", crc.Value);
            if (crcCachedBundles.HasValue) json.AddField("crcCachedBundles", crcCachedBundles.Value);
            if (BuildInfo.IsSafeValue(compression)) json.AddField("compression", compression);
            if (bundleCount > 0) json.AddField("bundleCount", bundleCount);
            if (totalBytes > 0) json.AddField("totalBytes", totalBytes);
            return json;
        }
    }

    /// Reads the build environment. Every probe is best-effort: a value that
    /// can't be read is left out, never guessed.
    public static class BuildInfoCollector
    {
        // Packages whose version changes what content bundles contain or how
        // they load. XLua ships inside the SDK (its version is the SDK's).
        private static readonly string[] PackagePrefixes =
        {
            "com.unity.addressables", "com.unity.scriptablebuildpipeline", "com.unity.render-pipelines",
            "com.unity.shadergraph", "com.unity.textmeshpro", "com.unity.ugui", "com.unity.xr", "com.meta.xr",
            "com.unity.inputsystem", "com.unity.timeline", "com.unity.animation",
        };

        public static BuildInfo Collect(string contentId, string uploadMode, string requestedUploadMode,
            string modeForcedReason, bool automated)
        {
            var info = new BuildInfo
            {
                unityVersion = Application.unityVersion,
                sdkVersion = SafeCall(() => SDKVersion.Current),
                bundleLayoutVersion = BundlingStrategyPrefs.Current == BundlingStrategy.Smart
                    ? SmartBundleGrouper.BundleLayoutVersion : 0,
                uploadMode = uploadMode,
                requestedUploadMode = requestedUploadMode,
                modeForcedReason = modeForcedReason,
                uploaderOrigin = automated ? "cli" : "gui",
            };
            foreach (var pair in PackageVersions()) info.packages[pair.Key] = pair.Value;
            (string commit, bool? dirty) = GitState(contentId);
            info.contentCommit = commit;
            info.contentDirty = dirty;
            return info;
        }

        /// Settings that were in effect for one platform's build.
        public static PlatformBuildInfo ForPlatform(BuildTarget target, BuildManifestPlatform built)
        {
            var info = new PlatformBuildInfo { buildTarget = target.ToString() };
            try
            {
                foreach (var api in PlayerSettings.GetGraphicsAPIs(target)) info.graphicsApis.Add(api.ToString());
            }
            catch { /* platform module not installed */ }
            info.colorSpace = PlayerSettings.colorSpace.ToString();
            if (target == BuildTarget.Android) info.textureCompression = EditorUserBuildSettings.androidBuildSubtarget.ToString();
            var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null)
            {
                info.contiguousBundles = settings.ContiguousBundles;
                info.nonRecursiveBuilding = settings.NonRecursiveBuilding;
                info.stripUnityVersion = ReadBool(settings, "StripUnityVersionFromBundleBuild");
                var schemas = settings.groups.Where(g => g != null)
                    .Select(g => g.GetSchema<BundledAssetGroupSchema>()).Where(s => s != null && s.IncludeInBuild).ToList();
                if (schemas.Count > 0)
                {
                    info.crc = schemas.All(s => s.UseAssetBundleCrc);
                    info.crcCachedBundles = schemas.All(s => s.UseAssetBundleCrcForCachedBundles);
                    var modes = schemas.Select(s => s.Compression.ToString()).Distinct().OrderBy(m => m).ToList();
                    info.compression = string.Join("+", modes);
                }
            }
            if (built?.files != null)
            {
                var bundles = built.files.Where(f => f != null && f.fileName != null
                    && f.fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)).ToList();
                info.bundleCount = bundles.Count;
                info.totalBytes = bundles.Sum(f => Math.Max(0, f.sizeBytes));
            }
            return info;
        }

        /// Totals and uploaded-vs-reused counts from the patch diff (null diff = all uploaded).
        public static void AddTotals(BuildInfo info, BuildManifest current, BuildManifestDiff diff)
        {
            if (info == null || current == null) return;
            foreach (var p in current.platforms)
            {
                var bundles = p.files.Where(f => f?.fileName != null && f.fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)).ToList();
                info.bundleCount += bundles.Count;
                info.totalBytes += bundles.Sum(f => Math.Max(0, f.sizeBytes));
                PlatformDiff pd = diff?.GetPlatform(p.platform);
                if (pd == null)
                {
                    info.bundlesUploaded += bundles.Count;
                    info.bytesUploaded += bundles.Sum(f => Math.Max(0, f.sizeBytes));
                    continue;
                }
                var sizes = bundles.ToDictionary(f => f.fileName, f => Math.Max(0, f.sizeBytes), StringComparer.Ordinal);
                foreach (string name in pd.changedFiles.Where(sizes.ContainsKey)) { info.bundlesUploaded++; info.bytesUploaded += sizes[name]; }
                foreach (string name in pd.unchangedFiles.Where(sizes.ContainsKey)) { info.bundlesReused++; info.bytesReused += sizes[name]; }
            }
        }

        internal static IEnumerable<KeyValuePair<string, string>> PackageVersions()
        {
            UnityEditor.PackageManager.PackageInfo[] all;
            try { all = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages(); }
            catch { yield break; }
            foreach (var p in all.Where(p => p != null && PackagePrefixes.Any(prefix => p.name.StartsWith(prefix, StringComparison.Ordinal)))
                         .OrderBy(p => p.name, StringComparer.Ordinal).Take(32))
                yield return new KeyValuePair<string, string>(p.name, p.version);
        }

        // HEAD commit and dirty flag of the repo holding the content folder.
        // Only the hash and a boolean leave this method — never a path.
        internal static (string commit, bool? dirty) GitState(string contentId)
        {
            try
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string head = RunGit(projectRoot, "rev-parse HEAD");
                if (head == null || !System.Text.RegularExpressions.Regex.IsMatch(head, "^[0-9a-f]{40}$")) return (null, null);
                string folder = string.IsNullOrEmpty(contentId) ? "Assets/Content" : $"Assets/Content/{contentId}";
                string status = RunGit(projectRoot, $"status --porcelain -- \"{folder}\"");
                return (head, status == null ? (bool?)null : status.Length > 0);
            }
            catch { return (null, null); }
        }

        private static string RunGit(string workingDirectory, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(psi))
            {
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000) || process.ExitCode != 0) return null;
                return output.Trim();
            }
        }

        private static bool? ReadBool(object target, string property)
        {
            var prop = target.GetType().GetProperty(property);
            if (prop == null || prop.PropertyType != typeof(bool)) return null;
            try { return (bool)prop.GetValue(target); } catch { return null; }
        }

        private static string SafeCall(Func<string> f) { try { return f(); } catch { return null; } }
    }
}
#endif
