namespace DreamPark {
using Unity.AI.Navigation;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.AI;
using Defective.JSON;

#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(LevelTemplate), true)]
public class LevelTemplateEditor : Editor {
    public override void OnInspectorGUI() {
        base.OnInspectorGUI();
        // Human-readable footprint + size-reference tag (the same ladder the
        // uploader publishes to the platform — see AttractionSizeReference).
        var template = target as LevelTemplate;
        if (template != null) {
            Vector2 feet = template.DimensionsInFeet;
            EditorGUILayout.HelpBox(AttractionSizeReference.Describe(feet.x, feet.y), MessageType.None);
        }
    }
}

#endif
    public enum GameLevelSize {
        Micro,
        Boutique,
        Small,
        Square,
        Medium,
        Large,
        Jumbo,
        MallCorridor,
        Custom
    }

    /// <summary>
    /// Footprint edges that must sit against a real wall, in the template's
    /// local frame. Flags make Unity render this as one multi-select dropdown
    /// and allow both attractions and props to describe corners, alcoves, or
    /// pass-throughs with the same authoring model.
    /// </summary>
    [System.Flags]
    public enum WallSide
    {
        None = 0,
        Front = 1 << 0,
        Back = 1 << 1,
        Right = 1 << 2,
        Left = 1 << 3
    }

    public static class GameLevelDimensions
    {
        public static Vector2 GetDimensions(GameLevelSize size)
        {
            switch (size)
            {
                case GameLevelSize.Micro:
                    return new Vector2(14f, 16f);
                case GameLevelSize.Boutique:
                    return new Vector2(16f, 30f);
                case GameLevelSize.Small:
                    return new Vector2(30f, 64f);
                case GameLevelSize.Square:
                    return new Vector2(40f, 50f);
                case GameLevelSize.Medium:
                    return new Vector2(50f, 94f);
                case GameLevelSize.Large:
                    return new Vector2(80f, 128f);
                case GameLevelSize.Jumbo:
                    return new Vector2(120f, 150f);
                case GameLevelSize.MallCorridor:
                    return new Vector2(30f, 260f);
                default:
                    return Vector2.zero;
            }
        }

        public static Vector2 GetDimensionsInMeters(GameLevelSize size)
        {
            return GetDimensions(size) * 0.3048f;
        }
        public static Vector2 GetDimensionsInMeters(Vector2 size)
        {
            return size * 0.3048f;
        }
    }

    [RequireComponent(typeof(GameArea))]
    [RequireComponent(typeof(MusicArea))]
    public class LevelTemplate : MonoBehaviour {
        /// <summary>
        /// Static event fired when any LevelTemplate changes (spawned, moved, floor regenerated).
        /// GapFiller subscribes to this to auto-regenerate.
        /// </summary>
        public static event System.Action OnAnyLevelTemplateChanged;

        /// <summary>
        /// Call this to notify listeners that a level template has changed.
        /// </summary>
        public static void NotifyLevelTemplateChanged()
        {
            // Ensure GapFiller exists before notifying
            GapFiller.EnsureInstance();
            OnAnyLevelTemplateChanged?.Invoke();
        }

        [ReadOnly] public string gameId;
        public GameLevelSize size;
        [ShowIf("_isCustom")] public Vector2 customSize = new Vector2(10f, 10f);
        public Vector2 defaultAnchorPosition;
        public bool generateFloor = true;
        [Tooltip("LEGACY. The rig's DepthMaskCeiling plane replaces per-attraction ceilings, " +
                 "and DepthMaskCeiling.AllowTemplateCeilings gates this whatever it is set to.")]
        public bool generateCeiling = true;

        /// <summary>
        /// Extra metres of depth-mask ceiling on every side, beyond the attraction's own
        /// footprint. The mask only helps where it covers, and authored content routinely
        /// leans, swings or spills past the footprint it was sized against, so the ceiling
        /// is deliberately larger than the attraction.
        ///
        /// A CONSTANT RATHER THAN A SERIALIZED FIELD, and that is the whole point. The path
        /// it feeds is gated off by DepthMaskCeiling.AllowTemplateCeilings, so no author can
        /// reach this number — but a public field is still written into every attraction
        /// prefab the moment ContentProcessor re-saves one, which re-bundles the entire
        /// catalog to carry a value nothing reads. Tune here if the legacy path is revived.
        /// </summary>
        private const float CeilingPadding = 3f;

        /// <summary>
        /// Height of the depth-mask ceiling above the attraction origin. LOWER IS STRONGER:
        /// it lowers the distance at which real geometry stops occluding. Content height is
        /// irrelevant — the mask edits the real-world depth map, not the content.
        /// A constant for the same reason as CeilingPadding above.
        /// </summary>
        private const float CeilingHeight = 2.4f;

        [HideInInspector] public GameObject runtimePlane;
        [HideInInspector] public GameObject runtimeCeiling;
        [SerializeField, HideInInspector]
        private bool _isCustom;
        public bool renderDimensions = true;
        public bool showCutoutGizmos = true;
        [Tooltip("Show a faint grid overlay in the Scene view at the spacing implied by gridDensity. " +
                 "Helps visualize floor mesh subdivision without any runtime cost.")]
        public bool showGridGizmo = true;
        public int gridDensity = 10;
        [HideInInspector] public float gridWidth;
        [HideInInspector] public float gridHeight;
        [HideInInspector] public int gridX;
        [HideInInspector] public int gridY;
        [HideInInspector] public JSONObject floorData;
        public Material floorMaterial;

