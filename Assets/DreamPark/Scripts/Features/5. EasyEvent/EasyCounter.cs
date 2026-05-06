using System.Collections.Generic;
using UnityEngine;

public class EasyCounter : EasyEvent
{
    public static Dictionary<string, int> globalCounts = new();
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

        if (!globalCounts.TryGetValue(name, out int value))
            return defaultValue;

        return value;
    }

    public static void SetGlobalCount(string name, int value)
    {
        if (string.IsNullOrEmpty(name))
            return;

        globalCounts[name] = value;
    }

    public static int AddGlobalCount(string name, int amount = 1)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        int nextValue = GetGlobalCount(name, 0) + amount;
        globalCounts[name] = nextValue;
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
