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
        /// Rematerialized floor prior (Auto-Calibration-Spec §5.3). Kept as a
        /// SEPARATE mask from arMeshLayer, not merged into it, so the conform
        /// can try live mesh first and fall back — live always beats memory.
        private LayerMask arMeshPriorLayer = -1;

        /// Set by the project-side FloorPriorManager when a floor prior has been
        /// rematerialized with trustworthy geometry; cleared on park switch and
        /// whenever the prior goes away (Phase 2.5 §4.1).
        ///
        /// This is the ONLY thing the SDK knows about the prior. One static
        /// bool, written from the project side, so CalibrateLevel never gains a
        /// dependency on the height field, the accumulator, or the backend.
        public static bool allowUnanchoredCalibration;

        // ── Search volume ────────────────────────────────────────────────
        // These are FLOORS, not fixed sizes. GroundProbe.MeasureSpan sweeps
        // the footprint before each conform and grows the span to cover the
        // ground it actually finds; these values are the minimum it will ever
        // use, and they reproduce the legacy ±10m volume exactly for any venue
        // flat enough that the old code worked. Raising them costs nothing but
        // raycast length on venues that need it; there is no reason to lower
        // them.
        //
        // The volume is anchored to the LEVEL'S OWN PLANE, not to the camera.
        // The old code started every ray at `Camera.main.y + 10` — see the
        // header of GroundProbe.cs for why that silently discarded whole bakes
        // whenever the guest was not standing at the attraction's elevation.
        [Header("Ground Search (minimums — the probe grows these to fit)")]
        [Tooltip("Minimum metres above the level plane to begin searching for ground.")]
        public float minRaycastAbove = 10f;
        [Tooltip("Minimum metres below the level plane the search must still reach.")]
        public float minRaycastBelow = 10f;

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
            if (arMeshPriorLayer == -1)
                arMeshPriorLayer = LayerMask.GetMask(FloorPriorLayerName);

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

                // Auto-Calibration-Spec Phase 2.5 §3/§4.1 — the sync gate guards
                // WORLD-SPACE PROVENANCE, and a rematerialized floor prior is
                // not world-space. Live AR mesh arrives in session coordinates
                // and is meaningless until the park is synced into the world,
                // which is why this gate exists. But the prior is stored park-
                // local, attraction transforms are park-local, and floorData is
                // park-local vertex Y — the whole chain stays inside park space
                // and never references the world. So an unanchored session may
                // apply it: that is remote editing.
                if (isHeadset || portalIsSynced || allowUnanchoredCalibration) {
                    // Don't apply yet — mark as pending so LevelAnchor can apply AFTER
                    // objects are disabled, preventing physics/collision issues from the
                    // floor shifting under active rigidbodies.
                    hasPendingCalibration = true;
                    Debug.Log($"[CalibrateLevel] Calibration data ready (pending) for {gameObject.name} (isHeadset={isHeadset}, portalSynced={portalIsSynced}, prior={allowUnanchoredCalibration})");
                } else {
                    Debug.Log("[CalibrateLevel] iOS - portal not synced and no floor prior, showing flat map for " + gameObject.name);
                    // Don't apply calibration visually, but keep floorData on CalibrateLevel
                    // so CompileCalibrationData() can preserve it during saves.
                    // (floorData is intentionally NOT cleared here)
                }
            }
