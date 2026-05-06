using UnityEngine;
using System.Collections.Generic;
using System.Collections;

/// <summary>
/// EasyObject allows storing and retrieving GameObjects by a key name.
/// Use "Set" mode to store a GameObject under a key.
/// Use "Get" mode to retrieve it and pass it to the next event as arg0.
/// </summary>
public class EasyObject : EasyEvent
{
    public enum Mode
    {
        Get,
        Set,
        Equals
    }

    // Global registry of objects by key
    private static Dictionary<string, GameObject> _registry = new Dictionary<string, GameObject>();

    [Header("Settings")]
    public Mode mode = Mode.Set;

    [Tooltip("The key name to store/retrieve the GameObject")]
    public string key = "default";

    [Tooltip("The GameObject to store (only used in Set mode)")]
    [ShowIf("mode", Mode.Set)]
    public GameObject targetObject;

    [Tooltip("If true in Set mode, uses this GameObject instead of targetObject")]
    [ShowIf("mode", Mode.Set)]
    public bool useSelf = false;

    [Tooltip("Object to compare against (for Equals mode)")]
    [ShowIf("mode", Mode.Equals)]
    public GameObject compareObject;

    [Tooltip("Event to invoke if objects are NOT equal (for Equals mode)")]
    [ShowIf("mode", Mode.Equals)]
    public EasyEvent onNotEqual;

    [Tooltip("If true in Get mode, keeps checking until an object exists for this key")]
    [ShowIf("mode", Mode.Get)]
    public bool waitForObjectIfMissing = true;

    [Tooltip("How often to retry key lookup while waiting")]
    [ShowIf("mode", Mode.Get)]
    [Range(0.05f, 5f)]
    public float retryInterval = 0.25f;

    [Tooltip("Log warnings/errors for missing keys and invalid configuration")]
    public bool logIssues = true;

    [Header("Runtime (Read Only)")]
    [Tooltip("Current object resolved from this key at runtime")]
    [ReadOnly] public GameObject runtimeObject;

    private Coroutine _waitForObjectRoutine;
    private bool _loggedMissingForCurrentWait;

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;

        if (string.IsNullOrEmpty(key))
        {
            runtimeObject = null;
            LogIssue("key is empty");
            OnEventDisable();
            return;
        }

        if (mode == Mode.Set)
        {
            GameObject objToStore = useSelf ? gameObject : targetObject;

            // Also allow setting from arg0 if no target specified
            if (objToStore == null && arg0 is GameObject go)
            {
                objToStore = go;
            }
            else if (objToStore == null && arg0 is Transform t)
            {
                objToStore = t.gameObject;
            }

            if (objToStore != null)
            {
                _registry[key] = objToStore;
                runtimeObject = objToStore;
                Debug.Log($"[EasyObject] Stored '{objToStore.name}' under key '{key}'");
            }
            else
            {
                runtimeObject = null;
                LogIssue($"no object to store for key '{key}'");
            }

            onEvent?.Invoke(arg0);
            OnEventDisable();
        }
        else if (mode == Mode.Get)
        {
            if (_registry.TryGetValue(key, out GameObject storedObject) && storedObject != null)
            {
                runtimeObject = storedObject;
                StopWaitingForObject();
                Debug.Log($"[EasyObject] Retrieved '{storedObject.name}' from key '{key}'");
                onEvent?.Invoke(storedObject);
                OnEventDisable();
            }
            else
            {
                runtimeObject = null;

                if (waitForObjectIfMissing)
                {
                    if (!_loggedMissingForCurrentWait)
                    {
                        LogIssue($"No object found for key '{key}'. Waiting and retrying every {retryInterval:0.##}s.");
                        _loggedMissingForCurrentWait = true;
                    }

                    if (_waitForObjectRoutine == null)
                        _waitForObjectRoutine = StartCoroutine(WaitForObjectAndInvoke());

                    return;
                }

                LogIssue($"No object found for key '{key}'");
                onEvent?.Invoke(null);
                OnEventDisable();
            }
        }
        else // Mode.Equals
        {
            // Get the stored object for comparison
            GameObject storedObject = null;
            _registry.TryGetValue(key, out storedObject);
            runtimeObject = storedObject;

            // Compare against compareObject (or arg0 if compareObject not set)
            GameObject objectToCompare = compareObject;
            if (objectToCompare == null && arg0 is GameObject go)
                objectToCompare = go;
            else if (objectToCompare == null && arg0 is Transform t)
                objectToCompare = t.gameObject;

            bool isEqual = (storedObject == objectToCompare);

            if (isEqual)
            {
                Debug.Log($"[EasyObject] Equals check PASSED for key '{key}'");
                onEvent?.Invoke(arg0);
            }
            else
            {
                Debug.Log($"[EasyObject] Equals check FAILED for key '{key}'");
                onNotEqual?.OnEvent(arg0);
            }

            OnEventDisable();
        }
    }

    public override void OnEventDisable()
    {
        StopWaitingForObject();
        _loggedMissingForCurrentWait = false;
        base.OnEventDisable();
    }

    private IEnumerator WaitForObjectAndInvoke()
    {
        while (enabled && gameObject.activeInHierarchy)
        {
            if (_registry.TryGetValue(key, out GameObject storedObject) && storedObject != null)
            {
                runtimeObject = storedObject;
                Debug.Log($"[EasyObject] Found '{storedObject.name}' for key '{key}' after waiting.");
                _waitForObjectRoutine = null;
                _loggedMissingForCurrentWait = false;
                onEvent?.Invoke(storedObject);
                OnEventDisable();
                yield break;
            }

            yield return new WaitForSeconds(retryInterval);
        }

        _waitForObjectRoutine = null;
        _loggedMissingForCurrentWait = false;
        OnEventDisable();
    }

    private void StopWaitingForObject()
    {
        if (_waitForObjectRoutine == null)
            return;

        StopCoroutine(_waitForObjectRoutine);
        _waitForObjectRoutine = null;
    }

    private void LogIssue(string message)
    {
        if (!logIssues)
            return;

        Debug.LogWarning($"[EasyObject] {gameObject.name} - {message}");
    }

    /// <summary>
    /// Static method to get an object by key from anywhere.
    /// </summary>
    public static GameObject Get(string key)
    {
        if (_registry.TryGetValue(key, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    /// <summary>
    /// Static method to set an object by key from anywhere.
    /// </summary>
    public static void Set(string key, GameObject obj)
    {
        _registry[key] = obj;
    }

    /// <summary>
    /// Static method to clear a key.
    /// </summary>
    public static void Clear(string key)
    {
        _registry.Remove(key);
    }

    /// <summary>
    /// Static method to clear all stored objects.
    /// </summary>
    public static void ClearAll()
    {
        _registry.Clear();
    }
}
