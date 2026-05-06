using UnityEngine;
using System.Collections;
using UnityEngine.Events;
#if UNITY_EDITOR
using UnityEditor.Events;
#endif
using System;
using System.Collections.Generic;

public class Componentizer : MonoBehaviour
{
    public static void DoDestroy (UnityEngine.Object destroyThis) {
        if (destroyThis != null) {
            if (Application.isPlaying) {
                Destroy(destroyThis);
            } else {
                DestroyImmediate(destroyThis, true);
            }
        }
    }

    //Eventually will make these static
    public T DestroyComponent<T>() where T : Component {
        if (GetComponent<T>()) {
            DoDestroy(GetComponent<T>());
        }
        return GetComponent<T>();
    }

    public static T DestroyComponent<T>(GameObject gameObject) where T : Component {
        if (gameObject.GetComponent<T>()) {
            DoDestroy(gameObject.GetComponent<T>());
        }
        return gameObject.GetComponent<T>();
    }


    public T DoComponent<T>(bool shouldExist) where T : Component {
        return (shouldExist) ? (GetComponent<T>() ? GetComponent<T>() : gameObject.AddComponent<T>()) : DestroyComponent<T>();
    }

    public static T DoComponent<T>(GameObject gameObject, bool shouldExist) where T : Component {
        return (shouldExist) ? (gameObject.GetComponent<T>() ? gameObject.GetComponent<T>() : gameObject.AddComponent<T>()) : DestroyComponent<T>(gameObject);
    }

    public static T DoComponent<T>(GameObject gameObject, T componentRef, bool shouldExist) where T : Component {
        return (shouldExist) ? (gameObject.GetComponent<T>() ? gameObject.GetComponent<T>() : gameObject.AddComponent<T>()) : DestroyComponent<T>(gameObject);
    }

    public static T CopyComponent<T>(GameObject gameObject, T componentRef) where T : Component {
        System.Type type = componentRef.GetType();
        Component copy = DoComponent<T>(gameObject, true);
        System.Reflection.FieldInfo[] fields = type.GetFields();
        foreach (System.Reflection.FieldInfo field in fields) {
            field.SetValue(copy, field.GetValue(componentRef));
        }
        return copy as T;
    }

    public static void DoEvent<T>(bool shouldExist, ref T eventRef, UnityAction method) where T : UnityEvent {

        if (eventRef == null) {
            // Debug.Log("eventref " + typeof(T).Name + " is null, setting up!");
            eventRef = (T)Activator.CreateInstance(typeof(T));
        }

        if (eventRef == null) {
            // Debug.Log(typeof(T).Name + " is fucked");
        }

        //Debug.Log("eventRef # = " + eventRef.GetPersistentEventCount());
        if (eventRef.GetPersistentEventCount() > 0) {

            List<int> listeners = new List<int>();
            for (int i = 0; i < eventRef.GetPersistentEventCount(); i++) {
                listeners.Add(i);
            }

            for (int i = 0; i < listeners.Count; i++) {
                //Debug.Log(eventRef.GetPersistentMethodName(listeners[i]) + " = " + method.Method.Name + "?");
                if (eventRef.GetPersistentMethodName(listeners[i]) == method.Method.Name) {

                    if (!shouldExist) {
                        if (!Application.isPlaying) {
#if UNITY_EDITOR
                            UnityEventTools.RemovePersistentListener(eventRef, listeners[i]);
                            listeners.Remove(i);
#endif
                        } else {
                            eventRef.RemoveListener(method);
                            listeners.Remove(i);
                        }
                    }
                    return;
                }
            }
        }

        if (!Application.isPlaying) {
#if UNITY_EDITOR
            UnityEventTools.AddPersistentListener(eventRef, method);
#endif
        } else {
            eventRef.AddListener(method);
        }
    }
}