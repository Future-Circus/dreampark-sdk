#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Defective.JSON;
using UnityEditor;
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
    // HOW THIS ACTUALLY HELPS, AND ITS REAL LIMIT
    //
    // A .unitypackage import ALWAYS leaves the editor mid-compile by the time any
    // AssetPostprocessor callback fires — see DreamParkPackageSyncWatcher.Settle
    // below and SDKVersionWatcher's own comment, which established this for real in
    // this codebase, not as an assumption. So this cannot prevent the FIRST
    // compile-error flash if new package-dependent files land before their package
    // does. What it does do: once that broken compile settles, patch
    // Packages/manifest.json with whatever is missing and kick off a package
    // resolve, so the NEXT recompile — automatic, no human involved — succeeds. A
    // permanently broken import becomes a self-healing one instead. Whether Unity's
    // resolve-then-recompile ordering actually plays out this cleanly needs a
    // live-Editor test to be certain; this is the theoretically-correct mechanism
    // and matches SDKVersionWatcher's proven shape, but has not been run for real.
    //
    // NEVER touches or downgrades a package a creator's project already has for
    // its own reasons — only adds keys that are missing entirely. Never rewrites
    // unrelated parts of the manifest, only patches the specific missing keys.
    internal static class DreamParkPackageSync
    {
        private const string RequiredPackagesAssetPath = "Assets/DreamPark/Resources/RequiredPackages.json";
        private const string ManifestRelativePath = "Packages/manifest.json";

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

            // Only ADD keys that are entirely missing. Never touch an existing
            // entry — a creator may have a newer or differently-sourced version
            // for their own reasons, and this tool's job is "make it compile,"
            // not "make it match."
            var added = new List<string>();
            for (int i = 0; i < packages.keys.Count; i++)
            {
                string id = packages.keys[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (deps.HasField(id)) continue;

                string value = packages.list[i] != null ? packages.list[i].stringValue : null;
                if (string.IsNullOrEmpty(value)) continue;

                deps.AddField(id, value);
                added.Add(id);
            }

            if (added.Count == 0)
            {
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
            if (verifyDeps == null || added.Any(id => !verifyDeps.HasField(id)))
            {
                Debug.LogWarning("[DreamPark] Package sync aborted — the merged manifest is missing an added key after re-parse.");
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

            Debug.Log($"[DreamPark] Added {added.Count} missing package(s) to {ManifestRelativePath}: "
                    + string.Join(", ", added) + ". Resolving...");

            // Kick the resolver so the added dependencies actually get fetched
            // rather than sitting unresolved in the manifest until some unrelated
            // domain reload happens to trigger one.
            try { Client.Resolve(); }
            catch (Exception e) { Debug.LogWarning($"[DreamPark] Client.Resolve() after package sync failed: {e.Message}"); }

            if (interactive)
            {
                EditorUtility.DisplayDialog("DreamPark",
                    $"Added {added.Count} missing package(s):\n\n" + string.Join("\n", added)
                    + "\n\nResolving now — the project will recompile once packages finish fetching.",
                    "OK");
            }
        }
    }

    // Fires when RequiredPackages.json is (re)imported — an SDK update — or on
    // every domain reload, so an import that already recompiled without this file
    // present (e.g. a .unitypackage import that dropped straight into a broken
    // compile before any of this could run) still gets a chance to self-heal once
    // that compile settles. Same 4-arg shape as SDKVersionWatcher and
    // ContentFolderWatchdog.AssetPostprocessorWatcher, the two existing
    // precedents for this exact pattern in this file family.
    internal class DreamParkPackageSyncWatcher : AssetPostprocessor
    {
        private const string RequiredPackagesFileName = "RequiredPackages.json";

        private static bool suppressReentrancy;
        private static bool pending;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (suppressReentrancy) return;
            if (!Touches(importedAssets) && !Touches(movedAssets)) return;

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

            // Same reasoning as SDKVersionWatcher.Settle: an import that dropped
            // this file in almost certainly also dropped in whatever new script
            // needs the packages it lists, and that script is very likely still
            // compiling — or has already failed to — by the time this callback
            // fires at all. Re-arm rather than act mid-compile.
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
