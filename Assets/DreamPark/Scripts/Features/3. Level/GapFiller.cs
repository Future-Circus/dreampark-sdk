namespace DreamPark {
    using System.Collections.Generic;
    using UnityEngine;
    using Unity.AI.Navigation;
    using System.Linq;

#if UNITY_EDITOR
    using UnityEditor;

    [CustomEditor(typeof(GapFiller))]
    public class GapFillerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            GapFiller gapFiller = (GapFiller)target;

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Generate Gap Filler Mesh"))
            {
                gapFiller.GenerateGapFillerMesh();
            }

            if (GUILayout.Button("Clear Gap Filler Mesh"))
            {
                gapFiller.ClearMesh();
            }
        }
    }
#endif

    public class GapFiller : MonoBehaviour
    {
        // Singleton instance
        private static GapFiller _instance;
        public static GapFiller Instance
        {
            get
            {
                if (_instance == null)
                {
                    // Try to find existing instance
                    _instance = FindFirstObjectByType<GapFiller>();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Ensures a GapFiller instance exists. Called automatically by LevelTemplate.
        /// </summary>
        public static void EnsureInstance()
        {
            if (Instance != null) return;

            // Create new GapFiller
            var go = new GameObject("GapFiller");
            _instance = go.AddComponent<GapFiller>();

            Debug.Log("[GapFiller] Auto-created GapFiller instance");
        }

        [Header("Settings")]
        [Tooltip("Grid density in vertices per meter")]
        public float verticesPerMeter = 3f;
        
        [Tooltip("Padding around the combined bounds")]
        public float boundsPadding = 3f;
        
        [Tooltip("How far to search for nearby floor edges when setting vertex heights")]
        public float edgeBlendDistance = 1f;
        
        [Tooltip("Material for the gap filler mesh")]
        public Material floorMaterial;

        [Header("Auto-Regeneration")]
        [Tooltip("Automatically regenerate when LevelTemplates change")]
        public bool autoRegenerate = true;

        [Tooltip("Delay before regenerating after a change (to batch multiple changes)")]
        public float regenerateDelay = 0.1f;

        [Header("Debug")]
        public bool debugLog = false;
        public bool showGizmos = true;

        [Header("Runtime")]
        [SerializeField, HideInInspector]
        private GameObject runtimeMesh;

        private List<LevelFloorData> levelFloors = new List<LevelFloorData>();
        private bool regeneratePending = false;
        private float regenerateTimer = 0f;

        /// <summary>
        /// When true, auto-regeneration from change events is suppressed.
        /// Set after the initial mesh generation completes during park load.
        /// GapFiller should only run during initial park load, not during
        /// active gameplay (where prop destruction/movement would otherwise
        /// trigger expensive recalculations).
        /// Reset when the GapFiller instance is destroyed (park teardown).
        /// </summary>
        private bool _initialGenerationComplete = false;

        private void OnEnable()
        {
            if (autoRegenerate)
            {
                LevelTemplate.OnAnyLevelTemplateChanged += OnLevelTemplateChanged;
                PropTemplate.OnAnyPropTemplateChanged += OnLevelTemplateChanged;
            }
        }

        private void OnDisable()
        {
            LevelTemplate.OnAnyLevelTemplateChanged -= OnLevelTemplateChanged;
            PropTemplate.OnAnyPropTemplateChanged -= OnLevelTemplateChanged;
        }

        private void Update()
        {
            if (regeneratePending)
            {
                regenerateTimer -= Time.deltaTime;
                if (regenerateTimer <= 0f)
                {
                    if (IsBuildMode())
                    {
                        // Keep pending so we regenerate as soon as play mode resumes.
                        return;
                    }

                    regeneratePending = false;
                    GenerateGapFillerMesh();

                    // Mark initial generation complete so further gameplay events
                    // (prop destruction, physics movement) do not retrigger generation.
                    _initialGenerationComplete = true;
                }
            }
        }

        /// <summary>
        /// Resets the generation lock so GapFiller will respond to change events again.
        /// Call this when reloading park content or entering build mode where
        /// regeneration during editing is desired.
        /// </summary>
        public void ResetGenerationLock()
        {
            _initialGenerationComplete = false;
        }

        private bool IsBuildMode()
        {
#if DREAMPARKCORE
            return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.buildMode;
#else
            return false;
#endif
        }

        private void OnLevelTemplateChanged()
        {
            if (!autoRegenerate) return;

            // Once the initial gap filler mesh has been generated during park load,
            // suppress further auto-regeneration in deployed gameplay. Gameplay events
            // (prop destruction, physics movement) should not trigger expensive mesh
            // recalculations. Build mode and the Unity Editor are exempt so authors can
            // iterate on LevelTemplate/PropTemplate changes and see the mesh update.
            if (_initialGenerationComplete && !IsBuildMode() && !Application.isEditor) return;

            // Start or reset the delay timer to batch multiple rapid changes
            regeneratePending = true;
            regenerateTimer = regenerateDelay;

            if (debugLog)
                Debug.Log("[GapFiller] LevelTemplate changed, regeneration scheduled");
        }

        // Stores data about each level's floor
        private class LevelFloorData
        {
            public GameObject runtimeFloor;
            public string sourceName;
            public Vector2[] worldFootprint; // 4 corners in world XZ space
            public List<Vector2[]> holePolygons = new List<Vector2[]>();
            public float[] cornerHeights;    // Y height at each corner
            public Vector2 center;
            public bool cutsHole = true;
            
            public bool ContainsPoint(Vector2 point)
            {
                return PointInPolygon(point, worldFootprint);
            }
            
            public float GetHeightAtPoint(Vector2 point)
            {
                // Bilinear interpolation based on position within the quad
                // For simplicity, use inverse distance weighting from corners
                float totalWeight = 0f;
                float weightedHeight = 0f;
                
                for (int i = 0; i < 4; i++)
                {
                    float dist = Vector2.Distance(point, worldFootprint[i]);
                    if (dist < 0.001f) return cornerHeights[i];
                    float weight = 1f / (dist * dist);
                    totalWeight += weight;
                    weightedHeight += cornerHeights[i] * weight;
                }
                
                return weightedHeight / totalWeight;
            }

            public bool IsPointInHole(Vector2 point)
            {
                if (!cutsHole || holePolygons == null)
                    return false;

                for (int i = 0; i < holePolygons.Count; i++)
                {
                    var polygon = holePolygons[i];
                    if (polygon != null && polygon.Length >= 3 && PointInPolygon(point, polygon))
                        return true;
                }

                return false;
            }
            
            public float GetDistanceToEdge(Vector2 point, out Vector2 closestEdgePoint)
            {
                float minDist = float.MaxValue;
                closestEdgePoint = point;
                
                for (int i = 0; i < worldFootprint.Length; i++)
                {
                    Vector2 a = worldFootprint[i];
                    Vector2 b = worldFootprint[(i + 1) % worldFootprint.Length];
                    
                    Vector2 closest = ClosestPointOnSegment(point, a, b);
                    float dist = Vector2.Distance(point, closest);
                    
                    if (dist < minDist)
                    {
                        minDist = dist;
                        closestEdgePoint = closest;
                    }
                }
                
                return minDist;
            }
            
            public float GetHeightAtEdgePoint(Vector2 edgePoint)
            {
                // Find which edge this point is on and interpolate height
                for (int i = 0; i < worldFootprint.Length; i++)
                {
                    Vector2 a = worldFootprint[i];
                    Vector2 b = worldFootprint[(i + 1) % worldFootprint.Length];
                    
                    // Check if point is on this edge
                    float distToLine = DistanceToSegment(edgePoint, a, b);
                    if (distToLine < 0.01f)
                    {
                        // Interpolate height along this edge
                        float t = Vector2.Distance(a, edgePoint) / Vector2.Distance(a, b);
                        return Mathf.Lerp(cornerHeights[i], cornerHeights[(i + 1) % 4], t);
                    }
                }
                
                return GetHeightAtPoint(edgePoint);
            }
            
            private static Vector2 ClosestPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
            {
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                return a + ab * t;
            }
            
            private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
            {
                return Vector2.Distance(p, ClosestPointOnSegment(p, a, b));
            }
            
            private static bool PointInPolygon(Vector2 p, Vector2[] poly)
            {
                bool inside = false;
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                {
                    if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                        (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                    {
                        inside = !inside;
                    }
                }
                return inside;
            }
        }

        public void GenerateGapFillerMesh()
        {
            ClearMesh();
            
            // Gather all floor-influencing templates
            LevelTemplate[] levelTemplates = FindObjectsByType<LevelTemplate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            PropTemplate[] propTemplates = FindObjectsByType<PropTemplate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            
            if (levelTemplates.Length == 0 && propTemplates.Length == 0)
            {
                Debug.LogWarning("[GapFiller] No LevelTemplates or PropTemplates found in scene");
                return;
            }
            
            if (debugLog)
                Debug.Log($"[GapFiller] Found {levelTemplates.Length} LevelTemplates and {propTemplates.Length} PropTemplates");

            // Extract floor data from each template
            levelFloors.Clear();
            Bounds combinedBounds = new Bounds();
            bool boundsInitialized = false;
            
            foreach (var template in levelTemplates)
            {
                var floorData = ExtractFloorData(template);
                if (floorData != null)
                {
                    levelFloors.Add(floorData);
                    
                    // Expand combined bounds
                    foreach (var corner in floorData.worldFootprint)
                    {
                        Vector3 worldPoint = new Vector3(corner.x, 0, corner.y);
                        if (!boundsInitialized)
                        {
                            combinedBounds = new Bounds(worldPoint, Vector3.zero);
                            boundsInitialized = true;
                        }
                        else
                        {
                            combinedBounds.Encapsulate(worldPoint);
                        }
                    }
                }
            }

            foreach (var template in propTemplates)
            {
                var floorData = ExtractFloorData(template);
                if (floorData != null)
                {
                    levelFloors.Add(floorData);

                    foreach (var corner in floorData.worldFootprint)
                    {
                        Vector3 worldPoint = new Vector3(corner.x, 0, corner.y);
                        if (!boundsInitialized)
                        {
                            combinedBounds = new Bounds(worldPoint, Vector3.zero);
                            boundsInitialized = true;
                        }
                        else
                        {
                            combinedBounds.Encapsulate(worldPoint);
                        }
                    }
                }
            }
            
            if (levelFloors.Count == 0)
            {
                Debug.LogWarning("[GapFiller] No valid floor data extracted");
                return;
            }
            
            // Add padding
            combinedBounds.Expand(boundsPadding * 2f);
            
            if (debugLog)
                Debug.Log($"[GapFiller] Combined bounds: center={combinedBounds.center}, size={combinedBounds.size}");

            // Generate the gap filler mesh
            GenerateMesh(combinedBounds);
        }

        private LevelFloorData ExtractFloorData(LevelTemplate template)
        {
            // Get the runtime floor mesh if it exists (this has calibrated vertices)
            if (template.runtimePlane != null)
            {
                MeshFilter mf = template.runtimePlane.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    return ExtractFloorDataFromMesh(template, mf);
                }
            }
            
            // Fallback to template dimensions if no runtime mesh yet
            return ExtractFloorDataFromTemplate(template);
        }

        private LevelFloorData ExtractFloorData(PropTemplate template)
        {
            if (template == null || !template.affectsGapFiller)
                return null;

            if (!template.TryGetWorldFootprint(out var footprint, out var surfaceHeight))
                return null;

            var data = new LevelFloorData
            {
                runtimeFloor = template.runtimePlane,
                sourceName = template.name,
                worldFootprint = footprint,
                cornerHeights = new float[footprint.Length],
                cutsHole = template.cutGapFillerHole
            };

            if (data.cutsHole)
            {
                if (template.TryGetWorldCutoutPolygons(out var cutoutPolygons) && cutoutPolygons != null && cutoutPolygons.Count > 0)
                    data.holePolygons = cutoutPolygons;
                else
                    data.holePolygons.Add(footprint);
            }

            Vector2 centerSum = Vector2.zero;
            for (int i = 0; i < footprint.Length; i++)
            {
                data.cornerHeights[i] = surfaceHeight;
                centerSum += footprint[i];
            }
            data.center = centerSum / Mathf.Max(1, footprint.Length);

            if (debugLog)
                Debug.Log($"[GapFiller] Extracted floor data from PROP for {template.name}: corners at {string.Join(", ", data.worldFootprint)}, cutouts={data.holePolygons.Count}");

            return data;
        }

        private LevelFloorData ExtractFloorDataFromMesh(LevelTemplate template, MeshFilter meshFilter)
        {
            Mesh mesh = meshFilter.sharedMesh;
            Vector3[] vertices = mesh.vertices;
            Transform meshTransform = meshFilter.transform;
            
            // Find the actual corner vertices of the mesh (min/max X and Z)
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            
            // Transform all vertices to world space and find bounds
            List<Vector3> worldVerts = new List<Vector3>();
            foreach (var v in vertices)
            {
                Vector3 worldV = meshTransform.TransformPoint(v);
                worldVerts.Add(worldV);
                
                if (worldV.x < minX) minX = worldV.x;
                if (worldV.x > maxX) maxX = worldV.x;
                if (worldV.z < minZ) minZ = worldV.z;
                if (worldV.z > maxZ) maxZ = worldV.z;
            }
            
            // For rotated meshes, we need the actual convex hull or the original corner positions
            // Let's use the template's corner positions but get heights from the mesh
            Vector2 dims = template.size == GameLevelSize.Custom
                ? GameLevelDimensions.GetDimensionsInMeters(template.customSize)
                : GameLevelDimensions.GetDimensionsInMeters(template.size);
            
            float halfWidth = dims.x / 2f;
            float halfHeight = dims.y / 2f;
            
            // Local corners (before rotation)
            Vector3[] localCorners = new Vector3[4]
            {
                new Vector3(-halfWidth, 0, -halfHeight),
                new Vector3(halfWidth, 0, -halfHeight),
                new Vector3(halfWidth, 0, halfHeight),
                new Vector3(-halfWidth, 0, halfHeight)
            };
            
            LevelFloorData data = new LevelFloorData
            {
                runtimeFloor = template.runtimePlane,
                sourceName = template.name,
                worldFootprint = new Vector2[4],
                cornerHeights = new float[4]
            };
            
            Vector2 centerSum = Vector2.zero;
            
            for (int i = 0; i < 4; i++)
            {
                // Get world XZ position from template transform
                Vector3 worldCorner = template.transform.TransformPoint(localCorners[i]);
                data.worldFootprint[i] = new Vector2(worldCorner.x, worldCorner.z);
                centerSum += data.worldFootprint[i];
                
                // Get actual Y height from the mesh by finding nearest vertex
                float nearestHeight = GetNearestMeshVertexHeight(worldVerts, worldCorner.x, worldCorner.z);
                data.cornerHeights[i] = nearestHeight;
            }
            
            data.center = centerSum / 4f;
            data.holePolygons.Add(data.worldFootprint);
            
            if (debugLog)
                Debug.Log($"[GapFiller] Extracted floor data from MESH for {template.name}: heights={string.Join(", ", data.cornerHeights)}");
            
            return data;
        }

        private float GetNearestMeshVertexHeight(List<Vector3> worldVerts, float x, float z)
        {
            float nearestDist = float.MaxValue;
            float nearestHeight = 0f;
            
            foreach (var v in worldVerts)
            {
                float dist = (v.x - x) * (v.x - x) + (v.z - z) * (v.z - z);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestHeight = v.y;
                }
            }
            
            return nearestHeight;
        }

        private LevelFloorData ExtractFloorDataFromTemplate(LevelTemplate template)
        {
            // Get dimensions
            Vector2 dims = template.size == GameLevelSize.Custom
                ? GameLevelDimensions.GetDimensionsInMeters(template.customSize)
                : GameLevelDimensions.GetDimensionsInMeters(template.size);
            
            float halfWidth = dims.x / 2f;
            float halfHeight = dims.y / 2f;
            
            // Local corners (before rotation)
            Vector3[] localCorners = new Vector3[4]
            {
                new Vector3(-halfWidth, 0, -halfHeight),
                new Vector3(halfWidth, 0, -halfHeight),
                new Vector3(halfWidth, 0, halfHeight),
                new Vector3(-halfWidth, 0, halfHeight)
            };
            
            // Transform to world space
            LevelFloorData data = new LevelFloorData
            {
                runtimeFloor = template.runtimePlane,
                sourceName = template.name,
                worldFootprint = new Vector2[4],
                cornerHeights = new float[4]
            };
            
            Vector2 centerSum = Vector2.zero;
            
            for (int i = 0; i < 4; i++)
            {
                Vector3 worldPos = template.transform.TransformPoint(localCorners[i]);
                data.worldFootprint[i] = new Vector2(worldPos.x, worldPos.z);
                data.cornerHeights[i] = worldPos.y;
                centerSum += data.worldFootprint[i];
            }
            
            data.center = centerSum / 4f;
            data.holePolygons.Add(data.worldFootprint);
            
            if (debugLog)
                Debug.Log($"[GapFiller] Extracted floor data from TEMPLATE for {template.name}: corners at {string.Join(", ", data.worldFootprint)}");
            
            return data;
        }

        private void GenerateMesh(Bounds bounds)
        {
            // Create runtime object
            runtimeMesh = new GameObject("GapFillerMesh");
            runtimeMesh.transform.SetParent(transform);
            runtimeMesh.transform.position = Vector3.zero;
            runtimeMesh.transform.rotation = Quaternion.identity;
            runtimeMesh.layer = LayerMask.NameToLayer("Level");
            runtimeMesh.tag = "Ground";
            
            MeshFilter mf = runtimeMesh.AddComponent<MeshFilter>();
            MeshRenderer mr = runtimeMesh.AddComponent<MeshRenderer>();
            MeshCollider mc = runtimeMesh.AddComponent<MeshCollider>();
            
            // Set material
            if (floorMaterial != null)
                mr.material = floorMaterial;
            else
                mr.material = Resources.Load<Material>("Materials/Occlusion");

            // Calculate grid dimensions
            float width = bounds.size.x;
            float height = bounds.size.z;
            float minX = bounds.min.x;
            float minZ = bounds.min.z;
            
            int gridX = Mathf.Max(1, Mathf.RoundToInt(width * verticesPerMeter));
            int gridY = Mathf.Max(1, Mathf.RoundToInt(height * verticesPerMeter));
            int vertCountX = gridX + 1;
            int vertCountY = gridY + 1;
            
            if (debugLog)
                Debug.Log($"[GapFiller] Generating {gridX}x{gridY} grid ({vertCountX * vertCountY} vertices)");

            List<LevelFloorData> holeFloors = levelFloors.FindAll(f => f.cutsHole);

            // Generate base grid vertices
            List<Vector3> verticesList = new List<Vector3>();
            List<Vector2> uvList = new List<Vector2>();
            
            // Track which grid indices map to which vertex list indices
            int[,] gridVertexIndices = new int[vertCountX, vertCountY];
            
            for (int y = 0; y < vertCountY; y++)
            {
                for (int x = 0; x < vertCountX; x++)
                {
                    float px = Mathf.Lerp(minX, minX + width, (float)x / gridX);
                    float pz = Mathf.Lerp(minZ, minZ + height, (float)y / gridY);
                    Vector2 point = new Vector2(px, pz);
                    
                    // Skip vertices inside level floors
                    bool insideFloor = false;
                    foreach (var floor in holeFloors)
                    {
                        if (floor.IsPointInHole(point))
                        {
                            insideFloor = true;
                            break;
                        }
                    }
                    
                    if (insideFloor)
                    {
                        gridVertexIndices[x, y] = -1; // Mark as invalid
                        continue;
                    }
                    
                    // Calculate height based on nearby floor edges
                    float py = CalculateVertexHeight(point);
                    
                    gridVertexIndices[x, y] = verticesList.Count;
                    verticesList.Add(new Vector3(px, py, pz));
                    uvList.Add(new Vector2((float)x / gridX, (float)y / gridY));
                }
            }
            
            int gridVertexCount = verticesList.Count;
            
            // Add edge vertices for each level floor
            // These will stitch the gap filler directly to the floor edges
            Dictionary<LevelFloorData, List<List<int>>> floorEdgeVertices = new Dictionary<LevelFloorData, List<List<int>>>();
            
            foreach (var floor in holeFloors)
            {
                List<List<int>> edgeLoops = new List<List<int>>();

                for (int polygonIndex = 0; polygonIndex < floor.holePolygons.Count; polygonIndex++)
                {
                    var holePolygon = floor.holePolygons[polygonIndex];
                    if (holePolygon == null || holePolygon.Length < 3)
                        continue;

                    List<int> edgeIndices = new List<int>();

                    // Add vertices along each edge of the hole polygon
                    for (int i = 0; i < holePolygon.Length; i++)
                    {
                        Vector2 edgeStart = holePolygon[i];
                        Vector2 edgeEnd = holePolygon[(i + 1) % holePolygon.Length];

                        float edgeLength = Vector2.Distance(edgeStart, edgeEnd);
                        int edgeSubdivisions = Mathf.Max(2, Mathf.RoundToInt(edgeLength * verticesPerMeter));

                        for (int j = 0; j <= edgeSubdivisions; j++)
                        {
                            float t = (float)j / edgeSubdivisions;
                            Vector2 edgePoint = Vector2.Lerp(edgeStart, edgeEnd, t);

                            float edgeHeight = GetFloorHeightAtPoint(floor, edgePoint);

                            int vertIndex = verticesList.Count;
                            verticesList.Add(new Vector3(edgePoint.x, edgeHeight, edgePoint.y));
                            uvList.Add(new Vector2(0.5f, 0.5f));
                            edgeIndices.Add(vertIndex);
                        }
                    }

                    edgeLoops.Add(edgeIndices);
                }

                floorEdgeVertices[floor] = edgeLoops;
            }
            
            Vector3[] vertices = verticesList.ToArray();
            Vector2[] uv = uvList.ToArray();

            // Generate triangles
            List<int> triangles = new List<int>();
            
            // Grid triangles (with holes cut out)
            for (int y = 0; y < gridY; y++)
            {
                for (int x = 0; x < gridX; x++)
                {
                    int i0 = gridVertexIndices[x, y];
                    int i1 = gridVertexIndices[x + 1, y];
                    int i2 = gridVertexIndices[x, y + 1];
                    int i3 = gridVertexIndices[x + 1, y + 1];
                    
                    // Skip if any vertex is inside a floor (marked as -1)
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0)
                        continue;
                    
                    // Additional check: skip triangles that cross floor boundaries
                    if (!TriangleCrossesFloor(vertices, i0, i2, i1) && !IsTriangleInsideAnyFloor(vertices, i0, i2, i1))
                    {
                        triangles.Add(i0);
                        triangles.Add(i2);
                        triangles.Add(i1);
                    }
                    
                    if (!TriangleCrossesFloor(vertices, i1, i2, i3) && !IsTriangleInsideAnyFloor(vertices, i1, i2, i3))
                    {
                        triangles.Add(i1);
                        triangles.Add(i2);
                        triangles.Add(i3);
                    }
                }
            }
            
            // Stitch edge vertices to nearby grid vertices
            foreach (var floor in holeFloors)
            {
                List<List<int>> edgeLoops = floorEdgeVertices[floor];

                for (int loopIndex = 0; loopIndex < edgeLoops.Count; loopIndex++)
                {
                    List<int> edgeIndices = edgeLoops[loopIndex];
                    if (edgeIndices == null || edgeIndices.Count < 2)
                        continue;

                    for (int i = 0; i < edgeIndices.Count; i++)
                    {
                        int edgeIdx = edgeIndices[i];
                        int nextEdgeIdx = edgeIndices[(i + 1) % edgeIndices.Count];

                        Vector3 edgeVert = vertices[edgeIdx];
                        Vector3 nextEdgeVert = vertices[nextEdgeIdx];

                        int nearestGridIdx = FindNearestGridVertex(vertices, gridVertexCount, edgeVert, floor);
                        int nextNearestGridIdx = FindNearestGridVertex(vertices, gridVertexCount, nextEdgeVert, floor);

                        if (nearestGridIdx >= 0 && nextNearestGridIdx >= 0)
                        {
                            Vector2 A = new Vector2(edgeVert.x, edgeVert.z);
                            Vector2 B = new Vector2(nextEdgeVert.x, nextEdgeVert.z);
                            Vector2 C = new Vector2(vertices[nearestGridIdx].x, vertices[nearestGridIdx].z);

                            if (!IsTriangleDegenerate(A, B, C) && !IsTriangleInsideAnyFloor(vertices, edgeIdx, nextEdgeIdx, nearestGridIdx))
                            {
                                if (Cross(B - A, C - A) > 0)
                                {
                                    triangles.Add(edgeIdx);
                                    triangles.Add(nextEdgeIdx);
                                    triangles.Add(nearestGridIdx);
                                }
                                else
                                {
                                    triangles.Add(edgeIdx);
                                    triangles.Add(nearestGridIdx);
                                    triangles.Add(nextEdgeIdx);
                                }
                            }

                            if (nextNearestGridIdx != nearestGridIdx)
                            {
                                Vector2 D = new Vector2(vertices[nextNearestGridIdx].x, vertices[nextNearestGridIdx].z);

                                if (!IsTriangleDegenerate(B, C, D) && !IsTriangleInsideAnyFloor(vertices, nextEdgeIdx, nearestGridIdx, nextNearestGridIdx))
                                {
                                    if (Cross(C - B, D - B) > 0)
                                    {
                                        triangles.Add(nextEdgeIdx);
                                        triangles.Add(nearestGridIdx);
                                        triangles.Add(nextNearestGridIdx);
                                    }
                                    else
                                    {
                                        triangles.Add(nextEdgeIdx);
                                        triangles.Add(nextNearestGridIdx);
                                        triangles.Add(nearestGridIdx);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Create mesh
            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.uv = uv;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;

            // Add NavMeshSurface
            var navSurface = runtimeMesh.AddComponent<NavMeshSurface>();
            navSurface.collectObjects = CollectObjects.Children;
            navSurface.layerMask = LayerMask.GetMask("Level");
            navSurface.BuildNavMesh();

            if (debugLog)
                Debug.Log($"[GapFiller] Generated mesh with {vertices.Length} vertices, {triangles.Count / 3} triangles");
        }

        private int FindNearestGridVertex(Vector3[] vertices, int gridVertexCount, Vector3 targetPos, LevelFloorData excludeFloor)
        {
            float nearestDist = float.MaxValue;
            int nearestIdx = -1;
            
            Vector2 target2D = new Vector2(targetPos.x, targetPos.z);
            
            for (int i = 0; i < gridVertexCount; i++)
            {
                Vector2 vert2D = new Vector2(vertices[i].x, vertices[i].z);
                
                // Skip if inside the floor we're stitching to
                if (excludeFloor.IsPointInHole(vert2D))
                    continue;
                
                float dist = Vector2.Distance(target2D, vert2D);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIdx = i;
                }
            }
            
            return nearestIdx;
        }

        private bool TriangleCrossesFloor(Vector3[] vertices, int a, int b, int c)
        {
            Vector2 A = new Vector2(vertices[a].x, vertices[a].z);
            Vector2 B = new Vector2(vertices[b].x, vertices[b].z);
            Vector2 C = new Vector2(vertices[c].x, vertices[c].z);
            
            foreach (var floor in levelFloors)
            {
                if (!floor.cutsHole) continue;
                for (int i = 0; i < floor.holePolygons.Count; i++)
                {
                    var holePolygon = floor.holePolygons[i];
                    if (holePolygon == null || holePolygon.Length < 3)
                        continue;

                    if (SegmentIntersectsPolygon(A, B, holePolygon)) return true;
                    if (SegmentIntersectsPolygon(B, C, holePolygon)) return true;
                    if (SegmentIntersectsPolygon(C, A, holePolygon)) return true;
                }
            }
            
            return false;
        }

        private bool IsTriangleInsideAnyFloor(Vector3[] vertices, int a, int b, int c)
        {
            Vector2 A = new Vector2(vertices[a].x, vertices[a].z);
            Vector2 B = new Vector2(vertices[b].x, vertices[b].z);
            Vector2 C = new Vector2(vertices[c].x, vertices[c].z);
            Vector2 center = (A + B + C) / 3f;
            
            foreach (var floor in levelFloors)
            {
                if (!floor.cutsHole) continue;
                if (floor.IsPointInHole(center))
                    return true;
            }
            
            return false;
        }

        private bool IsTriangleDegenerate(Vector2 a, Vector2 b, Vector2 c)
        {
            float area = Mathf.Abs(Cross(b - a, c - a)) / 2f;
            return area < 0.0001f;
        }

        private float CalculateVertexHeight(Vector2 point)
        {
            // Check if point is inside any level floor (shouldn't happen if holes are cut, but just in case)
            foreach (var floor in levelFloors)
            {
                if (floor.ContainsPoint(point))
                    return floor.GetHeightAtPoint(point);
            }
            
            // Find the closest edge point from all floors
            float closestDist = float.MaxValue;
            float closestHeight = 0f;
            LevelFloorData closestFloor = null;
            Vector2 closestEdgePoint = point;
            
            // Also track all nearby floors for blending
            List<(float dist, float height, float weight)> nearbyEdges = new List<(float, float, float)>();
            
            foreach (var floor in levelFloors)
            {
                Vector2 edgePoint;
                float dist = floor.GetDistanceToEdge(point, out edgePoint);
                
                // Get actual height at this edge point by raycasting onto the floor mesh
                float edgeHeight = GetFloorHeightAtPoint(floor, edgePoint);
                
                nearbyEdges.Add((dist, edgeHeight, 0f));
                
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestHeight = edgeHeight;
                    closestFloor = floor;
                    closestEdgePoint = edgePoint;
                }
            }
            
            // If very close to an edge, just use that height
            if (closestDist < 0.05f)
                return closestHeight;
            
            // Blend heights from all nearby floors based on inverse distance
            float totalWeight = 0f;
            float weightedHeight = 0f;
            
            foreach (var (dist, height, _) in nearbyEdges)
            {
                // Use inverse distance squared for smoother blending
                float weight = 1f / (dist * dist + 0.01f);
                totalWeight += weight;
                weightedHeight += height * weight;
            }
            
            if (totalWeight > 0f)
                return weightedHeight / totalWeight;
            
            // Fallback - shouldn't normally reach here
            return closestHeight;
        }

        private float GetFloorHeightAtPoint(LevelFloorData floor, Vector2 xzPoint)
        {
            // Try to raycast onto the actual floor mesh for accurate height
            if (floor.runtimeFloor != null)
            {
                MeshCollider mc = floor.runtimeFloor.GetComponent<MeshCollider>();
                if (mc != null)
                {
                    // Cast ray down from above
                    Ray ray = new Ray(new Vector3(xzPoint.x, 100f, xzPoint.y), Vector3.down);
                    RaycastHit hit;
                    
                    if (mc.Raycast(ray, out hit, 200f))
                    {
                        return hit.point.y;
                    }
                    
                    // If direct hit fails, try casting from nearby points on the edge
                    // The point might be exactly on the edge where raycast misses
                    Vector2 toCenter = (floor.center - xzPoint).normalized * 0.1f;
                    Vector3 offsetPoint = new Vector3(xzPoint.x + toCenter.x, 100f, xzPoint.y + toCenter.y);
                    ray = new Ray(offsetPoint, Vector3.down);
                    
                    if (mc.Raycast(ray, out hit, 200f))
                    {
                        return hit.point.y;
                    }
                }
            }
            
            // Fallback to interpolated corner heights
            return floor.GetHeightAtEdgePoint(xzPoint);
        }

        private void TryAddTriangle(List<int> triangles, Vector3[] vertices, int a, int b, int c)
        {
            Vector2 A = new Vector2(vertices[a].x, vertices[a].z);
            Vector2 B = new Vector2(vertices[b].x, vertices[b].z);
            Vector2 C = new Vector2(vertices[c].x, vertices[c].z);
            Vector2 center = (A + B + C) / 3f;
            
            // Check if triangle center is inside any level floor (hole)
            foreach (var floor in levelFloors)
            {
                if (!floor.cutsHole) continue;
                // Skip if center is inside the floor
                if (floor.IsPointInHole(center))
                    return;
                
                // Skip if any vertex is inside the floor
                if (floor.IsPointInHole(A) || floor.IsPointInHole(B) || floor.IsPointInHole(C))
                    return;
                
                // Skip if any edge intersects the floor polygon
                for (int i = 0; i < floor.holePolygons.Count; i++)
                {
                    var holePolygon = floor.holePolygons[i];
                    if (holePolygon == null || holePolygon.Length < 3)
                        continue;

                    if (SegmentIntersectsPolygon(A, B, holePolygon)) return;
                    if (SegmentIntersectsPolygon(B, C, holePolygon)) return;
                    if (SegmentIntersectsPolygon(C, A, holePolygon)) return;
                }
            }
            
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        private bool SegmentIntersectsPolygon(Vector2 a, Vector2 b, Vector2[] poly)
        {
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 c = poly[i];
                Vector2 d = poly[(i + 1) % poly.Length];
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

        /// <summary>
        /// Build mode can create floor occlusion artifacts on iOS.
        /// Hide gap filler in build mode; regenerate/show in play mode.
        /// </summary>
        public void SetGapFillerVisibilityForMode(bool isBuildMode)
        {
            if (isBuildMode)
            {
                if (runtimeMesh != null)
                    runtimeMesh.SetActive(false);
                return;
            }

            // Always regenerate on play to pick up any template moves done in build mode.
            regeneratePending = false;
            GenerateGapFillerMesh();

            if (runtimeMesh != null && !runtimeMesh.activeSelf)
                runtimeMesh.SetActive(true);
        }

        public void ClearMesh()
        {
            if (runtimeMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(runtimeMesh);
                else
                    DestroyImmediate(runtimeMesh);
                    
                runtimeMesh = null;
            }
            
            levelFloors.Clear();
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!showGizmos || levelFloors == null) return;
            
            // Draw floor footprints
            Gizmos.color = Color.cyan;
            foreach (var floor in levelFloors)
            {
                for (int i = 0; i < floor.worldFootprint.Length; i++)
                {
                    Vector2 a = floor.worldFootprint[i];
                    Vector2 b = floor.worldFootprint[(i + 1) % floor.worldFootprint.Length];
                    
                    Vector3 worldA = new Vector3(a.x, floor.cornerHeights[i], a.y);
                    Vector3 worldB = new Vector3(b.x, floor.cornerHeights[(i + 1) % 4], b.y);
                    
                    Gizmos.DrawLine(worldA, worldB);
                }
            }
        }
#endif
    }
}
