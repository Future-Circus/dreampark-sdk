#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEditor;

namespace DreamPark
{
    // How much of the just-built ServerData should ship on this run.
    //
    // All        — full re-upload. Every file in ServerData goes up, no diff
    //              skip, baseline irrelevant. Used to repair a divergent
    //              baseline or push a clean slate.
    // Patch      — current default. Diff ServerData vs the last successful
    //              upload's baseline and only ship changed files. Catalog
    //              always ships (its content hash changes every build).
    //              Backend addressables-fallback fills in unchanged bundles
    //              from prior versions.
    // CodeOnly   — Smart-strategy only. Ships just the {gameId}-Code bundle
    //              (game Lua) plus the catalog. Aborts if any non-Code bundle
    //              also changed locally, since the catalog would then
    //              reference local-only bundle hashes the backend fallback
    //              can't resolve.
    // PreviewsOnly — Smart-strategy only. Same shape as CodeOnly but for the
    //              {gameId}-Previews bundle.
    //
    // Code/Previews-only modes are valid for Lua / preview iteration loops
    // and require the Smart bundling strategy (the carve-out groups only
    // exist there). C# code changes can't ship through CodeOnly: compiled
    // scripts live in the Unity player binary and require a full app build.
    public enum UploadMode
    {
        All = 0,
        Patch = 1,
        CodeOnly = 2,
        PreviewsOnly = 3,
    }

    public static class UploadModePrefs
    {
        public const string PrefKey = "DreamPark.ContentUploader.UploadMode";

        public static UploadMode Current
        {
            get
            {
                // Default is All. It's always safe (full re-upload always works),
                // and it's the only valid mode for a first-upload anyway —
                // Patch needs a baseline, Code/Previews-only need a prior
                // version's bundles to fall back to. Users who want patch
                // semantics by default can flip the picker once and it sticks.
                int v = EditorPrefs.GetInt(PrefKey, (int)UploadMode.All);
                // Defensive: clamp to a valid value if EditorPrefs ever holds
                // garbage (e.g. an older enum value that's since been removed).
                if (!Enum.IsDefined(typeof(UploadMode), v)) return UploadMode.All;
                return (UploadMode)v;
            }
            set
            {
                EditorPrefs.SetInt(PrefKey, (int)value);
            }
        }

        public static string Label(UploadMode m)
        {
            switch (m)
            {
                case UploadMode.All:          return "Upload All (full re-upload)";
                case UploadMode.Patch:        return "Upload Patch (changed files only)";
                case UploadMode.CodeOnly:     return "Upload Code Only (Lua bundle)";
                case UploadMode.PreviewsOnly: return "Upload Previews Only (thumbnails bundle)";
                default:                      return m.ToString();
            }
        }

        public static string ShortLabel(UploadMode m)
        {
            switch (m)
            {
                case UploadMode.All:          return "All";
                case UploadMode.Patch:        return "Patch";
                case UploadMode.CodeOnly:     return "Code only";
                case UploadMode.PreviewsOnly: return "Previews only";
                default:                      return m.ToString();
            }
        }

        public static string Description(UploadMode m)
        {
            switch (m)
            {
                case UploadMode.All:
                    return "Re-upload every file in ServerData regardless of diff. " +
                           "Use this when the local baseline has drifted from the server.";
                case UploadMode.Patch:
                    return "Upload only the files that changed since your last successful " +
                           "upload. Unchanged bundles are served from prior versions on the " +
                           "backend.";
                case UploadMode.CodeOnly:
                    return "Upload just the Lua code bundle and catalog. Requires Smart " +
                           "bundling. Aborts if non-Code bundles also changed — those need a " +
                           "full Patch upload. C# scripts are bundled into the player binary " +
                           "and cannot ship through this mode.";
                case UploadMode.PreviewsOnly:
                    return "Upload just the preview-images bundle and catalog. Requires " +
                           "Smart bundling. Aborts if non-Previews bundles also changed.";
                default:
                    return "";
            }
        }

        // CodeOnly and PreviewsOnly carve specific Smart-managed groups out
        // of the upload set. Those groups only exist when Smart is active.
        public static bool RequiresSmart(UploadMode m)
        {
            return m == UploadMode.CodeOnly || m == UploadMode.PreviewsOnly;
        }
    }

