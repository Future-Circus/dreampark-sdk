using UnityEngine;
using System;
using System.Linq;
using System.Reflection;

#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(EasyCall), true)]
public class EasyCallEditor : EasyEventEditor
{
    public override void OnInspectorGUI()
    {
        var easyCall = (EasyCall)target;
        base.OnInspectorGUI();

        if (easyCall.target != null)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public; // add NonPublic if desired
            var methods = easyCall.target.GetType()
                .GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => m.GetParameters().Length <= 1) // support 0 or 1 parameter
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            if (methods.Length == 0)
            {
                EditorGUILayout.HelpBox("No eligible void methods found (0 or 1 parameter).", MessageType.Info);
                return;
            }

            int selectedIndex = Mathf.Max(0, Array.IndexOf(methods, easyCall.methodName));
            int newSelectedIndex = EditorGUILayout.Popup("Method", selectedIndex, methods);

            if (newSelectedIndex != selectedIndex)
            {
                easyCall.methodName = methods[newSelectedIndex];
                EditorUtility.SetDirty(easyCall);
            }
        }
        else
        {
            EditorGUILayout.HelpBox("Assign a Target to choose a method.", MessageType.Info);
        }
    }
}
#endif

public class EasyCall : EasyEvent
{
    public MonoBehaviour target;
    [HideInInspector] public string methodName;

    public override void OnEvent(object arg0 = null)
    {
        if (target == null || string.IsNullOrEmpty(methodName))
        {
            Debug.LogWarning($"[EasyCall] {gameObject.name} - target is null or methodName is empty");
            return;
        }

        // Only call on THIS target component (not other components on the GameObject)
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public; // add NonPublic if desired

        try
        {
            if (arg0 == null)
            {
                // Prefer parameterless overload
                var m0 = type.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                if (m0 == null)
                {
                    Debug.LogWarning($"[EasyCall] {gameObject.name} - No parameterless method '{methodName}' on {type.Name}");
                    return;
                }

                m0.Invoke(target, null);
            }
            else
            {
                // Prefer exact-type match first, then any single-parameter method that can accept arg0
                var argType = arg0.GetType();

                MethodInfo m1 =
                    type.GetMethod(methodName, flags, null, new[] { argType }, null)
                    ?? type.GetMethods(flags)
                        .Where(m => m.Name == methodName)
                        .Where(m => m.GetParameters().Length == 1)
                        .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(argType));

                if (m1 == null)
                {
                    Debug.LogWarning($"[EasyCall] {gameObject.name} - No compatible 1-arg method '{methodName}({argType.Name})' on {type.Name}");
                    return;
                }

                m1.Invoke(target, new object[] { arg0 });
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[EasyCall] {gameObject.name} - Error invoking '{methodName}' on {type.Name}: {e}");
        }

        onEvent?.Invoke(arg0);
    }
}