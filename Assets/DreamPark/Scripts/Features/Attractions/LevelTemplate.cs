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
        if (GUILayout.Button("Test Real World Calibration")) {
            var levelTemplate = target as LevelTemplate;
            levelTemplate.TestRealWorldCalibration();
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
        public bool generateCeiling = true;
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
        #if UNITY_EDITOR
        public void OnValidate()
        {
            _isCustom = size == GameLevelSize.Custom;
            if (floorMaterial == null) {
                floorMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/DreamPark/Materials/Occlusion.mat");
            }
        }
        #endif
        void Start()
        {
            if (generateFloor) GenerateFloorWithHoles();
            if (generateCeiling) GenerateDepthCeiling();
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

        private void GenerateDepthCeiling() {
            if (runtimeCeiling != null) Destroy(runtimeCeiling);

            Vector2 dims = _isCustom ? GameLevelDimensions.GetDimensionsInMeters(new Vector2(customSize.x, customSize.y)) : GameLevelDimensions.GetDimensionsInMeters(size);
            float width = dims.x;
            float height = dims.y;

            runtimeCeiling = new GameObject("LevelCeiling");
            runtimeCeiling.transform.localPosition = new Vector3(0, 2.4f, 0);
            runtimeCeiling.layer = LayerMask.NameToLayer("Triggers");
            runtimeCeiling.transform.SetParent(transform, false);
            runtimeCeiling.AddComponent<OptimizedAFIgnore>();

            MeshFilter mf = runtimeCeiling.AddComponent<MeshFilter>();
            runtimeCeiling.AddComponent<MeshRenderer>().enabled = false;

            Mesh mesh = new Mesh();

            Vector3[] vertices = new Vector3[4] {
                new Vector3(-width/2f, 0, -height/2f),
                new Vector3(-width/2f, 0,  height/2f),
                new Vector3( width/2f, 0,  height/2f),
                new Vector3( width/2f, 0, -height/2f)
            };

            // Flip normals by reversing the winding order
            int[] triangles = new int[6] {
                0, 2, 1,
                0, 3, 2
            };

            Vector2[] uv = new Vector2[4] {
                new Vector2(0,0),
                new Vector2(0,1),
                new Vector2(1,1),
                new Vector2(1,0)
            };

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uv;
            mesh.RecalculateNormals();

            mf.sharedMesh = mesh;
            var depthMask = runtimeCeiling.AddComponent<DepthMask>();
            depthMask.myMeshFilters.Add(mf);
            depthMask._someOffsetFloatValue = 0.6f;
        }

        /// <summary>
        /// Public method to regenerate the floor mesh and notify listeners.
        /// </summary>
        public void RegenerateFloor()
        {
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
                Debug.LogWarning($"[LevelTemplate] SetFloorVisibilityForMode: runtimePlane is null on {gameObject.name}, skipping to preserve calibration data");
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
    Vector2 dims = _isCustom
        ? GameLevelDimensions.GetDimensionsInMeters(new Vector2(customSize.x, customSize.y))
        : GameLevelDimensions.GetDimensionsInMeters(size);

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

            // get dimensions (in meters)
            Vector2 dimensions = GameLevelDimensions.GetDimensionsInMeters(size);

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

            // get dimensions (in meters)
            Vector2 dimensions = GameLevelDimensions.GetDimensionsInMeters(size);

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
            Vector2 dimensionsInFeet = _isCustom ? customSize : GameLevelDimensions.GetDimensions(size);
            Vector2 dimensions = _isCustom ? GameLevelDimensions.GetDimensionsInMeters(new Vector2(customSize.x, customSize.y)) : GameLevelDimensions.GetDimensionsInMeters(size);
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.5f, 0, 1f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(dimensions.x, 0, dimensions.y));
            Gizmos.color = new Color(0.5f, 0, 1f, 0.1f);
            Gizmos.DrawCube(Vector3.zero, new Vector3(dimensions.x, 0, dimensions.y));

            // Grid density visualization. Mirrors the gridX/gridY computation used
            // in floor mesh generation (line ~263) so the gizmo is exactly what the
            // floor will be subdivided into. We compute on the fly because the
            // [HideInInspector] gridX/gridY fields aren't populated until floor
            // generation runs — the gizmo needs to work in edit mode before that.
            if (showGridGizmo && gridDensity > 0 && dimensions.x > 0 && dimensions.y > 0)
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
            Gizmos.matrix = oldMatrix;
        }
    #endif
    }
}