    // Categorizes built ServerData files and computes per-mode skip sets so
    // ContentUploaderPanel can hand a clean skipSet to ContentAPI.UploadContent.
    //
    // The categorization is filename-based. Bundle filenames embed their group
    // name via Addressables' AppendHash naming style (e.g. a group named
    // "Park-Code" produces files matching "park-code_assets_…_<hash>.bundle"),
    // which lets us tell Code/Previews bundles apart from gameplay bundles
    // without parsing the catalog JSON.
    public static class UploadModeFilter
    {
        public enum FileCategory
        {
            // Catalog/hash/settings/link files — never skip; the new version
            // can't function without a complete catalog of its own.
            Catalog = 0,
            // Bundle whose filename matches the {gameId}-Code group prefix.
            CodeBundle = 1,
            // Bundle whose filename matches the {gameId}-Previews group prefix.
            PreviewsBundle = 2,
            // Unity package containing C# MonoScripts. Lives at
            // Unity/{contentId}.unitypackage and drives backend
            // codeChangeDetected. Treated separately because C# changes are
            // a "needs full app build" signal, not a hot-patchable bundle.
            UnityPackage = 3,
            // Any other bundle (root prefab bundles, Misc, MonoScript bundle,
            // Shared bundle if it ever comes back, etc.).
            OtherBundle = 4,
        }

        public static FileCategory Categorize(string contentId, string platformRelativePath)
        {
            if (string.IsNullOrEmpty(platformRelativePath)) return FileCategory.OtherBundle;
            string norm = platformRelativePath.Replace('\\', '/');
            string fileName = System.IO.Path.GetFileName(norm).ToLowerInvariant();

            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".hash", StringComparison.OrdinalIgnoreCase))
            {
                return FileCategory.Catalog;
            }

            // .unitypackage is shipped under a "Unity/" subfolder in
            // ServerData. The MonoScript bundle (Addressables-produced) lives
            // alongside the regular bundles and is *not* this file — it ends
            // in .bundle, not .unitypackage.
            if (fileName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                return FileCategory.UnityPackage;
            }

            if (!fileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown file type — be conservative and treat as Other so
                // mode filters don't accidentally skip something critical.
                return FileCategory.OtherBundle;
            }

            // Derive the expected bundle-filename prefix from
            // SmartBundleGrouper's group-name helpers so this stays in
            // lockstep with the group definitions — if someone renames
            // CodeSuffix from "Code" to something else, both ends move
            // together instead of silently drifting.
            string codePrefix = SmartBundleGrouper.CodeGroupName(contentId).ToLowerInvariant();
            string previewsPrefix = SmartBundleGrouper.PreviewsGroupName(contentId).ToLowerInvariant();

            // AppendHash naming: "<groupname-lowercased>_assets_…_<hash>.bundle".
            // We match against the start of the filename so an asset path
            // that happens to contain "-code" later in the bundle filename
            // (e.g. a folder named "code") doesn't get misclassified.
            if (fileName.StartsWith(codePrefix + "_", StringComparison.Ordinal))
            {
                return FileCategory.CodeBundle;
            }
            if (fileName.StartsWith(previewsPrefix + "_", StringComparison.Ordinal))
            {
                return FileCategory.PreviewsBundle;
            }