        [Tooltip("Which footprint edges need a real wall behind them. Select any combination: Front/Back are local +/-Z and Right/Left are local +/-X.")]
        public WallSide walls = WallSide.None;
        [Tooltip("Draw the required wall(s) as a gizmo plane, 10ft tall by default and taller if this attraction's own content reaches higher.")]
        public bool showWallGizmos = true;

        // The four booleans shipped before walls became a flags dropdown. Keep
        // their serialized values long enough to migrate existing prefabs, but
        // hide them so there is only one wall control in the inspector.
        [SerializeField, HideInInspector, UnityEngine.Serialization.FormerlySerializedAs("wallFront")]
        private bool _legacyWallFront;
        [SerializeField, HideInInspector, UnityEngine.Serialization.FormerlySerializedAs("wallBack")]
        private bool _legacyWallBack;
        [SerializeField, HideInInspector, UnityEngine.Serialization.FormerlySerializedAs("wallRight")]
        private bool _legacyWallRight;
        [SerializeField, HideInInspector, UnityEngine.Serialization.FormerlySerializedAs("wallLeft")]
        private bool _legacyWallLeft;
        [SerializeField, HideInInspector] private bool _wallSidesMigrated;

        // Source compatibility for scripts and generated bindings that used
        // the old booleans. New authoring code should use walls.
        public bool wallFront { get => HasWall(WallSide.Front); set => SetWall(WallSide.Front, value); }
        public bool wallBack { get => HasWall(WallSide.Back); set => SetWall(WallSide.Back, value); }
        public bool wallRight { get => HasWall(WallSide.Right); set => SetWall(WallSide.Right, value); }
        public bool wallLeft { get => HasWall(WallSide.Left); set => SetWall(WallSide.Left, value); }

        /// <summary>
        /// Wire format for the dimensions upload's "walls" field: comma-joined
        /// axis tokens in this attraction's own local frame ("+z,-x"), empty
        /// when no side is toggled. Any combination is valid — two adjacent
        /// sides describe a corner, two opposite sides describe a through-wall
        /// (e.g. a portal). Kept here rather than in the uploader so the token
        /// spelling has exactly one source.
        ///
        /// Always read and uploaded — never omitted from the row — because the
        /// backend treats an ABSENT "walls" field as "don't touch the stored
        /// value" and "" as the explicit clear. Untoggling every side has to
        /// still publish "" or the old value would stick forever.
        /// </summary>
        public string WallsWireValue
        {
            get
            {
                var sides = new List<string>(4);
                WallSide selected = EffectiveWalls;
                if ((selected & WallSide.Front) != 0) sides.Add("+z");
                if ((selected & WallSide.Back) != 0) sides.Add("-z");
                if ((selected & WallSide.Right) != 0) sides.Add("+x");
                if ((selected & WallSide.Left) != 0) sides.Add("-x");
                return string.Join(",", sides);
            }
        }

        private WallSide EffectiveWalls => _wallSidesMigrated ? walls : walls | LegacyWalls;
        private WallSide LegacyWalls =>
            (_legacyWallFront ? WallSide.Front : WallSide.None) |
            (_legacyWallBack ? WallSide.Back : WallSide.None) |
            (_legacyWallRight ? WallSide.Right : WallSide.None) |
            (_legacyWallLeft ? WallSide.Left : WallSide.None);
        private bool HasAnyWall => EffectiveWalls != WallSide.None;

        private bool HasWall(WallSide side) => (EffectiveWalls & side) != 0;

        private void SetWall(WallSide side, bool enabled)
        {
            MigrateLegacyWalls();
            if (enabled) walls |= side;
            else walls &= ~side;
        }

        private void MigrateLegacyWalls()
        {
            if (_wallSidesMigrated) return;
            walls |= LegacyWalls;
            _wallSidesMigrated = true;
        }

        #if UNITY_EDITOR
        public void OnValidate()
        {
            MigrateLegacyWalls();
            _isCustom = size == GameLevelSize.Custom;
            if (floorMaterial == null) {
                floorMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/DreamPark/Materials/Occlusion.mat");
            }
        }
        #endif
        void Start()
        {
            if (generateFloor) GenerateFloorWithHoles();
            // The unified overhead plane on the rig replaces this. Off unless the legacy
            // master switch is deliberately flipped — see DepthMaskCeiling.
            if (generateCeiling && DepthMaskCeiling.AllowTemplateCeilings) GenerateDepthCeiling();
            SetFloorVisibilityForMode(isBuildMode);
            NotifyLevelTemplateChanged();
        }

        void OnEnable()
        {
            // Notify when a LevelTemplate is enabled/spawned
            NotifyLevelTemplateChanged();
        }

        void OnDisable()
        {
            // Notify when a LevelTemplate is disabled/removed
            NotifyLevelTemplateChanged();
        } 

        /// <summary>
        /// Rebuild the attraction's depth-mask ceiling. Public so a tool or a script that
        /// resizes or re-fills an attraction at runtime can re-fit the mask to it.
        /// </summary>
        public void RegenerateCeiling() {
            if (generateCeiling && DepthMaskCeiling.AllowTemplateCeilings) GenerateDepthCeiling();
        }

