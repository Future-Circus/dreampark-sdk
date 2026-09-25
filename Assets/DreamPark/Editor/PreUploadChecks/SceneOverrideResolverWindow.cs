#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark.PreUploadChecks
{
    /// <summary>
    /// Reviews one scene prefab instance a change at a time. There is deliberately
    /// no Apply All: test-scene poses and shipping prefab changes commonly coexist,
    /// so every mutation requires an explicit Apply or Revert decision.
    /// </summary>
    public sealed class SceneOverrideResolverWindow : EditorWindow
    {
        private const string IgnoreRootScalePrefKey = "DreamPark.PreUploadChecks.IgnoreRootScaleOverride";

        private string scenePath;
        private string hierarchyPath;
        private string instanceGlobalObjectId;
        private string prefabPath;
        private Action<bool> onComplete;
        private bool completionReported;
        private bool changed;
        private Vector2 scroll;

        private sealed class OverrideRow
        {
            public string label;
            public string detail;
            public Action apply;
            public Action revert;
        }

        public static void Show(string scenePath, string hierarchyPath,
                                string instanceGlobalObjectId, string prefabPath,
                                Action<bool> onComplete = null)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                if (onComplete != null) onComplete(false);
                return;
            }

            Scene scene;
            try { scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not open '{scenePath}': {e.Message}");
                if (onComplete != null) onComplete(false);
                return;
            }

            if (!scene.IsValid())
            {
                if (onComplete != null) onComplete(false);
                return;
            }

            var win = CreateInstance<SceneOverrideResolverWindow>();
            win.titleContent = new GUIContent("Resolve Prefab Overrides");
            win.minSize = new Vector2(680f, 420f);
            win.scenePath = scenePath;
            win.hierarchyPath = hierarchyPath;
            win.instanceGlobalObjectId = instanceGlobalObjectId;
            win.prefabPath = prefabPath;
            win.onComplete = onComplete;
            win.Show();
            win.Focus();
            var target = win.FindTarget();
            if (target != null)
            {
                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
            }
        }

        private void OnDestroy()
        {
            ReportCompletion(changed);
        }

        private void ReportCompletion(bool result)
        {
            if (completionReported) return;
            completionReported = true;
            var callback = onComplete;
            onComplete = null;
            if (callback != null) callback(result);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Resolve scene overrides", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(scenePath, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(
                "Apply writes only the selected change into the prefab that ships. Revert discards only "
              + "that selected scene change. Each choice is confirmed; there is intentionally no Apply All.",
                MessageType.Info);

            var root = FindTarget();
            if (root == null)
            {
                EditorGUILayout.HelpBox(
                    "The prefab instance no longer exists at '" + hierarchyPath + "'. It may have been renamed "
                  + "or removed since the check ran, or it no longer comes from the expected prefab.",
                    MessageType.Warning);
                DrawDone();
                return;
            }

            var rows = BuildRows(root);
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("No actionable overrides remain on this instance.", MessageType.Info);
                DrawDone();
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var row in rows)
                DrawRow(row);
            EditorGUILayout.EndScrollView();
            DrawDone();
        }

        private void DrawRow(OverrideRow row)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(row.label, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(row.detail))
                EditorGUILayout.LabelField(row.detail, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Revert from scene", GUILayout.Width(130f)))
                ConfirmAndSchedule(row, false);
            if (GUILayout.Button("Apply to prefab", GUILayout.Width(130f)))
                ConfirmAndSchedule(row, true);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void ConfirmAndSchedule(OverrideRow row, bool apply)
        {
            string verb = apply ? "Apply" : "Revert";
            string message = apply
                ? "Write this one override into the shipping prefab?\n\n" + row.label
                : "Discard this one override from the test scene?\n\n" + row.label;
            if (!EditorUtility.DisplayDialog(verb + " override?", message, verb, "Cancel")) return;

            // Structural prefab changes can invalidate objects while IMGUI is walking
            // them. Run after this event finishes and rebuild the list next repaint.
            EditorApplication.delayCall += () => Execute(apply ? row.apply : row.revert);
        }

        private void Execute(Action action)
        {
            try
            {
                action();
                changed = true;
                AssetDatabase.SaveAssets();
                var scene = SceneManager.GetSceneByPath(scenePath);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                    EditorSceneManager.SaveScene(scene);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not resolve scene override: {e}");
                EditorUtility.DisplayDialog("Override was not changed", e.Message, "OK");
            }
            Repaint();
        }

        private void DrawDone()
        {
            GUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Done", GUILayout.Width(100f), GUILayout.Height(28f)))
            {
                ReportCompletion(changed);
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        private List<OverrideRow> BuildRows(GameObject root)
        {
            var rows = new List<OverrideRow>();
            var addedComponents = PrefabUtility.GetAddedComponents(root);
            var addedGameObjects = PrefabUtility.GetAddedGameObjects(root);
            var instanceTargetsByAssetId = BuildInstanceTargetMap(root, prefabPath);
            var addedComponentObjects = new HashSet<Component>(
                addedComponents.Where(a => a != null && a.instanceComponent != null)
                               .Select(a => a.instanceComponent));
            var addedGameObjectRoots = new HashSet<GameObject>(
                addedGameObjects.Where(a => a != null && a.instanceGameObject != null)
                                .Select(a => a.instanceGameObject));

            foreach (var added in addedComponents)
            {
                var component = added != null ? added.instanceComponent : null;
                if (component == null) continue;
                rows.Add(new OverrideRow
                {
                    label = "Added " + component.GetType().Name,
                    detail = "On " + HierarchyPath(component.transform),
                    apply = () => PrefabUtility.ApplyAddedComponent(component, prefabPath, InteractionMode.UserAction),
                    revert = () => PrefabUtility.RevertAddedComponent(component, InteractionMode.UserAction),
                });
            }

            foreach (var removed in PrefabUtility.GetRemovedComponents(root))
            {
                if (removed == null || removed.assetComponent == null || removed.containingInstanceGameObject == null)
                    continue;
                var assetComponent = removed.assetComponent;
                string targetPath = AssetDatabase.GetAssetPath(removed.GetAssetObject());
                if (!string.Equals(targetPath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[DreamPark] Skipping removed {assetComponent.GetType().Name}: "
                                   + $"its apply target is '{targetPath}', not '{prefabPath}'.");
                    continue;
                }
                rows.Add(new OverrideRow
                {
                    label = "Removed " + assetComponent.GetType().Name,
                    detail = "From " + HierarchyPath(removed.containingInstanceGameObject.transform),
                    // PrefabOverride.Apply(path) is the only removed-component API
                    // that explicitly selects the outer content prefab. The static
                    // ApplyRemovedComponent overload can instead delete from a nested
                    // vendor/source prefab.
                    apply = () => removed.Apply(prefabPath, InteractionMode.UserAction),
                    revert = () => removed.Revert(InteractionMode.UserAction),
                });
            }

            foreach (var added in addedGameObjects)
            {
                var go = added != null ? added.instanceGameObject : null;
                if (go == null) continue;
                rows.Add(new OverrideRow
                {
                    label = "Added GameObject '" + go.name + "'",
                    detail = HierarchyPath(go.transform),
                    apply = () => PrefabUtility.ApplyAddedGameObject(go, prefabPath, InteractionMode.UserAction),
                    revert = () => PrefabUtility.RevertAddedGameObject(go, InteractionMode.UserAction),
                });
            }

            foreach (var removed in PrefabUtility.GetRemovedGameObjects(root))
            {
                if (removed == null || removed.assetGameObject == null
                    || removed.parentOfRemovedGameObjectInInstance == null)
                    continue;

                string targetPath = AssetDatabase.GetAssetPath(removed.GetAssetObject());
                if (!string.Equals(targetPath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[DreamPark] Skipping removed GameObject "
                                   + $"'{removed.assetGameObject.name}': its apply target is "
                                   + $"'{targetPath}', not '{prefabPath}'.");
                    continue;
                }

                rows.Add(new OverrideRow
                {
                    label = "Removed GameObject '" + removed.assetGameObject.name + "'",
                    detail = "From " + HierarchyPath(
                        removed.parentOfRemovedGameObjectInInstance.transform),
                    apply = () => removed.Apply(prefabPath, InteractionMode.UserAction),
                    revert = () => removed.Revert(InteractionMode.UserAction),
                });
            }

            var modifications = PrefabUtility.GetPropertyModifications(root);
            if (modifications == null) return rows;

            bool ignoreRootScale = EditorPrefs.GetBool(IgnoreRootScalePrefKey, true);
            foreach (var mod in modifications)
            {
                if (mod == null || mod.target == null) continue;
                UnityEngine.Object target;
                if (!instanceTargetsByAssetId.TryGetValue(mod.target.GetInstanceID(), out target))
                {
                    Debug.LogWarning($"[DreamPark] Skipping stale property override "
                                   + $"'{mod.propertyPath}': its prefab target no longer maps to "
                                   + "this scene instance.");
                    continue;
                }
                if (!IsActionable(mod, target, root.transform, ignoreRootScale)) continue;
                var targetComponent = target as Component;
                var targetGameObject = target as GameObject;
                if (targetComponent != null && addedComponentObjects.Contains(targetComponent)) continue;
                if (IsUnderAddedGameObject(
                    targetComponent != null ? targetComponent.gameObject : targetGameObject,
                    addedGameObjectRoots)) continue;
                string propertyPath = mod.propertyPath;
                string typeName = target.GetType().Name;
                string value = mod.objectReference != null ? mod.objectReference.name : mod.value;

                rows.Add(new OverrideRow
                {
                    label = typeName + "." + FriendlyProperty(propertyPath),
                    detail = string.IsNullOrEmpty(value) ? ObjectPath(target) : ObjectPath(target) + " → " + value,
                    apply = () => WithProperty(target, propertyPath, property =>
                        PrefabUtility.ApplyPropertyOverride(property, prefabPath, InteractionMode.UserAction)),
                    revert = () => WithProperty(target, propertyPath, property =>
                        PrefabUtility.RevertPropertyOverride(property, InteractionMode.UserAction)),
                });
            }

            return rows;
        }

        private static Dictionary<int, UnityEngine.Object> BuildInstanceTargetMap(
            GameObject root, string targetPrefabPath)
        {
            var result = new Dictionary<int, UnityEngine.Object>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                AddInstanceTarget(result, transform.gameObject, targetPrefabPath);
                foreach (var component in transform.GetComponents<Component>())
                    if (component != null)
                        AddInstanceTarget(result, component, targetPrefabPath);
            }
            return result;
        }

        private static void AddInstanceTarget<T>(
            Dictionary<int, UnityEngine.Object> result, T instanceObject,
            string targetPrefabPath) where T : UnityEngine.Object
        {
            T assetObject = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(
                instanceObject, targetPrefabPath);
            if (assetObject != null)
                result[assetObject.GetInstanceID()] = instanceObject;
        }

        private static bool IsUnderAddedGameObject(GameObject gameObject, HashSet<GameObject> addedRoots)
        {
            if (gameObject == null || addedRoots.Count == 0) return false;
            for (var current = gameObject.transform; current != null; current = current.parent)
                if (addedRoots.Contains(current.gameObject)) return true;
            return false;
        }

        private static bool IsActionable(
            PropertyModification mod, UnityEngine.Object instanceTarget,
            Transform root, bool ignoreRootScale)
        {
            if (mod == null || mod.target == null || string.IsNullOrEmpty(mod.propertyPath)) return false;
            try { if (PrefabUtility.IsDefaultOverride(mod)) return false; }
            catch { }

            return !(ignoreRootScale && instanceTarget == root
                && mod.propertyPath.StartsWith("m_LocalScale", StringComparison.Ordinal));
        }

        private static void WithProperty(UnityEngine.Object target, string propertyPath,
                                         Action<SerializedProperty> action)
        {
            if (target == null) throw new InvalidOperationException("The override target no longer exists.");
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                throw new InvalidOperationException("The overridden property no longer exists: " + propertyPath);
            action(property);
        }

        private GameObject FindTarget()
        {
            var scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded) return null;

            GameObject candidate = null;
            GlobalObjectId parsed;
            if (!string.IsNullOrEmpty(instanceGlobalObjectId)
                && GlobalObjectId.TryParse(instanceGlobalObjectId, out parsed))
            {
                candidate = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as GameObject;
                if (candidate == null || candidate.scene != scene) return null;
            }
            else
            {
                // Legacy/fallback route only. Current findings always provide a
                // GlobalObjectId, which remains stable across sibling reordering.
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (HierarchyPath(t) == hierarchyPath) { candidate = t.gameObject; break; }
            }

            if (candidate == null) return null;
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(candidate)) return null;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(candidate);
            string actualSourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            return string.Equals(actualSourcePath, prefabPath,
                                 StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }

        private static string ObjectPath(UnityEngine.Object target)
        {
            var component = target as Component;
            if (component != null) return HierarchyPath(component.transform);
            var go = target as GameObject;
            return go != null ? HierarchyPath(go.transform) : target.name;
        }

        private static string FriendlyProperty(string path)
        {
            return path.StartsWith("m_", StringComparison.Ordinal) ? path.Substring(2) : path;
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null) return "(unknown)";
            var parts = new List<string>();
            for (var current = transform; current != null; current = current.parent)
                parts.Add(current.name + "[" + current.GetSiblingIndex() + "]");
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
#endif
