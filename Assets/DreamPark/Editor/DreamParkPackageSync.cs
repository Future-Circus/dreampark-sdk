#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEngine;

namespace DreamPark
{
    // Keeps a project's Packages/manifest.json in sync with whatever packages
    // Assets/DreamPark's own C# code needs to compile.
    //
    // WHY THIS EXISTS
    //
    // SDKPublishPanel exports Assets/DreamPark ONLY (AssetDatabase.ExportPackage,
    // SDKAssetPath = "Assets/DreamPark") — it has never touched Packages/manifest.json.
    // A fresh `git clone` gets a correct manifest for free; an existing project
    // updating via the .unitypackage never does. So any SDK release that adds a new
    // compile-time package dependency (DreamParkOpenXRSetup.cs needing
    // com.unity.xr.openxr / com.unity.xr.meta-openxr, DreamParkReleaseCommands.cs
    // needing com.unity.pipeline for its [CliCommand]/[CliArg] attributes) silently
    // breaks every existing project's Editor compile the moment the update lands —
    // and because there is no .asmdef anywhere in this SDK, ONE missing-type
    // compile error takes out the whole Assembly-CSharp-Editor assembly, not just
    // the one file that needed the package. Confirmed against a real import.
    //
    // BOOTSTRAP ORDER
    //
    // Package-dependent editor integrations are guarded by
    // DREAMPARK_SDK_PACKAGES_READY. That lets this package-free bootstrap compile
    // first in an older consumer project, add the missing manifest entries, and
    // resolve them. On the next domain reload it verifies that Unity has actually
    // registered every compile-time package before enabling the integrations. This
    // ordering matters: the original 1.7.9 implementation let those integrations
    // fail Assembly-CSharp-Editor before this watcher could ever execute.
    //
    // Never downgrades a creator's project or replaces a custom Git/file source.
    // Missing packages are added, and ordinary registry versions below the SDK's
    // declared minimum are raised to that minimum. Newer versions stay untouched.
    internal static class DreamParkPackageSync
    {
        private const string RequiredPackagesAssetPath = "Assets/DreamPark/Resources/RequiredPackages.json";
        private const string ManifestRelativePath = "Packages/manifest.json";
        private const string PackagesReadyDefine = "DREAMPARK_SDK_PACKAGES_READY";

        // Keep this deliberately narrower than RequiredPackages.json. Most entries
        // there are runtime dependencies already present in established projects;
        // these are the packages referenced directly by guarded editor source.
        private static readonly string[] CompileTimePackageIds =
        {
            "com.unity.xr.openxr",
            "com.unity.xr.meta-openxr",
            "com.unity.pipeline",
        };

        [MenuItem("DreamPark/Sync Required Packages", false, 3)]
        public static void SyncFromMenu()
        {
            Reconcile(interactive: true);
        }

        public static void Reconcile(bool interactive)
        {
            try
            {
                ReconcileInternal(interactive);
            }
            catch (Exception e)
            {
                // A broken sync must never be able to make things worse than the
                // problem it exists to fix.
                Debug.LogWarning($"[DreamPark] Package sync failed: {e.Message}");
                if (interactive)
                    EditorUtility.DisplayDialog("DreamPark", "Package sync failed — see the Console.", "OK");
            }
        }