        /// <summary>
        /// The horizontal quad handed to Meta's environment-depth mask, which switches
        /// OFF depth occlusion over the attraction so its content is not eaten by the
        /// depth of the real room behind it. See DepthCeiling for what the object is.
        ///
        /// Sized to the attraction footprint PLUS CeilingPadding on every side, because
        /// the mask only does anything where it actually covers and authored content
        /// routinely leans or spills past the footprint it was sized against.
        ///
        /// LEGACY PATH. The single head-anchored plane on the rig replaces this; see
        /// DepthMaskCeiling for why a patchwork of small ceilings is the wrong shape for
        /// the flicker this trick exists to kill. Runs only when that plane is absent.
        /// </summary>
        private void GenerateDepthCeiling() {
            // Feeds a runtime manager, and Destroy() is illegal in edit mode — the public
            // RegenerateCeiling entry point makes that reachable from tooling.
            if (!Application.isPlaying) return;

            if (runtimeCeiling != null) Destroy(runtimeCeiling);
            runtimeCeiling = null;

            Vector2 dims = RuntimeFootprintMeters;
            float pad = Mathf.Max(0f, CeilingPadding);
            float width = dims.x + pad * 2f;
            float height = dims.y + pad * 2f;

            runtimeCeiling = DepthCeiling.Create(transform, "LevelCeiling", width, height, new Vector3(0f, CeilingHeight, 0f));
        }

        /// <summary>
        /// Public method to regenerate the floor mesh and notify listeners.
        ///
        /// RESPECTS generateFloor, matching RegenerateCeiling above, which has always
        /// checked generateCeiling. This method did not check, and the asymmetry was
        /// invisible for as long as every template generated a floor.
        ///
        /// The Dream Sequence format makes FLOORLESS templates normal: child levels
        /// play on the DreamTemplate's single shared floor and set generateFloor =
        /// false, so an unguarded rebuild would hand a level a second floor —
        /// stacking a collider and a NavMeshSurface on the parent's, at the same
        /// height, on every level change.
        ///
        /// The only thing preventing that before was a guard written in LUA, at
        /// Assets/Content/Sample/Scripts/dreamsequence-controller.lua.txt:153, which
        /// no C# caller inherits. Guarding here means the invariant holds for every
        /// caller instead of for the one that remembered.
        ///
        /// No reachable behaviour changes: the two existing callers are that guarded
        /// Lua path, and CalibrateLevel.Clear() (CalibrateLevel.cs:872) — and
        /// CalibrateLevel only ever exists ON runtimePlane, which a template with
        /// generateFloor = false never creates, so that path cannot reach a floorless
        /// template at all.
        /// </summary>
        public void RegenerateFloor()
        {
            if (!generateFloor) return;
            GenerateFloorWithHoles();
            SetFloorVisibilityForMode(isBuildMode);
            NotifyLevelTemplateChanged();
        }

        /// <summary>
        /// Build mode can create floor occlusion artifacts on iOS.
        /// Keep floor alive and swap its layer in build mode; restore in play mode.
        /// </summary>
        public void SetFloorVisibilityForMode(bool isBuildMode)
        {
            if (runtimePlane == null)
            {
                // Warn only when a floor was EXPECTED. This message is about a template
                // that wants a floor and has not got one — the case where skipping
                // preserves calibration data. A template with generateFloor = false has
                // no runtimePlane by design, and Start() calls this unconditionally
                // (:198), so without this test every deliberately floorless template
                // logs a warning on spawn that reads exactly like a defect. Under the
                // Dream Sequence format that is once per streamed level, on device.
                if (generateFloor)
                {
                    Debug.LogWarning($"[LevelTemplate] SetFloorVisibilityForMode: runtimePlane is null on {gameObject.name}, skipping to preserve calibration data");
                }
                return;
            }

            // Use Water layer to hide floor from camera in Build Mode.
            // ARMesh layer is reserved for AR mesh raycast targets — putting the
            // floor on ARMesh caused calibration to hit itself instead of the real mesh.
            int buildLayer = LayerMask.NameToLayer("Water");
            int playLayer = LayerMask.NameToLayer("Level");
            int targetLayer = isBuildMode ? buildLayer : playLayer;

            // Fall back safely if layers are missing from TagManager.
            if (targetLayer < 0)
            {
                targetLayer = isBuildMode ? playLayer : runtimePlane.layer;
            }

            if (targetLayer >= 0 && runtimePlane.layer != targetLayer)
            {
                runtimePlane.layer = targetLayer;
            }

            if (!runtimePlane.activeSelf)
            {
                runtimePlane.SetActive(true);
            }
        }