#else
            // SDK / editor. The sync gate above exists to guard WORLD-SPACE
            // PROVENANCE: live AR mesh arrives in session coordinates and is
            // meaningless until the park is anchored into the world. There is
            // no AR session here and no portal to sync, and floorData is
            // park-local vertex Y, so there is nothing to guard against —
            // received floor data is simply applied.
            //
            // Without this branch the SDK could never replay a saved park's
            // floor: LevelTemplate would hand floorData to the calibrator and
            // ApplyPendingCalibration() would silently no-op forever, so the
            // Park Simulator would only ever exercise fresh placement and never
            // the load path a returning guest actually takes.
            if (floorData != null) {
                hasPendingCalibration = true;
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
        /// Stores the original vertices and hole definitions for later re-cutting.
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

        /// <summary>
        /// Auto-calibration Phase 1 (Docs/Auto-Calibration-Spec.md §4): one-shot
        /// conform against whatever raycastable environment exists right now
        /// (AR mesh chunks persist across mode switches within a session, so a
        /// Scan sweep keeps paying off after the user leaves Scan mode).
        /// Applies ONLY when at least minCoverage of the floor grid vertices get
        /// a floor-like hit — a partially-covered footprint stays flat rather
        /// than baking a tented edge. Returns true when a bake was applied.
        /// Callers: spawn commit (LevelAnchor.NewLevel) and move commit
        /// (BuildModeObjectController). Scan mode keeps its continuous conform
        /// and never routes through here.
        /// </summary>
        public bool ConformOnce(float minCoverage = 0.6f)
        {
            if (dynamicMesh == null) return false;
            return ConformGridToSurface(minCoverage);
        }

        /// <summary>
        /// Compute the world-space footprint of the floor grid, used to size the
        /// ground search before any per-vertex probing happens.
        ///
        /// Built from originalVertices (the flat authored grid) rather than the
        /// live mesh, for the same reason the conform itself re-clones them
        /// every pass: the authored grid is the stable reference, so a repeated
        /// conform in Scan mode measures the same footprint every time instead
        /// of drifting along with its own output.
        /// </summary>
        private Bounds ComputeWorldFootprint(Vector3[] verts)
        {
            if (verts == null || verts.Length == 0) {
                return new Bounds(transform.position, Vector3.one);
            }

            var b = new Bounds(transform.TransformPoint(verts[0]), Vector3.zero);
            for (int i = 1; i < verts.Length; i++) {
                b.Encapsulate(transform.TransformPoint(verts[i]));
            }
            // The grid is planar, so the Y extent is ~0. Give it a nominal
            // thickness so the centre is well-defined and the corner probes are
            // not degenerate.
            var size = b.size;
            if (size.y < 0.01f) {
                b.size = new Vector3(size.x, 0.01f, size.z);
            }
            return b;
        }

        private bool ConformGridToSurface(float minCoverage = 0f)
        {
            Debug.Log("ConformGridToSurface called");
            if (dynamicMesh == null)
            {
                Debug.LogWarning("CalibrateLevel: Missing dynamic mesh.");
                return false;
            }

            // If we have original vertices stored, use those as the base for calibration
            Vector3[] verts = originalVertices != null
                ? (Vector3[])originalVertices.Clone()
                : dynamicMesh.vertices;

            int hitCount = 0;

            // Size the search volume from the ground that actually exists under
            // this footprint, then anchor every ray to the vertex it belongs to.
            // See GroundProbe.cs for why this replaced a camera-anchored ±10m
            // constant. MeasureSpan can only ever return a span at least as
            // large as (minRaycastAbove, minRaycastBelow), so a venue that
            // calibrates today cannot stop calibrating because of this.
            Bounds footprint = ComputeWorldFootprint(verts);
            GroundProbe.Span span = GroundProbe.MeasureSpan(
                footprint, arMeshLayer, arMeshPriorLayer, minRaycastAbove, minRaycastBelow);

            // Which vertices actually found ground. A vertex that MISSES keeps
            // its authored height, and that is the problem this array exists to
            // fix — see FillUnconformedVertices.
            bool[] conformed = new bool[verts.Length];

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 worldPos = transform.TransformPoint(verts[i]);

                if (GroundProbe.TryFindGround(worldPos, span, arMeshLayer, arMeshPriorLayer, out RaycastHit hit))
                {
                    float targetY = hit.point.y;
                    float newY = targetY;
                    verts[i] = transform.InverseTransformPoint(new Vector3(worldPos.x, newY, worldPos.z));
                    conformed[i] = true;
                    hitCount++;
                }
            }

            if (hitCount > 0 && hitCount < verts.Length)
            {
                FillUnconformedVertices(verts, conformed);
            }

            // Coverage gate (Auto-Calibration-Spec §4): one-shot bakes pass a
            // minCoverage and must NOT apply a partial conform — a footprint
            // half-off the scanned area stays flat, never a tented edge.
            // Scan mode passes 0 = legacy behavior (apply on any hits, and the
            // calibrated flag / change notification fire regardless).
#if UNITY_EDITOR
            // Editor-only diagnostic (ReportNavCoverage); the field itself lives in
            // the UNITY_EDITOR region below, so the write must be guarded too.
            _lastConformHitCount = hitCount;
#endif
            float coverage = verts.Length > 0 ? (float)hitCount / verts.Length : 0f;
            bool gated = minCoverage > 0f;
            if (gated && coverage < minCoverage)
            {
                Debug.Log($"CalibrateLevel: coverage {coverage:P0} below gate {minCoverage:P0} — leaving {gameObject.name} unbaked (searched {span})");
                return false;
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
                    // RecalculateBounds is LOAD-BEARING, not housekeeping.
                    // NavMeshSurface.CalculateWorldBounds derives the volume
                    // Recast is allowed to voxelize from Mesh.bounds. Move the
                    // vertices without refreshing it and the bake volume still
                    // describes the FLAT authored grid while the floor has
                    // moved down onto the terrain — so only the sliver where
                    // the two still overlap gets navmesh. Perfectly walkable
                    // geometry, arbitrary partial coverage, and no error
                    // anywhere. RecutHolesAndUpdateMesh always called this,
                    // which is why levels WITH a FloorCutout were fine and
                    // every other level was not.
                    dynamicMesh.RecalculateBounds();
                    meshCollider.sharedMesh = dynamicMesh;
                }

                // The floor just moved. The navmesh baked over its old shape
                // is now wrong — see RequestNavMeshRebake.
                RequestNavMeshRebake();
            }
            else
            {
                Debug.LogWarning("CalibrateLevel: No hits found (searched " + span + ")");
            }

            // DELIBERATELY SET EVEN ON ZERO HITS. `calibrated` does not mean
            // "the floor is uneven", it means "a calibration pass has run and
            // this floor mesh is now authoritative". FloorAnchor.Update()
            // hard-returns while it is false, so every floor-anchored child
            // stops positioning until it flips. A perfectly flat venue that
            // legitimately needs no vertex offsets must still end up here, or
            // the operator's intent — "I calibrated this, the ground is flat" —
            // would leave every anchored child frozen at its authored pose.
            // Do not "fix" this to `hitCount > 0`.
            calibrated = true;
            LevelTemplate.NotifyLevelTemplateChanged();
            return hitCount > 0;
        }


        // ── NavMesh rebake ───────────────────────────────────────────────
        //  LevelTemplate.BuildNavSurfaceAndAnchors bakes this floor's
        //  NavMeshSurface exactly ONCE — at creation, over the FLAT authored
        //  grid — and only then attaches this component. Nothing rebaked it
        //  afterwards, so every conform moved the visible floor and its
        //  MeshCollider while the navmesh stayed behind at the authored
        //  height. Agents then pathed across a surface that was no longer
        //  there: they float above a floor that conformed downhill and sink
        //  into one that conformed up.
        //
        //  It hid for so long because the error is proportional to the relief.
        //  In a scanned room the conform delta is centimetres and NavMesh's
        //  own snap tolerance swallows it. On graded ground it is metres.
        //
        //  UpdateNavMesh, NOT BuildNavMesh: it rebakes in place into the
        //  already-registered NavMeshData and does it asynchronously, so a
        //  Scan-mode conform firing every couple of seconds cannot stall the
        //  frame, and no agent is momentarily unbound the way a
        //  RemoveData/AddData cycle would leave it.
        private bool _navRebakeInFlight;
        private bool _navRebakeQueued;

        private void RequestNavMeshRebake()
        {
            var surface = GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            if (surface == null) return;

            // Never baked, or the floor was rebuilt from scratch — there is no
            // data to update in place, so do the one-off build LevelTemplate
            // would have done.
            if (surface.navMeshData == null) {
                surface.BuildNavMesh();
                return;
            }

            // A coroutine needs a live component, and the optimizer can park a
            // floor that Scan mode is still conforming. Fall back to the
            // synchronous path rather than throwing.
            if (!isActiveAndEnabled) {
                surface.UpdateNavMesh(surface.navMeshData);
                return;
            }

            // Coalesce. A burst of meshesChanged events during Scan would
            // otherwise queue one bake per event and they would pile up.
            if (_navRebakeInFlight) { _navRebakeQueued = true; return; }
            StartCoroutine(RebakeNavMeshRoutine(surface));
        }

        private System.Collections.IEnumerator RebakeNavMeshRoutine(
            Unity.AI.Navigation.NavMeshSurface surface)
        {
            _navRebakeInFlight = true;
            try {
                do {
                    _navRebakeQueued = false;
                    if (surface == null || surface.navMeshData == null) break;
                    var op = surface.UpdateNavMesh(surface.navMeshData);
                    while (op != null && !op.isDone) yield return null;
#if UNITY_EDITOR
                    ReportNavCoverage();
#endif
                } while (_navRebakeQueued);
            } finally {
                _navRebakeInFlight = false;
                _navRebakeQueued = false;
            }
        }


