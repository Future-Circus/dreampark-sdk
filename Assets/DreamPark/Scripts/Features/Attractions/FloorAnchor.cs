// FloorAnchor v2 - Added precalculated bounds support
namespace DreamPark {
    using UnityEngine;
#if UNITY_EDITOR
    using UnityEditor;

    [CustomEditor(typeof(FloorAnchor))]
    public class FloorAnchorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            FloorAnchor anchor = (FloorAnchor)target;
            DrawDefaultInspector();
            
            EditorGUILayout.Space(10);
            
            if (GUILayout.Button("Recalculate Bounds"))
            {
                Undo.RecordObject(anchor, "Recalculate Bounds");
                anchor.PrecalculateBounds();
            }
            
            if (anchor.HasPrecalculatedBounds())
            {
                EditorGUILayout.HelpBox($"Bounds Size: {anchor.GetPrecalculatedSize():F2}", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Bounds not calculated. Click 'Recalculate Bounds'.", MessageType.Warning);
            }
        }

        private void OnSceneGUI()
        {
            FloorAnchor anchor = (FloorAnchor)target;
            
            if (!anchor.HasPrecalculatedBounds()) return;
            
            Vector3[] boundPoints = anchor.GetPrecalculatedCornersWorld();
            if (boundPoints == null || boundPoints.Length < 8) return;
            
            Handles.color = Color.yellow;
            
            // Draw bottom square
            Handles.DrawLine(boundPoints[0], boundPoints[1]);
            Handles.DrawLine(boundPoints[1], boundPoints[2]);
            Handles.DrawLine(boundPoints[2], boundPoints[3]);
            Handles.DrawLine(boundPoints[3], boundPoints[0]);
            
            // Draw top square
            Handles.DrawLine(boundPoints[4], boundPoints[5]);
            Handles.DrawLine(boundPoints[5], boundPoints[6]);
            Handles.DrawLine(boundPoints[6], boundPoints[7]);
            Handles.DrawLine(boundPoints[7], boundPoints[4]);
            
            // Draw vertical lines
            Handles.DrawLine(boundPoints[0], boundPoints[4]);
            Handles.DrawLine(boundPoints[1], boundPoints[5]);
            Handles.DrawLine(boundPoints[2], boundPoints[6]);
            Handles.DrawLine(boundPoints[3], boundPoints[7]);
        }
    }