        private void GenerateFloorWithHoles()
{
    if (runtimePlane != null) Destroy(runtimePlane);

    // Dimensions
    Vector2 dims = RuntimeFootprintMeters;

    float width  = dims.x;
    float height = dims.y;

    // Runtime plane
    runtimePlane = new GameObject("LevelFloor");
    runtimePlane.layer = LayerMask.NameToLayer("Level");
    runtimePlane.tag = "Ground";
    runtimePlane.transform.SetParent(transform, false);
    runtimePlane.transform.localPosition = Vector3.zero;
    runtimePlane.transform.localRotation = Quaternion.identity;
    runtimePlane.transform.localScale = Vector3.one;
    runtimePlane.AddComponent<OptimizedAFIgnore>();

    MeshFilter   mf = runtimePlane.AddComponent<MeshFilter>();
    MeshCollider mc = runtimePlane.AddComponent<MeshCollider>();
    MeshRenderer mr = runtimePlane.AddComponent<MeshRenderer>();
    mr.material = floorMaterial ? floorMaterial : Resources.Load<Material>("Materials/Occlusion");
    // && DREAMPARKCORE added porting this from dreampark-core (Grid,
    // feat/grid-portal-yaw @ 2e1e940e): PlaceHasScanForFloorHiding() below
    // calls DreamPark.EnvironmentDust.EnvironmentDustManager, which lives
    // under core's Assets/Scripts/ (not Assets/DreamPark/) and does not
    // exist in this SDK repo. Core's own commit used bare UNITY_IOS, which
    // compiles fine there but would fail here — same reason isBuildMode's
    // NativeInterfaceManager reference below is UNITY_IOS && DREAMPARKCORE
    // instead of UNITY_IOS alone. Do not drop DREAMPARKCORE re-syncing this.
#if UNITY_IOS && DREAMPARKCORE
    if (PlaceHasScanForFloorHiding()) {
        mr.enabled = false;
        Debug.Log("[LevelTemplate] Place has a scan — attraction floor renderer hidden so the scan renders (collider kept)");
    }
#endif

    // Grid setup (same logic as before)
    gridWidth  = width;
    gridHeight = height;
    gridX = Mathf.Max(1, Mathf.RoundToInt(gridDensity * (width  / Mathf.Min(width, height))));
    gridY = Mathf.Max(1, Mathf.RoundToInt(gridDensity * (height / Mathf.Min(width, height))));
    int vertCountX = gridX + 1;
    int vertCountY = gridY + 1;

    Vector3[] vertices = new Vector3[vertCountX * vertCountY];
    Vector2[] uv       = new Vector2[vertices.Length];

    // Build grid vertices
    for (int y = 0; y < vertCountY; y++)
    {
        for (int x = 0; x < vertCountX; x++)
        {
            int i = y * vertCountX + x;
            float px = Mathf.Lerp(-width  / 2f, width  / 2f, (float)x / gridX);
            float pz = Mathf.Lerp(-height / 2f, height / 2f, (float)y / gridY);
            vertices[i] = new Vector3(px, 0f, pz);
            uv[i] = new Vector2((float)x / gridX, (float)y / gridY);
        }
    }

    // Gather holes in level-local space
    List<List<Vector2>> holes = new List<List<Vector2>>();
    foreach (var pit in GetComponentsInChildren<FloorCutout>())
    {
        if (pit.points == null || pit.points.Count < 3) continue;

        List<Vector2> hole = new List<Vector2>();
        foreach (var p in pit.points)
        {
            Vector3 worldP      = pit.transform.TransformPoint(p);
            Vector3 localToLevel = transform.InverseTransformPoint(worldP);
            hole.Add(new Vector2(localToLevel.x, localToLevel.z));
        }
        if (hole.Count >= 3) holes.Add(hole);
    }

    // If no holes, just build a plain grid and bail
    if (holes.Count == 0)
    {
        List<int> fullTris = new List<int>();
        for (int y = 0; y < gridY; y++)
        {
            for (int x = 0; x < gridX; x++)
            {
                int i0 = y * vertCountX + x;
                int i1 = i0 + 1;
                int i2 = (y + 1) * vertCountX + x;
                int i3 = i2 + 1;

                fullTris.Add(i0); fullTris.Add(i2); fullTris.Add(i1);
                fullTris.Add(i1); fullTris.Add(i2); fullTris.Add(i3);
            }
        }

        Mesh fullMesh = new Mesh();
        fullMesh.vertices = vertices;
        fullMesh.triangles = fullTris.ToArray();
        fullMesh.uv = uv;
        fullMesh.RecalculateNormals();
        fullMesh.RecalculateBounds();
        fullMesh.MarkDynamic();

        mf.sharedMesh = fullMesh;
        mc.sharedMesh = fullMesh;

        BuildNavSurfaceAndAnchors(vertices, uv, gridX, gridY, null);
        return;
    }

    // ---- local helpers ----
    bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                (p.x < (poly[j].x - poly[i].x) *
                       (p.y - poly[i].y) /
                       (poly[j].y - poly[i].y) + poly[i].x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    Vector2 PolygonCentroid(List<Vector2> poly)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < poly.Count; i++)
            sum += poly[i];
        return sum / poly.Count;
    }

    // ------- NEW BIT: push any vertex that is inside a hole OUTSIDE -------
    float cellSize = Mathf.Min(width / gridX, height / gridY);
    float step = cellSize * 0.25f;       // how far we push per iteration
    const int maxPushIters = 20;         // safety

    // precompute centroids once
    var centroids = new List<Vector2>(holes.Count);
    foreach (var h in holes) centroids.Add(PolygonCentroid(h));

    for (int vi = 0; vi < vertices.Length; vi++)
    {
        Vector2 v2 = new Vector2(vertices[vi].x, vertices[vi].z);

        for (int h = 0; h < holes.Count; h++)
        {
            var hole = holes[h];
            if (!PointInPolygon(v2, hole))
                continue;

            // This vertex is inside this hole: push it outward until it's not.
            Vector2 center = centroids[h];

            int iter = 0;
            while (PointInPolygon(v2, hole) && iter < maxPushIters)
            {
                Vector2 dir = v2 - center;
                if (dir.sqrMagnitude < 1e-8f)
                    dir = Vector2.right;     // arbitrary if exactly at center

                dir.Normalize();
                v2 += dir * step;
                iter++;
            }

            vertices[vi].x = v2.x;
            vertices[vi].z = v2.y;

            // Once we've pushed it out of this hole, we stop checking others.
            break;
        }
    }
    // ------------------ end "never inside a hole" pass ------------------



    // Generate triangles & cut holes (same idea as your original, center test)
    List<int> triangles = new List<int>();

        void TryAddTriangle(int a, int b, int c)
{
    Vector2 A = new Vector2(vertices[a].x, vertices[a].z);
    Vector2 B = new Vector2(vertices[b].x, vertices[b].z);
    Vector2 C = new Vector2(vertices[c].x, vertices[c].z);

    foreach (var hole in holes)
    {
        // If ANY vertex inside → skip
        if (PointInPolygon(A, hole) ||
            PointInPolygon(B, hole) ||
            PointInPolygon(C, hole))
            return;

        // If ANY edge intersects polygon boundary → skip
        if (SegmentIntersectsPolygon(A, B, hole)) return;
        if (SegmentIntersectsPolygon(B, C, hole)) return;
        if (SegmentIntersectsPolygon(C, A, hole)) return;
    }

    triangles.Add(a);
    triangles.Add(b);
    triangles.Add(c);
}

