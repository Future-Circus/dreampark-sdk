using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using System.Reflection;
#endif

// =============================================================
// ATTRIBUTE
// =============================================================
public class ShowIfAttribute : PropertyAttribute
{
    public string conditionName;
    public object compareValue;
    
    public ShowIfAttribute(string conditionName)
    {
        this.conditionName = conditionName;
        this.compareValue = null;
    }

    public ShowIfAttribute(string conditionName, object compareValue)
    {
        this.conditionName = conditionName;
        this.compareValue = compareValue;
    }
}

public class HideIfAttribute : ShowIfAttribute
{
    public HideIfAttribute(string conditionName) : base(conditionName) {}
    public HideIfAttribute(string conditionName, object compareValue) : base(conditionName, compareValue) {}
}

#if UNITY_EDITOR
// =============================================================
// DRAWER
// =============================================================
[CustomPropertyDrawer(typeof(ShowIfAttribute))]
[CustomPropertyDrawer(typeof(HideIfAttribute))]
public class ConditionalDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        bool isShow = Evaluate(property);

        bool isHideDrawer = attribute is HideIfAttribute;
        if (isHideDrawer) isShow = !isShow;

        if (isShow)
            EditorGUI.PropertyField(position, property, label, true);
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        bool isShow = Evaluate(property);

        bool isHideDrawer = attribute is HideIfAttribute;
        if (isHideDrawer) isShow = !isShow;

        if (isShow)
            return EditorGUI.GetPropertyHeight(property, label, true);
        
        return -EditorGUIUtility.standardVerticalSpacing;
    }

    bool Evaluate(SerializedProperty property)
    {
        ShowIfAttribute cond = (ShowIfAttribute)attribute;

        SerializedProperty sourceProp = FindRelativeProperty(property, cond.conditionName);
        if (sourceProp != null)
        {
            switch (sourceProp.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return cond.compareValue == null
                        ? sourceProp.boolValue
                        : sourceProp.boolValue.Equals(cond.compareValue);

                case SerializedPropertyType.Enum:
                    int enumValue = sourceProp.enumValueIndex;
                    return cond.compareValue == null
                        ? enumValue != 0
                        : enumValue == (int)cond.compareValue;

                case SerializedPropertyType.Integer:
                    return sourceProp.intValue.Equals(cond.compareValue);

                case SerializedPropertyType.String:
                    return sourceProp.stringValue.Equals(cond.compareValue);
            }
        }

        return true;
    }

    SerializedProperty FindRelativeProperty(SerializedProperty property, string name)
    {
        string path = property.propertyPath.Replace(property.name, name);
        return property.serializedObject.FindProperty(path);
    }
}
#endif