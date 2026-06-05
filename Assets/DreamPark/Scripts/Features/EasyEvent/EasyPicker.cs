using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Picks a value from an array and passes it as arg0 to the next event.
/// Supports different value types and selection modes (incremental or random).
/// </summary>
public class EasyPicker : EasyEvent
{
    public enum ValueType
    {
        String,
        Int,
        Float,
        Vector3,
        GameObject,
        Transform,
        EasyObject      // Picks a string key, then looks up the GameObject via EasyObject.Get()
    }

    public enum SelectionMode
    {
        Incremental,    // Goes through values in order, loops back to start
        Random,         // Picks a random value each time (can repeat)
        Shuffle         // Random but no repeats until all values used
    }

    [Header("Picker Settings")]
    public ValueType valueType = ValueType.GameObject;
    public SelectionMode selectionMode = SelectionMode.Incremental;

    [HideInInspector] public string[] stringValues;
    [HideInInspector] public int[] intValues;
    [HideInInspector] public float[] floatValues;
    [HideInInspector] public Vector3[] vector3Values;
    [HideInInspector] public GameObject[] gameObjectValues;
    [HideInInspector] public Transform[] transformValues;
    [HideInInspector] public string[] easyObjectKeys;

    private int currentIndex = 0;
    private int[] shuffleOrder;
    private int shuffleIndex = 0;

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        object pickedValue = PickValue();

        if (pickedValue != null)
        {
            onEvent?.Invoke(pickedValue);
        }
        else
        {
            Debug.LogWarning($"[EasyPicker] {gameObject.name} - no values to pick from");
            onEvent?.Invoke(null);
        }

        OnEventDisable();
    }

    private object PickValue()
    {
        switch (valueType)
        {
            case ValueType.String:
                return PickFromArray(stringValues);
            case ValueType.Int:
                return PickFromArray(intValues);
            case ValueType.Float:
                return PickFromArray(floatValues);
            case ValueType.Vector3:
                return PickFromArray(vector3Values);
            case ValueType.GameObject:
                return PickFromArray(gameObjectValues);
            case ValueType.Transform:
                return PickFromArray(transformValues);
            case ValueType.EasyObject:
                string key = PickFromArray(easyObjectKeys);
                if (!string.IsNullOrEmpty(key))
                    return EasyObject.Get(key);
                return null;
            default:
                return null;
        }
    }

    private T PickFromArray<T>(T[] array)
    {
        if (array == null || array.Length == 0)
            return default;

        int index;
        switch (selectionMode)
        {
            case SelectionMode.Random:
                index = Random.Range(0, array.Length);
                break;

            case SelectionMode.Shuffle:
                // Initialize or reshuffle when needed
                if (shuffleOrder == null || shuffleOrder.Length != array.Length || shuffleIndex >= array.Length)
                {
                    InitializeShuffle(array.Length);
                }
                index = shuffleOrder[shuffleIndex];
                shuffleIndex++;
                break;

            default: // Incremental
                index = currentIndex;
                currentIndex = (currentIndex + 1) % array.Length;
                break;
        }

        Debug.Log($"[EasyPicker] {gameObject.name} picked index {index}: {array[index]}");
        return array[index];
    }

    private void InitializeShuffle(int length)
    {
        shuffleOrder = new int[length];
        for (int i = 0; i < length; i++)
            shuffleOrder[i] = i;

        // Fisher-Yates shuffle
        for (int i = length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int temp = shuffleOrder[i];
            shuffleOrder[i] = shuffleOrder[j];
            shuffleOrder[j] = temp;
        }

        shuffleIndex = 0;
    }

    /// <summary>
    /// Reset the incremental index back to 0.
    /// </summary>
    public void ResetIndex()
    {
        currentIndex = 0;
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(EasyPicker),true)]
public class EasyPickerEditor : EasyEventEditor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Draw default fields except the value arrays
        DrawPropertiesExcluding(serializedObject,
            "stringValues", "intValues", "floatValues",
            "vector3Values", "gameObjectValues", "transformValues", "easyObjectKeys");

        // Get the current value type
        EasyPicker picker = (EasyPicker)target;

        // Draw only the relevant array based on valueType
        SerializedProperty arrayProp = null;
        switch (picker.valueType)
        {
            case EasyPicker.ValueType.String:
                arrayProp = serializedObject.FindProperty("stringValues");
                break;
            case EasyPicker.ValueType.Int:
                arrayProp = serializedObject.FindProperty("intValues");
                break;
            case EasyPicker.ValueType.Float:
                arrayProp = serializedObject.FindProperty("floatValues");
                break;
            case EasyPicker.ValueType.Vector3:
                arrayProp = serializedObject.FindProperty("vector3Values");
                break;
            case EasyPicker.ValueType.GameObject:
                arrayProp = serializedObject.FindProperty("gameObjectValues");
                break;
            case EasyPicker.ValueType.Transform:
                arrayProp = serializedObject.FindProperty("transformValues");
                break;
            case EasyPicker.ValueType.EasyObject:
                arrayProp = serializedObject.FindProperty("easyObjectKeys");
                break;
        }

        if (arrayProp != null)
        {
            EditorGUILayout.PropertyField(arrayProp, new GUIContent("Values"), true);
        }

        serializedObject.ApplyModifiedProperties();
        DrawEasyEventInspectorFooter(picker);
    }
}
#endif