   for (int y = 0; y < gridY; y++)
{
    for (int x = 0; x < gridX; x++)
    {
        int i0 = y * vertCountX + x;
        int i1 = i0 + 1;
        int i2 = (y + 1) * vertCountX + x;
        int i3 = i2 + 1;

        TryAddTriangle(i0, i2, i1);
        TryAddTriangle(i1, i2, i3);
    }
}

    Mesh mesh = new Mesh();
    mesh.vertices = vertices;
    mesh.triangles = triangles.ToArray();
    mesh.uv = uv;
    mesh.RecalculateNormals();
    mesh.RecalculateBounds();
    mesh.MarkDynamic();

    mf.sharedMesh = mesh;
    mc.sharedMesh = mesh;

    // Store original vertices and hole data for CalibrateLevel to use when re-cutting
    BuildNavSurfaceAndAnchors(vertices, uv, gridX, gridY, holes);
}

private bool SegmentIntersectsPolygon(Vector2 a, Vector2 b, List<Vector2> poly)
{
    for (int i = 0; i < poly.Count; i++)
    {
        Vector2 c = poly[i];
        Vector2 d = poly[(i + 1) % poly.Count];

        if (SegmentsIntersect(a, b, c, d))
            return true;
    }
    return false;
}

private bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
{
    float d1 = Cross(b - a, c - a);
    float d2 = Cross(b - a, d - a);
    float d3 = Cross(d - c, a - c);
    float d4 = Cross(d - c, b - c);

    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
        ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        return true;

    return false;
}

private float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

private void BuildNavSurfaceAndAnchors(Vector3[] originalVertices = null, Vector2[] originalUV = null, int gridX = 0, int gridY = 0, List<List<Vector2>> holes = null)
{
    var surface = runtimePlane.AddComponent<Unity.AI.Navigation.NavMeshSurface>();
    surface.collectObjects = CollectObjects.Children;
    surface.layerMask = LayerMask.GetMask("Level");

    var agents = GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>();
    if (agents.Length > 0)
        surface.agentTypeID = agents[0].agentTypeID;

    ConfigureFloorBake(surface);
    surface.BuildNavMesh();

    var calibrator = runtimePlane.AddComponent<CalibrateLevel>();
    calibrator.levelTemplate = this;
    
    // Pass original mesh data and hole definitions for re-cutting after calibration
    if (originalVertices != null)
        calibrator.SetupForCalibration(originalVertices, originalUV, gridX, gridY, holes);
    
    if (floorData != null)
        calibrator.floorData = floorData;

    foreach (Transform child in transform)
    {
        if (child.gameObject == runtimePlane || child.gameObject == gameObject) continue;
        Componentizer.DoComponent<FloorAnchor>(child.gameObject, true).calibrator = calibrator;
    }
}


    /// <summary>
    /// Bake settings for an ATTRACTION FLOOR, which is not the open world
    /// NavMeshSurface defaults were chosen for.
    ///
    /// MIN REGION AREA. NavMeshSurface ships with minRegionArea = 2, meaning
    /// "delete any walkable region smaller than 2 square metres". That is a
    /// sensible way to clear specks of navmesh off scenery in a large level.
    /// On a 14ft x 16ft floor — about 21 square metres — it is a rule that
    /// throws away any piece under a TENTH of the entire attraction. Once the
    /// floor conforms to real ground it no longer bakes as one clean slab:
    /// relief splits it into regions, and every region that lands under the
    /// threshold silently disappears. The attraction ends up with a fraction
    /// of the navmesh it should have, agents cannot path across their own
    /// floor, and nothing anywhere reports a problem.
    ///
    /// Zero is the correct value here, not a smaller number. Every square
    /// metre of this mesh is deliberate, authored, walkable space — there is
    /// no scenery to clean up, so there is nothing that should ever be
    /// discarded for being small.
    ///
    /// VOXEL SIZE. Left alone, Recast voxelizes at agentRadius / 3. For the
    /// 0.75m-radius agent type that is 0.25m — coarser than this floor's own
    /// grid cell, so a conformed surface gets stair-stepped into steps that
    /// were never in the mesh, and those artificial steps are what fragments
    /// it in the first place. Resolving finer than a grid cell keeps the
    /// voxelization faithful to the geometry the creator authored.
    /// </summary>
    private void ConfigureFloorBake(Unity.AI.Navigation.NavMeshSurface surface)
    {
        surface.minRegionArea = 0f;

        float cellX = gridX > 0 ? gridWidth / gridX : gridWidth;
        float cellZ = gridY > 0 ? gridHeight / gridY : gridHeight;
        float cell = Mathf.Min(cellX, cellZ);

        float agentRadius = 0.25f;
        var settings = UnityEngine.AI.NavMesh.GetSettingsByID(surface.agentTypeID);
        if (settings.agentRadius > 0f) agentRadius = settings.agentRadius;

        // Never COARSER than Recast would have picked, and fine enough to put
        // several voxels across a grid cell. The floor keeps Unity's own
        // minimum so a tiny attraction cannot ask for an absurd voxel count.
        float defaultVoxel = agentRadius / 3f;
        float target = Mathf.Min(defaultVoxel, cell * 0.25f);
        surface.overrideVoxelSize = true;
        surface.voxelSize = Mathf.Max(0.01f, target);
    }

