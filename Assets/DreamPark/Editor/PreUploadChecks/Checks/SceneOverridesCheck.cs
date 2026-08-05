#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark.PreUploadChecks.Checks
{
    // Prefab instances in this package's scenes carrying overrides that were never
    // applied back to the prefab asset.
    //
    // WHY THIS EXISTS
    //
    // The originating incident: a developer had critical Player.prefab changes living
    // as instance overrides in their test scene and shipped the prefab without them.
    //
    // This is invisible to every existing pipeline pass. ".unity" is in
    // ContentProcessor's disallowed-extension list, so scenes are never addressable,
    // never bundled, never stamped — SmartBundleGrouper's own comment says "Unity
    // scenes are deliberately not classified as roots — DreamPark content ships as
    // Addressable prefabs, not scenes." So a scene under a content folder never ships,
    // and overrides authored on an instance in it are simply discarded at build time,
    // silently, with no warning anywhere.
    //
    // Warning, not Blocking: a test scene legitimately poses and tweaks instances, so
    // the false-positive surface is real even after filtering default overrides.
    //
    // Not part of the advisory scan: this is the only check that opens scenes, at
    // roughly 1–5 s each.
    public sealed class SceneOverridesCheck : IPreUploadCheck
    {
        public const string CheckId = "scene-overrides";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Unapplied scene overrides"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }
        public bool RunsInAdvisoryScan { get { return false; } }

        public string Rationale
        {
            get
            {
                return "Scenes never ship — content is delivered as Addressable prefabs. Changes made "
                     + "to a prefab instance in a test scene and not applied back to the prefab are "
                     + "silently discarded at build time. Placing and posing an instance is normal "
                     + "authoring and is not reported.";
            }
        }

        private const string IgnoreRootScalePrefKey = "DreamPark.PreUploadChecks.IgnoreRootScaleOverride";

        private sealed class InstanceHit
        {
            public string sourcePrefabPath;
            public string sourcePrefabGuid;
            public string scenePath;
            public string sceneGuid;
            public string hierarchyPath;
            public List<string> changes = new List<string>();
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            // UNCONDITIONAL. RestoreSceneManagerSetup closes every open scene and
            // reopens it from disk with no save prompt, so running this with unsaved
            // work in the editor destroys that work silently and un-undoably. The
            // upload path calls SaveModifiedScenesBeforeCompile first and so passes
            // this trivially; the "Review…" and "Re-run" buttons do not, and used to
            // be gated only on ctx.scenesAreSaved — which the runner hardcoded to true.
            if (AnyOpenSceneDirty())
            {
                return CheckResult.Skipped(CheckId,
                    "Skipped — you have unsaved scene changes. Save your open scenes (or start an "
                  + "upload, which saves them) so this reads what is actually on disk.");
            }

            var scenePaths = FindScenes(ctx);
            if (scenePaths.Count == 0) return CheckResult.Clean(CheckId);

            var hits = new List<InstanceHit>();

            // Only restore if we actually disturbed the setup. Restoring unconditionally
            // means every clean run still closes and reopens the user's scenes from
            // disk for no reason.
            bool openedAnything = false;

            // Capture BEFORE touching anything.
            SceneSetup[] setup = null;
            try { setup = EditorSceneManager.GetSceneManagerSetup(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not capture the scene setup; skipping the scene "
                               + $"override check rather than risk your open scenes: {e.Message}");
                return CheckResult.Skipped(CheckId, "Skipped — could not snapshot the current scene setup.");
            }

            try
            {
                for (int i = 0; i < scenePaths.Count; i++)
                {
                    string scenePath = scenePaths[i];
                    ctx.Progress((float)i / scenePaths.Count, $"Scanning {Path.GetFileName(scenePath)}…");

                    Scene scene = SceneManager.GetSceneByPath(scenePath);
                    bool wasOpen = scene.IsValid() && scene.isLoaded;

                    try
                    {
                        if (!wasOpen)
                        {
                            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                            openedAnything = true;
                        }

                        ScanScene(scene, ctx, hits);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[DreamPark] Could not scan {scenePath}: {e.Message}");
                    }
                    finally
                    {
                        if (!wasOpen && scene.IsValid() && scene.isLoaded)
                        {
                            if (!EditorSceneManager.CloseScene(scene, true))
                                Debug.LogWarning($"[DreamPark] Could not close {scenePath} after scanning.");
                        }
                    }
                }
            }
            finally
            {
                if (openedAnything) RestoreSetupSafely(setup);
            }

            return CheckResult.From(CheckId, BuildFindings(hits));
        }

        private static bool AnyOpenSceneDirty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) return true;
            return false;
        }

        // Unity's parameter doc for RestoreSceneManagerSetup is a PRECONDITION, not a
        // description: "at least one Scene should be loaded, and there must be one
        // active Scene." Restoring a setup that violates it throws — and a throw from
        // inside a finally masks whatever exception was already in flight, which
        // directly contradicts the "a broken check must never block shipping"
        // contract. So: guard, then catch.
        private static void RestoreSetupSafely(SceneSetup[] setup)
        {
            if (setup == null || setup.Length == 0) return;
            if (!setup.Any(s => s != null && s.isLoaded)) return;
            if (!setup.Any(s => s != null && s.isActive)) return;
            if (setup.Any(s => s == null || string.IsNullOrEmpty(s.path))) return;   // untitled scene

            try
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not restore your scene setup after the pre-upload "
                               + $"scan: {e.Message}");
            }
        }

        private static List<string> FindScenes(PreUploadCheckContext ctx)
        {
            var result = new List<string>();
            if (!AssetDatabase.IsValidFolder(ctx.contentRoot)) return result;

            // Nothing in the SDK hardcodes the "1. Scenes/" folder — scenes are always
            // found by type filter.
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { ctx.contentRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (ContentRootScanner.IsThirdPartyLocal(path)) continue;
                result.Add(path);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static void ScanScene(Scene scene, PreUploadCheckContext ctx, List<InstanceHit> hits)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            bool ignoreRootScale = EditorPrefs.GetBool(IgnoreRootScalePrefKey, true);

            foreach (var rootGo in scene.GetRootGameObjects())
            {
                foreach (var t in rootGo.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;

                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;

                    // "Should NOT include prefabs that are nested in other prefabs."
                    // This one line satisfies both readings: an instance nested inside
                    // another INSTANCE in the scene is skipped (its overrides belong to
                    // the outer instance), and prefabs nested inside a prefab ASSET are
                    // not scene instances at all so never appear here.
                    //
                    // It is also a precondition for the default-override filtering
                    // below: Unity only defines default overrides for the root of an
                    // OUTERMOST prefab instance.
                    if (PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go) continue;

                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source == null) continue;

                    string sourcePath = AssetDatabase.GetAssetPath(source);
                    if (string.IsNullOrEmpty(sourcePath)) continue;

                    // Only prefabs this package owns.
                    if (!ContentRootScanner.IsUnderContentRoot(sourcePath, ctx.contentRoot)) continue;
                    if (ContentRootScanner.IsThirdPartyLocal(sourcePath)) continue;

                    var changes = DescribeChanges(go, ignoreRootScale);
                    if (changes.Count == 0) continue;

                    hits.Add(new InstanceHit
                    {
                        sourcePrefabPath = sourcePath,
                        sourcePrefabGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                        scenePath = scene.path,
                        sceneGuid = sceneGuid,
                        hierarchyPath = HierarchyPath(go.transform),
                        changes = changes,
                    });
                }
            }
        }

        // Builds the human-readable "what changed" list.
        //
        // Uses GetPropertyModifications, NOT GetObjectOverrides. GetObjectOverrides is
        // COMPONENT-granularity — its element type exposes only instanceObject and
        // coupledOverride, with no property path and no value — so a root Transform
        // with an overridden position (a default override) and an overridden
        // localScale (not one) produces exactly ONE ObjectOverride either way, and you
        // cannot tell from it which properties changed. It is the right API for
        // driving Apply/Revert on a whole object and the wrong one for reporting.
        private static List<string> DescribeChanges(GameObject instanceRoot, bool ignoreRootScale)
        {
            var changes = new List<string>();

            // Added / removed structure. These are unambiguous — nobody adds a
            // component to a test-scene instance by accident.
            try
            {
                foreach (var added in PrefabUtility.GetAddedComponents(instanceRoot))
                {
                    if (added == null || added.instanceComponent == null) continue;
                    changes.Add($"added {added.instanceComponent.GetType().Name} on "
                              + $"{added.instanceComponent.gameObject.name}");
                }

                foreach (var removed in PrefabUtility.GetRemovedComponents(instanceRoot))
                {
                    if (removed == null || removed.assetComponent == null) continue;
                    changes.Add($"removed {removed.assetComponent.GetType().Name}");
                }

                foreach (var addedGo in PrefabUtility.GetAddedGameObjects(instanceRoot))
                {
                    if (addedGo == null || addedGo.instanceGameObject == null) continue;
                    changes.Add($"added GameObject '{addedGo.instanceGameObject.name}'");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read structural overrides on "
                               + $"{instanceRoot.name}: {e.Message}");
            }

            // Property modifications. Unity documents four limitations, all of which
            // bite here: it returns BOTH default and non-default overrides; it returns
            // overrides for the whole subtree; it returns overrides that are no longer
            // valid; and IT CAN RETURN NULL.
            PropertyModification[] mods = null;
            try { mods = PrefabUtility.GetPropertyModifications(instanceRoot); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not read property overrides on "
                               + $"{instanceRoot.name}: {e.Message}");
            }

            if (mods != null)
            {
                var rootTransform = instanceRoot.transform;

                foreach (var mod in mods)
                {
                    if (mod == null || mod.target == null) continue;      // stale
                    if (string.IsNullOrEmpty(mod.propertyPath)) continue;

                    // Unity's own definition of "things you always override on a placed
                    // instance": root name, root localPosition / localRotation /
                    // localEulerAnglesHint / rootOrder, plus the RectTransform set.
                    bool isDefault;
                    try { isDefault = PrefabUtility.IsDefaultOverride(mod); }
                    catch { isDefault = false; }
                    if (isDefault) continue;

                    bool onRootTransform = ReferenceEquals(mod.target, rootTransform);

                    // localScale is NOT a default override, so IsDefaultOverride will
                    // not drop it — but scaling an instance to eyeball it in a test
                    // scene is ordinary authoring. Off by default, behind a pref,
                    // because some teams consider a scaled instance meaningful.
                    if (ignoreRootScale && onRootTransform &&
                        mod.propertyPath.StartsWith("m_LocalScale", StringComparison.Ordinal))
                        continue;

                    changes.Add(Describe(mod));
                }
            }

            return changes.Distinct(StringComparer.Ordinal).Take(24).ToList();
        }

        private static string Describe(PropertyModification mod)
        {
            string typeName = mod.target != null ? mod.target.GetType().Name : "?";
            string prop = mod.propertyPath;

            // Strip Unity's serialized-field decoration so the row reads like the
            // Inspector rather than like YAML.
            if (prop.StartsWith("m_", StringComparison.Ordinal)) prop = prop.Substring(2);

            string value = mod.objectReference != null
                ? mod.objectReference.name
                : mod.value;

            // PropertyModification only carries the NEW value. Showing "3 → 5" would
            // mean opening a SerializedObject on the source asset for every single
            // modification; not worth it for a line of report text.
            return string.IsNullOrEmpty(value)
                ? $"{typeName}.{prop}"
                : $"{typeName}.{prop} → {Truncate(value, 40)}";
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static List<Finding> BuildFindings(List<InstanceHit> hits)
        {
            var findings = new List<Finding>();

            // One finding per (source prefab, scene) pair, so a dev can ignore
            // "Player.prefab in Template.unity" without also ignoring the same prefab
            // in a different scene.
            foreach (var group in hits.GroupBy(h => h.sourcePrefabPath + "|" + h.scenePath))
            {
                var list = group.ToList();
                var first = list[0];

                var allChanges = list.SelectMany(h => h.changes)
                                     .Distinct(StringComparer.Ordinal)
                                     .ToList();

                string prefabName = Path.GetFileNameWithoutExtension(first.sourcePrefabPath);
                string sceneName = Path.GetFileNameWithoutExtension(first.scenePath);

                string changeText = string.Join(", ", allChanges.Take(6));
                if (allChanges.Count > 6) changeText += $", +{allChanges.Count - 6} more";

                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = CheckSeverity.Warning,
                    assetGuid = first.sourcePrefabGuid,
                    assetPath = first.sourcePrefabPath,
                    subKey = first.sceneGuid,
                    title = $"{prefabName} has {allChanges.Count} unapplied override"
                          + (allChanges.Count == 1 ? "" : "s") + $" in {sceneName}",
                    detail = $"{changeText}\n\n"
                           + $"Scene: {first.scenePath}\n"
                           + "Scenes are not part of an upload. These changes exist only in the scene "
                           + "and will not ship unless they are applied to the prefab.",
                };

                string scenePath = first.scenePath;
                string hierarchy = first.hierarchyPath;

                // "Open Scene & Select" is offered as the primary action rather than an
                // automatic apply. Applying selectively means re-resolving the instance
                // after a scene reopen and calling Apply* per override — and getting
                // that subtly wrong writes the test-scene pose into shipped content.
                // Unity's own Overrides dropdown already does this correctly, with a
                // diff view; this puts the dev in front of it.
                finding.fixes.Add(new FixAction("Open scene & select", () =>
                {
                    return OpenAndSelect(scenePath, hierarchy);
                })
                {
                    resolvesFinding = false,   // opening resolves nothing on its own
                    tooltip = "Opens the scene and selects the instance so you can use Unity's own "
                            + "Overrides dropdown to review and apply.",
                    confirmTitle = "Open scene",
                    confirmMessage = $"Open {scenePath}?\n\nAny unsaved changes in your current scenes "
                                   + "will prompt to be saved first.",
                });

                findings.Add(finding);
            }

            return findings;
        }

        private static bool OpenAndSelect(string scenePath, string hierarchyPath)
        {
            try
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                if (!scene.IsValid()) return false;

                var target = FindByHierarchyPath(scene, hierarchyPath);
                if (target != null)
                {
                    Selection.activeGameObject = target;
                    EditorGUIUtility.PingObject(target);
                }

                // Opening resolves nothing on its own — the dev still has to apply.
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not open {scenePath}: {e}");
                return false;
            }
        }

        private static GameObject FindByHierarchyPath(Scene scene, string hierarchyPath)
        {
            if (string.IsNullOrEmpty(hierarchyPath)) return null;

            foreach (var rootGo in scene.GetRootGameObjects())
            {
                foreach (var t in rootGo.GetComponentsInChildren<Transform>(true))
                {
                    if (HierarchyPath(t) == hierarchyPath) return t.gameObject;
                }
            }
            return null;
        }

        private static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
#endif
