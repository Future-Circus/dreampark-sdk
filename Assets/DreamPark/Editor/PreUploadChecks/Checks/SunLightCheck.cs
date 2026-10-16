#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // Directional ("Sun") lights inside shipped content.
    //
    // WHY THIS BLOCKS
    //
    // A directional light has no position and no falloff — it lights the entire
    // scene. One shipped inside an attraction, a prop, or the player rig therefore
    // relights EVERY attraction in the park, including other creators' work, and
    // there is no way for them to opt out. It is the one lighting mistake whose blast
    // radius is the whole venue rather than the object.
    //
    // Scope is content root prefabs only, never scenes: a directional light in a test
    // scene is correct authoring. Only what ships matters.
    //
    // The SDK's own directional lights are all editor-only scaffolding, and none of
    // them live in a content prefab, so none of them can trip this check:
    //
    //   - Simulator.SpawnCamera creates one with `new GameObject` at play-mode start.
    //     It is inside that file's #if UNITY_EDITOR branch (the #else branch is a
    //     device stub that destroys itself in Awake), and being runtime-spawned it
    //     never exists in a prefab asset for this check to scan.
    //   - PrefabPreviewRenderer's key/fill lights, created with
    //     HideFlags.HideAndDontSave for the preview camera.
    //   - The park simulator's scenery lighting, which lives entirely under
    //     Assets/DreamPark/Editor/ParkSimulator/ and so is not in player builds at all.
    public sealed class SunLightCheck : IPreUploadCheck
    {
        public const string CheckId = "sun-light";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Directional lights in content"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "A directional light has no position and no falloff, so one shipped inside an "
                     + "attraction, prop or player rig relights the entire park — every other "
                     + "creator's content included, with no way for them to opt out.";
            }
        }

        private sealed class Hit
        {
            public string hierarchyPath;
            public string goName;
            public bool enabled;
            public bool activeInHierarchy;
            public float intensity;
            public string nestedSourcePath;   // null when the Light is defined on this prefab
            public string nestedSourceHierarchyPath;
            public bool goIsBare;             // Transform + Light only, no children
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            int i = 0;

            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count), $"Checking lights in {root.name}…");
                i++;

                List<Hit> hits;
                try
                {
                    hits = Scan(root.assetPath);
                }
                catch (Exception e)
                {
                    // Loading a broken prefab must not blow up the whole scan — vendor
                    // demo prefabs with missing-script references are a known hazard
                    // in this codebase.
                    Debug.LogWarning($"[DreamPark] Could not scan {root.assetPath} for lights: {e.Message}");
                    continue;
                }

                foreach (var hit in hits)
                {
                    bool live = hit.enabled && hit.activeInHierarchy;

                    var finding = new Finding
                    {
                        checkId = CheckId,
                        // A disabled or inactive sun is a landmine rather than a fire:
                        // it ships, and it lights the park the moment anything enables
                        // it. Report it, don't block on it.
                        severity = live ? CheckSeverity.Blocking : CheckSeverity.Warning,
                        assetGuid = root.guid,
                        assetPath = root.assetPath,
                        subKey = hit.hierarchyPath,
                        title = $"{root.KindLabel} '{root.name}' contains a directional light: {hit.hierarchyPath}"
                              + (live ? "" : "  (currently disabled)"),
                    };

                    // Only offer to edit a nested source that belongs to this content
                    // package. GetCorrespondingObjectFromOriginalSource can land on
                    // Assets/DreamPark/** (which CLAUDE.md says never to hand-edit) or
                    // on another content package entirely — and RemoveLights(nested,
                    // null) strips EVERY directional light in that prefab.
                    bool nestedIsOurs = hit.nestedSourcePath != null
                        && ContentRootScanner.IsUnderContentRoot(hit.nestedSourcePath, ctx.contentRoot);

                    if (hit.nestedSourcePath != null)
                    {
                        finding.detail =
                            $"The light comes from the nested prefab {hit.nestedSourcePath}, not from "
                          + $"{root.name} itself. Removing it here would only record a removed-component "
                          + "override on this one consumer — the light would still exist in the nested "
                          + "prefab and reappear everywhere else it is used. Fix it at the source.";

                        string nested = hit.nestedSourcePath;
                        string hierarchy = hit.hierarchyPath;
                        string nestedHierarchy = hit.nestedSourceHierarchyPath;

                        if (!nestedIsOurs)
                        {
                            finding.detail += $"\n\n{nested} is outside this content package, so no "
                                            + "source asset will be edited. DreamPark can instead record "
                                            + "a removed-component override on this content prefab only; "
                                            + "other consumers of the nested prefab remain unchanged.";
                            string outerPrefab = root.assetPath;
                            finding.fixes.Add(new FixAction(
                                "Remove from this prefab only",
                                () => RemoveNestedLightOverride(outerPrefab, hierarchy, nested))
                            {
                                tooltip = "Records a removed-Light override on this content prefab; the "
                                        + "nested source prefab and its other consumers are unchanged.",
                                bulkKey = "remove-external-nested-light-override",
                                bulkLabel = "Remove external nested lights locally",
                                affectedPaths = new[] { outerPrefab },
                                alsoRerunCheckIds = new[] { RootComponentsCheck.CheckId },
                                confirmTitle = "Remove nested light from this prefab only",
                                confirmMessage =
                                    $"Remove the directional Light at '{hierarchy}' from {root.name} only?\n\n"
                                  + $"DreamPark will save a removed-component override in:\n{outerPrefab}\n\n"
                                  + $"It will NOT edit the nested source:\n{nested}\n\n"
                                  + "If the nested prefab is replaced or its hierarchy is substantially "
                                  + "changed later, review this override. Cannot be undone via Ctrl-Z; use "
                                  + "version control to revert.",
                            });
                            finding.fixes.Add(FixAction.Navigate("Select nested prefab", () =>
                            {
                                var obj = AssetDatabase.LoadMainAssetAtPath(nested);
                                if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
                            }));
                            findings.Add(finding);
                            continue;
                        }

                        finding.fixes.Add(new FixAction(
                            $"Fix in {System.IO.Path.GetFileName(nested)}",
                            () => RemoveLights(nested, nestedHierarchy))
                        {
                            canBulk = false,
                            affectedPaths = new[] { nested },
                            alsoRerunCheckIds = new[] { RootComponentsCheck.CheckId },
                            tooltip = "Removes the Light component from the nested prefab asset.",
                            confirmTitle = "Remove directional light",
                            confirmMessage =
                                $"Remove the directional Light from {nested}?\n\n"
                              + $"(Reached via {hierarchy} in {root.name}.)\n\n"
                              + "The GameObject is kept — only the Light component is removed, since the "
                              + "object may have children or a name other things look up.\n\n"
                              + "Cannot be undone with Ctrl-Z. Use version control to revert.",
                        });
                    }
                    else
                    {
                        finding.detail =
                            $"Intensity {hit.intensity:0.##}. This light will be part of the uploaded "
                          + "bundle and will affect every attraction in any park that loads this content.";

                        string path = root.assetPath;
                        string hierarchy = hit.hierarchyPath;
                        finding.fixes.Add(new FixAction("Fix Now", () => RemoveLights(path, hierarchy))
                        {
                            tooltip = "Removes the Light component (keeps the GameObject).",
                            alsoRerunCheckIds = new[] { RootComponentsCheck.CheckId },
                            confirmTitle = "Remove directional light",
                            confirmMessage =
                                $"Remove the directional Light on '{hierarchy}' from {root.name}?\n\n"
                              + path + "\n\n"
                              + "The GameObject is kept — only the Light component is removed, since it "
                              + "may have children, or a name other code or a NetId hash depends on.\n\n"
                              + "Cannot be undone with Ctrl-Z. Use version control to revert.",
                        });
                    }

                    findings.Add(finding);
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static List<Hit> Scan(string prefabPath)
        {
            var hits = new List<Hit>();

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return hits;

            try
            {
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light == null) continue;
                    if (light.type != LightType.Directional) continue;

                    // GetCorrespondingObjectFromSource returns the object from the
                    // OUTERMOST asset, which at two levels of nesting names the wrong
                    // prefab — and "fix it there" would just record another removed-
                    // component override. GetCorrespondingObjectFromOriginalSource
                    // reaches the true innermost source. It returns null for a
                    // component defined directly on the prefab being edited, which is
                    // exactly what makes this test work.
                    string nestedSource = null;
                    string nestedSourceHierarchy = null;
                    try
                    {
                        var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(light);
                        if (original != null)
                        {
                            string p = AssetDatabase.GetAssetPath(original);
                            if (!string.IsNullOrEmpty(p) &&
                                !string.Equals(p, prefabPath, StringComparison.OrdinalIgnoreCase))
                            {
                                nestedSource = p;
                                var sourceRoot = original.transform;
                                while (sourceRoot.parent != null) sourceRoot = sourceRoot.parent;
                                nestedSourceHierarchy = HierarchyPath(
                                    sourceRoot, original.transform);
                            }
                        }
                    }
                    catch { /* nesting detection is a nicety, not a correctness gate */ }

                    var go = light.gameObject;
                    hits.Add(new Hit
                    {
                        hierarchyPath = HierarchyPath(root.transform, go.transform),
                        goName = go.name,
                        enabled = light.enabled,
                        activeInHierarchy = go.activeInHierarchy,
                        intensity = light.intensity,
                        nestedSourcePath = nestedSource,
                        nestedSourceHierarchyPath = nestedSourceHierarchy,
                        goIsBare = go.transform.childCount == 0
                                && go.GetComponents<Component>().Length <= 2,
                    });
                }
            }
            finally
            {
                // ALWAYS paired, in a finally — otherwise a throw leaks the temp scene.
                PrefabUtility.UnloadPrefabContents(root);
            }

            return hits;
        }

        // Remove a Light inherited from a nested prefab without touching that nested
        // source asset. Destroying the inherited component while editing the OUTER
        // prefab records a removed-component override in the outer asset. We verify
        // both the source identity before mutation and the resolved hierarchy after
        // saving so a stale finding cannot delete the wrong component.
        private static bool RemoveNestedLightOverride(
            string outerPrefabPath, string hierarchyPath, string expectedNestedSourcePath)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(outerPrefabPath);
                if (root == null) return false;

                var light = root.GetComponentsInChildren<Light>(true)
                    .FirstOrDefault(l => l != null
                                      && l.type == LightType.Directional
                                      && HierarchyPath(root.transform, l.transform) == hierarchyPath);
                if (light == null)
                {
                    Debug.LogWarning($"[DreamPark] The directional light at '{hierarchyPath}' no longer "
                                   + $"exists in {outerPrefabPath}; re-run the check.");
                    return false;
                }

                var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(light);
                string actualSourcePath = original != null ? AssetDatabase.GetAssetPath(original) : null;
                if (string.IsNullOrEmpty(actualSourcePath)
                    || !string.Equals(actualSourcePath, expectedNestedSourcePath,
                                      StringComparison.OrdinalIgnoreCase)
                    || !PrefabUtility.IsPartOfPrefabInstance(light))
                {
                    Debug.LogWarning($"[DreamPark] Refused to remove '{hierarchyPath}': its nested source "
                                   + $"changed from {expectedNestedSourcePath} to "
                                   + $"{actualSourcePath ?? "(none)"}. Re-run the check.");
                    return false;
                }

                UnityEngine.Object.DestroyImmediate(light, true);

                bool saved;
                PrefabUtility.SaveAsPrefabAsset(root, outerPrefabPath, out saved);
                if (!saved)
                {
                    Debug.LogWarning($"[DreamPark] Failed to save the removed-Light override in "
                                   + outerPrefabPath + ".");
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not remove the nested directional light from "
                               + $"{outerPrefabPath}: {e}");
                return false;
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }

            // Reload from disk rather than trusting the in-memory deletion. This also
            // confirms that Unity serialized the removal as an override successfully.
            GameObject verifyRoot = null;
            try
            {
                verifyRoot = PrefabUtility.LoadPrefabContents(outerPrefabPath);
                if (verifyRoot == null) return false;

                bool stillPresent = verifyRoot.GetComponentsInChildren<Light>(true)
                    .Any(l => l != null
                           && l.type == LightType.Directional
                           && HierarchyPath(verifyRoot.transform, l.transform) == hierarchyPath);
                if (stillPresent)
                {
                    Debug.LogWarning($"[DreamPark] Unity did not persist the removed-Light override at "
                                   + $"'{hierarchyPath}' in {outerPrefabPath}.");
                    return false;
                }

                Debug.Log($"[DreamPark] Removed the nested directional light at '{hierarchyPath}' "
                        + $"from {outerPrefabPath} only. Source left unchanged: "
                        + expectedNestedSourcePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not verify the removed-Light override in "
                               + $"{outerPrefabPath}: {e}");
                return false;
            }
            finally
            {
                if (verifyRoot != null) PrefabUtility.UnloadPrefabContents(verifyRoot);
            }
        }

        // Removes the directional Light at one sibling-indexed hierarchy path from a
        // prefab asset. A null path remains supported for deliberate whole-prefab
        // callers, but pre-upload findings always pass the exact scanned component.
        private static bool RemoveLights(string prefabPath, string hierarchyPath)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null) return false;

                var doomed = root.GetComponentsInChildren<Light>(true)
                    .Where(l => l != null && l.type == LightType.Directional)
                    .Where(l => hierarchyPath == null
                             || HierarchyPath(root.transform, l.transform) == hierarchyPath)
                    .ToList();

                if (doomed.Count == 0) return false;

                foreach (var l in doomed)
                    UnityEngine.Object.DestroyImmediate(l, true);

                bool success;
                // Use the out-bool overload. Unity documents that SaveAsPrefabAsset
                // "will return null even if the save was successful" when the editor is
                // inside a StartAssetEditing / StopAssetEditing batch — so the return
                // value is not a reliable success signal, but this flag is.
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out success);

                if (!success)
                {
                    Debug.LogWarning($"[DreamPark] Failed to save {prefabPath} after removing a directional light.");
                    return false;
                }

                Debug.Log($"[DreamPark] Removed {doomed.Count} directional light(s) from {prefabPath}.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not remove directional light from {prefabPath}: {e}");
                return false;
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Sibling index is part of the path on purpose. Two GameObjects named "Light"
        // under one parent would otherwise produce an identical key, which means one
        // Ignore silently covers both findings AND one "Fix Now" destroys both lights
        // while the second finding still claims to be unfixed.
        private static string HierarchyPath(Transform root, Transform t)
        {
            if (t == root) return root.name;

            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name + "[" + cur.GetSiblingIndex() + "]");
                cur = cur.parent;
            }
            parts.Add(root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
#endif
