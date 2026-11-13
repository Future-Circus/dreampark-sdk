// The Jump Pads author's Scene-view handles (goal-ui, Oct 20 2026): drag each pad on the floor; add / remove pads; copy
// the stop's options.pads JSON or the goal block's stop.jump{…} line.
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    [CustomEditor(typeof(PlaylistJumpPadsAuthoring))]
    public sealed class PlaylistJumpPadsAuthoringEditor : UnityEditor.Editor
    {
        [MenuItem("DreamPark/Playlist/Jump Pads: add to the selected attraction")]
        static void AddToSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null) { EditorUtility.DisplayDialog("Jump Pads", "Select an attraction (its AttractionTemplate object) first.", "OK"); return; }
            var tpl = go.GetComponentInChildren<AttractionTemplate>(true);
            var target = tpl != null ? tpl.gameObject : go;
            var a = target.GetComponent<PlaylistJumpPadsAuthoring>() ?? Undo.AddComponent<PlaylistJumpPadsAuthoring>(target);
            Selection.activeGameObject = a.gameObject;
        }

        public override void OnInspectorGUI()
        {
            var a = (PlaylistJumpPadsAuthoring)target;
            DrawDefaultInspector();
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add pad"))
                {
                    Undo.RecordObject(a, "Add jump pad");
                    Vector3 last = a.pads.Count > 0 ? a.pads[a.pads.Count - 1] : Vector3.zero;
                    a.pads.Add(last + new Vector3(0f, 0f, 1.6f));
                }
                if (GUILayout.Button("Remove last") && a.pads.Count > 2) { Undo.RecordObject(a, "Remove jump pad"); a.pads.RemoveAt(a.pads.Count - 1); }
            }
            EditorGUILayout.LabelField("options.pads", a.OptionsJson(), EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy options.pads")) EditorGUIUtility.systemCopyBuffer = a.OptionsJson();
                if (GUILayout.Button("Copy stop.jump{…}")) EditorGUIUtility.systemCopyBuffer = a.GoalLua();
            }
        }

        void OnSceneGUI()
        {
            var a = (PlaylistJumpPadsAuthoring)target;
            var tr = a.transform;
            for (int i = 0; i < a.pads.Count; i++)
            {
                Vector3 world = tr.TransformPoint(new Vector3(a.pads[i].x, 0f, a.pads[i].z));
                Handles.color = MagicUI.MagicLook.VioletBright;
                Handles.Label(world + Vector3.up * 0.3f, ((char)('A' + i)).ToString());
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.Slider2D(world, Vector3.up, tr.right, tr.forward, a.radius, Handles.CircleHandleCap, 0.05f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(a, "Move jump pad");
                    Vector3 l = tr.InverseTransformPoint(moved);
                    a.pads[i] = new Vector3(l.x, 0f, l.z);
                }
            }
        }
    }
}
