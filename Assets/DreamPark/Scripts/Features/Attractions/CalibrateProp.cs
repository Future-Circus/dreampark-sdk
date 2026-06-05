namespace DreamPark
{
    using System;
    using Defective.JSON;
    using UnityEngine;

    public class CalibrateProp : MonoBehaviour
    {
        [Header("Raycast Input")]
        public float updateInterval = 2f;
        [NonSerialized] public LayerMask arMeshLayer = -1;
        public float raycastHeight = 10f;
        public float raycastLength = 20f;

        [Header("Integration")]
        public PropTemplate propTemplate;
        [HideInInspector] public JSONObject pointData;
        [ReadOnly] public bool calibrated = false;
        [HideInInspector] public bool EditorOverride = false;

        private float _lastUpdateTime = -Mathf.Infinity;

        private void Start()
        {
            if (propTemplate == null)
                propTemplate = GetComponent<PropTemplate>();

            if (arMeshLayer == -1)
                arMeshLayer = LayerMask.GetMask("ARMesh");

#if DREAMPARKCORE
            if (pointData != null)
            {
                ApplyCalibrationData(pointData);
                pointData = null;
            }
#endif
        }

        private void Update()
        {
            if (!isCalibrating || Time.time - _lastUpdateTime <= updateInterval)
                return;

            _lastUpdateTime = Time.time;
            CalibrateSinglePoint();
        }

        public void CalibrateSinglePoint()
        {
            if (propTemplate == null)
                return;

            Vector3 source = propTemplate.transform.position;
            Ray ray = new Ray(source + Vector3.up * raycastHeight, Vector3.down);

            if (Physics.Raycast(ray, out var hit, raycastLength, arMeshLayer))
            {
                float yOffset = hit.point.y - propTemplate.transform.position.y;
                propTemplate.ApplyCalibrationYOffset(yOffset);
                calibrated = true;
            }
        }

        public JSONObject CompileCalibrationData()
        {
            if (propTemplate == null)
                return new JSONObject();

            var point = new JSONObject();
            point.AddField("0", (propTemplate.SurfaceHeight - propTemplate.transform.position.y).RoundFloat().ToString("F3"));
            return point;
        }

        public void ApplyCalibrationData(JSONObject calibrationData)
        {
            if (calibrationData == null || !calibrationData.HasField("0"))
                return;

            pointData = calibrationData;
            float yOffset = float.Parse(calibrationData.GetField("0").stringValue);
            propTemplate.ApplyCalibrationYOffset(yOffset);
            calibrated = true;
        }

        public void Clear()
        {
            calibrated = false;
            if (propTemplate != null)
                propTemplate.ApplyCalibrationYOffset(0f);
        }

        public bool isCalibrating
        {
            get
            {
#if DREAMPARKCORE
                return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.unityState == "CALIBRATE";
#else
                return EditorOverride;
#endif
            }
        }
    }
}
