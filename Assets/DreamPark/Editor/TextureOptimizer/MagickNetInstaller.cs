#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DreamPark.EditorTools.TextureOptimization
{
    /// <summary>
    /// Auto-installs Magick.NET on first use of the Texture Optimizer. The
    /// goal is "creator opens the tool, it just works" — no NuGet client,
    /// no UPM package, no manual DLL drop, no checked-in binaries bloating
    /// the SDK repo.
    ///
    /// What gets installed and where:
    ///
    ///   Assets/DreamPark/ThirdParty/MagickNet/
    ///     .gitignore                              (committed, ignores siblings)
    ///     Editor/
    ///       Magick.NET-Q8-AnyCPU.dll              (managed, ~6 MB)
    ///       Magick.NET.Core.dll                   (managed, ~0.1 MB)
    ///       Native/{host-rid}/
    ///         Magick.Native-Q8-arm64.dll.dylib    (host-only, ~22 MB)
    ///
    /// Everything except the .gitignore is downloaded per-machine on first
    /// use. The .gitignore is committed so accidental adds don't push 28 MB
    /// of binaries to anyone else's clone.
    ///
    /// We pull from NuGet's flat-container CDN (api.nuget.org), pinned to a
    /// known-good Magick.NET version so a future API break doesn't surprise
    /// us. Bumping the pin is a one-line edit + a manual install on a clean
    /// machine to verify.
    ///
    /// License note: Magick.NET is Apache 2.0. Auto-downloading at first
    /// use is fine; the LICENSE is left in the unzipped folder for
    /// attribution.
    /// </summary>
    public static class MagickNetInstaller
    {
        // Pinned version. Bumping: change here, delete an installed
        // ThirdParty/MagickNet/Editor folder on a test machine, open the
        // Texture Optimizer, confirm the new download + the executor's
        // reflection still binds (Magick.NET hasn't broken its public API).
        public const string PinnedVersion = "14.11.0";

        private const string NuGetBase = "https://api.nuget.org/v3-flatcontainer";

        // Asset-database-relative install paths. Kept public so the
        // bootstrap can probe them with File.Exists at the right places.
        public const string Root = "Assets/DreamPark/ThirdParty/MagickNet";
        public const string EditorFolder = Root + "/Editor";
        public const string NativeFolderBase = EditorFolder + "/Native";

        public static string ManagedDllPath => EditorFolder + "/Magick.NET-Q8-AnyCPU.dll";
        public static string CoreDllPath    => EditorFolder + "/Magick.NET.Core.dll";

        /// <summary>
        /// EditorPrefs flag set right before AssetDatabase.Refresh kicks
        /// off the domain reload that loads the new DLLs. A separate
        /// [InitializeOnLoad] reads this flag on next domain ready and
        /// re-opens the Texture Optimizer window, so the install feels
        /// like "click → progress → ready" with no second click required.
        /// </summary>
        public const string ReopenFlagPref = "DreamPark.TextureOptimizer.PendingReopen";

        // ─── Public API ─────────────────────────────────────────────────

        /// <summary>
        /// True when every required file is on disk for the current host.
        /// Cheap — just a few File.Exists calls.
        /// </summary>
        public static bool IsInstalled()
        {
            if (!File.Exists(AbsOf(ManagedDllPath))) return false;
            if (!File.Exists(AbsOf(CoreDllPath)))    return false;

            try
            {
                var host = DetectHost();
                return File.Exists(AbsOf(NativeBinaryAssetPath(host)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Synchronously download + extract + configure. Blocks the editor
        /// thread, but reports progress via <paramref name="onProgress"/>
        /// so the caller can keep its progress bar fresh. ~30-60 seconds
        /// on a typical broadband connection.
        ///
        /// Sets <see cref="ReopenFlagPref"/> and calls AssetDatabase.Refresh
        /// at the end, which triggers a domain reload — the caller's
        /// window will close, and the reopen hook below reopens it on the
        /// next editor tick.
        /// </summary>
        public static void InstallSync(Action<float, string> onProgress)
        {
            var host = DetectHost();
            onProgress?.Invoke(0.01f, "Preparing install folder...");

            // Always start from a clean slate. If a previous install
            // failed midway (e.g. extracting the native binary), Q8.dll
            // may be on disk without Core.dll, and Unity's plugin-
            // reference cache holds a "Q8 has unresolved reference"
            // state that survives partial re-installs. The cheapest
            // reliable fix is to delete every Magick.NET artifact first
            // and import fresh.
            CleanInstallArtifacts();

            Directory.CreateDirectory(AbsOf(EditorFolder));
            Directory.CreateDirectory(AbsOf(NativeFolderBase + "/" + host.runtimeId));
            EnsureGitignore();
            Debug.Log("[TextureOptimizer] Installing Magick.NET " + PinnedVersion + " for " + host.runtimeId + "...");

            // ── Magick.NET-Q8-AnyCPU: managed DLL + native binary ──────
            onProgress?.Invoke(0.05f, "Downloading Magick.NET-Q8-AnyCPU...");
            byte[] qNupkg = DownloadNupkg("Magick.NET-Q8-AnyCPU", PinnedVersion,
                p => onProgress?.Invoke(0.05f + 0.40f * p, "Downloading Magick.NET-Q8-AnyCPU..."));

            onProgress?.Invoke(0.45f, "Extracting managed DLL...");
            ExtractEntry(qNupkg,
                "lib/netstandard20/Magick.NET-Q8-AnyCPU.dll",
                AbsOf(ManagedDllPath),
                fallbackEntry: "lib/netstandard2.0/Magick.NET-Q8-AnyCPU.dll");

            onProgress?.Invoke(0.50f, "Extracting native binary for " + host.runtimeId + "...");
            ExtractEntry(qNupkg,
                $"runtimes/{host.runtimeId}/native/{host.nativeFileName}",
                AbsOf(NativeBinaryAssetPath(host)));

            // ── Resolve Magick.NET.Core version from the Q8 nuspec ────
            // Earlier drafts assumed Core ships at the same SemVer as
            // Q8-AnyCPU — true historically, not guaranteed. The nuspec
            // declares the exact dependency version, so read it and
            // honor it.
            string coreVersion = FindCoreDependencyVersion(qNupkg, fallback: PinnedVersion);

            onProgress?.Invoke(0.55f, $"Downloading Magick.NET.Core {coreVersion}...");
            byte[] coreNupkg = DownloadNupkg("Magick.NET.Core", coreVersion,
                p => onProgress?.Invoke(0.55f + 0.20f * p, $"Downloading Magick.NET.Core {coreVersion}..."));

            onProgress?.Invoke(0.77f, "Extracting Magick.NET.Core...");
            ExtractEntry(coreNupkg,
                "lib/netstandard20/Magick.NET.Core.dll",
                AbsOf(CoreDllPath),
                fallbackEntry: "lib/netstandard2.0/Magick.NET.Core.dll");

            // ── Tell Unity to pick up the new files ────────────────────
            // CRITICAL: Lock assembly reloads across the import +
            // PluginImporter-configure pass. Without this, the Refresh
            // call would trigger an immediate domain reload (because
            // we just dropped managed DLLs into Assets/) and our
            // Configure calls would run in a tearing-down AppDomain.
            // Locking defers the reload until we Unlock at the end.
            onProgress?.Invoke(0.82f, "Importing into Unity...");
            EditorApplication.LockReloadAssemblies();
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                onProgress?.Invoke(0.90f, "Configuring plugin settings...");
                ConfigureManagedPlugin(ManagedDllPath);
                ConfigureManagedPlugin(CoreDllPath);
                ConfigureNativePlugin(NativeBinaryAssetPath(host), host);

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
                // tick has the new Magick.NET assemblies loaded and
                // the reopen hook runs.
                EditorApplication.UnlockReloadAssemblies();
            }

            onProgress?.Invoke(1f, "Done.");
        }

        public static string NativeBinaryAssetPath(HostInfo host)
            => $"{NativeFolderBase}/{host.runtimeId}/{host.nativeFileName}";

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

            // Drop the managed DLLs + their .meta files. We don't delete
            // the Editor folder itself because Unity uses the folder GUID
            // for organization — recreating it fresh would change the
            // GUID and could leak as a "broken reference" warning.
            foreach (var dll in Directory.GetFiles(editorAbs, "Magick.NET*.dll", SearchOption.TopDirectoryOnly))
                SafeDelete(dll);
            foreach (var meta in Directory.GetFiles(editorAbs, "Magick.NET*.dll.meta", SearchOption.TopDirectoryOnly))
                SafeDelete(meta);

            // Drop the entire Native folder — host detection might have
            // changed (e.g. user moved from Intel Mac to Apple Silicon)
            // and we don't want stale binaries lingering.
            string nativeAbs = AbsOf(NativeFolderBase);
            if (Directory.Exists(nativeAbs))
            {
                try { Directory.Delete(nativeAbs, recursive: true); } catch { /* ignore */ }
                try { File.Delete(nativeAbs + ".meta"); } catch { /* ignore */ }
            }
        }

        private static void SafeDelete(string absolutePath)
        {
            try { if (File.Exists(absolutePath)) File.Delete(absolutePath); }
            catch { /* ignore */ }
        }

        // ─── Host detection ─────────────────────────────────────────────

        public struct HostInfo
        {
            public string runtimeId;       // "osx-arm64" / "osx-x64" / "win-x64" / "linux-x64"
            public string nativeFileName;  // "Magick.Native-Q8-arm64.dll.dylib" etc.
            public string editorOsLabel;   // PluginImporter.SetEditorData("OS", ...)
            public string editorCpuLabel;  // PluginImporter.SetEditorData("CPU", ...)
        }

        public static HostInfo DetectHost()
        {
            // Important detail on the native filenames below: Magick.NET
            // uses `.dll.dylib` / `.dll.so` extensions deliberately. The
            // managed DllImport call references "Magick.Native-Q8-X.dll"
            // and .NET's runtime appends the platform suffix when
            // looking up the actual file. Unity sees the final extension
            // (.dylib / .so / .dll) and treats the file as a native
            // plugin for that platform.
            var p = Application.platform;
            var arch = RuntimeInformation.OSArchitecture;

            if (p == RuntimePlatform.OSXEditor)
            {
                if (arch == Architecture.Arm64)
                    return new HostInfo
                    {
                        runtimeId = "osx-arm64",
                        nativeFileName = "Magick.Native-Q8-arm64.dll.dylib",
                        editorOsLabel = "OSX",
                        editorCpuLabel = "ARM64",
                    };
                return new HostInfo
                {
                    runtimeId = "osx-x64",
                    nativeFileName = "Magick.Native-Q8-x64.dll.dylib",
                    editorOsLabel = "OSX",
                    editorCpuLabel = "x86_64",
                };
            }
            if (p == RuntimePlatform.WindowsEditor)
            {
                if (arch == Architecture.Arm64)
                    return new HostInfo
                    {
                        runtimeId = "win-arm64",
                        nativeFileName = "Magick.Native-Q8-arm64.dll",
                        editorOsLabel = "Windows",
                        editorCpuLabel = "ARM64",
                    };
                return new HostInfo
                {
                    runtimeId = "win-x64",
                    nativeFileName = "Magick.Native-Q8-x64.dll",
                    editorOsLabel = "Windows",
                    editorCpuLabel = "x86_64",
                };
            }
            if (p == RuntimePlatform.LinuxEditor)
            {
                if (arch == Architecture.Arm64)
                    return new HostInfo
                    {
                        runtimeId = "linux-arm64",
                        nativeFileName = "Magick.Native-Q8-arm64.dll.so",
                        editorOsLabel = "Linux",
                        editorCpuLabel = "ARM64",
                    };
                return new HostInfo
                {
                    runtimeId = "linux-x64",
                    nativeFileName = "Magick.Native-Q8-x64.dll.so",
                    editorOsLabel = "Linux",
                    editorCpuLabel = "x86_64",
                };
            }
            throw new InvalidOperationException(
                "Unsupported Unity Editor host: " + p + ". Texture Optimizer supports macOS, Windows, and Linux editors.");
        }

        // ─── Download / extract helpers ─────────────────────────────────

        private static byte[] DownloadNupkg(string pkgId, string version, Action<float> onProgress)
        {
            // NuGet flat-container URLs require lowercase package IDs.
            string lower = pkgId.ToLowerInvariant();
            string url = $"{NuGetBase}/{lower}/{version}/{lower}.{version}.nupkg";

            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 120;
                var op = req.SendWebRequest();
                // Poll the request — UnityWebRequest in the editor doesn't
                // honor await in a non-PlayMode context cleanly, so we
                // poll and sleep. The progress bar caller refreshes the
                // editor UI in between.
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
        /// Find <paramref name="entryPath"/> inside the .nupkg zip and
        /// write its contents to <paramref name="destAbsolutePath"/>.
        /// NuGet packages store paths case-insensitively but with
        /// inconsistent casing across versions — we accept a fallback
        /// path (e.g. netstandard20 vs netstandard2.0) before giving up.
        /// </summary>
        private static void ExtractEntry(byte[] zipBytes, string entryPath, string destAbsolutePath, string fallbackEntry = null)
        {
            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = FindEntry(zip, entryPath) ?? (fallbackEntry != null ? FindEntry(zip, fallbackEntry) : null);
                if (entry == null)
                {
                    throw new FileNotFoundException(
                        $"Couldn't find '{entryPath}' inside the NuGet package. "
                        + "The package layout may have changed in a newer version — "
                        + "bump MagickNetInstaller.PinnedVersion or report the issue.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destAbsolutePath));
                using (var dest = File.Create(destAbsolutePath))
                using (var src = entry.Open())
                {
                    src.CopyTo(dest);
                }
            }
        }

        /// <summary>
        /// Parse the .nuspec at the root of <paramref name="nupkgBytes"/>
        /// and return the version of the declared Magick.NET.Core
        /// dependency. NuGet ranges like "[14.11.0]" get normalized to
        /// "14.11.0". Returns <paramref name="fallback"/> on any parse
        /// failure — caller is responsible for the consequence (we'll
        /// 404 on the Core download and surface a clear error).
        /// </summary>
        private static string FindCoreDependencyVersion(byte[] nupkgBytes, string fallback)
        {
            try
            {
                using (var ms = new MemoryStream(nupkgBytes))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    // The .nuspec lives at the zip root with a filename
                    // like "Magick.NET-Q8-AnyCPU.nuspec". Pick whichever
                    // entry has a .nuspec extension at the root level.
                    var nuspec = zip.Entries.FirstOrDefault(e =>
                        !e.FullName.Contains('/') &&
                        e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                    if (nuspec == null) return fallback;

                    using (var stream = nuspec.Open())
                    {
                        var doc = XDocument.Load(stream);
                        if (doc.Root == null) return fallback;

                        // .nuspec is namespaced; XML uses the package
                        // metadata schema. Grab the default namespace
                        // off the root and look for any <dependency> with
                        // id="Magick.NET.Core".
                        XNamespace ns = doc.Root.GetDefaultNamespace();
                        var dep = doc.Descendants(ns + "dependency")
                            .FirstOrDefault(d => string.Equals(
                                (string)d.Attribute("id"),
                                "Magick.NET.Core",
                                StringComparison.OrdinalIgnoreCase));
                        if (dep == null) return fallback;

                        string ver = (string)dep.Attribute("version") ?? fallback;
                        // Strip NuGet version-range brackets and pick
                        // the first concrete version if a range was
                        // declared (e.g. "[14.11.0,15.0)" → "14.11.0").
                        ver = ver.Trim('[', ']', '(', ')');
                        if (ver.Contains(','))
                            ver = ver.Split(',')[0].Trim();
                        return string.IsNullOrEmpty(ver) ? fallback : ver;
                    }
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, string path)
        {
            // First try exact match.
            var direct = zip.GetEntry(path);
            if (direct != null) return direct;

            // Then case-insensitive scan — NuGet is loose with casing.
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
            // Defensive: every player platform off. Even if iOS doesn't
            // see managed code, having compat=false documents intent.
            foreach (BuildTarget bt in Enum.GetValues(typeof(BuildTarget)))
            {
                if (bt == BuildTarget.NoTarget) continue;
                try { importer.SetCompatibleWithPlatform(bt, false); }
                catch { /* obsolete enum values — ignore */ }
            }
            importer.SaveAndReimport();
        }

        private static void ConfigureNativePlugin(string assetPath, HostInfo host)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null) return;

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);

            // Tell Unity which editor host this native binary is for so
            // it loads on the right machine and ignores it elsewhere.
            importer.SetEditorData("OS", host.editorOsLabel);
            importer.SetEditorData("CPU", host.editorCpuLabel);

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
            // Sit the .gitignore at the root of ThirdParty/MagickNet/ so
            // every downloaded binary under Editor/ is ignored. The
            // installer source code itself lives under DreamPark/Editor/
            // (not under ThirdParty/MagickNet/), so it stays committed.
            string gitignorePath = AbsOf(Root + "/.gitignore");
            if (File.Exists(gitignorePath)) return;

            Directory.CreateDirectory(AbsOf(Root));
            File.WriteAllText(gitignorePath,
                "# Magick.NET binaries — auto-downloaded per-machine by\n" +
                "# MagickNetInstaller.cs on first use of the Texture Optimizer.\n" +
                "# Keep the repo small: don't commit the DLLs or natives.\n" +
                "Editor/Magick.NET*.dll\n" +
                "Editor/Magick.NET*.dll.meta\n" +
                "Editor/Native/\n" +
                "Editor/Native.meta\n");
        }
    }

    /// <summary>
    /// Domain-reload hook: when the installer finishes downloading DLLs,
    /// it sets <see cref="MagickNetInstaller.ReopenFlagPref"/> and calls
    /// AssetDatabase.Refresh. Unity recompiles, our scripts reload, and
    /// this static ctor runs in the fresh domain — at which point the
    /// new Magick.NET assembly is loaded. We delayCall the window open
    /// so the editor finishes its first-tick layout before we draw.
    /// </summary>
    [InitializeOnLoad]
    internal static class MagickNetInstallerReopenHook
    {
        static MagickNetInstallerReopenHook()
        {
            if (!EditorPrefs.GetBool(MagickNetInstaller.ReopenFlagPref, false)) return;
            EditorPrefs.DeleteKey(MagickNetInstaller.ReopenFlagPref);
            EditorApplication.delayCall += () => TextureOptimizerWindow.Open();
        }
    }
}
#endif
