#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark.PreUploadChecks
{
    // Interactive Transfer/Copy resolver for one OutsideContentFolderCheck violation.
    // Opened from that finding's "Resolve dependencies…" fix action.
    //
    // WHY A SEPARATE TOOL FROM THE SINGLE "Move into content folder" FIX
    //
    // CollectOffenders (OutsideContentFolderCheck.cs) deliberately stops at the first
    // out-of-folder asset it finds — "an out-of-folder asset's own dependencies are
    // that asset's problem," which is correct for a FINDING (one row per violation,
    // no noise) but wrong for FIXING one: moving only the top offender leaves ITS own
    // out-of-folder dependencies exactly where they were. Nothing breaks immediately
    // — AssetDatabase.MoveAsset preserves GUIDs, so references keep resolving either
    // way — but those dependencies are still assets this content needs, still outside
    // any content folder, and still subject to the same two failure modes the check
    // exists for: a .cs among them still won't ship in the .unitypackage or link.xml,
    // and a non-script asset among them is still up for grabs by SmartBundleGrouper's
    // alphabetical-first bundle assignment. This tool walks that whole sub-tree and
    // resolves it in one pass instead of one finding at a time.
    //
    // WHY TRANSFER ISN'T ALWAYS RIGHT
    //
    // A move physically relocates the file under THIS content package's folder while
    // preserving its GUID, so every existing reference — from this content, from
    // another content package, from anywhere in the project — keeps resolving. That
    // is exactly the danger: the dependency still works, but the file is no longer
    // where anyone exporting or bundling the OTHER package would expect it, and
    // SmartBundleGrouper now assigns its bundle ownership to this package instead.
    // Copy is the right answer whenever something outside this package needs the
    // asset to stay exactly where it is: each package ends up owning its own
    // physical, independently-exportable file.
    //
    // DEFAULT SIGNAL
    //
    // A project-wide scan for `guid: <hex>` references, modelled on
    // ThirdPartyLocalDeduplicator's "IndexConsumers" pass — one walk over every
    // YAML-text asset in the project, not one walk per item. Deliberately NOT that
    // tool's exact regex: ThirdPartyLocalDeduplicator's GuidLineRegex requires the
    // WHOLE line to be `guid: <hex>`, which only matches a .meta file's own identity
    // declaration. Real asset-to-asset references are Unity's normal flow-style PPtr
    // form — `{fileID: X, guid: Y, type: Z}` — verified against this project's own
    // files (Assets/DreamPark/Materials/Glow.mat:24, :63; any .prefab's m_Script
    // line), where guid: sits mid-line with more content on both sides. An anchored
    // per-line regex would silently miss almost every real reference and this tool
    // would default everything to Transfer regardless of actual sharing. The regex
    // below is unanchored so it matches guid: wherever it appears.
    //
    // The check's own "cross-content-folder" signal (BuildDetail's "This belongs to a
    // DIFFERENT content package" case) only fires when the offending asset's OWN
    // location happens to be inside another content package's folder. It says
    // nothing about whether a stray Assets/SomeVendor/ asset is ALSO used by a
    // completely unrelated non-content prefab, or by another content package that
    // reaches it directly rather than nesting it. This scan is the general case:
    // does ANYTHING outside this content package reference this GUID, regardless of
    // where that something lives.
    //
    // A reference from a sibling prop/attraction inside the SAME content package
    // (not part of this finding's crawl) is NOT "shared" for this purpose — the
    // asset is about to be homed inside this package's own ThirdParty/ folder either
    // way, so that sibling keeps working. Only a reference from OUTSIDE the package
    // means Copy is the safer default.
    public class OutsideContentDependencyResolverPopup : EditorWindow
    {
        private const float WindowWidth = 640f;
        private const float WindowHeight = 560f;

        // YAML-text Unity asset extensions that can contain a guid reference.
        // Mirrors ThirdPartyLocalDeduplicator.ConsumerExtensions so the two tools
        // agree about what counts as "a file that might reference an asset."
        private static readonly HashSet<string> ConsumerExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".mat", ".asset", ".controller", ".anim",
            ".overrideController", ".physicMaterial", ".physicsMaterial2D",
            ".cubemap", ".flare", ".renderTexture", ".mask", ".signal",
            ".playable", ".mixer", ".shadergraph", ".shadersubgraph",
            ".terrainlayer", ".brush", ".preset", ".lighting", ".spriteatlas",
            ".guiskin", ".fontsettings", ".meta",
        };

        // Unanchored on purpose — see the header note above. Matches guid: wherever
        // it appears on a line, which is what real flow-style PPtr references need.
        private static readonly Regex GuidRegex = new Regex(@"guid:\s*([0-9a-fA-F]{32})");

        private enum ItemAction { Transfer, Copy, Skip }

        private class Item
        {
            public string sourcePath;
            public string guid;

            // True when this item must never be auto-relocated at all (protected
            // top-level folder, SDK-shaped folder, or no resolvable GUID) — same
            // CanOfferMove test the check itself uses before offering "Move into
            // content folder". Locked items are Skip-only.
            public bool locked;
            public string lockedReason;

            public List<string> outsideConsumers = new List<string>();  // drives the Copy default
            public List<string> insideConsumers = new List<string>();   // repointed after a Copy

            public ItemAction action;
        }

        private string rootAssetPath;
        private string offenderPath;
        private string contentId;
        private string contentRoot;
        private List<Item> items = new List<Item>();
        private Vector2 scroll;
        private bool busy;
        private bool scanFailed;
        private Action<bool> onComplete;
        private bool completionReported;
        private string applyBackupDir;

        // ------------------------------------------------------------------
        // Show

        public static void Show(string rootAssetPath, string offenderPath, string contentId,
                                string contentRoot, Action<bool> onComplete = null)
        {
            // The resolver rewrites serialized YAML on disk. Never let a later save
            // of an already-open dirty scene overwrite those GUID replacements.
            if (!EnsureScenesSaved())
            {
                if (onComplete != null) onComplete(false);
                return;
            }
            AssetDatabase.SaveAssets();

            var win = CreateInstance<OutsideContentDependencyResolverPopup>();
            win.titleContent = new GUIContent("Resolve Dependencies");
            win.minSize = new Vector2(560f, 420f);
            win.maxSize = new Vector2(900f, 900f);

            var main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(
                main.x + (main.width - WindowWidth) / 2f,
                main.y + (main.height - WindowHeight) / 2f,
                WindowWidth, WindowHeight);

            win.rootAssetPath = rootAssetPath;
            win.offenderPath = offenderPath;
            win.contentId = contentId;
            win.contentRoot = contentRoot;
            win.onComplete = onComplete;
            win.Scan();

            win.ShowUtility();
        }

        private void OnDestroy()
        {
            ReportCompletion(false);
        }

        private void ReportCompletion(bool changed)
        {
            if (completionReported) return;
            completionReported = true;
            var callback = onComplete;
            onComplete = null;
            if (callback != null) callback(changed);
        }

        private void Scan()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Resolve Dependencies", "Mapping dependency chain…", 0.05f);
                items = BuildMoveSet(offenderPath, contentId, contentRoot);

                EditorUtility.DisplayProgressBar("Resolve Dependencies", "Scanning project for other consumers…", 0.3f);
                ComputeConsumers(items, contentRoot);

                foreach (var item in items)
                {
                    if (item.locked) { item.action = ItemAction.Skip; continue; }
                    // Pre-selected default: Transfer unless something outside this
                    // content package would lose the asset out from under it.
                    item.action = item.outsideConsumers.Count > 0 ? ItemAction.Copy : ItemAction.Transfer;
                }
                scanFailed = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not map dependencies for '{offenderPath}': {e}");
                items = new List<Item>();
                scanFailed = true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ------------------------------------------------------------------
        // Crawl — BFS over the offender's own dependency chain, reusing
        // OutsideContentFolderCheck's Classify/CanOfferMove exactly so this tool
        // never disagrees with what the check itself would flag on the next scan.
        private static List<Item> BuildMoveSet(string offenderPath, string contentId, string contentRoot)
        {
            var result = new List<Item>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { offenderPath };
            var queue = new Queue<string>();
            queue.Enqueue(offenderPath);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();

                var item = new Item
                {
                    sourcePath = current,
                    guid = AssetDatabase.AssetPathToGUID(current),
                };

                if (string.IsNullOrEmpty(item.guid))
                {
                    item.locked = true;
                    item.lockedReason = "Could not resolve a GUID for this asset — handle it by hand.";
                }
                else if (!Checks.OutsideContentFolderCheck.CanOfferMove(current))
                {
                    item.locked = true;
                    item.lockedReason = "Lives in a protected or SDK-shaped folder — never auto-relocated. "
                                       + "Move it by hand if it is genuinely your content.";
                }

                result.Add(item);

                string[] deps;
                try { deps = AssetDatabase.GetDependencies(current, false); }
                catch { continue; }

                foreach (var dep in deps)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (string.Equals(dep, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!visited.Add(dep)) continue;

                    // Already shipping — nothing to do.
                    if (ContentRootScanner.IsUnderContentRoot(dep, contentRoot)) continue;

                    // Same classification the check itself uses. Only a Violation
                    // needs to move — Allowed (foundation/packages/editor-only/
                    // builtin) is fine where it is, and Informational
                    // (ThirdPartyLocal, protected top-level) either resolves itself
                    // before the build or is project infrastructure this tool must
                    // never touch.
                    if (Checks.OutsideContentFolderCheck.Classify(dep, contentId)
                        != Checks.OutsideContentFolderCheck.Verdict.Violation)
                        continue;

                    queue.Enqueue(dep);
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Consumer scan — one project-wide pass covering every item's GUID at once,
        // splitting hits into "inside this content package" (repointed after a Copy)
        // and "outside" (the signal that defaults an item to Copy in the first
        // place). Modelled on ThirdPartyLocalDeduplicator.IndexConsumers.
        private static void ComputeConsumers(List<Item> items, string contentRoot)
        {
            var byGuid = items.Where(i => !string.IsNullOrEmpty(i.guid))
                               .ToDictionary(i => i.guid, i => i, StringComparer.OrdinalIgnoreCase);
            if (byGuid.Count == 0) return;

            // Two assets that are BOTH about to move together don't count as
            // "referenced by someone else" just because one mentions the other's
            // GUID — at scan time both still live outside the content folder, so
            // without this exclusion a material moving alongside its own texture
            // would make the texture look shared purely because the material
            // (itself also moving) names it.
            var movingPaths = new HashSet<string>(items.Select(i => i.sourcePath), StringComparer.OrdinalIgnoreCase);

            // An asset's own .meta file DECLARES that asset's GUID — identity, not a
            // reference. Without this exclusion every item would look "consumed" by
            // its own .meta.
            var selfMetaPaths = new HashSet<string>(items.Select(i => i.sourcePath + ".meta"), StringComparer.OrdinalIgnoreCase);

            string assetsRoot = Application.dataPath;
            int processed = 0;

            foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                processed++;
                if ((processed & 4095) == 0)
                    EditorUtility.DisplayProgressBar("Resolve Dependencies",
                        "Scanning project for other consumers…",
                        0.3f + 0.6f * Mathf.Min(1f, processed / 100000f));

                string ext = Path.GetExtension(file);
                if (!ConsumerExtensions.Contains(ext)) continue;

                string relPath = "Assets" + file.Substring(assetsRoot.Length).Replace('\\', '/');
                if (movingPaths.Contains(relPath)) continue;
                if (selfMetaPaths.Contains(relPath)) continue;

                string content;
                try { content = File.ReadAllText(file); }
                catch { continue; }

                bool insidePackage = ContentRootScanner.IsUnderContentRoot(relPath, contentRoot);

                foreach (Match m in GuidRegex.Matches(content))
                {
                    string g = m.Groups[1].Value.ToLowerInvariant();
                    Item item;
                    if (!byGuid.TryGetValue(g, out item)) continue;

                    if (insidePackage) item.insideConsumers.Add(relPath);
                    else item.outsideConsumers.Add(relPath);
                }
            }

            foreach (var item in items)
            {
                item.insideConsumers = item.insideConsumers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                item.outsideConsumers = item.outsideConsumers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // ------------------------------------------------------------------
        // GUI

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Resolve Dependencies", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{Path.GetFileName(offenderPath)} and its own out-of-content-folder dependencies, "
              + $"found from {Path.GetFileName(rootAssetPath)}.",
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(4f);

            if (scanFailed)
            {
                EditorGUILayout.HelpBox("Could not map dependencies. See the console.", MessageType.Warning);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            if (items.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing to resolve.", MessageType.Info);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUI.DisabledScope(busy))
            {
                foreach (var item in items)
                    DrawItem(item);
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(6f);
            DrawFooter();
        }

        private void DrawItem(Item item)
        {
            GUILayout.Space(2f);
            GUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(item.sourcePath, EditorStyles.miniLabel);

            if (item.locked)
            {
                EditorGUILayout.LabelField("Skip — " + item.lockedReason, EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                GUILayout.BeginHorizontal();

                var options = new[] { "Transfer", "Copy", "Skip" };
                int chosen = EditorGUILayout.Popup((int)item.action, options, GUILayout.Width(90f));
                item.action = (ItemAction)chosen;

                if (item.action == ItemAction.Skip)
                {
                    GUILayout.Label("— stays where it is", EditorStyles.miniLabel);
                }
                else
                {
                    string target = Checks.OutsideContentFolderCheck.TargetPathFor(item.sourcePath, contentId);
                    GUILayout.Label("→ " + target, EditorStyles.miniLabel);
                }

                GUILayout.EndHorizontal();

                if (item.outsideConsumers.Count > 0)
                {
                    string names = string.Join(", ", item.outsideConsumers.Take(3).Select(Path.GetFileName));
                    if (item.outsideConsumers.Count > 3)
                        names += $", +{item.outsideConsumers.Count - 3} more";
                    EditorGUILayout.LabelField(
                        $"Also referenced outside this content package by: {names}. Defaulted to Copy so that "
                      + "reference is left alone.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawFooter()
        {
            int transferring = items.Count(i => i.action == ItemAction.Transfer);
            int copying = items.Count(i => i.action == ItemAction.Copy);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Cancel", GUILayout.Height(30f), GUILayout.Width(90f)))
                {
                    Close();
                    return;
                }
            }

            GUILayout.Space(6f);

            using (new EditorGUI.DisabledScope(busy || (transferring == 0 && copying == 0)))
            {
                string label = $"Apply ({transferring} transfer, {copying} copy)";
                if (GUILayout.Button(label, GUILayout.Height(30f), GUILayout.Width(230f)))
                {
                    bool confirmed = EditorUtility.DisplayDialog(
                        "Resolve Dependencies",
                        $"{transferring} asset(s) will be MOVED into "
                      + $"{ContentRootScanner.RootFor(contentId)}/ThirdParty/, preserving their GUIDs — every "
                      + "existing reference, including from other content packages, keeps resolving.\n\n"
                      + $"{copying} asset(s) will be COPIED there as new, independent assets with new GUIDs. "
                      + "This content package's own references are repointed at the copy; references from "
                      + "anywhere else are left alone, pointing at the original.\n\n"
                      + "Cannot be undone with Ctrl-Z. Use version control to revert.",
                        "Apply", "Cancel");
                    if (confirmed) Apply();
                }
            }

            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------
        // Apply

        private void Apply()
        {
            // The window can remain open while the creator keeps working, so repeat
            // the dirty-scene guard immediately before the raw YAML mutation.
            if (!EnsureScenesSaved()) return;
            AssetDatabase.SaveAssets();

            busy = true;
            int transferred = 0, copied = 0, failed = 0;
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            applyBackupDir = Path.Combine(projectRoot, "Library", "DreamPark",
                "OutsideContentResolver", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"), "backup");

            try
            {
                DreamPark.ContentProcessor.ExecuteWithWatchdogPaused(() =>
                {
                    foreach (var item in items)
                    {
                        if (item.action == ItemAction.Skip) continue;

                        try
                        {
                            if (item.action == ItemAction.Transfer)
                            {
                                string target = Checks.OutsideContentFolderCheck.TargetPathFor(item.sourcePath, contentId);
                                if (Checks.OutsideContentFolderCheck.MoveAsset(item.sourcePath, target)) transferred++;
                                else failed++;
                            }
                            else
                            {
                                if (CopyAndRepoint(item)) copied++;
                                else failed++;
                            }
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Debug.LogWarning($"[DreamPark] Resolve dependencies: '{item.sourcePath}' failed: {e}");
                        }
                    }
                });
            }
            finally
            {
                busy = false;
            }

            if (failed > 0)
                Debug.LogWarning($"[DreamPark] Resolve dependencies: {failed} item(s) did not complete — see warnings above.");
            if (transferred > 0 || copied > 0)
                Debug.Log($"[DreamPark] Resolve dependencies for '{offenderPath}': {transferred} transferred, {copied} copied.");

            PreUploadCheckRunner.InvalidateCache(contentId);
            ReportCompletion(transferred > 0 || copied > 0);
            Close();
        }

        private static bool EnsureScenesSaved()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

            // Unity returns true for both Save and Don't Save. Don't Save leaves the
            // loaded scene dirty, which could later overwrite our on-disk GUID edits.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (!SceneManager.GetSceneAt(i).isDirty) continue;
                EditorUtility.DisplayDialog(
                    "Save scenes before resolving",
                    "The dependency resolver cannot safely rewrite scene references while a loaded "
                  + "scene still has unsaved changes. Save or close the dirty scene, then try again.",
                    "OK");
                return false;
            }
            return true;
        }

        // Copy mints a new GUID (AssetDatabase.CopyAsset, unlike MoveAsset, does NOT
        // preserve it), so the copy is inert — nothing points at it yet — until every
        // reference this CONTENT PACKAGE owns is rewritten to the new GUID.
        // References from OUTSIDE the package are deliberately left untouched
        // pointing at the original: that is the entire reason Copy exists instead of
        // Transfer.
        //
        // There is no AssetDatabase.ValidateCopyAsset (unlike ValidateMoveAsset for
        // the Transfer path). The closest equivalent dry run: confirm the source
        // still resolves, and that CreateFolderRecursive + ResolveCollision (the same
        // helpers the check's own Move fix uses) produce a destination before
        // anything is written.
        private bool CopyAndRepoint(Item item)
        {
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(item.sourcePath)))
            {
                Debug.LogWarning($"[DreamPark] Cannot copy '{item.sourcePath}': source no longer resolves.");
                return false;
            }

            string target = Checks.OutsideContentFolderCheck.TargetPathFor(item.sourcePath, contentId);
            target = Checks.OutsideContentFolderCheck.ResolveCollision(target);

            string targetDir = Path.GetDirectoryName(target)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(targetDir))
                Checks.OutsideContentFolderCheck.CreateFolderRecursive(targetDir);

            if (!AssetDatabase.CopyAsset(item.sourcePath, target))
            {
                Debug.LogWarning($"[DreamPark] AssetDatabase.CopyAsset failed: '{item.sourcePath}' -> '{target}'.");
                return false;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newGuid = AssetDatabase.AssetPathToGUID(target);
            if (string.IsNullOrEmpty(newGuid))
            {
                Debug.LogWarning($"[DreamPark] Copied '{item.sourcePath}' to '{target}' but could not resolve the "
                                + "new GUID — in-package references were NOT repointed. Repoint them by hand.");
                return false;
            }

            // Re-scan after the copy instead of trusting the pre-apply consumer list.
            // If a copied parent depends on another copied item, that newly-created
            // parent did not exist during Scan(); this pass is what repoints the whole
            // copied dependency chain rather than leaving its children external.
            bool scanSucceeded;
            var consumers = FindInPackageConsumers(item.guid, out scanSucceeded);
            if (!scanSucceeded
                || !RepointInPackageConsumers(item.guid, newGuid, consumers))
            {
                Debug.LogWarning($"[DreamPark] Copied '{item.sourcePath}' to '{target}', but one or more "
                               + "in-package references could not be repointed. Restore from "
                               + $"'{applyBackupDir}' or version control before retrying.");
                return false;
            }

            Debug.Log($"[DreamPark] Copied '{item.sourcePath}' -> '{target}' and repointed "
                    + $"{consumers.Count} in-package reference(s).");
            return true;
        }

        private List<string> FindInPackageConsumers(string guid, out bool succeeded)
        {
            var result = new List<string>();
            succeeded = true;
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(contentRoot)) return result;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string fullRoot = Path.Combine(projectRoot,
                contentRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullRoot)) return result;

            foreach (string file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            {
                if (!ConsumerExtensions.Contains(Path.GetExtension(file))) continue;

                string content;
                try { content = File.ReadAllText(file); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not inspect '{file}' while repointing: {e.Message}");
                    succeeded = false;
                    continue;
                }

                if (!GuidRegex.Matches(content).Cast<Match>()
                    .Any(m => string.Equals(m.Groups[1].Value, guid,
                                            StringComparison.OrdinalIgnoreCase)))
                    continue;

                string relative = "Assets" + file.Substring(Application.dataPath.Length)
                    .Replace('\\', '/');
                result.Add(relative);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Rewrites `guid: <old>` occurrences to `guid: <new>` in every in-package
        // consumer file. Same atomic-write-plus-backup discipline as
        // ThirdPartyLocalDeduplicator.RunApply: a timestamped backup under Library/
        // before every rewrite, so a bad remap is recoverable outside of git too.
        private bool RepointInPackageConsumers(
            string oldGuid, string newGuid, List<string> consumerAssetPaths)
        {
            if (consumerAssetPaths == null || consumerAssetPaths.Count == 0) return true;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            bool succeeded = true;

            foreach (var assetPath in consumerAssetPaths)
            {
                string fullPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

                string content;
                try { content = File.ReadAllText(fullPath); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not read '{assetPath}' to repoint: {e.Message}");
                    succeeded = false;
                    continue;
                }

                string newContent = GuidRegex.Replace(content, m =>
                {
                    string g = m.Groups[1].Value;
                    return string.Equals(g, oldGuid, StringComparison.OrdinalIgnoreCase)
                        ? m.Value.Replace(g, newGuid)
                        : m.Value;
                });

                if (string.Equals(content, newContent, StringComparison.Ordinal)) continue;

                try
                {
                    string backupPath = Path.Combine(applyBackupDir,
                        assetPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    // A file may contain several copied dependency GUIDs. Preserve
                    // its pre-apply state once; later replacements must not overwrite
                    // that recovery point with an already-modified intermediate.
                    if (!File.Exists(backupPath)) File.Copy(fullPath, backupPath);

                    string tmp = fullPath + ".dp_resolve_tmp";
                    File.WriteAllText(tmp, newContent);
                    File.Replace(tmp, fullPath, null);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DreamPark] Could not repoint '{assetPath}': {e.Message}");
                    succeeded = false;
                }
            }

            AssetDatabase.Refresh();
            return succeeded;
        }
    }
}
#endif
