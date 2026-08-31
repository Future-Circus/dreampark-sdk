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
    //
    // CodeOnly is valid for the Lua iteration loop and requires the Smart
    // bundling strategy (the Code carve-out group only exists there). C#
    // code changes can't ship through CodeOnly: compiled scripts live in
    // the Unity player binary and require a full app build.
    //
    // July 2026: PreviewsOnly was removed along with preview bundling
    // itself. Preview PNGs are pushed straight to the backend
    // (POST /api/content/:id/attractions/preview) and are no longer built
    // into Addressables, so there is no Previews bundle to ship — and no
    // preview churn to abort a CodeOnly upload.
    public enum UploadMode
    {
        All = 0,
        Patch = 1,
        CodeOnly = 2,
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
                // Patch needs a baseline, Code-only needs a prior
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
                default:
                    return "";
            }
        }

        // CodeOnly carves the Smart-managed {gameId}-Code group out of the
        // upload set. That group only exists when Smart is active.
        public static bool RequiresSmart(UploadMode m)
        {
            return m == UploadMode.CodeOnly;
        }
    }

    // Categorizes built ServerData files and computes per-mode skip sets so
    // ContentUploaderPanel can hand a clean skipSet to ContentAPI.UploadContent.
    //
    // The categorization is filename-based. Bundle filenames embed their group
    // name via Addressables' AppendHash naming style (e.g. a group named
    // "Park-Code" produces files matching "park-code_assets_…_<hash>.bundle"),
    // which lets us tell the Code bundle apart from gameplay bundles
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
            // Unity package containing C# MonoScripts. Lives at
            // Unity/{contentId}.unitypackage and drives backend
            // codeChangeDetected. Treated separately because C# changes are
            // a "needs full app build" signal, not a hot-patchable bundle.
            UnityPackage = 2,
            // Any other bundle (root prefab bundles, Runtime, MonoScript bundle,
            // Shared bundle if it ever comes back, etc.).
            OtherBundle = 3,
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

            // AppendHash naming: "<groupname-lowercased>_assets_…_<hash>.bundle".
            // Match either form:
            //   "<prefix>_assets_…"   → unchunked, the original single bundle
            //   "<prefix>-N_assets_…" → chunked variant (N = 2, 3, ...) from
            //                            SmartBundleGrouper's hash-bucketing pass
            // Both belong to the same logical Code group, so a CodeOnly
            // upload ships all of its chunks together.
            if (fileName.StartsWith(codePrefix + "_", StringComparison.Ordinal)
                || fileName.StartsWith(codePrefix + "-", StringComparison.Ordinal))
            {
                return FileCategory.CodeBundle;
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
                {
                    // Carve out: only catalog files + the target group's
                    // bundle ship. Every other built file gets added to
                    // skipSet, regardless of whether it changed in the diff.
                    // If a non-target bundle *did* change, the catalog this
                    // build produced references a bundle hash the backend
                    // can't resolve via fallback (the new hash exists nowhere
                    // on disk), so we abort instead of shipping a broken
                    // catalog.
                    const FileCategory targetCategory = FileCategory.CodeBundle;
                    const string targetName = "Code";

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
