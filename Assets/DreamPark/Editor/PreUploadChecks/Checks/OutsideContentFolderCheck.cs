#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // Assets a content package depends on that do not live inside its content folder.
    //
    // WHY THIS MATTERS
    //
    // BuildUnityPackage exports only the t:Script assets under
    // Assets/Content/{contentId} plus the generated link.xml, and
    // ContentLinkXmlGenerator scopes link.xml the same way — so a script outside the
    // folder ships in neither, producing the "Could not produce class with ID X"
    // failures that ContentLinkXmlGenerator's header documents at length. For
    // non-script assets the mechanism is Addressables: SmartBundleGrouper assigns
    // bundle ownership by content-folder scoping, and its own comment records what
    // happens to anything it cannot see:
    //
    //   "…invisible to ownership assignment, so Unity's bundle builder would silently
    //    pack it into whichever bundle it built first — typically an attraction,
    //    alphabetically first — instead of the prop that actually owns it. Result was
    //    4 KB prop bundles with all their texture/material content drained into the
    //    host attraction."
    //
    // WHY DEVELOPERS KEEP GETTING THIS WRONG — from PackageRelocator's header:
    //
    //   ".unitypackage files hardcode their import paths in metadata, so Unity always
    //    lands them at Assets/<VendorName>/ regardless of what folder was selected at
    //    import time."
    //
    // Unity actively works against the folder convention. That is why this reports
    // rather than scolds, and why it offers to do the move.
    //
    // Shipped at Warning, not Blocking. The allowlist is the entire check: get it
    // wrong and this fires on every prefab in every project, and then people learn to
    // ignore it. Collect a release of real-world data before considering an
    // escalation.
    public sealed class OutsideContentFolderCheck : IPreUploadCheck
    {
        public const string CheckId = "outside-content-folder";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Dependencies outside the content folder"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // Dependency crawling is Unity-cached and reasonably quick, so it can run on
        // panel open.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "Only assets under Assets/Content/{game}/ are guaranteed to ship. Anything else "
                     + "is either dropped or silently packed into the wrong bundle. Assets/DreamPark, "
                     + "Unity packages and built-in resources are expected and not reported.";
            }
        }

        // Deliberately expected outside the content folder. These are promoted into
        // the Shared-Foundation bundle on purpose — SDK shaders, Occlusion.mat, URP
        // fallback shaders, ShaderGraph's Hidden/* shaders. Mirrors
        // SmartBundleGrouper.FoundationPathPrefixes.
        private static readonly string[] FoundationPrefixes =
        {
            "Assets/DreamPark/",
            "Packages/com.unity.render-pipelines.universal/",
            "Packages/com.unity.render-pipelines.core/",
            "Packages/com.unity.shadergraph/",
        };

        // Unity's built-in assets. NOTHING in this codebase handles these by name —
        // they fall out of every existing filter implicitly, by not matching a
        // positive rule. A naive "not under the content root" test surfaces
        // Default-Material, the Cube mesh and the default font on essentially every
        // prefab, which would make this check useless on day one.
        private static readonly string[] BuiltInPaths =
        {
            "Resources/unity_builtin_extra",
            "Library/unity default resources",
            "Library/unity_builtin_extra",
        };

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            var directDepsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count),
                             $"Crawling dependencies of {root.name}…");
                i++;

                Dictionary<string, List<string>> offenders;
                try
                {
                    offenders = CollectOffenders(root.assetPath, ctx.contentRoot, directDepsCache);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Dependency crawl failed for {root.assetPath}: {e.Message}");
                    continue;
                }

                foreach (var kv in offenders)
                {
                    string depPath = kv.Key;
                    List<string> chain = kv.Value;

                    var verdict = Classify(depPath, ctx.contentId);
                    if (verdict == Verdict.Allowed) continue;

                    var finding = new Finding
                    {
                        checkId = CheckId,
                        severity = verdict == Verdict.Informational
                            ? CheckSeverity.Info
                            : CheckSeverity.Warning,
                        assetGuid = root.guid,
                        assetPath = root.assetPath,
                        subKey = depPath,
                        title = $"{root.name} depends on {Path.GetFileName(depPath)} (outside the content folder)",
                        detail = BuildDetail(root, depPath, chain, verdict, ctx.contentId),
                    };

                    if (verdict == Verdict.Violation && CanOfferMove(depPath))
                    {
                        string target = TargetPathFor(depPath, ctx.contentId);
                        string source = depPath;
                        finding.fixes.Add(new FixAction(
                            "Move into content folder",
                            () => MoveAsset(source, target))
                        {
                            tooltip = "Uses AssetDatabase.MoveAsset, which preserves the GUID, so every "
                                    + "existing reference stays valid.",
                            confirmTitle = "Move asset",
                            confirmMessage =
                                $"Move\n  {source}\nto\n  {target}\n\n"
                              + "GUIDs are preserved, so references from every project file stay valid — "
                              + "including from OTHER content packages, which will now point into this "
                              + "one's folder. If this asset is shared, duplicate it instead.\n\n"
                              + "Cannot be undone with Ctrl-Z. Use version control to revert.",
                        });
                    }

                    string pingPath = depPath;
                    finding.fixes.Add(FixAction.Navigate("Select asset", () =>
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(pingPath);
                        if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                    }));

                    findings.Add(finding);
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static string BuildDetail(ContentRootInfo root, string depPath,
                                          List<string> chain, Verdict verdict, string contentId)
        {
            string via = chain != null && chain.Count > 1
                ? "\n\nReached via: " + string.Join(" → ", chain.Select(Path.GetFileName))
                : "";

            switch (verdict)
            {
                case Verdict.Informational:
                    if (ContentRootScanner.IsThirdPartyLocal(depPath))
                        return $"{depPath}\n\nThis lives in ThirdPartyLocal, which is gitignored and "
                             + "build-excluded. The upload flow already moves referenced ThirdPartyLocal "
                             + "assets into ThirdParty/ before the Addressables build, so this resolves "
                             + "itself — expect the churn in git." + via;

                    return $"{depPath}\n\nThis is in a protected top-level folder (project "
                         + "infrastructure or an installed SDK), so no move is offered. If it is "
                         + "genuinely your content, move it by hand." + via;

                default:
                    string cross = depPath.StartsWith(ContentRootScanner.ContentFolder + "/",
                                                      StringComparison.OrdinalIgnoreCase)
                        ? "\n\nThis belongs to a DIFFERENT content package. Cross-content references do "
                        + "not ship — the other package's bundle is not loaded alongside this one."
                        : "";

                    return $"{depPath}\n\nOnly assets under Assets/Content/{contentId}/ are guaranteed to "
                         + "ship. This one will either be dropped or packed into another package's "
                         + "bundle." + cross + via;
            }
        }

        // ------------------------------------------------------------------
        // Crawl

        // BFS over direct dependencies, tracking the path from the root so the finding
        // can say HOW the asset is reached.
        //
        // Modelled on SmartBundleGrouper.CollectExclusiveDeps, with one deliberate
        // difference: no rootSet. The original stops at other attraction/prop roots
        // because a root's subtree belongs to that root. For this check that would
        // hide exactly what we're looking for — an attraction referencing P_Coin which
        // itself references Assets/SomeVendor/coin.mat would have the violation
        // swallowed at the root boundary. Each root is crawled independently and
        // reports its own violations.
        private static Dictionary<string, List<string>> CollectOffenders(
            string startRoot, string contentRoot, Dictionary<string, string[]> directDepsCache)
        {
            var offenders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startRoot };
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(startRoot);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();

                string[] directDeps;
                if (!directDepsCache.TryGetValue(current, out directDeps))
                {
                    try
                    {
                        directDeps = AssetDatabase.GetDependencies(current, false);
                    }
                    catch
                    {
                        // Throws on assets mid-import. ContentProcessor's own sweep
                        // swallows this the same way.
                        continue;
                    }
                    directDepsCache[current] = directDeps;
                }

                foreach (var dep in directDeps)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (string.Equals(dep, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!visited.Add(dep)) continue;

                    parent[dep] = current;

                    if (ContentRootScanner.IsUnderContentRoot(dep, contentRoot))
                    {
                        queue.Enqueue(dep);
                        continue;
                    }

                    // Outside the folder. Record it, and don't crawl through it — an
                    // out-of-folder asset's own dependencies are that asset's problem,
                    // and following them buries the finding that matters in noise.
                    if (!offenders.ContainsKey(dep))
                        offenders[dep] = ChainTo(dep, parent, startRoot);
                }
            }

            return offenders;
        }

        private static List<string> ChainTo(string dep, Dictionary<string, string> parent, string root)
        {
            var chain = new List<string>();
            string cur = dep;
            int guard = 0;
            while (!string.IsNullOrEmpty(cur) && guard++ < 64)
            {
                chain.Add(cur);
                if (string.Equals(cur, root, StringComparison.OrdinalIgnoreCase)) break;
                string next;
                if (!parent.TryGetValue(cur, out next)) break;
                cur = next;
            }
            chain.Reverse();
            return chain;
        }

        // ------------------------------------------------------------------
        // Classification

        private enum Verdict { Allowed, Informational, Violation }

        private static Verdict Classify(string path, string contentId)
        {
            if (string.IsNullOrEmpty(path)) return Verdict.Allowed;

            // --- SmartBundleGrouper.ShouldSkipAsDep, reproduced ---
            if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return Verdict.Allowed;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".asmdef" || ext == ".asmref" || ext == ".dll" || ext == ".meta")
                return Verdict.Allowed;

            // --- Editor-only assets never ship ---
            if (path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0) return Verdict.Allowed;

            // --- Foundation: expected, and deliberately promoted ---
            foreach (var prefix in FoundationPrefixes)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Verdict.Allowed;

            // --- Unity built-ins ---
            foreach (var builtIn in BuiltInPaths)
                if (path.IndexOf(builtIn, StringComparison.OrdinalIgnoreCase) >= 0) return Verdict.Allowed;

            if (AssetDatabase.IsValidFolder(path)) return Verdict.Allowed;

            // --- Scripts. Checked AFTER foundation/Editor/package exclusions. ---
            //
            // A .cs outside the content folder is the headline failure this whole check
            // exists for: BuildUnityPackage exports only the t:Script assets under
            // Assets/Content/{contentId}, and ContentLinkXmlGenerator scopes link.xml
            // the same way, so the script ships in NEITHER and the content throws
            // "Could not produce class with ID X" at a venue.
            if (ext == ".cs") return Verdict.Violation;

            // --- ThirdPartyLocal: auto-resolved before the build, not an error ---
            if (ContentRootScanner.IsThirdPartyLocal(path)) return Verdict.Informational;

            // --- Protected top-level folders: project infrastructure ---
            if (IsProtectedTopLevel(path)) return Verdict.Informational;

            return Verdict.Violation;
        }

        private static bool IsProtectedTopLevel(string path)
        {
            string top = TopLevelFolder(path);
            if (string.IsNullOrEmpty(top)) return false;
            if (string.Equals(top, "Content", StringComparison.Ordinal)) return false;  // handled elsewhere
            return global::DreamPark.EditorTools.PackageRelocator.ProtectedTopLevelFolders.Contains(top);
        }

        private static string TopLevelFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return null;

            string rest = assetPath.Substring("Assets/".Length);
            int slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : null;
        }

        // ------------------------------------------------------------------
        // Move

        private static bool CanOfferMove(string depPath)
        {
            // Never offer to relocate an installed SDK or another content package's
            // folder wholesale. PackageRelocator's content-based safety net catches
            // vendor packs that aren't on the protected list.
            string top = TopLevelFolder(depPath);
            if (string.IsNullOrEmpty(top)) return false;
            if (global::DreamPark.EditorTools.PackageRelocator.ProtectedTopLevelFolders.Contains(top))
                return false;

            // LooksLikeImportedAssetPack returns FALSE for SDK-shaped folders (a root
            // package.json, asmdef + Runtime/ + Editor/, a BuildingBlocks/ dir,
            // Core/ + Editor/). Those are installed SDKs that happen not to be on the
            // protected allowlist yet — never offer to relocate a file out of one.
            try
            {
                if (!global::DreamPark.EditorTools.PackageRelocator
                        .LooksLikeImportedAssetPack("Assets/" + top))
                    return false;
            }
            catch
            {
                // The folder may not exist on disk (a dependency on a sub-asset path).
                // Fall through and let the dev decide.
            }

            return true;
        }

        // Mirrors ThirdPartySyncTool's convention: preserve the source structure under
        // the content folder's ThirdParty/. Deliberately NOT type-sorted into Models/
        // Textures/ Materials/ — those folder names drive the Addressables address
        // ({gameId}/{TypeFolder}/{name}), so auto-sorting would mint addresses the dev
        // never asked for.
        private static string TargetPathFor(string depPath, string contentId)
        {
            string rest = depPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? depPath.Substring("Assets/".Length)
                : Path.GetFileName(depPath);

            return $"{ContentRootScanner.ContentFolder}/{contentId}/ThirdParty/{rest}";
        }

        private static bool MoveAsset(string source, string target)
        {
            try
            {
                target = ResolveCollision(target);

                string targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir))
                    CreateFolderRecursive(targetDir.Replace('\\', '/'));

                // Dry-run first. ValidateMoveAsset has zero uses anywhere in this
                // codebase today and is exactly what you want before touching someone's
                // project.
                string invalid = AssetDatabase.ValidateMoveAsset(source, target);
                if (!string.IsNullOrEmpty(invalid))
                {
                    Debug.LogWarning($"[DreamPark] Cannot move {source} → {target}: {invalid}");
                    return false;
                }

                // Returns an error STRING, not a bool. Empty means success.
                string err = AssetDatabase.MoveAsset(source, target);
                if (!string.IsNullOrEmpty(err))
                {
                    Debug.LogWarning($"[DreamPark] Move failed: {err}");
                    return false;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[DreamPark] Moved '{source}' → '{target}' (GUID preserved).");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not move {source}: {e}");
                return false;
            }
        }

        private static string ResolveCollision(string target)
        {
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(target))) return target;

            string dir = Path.GetDirectoryName(target)?.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);

            for (int n = 2; n < 1000; n++)
            {
                string candidate = $"{dir}/{stem}_{n}{ext}";
                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(candidate)))
                {
                    Debug.LogWarning($"[DreamPark] '{Path.GetFileName(target)}' already exists at the "
                                   + $"destination; using '{Path.GetFileName(candidate)}'.");
                    return candidate;
                }
            }
            return target;
        }

        // AssetDatabase.CreateFolder only creates ONE level, so walk up then build
        // downward. (PackageRelocator has an equivalent private helper.)
        private static void CreateFolderRecursive(string folderAssetPath)
        {
            if (string.IsNullOrEmpty(folderAssetPath)) return;
            if (AssetDatabase.IsValidFolder(folderAssetPath)) return;

            string parent = Path.GetDirectoryName(folderAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                CreateFolderRecursive(parent);

            string leaf = Path.GetFileName(folderAssetPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
                AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
