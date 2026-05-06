namespace DreamPark
{
    using System.Collections.Generic;
    using Defective.JSON;
    using UnityEngine;

    public enum PropCategory
    {
        Generic,
        Coin,
        Block,
        Hazard,
        Decoration,
        Custom
    }

    [DisallowMultipleComponent][RequireComponent(typeof(GameArea))]
    public class PropTemplate : MonoBehaviour
    {
        /// <summary>
        /// Static event fired when any PropTemplate changes.
        /// GapFiller subscribes to this to auto-regenerate.
        /// </summary>
        public static event System.Action OnAnyPropTemplateChanged;

        [ReadOnly] public string gameId;
        public PropCategory category = PropCategory.Generic;
        [Tooltip("If enabled, this prop contributes footprint + height data to GapFiller.")]
        public bool affectsGapFiller = true;
        [Tooltip("If enabled, this prop's footprint is carved out as a hole in GapFiller. Leave off for height-only influence.")]
        public bool cutGapFillerHole = false;
        public bool useColliderBounds = true;
        [ShowIf("_isManualFootprint")] public Vector2 customFootprintMeters = new Vector2(1f, 1f);
        public Vector2 footprintOffsetMeters = Vector2.zero;
        public bool showFootprintGizmos = true;
        [HideInInspector] public JSONObject pointData;
        [HideInInspector] public GameObject runtimePlane;
        [SerializeField, HideInInspector] private bool _isManualFootprint;
        [SerializeField, HideInInspector] private float _calibratedYOffset;

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;
        private bool _isSuppressedByAttractionParent;

        public float SurfaceHeight => transform.position.y + _calibratedYOffset;

        public static void NotifyPropTemplateChanged()
        {
            GapFiller.EnsureInstance();
            OnAnyPropTemplateChanged?.Invoke();
        }

        public void NotifyChanged()
        {
            NotifyPropTemplateChanged();
        }

        public void ApplyCalibrationYOffset(float yOffset)
        {
            _calibratedYOffset = yOffset;
            pointData = CompileCalibrationData();
            NotifyChanged();
        }

        public JSONObject CompileCalibrationData()
        {
            var calibration = new JSONObject();
            calibration.AddField("0", _calibratedYOffset.RoundFloat().ToString("F3"));
            return calibration;
        }

        public void ApplyCalibrationData(JSONObject calibrationData)
        {
            if (calibrationData == null || !calibrationData.HasField("0"))
                return;

            pointData = calibrationData;
            _calibratedYOffset = float.Parse(calibrationData.GetField("0").stringValue);
            NotifyChanged();
        }

        private void Awake()
        {
            if (TrySuppressUnderAttraction())
                return;

            EnsureGameId();
            EnsureGameArea();
            CacheTransform();
        }

        private void Start()
        {
            if (_isSuppressedByAttractionParent) return;
            EnsureCalibrator();
            NotifyChanged();
        }

        private void OnEnable()
        {
            if (_isSuppressedByAttractionParent) return;
            NotifyChanged();
        }

        private void OnDisable()
        {
            if (_isSuppressedByAttractionParent) return;
            NotifyChanged();
        }

        private void OnTransformChildrenChanged()
        {
            if (_isSuppressedByAttractionParent) return;
            NotifyChanged();
        }

        private void LateUpdate()
        {
            if (_isSuppressedByAttractionParent) return;
            if (_lastPosition != transform.position || _lastRotation != transform.rotation || _lastScale != transform.localScale)
            {
                CacheTransform();
                NotifyChanged();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            EnsureGameId();
            _isManualFootprint = !useColliderBounds;
        }
#endif

        /// <summary>
        /// PropTemplate and AttractionTemplate both auto-add a GameArea and broadcast
        /// change events to GapFiller. When a Prop sits inside an Attraction the parent
        /// already owns the player-rig zone and the floor regeneration cycle, so the
        /// nested prop's GameArea + change events are redundant — and noisy, since
        /// LateUpdate would fire NotifyChanged every time a moving prop's transform
        /// updates.
        ///
        /// If we detect that situation in Awake, suppress this component: disable the
        /// attached GameArea, disable this PropTemplate, and skip all NotifyChanged
        /// broadcasts. Authors can still place props inside attractions for visual or
        /// gameplay purposes — they just don't double-register with GapFiller.
        /// </summary>
        private bool TrySuppressUnderAttraction()
        {
            var parentAttraction = GetComponentInParent<AttractionTemplate>(true);
            if (parentAttraction == null || parentAttraction.gameObject == gameObject)
                return false;

            _isSuppressedByAttractionParent = true;

            var gameArea = GetComponent<GameArea>();
            if (gameArea != null)
                gameArea.enabled = false;

            enabled = false;
            return true;
        }

        /// <summary>
        /// Every prop participates in player-rig switching via a GameArea. If a prop
        /// doesn't already have one (they aren't authored with one by default), attach
        /// one now and seed it with this prop's gameId + a lower default priority so
        /// that a surrounding LevelTemplate or AttractionTemplate wins when the player
        /// is inside both.
        /// </summary>
        private void EnsureGameArea()
        {
            var gameArea = GetComponent<GameArea>();
            if (gameArea == null)
            {
                gameArea = gameObject.AddComponent<GameArea>();
                // Props lose to containing levels/attractions when the player is inside
                // multiple overlapping zones.
                gameArea.priority = -1;
            }

            if (string.IsNullOrEmpty(gameArea.gameId) && !string.IsNullOrEmpty(gameId))
            {
                gameArea.gameId = gameId;
            }

            // GameArea.Awake already ran ComputeBounds, but it may have fired before
            // we'd finalized gameId/footprint — recompute so the padded bounds match
            // this prop's current footprint.
            gameArea.ComputeBounds();
        }

        private void EnsureGameId()
        {
            if (!string.IsNullOrEmpty(gameId))
                return;

            var gameArea = GetComponent<GameArea>() ?? GetComponentInParent<GameArea>();
            if (gameArea != null && !string.IsNullOrEmpty(gameArea.gameId))
            {
                gameId = gameArea.gameId;
                return;
            }

            var levelTemplate = GetComponentInParent<LevelTemplate>();
            if (levelTemplate != null && !string.IsNullOrEmpty(levelTemplate.gameId))
            {
                gameId = levelTemplate.gameId;
                return;
            }

            string resourceName = gameObject.name.Split('(')[0].Trim();
            int slashIndex = resourceName.IndexOf('/');
            if (slashIndex > 0)
            {
                gameId = resourceName.Substring(0, slashIndex);
            }
        }

        public bool TryGetWorldFootprint(out Vector2[] worldFootprint, out float surfaceHeight)
        {
            surfaceHeight = SurfaceHeight;

            if (useColliderBounds && TryGetColliderFootprint(out worldFootprint))
            {
                return true;
            }

            return TryGetManualFootprint(out worldFootprint);
        }

        public bool TryGetWorldCutoutPolygons(out List<Vector2[]> worldCutouts)
        {
            worldCutouts = null;

            var cutouts = GetComponentsInChildren<FloorCutout>(true);
            if (cutouts == null || cutouts.Length == 0)
                return false;

            var polygons = new List<Vector2[]>();
            for (int i = 0; i < cutouts.Length; i++)
            {
                var cutout = cutouts[i];
                if (cutout == null || cutout.points == null || cutout.points.Count < 3)
                    continue;

                var polygon = new Vector2[cutout.points.Count];
                for (int j = 0; j < cutout.points.Count; j++)
                {
                    Vector3 worldPoint = cutout.transform.TransformPoint(cutout.points[j]);
                    polygon[j] = new Vector2(worldPoint.x, worldPoint.z);
                }

                polygons.Add(polygon);
            }

            if (polygons.Count == 0)
                return false;

            worldCutouts = polygons;
            return true;
        }

        private void EnsureCalibrator()
        {
            if (!Application.isPlaying)
                return;
                
            var calibrator = GetComponent<CalibrateProp>();
            if (calibrator == null)
                calibrator = gameObject.AddComponent<CalibrateProp>();

            calibrator.propTemplate = this;
            if (pointData != null)
                calibrator.pointData = pointData;
        }

        private void CacheTransform()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;
        }

        private bool TryGetManualFootprint(out Vector2[] worldFootprint)
        {
            worldFootprint = null;
            if (customFootprintMeters.x <= 0f || customFootprintMeters.y <= 0f)
                return false;

            Vector3 center = transform.TransformPoint(new Vector3(footprintOffsetMeters.x, 0f, footprintOffsetMeters.y));
            Vector3 right = transform.right * (customFootprintMeters.x * 0.5f);
            Vector3 forward = transform.forward * (customFootprintMeters.y * 0.5f);

            worldFootprint = new[]
            {
                new Vector2(center.x - right.x - forward.x, center.z - right.z - forward.z),
                new Vector2(center.x + right.x - forward.x, center.z + right.z - forward.z),
                new Vector2(center.x + right.x + forward.x, center.z + right.z + forward.z),
                new Vector2(center.x - right.x + forward.x, center.z - right.z + forward.z)
            };
            return true;
        }

        private bool TryGetColliderFootprint(out Vector2[] worldFootprint)
        {
            worldFootprint = null;
            var colliders = GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
                return false;

            // Aggregate all collider bounds in this template's local XZ frame so
            // compound props produce a single oriented footprint.
            float minLocalX = float.MaxValue;
            float maxLocalX = float.MinValue;
            float minLocalZ = float.MaxValue;
            float maxLocalZ = float.MinValue;
            bool hasValidCollider = false;

            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (!collider.enabled)
                    continue;

                Bounds bounds = collider.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;

                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 worldCorner = new Vector3(
                                center.x + extents.x * sx,
                                center.y + extents.y * sy,
                                center.z + extents.z * sz);

                            Vector3 localCorner = transform.InverseTransformPoint(worldCorner);
                            minLocalX = Mathf.Min(minLocalX, localCorner.x);
                            maxLocalX = Mathf.Max(maxLocalX, localCorner.x);
                            minLocalZ = Mathf.Min(minLocalZ, localCorner.z);
                            maxLocalZ = Mathf.Max(maxLocalZ, localCorner.z);
                            hasValidCollider = true;
                        }
                    }
                }
            }

            if (!hasValidCollider)
                return false;

            Vector3 worldA = transform.TransformPoint(new Vector3(minLocalX, 0f, minLocalZ));
            Vector3 worldB = transform.TransformPoint(new Vector3(maxLocalX, 0f, minLocalZ));
            Vector3 worldC = transform.TransformPoint(new Vector3(maxLocalX, 0f, maxLocalZ));
            Vector3 worldD = transform.TransformPoint(new Vector3(minLocalX, 0f, maxLocalZ));
            worldFootprint = new[]
            {
                new Vector2(worldA.x, worldA.z),
                new Vector2(worldB.x, worldB.z),
                new Vector2(worldC.x, worldC.z),
                new Vector2(worldD.x, worldD.z)
            };
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!showFootprintGizmos || !TryGetWorldFootprint(out var footprint, out var surfaceHeight))
                return;

            Gizmos.color = new Color(1f, 0.6f, 0f, 1f);
            for (int i = 0; i < footprint.Length; i++)
            {
                Vector2 a = footprint[i];
                Vector2 b = footprint[(i + 1) % footprint.Length];
                Gizmos.DrawLine(new Vector3(a.x, surfaceHeight, a.y), new Vector3(b.x, surfaceHeight, b.y));
            }
        }
#endif
    }
}