            return FileCategory.OtherBundle;
        }

        // Result of a mode-filtered skip-set build. The skipSet keys are
        // "{platform}/{relativePath}" — the same shape ContentAPI.UploadContent
        // expects. blockingError is non-null when the chosen mode is unsafe
        // given the current diff (e.g. CodeOnly was picked but a non-Code
        // bundle changed locally). When blockingError is set, callers should
        // surface it to the user and abort the upload — the skipSet is not
        // populated in that case.
        public class Result
        {
            public HashSet<string> skipSet;
            public string blockingError;
            public int filesToUpload;
            public int filesSkipped;
            public long bytesToUpload;
        }

        // Computes the skipSet for the given mode. Pass the freshly-built
        // current manifest plus the diff vs. baseline (diff may be null when
        // there's no baseline). Returns a Result whose blockingError is
        // non-null when the mode is unsafe to proceed with.
        public static Result Build(
            UploadMode mode,
            string contentId,
            BuildManifest current,
            BuildManifestDiff diff)
        {
            var result = new Result { skipSet = new HashSet<string>(StringComparer.Ordinal) };
            if (current == null) return result;

            switch (mode)
            {
                case UploadMode.All:
                    // Don't populate skipSet — null tells the uploader "ship
                    // everything". We still tally totals for UI feedback.
                    result.skipSet = null;
                    foreach (var p in current.platforms)
                    {
                        result.filesToUpload += p.FileCount;
                        result.bytesToUpload += p.TotalBytes;
                    }
                    return result;

                case UploadMode.Patch:
                    // Standard diff-driven skip. Identical to the pre-existing
                    // BuildManifestStore.BuildSkipSet output, just routed
                    // through this helper for uniformity.
                    if (diff == null)
                    {
                        // No baseline → first upload → ship everything.
                        result.skipSet = null;
                        foreach (var p in current.platforms)
                        {
                            result.filesToUpload += p.FileCount;
                            result.bytesToUpload += p.TotalBytes;
                        }
                        return result;
                    }
                    foreach (var p in diff.platforms)
                    {
                        foreach (var f in p.unchangedFiles)
                        {
                            result.skipSet.Add($"{p.platform}/{f}");
                            result.filesSkipped++;
                        }
                        result.filesToUpload += p.changedFiles.Count;
                        result.bytesToUpload += p.changedBytes;
                    }
                    return result;

                case UploadMode.CodeOnly:
                case UploadMode.PreviewsOnly:
                {
                    // Carve out: only catalog files + the target group's
                    // bundle ship. Every other built file gets added to
                    // skipSet, regardless of whether it changed in the diff.
                    // If a non-target bundle *did* change, the catalog this
                    // build produced references a bundle hash the backend
                    // can't resolve via fallback (the new hash exists nowhere
                    // on disk), so we abort instead of shipping a broken
                    // catalog.
                    FileCategory targetCategory = mode == UploadMode.CodeOnly
                        ? FileCategory.CodeBundle
                        : FileCategory.PreviewsBundle;
                    string targetName = mode == UploadMode.CodeOnly ? "Code" : "Previews";

                    // Pass 1 — safety check against the diff. Catalog files
                    // are exempt (they always change). UnityPackage flagging
                    // a change in CodeOnly mode signals C# edits the user
                    // probably didn't intend to bundle into a Lua hotfix —
                    // surface that as a hard block.
                    if (diff != null)
                    {
                        var offending = new List<string>();
                        bool unityPackageChanged = false;
                        foreach (var p in diff.platforms)
                        {
                            foreach (var changed in p.changedFiles)
                            {
                                var cat = Categorize(contentId, changed);
                                if (cat == FileCategory.Catalog) continue;
                                if (cat == targetCategory) continue;
                                if (cat == FileCategory.UnityPackage)
                                {
                                    unityPackageChanged = true;
                                    continue;
                                }
                                offending.Add($"{p.platform}/{changed}");
                            }
                        }
                        if (offending.Count > 0 || unityPackageChanged)
                        {
                            var msg = new System.Text.StringBuilder();
                            msg.Append($"{targetName}-only upload aborted: ");
                            if (unityPackageChanged)
                            {
                                msg.Append("C# scripts changed (Unity/<contentId>.unitypackage differs from baseline). ");
                                msg.Append("C# code ships inside the player binary, not as a hot-patchable bundle. ");
                                msg.Append("Use Upload Patch (or Upload All) to ship a full release. ");
                            }
                            if (offending.Count > 0)
                            {
                                msg.Append($"{offending.Count} non-{targetName} bundle(s) also changed:");
                                int sample = Math.Min(offending.Count, 5);
                                for (int i = 0; i < sample; i++) msg.Append($"\n  • {offending[i]}");
                                if (offending.Count > sample) msg.Append($"\n  • …and {offending.Count - sample} more.");
                                msg.Append("\nUse Upload Patch to ship every changed bundle, or revert the non-")
                                   .Append(targetName).Append(" changes and try again.");
                            }
                            result.blockingError = msg.ToString();
                            return result;
                        }
                    }

                    // Pass 2 — assemble the skip set:
                    //   - Catalog files: always upload (new build → new hash).
                    //   - Target-group bundles: upload only if the diff marks
                    //     them changed. An unchanged target bundle means the
                    //     content this mode is supposed to ship didn't
                    //     actually move; re-uploading it would just burn
                    //     bandwidth without effect. The catalog still ships,
                    //     and the existing zero-change short-circuit in
                    //     ContentUploaderPanel will catch the "everything
                    //     skipped" case after we return.
                    //   - Everything else: skip.
                    // We consult per-platform unchanged sets from the diff
                    // for the target check. When diff is null (no baseline),
                    // every target bundle counts as changed.
                    var unchangedByPlatform = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    if (diff != null)
                    {
                        foreach (var p in diff.platforms)
                        {
                            var set = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var f in p.unchangedFiles) set.Add(f);
                            unchangedByPlatform[p.platform] = set;
                        }
                    }

                    foreach (var p in current.platforms)
                    {
                        unchangedByPlatform.TryGetValue(p.platform, out var unchangedSet);
                        foreach (var f in p.files)
                        {
                            var cat = Categorize(contentId, f.fileName);
                            bool shouldUpload;
                            if (cat == FileCategory.Catalog)
                            {
                                shouldUpload = true;
                            }
                            else if (cat == targetCategory)
                            {
                                bool unchanged = unchangedSet != null && unchangedSet.Contains(f.fileName);
                                shouldUpload = !unchanged;
                            }
                            else
                            {
                                shouldUpload = false;
                            }

                            if (shouldUpload)
                            {
                                result.filesToUpload++;
                                result.bytesToUpload += f.sizeBytes;
                            }
                            else
                            {
                                result.skipSet.Add($"{p.platform}/{f.fileName}");
                                result.filesSkipped++;
                            }
                        }
                    }
                    return result;
                }
            }

            // Unknown mode — fall through as "ship everything", least
            // destructive option.
            result.skipSet = null;
            return result;
        }
    }
}
#endif