#if UNITY_EDITOR
        // ── Coverage check ───────────────────────────────────────────────
        //  A floor that bakes only part of its navmesh fails SILENTLY. The
        //  mesh looks right, the collider is right, agents spawn fine — they
        //  just cannot path across regions that were never baked, and the
        //  symptom surfaces much later as an agent that refuses to move or
        //  takes an absurd detour. Nothing in Unity warns about it, because
        //  from Recast's point of view discarding a sub-threshold region is
        //  the requested behaviour.
        //
        //  So after every rebake the floor asks the question directly: at how
        //  many of my own vertices could an agent actually stand? Editor-only
        //  — this is a development diagnostic and the sampling is not free.
        private int _lastConformHitCount;
        private const float NavCoverageProbe = 0.35f;
        private const float NavCoverageWarnBelow = 0.75f;

        private void ReportNavCoverage()
        {
            if (dynamicMesh == null) return;
            var verts = dynamicMesh.vertices;
            if (verts.Length == 0) return;

            int reachable = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 world = transform.TransformPoint(verts[i]);
                if (UnityEngine.AI.NavMesh.SamplePosition(
                        world, out _, NavCoverageProbe, UnityEngine.AI.NavMesh.AllAreas))
                {
                    reachable++;
                }
            }

            float coverage = (float)reachable / verts.Length;
            if (coverage >= NavCoverageWarnBelow) return;

            // Measure the GEOMETRY, not just the outcome. These three numbers
            // separate the two candidate causes outright, which no amount of
            // staring at the bake can:
            //
            //   steep quads high  -> the conformed floor genuinely exceeds the
            //                        agent's slope limit. Recast is correct to
            //                        reject it and the fix belongs in how the
            //                        floor conforms, not in bake settings.
            //   steep quads ~zero -> the geometry is walkable and something in
            //                        the BAKE is discarding it. Bake settings.
            //
            // maxDrop is reported alongside because a single stranded vertex
            // shows up as a large drop against an otherwise gentle surface.
            float agentSlope = 45f;
            var surface = GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            var agentSettings = UnityEngine.AI.NavMesh.GetSettingsByID(
                surface != null ? surface.agentTypeID : 0);
            if (agentSettings.agentSlope > 0f) agentSlope = agentSettings.agentSlope;

            int steepQuads = 0, totalQuads = 0;
            float maxSlopeDeg = 0f, maxDrop = 0f;
            int vertCountX = gridX + 1, vertCountY = gridY + 1;

            if (verts.Length == vertCountX * vertCountY && gridX > 0 && gridY > 0)
            {
                float cellX = gridWidthOrDefault() / gridX;
                float cellZ = gridHeightOrDefault() / gridY;

                for (int y = 0; y < vertCountY; y++)
                {
                    for (int x = 0; x < vertCountX; x++)
                    {
                        int i = y * vertCountX + x;
                        if (x + 1 < vertCountX) {
                            Measure(verts[i].y, verts[i + 1].y, cellX,
                                    ref steepQuads, ref totalQuads, ref maxSlopeDeg, ref maxDrop, agentSlope);
                        }
                        if (y + 1 < vertCountY) {
                            Measure(verts[i].y, verts[i + vertCountX].y, cellZ,
                                    ref steepQuads, ref totalQuads, ref maxSlopeDeg, ref maxDrop, agentSlope);
                        }
                    }
                }
            }

            // The mesh's cached bounds against where the vertices actually
            // are. NavMeshSurface voxelizes the former; if they disagree, the
            // bake volume does not contain the floor.
            float vMinY = float.MaxValue, vMaxY = float.MinValue;
            for (int i = 0; i < verts.Length; i++) {
                if (verts[i].y < vMinY) vMinY = verts[i].y;
                if (verts[i].y > vMaxY) vMaxY = verts[i].y;
            }
            Bounds mb = dynamicMesh.bounds;

            Debug.LogWarning(string.Format(
                "[CalibrateLevel] {0}: {1:P0} navmesh coverage ({2}/{3} verts).\n" +
                "  conform: {4}/{3} vertices found ground\n" +
                "  slope:   {5}/{6} edges exceed the agent limit of {7:F0}deg (max {8:F1}deg, max step {9:F2}m)\n" +
                "  bounds:  mesh.bounds y [{10:F2}, {11:F2}] vs actual vertex y [{12:F2}, {13:F2}]\n" +
                "  -> a bounds mismatch means the bake volume does not contain the floor.",
                gameObject.name, coverage, reachable, verts.Length,
                _lastConformHitCount, steepQuads, totalQuads, agentSlope, maxSlopeDeg, maxDrop,
                mb.min.y, mb.max.y, vMinY, vMaxY), this);
        }

        private float gridWidthOrDefault()
        {
            return levelTemplate != null && levelTemplate.gridWidth > 0f ? levelTemplate.gridWidth : 1f;
        }

        private float gridHeightOrDefault()
        {
            return levelTemplate != null && levelTemplate.gridHeight > 0f ? levelTemplate.gridHeight : 1f;
        }

        private static void Measure(
            float yA, float yB, float run,
            ref int steep, ref int total, ref float maxSlopeDeg, ref float maxDrop, float limitDeg)
        {
            if (run <= 0f) return;
            total++;
            float drop = Mathf.Abs(yA - yB);
            float deg = Mathf.Atan2(drop, run) * Mathf.Rad2Deg;
            if (deg > maxSlopeDeg) maxSlopeDeg = deg;
            if (drop > maxDrop) maxDrop = drop;
            if (deg > limitDeg) steep++;
        }
