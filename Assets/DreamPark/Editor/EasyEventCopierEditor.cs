using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using System.Collections.Generic;

namespace DreamPark.Easy
{
    public static class EasyEventCopierEditor
    {
        private static GameObject copySource;

        // Menu item removed from the DreamPark top-level menu. Callable
        // programmatically if needed; still registered elsewhere only if
        // re-decorated with [MenuItem] by a consumer.
        public static void CopyEasyEvents()
        {
            if (Selection.activeGameObject == null)
            {
                EditorUtility.DisplayDialog("EasyEvent Copier", "Select a GameObject to copy EasyEvents from.", "OK");
                return;
            }

            copySource = Selection.activeGameObject;
            int count = copySource.GetComponents<EasyEvent>().Length;

            EditorUtility.DisplayDialog("EasyEvent Copier",
                $"Copied {count} EasyEvent components from '{copySource.name}'.\nNow select a target and choose:\nDreamPark > EasyEvent > Paste",
                "OK");
        }

        // Menu item removed from the DreamPark top-level menu.
        public static void PasteEasyEvents()
        {
            if (copySource == null)
            {
                EditorUtility.DisplayDialog("EasyEvent Copier", "No source copied yet.", "OK");
                return;
            }

            if (Selection.activeGameObject == null)
            {
                EditorUtility.DisplayDialog("EasyEvent Copier", "Select a target GameObject to paste to.", "OK");
                return;
            }

            var target = Selection.activeGameObject;
            Undo.RegisterFullObjectHierarchyUndo(target, "Paste EasyEvents");

            // Step 1: Copy components and build mapping
            var sourceEvents = copySource.GetComponents<EasyEvent>();
            var mapping = new Dictionary<Component, Component>();

            foreach (var src in sourceEvents)
            {
                var dst = target.AddComponent(src.GetType());
                CopySerializedFields(src, dst);
                mapping[src] = dst;
            }

            // Step 2: Remap serialized references deeply
            foreach (var pair in mapping)
            {
                var so = new SerializedObject(pair.Value);
                var prop = so.GetIterator();

                bool enterChildren = true;
                while (prop.NextVisible(enterChildren))
                {
                    enterChildren = true;
                    if (prop.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        UnityEngine.Object refObj = prop.objectReferenceValue;
                        if (refObj is EasyEvent refEvt && mapping.ContainsKey(refEvt))
                        {
                            prop.objectReferenceValue = mapping[refEvt];
                        }
                    }
                }

                so.ApplyModifiedPropertiesWithoutUndo();  // <-- ensures Unity writes the new values

                EditorUtility.SetDirty(pair.Value);       // <-- marks component dirty
            }

            // Step 2: Fix internal references between copied components
            foreach (var pair in mapping)
            {
                RemapInternalReferences(pair.Key, pair.Value, mapping);
            }

            EditorUtility.DisplayDialog("EasyEvent Copier",
                $"Pasted {mapping.Count} EasyEvent components from '{copySource.name}' to '{target.name}'.",
                "OK");
        }

        private static void CopySerializedFields(Component src, Component dst)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in src.GetType().GetFields(flags))
            {
                if (field.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                try { field.SetValue(dst, field.GetValue(src)); }
                catch { /* skip invalid field */ }
            }
        }

        private static void RemapInternalReferences(Component src, Component dst, Dictionary<Component, Component> map)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in src.GetType().GetFields(flags))
            {
                var value = field.GetValue(dst);

                // If it's a reference to another EasyEvent on the same source, remap it
                if (value is EasyEvent refEvt && map.ContainsKey(refEvt))
                {
                    field.SetValue(dst, map[refEvt]);
                }

                // If it's a list/array of EasyEvents, handle those too
                else if (value is IEnumerable<EasyEvent> evtList)
                {
                    var elementType = field.FieldType.IsArray
                        ? field.FieldType.GetElementType()
                        : field.FieldType.GetGenericArguments().Length > 0
                            ? field.FieldType.GetGenericArguments()[0]
                            : null;

                    if (elementType == null) continue;

                    var remapped = new List<EasyEvent>();
                    foreach (var e in evtList)
                    {
                        if (map.ContainsKey(e))
                            remapped.Add(map[e] as EasyEvent);
                        else
                            remapped.Add(e);
                    }

                    if (field.FieldType.IsArray)
                        field.SetValue(dst, remapped.ToArray());
                    else
                        field.SetValue(dst, remapped);
                }
            }
        }

        // Paste validator — used to live alongside the removed menu item.
        private static bool PasteValidate() => copySource != null;
    }
}