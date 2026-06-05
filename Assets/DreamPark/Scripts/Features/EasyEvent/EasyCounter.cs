using System.Collections.Generic;
using UnityEngine;

public class EasyCounter : EasyEvent
{
    // Global counts live on a scene-scoped GameObject (NOT DontDestroyOnLoad) instead of a static
    // dictionary, so they are destroyed with the scene and lazily rebuilt empty the next session.
    // This makes global counters reset automatically on a Main.scene reload -- no manual clearing,
    // no cross-session leak (e.g. TOTAL_SUNS no longer accumulates across resets), and no ordering
    // race (the first AddGlobalCount of a session creates the empty store, so it doesn't matter who
    // touches it first). NOTE: the static field below is only a POINTER; the data lives on the
    // GameObject. Unity reports a destroyed object as == null, which is what drives the rebuild.
    private sealed class GlobalStore : MonoBehaviour
    {
        public readonly Dictionary<string, int> counts = new();
    }

    private static GlobalStore _store;

#if UNITY_EDITOR
    // Edit-mode inspector reads must not spawn a runtime GameObject; mirror the old "empty" behaviour.
    private static readonly Dictionary<string, int> _editorCounts = new();
#endif

    private static Dictionary<string, int> Counts
    {
        get
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return _editorCounts;
#endif
            if (_store == null)
                _store = new GameObject("[EasyCounterGlobals]").AddComponent<GlobalStore>();
            return _store.counts;
        }
    }
    public enum VariableType {
        LOCAL,
        GLOBAL
    }

    public VariableType variableType = VariableType.LOCAL;
    [ShowIf("variableType", VariableType.LOCAL)] [ReadOnly] public int count = 0;
    [ShowIf("variableType", VariableType.GLOBAL)] public string variableName = "count";
    public VariableType totalCountSource = VariableType.LOCAL;
    [ShowIf("totalCountSource", VariableType.LOCAL)] public int totalCount = 6;
    [ShowIf("totalCountSource", VariableType.GLOBAL)] public string totalCountVariableName = "MIN_SUNS";
    [ShowIf("totalCountSource", VariableType.GLOBAL)] [ReadOnly] public int runtimeTotalCountValue;

    [Tooltip("Optional event to invoke when count does not equal totalCount")]
    public EasyEvent onNotEqual;

    public override void Start() {
        eventOnStart = false;
        base.Start();
        RefreshRuntimeTotalCountValue();
    }

    #if UNITY_EDITOR
    private void Update()
    {
        RefreshRuntimeTotalCountValue();
    }
    #endif

    public static int GetGlobalCount(string name, int defaultValue = 0)
    {
        if (string.IsNullOrEmpty(name))
            return defaultValue;

        if (!Counts.TryGetValue(name, out int value))
            return defaultValue;

        return value;
    }

    public static void SetGlobalCount(string name, int value)
    {
        if (string.IsNullOrEmpty(name))
            return;

        Counts[name] = value;
    }

    public static int AddGlobalCount(string name, int amount = 1)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        int nextValue = GetGlobalCount(name, 0) + amount;
        Counts[name] = nextValue;
        return nextValue;
    }

    private int ResolveTargetTotalCount()
    {
        if (totalCountSource == VariableType.GLOBAL)
        {
            return GetGlobalCount(totalCountVariableName, totalCount);
        }

        return totalCount;
    }

    private void RefreshRuntimeTotalCountValue()
    {
        if (totalCountSource == VariableType.GLOBAL)
        {
            runtimeTotalCountValue = GetGlobalCount(totalCountVariableName, totalCount);
        }
    }

    public override void OnEvent(object arg0 = null) {
        int targetTotalCount = ResolveTargetTotalCount();

        if (variableType == VariableType.LOCAL) {
            count++;
            if (count == targetTotalCount) {
                onEvent?.Invoke(arg0);
            } else {
                onNotEqual?.OnEvent(arg0);
            }
        } else if (variableType == VariableType.GLOBAL) {
            int currentCount = AddGlobalCount(variableName, 1);
            if (currentCount == targetTotalCount) {
                onEvent?.Invoke(arg0);
            } else {
                onNotEqual?.OnEvent(arg0);
            }
        }
    }
}
