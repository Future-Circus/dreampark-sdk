namespace DreamPark
{
    using System.Collections.Generic;
    using Defective.JSON;
    using UnityEngine;

    /// <summary>
    /// FROZEN. Serialized by integer value into every prop prefab and read back
    /// into the addressable address — see the comment on PropTemplate.category
    /// for the full reasoning. Do not add, remove, rename or REORDER members:
    /// a reorder re-categorizes every existing prop and therefore renames it.
    /// New categories go in the portal taxonomy, not here.
    /// </summary>
    public enum PropCategory
    {
        Generic,
        Coin,
        Block,
        Hazard,
        Decoration,
        Custom
    }

    /// <summary>
    /// The single footprint edge that must sit against a real wall. Values for
    /// Right and Left retain their original serialized integers so existing
    /// prop prefabs migrate without changing sides.
    /// </summary>
    public enum PropWallSide
    {
        None = 0,
        Front = 3,
        Back = 4,
        Right = 1,
        Left = 2
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
        [ReadOnly] public string resourceName;

        /// <summary>
        /// FROZEN — read by the build pipeline, no longer set by the author.
        ///
        /// This value is baked into the prefab's addressable ADDRESS:
        /// ContentProcessor builds "{gameId}/Props/{category}/{filename}" and
        /// stamps that same string onto GameArea.resourceName and
        /// PropTemplate.resourceName — the key the backend joins on for the
        /// attractions catalog, downloads, views, collected rows and revenue
        /// attribution. So changing a prop's category does not re-tag it: it
        /// RENAMES the asset, and every row of that prop's history stays behind
        /// under the old address. That is a silent data-loss bug wearing an
        /// edit's clothes, which is why the field is greyed out rather than
        /// merely discouraged.
        ///
        /// Categories now live in the developer portal's Attractions tab, and
        /// are assigned per attraction AFTER upload against the server-owned
        /// taxonomy in DreamPark-Web/lib/attractionCategories.js — a flat list
        /// of ~130 slugs with derived groupings, which can grow and be
        /// re-grouped without touching a single prefab. A serialized enum in
        /// the SDK can do none of that: extending it needs an SDK release, and
        /// re-pointing an existing value is the rename above.
        ///
        /// The field is kept, with its six members in their original order,
        /// precisely BECAUSE it is frozen. Existing prefabs keep whatever value
        /// they already serialize, so every existing address stays
        /// byte-identical and there is no migration; new props take the Generic
        /// default and land at "{gameId}/Props/Generic/{name}", stable forever.
        /// Deleting the enum, reordering its members (Unity serializes enums by
        /// integer value, so a reorder silently re-categorizes and therefore
        /// renames every existing prop), or renaming the field would each
        /// orphan shipped content.
        ///
        /// Do not surface this in the inspector, and do not add a category
        /// field to LevelTemplate or AttractionTemplate — an attraction's
        /// category is portal state and always has been.
        /// </summary>
        [ReadOnly]
        [Tooltip("Frozen. Baked into this prefab's addressable address, so changing it would rename the asset and orphan its catalog history. Set an attraction's category in the developer portal's Attractions tab after upload.")]
        public PropCategory category = PropCategory.Generic;
        [Tooltip("If enabled, this prop contributes footprint + height data to GapFiller.")]
        public bool affectsGapFiller = true;
        [Tooltip("If enabled, this prop's footprint is carved out as a hole in GapFiller. Leave off for height-only influence.")]
        public bool cutGapFillerHole = false;
        public bool useColliderBounds = true;
        [ShowIf("_isManualFootprint")] public Vector2 customFootprintMeters = new Vector2(1f, 1f);
        public Vector2 footprintOffsetMeters = Vector2.zero;
        public bool showFootprintGizmos = true;
        [Tooltip("The one footprint edge that needs a real wall behind it. Front/Back are local +/-Z and Right/Left are local +/-X.")]
        public PropWallSide wallSide = PropWallSide.None;
        [Tooltip("Draw the required wall with the same filled blue gizmo used by AttractionTemplate.")]
        public bool showWallGizmo = true;
        [HideInInspector] public JSONObject pointData;
        [HideInInspector] public GameObject runtimePlane;
        [SerializeField, HideInInspector] private bool _isManualFootprint;
        [SerializeField, HideInInspector] private float _calibratedYOffset;

        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;
        private bool _isSuppressedByTemplateParent;

        public float SurfaceHeight => transform.position.y + _calibratedYOffset;

        /// <summary>
        /// The wall height this prop needs, in meters: the 10 ft default, or
        /// taller if the prop's own content reaches higher — never shorter.
        /// Delegates to WallHeightMeasurement (collider-shape based, safe on a
        /// disk-loaded prefab asset) rather than Renderer.bounds — see that
        /// class's docblock, which is this exact component's own
        /// FootprintMeters reasoning applied to Y instead of X/Z. Floor
        /// reference is SurfaceHeight, not transform.position.y, so a
        /// calibrated prop measures from its real floor.
        /// </summary>
        public float GetWallHeightMeters() => WallHeightMeasurement.GetWallHeightMeters(transform, SurfaceHeight);

        /// <summary>
        /// This prop's single wall side as an axis token, or "" if none is
        /// selected. Always emitted on every dimensions row (never
        /// omitted) per the backend contract: undefined means "don't touch
        /// the stored value", "" is the explicit clear a re-authored prop
        /// needs to actually turn a wall off.
        /// </summary>
        public string WallsWireValue
        {
            get
            {
                switch (wallSide)
                {
                    case PropWallSide.Front: return "+z";
                    case PropWallSide.Back: return "-z";
                    case PropWallSide.Right: return "+x";
                    case PropWallSide.Left: return "-x";
                    default: return "";
                }
            }
        }

        // Compatibility alias retained for callers introduced while both
        // templates briefly shared the plural wire-value name.
        public string PublishedWallSideToken => WallsWireValue;

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
            if (TrySuppressUnderTemplate())
                return;

            EnsureGameId();
            EnsureGameArea();
            CacheTransform();
        }

        private void Start()
        {
            if (_isSuppressedByTemplateParent) return;
            EnsureCalibrator();
            NotifyChanged();
        }

        private void OnEnable()
        {
            if (_isSuppressedByTemplateParent) return;
            NotifyChanged();
        }

        private void OnDisable()
        {
            if (_isSuppressedByTemplateParent) return;
            NotifyChanged();
        }

        private void OnTransformChildrenChanged()
        {
            if (_isSuppressedByTemplateParent) return;
            NotifyChanged();
        }

        private void LateUpdate()
        {
            if (_isSuppressedByTemplateParent) return;
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
        /// True when this prop is baked inside a LevelTemplate rather than placed on its
        /// own. A nested prop is authored content that the level owns, so the SDK treats
        /// it as an ordinary child object: its PropTemplate and GameArea are suppressed
        /// in Awake, and OptimizedAF registers its root as a LevelObject instead of
        /// recursing past it.
        ///
        /// The test is LevelTemplate, not AttractionTemplate. AttractionTemplate is an
        /// empty subclass - a naming convention - and LevelTemplate carries the
        /// RequireComponent(GameArea) and the GapFiller broadcast that make a nested
        /// prop redundant in the first place, so both cases are the same case. The walk
        /// starts at transform.parent so an object carrying both components does not
        /// match itself.
        ///
        /// Computed from the hierarchy rather than from _isSuppressedByTemplateParent,
        /// so callers can ask before this component's Awake has run - registration during
        /// a spawn can beat Awake.
        /// </summary>
        public bool IsNestedUnderTemplate =>
            transform.parent != null
            && transform.parent.GetComponentInParent<LevelTemplate>(true) != null;

        /// <summary>
        /// PropTemplate and LevelTemplate both auto-add a GameArea and broadcast
        /// change events to GapFiller. When a Prop sits inside a Level or Attraction the
        /// parent already owns the player-rig zone and the floor regeneration cycle, so
        /// the nested prop's GameArea + change events are redundant — and noisy, since
        /// LateUpdate would fire NotifyChanged every time a moving prop's transform
        /// updates.
        ///
        /// If we detect that situation in Awake, suppress this component: disable the
        /// attached GameArea, disable this PropTemplate, and skip all NotifyChanged
        /// broadcasts. Authors can still place props inside levels and attractions for
        /// visual or gameplay purposes — they just don't double-register with GapFiller.
        /// </summary>
        private bool TrySuppressUnderTemplate()
        {
            if (!IsNestedUnderTemplate)
                return false;

            _isSuppressedByTemplateParent = true;

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

            if (string.IsNullOrEmpty(gameArea.resourceName) && !string.IsNullOrEmpty(resourceName))
            {
                gameArea.resourceName = resourceName;
            }

            // A prop nested inside a LevelTemplate/AttractionTemplate can never win the
            // active zone — its priority is -1, so the containing level always takes it.
            // That makes the prop's per-frame GameArea.Update pure overhead, and there is
            // one GameArea per prop. Disable the redundant zone so it stops ticking (its
            // OnDisable also removes it from GameArea.allGameAreas). The component is left
            // attached to satisfy [RequireComponent] and keep serialized data intact.
            if (IsNestedInContainerZone())
            {
                gameArea.enabled = false;
                return;
            }

            // Standalone prop: its GameArea is a real activation zone, so keep it live.
            // GameArea.Awake already ran ComputeBounds, but it may have fired before we'd
            // finalized gameId/footprint — recompute so the padded bounds match this prop.
            gameArea.ComputeBounds();
        }

        /// <summary>
        /// Kept as a named call site for readability inside EnsureGameArea. The test now
        /// lives in one place: IsNestedUnderTemplate.
        /// </summary>
        private bool IsNestedInContainerZone() => IsNestedUnderTemplate;

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

        /// <summary>
        /// The footprint published for a prop the SDK cannot measure, in METERS.
        /// Mirrored server-side (DreamPark-Web lib/poiScale.js PROP_FALLBACK_M) so an
        /// unmeasured prop draws at the same size whether its catalog row was never
        /// published or was published from geometry we could not read. It is also the
        /// serialized default of customFootprintMeters below, which is what makes the
        /// two agree by construction rather than by someone remembering to.
        /// </summary>
        public const float DefaultFootprintMeters = 1f;

        /// <summary>
        /// The prop's footprint in METERS (x = width, y = length), measured in its own
        /// local frame — the publishable twin of LevelTemplate.DimensionsInFeet. Read by
        /// the Content Uploader and pushed to the attractions catalog
        /// (POST /api/content/{id}/attractions/dimensions, converted to feet on the
        /// wire), where it drives RELATIVE marker scale on the 2D park maps.
        ///
        /// NOT AN OPERATOR-FACING MEASUREMENT, and that distinction is the whole reason
        /// this can exist. The July 2026 call was that props stay out of the dimensions
        /// UI because a bounds-derived number misleads an operator reading "this is
        /// 2 x 3 ft" — an attraction's GameLevelSize is authored, a prop's extent is
        /// inferred, and printing them in the same typeface claims a confidence the
        /// second one has not earned. That call still stands: nothing here is rendered
        /// as a measurement, and the server refuses to stamp a size-reference tag on a
        /// prop row. Relative scale asks a strictly weaker question — is this bigger
        /// than that — and an inferred extent answers it honestly.
        ///
        /// SCENE-INDEPENDENT BY CONSTRUCTION. Collider.bounds is a world-space AABB and
        /// only means anything for an INSTANTIATED object, while the uploader reads
        /// prefab assets straight off disk via AssetDatabase.LoadAssetAtPath and never
        /// instantiates them. Reading .bounds there returns zero, which would quietly
        /// publish every prop in the project at the fallback size and look like the
        /// feature working. So this walks collider SHAPE data through matrix math
        /// instead, which is serialized asset state and valid with no scene at all.
        /// </summary>
        public Vector2 FootprintMeters
        {
            get
            {
                if (useColliderBounds && TryMeasureLocalColliderFootprint(out Vector2 measured))
                    return SanitizeFootprint(measured);

                // Manual mode, or nothing measurable — the serialized value, whose
                // default is DefaultFootprintMeters square.
                return SanitizeFootprint(customFootprintMeters);
            }
        }

        /// <summary>
        /// Guarantees a positive, finite footprint. A zero, negative or NaN axis is not
        /// a small prop, it is an unusable answer, and shipping it would divide through
        /// the scale curve on the server.
        /// </summary>
        private static Vector2 SanitizeFootprint(Vector2 footprint)
        {
            float x = (float.IsNaN(footprint.x) || float.IsInfinity(footprint.x) || footprint.x <= 0f)
                ? DefaultFootprintMeters : footprint.x;
            float y = (float.IsNaN(footprint.y) || float.IsInfinity(footprint.y) || footprint.y <= 0f)
                ? DefaultFootprintMeters : footprint.y;
            return new Vector2(x, y);
        }

        /// <summary>
        /// Axis-aligned extent of every enabled collider, expressed in this prop's
        /// ORIENTED frame — the root's position and rotation removed, its SCALE kept.
        /// Scale is deliberately retained: a prop authored at 1 m and shipped on a root
        /// scaled to 3 occupies three metres of somebody's living room, and the map is
        /// drawing the room.
        ///
        /// (The gizmo path above uses InverseTransformPoint, which divides root scale
        /// out — correct there, because it transforms straight back out again and the
        /// round trip cancels. This one does not round-trip, so it must not.)
        /// </summary>
        private bool TryMeasureLocalColliderFootprint(out Vector2 footprintMeters)
        {
            footprintMeters = Vector2.zero;

            var colliders = GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.Length == 0)
                return false;

            Matrix4x4 worldToProp = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one).inverse;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            bool measured = false;

            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider == null || !collider.enabled)
                    continue;
                if (!TryGetLocalShapeBounds(collider, out Bounds shape))
                    continue;

                Matrix4x4 shapeToProp = worldToProp * collider.transform.localToWorldMatrix;
                Vector3 c = shape.center;
                Vector3 e = shape.extents;

                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 corner = shapeToProp.MultiplyPoint3x4(
                                new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));
                            if (float.IsNaN(corner.x) || float.IsNaN(corner.z))
                                continue;
                            minX = Mathf.Min(minX, corner.x);
                            maxX = Mathf.Max(maxX, corner.x);
                            minZ = Mathf.Min(minZ, corner.z);
                            maxZ = Mathf.Max(maxZ, corner.z);
                            measured = true;
                        }
            }

            if (!measured)
                return false;

            footprintMeters = new Vector2(maxX - minX, maxZ - minZ);
            return footprintMeters.x > 0f && footprintMeters.y > 0f;
        }

        /// <summary>
        /// A collider's shape in its OWN local space, from serialized fields only.
        /// Every case here is asset data that survives with no scene loaded; a collider
        /// type not listed is skipped rather than guessed at, and a prop made entirely
        /// of skipped colliders falls back to customFootprintMeters.
        /// </summary>
        // internal rather than private: WallHeightMeasurement (shared by
        // LevelTemplate's wall gizmo) reuses this exact shape-reading logic
        // rather than duplicating the Box/Sphere/Capsule/Mesh switch.
        internal static bool TryGetLocalShapeBounds(Collider collider, out Bounds bounds)
        {
            bounds = default;

            if (collider is BoxCollider box)
            {
                bounds = new Bounds(box.center, box.size);
                return true;
            }
            if (collider is SphereCollider sphere)
            {
                bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                return true;
            }
            if (collider is CapsuleCollider capsule)
            {
                float diameter = capsule.radius * 2f;
                float height = Mathf.Max(capsule.height, diameter);
                Vector3 size = capsule.direction == 0 ? new Vector3(height, diameter, diameter)
                             : capsule.direction == 1 ? new Vector3(diameter, height, diameter)
                             : new Vector3(diameter, diameter, height);
                bounds = new Bounds(capsule.center, size);
                return true;
            }
            if (collider is MeshCollider mesh)
            {
                if (mesh.sharedMesh == null)
                    return false;
                bounds = mesh.sharedMesh.bounds;
                return true;
            }

            return false;
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
        // Wall previews match LevelTemplate: once a wall is declared it stays
        // visible, even when the prop is not selected. The expensive footprint
        // walk remains gated to the uncommon wall-authored prop.
        private void OnDrawGizmos()
        {
            if (!showWallGizmo || wallSide == PropWallSide.None)
                return;
            if (!TryGetWorldFootprint(out var footprint, out var surfaceHeight))
                return;
            DrawWallGizmos(footprint, surfaceHeight);
        }

        // The orange footprint is selection-only: unlike the wall preview it
        // is useful only while sizing a prop, and drawing it for every prop
        // would walk every collider hierarchy on every editor frame.
        private void OnDrawGizmosSelected()
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

        /// <summary>
        /// footprint[] is ordered (-x,-z),(+x,-z),(+x,+z),(-x,+z) in the prop's
        /// own oriented frame — see TryGetManualFootprint/TryGetColliderFootprint,
        /// which both build it in that winding. Each selected edge is drawn from
        /// those world-space corners, so collider-derived offsets and root scale
        /// stay identical to the orange footprint gizmo.
        /// </summary>
        private void DrawWallGizmos(Vector2[] footprint, float surfaceHeight)
        {
            float wallHeight = GetWallHeightMeters();
            switch (wallSide)
            {
                case PropWallSide.Back:
                    DrawWallEdge(footprint, 0, 1, surfaceHeight, wallHeight);
                    break;
                case PropWallSide.Right:
                    DrawWallEdge(footprint, 1, 2, surfaceHeight, wallHeight);
                    break;
                case PropWallSide.Front:
                    DrawWallEdge(footprint, 2, 3, surfaceHeight, wallHeight);
                    break;
                case PropWallSide.Left:
                    DrawWallEdge(footprint, 3, 0, surfaceHeight, wallHeight);
                    break;
            }
        }

        private static void DrawWallEdge(Vector2[] footprint, int a, int b, float surfaceHeight, float wallHeight)
        {
            Vector3 baseA = new Vector3(footprint[a].x, surfaceHeight, footprint[a].y);
            Vector3 baseB = new Vector3(footprint[b].x, surfaceHeight, footprint[b].y);
            Vector3 edge = baseB - baseA;
            if (edge.sqrMagnitude <= Mathf.Epsilon) return;

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Quaternion rotation = Quaternion.LookRotation(Vector3.Cross(edge.normalized, Vector3.up), Vector3.up);
            Gizmos.matrix = Matrix4x4.TRS((baseA + baseB) * 0.5f + Vector3.up * (wallHeight * 0.5f), rotation, Vector3.one);
            Vector3 size = new Vector3(edge.magnitude, wallHeight, 0.05f);
            Gizmos.color = new Color(0.1f, 0.6f, 1f, 0.15f);
            Gizmos.DrawCube(Vector3.zero, size);
            Gizmos.color = new Color(0.1f, 0.6f, 1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, size);
            Gizmos.matrix = oldMatrix;
        }
#endif
    }
}
