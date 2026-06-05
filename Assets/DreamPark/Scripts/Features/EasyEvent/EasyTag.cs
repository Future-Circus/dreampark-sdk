using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
[CustomEditor(typeof(EasyTag), true)]
public class EasyTagEditor : EasyEventEditor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        EasyTag easyTag = (EasyTag)target;
        //make newTag a dropdown of all tags in the project
        string[] tags = UnityEditorInternal.InternalEditorUtility.tags;
        int selectedIndex = Mathf.Max(0, System.Array.IndexOf(tags, easyTag.newTag));
        int newSelectedIndex = EditorGUILayout.Popup("New Tag", selectedIndex, tags);
        if (newSelectedIndex != selectedIndex) {
            easyTag.newTag = tags[newSelectedIndex];
            EditorUtility.SetDirty(easyTag);
        }
    }
}
#endif

public class EasyTag : EasyEvent
{
    [HideInInspector] public string newTag;

    public override void OnEvent(object arg0 = null)
    {
        gameObject.tag = newTag;
        onEvent?.Invoke(null);
    }
}
