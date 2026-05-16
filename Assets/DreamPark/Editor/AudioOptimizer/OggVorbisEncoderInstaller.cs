#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.EditorTools.AudioOptimization
{
    /// <summary>
    /// Auto-installs the OggVorbisEncoder NuGet package on first use of
    /// the Audio Optimizer. Same install-and-go pattern as the Texture
    /// Optimizer's <c>MagickNetInstaller</c> — creator opens the tool, it
    /// just works. No NuGet client, no UPM package, no manual DLL drop,
    /// no checked-in binaries bloating the SDK repo.
    ///
    /// What gets installed and where:
    ///
    ///   Assets/DreamPark/ThirdParty/OggVorbisEncoder/
    ///     .gitignore                              (committed, ignores siblings)
    ///     Editor/
    ///       OggVorbisEncoder.dll                  (managed, ~140 KB)
    ///
    /// Everything except the .gitignore is downloaded per-machine on first
    /// use. The .gitignore is committed so accidental adds don't push the
    /// binary to anyone else's clone.
    ///
    /// Pure C#: there are no native binaries to worry about. That's the
    /// single biggest reason we picked OggVorbisEncoder over FFmpeg —
    /// install footprint is ~140 KB instead of ~100 MB, and it works on
    /// every editor host without per-platform packaging.
    ///
    /// License note: OggVorbisEncoder is a port of libvorbis (BSD-style
    /// license). Auto-downloading at first use is fine.
    /// </summary>
    public static class OggVorbisEncoderInstaller
    {
        // Pinned version. Bumping: change here, delete an installed
        // ThirdParty/OggVorbisEncoder/Editor folder on a test machine,
        // open the Audio Optimizer, confirm the new download + the
        // bootstrap's reflection still binds. The OggVorbisEncoder API
        // has been stable across 1.x, but a major version bump could
        // shuffle namespaces — that's what would force a reflection edit.
        public const string PinnedVersion = "1.2.2";
        public const string PackageId = "OggVorbisEncoder";

        private const string NuGetBase = "https://api.nuget.org/v3-flatcontainer";

        // Asset-database-relative install paths. Kept public so the
        // bootstrap can probe them with File.Exists at the right places.
        public const string Root = "Assets/DreamPark/ThirdParty/OggVorbisEncoder";
        public const string EditorFolder = Root + "/Editor";

        public static string ManagedDllPath => EditorFolder + "/OggVorbisEncoder.dll";

        /// <summary>
        /// OggVorbisEncoder pulls a chain of transitive NuGet dependencies:
        ///
        ///   OggVorbisEncoder 1.2.2
        ///     → System.Memory 4.5.5
        ///         → System.Buffers 4.5.1
        ///         → System.Runtime.CompilerServices.Unsafe 4.5.3
        ///
        /// Unity ships its own copies of some of these for other packages
        /// (burst, Sentis, etc.) but at different assembly versions, which
        /// means OggVorbisEncoder's strong-name reference can't bind to
        /// them. We sidestep the version mismatch by shipping the exact
        /// versions OggVorbisEncoder expects alongside it, configured as
        /// editor-only plugins so they don't fight Unity's built-in dlls
        /// at runtime in player builds.
        ///
        /// The version pins below should be bumped in lockstep with
        /// OggVorbisEncoder when its <see cref="PinnedVersion"/> changes —
        /// re-read the new package's .nuspec dependency graph.
        /// </summary>
        public static readonly (string Id, string Version, string Dll)[] TransitiveDeps =
        {
            ("System.Memory",                            "4.5.5", "System.Memory.dll"),
            ("System.Buffers",                           "4.5.1", "System.Buffers.dll"),
            ("System.Runtime.CompilerServices.Unsafe",   "4.5.3", "System.Runtime.CompilerServices.Unsafe.dll"),
        };

        private static string DepDllPath(string dllName) => EditorFolder + "/" + dllName;

        /// <summary>
        /// EditorPrefs flag set right before AssetDatabase.Refresh kicks
        /// off the domain reload that loads the new DLL. A separate
        /// [InitializeOnLoad] reads this flag on next domain ready and
        /// re-opens the Audio Optimizer window, so the install feels
        /// like "click → progress → ready" with no second click required.
        /// </summary>
        public const string ReopenFlagPref = "DreamPark.AudioOptimizer.PendingReopen";

        // ─── Public API ─────────────────────────────────────────────────

        /// <summary>
        /// True when the managed DLL is on disk. Cheap — one File.Exists.
        /// </summary>
        public static bool IsInstalled()
        {
            if (!File.Exists(AbsOf(ManagedDllPath))) return false;
            foreach (var dep in TransitiveDeps)
                if (!File.Exists(AbsOf(DepDllPath(dep.Dll)))) return false;
            return true;
        }

        /// <summary>
        /// Synchronously download + extract + configure. Blocks the editor
        /// thread, but reports progress via <paramref name="onProgress"/>
        /// so the caller can keep its progress bar fresh. ~5-15 seconds
        /// on a typical broadband connection — the package is tiny.
        ///
        /// Sets <see cref="ReopenFlagPref"/> and calls AssetDatabase.Refresh
        /// at the end, which triggers a domain reload — the caller's
        /// window will close, and the reopen hook below reopens it on the
        /// next editor tick.
        /// </summary>
        public static void InstallSync(Action<float, string> onProgress)
        {
            onProgress?.Invoke(0.01f, "Preparing install folder...");

            // Always start from a clean slate. If a previous install
            // failed midway, Unity's plugin-reference cache holds an
            // "unresolved reference" state that survives partial
            // re-installs. The cheapest reliable fix is to delete the
            // existing artifact first and import fresh.
            CleanInstallArtifacts();

            Directory.CreateDirectory(AbsOf(EditorFolder));
            EnsureGitignore();
            Debug.Log("[AudioOptimizer] Installing OggVorbisEncoder " + PinnedVersion + " + " + TransitiveDeps.Length + " transitive deps...");

            // We want a single progress curve across N+1 downloads.
            // Total work = main package + each transitive dep. Allocate
            // 75% of the bar to downloads, 10% to extraction, the
            // remaining 15% to Unity import + plugin configuration.
            int totalDownloads = 1 + TransitiveDeps.Length;
            float downloadBudget = 0.75f / totalDownloads;
            float currentBase = 0.05f;

            // ── Download the main package ──────────────────────────────
            onProgress?.Invoke(currentBase, "Downloading OggVorbisEncoder...");
            byte[] mainNupkg = DownloadNupkg(PackageId, PinnedVersion,
                p => onProgress?.Invoke(currentBase + downloadBudget * p, "Downloading OggVorbisEncoder..."));
            currentBase += downloadBudget;

            string mainEntry = FindDllEntry(mainNupkg);
            if (mainEntry == null)
            {
                throw new FileNotFoundException(
                    "Couldn't find OggVorbisEncoder.dll in any lib/* folder of the NuGet package. "
                    + "The package layout may have changed in a newer version — "
                    + "bump OggVorbisEncoderInstaller.PinnedVersion or report the issue.");
            }
            ExtractEntry(mainNupkg, mainEntry, AbsOf(ManagedDllPath));

            // ── Download each transitive dep ───────────────────────────
            // Each dep ships under lib/netstandard2.0/{Name}.dll in its
            // own .nupkg. We download one at a time (NuGet's flat CDN
            // gives us atomic .nupkg downloads, so chunking can't help).
            var depPaths = new List<string>();
            foreach (var dep in TransitiveDeps)
            {
                onProgress?.Invoke(currentBase, "Downloading " + dep.Id + "...");
                byte[] depNupkg = DownloadNupkg(dep.Id, dep.Version,
                    p => onProgress?.Invoke(currentBase + downloadBudget * p, "Downloading " + dep.Id + "..."));
                currentBase += downloadBudget;

                string depEntry = FindDllEntryByName(depNupkg, dep.Dll);
                if (depEntry == null)
                {
                    throw new FileNotFoundException(
                        $"Couldn't find {dep.Dll} in {dep.Id} {dep.Version}. "
                        + "The package layout may have changed — re-check transitive deps.");
                }
                string destPath = DepDllPath(dep.Dll);
                ExtractEntry(depNupkg, depEntry, AbsOf(destPath));
                depPaths.Add(destPath);
            }

            // ── Tell Unity to pick up the new files ────────────────────
            // CRITICAL: Lock assembly reloads across the import +
            // PluginImporter-configure pass. Without this, the Refresh
            // call would trigger an immediate domain reload (because we
            // just dropped managed DLLs into Assets/) and our Configure
            // calls would run in a tearing-down AppDomain. Locking
            // defers the reload until we Unlock at the end.
            onProgress?.Invoke(0.85f, "Importing into Unity...");
            EditorApplication.LockReloadAssemblies();
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                onProgress?.Invoke(0.92f, "Configuring plugin settings...");
                ConfigureManagedPlugin(ManagedDllPath);
                foreach (var p in depPaths)
                    ConfigureManagedPlugin(p);

                // Set the reopen flag LAST, after every Configure has
                // succeeded. The InitializeOnLoad hook below reads it
                // post-reload and reopens the optimizer window.
                EditorPrefs.SetBool(ReopenFlagPref, true);
            }
            finally
            {
                // Unlock outside the try so we never leave the editor
                // permanently locked even if something throws. The
                // unlock triggers the queued domain reload — the next
                // tick has the new assemblies loaded and the reopen
                // hook runs.
                EditorApplication.UnlockReloadAssemblies();
            }

            onProgress?.Invoke(1f, "Done.");
        }

        /// <summary>
        /// Remove every downloaded artifact under Editor/. Used at the
        /// start of InstallSync so a partial / corrupted previous install
        /// can't keep Unity's plugin cache in a half-loaded state. Safe
        /// to call when nothing is installed yet (just no-ops).
        /// </summary>
        public static void CleanInstallArtifacts()
        {
            string editorAbs = AbsOf(EditorFolder);
            if (!Directory.Exists(editorAbs)) return;

            // Wipe the main package + every transitive dep, and their
            // .meta files. We don't blanket-delete the Editor folder
            // itself because Unity uses the folder GUID for organization;
            // recreating it fresh would change the GUID and could leak
            // a "broken reference" warning into the next session.
            SafeDelete(Path.Combine(editorAbs, "OggVorbisEncoder.dll"));
            SafeDelete(Path.Combine(editorAbs, "OggVorbisEncoder.dll.meta"));
            foreach (var dep in TransitiveDeps)
            {
                SafeDelete(Path.Combine(editorAbs, dep.Dll));
                SafeDelete(Path.Combine(editorAbs, dep.Dll + ".meta"));
            }
        }

        private static void SafeDelete(string absolutePath)
        {
            try { if (File.Exists(absolutePath)) File.Delete(absolutePath); }
            catch { /* ignore */ }
        }

        // ─── Download / extract helpers ─────────────────────────────────

        private static byte[] DownloadNupkg(string pkgId, string version, Action<float> onProgress)
        {
            // NuGet flat-container URLs require lowercase package IDs.
            string lower = pkgId.ToLowerInvariant();
            string url = $"{NuGetBase}/{lower}/{version}/{lower}.{version}.nupkg";

            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 60;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    onProgress?.Invoke(op.progress);
                    Thread.Sleep(50);
                }

#if UNITY_2020_2_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
                    throw new Exception($"Couldn't download {pkgId} {version} from NuGet: {req.error}");
#else
                if (req.isHttpError || req.isNetworkError)
                    throw new Exception($"Couldn't download {pkgId} {version} from NuGet: {req.error}");
#endif
                return req.downloadHandler.data;
            }
        }

        /// <summary>
        /// Scan the nupkg for OggVorbisEncoder.dll under any lib/{TFM}/
        /// folder. Returns the entry path of whichever target framework
        /// is present, preferring modern netstandard → older TFMs.
        /// Returns null if no DLL is found (caller throws a clear error).
        /// </summary>
        private static string FindDllEntry(byte[] nupkgBytes)
        {
            // Preference order: newer netstandard wins over older,
            // netstandard wins over net4x.
            string[] preferred =
            {
                "lib/netstandard2.0/OggVorbisEncoder.dll",
                "lib/netstandard20/OggVorbisEncoder.dll",
                "lib/netstandard2.1/OggVorbisEncoder.dll",
                "lib/netstandard1.3/OggVorbisEncoder.dll",
                "lib/netstandard1.0/OggVorbisEncoder.dll",
                "lib/net45/OggVorbisEncoder.dll",
                "lib/net40/OggVorbisEncoder.dll",
            };

            using (var ms = new MemoryStream(nupkgBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var p in preferred)
                {
                    if (FindEntry(zip, p) != null) return p;
                }

                // Last resort: any lib/*/OggVorbisEncoder.dll. NuGet has
                // shipped weird TFM strings in the past.
                var fallback = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith("/OggVorbisEncoder.dll", StringComparison.OrdinalIgnoreCase));
                return fallback?.FullName;
            }
        }

        /// <summary>
        /// Generic version of <see cref="FindDllEntry"/> for transitive
        /// dependency packages — same TFM preference order, just
        /// parameterized on the DLL filename.
        /// </summary>
        private static string FindDllEntryByName(byte[] nupkgBytes, string dllName)
        {
            string[] preferredFolders =
            {
                "lib/netstandard2.0/",
                "lib/netstandard20/",
                "lib/netstandard2.1/",
                "lib/netstandard1.3/",
                "lib/netstandard1.0/",
                "lib/net461/",
                "lib/net45/",
                "lib/net40/",
            };

            using (var ms = new MemoryStream(nupkgBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var folder in preferredFolders)
                {
                    var entry = FindEntry(zip, folder + dllName);
                    if (entry != null) return entry.FullName;
                }
                // Last resort: any lib/*/<dllName>.
                var fallback = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith("/" + dllName, StringComparison.OrdinalIgnoreCase));
                return fallback?.FullName;
            }
        }

        /// <summary>
        /// Extract <paramref name="entryPath"/> from the nupkg byte buffer
        /// to <paramref name="destAbsolutePath"/>.
        /// </summary>
        private static void ExtractEntry(byte[] zipBytes, string entryPath, string destAbsolutePath)
        {
            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = FindEntry(zip, entryPath);
                if (entry == null)
                    throw new FileNotFoundException(
                        "Couldn't find '" + entryPath + "' inside the NuGet package.");

                Directory.CreateDirectory(Path.GetDirectoryName(destAbsolutePath));
                using (var dest = File.Create(destAbsolutePath))
                using (var src = entry.Open())
                {
                    src.CopyTo(dest);
                }
            }
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, string path)
        {
            var direct = zip.GetEntry(path);
            if (direct != null) return direct;
            foreach (var e in zip.Entries)
                if (string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }

        // ─── PluginImporter configuration ───────────────────────────────

        private static void ConfigureManagedPlugin(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null) return;

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            // Every player platform off — the encoder is editor-only.
            foreach (BuildTarget bt in Enum.GetValues(typeof(BuildTarget)))
            {
                if (bt == BuildTarget.NoTarget) continue;
                try { importer.SetCompatibleWithPlatform(bt, false); }
                catch { /* obsolete enum values — ignore */ }
            }
            importer.SaveAndReimport();
        }

        // ─── Misc helpers ───────────────────────────────────────────────

        private static string AbsOf(string assetPath)
            => Path.GetFullPath(assetPath);

        private static void EnsureGitignore()
        {
            // Unconditionally rewrite — the .gitignore is auto-managed
            // and we own its contents. Previous versions only listed the
            // OggVorbisEncoder DLL; the bump to include transitive deps
            // means we have to refresh existing .gitignores too.
            string gitignorePath = AbsOf(Root + "/.gitignore");
            Directory.CreateDirectory(AbsOf(Root));
            File.WriteAllText(gitignorePath,
                "# OggVorbisEncoder + transitive deps — auto-downloaded per-machine\n" +
                "# by OggVorbisEncoderInstaller.cs on first use of the Audio Optimizer.\n" +
                "# Keep the repo small: don't commit the DLLs.\n" +
                "Editor/OggVorbisEncoder*.dll\n" +
                "Editor/OggVorbisEncoder*.dll.meta\n" +
                "Editor/System.Memory.dll\n" +
                "Editor/System.Memory.dll.meta\n" +
                "Editor/System.Buffers.dll\n" +
                "Editor/System.Buffers.dll.meta\n" +
                "Editor/System.Runtime.CompilerServices.Unsafe.dll\n" +
                "Editor/System.Runtime.CompilerServices.Unsafe.dll.meta\n");
        }
    }

    /// <summary>
    /// Domain-reload hook: when the installer finishes downloading the DLL,
    /// it sets <see cref="OggVorbisEncoderInstaller.ReopenFlagPref"/> and
    /// calls AssetDatabase.Refresh. Unity recompiles, our scripts reload,
    /// and this static ctor runs in the fresh domain — at which point the
    /// new encoder assembly is loaded. We delayCall the window open so
    /// the editor finishes its first-tick layout before we draw.
    /// </summary>
    [InitializeOnLoad]
    internal static class OggVorbisEncoderInstallerReopenHook
    {
        static OggVorbisEncoderInstallerReopenHook()
        {
            if (!EditorPrefs.GetBool(OggVorbisEncoderInstaller.ReopenFlagPref, false)) return;
            EditorPrefs.DeleteKey(OggVorbisEncoderInstaller.ReopenFlagPref);
            EditorApplication.delayCall += () => AudioOptimizerWindow.Open();
        }
    }
}
#endif
