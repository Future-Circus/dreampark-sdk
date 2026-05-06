using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;
using System.Collections.Generic;

public class ScriptRebinder : EditorWindow
{
    MonoScript oldScript;
    MonoScript newScript;

    // Menu item removed from the DreamPark top-level menu.
    // Window can still be opened programmatically via
    // EditorWindow.GetWindow<ScriptRebinder>("Script Rebinder") if needed.
    static void Open()
    {
        GetWindow<ScriptRebinder>("Script Rebinder");
    }

    void OnGUI()
    {
        GUILayout.Label("Replace Script Everywhere", EditorStyles.boldLabel);

        oldScript = (MonoScript)EditorGUILayout.ObjectField(
            "Old Script", oldScript, typeof(MonoScript), false);

        newScript = (MonoScript)EditorGUILayout.ObjectField(
            "New Script", newScript, typeof(MonoScript), false);

        GUI.enabled = oldScript && newScript;

        if (GUILayout.Button("Replace In Open Scenes"))
        {
            ReplaceInOpenScenes();
        }

        if (GUILayout.Button("Replace In Prefabs"))
        {
            ReplaceInPrefabs();
        }

        GUI.enabled = true;
    }

    void ReplaceInOpenScenes()
    {
        var oldType = oldScript.GetClass();
        var newType = newScript.GetClass();

        if (oldType == null || newType == null)
        {
            Debug.LogError("Invalid script types.");
            return;
        }

        foreach (var root in GetAllSceneObjects())
        {
            ReplaceOnGameObject(root, oldType, newType);
        }

        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Finished replacing in open scenes.");
    }

    void ReplaceInPrefabs()
    {
        var oldType = oldScript.GetClass();
        var newType = newScript.GetClass();

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");

        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = PrefabUtility.LoadPrefabContents(path);

            bool modified = ReplaceOnGameObject(prefab, oldType, newType);

            if (modified)
            {
                PrefabUtility.SaveAsPrefabAsset(prefab, path);
            }

            PrefabUtility.UnloadPrefabContents(prefab);
        }

        Debug.Log("Finished replacing in prefabs.");
    }

    bool ReplaceOnGameObject(GameObject go, System.Type oldType, System.Type newType)
    {
        bool replacedAny = false;

        var oldComp = go.GetComponent(oldType);
        if (oldComp != null)
        {
            SerializedObject oldSO = new SerializedObject(oldComp);

            Undo.RegisterCompleteObjectUndo(go, "Replace Script");

            Object.DestroyImmediate(oldComp, true);
            Component newComp = go.AddComponent(newType);

            SerializedObject newSO = new SerializedObject(newComp);
            CopySerializedFields(oldSO, newSO);

            newSO.ApplyModifiedProperties();
            replacedAny = true;
        }

        foreach (Transform child in go.transform)
        {
            replacedAny |= ReplaceOnGameObject(child.gameObject, oldType, newType);
        }

        return replacedAny;
    }

    void CopySerializedFields(SerializedObject from, SerializedObject to)
    {
        var prop = from.GetIterator();
        while (prop.NextVisible(true))
        {
            if (prop.name == "m_Script") continue;

            SerializedProperty destProp = to.FindProperty(prop.name);
            if (destProp != null)
            {
                destProp.serializedObject.CopyFromSerializedProperty(prop);
            }
        }
    }

    IEnumerable<GameObject> GetAllSceneObjects()
    {
        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var scene = EditorSceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (var root in scene.GetRootGameObjects())
                yield return root;
        }
    }
}