#endif


        /// <summary>
        /// Give every vertex that found no ground a height interpolated from
        /// the neighbours that did.
        ///
        /// WITHOUT THIS THE FLOOR IS A MIXTURE OF TWO SURFACES. A vertex whose
        /// raycast missed keeps its AUTHORED height — the flat plane — while
        /// its neighbours have been pulled down onto real ground. The coverage
        /// gate then happily applies the bake at anything over 60%, so up to
        /// four vertices in ten can be left standing at the old elevation.
        /// Each one is a spike, every triangle touching it is near-vertical,
        /// and near-vertical triangles fail the agent's 45-degree slope limit
        /// and become non-walkable. Recast then erodes agentRadius around each
        /// of those, so a single stranded vertex punches roughly a square metre
        /// out of the navmesh. Dozens of them shred it into the slivers this
        /// was diagnosed from.
        ///
        /// It never showed on a headset because a scanned room gives close to
        /// 100% coverage — the mixture needs misses to exist at all.
        ///
        /// Iterative nearest-neighbour relaxation over the grid rather than a
        /// single pass: a hole several vertices wide has interior vertices with
        /// no conformed neighbour at all on the first sweep, and they fill
        /// inward one ring at a time. Bounded by the grid's own dimensions, so
        /// it always terminates.
        /// </summary>
        private void FillUnconformedVertices(Vector3[] verts, bool[] conformed)
        {
            int vertCountX = gridX + 1;
            int vertCountY = gridY + 1;
            if (vertCountX <= 0 || vertCountY <= 0) return;
            if (verts.Length != vertCountX * vertCountY) return;

            int maxRings = Mathf.Max(vertCountX, vertCountY);
            var filledThisRing = new List<int>();

            for (int ring = 0; ring < maxRings; ring++)
            {
                filledThisRing.Clear();

                for (int y = 0; y < vertCountY; y++)
                {
                    for (int x = 0; x < vertCountX; x++)
                    {
                        int i = y * vertCountX + x;
                        if (conformed[i]) continue;

                        float sum = 0f;
                        int n = 0;
                        AccumulateNeighbour(verts, conformed, x - 1, y, vertCountX, vertCountY, ref sum, ref n);
                        AccumulateNeighbour(verts, conformed, x + 1, y, vertCountX, vertCountY, ref sum, ref n);
                        AccumulateNeighbour(verts, conformed, x, y - 1, vertCountX, vertCountY, ref sum, ref n);
                        AccumulateNeighbour(verts, conformed, x, y + 1, vertCountX, vertCountY, ref sum, ref n);
                        if (n == 0) continue;

                        verts[i].y = sum / n;
                        filledThisRing.Add(i);
                    }
                }

                if (filledThisRing.Count == 0) break;

                // Marked AFTER the whole sweep, from an explicit list. Marking
                // inline would let a vertex filled earlier in this same sweep
                // act as a source for one filled later in it, so the result
                // would depend on iteration order and drift across the hole
                // instead of growing evenly inward from its rim.
                for (int k = 0; k < filledThisRing.Count; k++)
                {
                    conformed[filledThisRing[k]] = true;
                }
            }
        }

        private static void AccumulateNeighbour(
            Vector3[] verts, bool[] conformed, int x, int y, int w, int h, ref float sum, ref int n)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = y * w + x;
            if (!conformed[i]) return;
            sum += verts[i].y;
            n++;
        }

        private void RecutHolesAndUpdateMesh(Vector3[] calibratedVertices)
        {
            // Push step must be a fraction of ONE CELL, matching
            // LevelTemplate.GenerateFloorWithHoles:
            //     cellSize = Mathf.Min(width / gridX, height / gridY)
            //     step     = cellSize * 0.25f
            //
            // This previously read `calibratedVertices[gridX].x -
            // calibratedVertices[0].x`, which is the span of the ENTIRE FIRST
            // ROW (vertCountX == gridX + 1, so index gridX is the row's last
            // vertex), not one cell. That made the step ~gridX times too large
            // — 10x at the default gridDensity of 10 — so a vertex found inside
            // a FloorCutout was shoved a quarter of the level's WIDTH per
            // iteration, up to 20 iterations, dragging the mesh far outside the
            // level and shredding the geometry around every cutout. It only
            // surfaced on levels that have cutouts AND have been baked, which
            // is why it survived this long.
            float cellSizeX = gridX > 0
                ? Mathf.Abs(calibratedVertices[gridX].x - calibratedVertices[0].x) / gridX
                : 1f;
            float cellSizeZ = gridY > 0 && calibratedVertices.Length > (gridY * (gridX + 1))
                ? Mathf.Abs(calibratedVertices[gridY * (gridX + 1)].z - calibratedVertices[0].z) / gridY
                : cellSizeX;
            float cellSize = Mathf.Min(
                cellSizeX > 0f ? cellSizeX : float.MaxValue,
                cellSizeZ > 0f ? cellSizeZ : float.MaxValue);
            if (cellSize <= 0f || float.IsInfinity(cellSize)) cellSize = 1f;

            float step = cellSize * 0.25f;
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

            Debug.Log($"[CalibrateLevel] Re-cut holes: {triangles.Count / 3} triangles (cell {cellSize:F2}m, step {step:F2}m)");
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
            // Same reason as the conform path — see there. A replayed floor
            // moves exactly as far as a freshly conformed one.
            dynamicMesh.RecalculateBounds();
            meshCollider.sharedMesh = dynamicMesh;
            // Saved floor data reshapes the floor, so the navmesh baked over
            // the flat grid must follow.
            RequestNavMeshRebake();
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
                // Flattening moves vertices too, so the bounds are just as
                // stale as they are on the way down.
                dynamicMesh.RecalculateBounds();
                meshCollider.sharedMesh = dynamicMesh;
            }

            LevelTemplate.NotifyLevelTemplateChanged();
            Debug.Log("[CalibrateLevel] Cleared calibration data for " + gameObject.name);
        }

        /// Must match FloorPriorSurface.LayerName. Named here rather than
        /// referenced so the SDK keeps no compile-time edge to the project-side
        /// prior builder.
        private const string FloorPriorLayerName = "ARMeshPrior";

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
            // Auto-calibration Phase 1 (Docs/Auto-Calibration-Spec.md §4/§6):
            // conform ONLY while Scan mode is active. Meshing also runs in AR
            // build mode (placement raycasts), and mesh updates there used to
            // silently re-conform every placed floor. Floors may change only
            // at sanctioned moments: Scan mode, spawn commit, move commit.
            if (!isCalibrating) return;
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