#endif

    [ExecuteAlways]
    public class FloorAnchor : MonoBehaviour
    {
        [Header("References")]
        public MeshFilter floorMeshFilter;
        public Transform floorTransform;
        public CalibrateLevel calibrator;

        [Header("Settings")]
        public Vector3 localOffset;
        public bool autoFindFloor = true;
        public bool matchGrade = false;

        [Header("Multi-Point Settings")]
        public bool debugDrawBounds = false;
        public bool debugLogValues = false;
        public float fixedHeightOffset = 0f;
        public bool useFixedHeight = false;
        
        [Header("Bounds Settings")]
        public string excludeLayerName = "Gizmo";
        public bool includeInactiveRenderers = false;
        
        [Header("Outlier Filtering")]
        [Tooltip("Max height difference from median before a corner is excluded (e.g., table/wall)")]
        public float maxHeightDeviation = 0.3f;
        [Tooltip("Max distance to nearest floor vertex before corner is considered off-grid")]
        public float maxVertexDistance = 1.0f;
        [Tooltip("Enable to see which corners are being filtered")]
        public bool debugOutlierFiltering = false;
        
        // Precalculated bounds (8 corners like LevelMap)
        [SerializeField, HideInInspector] private Vector3[] precalculatedCorners = new Vector3[8];
        [SerializeField, HideInInspector] private bool boundsPrecalculated = false;

        // Single point tracking
        private int closestVertexIndex = -1;

        // ── The authored sink depth, captured ONCE ───────────────────────
        //  centerLocalOffset is recomputed on every recache as
        //  (object position - floor vertex) — it measures a position THIS
        //  COMPONENT set last cycle. Place the object slightly wrong, recache,
        //  and the wrong position becomes the new intended offset, so the next
        //  placement compounds it. The vertical relationship between an
        //  anchored prop and its floor is an AUTHORING decision, not a runtime
        //  measurement: read once, from the first cache (before any placement
        //  has run, while the floor is still the flat authored grid), never
        //  revised.
        private bool authoredVerticalCaptured = false;
        private float authoredVerticalOffset = 0f;
        
        // Multi-point tracking (4 corners + center)
        private int[] cornerVertexIndices = new int[4] { -1, -1, -1, -1 };
        private Vector3[] cornerLocalOffsets = new Vector3[4];
        private float[] cornerVertexDistances = new float[4]; // Track distance to nearest vertex for outlier detection
        private int centerVertexIndex = -1;
        private Vector3 centerLocalOffset;
        private Bounds cachedBounds;
        
        private Mesh floorMesh;
        private int lastVertexCount = -1;
        private bool firstUpdate = false;

        [ContextMenu("Recache Corners")]
        public void RecacheCorners()
        {
            if (matchGrade)
            {
                FindFloor();
                CacheCornerVertices();
                Debug.Log($"[FloorAnchor] Recached - Center offset: {centerLocalOffset}, Corner offsets: {string.Join(", ", cornerLocalOffsets)}");
            }
        }
        
        [ContextMenu("Precalculate Bounds")]
        public void PrecalculateBounds()
        {
#if UNITY_EDITOR
            if (Application.isPlaying) return;
            
            if (precalculatedCorners == null || precalculatedCorners.Length != 8)
                precalculatedCorners = new Vector3[8];
            
            Renderer[] renderers = includeInactiveRenderers 
                ? GetComponentsInChildren<Renderer>(true) 
                : GetComponentsInChildren<Renderer>();
            
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"[FloorAnchor] No renderers found on {gameObject.name}");
                boundsPrecalculated = false;
                return;
            }
            
            Bounds bounds = new Bounds();
            bool initialized = false;
            int excludeLayer = string.IsNullOrEmpty(excludeLayerName) ? -1 : LayerMask.NameToLayer(excludeLayerName);
            
            foreach (var r in renderers)
            {
                if (excludeLayer >= 0 && r.gameObject.layer == excludeLayer)
                    continue;
                
                if (!initialized)
                {
                    bounds = new Bounds(r.bounds.center, r.bounds.size);
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            
            if (!initialized)
            {
                Debug.LogWarning($"[FloorAnchor] No valid renderers found on {gameObject.name}");
                boundsPrecalculated = false;
                return;
            }
            
            // After calculating the world-space bounds, convert to local space relative to THIS object's position
            Vector3 localCenter = transform.InverseTransformPoint(bounds.center);
            Vector3 localExtents = bounds.extents; // Half the size
            Quaternion invRotation = Quaternion.Inverse(transform.rotation);
            localExtents = invRotation * localExtents;

            // Now calculate corners relative to the local center
            precalculatedCorners[0] = localCenter + new Vector3(-localExtents.x, -localExtents.y, -localExtents.z);
            precalculatedCorners[1] = localCenter + new Vector3(localExtents.x, -localExtents.y, -localExtents.z);
            precalculatedCorners[2] = localCenter + new Vector3(localExtents.x, -localExtents.y, localExtents.z);
            precalculatedCorners[3] = localCenter + new Vector3(-localExtents.x, -localExtents.y, localExtents.z);
            precalculatedCorners[4] = localCenter + new Vector3(-localExtents.x, localExtents.y, -localExtents.z);
            precalculatedCorners[5] = localCenter + new Vector3(localExtents.x, localExtents.y, -localExtents.z);
            precalculatedCorners[6] = localCenter + new Vector3(localExtents.x, localExtents.y, localExtents.z);
            precalculatedCorners[7] = localCenter + new Vector3(-localExtents.x, localExtents.y, localExtents.z);
            
            boundsPrecalculated = true;
            EditorUtility.SetDirty(this);
            Debug.Log($"[FloorAnchor] Bounds precalculated for {gameObject.name}: Size = {GetPrecalculatedSize()}");
#endif
        }
        
        public bool HasPrecalculatedBounds() => boundsPrecalculated && precalculatedCorners != null && precalculatedCorners.Length == 8;
        
        public Vector3 GetPrecalculatedSize()
        {
            if (!HasPrecalculatedBounds()) return Vector3.zero;
            return new Vector3(
                Mathf.Abs(precalculatedCorners[1].x - precalculatedCorners[0].x),
                Mathf.Abs(precalculatedCorners[4].y - precalculatedCorners[0].y),
                Mathf.Abs(precalculatedCorners[2].z - precalculatedCorners[1].z)
            );
        }
        
        public Vector3 GetCenter()
        {
            if (!HasPrecalculatedBounds()) return transform.position;
            Vector3 center = Vector3.zero;
            foreach (var corner in precalculatedCorners)
                center += corner;
            return transform.TransformPoint(center / 8f);
        }
        
        public Vector3[] GetPrecalculatedCornersWorld()
        {
            if (!HasPrecalculatedBounds()) return null;
            Vector3[] worldCorners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                worldCorners[i] = transform.TransformPoint(precalculatedCorners[i]);
            return worldCorners;
        }
        
        public Vector3[] GetPrecalculatedFloorCornersWorld()
        {
            if (!HasPrecalculatedBounds()) return null;
            Vector3[] floorCorners = new Vector3[4];
            for (int i = 0; i < 4; i++)
                floorCorners[i] = transform.TransformPoint(precalculatedCorners[i]);
            return floorCorners;
        }

        void Awake()
        {
            // Do nothing - will use Start
        }

        void Start()
        {
            FindFloor();

            if (floorMesh == null)
            {
                StartCoroutine(RetryFindFloor());
            }

            // Subscribe to calibration changes
            LevelTemplate.OnAnyLevelTemplateChanged += OnCalibrationChanged;
        }

        void OnDestroy()
        {
            LevelTemplate.OnAnyLevelTemplateChanged -= OnCalibrationChanged;
        }

        void OnCalibrationChanged()
        {
            // Reset firstUpdate so we reposition when calibration data is applied
            firstUpdate = false;

            // Only re-cache if NOT actively calibrating
            // During calibration, the object has already moved to follow the floor,
            // so recaching would capture the elevated position and cause runaway drift
            if (calibrator != null && calibrator.isCalibrating)
            {
                if (debugLogValues) Debug.Log("[FloorAnchor] Skipping recache during active calibration");
                return;
            }

            // Re-cache vertex indices since floor geometry may have changed
            if (floorMesh != null)
            {
                if (matchGrade)
                    CacheCornerVertices();
                else
                    CacheClosestVertex();
            }
        }

        System.Collections.IEnumerator RetryFindFloor()
        {
            while (floorMesh == null)
            {
                yield return null;
                FindFloor();
            }
            
            Debug.Log($"[FloorAnchor] Floor found and cached!");
        }

        void FindFloor()
        {
            if (autoFindFloor)
            {
                if (floorMeshFilter == null)
                    floorMeshFilter = FindClosestFloorMesh();
                if (floorMeshFilter != null)
                    floorTransform = floorMeshFilter.transform;
            }

            if (floorMeshFilter != null)
            {
                floorMesh = floorMeshFilter.sharedMesh;
                lastVertexCount = floorMesh != null ? floorMesh.vertexCount : -1;
                
                if (matchGrade)
                    CacheCornerVertices();
                else
                    CacheClosestVertex();
            }
        }

        public void Update()
        {
            if (calibrator == null) {
                if (debugLogValues) Debug.Log("[FloorAnchor] No calibrator");
                var levelTemplate = GetComponentInParent<LevelTemplate>();
                if (levelTemplate != null && levelTemplate.runtimePlane != null)
                {
                    calibrator = levelTemplate.runtimePlane.GetComponent<CalibrateLevel>();
                }
                return;
            }
            if (!calibrator.calibrated) {
                if (debugLogValues) Debug.Log("[FloorAnchor] Not calibrated");
                return;
            }
            // Run update if: first time seeing calibrated data OR actively calibrating
            // firstUpdate=false means we haven't positioned yet, so we SHOULD run
            if (firstUpdate && !calibrator.isCalibrating) {
                if (debugLogValues) Debug.Log("[FloorAnchor] Already positioned and not calibrating");
                return;
            }
            
            if (debugLogValues) Debug.Log($"[FloorAnchor] Update called - matchGrade: {matchGrade}");
            
            if (floorMeshFilter == null || floorMeshFilter.sharedMesh == null)
            {
                FindFloor();
                return;
            }

            if (floorMesh != floorMeshFilter.sharedMesh ||
                floorMesh.vertexCount != lastVertexCount)
            {
                floorMesh = floorMeshFilter.sharedMesh;
                lastVertexCount = floorMesh.vertexCount;

                // Only recache if NOT actively calibrating to avoid runaway drift
                // During calibration, the object has already moved, so recaching
                // would capture the elevated position and double-count the offset
                if (!calibrator.isCalibrating)
                {
                    if (matchGrade)
                        CacheCornerVertices();
                    else
                        CacheClosestVertex();
                }
            }

            if (matchGrade)
            {
                UpdateMultiPoint();
            }
            else
            {
                UpdateSinglePoint();
            }
        }

        void UpdateSinglePoint()
        {
            if (closestVertexIndex < 0 || floorMesh == null)
                return;

            Vector3[] verts = floorMesh.vertices;
            if (closestVertexIndex >= verts.Length)
            {
                CacheClosestVertex();
                return;
            }

            Vector3 vertexLocal = verts[closestVertexIndex];
            Vector3 vertexWorld = floorTransform.TransformPoint(vertexLocal);
            Vector3 targetWorld = vertexWorld + floorTransform.TransformVector(localOffset);
            Vector3 targetLocal = floorTransform.parent.InverseTransformPoint(targetWorld);

            if (!firstUpdate) {
                transform.localPosition = targetLocal;
                firstUpdate = true;
            } else {
                transform.localPosition = Vector3.Lerp(transform.localPosition, targetLocal, Time.deltaTime * 10f);
            }
        }

        /// <summary>
        /// Filters out corners that are outliers (too high compared to others, or too far from floor vertices).
        /// Returns indices of valid corners (0-3) and their world positions.
        /// </summary>
        (int[] validIndices, Vector3[] validPositions) GetValidCorners(Vector3[] verts)
        {
            Vector3[] floorVertices = new Vector3[4];
            float[] heights = new float[4];
            
            for (int i = 0; i < 4; i++)
            {
                floorVertices[i] = floorTransform.TransformPoint(verts[cornerVertexIndices[i]]);
                heights[i] = floorVertices[i].y;
            }
            
            // ── Outliers are measured against the best-fit PLANE ─────────
            //
            //  This used to compare each corner's height to the MEDIAN of the
            //  four and reject anything more than maxHeightDeviation (0.3m)
            //  away. That test contradicts the feature it serves. Match-grade
            //  exists precisely because the corners sit at DIFFERENT heights —
            //  that spread IS the grade — so on any real slope the uphill and
            //  downhill corners are outliers by definition. A 3.9m-long pit on
            //  a 10 degree grade spans 0.69m corner to corner, so two of its
            //  four corners get thrown out for doing exactly what they are
            //  there to do. Fewer than three survive, the plane cannot be
            //  fitted, and the whole thing collapses to a fallback. Flat
            //  ground hid it completely, which is why this only ever shows up
            //  on a slope.
            //
            //  A graded floor is a PLANE, so residual-from-plane is the
            //  meaningful measure. All four corners of a properly conformed
            //  pit lie near one plane however steep it is; a genuinely bad
            //  sample — a rim vertex flung somewhere odd — does not.
            //
            //  The plane is chosen by trying each triple and keeping the one
            //  whose excluded corner fits best. With four near-coplanar points
            //  every triple agrees; with one bad point, the triple that
            //  excludes it wins, so the bad point is the only large residual.
            float[] residuals = ComputePlaneResiduals(floorVertices);
            
            // ── Filter corners, DEGRADING RATHER THAN FAILING ──────────────
            //
            //  Strict first, then height-only, then everything. The old code
            //  had only the strict tier and no fallback, so when it rejected
            //  every corner the caller silently reverted to positioning from
            //  one cached vertex — which is the behaviour the corner plane
            //  exists to replace.
            //
            //  The distance test is the one that has to yield, and a
            //  FloorCutout is exactly why. Corners are taken from the object's
            //  full bounds, so a 2.3m x 3.9m pit samples nearly 2m out from
            //  its own centre; and every floor vertex inside a cutout has been
            //  PUSHED OUT to the rim. The nearest surviving vertex is then far
            //  past maxVertexDistance (1m) essentially by construction. For an
            //  object sitting over a hole a large distance is not evidence of
            //  a bad sample — it is what a hole IS, and the rim is precisely
            //  the surface the object should sit flush with.
            //
            //  The height test keeps its authority through the second tier,
            //  because a corner that disagrees vertically really is suspect
            //  regardless of how far away it sits.
            var strict = FilterCorners(residuals, true, true);
            if (strict.Count >= 3) return Emit(strict, floorVertices, "strict");

            var heightOnly = FilterCorners(residuals, true, false);
            if (heightOnly.Count >= 3) return Emit(heightOnly, floorVertices, "distance test relaxed");

            var all = FilterCorners(residuals, false, false);
            return Emit(all, floorVertices, "all corners");
        }

        /// <summary>
        /// Least-squares plane y = a*x + b*z + c through the given points, or
        /// null when they are degenerate (collinear / coincident in XZ).
        /// </summary>
        static bool TryFitPlane(Vector3[] pts, int count, out float a, out float b, out float c)
        {
            a = b = c = 0f;
            if (count < 3) return false;

            double Sxx=0, Sxz=0, Szz=0, Sx=0, Sz=0, S1=0, Sxy=0, Szy=0, Sy=0;
            for (int i = 0; i < count; i++)
            {
                double x = pts[i].x, y = pts[i].y, z = pts[i].z;
                Sxx += x*x; Sxz += x*z; Szz += z*z; Sx += x; Sz += z; S1 += 1;
                Sxy += x*y; Szy += z*y; Sy += y;
            }

            double det =
                Sxx*(Szz*S1 - Sz*Sz) - Sxz*(Sxz*S1 - Sz*Sx) + Sx*(Sxz*Sz - Szz*Sx);
            if (System.Math.Abs(det) < 1e-9) return false;

            double da =
                Sxy*(Szz*S1 - Sz*Sz) - Sxz*(Szy*S1 - Sz*Sy) + Sx*(Szy*Sz - Szz*Sy);
            double db =
                Sxx*(Szy*S1 - Sy*Sz) - Sxy*(Sxz*S1 - Sz*Sx) + Sx*(Sxz*Sy - Szy*Sx);
            double dc =
                Sxx*(Szz*Sy - Szy*Sz) - Sxz*(Sxz*Sy - Szy*Sx) + Sxy*(Sxz*Sz - Szz*Sx);

            a = (float)(da/det); b = (float)(db/det); c = (float)(dc/det);
            return true;
        }

        /// <summary>
        /// Vertical distance of each corner from the least-squares plane through
        /// ALL of them.
        ///
        /// Least squares rather than anything cleverer, deliberately. The four
        /// corners of a rectangle are affinely dependent (y3 = y1 + y2 - y0),
        /// so "corner 1 is 1m high" and "corner 3 is 1m low" are the SAME
        /// observation — a single bad corner is mathematically unidentifiable
        /// from four samples. Any scheme that claims to name the culprit is
        /// resolving that tie arbitrarily, and half the time it drops a good
        /// corner and keeps the bad one, tilting the plane it was supposed to
        /// protect. Least squares spreads the disagreement evenly (a 1m error
        /// becomes 0.25m on each corner), never picks wrong, and leaves the
        /// centre off by a quarter of the error instead of all of it.
        ///
        /// What it is still good at is the case that matters: a corner that is
        /// grossly wrong lands far enough outside maxHeightDeviation to be cut,
        /// while every corner of a floor on a genuine SLOPE has a residual of
        /// zero however steep it is — which is the whole reason this replaced
        /// deviation-from-median.
        /// </summary>
        float[] ComputePlaneResiduals(Vector3[] c)
        {
            var r = new float[4];
            if (!TryFitPlane(c, 4, out float a, out float b, out float cc)) return r;
            for (int i = 0; i < 4; i++)
            {
                r[i] = Mathf.Abs(c[i].y - (a * c[i].x + b * c[i].z + cc));
            }
            return r;
        }

        System.Collections.Generic.List<int> FilterCorners(
            float[] residuals, bool applyHeight, bool applyDistance)
        {
            var kept = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 4; i++)
            {
                if (applyHeight && residuals[i] > maxHeightDeviation) continue;
                if (applyDistance && cornerVertexDistances[i] > maxVertexDistance) continue;
                kept.Add(i);
            }
            return kept;
        }

        (int[] validIndices, Vector3[] validPositions) Emit(
            System.Collections.Generic.List<int> kept, Vector3[] floorVertices, string tier)
        {
            var positions = new Vector3[kept.Count];
            for (int i = 0; i < kept.Count; i++) positions[i] = floorVertices[kept[i]];

            if (debugOutlierFiltering)
            {
                Debug.Log($"[FloorAnchor] Using {kept.Count}/4 corners for grade calculation ({tier})");
            }
            return (kept.ToArray(), positions);
        }

        /// <summary>
        /// Calculates rotation from valid corners. Handles 2, 3, or 4 corner cases.
        /// </summary>
        Quaternion CalculateGradeRotation(int[] validIndices, Vector3[] validPositions)
        {
            if (validPositions.Length < 2)
            {
                // Not enough points - return current rotation
                if (debugOutlierFiltering)
                    Debug.LogWarning("[FloorAnchor] Less than 2 valid corners - cannot calculate grade");
                return transform.rotation;
            }
            
            Vector3 up;
            Vector3 forward;
            
            if (validPositions.Length >= 3)
            {
                // 3+ points: calculate plane normal
                Vector3 edge1 = validPositions[1] - validPositions[0];
                Vector3 edge2 = validPositions[2] - validPositions[0];
                up = Vector3.Cross(edge1, edge2).normalized;
                
                if (up.y < 0)
                    up = -up;
                
                // Calculate forward from the valid corners we have
                forward = CalculateForwardFromValidCorners(validIndices, validPositions, up);
            }
            else // exactly 2 points
            {
                // 2 points: estimate tilt along one axis, keep other axis level
                Vector3 edge = validPositions[1] - validPositions[0];
                Vector3 horizontal = new Vector3(edge.x, 0, edge.z).normalized;
                float tiltAngle = Mathf.Atan2(edge.y, new Vector2(edge.x, edge.z).magnitude) * Mathf.Rad2Deg;
                
                // Determine if this edge is more front-back or left-right based on corner indices
                bool isFrontBackEdge = (validIndices[0] <= 1 && validIndices[1] >= 2) || (validIndices[1] <= 1 && validIndices[0] >= 2);
                
                if (isFrontBackEdge)
                {
                    // Tilt forward/back
                    up = Quaternion.AngleAxis(-tiltAngle, Vector3.Cross(Vector3.up, horizontal)) * Vector3.up;
                    forward = horizontal;
                }
                else
                {
                    // Tilt left/right - keep forward as current forward projected
                    Vector3 right = horizontal;
                    up = Quaternion.AngleAxis(tiltAngle, right) * Vector3.up;
                    forward = Vector3.Cross(right, up).normalized;
                }
                
                if (debugOutlierFiltering)
                    Debug.Log($"[FloorAnchor] 2-corner fallback: edge tilt={tiltAngle:F1}°, isFrontBack={isFrontBackEdge}");
            }
            
            forward = Vector3.ProjectOnPlane(forward, up).normalized;
            return Quaternion.LookRotation(forward, up);
        }
        
        Vector3 CalculateForwardFromValidCorners(int[] validIndices, Vector3[] validPositions, Vector3 up)
        {
            // Corner layout: 0=front-left, 1=front-right, 2=back-right, 3=back-left
            // Try to find front and back centers from available corners
            
            System.Collections.Generic.List<Vector3> frontPoints = new System.Collections.Generic.List<Vector3>();
            System.Collections.Generic.List<Vector3> backPoints = new System.Collections.Generic.List<Vector3>();
            
            for (int i = 0; i < validIndices.Length; i++)
            {
                int cornerIdx = validIndices[i];
                if (cornerIdx <= 1) // front corners (0, 1)
                    frontPoints.Add(validPositions[i]);
                else // back corners (2, 3)
                    backPoints.Add(validPositions[i]);
            }
            
            Vector3 forward;
            
            if (frontPoints.Count > 0 && backPoints.Count > 0)
            {
                // We have at least one front and one back point
                Vector3 frontCenter = Vector3.zero;
                foreach (var p in frontPoints) frontCenter += p;
                frontCenter /= frontPoints.Count;
                
                Vector3 backCenter = Vector3.zero;
                foreach (var p in backPoints) backCenter += p;
                backCenter /= backPoints.Count;
                
                forward = (backCenter - frontCenter).normalized;
            }
            else
            {
                // All points on one side - use object's current forward projected onto plane
                forward = Vector3.ProjectOnPlane(transform.forward, up).normalized;
                if (forward.sqrMagnitude < 0.001f)
                    forward = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            }
            
            return forward;
        }

        void UpdateMultiPoint()
        {
            if (floorMesh == null) return;
            
            if (centerVertexIndex < 0 || centerVertexIndex >= floorMesh.vertexCount)
            {
                CacheCornerVertices();
                return;
            }
            
            for (int i = 0; i < 4; i++)
            {
                if (cornerVertexIndices[i] < 0 || cornerVertexIndices[i] >= floorMesh.vertexCount)
                {
                    CacheCornerVertices();
                    return;
                }
            }

            Vector3[] verts = floorMesh.vertices;
            
            Vector3 centerVertexLocal = verts[centerVertexIndex];
            Vector3 centerVertexWorld = floorTransform.TransformPoint(centerVertexLocal);

            // Get valid corners (filtered for outliers)
            var (validIndices, validPositions) = GetValidCorners(verts);

            // ── Height comes from the SAME plane the grade is fitted to ──
            //
            //  This used to rigidly track ONE cached vertex:
            //
            //      target = verts[centerVertexIndex] + centerLocalOffset
            //
            //  so the object translated, in XZ as well as Y, by whatever that
            //  single vertex did. Rotation meanwhile came from a plane fitted
            //  through four corner vertices WITH outlier rejection. Two
            //  different answers to "where is the floor", and nothing
            //  reconciling them: an object could be tilted to its corners
            //  while sitting at the height of one unrelated vertex.
            //
            //  It is worst by far for anything over a FloorCutout — a lava
            //  pit, a pool. centerVertexIndex is picked as the nearest vertex
            //  to the object's centre, but every vertex under a cutout has
            //  been PUSHED OUT of the hole by the hole-cutting pass. The
            //  object therefore latches onto a rim vertex that has since been
            //  displaced, and inherits whatever terrain height that vertex
            //  ended up at. The cutout polygon is authored geometry and stays
            //  put, which is why the hole looks right while the pit floats
            //  above it.
            //
            //  Fitting the height to the corner plane fixes both halves:
            //  position and rotation now describe the same surface, and for a
            //  cutout object the rim corners are genuinely what it should sit
            //  flush with. XZ is left alone — an anchored prop belongs where
            //  the creator placed it and should only ride the floor
            //  vertically.
            // Authored depth, NOT the live delta — see authoredVerticalOffset.
            float verticalOffset = floorTransform.TransformVector(
                new Vector3(0f, authoredVerticalOffset, 0f)).y;
            Vector3 anchorHere = transform.position;
            Vector3 targetPositionWorld = centerVertexWorld + floorTransform.TransformVector(centerLocalOffset);

            if (validPositions.Length >= 3)
            {
                // Fitted through EVERY kept corner, not an arbitrary three of
                // them — with four corners available, ignoring one throws away
                // a quarter of the evidence for no reason and makes the result
                // depend on which corner happened to be first.
                if (TryFitPlane(validPositions, validPositions.Length,
                                out float pa, out float pb, out float pc))
                {
                    float planeY = pa * anchorHere.x + pb * anchorHere.z + pc;
                    targetPositionWorld = new Vector3(anchorHere.x, planeY + verticalOffset, anchorHere.z);
                }
            }
            else if (validPositions.Length == 2)
            {
                // Two corners define a line, not a plane. Their mean height is
                // the honest answer — better than one arbitrary vertex, and it
                // matches what CalculateGradeRotation does with two points.
                float meanY = (validPositions[0].y + validPositions[1].y) * 0.5f;
                targetPositionWorld = new Vector3(anchorHere.x, meanY + verticalOffset, anchorHere.z);
            }
            
            Quaternion targetRotationWorld;
            
            if (validIndices.Length >= 2)
            {
                // Calculate rotation from valid corners
                targetRotationWorld = CalculateGradeRotation(validIndices, validPositions);
            }
            else
            {
                // Not enough valid corners - fall back to using all corners (original behavior)
                Vector3[] floorVertices = new Vector3[4];
                for (int i = 0; i < 4; i++)
                {
                    floorVertices[i] = floorTransform.TransformPoint(verts[cornerVertexIndices[i]]);
                }
                
                Vector3 edge1 = floorVertices[1] - floorVertices[0];
                Vector3 edge2 = floorVertices[2] - floorVertices[0];
                Vector3 up = Vector3.Cross(edge1, edge2).normalized;
                
                if (up.y < 0)
                    up = -up;

                Vector3 frontCenter = (floorVertices[0] + floorVertices[1]) / 2f;
                Vector3 backCenter = (floorVertices[2] + floorVertices[3]) / 2f;
                Vector3 forward = (backCenter - frontCenter).normalized;
                forward = Vector3.ProjectOnPlane(forward, up).normalized;

                targetRotationWorld = Quaternion.LookRotation(forward, up);
                
                if (debugOutlierFiltering)
                    Debug.LogWarning("[FloorAnchor] Not enough valid corners, using all corners (may include outliers)");
            }

            Vector3 targetPositionLocal = targetPositionWorld;
            Quaternion targetRotationLocal = targetRotationWorld;
            
            if (transform.parent != null)
            {
                targetPositionLocal = transform.parent.InverseTransformPoint(targetPositionWorld);
                targetRotationLocal = Quaternion.Inverse(transform.parent.rotation) * targetRotationWorld;
            }

            if (debugLogValues)
            {
                Debug.Log($"[FloorAnchor] Center vertex world: {centerVertexWorld}, offset: {centerLocalOffset}, target world: {targetPositionWorld}, current world: {transform.position}");
                Debug.Log($"[FloorAnchor] Parent: {(transform.parent != null ? transform.parent.name : "null")}, Floor parent: {(floorTransform.parent != null ? floorTransform.parent.name : "null")}");
                Debug.Log($"[FloorAnchor] target local: {targetPositionLocal}, current local: {transform.localPosition}");
                Debug.Log($"[FloorAnchor] Valid corners: {validIndices.Length}/4");
            }

            if (!firstUpdate)
            {
                transform.localPosition = targetPositionLocal;
                transform.localRotation = targetRotationLocal;
                firstUpdate = true;
            }
            else
            {
                transform.localPosition = Vector3.Lerp(transform.localPosition, targetPositionLocal, Time.deltaTime * 10f);
                transform.localRotation = Quaternion.Slerp(transform.localRotation, targetRotationLocal, Time.deltaTime * 10f);
            }
        }

        Bounds CalculateAccurateBounds()
        {
            // Use precalculated bounds if available
            if (HasPrecalculatedBounds())
            {
                Vector3[] worldCorners = GetPrecalculatedCornersWorld();
                Bounds bounds = new Bounds(worldCorners[0], Vector3.zero);
                for (int i = 1; i < worldCorners.Length; i++)
                    bounds.Encapsulate(worldCorners[i]);
                
                if (debugLogValues)
                    Debug.Log($"[FloorAnchor] Using precalculated bounds: center={bounds.center}, size={bounds.size}");
                
                return bounds;
            }
            
            // Fallback to runtime calculation
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            
            if (renderers.Length == 0)
            {
                return new Bounds(transform.position, Vector3.one * 0.1f);
            }

            Bounds fallbackBounds = renderers[0].bounds;
            
            for (int i = 1; i < renderers.Length; i++)
            {
                fallbackBounds.Encapsulate(renderers[i].bounds);
            }

            return fallbackBounds;
        }

        Vector3[] GetBoundsCorners(Bounds bounds)
        {
            // Use precalculated floor corners if available
            if (HasPrecalculatedBounds())
            {
                return GetPrecalculatedFloorCornersWorld();
            }
            
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            
            return new Vector3[4]
            {
                new Vector3(center.x - extents.x, center.y - extents.y, center.z - extents.z),
                new Vector3(center.x + extents.x, center.y - extents.y, center.z - extents.z),
                new Vector3(center.x - extents.x, center.y - extents.y, center.z + extents.z),
                new Vector3(center.x + extents.x, center.y - extents.y, center.z + extents.z)
            };
        }

        void CacheCornerVertices()
        {
            if (floorMesh == null || floorTransform == null) return;

            cachedBounds = CalculateAccurateBounds();
            Vector3[] corners = GetBoundsCorners(cachedBounds);

            Vector3[] verts = floorMesh.vertices;

            Vector3 currentPos = transform.position;
            float minDistCenter = float.MaxValue;
            int nearestCenterIndex = -1;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 vertexWorld = floorTransform.TransformPoint(verts[i]);
                float dist = Vector2.SqrMagnitude(
                    new Vector2(currentPos.x - vertexWorld.x, currentPos.z - vertexWorld.z)
                );
                
                if (dist < minDistCenter)
                {
                    minDistCenter = dist;
                    nearestCenterIndex = i;
                }
            }
            
            centerVertexIndex = nearestCenterIndex;
            if (nearestCenterIndex >= 0)
            {
                Vector3 centerLocal = floorTransform.InverseTransformPoint(currentPos);
                centerLocalOffset = centerLocal - verts[nearestCenterIndex];

                if (!authoredVerticalCaptured)
                {
                    authoredVerticalOffset = centerLocalOffset.y;
                    authoredVerticalCaptured = true;
                }
                
                if (debugLogValues)
                {
                    Vector3 vertexWorld = floorTransform.TransformPoint(verts[nearestCenterIndex]);
                    Debug.Log($"[FloorAnchor] CACHING NOW - Object Y: {currentPos.y}, Floor vertex Y: {vertexWorld.y}, Offset (local): {centerLocalOffset}");
                }
            }

            Debug.Log($"[FloorAnchor] CacheCornerVertices completed - centerVertexIndex: {centerVertexIndex}, offset: {centerLocalOffset}");

            for (int cornerIdx = 0; cornerIdx < 4; cornerIdx++)
            {
                Vector3 cornerWorld = corners[cornerIdx];
                
                float minDist = float.MaxValue;
                int nearestIndex = -1;

                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 vertexWorld = floorTransform.TransformPoint(verts[i]);
                    
                    float dist = Vector2.SqrMagnitude(
                        new Vector2(cornerWorld.x - vertexWorld.x, cornerWorld.z - vertexWorld.z)
                    );
                    
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestIndex = i;
                    }
                }

                cornerVertexIndices[cornerIdx] = nearestIndex;
                cornerVertexDistances[cornerIdx] = Mathf.Sqrt(minDist); // Store actual distance for outlier detection
                
                if (nearestIndex >= 0)
                {
                    Vector3 cornerLocal = floorTransform.InverseTransformPoint(cornerWorld);
                    cornerLocalOffsets[cornerIdx] = cornerLocal - verts[nearestIndex];
                }
                
                if (debugOutlierFiltering)
                {
                    Debug.Log($"[FloorAnchor] Corner {cornerIdx} cached: vertex index={nearestIndex}, distance={cornerVertexDistances[cornerIdx]:F3}m");
                }
            }
        }

        void CacheClosestVertex()
        {
            if (floorMesh == null || floorTransform == null) return;

            Vector3[] verts = floorMesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i].y = 0;
            }
            Vector3 localPos = floorTransform.InverseTransformPoint(transform.position);

            float minDist = float.MaxValue;
            int nearestIndex = -1;

            for (int i = 0; i < verts.Length; i++)
            {
                float dist = Vector2.SqrMagnitude(new Vector2(localPos.x, localPos.z) - new Vector2(verts[i].x, verts[i].z));
                if (dist < minDist)
                {
                    minDist = dist;
                    nearestIndex = i;
                }
            }

            closestVertexIndex = nearestIndex;
            if (nearestIndex >= 0)
                localOffset = floorTransform.InverseTransformPoint(transform.position) - verts[nearestIndex];
        }

        MeshFilter FindClosestFloorMesh()
        {
            var levelTemplate = GetComponentInParent<LevelTemplate>();
            if (levelTemplate != null && levelTemplate.runtimePlane != null)
            {
                var runtimeFloor = levelTemplate.runtimePlane.GetComponent<MeshFilter>();
                if (runtimeFloor != null && runtimeFloor.sharedMesh != null)
                {
                    return runtimeFloor;
                }
            }

            MeshFilter[] filters = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            MeshFilter closest = null;
            float minDist = float.MaxValue;
            Vector3 myPos = transform.position;

            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                if (string.IsNullOrEmpty(f.name) || !f.name.Contains("LevelFloor")) continue;
                float dist = Vector3.Distance(f.transform.position, myPos);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = f;
                }
            }
            return closest;
        }

    #if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (matchGrade)
            {
                if (debugDrawBounds)
                {
                    Bounds bounds = CalculateAccurateBounds();
                    Gizmos.color = HasPrecalculatedBounds() ? Color.green : Color.yellow;
                    Gizmos.DrawWireCube(bounds.center, bounds.size);
                }

                if (floorMeshFilter != null && floorMesh != null)
                {
                    Vector3[] corners = GetBoundsCorners(cachedBounds);
                    Vector3[] verts = floorMesh.vertices;
                    
                    // Calculate which corners are valid for visualization
                    float[] heights = new float[4];
                    for (int i = 0; i < 4; i++)
                    {
                        if (cornerVertexIndices[i] >= 0 && cornerVertexIndices[i] < verts.Length)
                            heights[i] = floorTransform.TransformPoint(verts[cornerVertexIndices[i]]).y;
                    }
                    // Same rule the real filter uses, so the gizmo cannot
                    // disagree with the placement it is drawn to explain.
                    Vector3[] cornerWorld = new Vector3[4];
                    for (int i = 0; i < 4; i++)
                    {
                        cornerWorld[i] = (cornerVertexIndices[i] >= 0 && cornerVertexIndices[i] < verts.Length)
                            ? floorTransform.TransformPoint(verts[cornerVertexIndices[i]])
                            : Vector3.zero;
                    }
                    float[] gizmoResiduals = ComputePlaneResiduals(cornerWorld);

                    for (int i = 0; i < 4; i++)
                    {
                        if (cornerVertexIndices[i] >= 0 && cornerVertexIndices[i] < verts.Length)
                        {
                            Vector3 vertexWorld = floorTransform.TransformPoint(verts[cornerVertexIndices[i]]);
                            
                            // Check if this corner would be filtered
                            bool isHeightOutlier = gizmoResiduals[i] > maxHeightDeviation;
                            bool isDistanceOutlier = cornerVertexDistances[i] > maxVertexDistance;
                            bool isFiltered = isHeightOutlier || isDistanceOutlier;
                            
                            // Red for filtered corners, cyan for valid
                            Gizmos.color = isFiltered ? Color.red : Color.cyan;
                            Gizmos.DrawSphere(vertexWorld, 0.02f);
                            
                            // Draw line - yellow for valid, red for filtered
                            Gizmos.color = isFiltered ? new Color(1f, 0.5f, 0.5f) : Color.yellow;
                            Gizmos.DrawLine(vertexWorld, corners[i]);
                            
                            // Corner point - magenta for valid, dark red for filtered
                            Gizmos.color = isFiltered ? new Color(0.5f, 0f, 0f) : Color.magenta;
                            Gizmos.DrawSphere(corners[i], 0.015f);
                        }
                    }
                }
            }
            else
            {
                if (floorMeshFilter != null && closestVertexIndex >= 0 && floorMesh != null)
                {
                    Vector3 vertexLocal = floorMesh.vertices[closestVertexIndex];
                    Vector3 vertexWorld = floorTransform.TransformPoint(vertexLocal);
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawSphere(vertexWorld, 0.02f);
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawLine(vertexWorld, transform.position);
                }
            }
        }
    #endif

        public bool isBuildMode {
            get {
                #if DREAMPARKCORE
                return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.buildMode;
                #else
                return false;
                #endif
            }
        }
    }
}