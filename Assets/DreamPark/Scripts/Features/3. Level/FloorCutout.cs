namespace DreamPark
{
    using UnityEngine;
    using System.Collections.Generic;
    using System.Linq;
    using Meta.XR.EnvironmentDepth;
    using System;
    using System.IO;

    #if UNITY_EDITOR
    using UnityEditor;
    using UnityEditor.SceneManagement;

    [CustomEditor(typeof(FloorCutout), true)]
    public class ProceduralPitEditor : UnityEditor.Editor
    {
        [SerializeField] private bool _boot = false;
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI(); // Draws the default inspector

            FloorCutout Pit = (FloorCutout)target;

            if (!_boot)
            {
                _boot = true;
                if (Pit.points.Count == 0)
                {
                    Pit.points = new Vector3[]{
                    new Vector3(-1, 0, -1),
                    new Vector3(1, 0, -1),
                    new Vector3(1, 0, 1),
                    new Vector3(-1, 0, 1)
                }.ToList();
                }
            }
        }

        private Vector3 PlayModeConversion(Vector3 v, FloorCutout p)
        {
            if (Application.isPlaying)
            {
                return p.ogPosition.TransformPoint(v);
            }
            else
            {
                return p.transform.TransformPoint(v);
            }
        }

        private void OnSceneGUI()
        {
            FloorCutout Pit = (FloorCutout)target;
            int handleControlId = -1;

            Handles.color = Color.red;
            for (int i = 0; i < Pit.points.Count; i++)
            {

                Vector3 worldPosition = PlayModeConversion(Pit.points[i], Pit);
                Vector3 lastPosition = worldPosition;

                handleControlId = GUIUtility.GetControlID(FocusType.Passive);

                Vector3 newWorldPosition = Handles.FreeMoveHandle(
                    worldPosition,
                    0.2f,
                    Vector3.zero,
                    Handles.CircleHandleCap
                );

                if (newWorldPosition != worldPosition)
                {
                    Vector3 newLocalPosition = Pit.transform.InverseTransformPoint(newWorldPosition);
                    newLocalPosition.y = 0;
                    Pit.points[i] = newLocalPosition;
                    if (!Application.isPlaying)
                    {
                        EditorUtility.SetDirty(Pit);
                        Undo.RegisterCompleteObjectUndo(Pit, "Move Point");
                    }
                }
            }

            // Draw lines connecting the points
            Handles.color = Color.green;
            for (int i = 0; i < Pit.points.Count; i++)
            {
                Handles.DrawLine(
                    PlayModeConversion(Pit.points[i], Pit),
                    PlayModeConversion(Pit.points[(i + 1) % Pit.points.Count], Pit)
                );
            }
        }
    }
    #endif
    public class FloorCutout : MonoBehaviour
    {
        public List<Vector3> points = new List<Vector3>();
        [NonSerialized] public Transform ogPosition;
       
        void Awake()
        {
            ogPosition = transform;
        }
    }
}
