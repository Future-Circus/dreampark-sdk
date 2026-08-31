using System.Collections.Generic;
using UnityEngine;

public class EasyMath : EasyEvent
{
    public static Dictionary<string, float> globalValues = new();

    public enum VariableType
    {
        LOCAL,
        GLOBAL
    }

    public enum Operation
    {
        ADD,
        SUBTRACT,
        MULTIPLY,
        DIVIDE
    }

    public enum OperandSource
    {
        CONSTANT,
        LOCAL,
        GLOBAL
    }

    [Header("Target")]
    public VariableType variableType = VariableType.LOCAL;
    [ShowIf("variableType", VariableType.LOCAL)] public float localValue = 0f;
    [ShowIf("variableType", VariableType.GLOBAL)] public string variableName = "value";

    [Header("Math")]
    public Operation operation = Operation.ADD;
    public OperandSource operandSource = OperandSource.CONSTANT;
    [ShowIf("operandSource", OperandSource.CONSTANT)] public float constantOperand = 1f;
    [ShowIf("operandSource", OperandSource.LOCAL)] public float localOperand = 1f;
    [ShowIf("operandSource", OperandSource.GLOBAL)] public string operandVariableName = "operand";

    [Header("Runtime")]
    [ReadOnly] public float runtimeCurrentValue;
    [ReadOnly] public float runtimeOperandValue;
    [ReadOnly] public float runtimeResultValue;

    public override void Start()
    {
        base.Start();
        RefreshRuntimeValues();
    }

#if UNITY_EDITOR
    private void Update()
    {
        RefreshRuntimeValues();
    }
#endif

    public static float GetGlobalValue(string name, float defaultValue = 0f)
    {
        if (string.IsNullOrEmpty(name))
            return defaultValue;

        if (globalValues.TryGetValue(name, out float value))
            return value;

        // Interop with existing EasyCounter globals so math can read values
        // already managed by other EasyEvents.
        return EasyCounter.GetGlobalCount(name, (int)defaultValue);
    }

    public static void SetGlobalValue(string name, float value)
    {
        if (string.IsNullOrEmpty(name))
            return;

        globalValues[name] = value;
        // Keep EasyCounter global ints in sync for compatibility.
        EasyCounter.SetGlobalCount(name, Mathf.RoundToInt(value));
    }

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;

        float current = ResolveCurrentValue();
        float operand = ResolveOperandValue();
        float result = current;

        switch (operation)
        {
            case Operation.ADD:
                result = current + operand;
                break;
            case Operation.SUBTRACT:
                result = current - operand;
                break;
            case Operation.MULTIPLY:
                result = current * operand;
                break;
            case Operation.DIVIDE:
                if (Mathf.Abs(operand) <= Mathf.Epsilon)
                {
                    Debug.LogWarning($"[EasyMath] {gameObject.name} divide by zero prevented");
                    result = current;
                }
                else
                {
                    result = current / operand;
                }
                break;
        }

        ApplyResult(result);
        RefreshRuntimeValues();
        onEvent?.Invoke(arg0);
    }

    private float ResolveCurrentValue()
    {
        if (variableType == VariableType.GLOBAL)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                Debug.LogWarning($"[EasyMath] {gameObject.name} variableName is empty (GLOBAL target)");
                return localValue;
            }
            return GetGlobalValue(variableName, localValue);
        }

        return localValue;
    }

    private float ResolveOperandValue()
    {
        switch (operandSource)
        {
            case OperandSource.LOCAL:
                return localOperand;
            case OperandSource.GLOBAL:
                if (string.IsNullOrEmpty(operandVariableName))
                {
                    Debug.LogWarning($"[EasyMath] {gameObject.name} operandVariableName is empty (GLOBAL operand)");
                    return 0f;
                }
                return GetGlobalValue(operandVariableName, 0f);
            default:
                return constantOperand;
        }
    }

    private void ApplyResult(float value)
    {
        if (variableType == VariableType.GLOBAL)
        {
            SetGlobalValue(variableName, value);
        }
        else
        {
            localValue = value;
        }
    }

    private void RefreshRuntimeValues()
    {
        runtimeCurrentValue = ResolveCurrentValue();
        runtimeOperandValue = ResolveOperandValue();

        switch (operation)
        {
            case Operation.ADD:
                runtimeResultValue = runtimeCurrentValue + runtimeOperandValue;
                break;
            case Operation.SUBTRACT:
                runtimeResultValue = runtimeCurrentValue - runtimeOperandValue;
                break;
            case Operation.MULTIPLY:
                runtimeResultValue = runtimeCurrentValue * runtimeOperandValue;
                break;
            case Operation.DIVIDE:
                runtimeResultValue = Mathf.Abs(runtimeOperandValue) <= Mathf.Epsilon
                    ? runtimeCurrentValue
                    : runtimeCurrentValue / runtimeOperandValue;
                break;
        }
    }
}