        private static void ReconcileInternal(bool interactive)
        {
            if (!File.Exists(RequiredPackagesAssetPath))
            {
                if (interactive)
                    EditorUtility.DisplayDialog("DreamPark",
                        "No RequiredPackages.json shipped with this SDK version — nothing to sync.", "OK");
                return;
            }

            JSONObject required;
            try { required = new JSONObject(File.ReadAllText(RequiredPackagesAssetPath)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not parse {RequiredPackagesAssetPath}: {e.Message}");
                return;
            }

            var packages = required.GetField("packages");
            if (packages == null || packages.type != JSONObject.Type.Object
                || packages.keys == null || packages.keys.Count == 0)
            {
                if (interactive)
                    EditorUtility.DisplayDialog("DreamPark", "Nothing to sync.", "OK");
                return;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string manifestFullPath = Path.Combine(projectRoot, ManifestRelativePath);
            if (!File.Exists(manifestFullPath))
            {
                Debug.LogWarning($"[DreamPark] {ManifestRelativePath} not found — cannot sync packages.");
                return;
            }

            string manifestText;
            try { manifestText = File.ReadAllText(manifestFullPath); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read {ManifestRelativePath}: {e.Message}");
                return;
            }

            JSONObject manifest;
            try { manifest = new JSONObject(manifestText); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not parse {ManifestRelativePath}: {e.Message}");
                return;
            }

            var deps = manifest.GetField("dependencies");
            if (deps == null || deps.type != JSONObject.Type.Object)
            {
                Debug.LogWarning($"[DreamPark] {ManifestRelativePath} has no 'dependencies' object — cannot sync packages.");
                return;
            }

            // Add missing dependencies and raise older registry versions to the
            // SDK minimum. Preserve newer versions and non-registry sources (Git,
            // file:, tarballs): comparing or replacing those would be guesswork.
            var added = new List<string>();
            var upgraded = new List<string>();
            var changedValues = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < packages.keys.Count; i++)
            {
                string id = packages.keys[i];
                if (string.IsNullOrEmpty(id)) continue;

                string value = packages.list[i] != null ? packages.list[i].stringValue : null;
                if (string.IsNullOrEmpty(value)) continue;

                if (!deps.HasField(id))
                {
                    deps.AddField(id, value);
                    added.Add(id);
                    changedValues[id] = value;
                    continue;
                }

                var existingField = deps.GetField(id);
                string existingValue = existingField != null ? existingField.stringValue : null;
                if (!IsOlderRegistryVersion(existingValue, value)) continue;

                deps.SetField(id, value);
                upgraded.Add($"{id} ({existingValue} → {value})");
                changedValues[id] = value;
            }

            if (changedValues.Count == 0)
            {
                EnablePackageIntegrationsIfReady();
                if (interactive)
                    EditorUtility.DisplayDialog("DreamPark", "Already in sync — nothing to add.", "OK");
                return;
            }

            string newManifestText = manifest.Print(true);

            // Round-trip check before writing anything: if what we are about to
            // write doesn't parse back to an object carrying the keys we just
            // added, something about re-serialization went sideways — refuse to
            // touch the real file rather than risk the project's package
            // resolution entirely on a string we cannot verify.
            JSONObject verify;
            try { verify = new JSONObject(newManifestText); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Package sync aborted — the merged manifest failed to re-parse: {e.Message}");
                return;
            }
            var verifyDeps = verify.GetField("dependencies");
            if (verifyDeps == null || changedValues.Any(change =>
                    !verifyDeps.HasField(change.Key)
                    || verifyDeps.GetField(change.Key).stringValue != change.Value))
            {
                Debug.LogWarning("[DreamPark] Package sync aborted — a changed dependency did not survive manifest re-parse.");
                return;
            }

            // Back up before touching a file this tool does not own. Manifest.json
            // controls whether the WHOLE project resolves at all — the one file in
            // this operation where "just try again" is not good enough.
            try
            {
                string backupDir = Path.Combine(
                    projectRoot, "Library", "DreamPark", "PackageSync", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(backupDir);
                File.Copy(manifestFullPath, Path.Combine(backupDir, "manifest.json"), overwrite: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not back up {ManifestRelativePath} before syncing (continuing anyway): {e.Message}");
            }

            File.WriteAllText(manifestFullPath, newManifestText);

            var summary = new List<string>();
            if (added.Count > 0) summary.Add($"added {added.Count}: {string.Join(", ", added)}");
            if (upgraded.Count > 0) summary.Add($"upgraded {upgraded.Count}: {string.Join(", ", upgraded)}");
            Debug.Log($"[DreamPark] Synced required packages in {ManifestRelativePath} ({string.Join("; ", summary)}). Resolving...");

            // Kick the resolver so the added dependencies actually get fetched
            // rather than sitting unresolved in the manifest until some unrelated
            // domain reload happens to trigger one.
            try { Client.Resolve(); }
            catch (Exception e) { Debug.LogWarning($"[DreamPark] Client.Resolve() after package sync failed: {e.Message}"); }

            // This succeeds immediately when the packages were already registered,
            // and otherwise intentionally waits for the domain reload caused by the
            // package resolve. DreamParkPackageSyncWatcher schedules another pass on
            // every reload.
            EnablePackageIntegrationsIfReady();

            if (interactive)
            {
                EditorUtility.DisplayDialog("DreamPark",
                    $"Synced {changedValues.Count} required package(s):\n\n"
                    + string.Join("\n", added.Concat(upgraded))
                    + "\n\nResolving now — the project will recompile once packages finish fetching.",
                    "OK");
            }
        }

        private static bool IsOlderRegistryVersion(string installed, string required)
        {
            if (!TryParseRegistryVersion(installed, out var installedVersion, out bool installedPrerelease)
                || !TryParseRegistryVersion(required, out var requiredVersion, out bool requiredPrerelease))
                return false;

            int comparison = installedVersion.CompareTo(requiredVersion);
            if (comparison != 0) return comparison < 0;

            // At the same numeric version, a stable release is newer than a
            // prerelease. We deliberately do not try to order two different
            // prerelease labels; UPM's full SemVer rules are richer than a safe
            // bootstrapper needs, and guessing could replace a creator's choice.
            return installedPrerelease && !requiredPrerelease;
        }

        private static bool TryParseRegistryVersion(
            string value, out Version version, out bool prerelease)
        {
            version = null;
            prerelease = false;
            if (string.IsNullOrWhiteSpace(value)) return false;

            int dash = value.IndexOf('-');
            string numeric = dash >= 0 ? value.Substring(0, dash) : value;
            prerelease = dash >= 0;

            // Reject Git URLs, file paths, ranges, and other UPM source syntax.
            if (numeric.Any(c => !char.IsDigit(c) && c != '.')) return false;
            int componentCount = numeric.Count(c => c == '.') + 1;
            if (componentCount < 2 || componentCount > 4) return false;
            return Version.TryParse(numeric, out version);
        }

        private static void EnablePackageIntegrationsIfReady()
        {
            UnityEditor.PackageManager.PackageInfo[] registered;
            try { registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages(); }
            catch { return; }

            if (registered == null) return;
            var registeredIds = new HashSet<string>(
                registered.Where(p => p != null && !string.IsNullOrEmpty(p.name)).Select(p => p.name),
                StringComparer.Ordinal);
            if (CompileTimePackageIds.Any(id => !registeredIds.Contains(id))) return;

            foreach (var namedTarget in AllNamedBuildTargets())
            {
                try
                {
                    string defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
                    var defineList = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
                    if (defineList.Contains(PackagesReadyDefine)) continue;

                    defineList.Add(PackagesReadyDefine);
                    PlayerSettings.SetScriptingDefineSymbols(namedTarget, string.Join(";", defineList));
                    Debug.Log($"[DreamPark] Required packages resolved — enabled SDK package integrations for {namedTarget.TargetName}.");
                }
                catch
                {
                    // Unity throws for targets whose platform module is not installed.
                }
            }
        }

        private static IEnumerable<NamedBuildTarget> AllNamedBuildTargets()
        {
            var fields = typeof(NamedBuildTarget).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(NamedBuildTarget)) continue;

                NamedBuildTarget target;
                try { target = (NamedBuildTarget)field.GetValue(null); }
                catch { continue; }

                if (!string.IsNullOrEmpty(target.TargetName)) yield return target;
            }
        }
    }

