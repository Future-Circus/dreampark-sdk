namespace DreamPark {
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.XR.ARFoundation;
    using UnityEngine.Rendering;
    using Defective.JSON;

    [RequireComponent(typeof(MeshFilter), typeof(MeshCollider))]
    public class CalibrateLevel : MonoBehaviour
    {
        [Header("AR Mesh Input")]
        public ARMeshManager arMeshManager;

        [Header("Surface Adaptation")]
        public float updateInterval = 2f;
        public float surfaceFollowSpeed = 5f;
        public LayerMask arMeshLayer = -1; // layer for AR meshes
        private float raycastAboveDevice = 10f;  // how far above device Y to start ray
        private float raycastLength = 20f;        // total ray length downward
        private Mesh dynamicMesh;
        private MeshCollider meshCollider;
        private MeshFilter meshFilter;
        private float lastUpdateTime = -Mathf.Infinity;
        public bool calibrated = false;
        [HideInInspector] public bool EditorOverride = false;
        [Header("Integration")]
        public LevelTemplate levelTemplate;
        [HideInInspector] public JSONObject floorData;
        [HideInInspector] public bool hasPendingCalibration { get; private set; }

        // Store original mesh data and hole definitions for re-cutting
        private Vector3[] originalVertices;
        private Vector2[] originalUV;
        private int gridX, gridY;
        private List<List<Vector2>> holeDefinitions = new List<List<Vector2>>();

        void Start()
        {
            if (arMeshManager == null)
                arMeshManager = FindFirstObjectByType<ARMeshManager>(FindObjectsInactive.Include);

            meshFilter = GetComponent<MeshFilter>();
            meshCollider = GetComponent<MeshCollider>();

            if (meshFilter != null)
                dynamicMesh = meshFilter.sharedMesh;

            // Assign layer if not set
            if (arMeshLayer == -1)
                arMeshLayer = LayerMask.GetMask("ARMesh");

            if (levelTemplate == null)
                levelTemplate = GetComponentInParent<LevelTemplate>();

#if DREAMPARKCORE
            if (floorData != null) {
                // On headset (Android), apply immediately. On iOS, only apply if portal is synced.
                bool isHeadset = Application.platform == RuntimePlatform.Android;

                // Check if portal is synced (find the portal anchor for this level)
                bool portalIsSynced = false;
                var portalAnchor = GetComponentInParent<PortalAnchor>();
                if (portalAnchor != null) {
                    portalIsSynced = portalAnchor.isSynced;
                }

                if (isHeadset || portalIsSynced) {
                    // Don't apply yet — mark as pending so LevelAnchor can apply AFTER
                    // objects are disabled, preventing physics/collision issues from the
                    // floor shifting under active rigidbodies.
                    hasPendingCalibration = true;
                    Debug.Log($"[CalibrateLevel] Calibration data ready (pending) for {gameObject.name} (isHeadset={isHeadset}, portalSynced={portalIsSynced})");
                } else {
                    Debug.Log("[CalibrateLevel] iOS - portal not synced, showing flat map for " + gameObject.name);
                    // Don't apply calibration visually, but keep floorData on CalibrateLevel
                    // so CompileCalibrationData() can preserve it during saves.
                    // (floorData is intentionally NOT cleared here)
                }
            }
#endif
        }

        /// <summary>
        /// Called by LevelAnchor after all objects are spawned and disabled.
        /// Applies stored calibration data safely — no active rigidbodies to disturb.
        /// </summary>
        public void ApplyPendingCalibration()
        {
            if (!hasPendingCalibration || floorData == null) return;
            Debug.Log($"[CalibrateLevel] Applying pending calibration for {gameObject.name}");
            ApplyCalibrationData(floorData);
            floorData = null;
            hasPendingCalibration = false;
        }

        /// <summary>
        /// Called by LevelTemplate after creating the base grid mesh (before hole cutting).
        /// Stores the original vertices and hole definitions for later re-cutting after calibration.
        /// </summary>
        public void SetupForCalibration(Vector3[] vertices, Vector2[] uv, int gridX, int gridY, List<List<Vector2>> holes)
        {
            this.originalVertices = (Vector3[])vertices.Clone();
            this.originalUV = (Vector2[])uv.Clone();
            this.gridX = gridX;
            this.gridY = gridY;
            this.holeDefinitions = holes ?? new List<List<Vector2>>();
            
            Debug.Log($"[CalibrateLevel] Setup for calibration: {vertices.Length} vertices, {holes?.Count ?? 0} holes");
        }

        void Update()
        {
            if (isCalibrating && Time.time - lastUpdateTime > updateInterval)
            {
                lastUpdateTime = Time.time;
                ConformGridToSurface();
            }
        }

        private void ConformGridToSurface()
        {
            Debug.Log("ConformGridToSurface called");
            if (dynamicMesh == null)
            {
                Debug.LogWarning("CalibrateLevel: Missing dynamic mesh.");
                return;
            }

            // If we have original vertices stored, use those as the base for calibration
            Vector3[] verts = originalVertices != null
                ? (Vector3[])originalVertices.Clone()
                : dynamicMesh.vertices;

            int hitCount = 0;

            // Anchor raycast origin to device/camera height so it works on
            // hillsides and varied terrain instead of a fixed world-space offset.
            float deviceY = Camera.main != null ? Camera.main.transform.position.y : 0f;
            float rayOriginY = deviceY + raycastAboveDevice;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 worldPos = transform.TransformPoint(verts[i]);
                Ray ray = new Ray(new Vector3(worldPos.x, rayOriginY, worldPos.z), Vector3.down);

                if (TryGetRaycastHit(ray, out RaycastHit hit))
                {
                    float targetY = hit.point.y;
                    float newY = targetY;
                    verts[i] = transform.InverseTransformPoint(new Vector3(worldPos.x, newY, worldPos.z));
                    hitCount++;
                }
            }

            if (hitCount > 0)
            {
                Debug.Log("CalibrateLevel: " + hitCount + " hits found, updating mesh");
                
                // Re-cut holes with calibrated vertices
                if (holeDefinitions != null && holeDefinitions.Count > 0)
                {
                    RecutHolesAndUpdateMesh(verts);
                }
                else
                {
                    // No holes, just update the mesh directly
                    dynamicMesh.vertices = verts;
                    dynamicMesh.RecalculateNormals();
                    meshCollider.sharedMesh = dynamicMesh;
                }
            } 
            else
            {
                Debug.LogWarning("CalibrateLevel: No hits found");
            }
            calibrated = true;
            LevelTemplate.NotifyLevelTemplateChanged();
        }

        private void RecutHolesAndUpdateMesh(Vector3[] calibratedVertices)
        {
            // Push vertices out of holes (same logic as LevelTemplate)
            float cellSizeX = gridX > 0 ? (calibratedVertices[gridX].x - calibratedVertices[0].x) : 1f;
            float step = cellSizeX * 0.25f;
            const int maxPushIters = 20;

            // Precompute centroids
            var centroids = new List<Vector2>(holeDefinitions.Count);
            foreach (var h in holeDefinitions)
                centroids.Add(PolygonCentroid(h));

            // Push vertices out of holes
            for (int vi = 0; vi < calibratedVertices.Length; vi++)
            {
                Vector2 v2 = new Vector2(calibratedVertices[vi].x, calibratedVertices[vi].z);

                for (int h = 0; h < holeDefinitions.Count; h++)
                {
                    var hole = holeDefinitions[h];
                    if (!PointInPolygon(v2, hole))
                        continue;

                    Vector2 center = centroids[h];
                    int iter = 0;
                    while (PointInPolygon(v2, hole) && iter < maxPushIters)
                    {
                        Vector2 dir = v2 - center;
                        if (dir.sqrMagnitude < 1e-8f)
                            dir = Vector2.right;
                        dir.Normalize();
                        v2 += dir * step;
                        iter++;
                    }

                    calibratedVertices[vi].x = v2.x;
                    calibratedVertices[vi].z = v2.y;
                    break;
                }
            }

            // Generate triangles with hole cutting
            int vertCountX = gridX + 1;
            List<int> triangles = new List<int>();

            for (int y = 0; y < gridY; y++)
            {
                for (int x = 0; x < gridX; x++)
                {
                    int i0 = y * vertCountX + x;
                    int i1 = i0 + 1;
                    int i2 = (y + 1) * vertCountX + x;
                    int i3 = i2 + 1;

                    TryAddTriangle(triangles, calibratedVertices, i0, i2, i1);
                    TryAddTriangle(triangles, calibratedVertices, i1, i2, i3);
                }
            }

            // Update mesh
            dynamicMesh.Clear();
            dynamicMesh.vertices = calibratedVertices;
            dynamicMesh.triangles = triangles.ToArray();
            if (originalUV != null)
                dynamicMesh.uv = originalUV;
            dynamicMesh.RecalculateNormals();
            dynamicMesh.RecalculateBounds();
            meshCollider.sharedMesh = dynamicMesh;

            Debug.Log($"[CalibrateLevel] Re-cut holes: {triangles.Count / 3} triangles");
        }

        private void TryAddTriangle(List<int> triangles, Vector3[] vertices, int a, int b, int c)
        {
            Vector2 A = new Vector2(vertices[a].x, vertices[a].z);
            Vector2 B = new Vector2(vertices[b].x, vertices[b].z);
            Vector2 C = new Vector2(vertices[c].x, vertices[c].z);

            foreach (var hole in holeDefinitions)
            {
                if (PointInPolygon(A, hole) ||
                    PointInPolygon(B, hole) ||
                    PointInPolygon(C, hole))
                    return;

                if (SegmentIntersectsPolygon(A, B, hole)) return;
                if (SegmentIntersectsPolygon(B, C, hole)) return;
                if (SegmentIntersectsPolygon(C, A, hole)) return;
            }

            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        private bool PointInPolygon(Vector2 p, List<Vector2> poly)
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

        private Vector2 PolygonCentroid(List<Vector2> poly)
        {
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < poly.Count; i++)
                sum += poly[i];
            return sum / poly.Count;
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
        public JSONObject CompileCalibrationData() {
            JSONObject gridData = new JSONObject();
            for (int i = 0; i < dynamicMesh.vertices.Length; i++) {
                float rounded = dynamicMesh.vertices[i].y.RoundFloat();
                // Skip vertices at or near zero — avoids storing "0.000" entries
                // caused by floating point drift from mesh operations.
                if (Mathf.Abs(rounded) < 0.001f) {
                    continue;
                }
                gridData.AddField(i.ToString(), rounded.ToString("F3"));
            }
            // Safety net: if mesh vertices are all flat (calibration wasn't applied to mesh),
            // preserve the original loaded floor data rather than saving empty/zero data
            // that would overwrite good calibration on the backend.
            if (gridData.count == 0 && levelTemplate != null && levelTemplate.floorData != null && levelTemplate.floorData.count > 0) {
                Debug.LogWarning("[CalibrateLevel] Mesh has no calibration applied — preserving stored floorData for " + gameObject.name);
                return levelTemplate.floorData;
            }
            return gridData;
        }

        public void ApplyCalibrationData(JSONObject gridData) {
            Vector3[] verts = dynamicMesh.vertices;
            for (int i = 0; i < verts.Length; i++) {
                if (gridData.HasField(i.ToString())) {
                    verts[i].y = float.Parse(gridData.GetField(i.ToString()).stringValue);
                }
            }
            dynamicMesh.vertices = verts;
            dynamicMesh.RecalculateNormals();
            meshCollider.sharedMesh = dynamicMesh;
            Debug.Log("[CalibrateLevel] Applied calibration data for " + gameObject.name);
            calibrated = true;
            LevelTemplate.NotifyLevelTemplateChanged();
        }
        public bool hasFloorData {
            get {
                if (floorData != null && floorData.count > 0) return true;
                if (dynamicMesh == null) return false;
                // Check if any vertex has a non-zero Y (i.e. calibrated)
                var verts = dynamicMesh.vertices;
                for (int i = 0; i < verts.Length; i++) {
                    if (Mathf.Abs(verts[i].y) > 0.001f) return true;
                }
                return false;
            }
        }

        public void Clear() {
            // Clear stored data
            floorData = null;
            hasPendingCalibration = false;
            calibrated = false;

            // Rebuild the floor from scratch so holes are re-cut on a flat grid
            if (levelTemplate != null) {
                levelTemplate.floorData = null;
                levelTemplate.RegenerateFloor();
            } else {
                // Fallback: just zero out vertices directly
                Vector3[] verts = dynamicMesh.vertices;
                for (int i = 0; i < verts.Length; i++) {
                    verts[i].y = 0;
                }
                dynamicMesh.vertices = verts;
                dynamicMesh.RecalculateNormals();
                meshCollider.sharedMesh = dynamicMesh;
            }

            LevelTemplate.NotifyLevelTemplateChanged();
            Debug.Log("[CalibrateLevel] Cleared calibration data for " + gameObject.name);
        }
        // Minimum upward component of surface normal to count as "floor".
        // 0.5 ≈ 60° from vertical — accepts slopes, rejects walls and ceilings.
        private const float minFloorNormalY = 0.5f;

        private bool TryGetRaycastHit(Ray ray, out RaycastHit hit)
        {
            // RaycastAll so we can skip walls/ceilings and find the floor behind them.
            var hits = Physics.RaycastAll(ray, raycastLength, arMeshLayer);
            hit = default;
            if (hits.Length == 0) return false;

            // Sort by distance (closest first) and pick the first floor-like surface.
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].normal.y >= minFloorNormalY)
                {
                    hit = hits[i];
                    return true;
                }
            }
            return false;
        }

        void OnEnable()
        {
            if (arMeshManager != null)
                arMeshManager.meshesChanged += OnMeshesChanged;
        }

        void OnDisable()
        {
            if (arMeshManager != null)
                arMeshManager.meshesChanged -= OnMeshesChanged;
        }

        private void OnMeshesChanged(ARMeshesChangedEventArgs args)
        {
            if (args.added.Count > 0 || args.updated.Count > 0)
                ConformGridToSurface();
        }

        public bool isCalibrating {
            get {
                #if DREAMPARKCORE
                return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.unityState == "CALIBRATE";
                #else
                return EditorOverride;
                #endif
            }
        }
    }
}