using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;

public static class ComponentCopier
{
    private static Component copySource;

    [MenuItem("CONTEXT/Component/Copy Values (Force Rebuild)")]
    private static void Copy(MenuCommand cmd)
    {
        copySource = cmd.context as Component;
        Debug.Log($"📋 Copied values from {copySource?.GetType().Name}");
    }

    [MenuItem("CONTEXT/Component/Paste Values (Force Rebuild)")]
    private static void Paste(MenuCommand cmd)
    {
        if (!copySource)
        {
            Debug.LogWarning("No component copied yet.");
            return;
        }

        var target = cmd.context as Component;
        if (!target)
        {
            Debug.LogWarning("Target invalid.");
            return;
        }

        Undo.RecordObject(target, "Paste Values (Force Rebuild)");
        CopyFieldValues(copySource, target);
        RebuildUnityEvents(copySource, target);
        EditorUtility.SetDirty(target);

        Debug.Log($"✅ Pasted {copySource.GetType().Name} → {target.GetType().Name}");
    }

    // ------------------------------------------------------------------------

    private static void CopyFieldValues(Component src, Component dst)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var field in src.GetType().GetFields(flags))
        {
            if (field.IsDefined(typeof(ObsoleteAttribute), true) || field.IsLiteral)
                continue;

            try
            {
                object value = field.GetValue(src);

                // Clone arrays/lists safely (no Activator.CreateInstance)
                if (value is IList list && field.FieldType != typeof(string))
                {
                    var elementType = field.FieldType.IsArray
                        ? field.FieldType.GetElementType()
                        : field.FieldType.IsGenericType
                            ? field.FieldType.GetGenericArguments()[0]
                            : null;

                    if (field.FieldType.IsArray && elementType != null)
                    {
                        var arr = Array.CreateInstance(elementType, list.Count);
                        for (int i = 0; i < list.Count; i++)
                            arr.SetValue(list[i], i);
                        value = arr;
                    }
                    else if (typeof(IList).IsAssignableFrom(field.FieldType) && elementType != null)
                    {
                        var listType = field.FieldType.IsInterface || field.FieldType.IsAbstract
                            ? typeof(List<>).MakeGenericType(elementType)
                            : field.FieldType;

                        var newList = (IList)Activator.CreateInstance(listType);
                        foreach (var item in list)
                            newList.Add(item);
                        value = newList;
                    }
                }

                field.SetValue(dst, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Skipping field {field.Name}: {e.Message}");
            }
        }
    }

    // ------------------------------------------------------------------------
    // UnityEvent rebuild
    // ------------------------------------------------------------------------

    private static void RebuildUnityEvents(Component src, Component dst)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var f in src.GetType().GetFields(flags))
        {
            if (!typeof(UnityEventBase).IsAssignableFrom(f.FieldType))
                continue;

            var oldEvt = f.GetValue(src) as UnityEventBase;
            if (oldEvt == null) continue;

            var newField = dst.GetType().GetField(f.Name, flags);
            if (newField == null) continue;

            var newEvt = newField.GetValue(dst) as UnityEventBase;
            if (newEvt == null) continue;

            // Clear listeners
            for (int i = newEvt.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(newEvt, i);

            int count = oldEvt.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                var targetObj = oldEvt.GetPersistentTarget(i);
                var methodName = oldEvt.GetPersistentMethodName(i);
                if (string.IsNullOrEmpty(methodName)) continue;

                if (targetObj == src)
                    targetObj = dst; // rebinding to new component

                var method = targetObj?.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) continue;

                AddPersistentListenerDynamic(newEvt, targetObj, method);
            }

            newField.SetValue(dst, newEvt);
        }
    }

    private static void AddPersistentListenerDynamic(UnityEventBase evt, object target, MethodInfo method)
    {
        // UnityEvent (no args)
        if (evt is UnityEvent simple)
        {
            var de = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), target, method);
            UnityEventTools.AddPersistentListener(simple, de);
            return;
        }

        // UnityEvent<T> (single arg)
        var baseType = evt.GetType().BaseType;
        if (baseType == null || !baseType.IsGenericType) return;

        var args = baseType.GetGenericArguments();
        if (args.Length != 1) return;

        var argType = args[0];
        var actionType = typeof(UnityAction<>).MakeGenericType(argType);
        var del = Delegate.CreateDelegate(actionType, target, method);

        var add = typeof(UnityEventTools)
            .GetMethod("AddPersistentListener", BindingFlags.Static | BindingFlags.Public)
            .MakeGenericMethod(argType);

        add.Invoke(null, new object[] { evt, del });
    }
}