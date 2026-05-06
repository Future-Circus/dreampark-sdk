#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

namespace DreamPark.Easy
{
    /// <summary>
    /// Rebuilds EasyEvent links for saved prefabs (root + all children) before write.
    /// </summary>
    public class EasyEventPrefabSaveProcessor : AssetModificationProcessor
    {
        private static bool isProcessingSave;

        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (isProcessingSave)
            {
                return paths;
            }

            var prefabPaths = paths
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            if (prefabPaths.Length == 0)
            {
                return paths;
            }

            isProcessingSave = true;
            try
            {
                foreach (var prefabPath in prefabPaths)
                {
                    RebuildPrefabEasyEvents(prefabPath);
                }
            }
            finally
            {
                isProcessingSave = false;
            }

            return paths;
        }

        private static void RebuildPrefabEasyEvents(string prefabPath)
        {
            try
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage == null || stage.prefabContentsRoot == null)
                {
                    return;
                }

                // Only process the prefab currently open in Prefab Mode.
                if (!string.Equals(stage.assetPath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool changed = RebuildHierarchy(stage.prefabContentsRoot, out int brokenLinkFixes);
                if (changed)
                {
                    EditorUtility.SetDirty(stage.prefabContentsRoot);
                }

                Debug.Log($"[EasyEvent] Prefab save scan '{prefabPath}': fixed {brokenLinkFixes} broken above/below links.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[EasyEvent] Failed prefab save rebuild for '{prefabPath}': {e.Message}");
            }
        }

        private static bool RebuildHierarchy(GameObject root, out int brokenLinkFixes)
        {
            bool changed = false;
            brokenLinkFixes = 0;
            var transforms = root.GetComponentsInChildren<Transform>(true);

            foreach (var currentTransform in transforms)
            {
                var eventsOnObject = currentTransform.GetComponents<EasyEvent>();
                if (eventsOnObject.Length == 0)
                {
                    continue;
                }

                if (RebuildOnGameObject(eventsOnObject, out int objectBrokenLinkFixes))
                {
                    changed = true;
                }
                brokenLinkFixes += objectBrokenLinkFixes;
            }

            return changed;
        }

        private static bool RebuildOnGameObject(EasyEvent[] allComponents, out int brokenLinkFixes)
        {
            bool changed = false;
            brokenLinkFixes = 0;

            for (int i = 0; i < allComponents.Length; i++)
            {
                bool componentChanged = false;
                EasyEvent current = allComponents[i];
                EasyEvent expectedAbove = GetExpectedAbove(allComponents, i);
                EasyEvent expectedBelow = GetExpectedBelow(allComponents, i);
                bool expectedEventOnStart = i == 0;

                if (current.aboveEvent != expectedAbove)
                {
                    current.aboveEvent = expectedAbove;
                    changed = true;
                    componentChanged = true;
                    brokenLinkFixes++;
                }

                if (current.belowEvent != expectedBelow)
                {
                    current.belowEvent = expectedBelow;
                    changed = true;
                    componentChanged = true;
                    brokenLinkFixes++;
                }

                if (current.eventOnStart != expectedEventOnStart)
                {
                    current.eventOnStart = expectedEventOnStart;
                    changed = true;
                    componentChanged = true;
                }

                if (expectedAbove != null)
                {
                    bool aboveChanged = RebuildListenerFromAbove(expectedAbove, current);
                    if (aboveChanged)
                    {
                        changed = true;
                    }
                }

                if (expectedBelow == null && HasAnyPersistentCalls(current.onEvent))
                {
                    ClearPersistentCalls(current.onEvent);
                    changed = true;
                    componentChanged = true;
                }

                if (componentChanged)
                {
                    EditorUtility.SetDirty(current);
                }
            }

            return changed;
        }

        private static EasyEvent GetExpectedAbove(EasyEvent[] allComponents, int index)
        {
            if (index <= 0)
            {
                return null;
            }

            EasyEvent current = allComponents[index];
            if (current != null && current.IgnoreAboveEventLink)
            {
                return null;
            }

            return allComponents[index - 1];
        }

        private static EasyEvent GetExpectedBelow(EasyEvent[] allComponents, int index)
        {
            if (index >= allComponents.Length - 1)
            {
                return null;
            }

            EasyEvent next = allComponents[index + 1];
            if (next != null && next.IgnoreAboveEventLink)
            {
                return null;
            }

            return next;
        }

        private static bool RebuildListenerFromAbove(EasyEvent above, EasyEvent target)
        {
            bool hadCorrectSingleLink = above.onEvent.GetPersistentEventCount() == 1
                && above.onEvent.GetPersistentTarget(0) == target
                && above.onEvent.GetPersistentMethodName(0) == nameof(EasyEvent.OnEvent);

            if (hadCorrectSingleLink)
            {
                return false;
            }

            ClearPersistentCalls(above.onEvent);

            var targetType = target.GetType();
            var method = targetType.GetMethod(nameof(EasyEvent.OnEvent), new[] { typeof(object) });
            if (method == null)
            {
                return false;
            }

            var action = (UnityAction<object>)Delegate.CreateDelegate(typeof(UnityAction<object>), target, method);
            UnityEventTools.AddPersistentListener(above.onEvent, action);
            EditorUtility.SetDirty(above);
            return true;
        }

        private static bool HasAnyPersistentCalls(UnityEvent<object> unityEvent)
        {
            return unityEvent != null && unityEvent.GetPersistentEventCount() > 0;
        }

        private static void ClearPersistentCalls(UnityEvent<object> unityEvent)
        {
            if (unityEvent == null)
            {
                return;
            }

            for (int i = unityEvent.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(unityEvent, i);
            }
        }
    }
}
#endif