    public void ShowSelect()
        {
            // Only execute in playmode to avoid messing with editor objects
            if (!Application.isPlaying)
                return;

            // Remove previous linerenderer if exists
            var lr = Componentizer.DoComponent<LineRenderer>(gameObject, true);
            lr.positionCount = 5;
            lr.widthMultiplier = 0.04f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = new Color(0.5f, 0, 1f, 1f);
            lr.endColor = new Color(0.5f, 0, 1f, 1f);
            lr.loop = false;
            lr.useWorldSpace = false;

            // get dimensions (in meters) — must be custom-aware: the enum
            // overload returns Vector2.zero for GameLevelSize.Custom, which
            // drew a degenerate (invisible) selection outline on custom levels.
            Vector2 dimensions = RuntimeFootprintMeters;

            // rectangle points starting from bottom-left corner (counterclockwise)
            Vector3[] rectangle = new Vector3[5];
            rectangle[0] = new Vector3(-dimensions.x/2, 0f, -dimensions.y/2);
            rectangle[1] = new Vector3(-dimensions.x/2, 0f,  dimensions.y/2);
            rectangle[2] = new Vector3( dimensions.x/2, 0f,  dimensions.y/2);
            rectangle[3] = new Vector3( dimensions.x/2, 0f, -dimensions.y/2);
            rectangle[4] = rectangle[0]; // close the loop

            lr.SetPositions(rectangle);
        }
        public Vector3 Size {
            get {
                Vector2 dims = (size == GameLevelSize.Custom) ? GameLevelDimensions.GetDimensionsInMeters(customSize) : GameLevelDimensions.GetDimensionsInMeters(size);
                return new Vector3(dims.x, 0, dims.y);
            }
        }

        /// <summary>
        /// Footprint used by live floor, nav, calibration and activation systems.
        /// Ordinary levels use their authored Size; AttractionTemplate overrides
        /// this after a baked packing variant is selected.
        /// </summary>
        public virtual Vector2 RuntimeFootprintMeters {
            get {
                Vector3 authored = Size;
                return new Vector2(authored.x, authored.z);
            }
        }

        /// <summary>
        /// The authored footprint in FEET (x = width, y = length) — custom-aware.
        /// This is the value the Content Uploader publishes to the platform's
        /// attractions catalog (POST /api/content/{id}/attractions/dimensions),
        /// where it drives the operator app's dimensions display, size-reference
        /// tags, and size-grouped placement picker.
        /// </summary>
        public Vector2 DimensionsInFeet {
            get {
                return (size == GameLevelSize.Custom) ? customSize : GameLevelDimensions.GetDimensions(size);
            }
        }
        public void HideSelect()
        {
            Componentizer.DoComponent<LineRenderer>(gameObject,false);
        }

        public void RenderDimensions()
        {
            // Only execute in playmode to avoid messing with editor objects
            if (!Application.isPlaying)
                return;

            // Remove previous linerenderer if exists
            var existing = GetComponent<LineRenderer>();
            if (existing != null)
            {
                Destroy(existing);
            }

            var lr = gameObject.AddComponent<LineRenderer>();
            lr.positionCount = 5;
            lr.widthMultiplier = 0.02f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = new Color(0.5f, 0, 1f, 1f);
            lr.endColor = new Color(0.5f, 0, 1f, 1f);
            lr.loop = false;
            lr.useWorldSpace = false;

            // get dimensions (in meters) — custom-aware, same fix as ShowSelect
            Vector2 dimensions = RuntimeFootprintMeters;

            // rectangle points starting from bottom-left corner (counterclockwise)
            Vector3[] rectangle = new Vector3[5];
            rectangle[0] = new Vector3(-dimensions.x/2, 0f, -dimensions.y/2);
            rectangle[1] = new Vector3(-dimensions.x/2, 0f,  dimensions.y/2);
            rectangle[2] = new Vector3( dimensions.x/2, 0f,  dimensions.y/2);
            rectangle[3] = new Vector3( dimensions.x/2, 0f, -dimensions.y/2);
            rectangle[4] = rectangle[0]; // close the loop

            lr.SetPositions(rectangle);
        }

        public void TestRealWorldCalibration() {
            #if UNITY_EDITOR
            var calibrator = runtimePlane.GetComponent<CalibrateLevel>();
            if (calibrator == null) {
                calibrator = runtimePlane.AddComponent<CalibrateLevel>();
            }

            var levelObjectManager = FindFirstObjectByType<ParkBuilder.LevelObjectManager>(FindObjectsInactive.Include);
            if (levelObjectManager == null) {
                levelObjectManager = new GameObject("LevelObjectManager").AddComponent<ParkBuilder.LevelObjectManager>();
            }

            //get mesh from asset database
            var parkAsset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/DreamPark/Models/Park.fbx");
            var park = Instantiate(parkAsset);
            park.layer = LayerMask.NameToLayer("ARMesh");
            park.transform.position = transform.position;
            var mesh = park.GetComponent<MeshFilter>().sharedMesh;
            park.AddComponent<MeshCollider>().sharedMesh = mesh;

            //turn off all game activity to simulate Build Mode
            levelObjectManager.RegisterLevelObject(gameObject, true);

            //gather up all LevelTemplates and activate
            var levelTemplates = FindObjectsByType<LevelTemplate>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var lt in levelTemplates) {
                levelObjectManager.RegisterLevelObject(lt.gameObject, true);
                if (lt.runtimePlane != null) {
                    var ltcalibrator = lt.runtimePlane.GetComponent<CalibrateLevel>();
                    if (ltcalibrator == null) {
                        ltcalibrator = lt.runtimePlane.AddComponent<CalibrateLevel>();
                    }
                    ltcalibrator.calibrated = false;
                    ltcalibrator.EditorOverride = true;
                }
            }
            
            levelObjectManager.Disable();

