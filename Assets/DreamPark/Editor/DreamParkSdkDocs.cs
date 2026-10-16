#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Ships the creator/agent docs (CLAUDE.md, PACKAGES.md, RELEASE.md and the specs it
    // references; see ShippedDocs) inside every SDK .unitypackage, and keeps a game
    // project's copies fresh.
    //
    // WHY: the package is Assets/DreamPark ONLY, and these docs live at the SDK
    // repo root, so a game project kept whatever copy it was cloned with — agents
    // in older projects were reading months-stale rules.
    //
    // EXPORT (SDK source project): StageForExport() copies the root docs into
    // Assets/DreamPark/Docs with DETERMINISTIC .meta GUIDs, the caller exports, and
    // disposing deletes the folder again — so the SDK source never carries a second,
    // drifting copy. Fixed GUIDs make every later package replace
    // the files in place instead of importing "CLAUDE 1.md".
    //
    // IMPORT (game projects): once per SDK version (ProjectSettings/DreamParkSdkDocs.json,
    // which also holds the opt-out), the SDK-owned root docs are
    // rewritten from Assets/DreamPark/Docs (they carry a "managed" header — edit the
    // SDK, not the copy), and the game's own CLAUDE.md gets a managed block that
    // @imports Assets/DreamPark/Docs/CLAUDE.md. Everything below that block belongs
    // to the game and is never touched.
    [InitializeOnLoad]
    public static class DreamParkSdkDocs
    {
        public const string DocsFolder = "Assets/DreamPark/Docs";

        // The docs a creator or agent in a GAME project needs. Paths are repo-relative
        // and keep their folders. Deliberately NOT shipped: SDK-maintainer material
        // (release, review and plan notes) and READMEs for Tools/ that are not in the package.
        public static readonly string[] ShippedDocs =
        {
            "CLAUDE.md",                     // the agent/creator guide; imported by the game's CLAUDE.md
            "README.md",                     // SDK overview (stays in Docs/, never replaces the game's README)
            "PACKAGES.md",                   // Arena / Sequence / Adventure creator guide
            "RELEASE.md",                    // how agents upload (agent keys, dreampark_content_publish)
            "PIPELINE.md",                   // end-to-end shipping checklist
            "LAYOUT_FRAMES_SPEC.md",         // referenced by CLAUDE.md
            "WALL_LAYERS_SPEC.md",           // referenced by CLAUDE.md
            "PACKAGE_RUNTIME_SPEC.md",       // referenced by CLAUDE.md / PACKAGES.md
            "HANDHELD.md",                   // hand-held objects: device wrist frame, Grip/Muzzle convention, archetypes, dp.hold, HandheldKit
            "ROUTES.md",                     // dp.ports / dp.neighbors / dp.route / dp.tour (Routes phase 1, Oct 2026)
            "NOTICE.md",                     // third-party notices for the bundled components
        };

        // Staged by older SDK exports and no longer shipped. ApplyToProject removes them
        // from a game project's Docs folder (SDKUpgradeCleanup also retires the files)
        // so a stale copy is never re-applied to the project root.
        private static readonly string[] RetiredDocFolders = { "AgentKit", "Tools" };

        /// <summary>Repo-relative paths of the shipped docs that exist in this checkout.</summary>
        public static string[] SourceDocs()
            => ShippedDocs.Where(rel => File.Exists(Path.Combine(ProjectRoot, rel))).ToArray();

        private const string BlockBegin = "<!-- dreampark-sdk-docs:begin -->";
        private const string BlockEnd = "<!-- dreampark-sdk-docs:end -->";
        private const string ManagedHeader =
            "<!-- Managed by the DreamPark SDK: copied from Assets/DreamPark/Docs on every SDK update. Edits here are overwritten. -->";

        static DreamParkSdkDocs()
        {
            EditorApplication.delayCall += () =>
            {
                try { ApplyToProject(); }
                catch (Exception e) { Debug.LogWarning("[DreamPark] Could not refresh SDK docs: " + e.Message); }
            };
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // ── Export ─────────────────────────────────────────────────────────

        /// <summary>Stage the root docs into the package folder for one export.</summary>
        public static IDisposable StageForExport()
        {
            string folderFull = Path.Combine(ProjectRoot, DocsFolder);
            bool existed = Directory.Exists(folderFull);
            Directory.CreateDirectory(folderFull);
            if (!File.Exists(folderFull + ".meta"))
                File.WriteAllText(folderFull + ".meta", FolderMeta(StableGuid(DocsFolder)));

            int staged = 0;
            foreach (string rel in SourceDocs())
            {
                string target = Path.Combine(folderFull, rel);
                EnsureFolderWithMeta(folderFull, Path.GetDirectoryName(rel));
                File.WriteAllText(target, File.ReadAllText(Path.Combine(ProjectRoot, rel)));
                File.WriteAllText(target + ".meta", TextMeta(StableGuid(DocsFolder + "/" + rel)));
                staged++;
            }
            AssetDatabase.ImportAsset(DocsFolder,
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[DreamPark] Staged {staged} SDK doc file(s) into {DocsFolder} for export.");
            var cleanup = new Cleanup(existed);

            // Secret gate: every export path (Publish, CLI publish, local export) stages
            // through here, so this is the one place the package is checked before
            // ExportPackage runs. Scans exactly what ships: Assets/DreamPark plus the
            // docs staged above. Findings name files, never matched text.
            var problems = global::DreamPark.Security.DreamParkSecretScan.ScanSdkPackage(
                Path.Combine(ProjectRoot, "Assets", "DreamPark"));
            if (problems.Count > 0)
            {
                cleanup.Dispose();
                string report = "SDK export blocked: the package would ship secret material or private documents.\n  - " +
                                string.Join("\n  - ", problems) +
                                "\nRemove these from Assets/DreamPark (and the staged root docs), then export again.";
                Debug.LogError("[DreamPark] " + report);
                throw new InvalidOperationException(report);
            }
            return cleanup;
        }

        // Nested folders also need fixed GUIDs, or each export mints new ones.
        private static void EnsureFolderWithMeta(string docsFull, string relDir)
        {
            if (string.IsNullOrEmpty(relDir)) return;
            string acc = "";
            foreach (string part in relDir.Replace('\\', '/').Split('/'))
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                string dir = Path.Combine(docsFull, acc);
                Directory.CreateDirectory(dir);
                if (!File.Exists(dir + ".meta"))
                    File.WriteAllText(dir + ".meta", FolderMeta(StableGuid(DocsFolder + "/" + acc)));
            }
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly bool keep;
            private bool done;
            public Cleanup(bool keep) { this.keep = keep; }
            public void Dispose()
            {
                if (done || keep) return;
                done = true;
                AssetDatabase.DeleteAsset(DocsFolder);
            }
        }

        // ── Import (game projects) ─────────────────────────────────────────

        /// <summary>Refresh this project's root docs from the shipped copies.</summary>
        public static void ApplyToProject()
        {
            string docsFull = Path.Combine(ProjectRoot, DocsFolder);
            if (!Directory.Exists(docsFull)) return;
            // The SDK source/template itself (placeholder never renamed): its root
            // docs ARE the source, never overwrite them from a staged copy.
            if (AssetDatabase.IsValidFolder("Assets/Content/YOUR_GAME_HERE")) return;

            int removed = RemoveRetiredDocFolders();

            // Once per SDK version, not every domain reload: a project that edits or
            // declines a managed file keeps that choice until the next SDK update.
            // ProjectSettings/DreamParkSdkDocs.json is committed, so the whole team
            // (and every agent) shares it. Set "enabled": false to opt out entirely.
            var state = SyncState.Load();
            if (!state.enabled) return;
            string version = SDKVersion.Current ?? "";
            if (state.appliedVersion == version) return;

            int refreshed = removed;
            foreach (string shipped in Directory.GetFiles(docsFull, "*.md", SearchOption.AllDirectories))
            {
                string rel = shipped.Substring(docsFull.Length).TrimStart('/', '\\').Replace('\\', '/');
                // The game's own CLAUDE.md (handled below) and README.md are never replaced.
                if (rel == "CLAUDE.md" || rel == "README.md") continue;
                string content = ManagedHeader + "\n\n" + File.ReadAllText(shipped);
                string target = Path.Combine(ProjectRoot, rel);
                if (File.Exists(target) && File.ReadAllText(target) == content) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, content);
                refreshed++;
            }
            if (EnsureClaudeImport(docsFull)) refreshed++;
            state.appliedVersion = version;
            state.Save();
            if (refreshed > 0)
                Debug.Log($"[DreamPark] Refreshed {refreshed} project doc file(s) from SDK {version} ({DocsFolder}). "
                    + "Opt out with \"enabled\": false in " + SyncState.RelativePath + ".");
        }

        [Serializable]
        private sealed class SyncState
        {
            public const string RelativePath = "ProjectSettings/DreamParkSdkDocs.json";
            public bool enabled = true;
            public string appliedVersion = "";

            private static string FullPath => Path.Combine(ProjectRoot, RelativePath);

            public static SyncState Load()
            {
                try
                {
                    if (File.Exists(FullPath))
                        return JsonUtility.FromJson<SyncState>(File.ReadAllText(FullPath)) ?? new SyncState();
                }
                catch (Exception e) { Debug.LogWarning("[DreamPark] Unreadable " + RelativePath + ": " + e.Message); }
                return new SyncState();
            }

            public void Save() => File.WriteAllText(FullPath, JsonUtility.ToJson(this, true) + "\n");
        }

        // Deletes folders older SDKs staged into Docs (see RetiredDocFolders). Runs before
        // the version check, so it also cleans projects whose docs are already current.
        private static int RemoveRetiredDocFolders()
        {
            int removed = 0;
            foreach (string name in RetiredDocFolders)
            {
                string folder = DocsFolder + "/" + name;
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                if (AssetDatabase.DeleteAsset(folder)) removed++;
                else Debug.LogWarning("[DreamPark] Could not remove retired SDK folder " + folder);
            }
            return removed;
        }

        private static bool EnsureClaudeImport(string docsFull)
        {
            string shipped = Path.Combine(docsFull, "CLAUDE.md");
            if (!File.Exists(shipped)) return false;
            string block = BlockBegin + "\n"
                + "<!-- Managed by the DreamPark SDK and refreshed on SDK updates: the import below is the current SDK guide. Put this game's own notes BELOW the end marker. -->\n"
                + "@" + DocsFolder + "/CLAUDE.md\n"
                + "\n"
                + "**Precedence:** the import above is the generic DreamPark SDK guide. Where this project's own notes below disagree with it, the project notes win for this project.\n"
                + BlockEnd;

            string target = Path.Combine(ProjectRoot, "CLAUDE.md");
            if (!File.Exists(target))
            {
                File.WriteAllText(target, block + "\n\n# Project notes\n\n");
                return true;
            }

            string current = File.ReadAllText(target);
            int begin = current.IndexOf(BlockBegin, StringComparison.Ordinal);
            int end = current.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (begin >= 0 && end > begin)
            {
                string updated = current.Substring(0, begin) + block + current.Substring(end + BlockEnd.Length);
                if (updated == current) return false;
                File.WriteAllText(target, updated);
                return true;
            }

            // A project cloned from the SDK template carries a full, now-stale copy
            // of the SDK guide. Keep it as a backup (it may hold the creator's own
            // additions) and start a fresh file rather than duplicating old rules.
            string sdkTitle = FirstLine(File.ReadAllText(shipped));
            if (!string.IsNullOrEmpty(sdkTitle) && FirstLine(current) == sdkTitle)
            {
                string backup = Path.Combine(ProjectRoot, "CLAUDE.pre-sdk-docs.md");
                if (!File.Exists(backup)) File.WriteAllText(backup, current);
                File.WriteAllText(target, block + "\n\n# Project notes\n\n"
                    + "> The previous CLAUDE.md was a copy of the SDK guide and is saved as `CLAUDE.pre-sdk-docs.md`."
                    + " Move any notes about this game here, then delete it.\n");
                Debug.LogWarning("[DreamPark] CLAUDE.md was a stale copy of the SDK guide; it now imports the current one. "
                    + "Your old file is saved as CLAUDE.pre-sdk-docs.md — move any game-specific notes back.");
                return true;
            }

            File.WriteAllText(target, block + "\n\n" + current);
            return true;
        }

        private static string FirstLine(string text)
            => (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        // ── Meta helpers ───────────────────────────────────────────────────

        private static string StableGuid(string key)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("dreampark-sdk-docs:" + key));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private static string TextMeta(string guid)
            => "fileFormatVersion: 2\nguid: " + guid + "\nTextScriptImporter:\n  externalObjects: {}\n"
             + "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

        private static string FolderMeta(string guid)
            => "fileFormatVersion: 2\nguid: " + guid + "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
             + "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
    }
}
#endif
