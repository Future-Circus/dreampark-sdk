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
        /// Rematerialized floor prior (Auto-Calibration-Spec §5.3). Separate
        /// mask, not merged into arMeshLayer, so live mesh always wins.
        [NonSerialized] private LayerMask arMeshPriorLayer;

        // ── Search volume ────────────────────────────────────────────────
        // Same contract as CalibrateLevel: these are the MINIMUM reach, and
        // GroundProbe.MeasureSpan grows them to cover whatever ground actually
        // sits under the prop. Existing serialized values on shipped prefabs
        // keep their meaning — a prefab authored with raycastHeight 10 /
        // raycastLength 20 still searches at least 10m up and 10m down, it just
        // no longer STOPS there when the ground is further away.
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
            arMeshPriorLayer = LayerMask.GetMask("ARMeshPrior");

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

            // Props and levels now genuinely agree on where the ground is —
            // same origin rule (the object's own position), same span
            // measurement, same "what counts as floor" test. This used to be a
            // bare Physics.Raycast that took the FIRST hit regardless of
            // orientation, so a prop placed under a table or against a wall
            // bound itself to that surface while a level in the same spot
            // correctly saw past it to the floor. See GroundProbe.cs.
            Bounds footprint = new Bounds(source, new Vector3(1f, 0.01f, 1f));
            GroundProbe.Span span = GroundProbe.MeasureSpan(
                footprint, arMeshLayer, arMeshPriorLayer,
                raycastHeight, Mathf.Max(0f, raycastLength - raycastHeight));

            if (GroundProbe.TryFindGround(source, span, arMeshLayer, arMeshPriorLayer, out RaycastHit hit))
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
