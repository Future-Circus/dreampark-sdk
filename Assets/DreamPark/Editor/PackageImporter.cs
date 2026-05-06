#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools
{
    /// <summary>
    /// Programmatic .unitypackage import for headless / agent use.
    ///
    /// Why this exists: Unity Package Manager (UPM) only handles package.json-based
    /// packages. Legacy `.unitypackage` files require AssetDatabase.ImportPackage,
    /// which is async, callback-driven, and a pain to drive correctly from a one-off
    /// `execute_csharp` script. This helper wraps it with a synchronous-feeling API
    /// so agents (and humans) get a clean Import / ImportMany surface.
    ///
    /// The interactive=false path of AssetDatabase.ImportPackage is effectively
    /// synchronous in modern Unity (the call returns after the package is queued
    /// AND processed). We still subscribe to the completion callbacks so we can
    /// distinguish success / failure / cancel and report meaningful diagnostics
    /// per package.
    ///
    /// Usage:
    ///   PackageImporter.Import("/abs/path/to/Foo.unitypackage")
    ///       → ImportResult { ok=true, packageName="Foo", ... }
    ///
    ///   PackageImporter.ImportMany(new[] { "/abs/path/A.unitypackage", "/abs/path/B.unitypackage" })
    ///       → ImportSummary { total=2, succeeded=2, failed=0, results=[...] }
    /// </summary>
    public static class PackageImporter
    {
        // ─── Result types ───────────────────────────────────────────────────────

        [Serializable]
        public struct ImportResult
        {
            public bool ok;
            public string packagePath;   // input path the caller passed
            public string packageName;   // Unity's name for the package (filename minus extension)
            public string status;        // "completed" | "failed" | "cancelled" | "missing_file" | "unknown"
            public string error;         // populated when ok=false

            public override string ToString()
            {
                if (ok) return $"[PackageImporter] ✓ {packageName} ({status})";
                return $"[PackageImporter] ✗ {packageName ?? Path.GetFileName(packagePath)} — {status}: {error}";
            }
        }

        [Serializable]
        public struct ImportSummary
        {
            public int total;
            public int succeeded;
            public int failed;
            public ImportResult[] results;

            public override string ToString()
            {
                return $"[PackageImporter] batch: {succeeded}/{total} succeeded, {failed} failed";
            }
        }

        // ─── Internal completion tracking ───────────────────────────────────────
        // We use static state because AssetDatabase callbacks are global and we
        // want to capture per-package outcome even when ImportPackage is async.
        // Resets per Import() call.
        static string _expectedPackageName;
        static string _lastStatus;
        static string _lastError;

        static void OnCompleted(string packageName)
        {
            if (packageName == _expectedPackageName)
            {
                _lastStatus = "completed";
                _lastError = null;
            }
        }

        static void OnFailed(string packageName, string errorMessage)
        {
            if (packageName == _expectedPackageName)
            {
                _lastStatus = "failed";
                _lastError = errorMessage ?? "unknown failure";
            }
        }

        static void OnCancelled(string packageName)
        {
            if (packageName == _expectedPackageName)
            {
                _lastStatus = "cancelled";
                _lastError = "user or system cancelled the import";
            }
        }

        // ─── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Import a single .unitypackage from an absolute filesystem path.
        /// Blocks until the import completes (or is determined to have failed).
        /// </summary>
        public static ImportResult Import(string absolutePackagePath)
        {
            var result = new ImportResult { packagePath = absolutePackagePath };

            if (string.IsNullOrEmpty(absolutePackagePath))
            {
                result.status = "missing_file";
                result.error = "absolutePackagePath is null or empty";
                Debug.LogError(result);
                return result;
            }

            if (!File.Exists(absolutePackagePath))
            {
                result.status = "missing_file";
                result.error = $"file not found: {absolutePackagePath}";
                Debug.LogError(result);
                return result;
            }

            // Unity uses the filename (minus .unitypackage) as the package's "name"
            // in completion callbacks.
            result.packageName = Path.GetFileNameWithoutExtension(absolutePackagePath);
            _expectedPackageName = result.packageName;
            _lastStatus = "unknown";
            _lastError = null;

            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageFailed += OnFailed;
            AssetDatabase.importPackageCancelled += OnCancelled;

            try
            {
                // interactive: false → no modal Import dialog, fully programmatic.
                // In modern Unity (2020+), this path is effectively synchronous —
                // ImportPackage returns after the package contents are written.
                AssetDatabase.ImportPackage(absolutePackagePath, false);

                // Force any pending asset-import work to flush so subsequent code
                // (e.g., PackageRelocator) can see the imported folders.
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                result.status = _lastStatus == "unknown" ? "completed" : _lastStatus;
                result.error = _lastError;
                result.ok = result.status == "completed";
            }
            catch (Exception e)
            {
                result.status = "failed";
                result.error = $"exception during ImportPackage: {e.Message}";
                result.ok = false;
            }
            finally
            {
                AssetDatabase.importPackageCompleted -= OnCompleted;
                AssetDatabase.importPackageFailed -= OnFailed;
                AssetDatabase.importPackageCancelled -= OnCancelled;
                _expectedPackageName = null;
            }

            Debug.Log(result);
            return result;
        }

        /// <summary>
        /// Import a batch of packages sequentially. Continues past individual
        /// failures so a bad package doesn't stop the rest of the batch — the
        /// summary records which succeeded and which failed.
        /// </summary>
        public static ImportSummary ImportMany(string[] absolutePackagePaths)
        {
            var summary = new ImportSummary
            {
                total = absolutePackagePaths?.Length ?? 0,
                results = absolutePackagePaths == null
                    ? Array.Empty<ImportResult>()
                    : new ImportResult[absolutePackagePaths.Length],
            };

            if (absolutePackagePaths == null || absolutePackagePaths.Length == 0)
            {
                Debug.LogWarning("[PackageImporter] ImportMany called with no paths.");
                return summary;
            }

            for (int i = 0; i < absolutePackagePaths.Length; i++)
            {
                var r = Import(absolutePackagePaths[i]);
                summary.results[i] = r;
                if (r.ok) summary.succeeded++;
                else summary.failed++;
            }

            // One final refresh after all imports — covers any GUID work that needed
            // multiple passes to settle (e.g., cross-package material references).
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Debug.Log(summary);
            return summary;
        }

        /// <summary>
        /// Convenience: glob a folder for .unitypackage files and import them all.
        /// Useful when the resource-library has many packages organized by category.
        /// </summary>
        public static ImportSummary ImportFolder(string absoluteFolderPath, bool recursive = true)
        {
            if (!Directory.Exists(absoluteFolderPath))
            {
                Debug.LogError($"[PackageImporter] folder not found: {absoluteFolderPath}");
                return new ImportSummary { results = Array.Empty<ImportResult>() };
            }

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var paths = Directory.GetFiles(absoluteFolderPath, "*.unitypackage", option);
            return ImportMany(paths);
        }
    }
}
#endif