    // Fires when RequiredPackages.json is (re)imported and after every domain
    // reload. The reload pass is what enables the guarded integrations after UPM
    // finishes resolving packages added by the import pass.
    [InitializeOnLoad]
    internal class DreamParkPackageSyncWatcher : AssetPostprocessor
    {
        private const string RequiredPackagesFileName = "RequiredPackages.json";

        private static bool suppressReentrancy;
        private static bool pending;

        static DreamParkPackageSyncWatcher()
        {
            Schedule();
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (suppressReentrancy) return;
            if (!Touches(importedAssets) && !Touches(movedAssets)) return;

            Schedule();
        }

        private static void Schedule()
        {
            pending = true;
            EditorApplication.delayCall += Settle;
        }

        private static bool Touches(string[] paths)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
            {
                string p = paths[i];
                if (!string.IsNullOrEmpty(p)
                    && p.EndsWith(RequiredPackagesFileName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void Settle()
        {
            if (!pending) return;

            // Do not edit the package manifest while Unity is compiling or while
            // Package Manager is already refreshing the asset database.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Settle;
                return;
            }

            pending = false;
            suppressReentrancy = true;
            try
            {
                DreamParkPackageSync.Reconcile(interactive: false);
            }
            finally
            {
                suppressReentrancy = false;
            }
        }
    }
}
#endif