            //enable calibration mode
            calibrator.calibrated = false;
            calibrator.EditorOverride = true;
            #endif
        }

        public bool isBuildMode {
            get {
    #if UNITY_IOS && DREAMPARKCORE
                return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.appState == "BUILD";
    #elif UNITY_EDITOR
                // In the editor, Play Mode represents actual gameplay, so the floor
                // should stay on the "Level" layer and collide with props. Only treat
                // the editor as "build mode" when not in Play Mode (i.e. scene authoring).
                return !Application.isPlaying;
    #else
                return false;
    #endif
            }
        }

#if UNITY_IOS && DREAMPARKCORE
    /// && DREAMPARKCORE: EnvironmentDustManager below lives under core's
    /// Assets/Scripts/ (not Assets/DreamPark/) and does not exist in this SDK
    /// repo — see isBuildMode's NativeInterfaceManager reference above for
    /// the same pattern. Core's own commit (feat/grid-portal-yaw @ 2e1e940e)
    /// used bare UNITY_IOS; do not drop DREAMPARKCORE re-syncing this.
    ///
    /// THE SCAN OWNS THE FLOOR ON MOBILE (Aidan, Sep 1 2026: "if the place has
    /// a scan I'd like the generated floor and gap filler to have the invisible
    /// material instead of the occluder").
    ///
    /// The occluder writes depth so real-world geometry hides content behind
    /// it. With no scan that is the whole point — it is the only floor there
    /// is. With a scan it competes with the mesh the operator walked, and the
    /// scan is the better answer: it is measured, the generated plane is
    /// inferred.
    ///
    /// DISABLING THE RENDERER, not swapping the material, and deliberately:
    /// `Materials/InvisibleOccluder` sits next to `Materials/Occlusion` in the
    /// same Resources folder and uses the IDENTICAL shader guid — swapping to
    /// it changes nothing while looking like the fix. The one genuinely
    /// different material (`DreamPark/Materials/Invisible.mat`) is not under a
    /// Resources folder, so Resources.Load returns null and the floor falls
    /// back to Unity's default: magenta over the scan. A disabled renderer is
    /// what "invisible" means, cannot fail to load, and leaves the collider —
    /// so placement raycasts and physics are untouched.
    ///
    /// MESH **OR** DUST. Zero of 32 stored environments carry a mesh today
    /// (Web, measured), so a mesh-only test would never fire on any real park.
    /// `global::` IS LOAD-BEARING, do not "tidy" it away. This file opens with
    /// `namespace DreamPark {`, and core also has a CLASS named `DreamPark`
    /// inside that same namespace (core's Assets/Scripts/DreamPark.cs). From
    /// in here the bare name binds to the sibling CLASS, not the namespace, so
    /// `DreamPark.EnvironmentDust...` fails core's iOS compile with CS0117
    /// "'DreamPark' does not contain a definition for 'EnvironmentDust'".
    /// Harmless in this repo — DREAMPARKCORE is undefined here so the body
    /// never compiles — which is exactly why it keeps getting lost: it costs
    /// nothing here and breaks core on the next import. It has already been
    /// fixed in core once and reverted by a sync; the prefix lives HERE so it
    /// survives.
    private static bool PlaceHasScanForFloorHiding()
    {
        var mesh = global::DreamPark.EnvironmentDust.EnvironmentDustManager.ActiveMeshStore;
        if (mesh != null && mesh.HasMesh) return true;
        var dust = global::DreamPark.EnvironmentDust.EnvironmentDustManager.ActiveGrid;
        return dust != null && dust.Count > 0;
    }
#endif

    #if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (showCutoutGizmos) {
                foreach (var pit in GetComponentsInChildren<FloorCutout>())
                {
                    foreach (var p in pit.points)
                    {
                        Vector3 worldP = pit.transform.TransformPoint(p);
                        Debug.DrawLine(worldP, worldP + Vector3.up * 0.2f, Color.magenta, 5f);
                    }
                }
            }
            Vector2 dimensions = RuntimeFootprintMeters;
            Vector2 dimensionsInFeet = dimensions / 0.3048f;
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Color levelPurple      = new Color(0.5f, 0, 1f);
            Color levelPurpleFill   = new Color(0.5f, 0, 1f, 0.1f);
            Color levelPurpleFaint  = new Color(0.5f, 0, 1f, 0.25f);

            // Once the floor has calibrated, the authored flat rectangle is a
            // lie — every vertex has its own height and this gizmo would be
            // left hanging in space describing a floor that is no longer
            // there, which looks exactly like a floor that FAILED to
            // calibrate. So draw the real mesh when there is one, and fall
            // back to the authored rectangle when there is not (edit mode,
            // pre-calibration, or a floor that legitimately came out flat).
            bool drewConformed = CalibratedFloorGizmo.TryDraw(
                this, levelPurple, levelPurpleFill, levelPurpleFaint, showGridGizmo);

            if (!drewConformed)
            {
                Gizmos.color = levelPurple;
                Gizmos.DrawWireCube(Vector3.zero, new Vector3(dimensions.x, 0, dimensions.y));
                Gizmos.color = levelPurpleFill;
                Gizmos.DrawCube(Vector3.zero, new Vector3(dimensions.x, 0, dimensions.y));
            }

            // Grid density visualization. Mirrors the gridX/gridY computation used
            // in floor mesh generation (line ~263) so the gizmo is exactly what the
            // floor will be subdivided into. We compute on the fly because the
            // [HideInInspector] gridX/gridY fields aren't populated until floor
            // generation runs — the gizmo needs to work in edit mode before that.
            // Skipped when the conformed pass ran — it drew the same lattice
            // from the real mesh, at the real heights.
            if (!drewConformed && showGridGizmo && gridDensity > 0 && dimensions.x > 0 && dimensions.y > 0)
            {
                float minDim = Mathf.Min(dimensions.x, dimensions.y);
                int gx = Mathf.Max(1, Mathf.RoundToInt(gridDensity * (dimensions.x / minDim)));
                int gy = Mathf.Max(1, Mathf.RoundToInt(gridDensity * (dimensions.y / minDim)));

                float halfX = dimensions.x / 2f;
                float halfZ = dimensions.y / 2f;
                float stepX = dimensions.x / gx;
                float stepZ = dimensions.y / gy;

                // Faint version of the level's purple so the grid recedes
                // visually behind the boundary wireframe.
                Gizmos.color = new Color(0.5f, 0, 1f, 0.25f);

                // Interior vertical lines (skip i=0 and i=gx — those are the
                // boundary which the wireframe already draws).
                for (int i = 1; i < gx; i++)
                {
                    float x = -halfX + stepX * i;
                    Gizmos.DrawLine(new Vector3(x, 0, -halfZ), new Vector3(x, 0, halfZ));
                }
                // Interior horizontal lines.
                for (int j = 1; j < gy; j++)
                {
                    float z = -halfZ + stepZ * j;
                    Gizmos.DrawLine(new Vector3(-halfX, 0, z), new Vector3(halfX, 0, z));
                }
            }
            Handles.Label(transform.position + transform.right * (-dimensions.x / 2f - 0.5f), dimensionsInFeet.y + "ft");
            Handles.Label(transform.position + transform.right * ( dimensions.x / 2f + 0.3f), dimensionsInFeet.y + "ft");
            Handles.Label(transform.position + transform.forward * ( dimensions.y / 2f + 0.5f), dimensionsInFeet.x + "ft");
            Handles.Label(transform.position + transform.forward * (-dimensions.y / 2f - 0.3f), dimensionsInFeet.x + "ft");
            Gizmos.color = new Color(0.5f, 0, 1f,0.1f);
            Vector3 portalPosition = new Vector3(defaultAnchorPosition.x, 0, defaultAnchorPosition.y);
            Vector3 bodyPosition = new Vector3(portalPosition.x, 0, portalPosition.z - 1f);
            Mesh humanMesh = Resources.Load<Mesh>("Meshes/HumanReference");
            Mesh quadMesh = Resources.Load<Mesh>("Meshes/Quad");
            Material unlitMat = Resources.Load<Material>("Materials/UnlitCutout");
            unlitMat.SetTexture("_baseTex", Resources.Load<Texture2D>("Textures/Portal"));
            Matrix4x4 matrix = Matrix4x4.Translate(portalPosition);
            matrix = transform.localToWorldMatrix * matrix;
            unlitMat.SetPass(0);
            Gizmos.DrawMesh(humanMesh, bodyPosition);
            Graphics.DrawMeshNow(quadMesh, matrix);

            // Gated on HasAnyWall (not just showWallGizmos) so the
            // GetComponentsInChildren<Collider> walk inside
            // GetWallHeightMeters only runs for attractions that actually
            // declare a wall — the common no-wall case costs one bool check,
            // not the per-frame tax PropTemplate's own footprint gizmo was
            // moved off OnDrawGizmos for.
            if (showWallGizmos && HasAnyWall)
            {
                DrawWallGizmos(dimensions);
            }

            Gizmos.matrix = oldMatrix;
        }

        private static readonly Color WallGizmoColor = new Color(0.1f, 0.6f, 1f, 1f);
        private static readonly Color WallGizmoFill = new Color(0.1f, 0.6f, 1f, 0.15f);

        // Called with Gizmos.matrix already set to transform.localToWorldMatrix
        // by the caller (OnDrawGizmos) — dimensions is local X = width, Y =
        // length in meters, the same frame the floor rectangle above is drawn
        // in.
        private void DrawWallGizmos(Vector2 dimensions)
        {
            float height = GetWallHeightMeters();

            WallSide selected = EffectiveWalls;
            DrawWallIfSet((selected & WallSide.Front) != 0, new Vector3(0f, height * 0.5f, dimensions.y * 0.5f), new Vector3(dimensions.x, height, 0.05f));
            DrawWallIfSet((selected & WallSide.Back) != 0, new Vector3(0f, height * 0.5f, -dimensions.y * 0.5f), new Vector3(dimensions.x, height, 0.05f));
            DrawWallIfSet((selected & WallSide.Right) != 0, new Vector3(dimensions.x * 0.5f, height * 0.5f, 0f), new Vector3(0.05f, height, dimensions.y));
            DrawWallIfSet((selected & WallSide.Left) != 0, new Vector3(-dimensions.x * 0.5f, height * 0.5f, 0f), new Vector3(0.05f, height, dimensions.y));
        }

        private static void DrawWallIfSet(bool set, Vector3 center, Vector3 size)
        {
            if (!set) return;
            Gizmos.color = WallGizmoFill;
            Gizmos.DrawCube(center, size);
            Gizmos.color = WallGizmoColor;
            Gizmos.DrawWireCube(center, size);
        }

        /// <summary>
        /// The wall height this attraction needs, in meters: the 10 ft
        /// default, or taller if the attraction's own content reaches higher
        /// (e.g. a tall portal frame) — never shorter. See
        /// WallHeightMeasurement for why this is collider-shape based rather
        /// than Renderer.bounds, which is also what makes it safe for the
        /// Content Uploader to call on a disk-loaded prefab asset (it does
        /// not, today, but could).
        /// </summary>
        public float GetWallHeightMeters() => WallHeightMeasurement.GetWallHeightMeters(transform, transform.position.y);
    #endif
    }
}
