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
        // Runtime-only floor geometry for a level presented inside a Sequence.
        // The attraction's authored size, packing bake and gridDensity stay intact;
        // only its generated playable floor takes the package room's footprint.
        [System.NonSerialized] private Vector2 sequenceFloorFootprint;
        [System.NonSerialized] private int sequenceFloorGridDensity;
        // Runtime conform grid cell size in metres (dreampark-core
        // Docs/Adaptive-Conform-Density-Spec.md §4). Set by the host app when an
        // attraction is FIRST placed on measured ground; 0 = unset, so the editor,
        // the Park Simulator and every other caller keep the authored gridDensity.
        // Precedence lives in ResolveFloorGrid.
        [System.NonSerialized] private float runtimeConformCellM;
        // True only inside ConfigureConformCell(replaceSavedGrid: true) — a move
        // commit re-grids a level whose saved bake would otherwise pin its grid.
        [System.NonSerialized] private bool ignoreSavedGrid;
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
        private void GenerateDepthCeiling(bool destroyImmediately = false) {
            // Feeds a runtime manager, and Destroy() is illegal in edit mode — the public
            // RegenerateCeiling entry point makes that reachable from tooling.
            if (!Application.isPlaying) return;

            if (runtimeCeiling != null)
            {
                GameObject previousCeiling = runtimeCeiling;
                runtimeCeiling = null;
                DestroyGeneratedObject(previousCeiling, destroyImmediately);
            }
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
        /// Floorless templates still exist (for example the persistent Sequence
        /// overlay). Its active attraction owns the cutout floor, while the
        /// package root keeps a non-colliding calibration reference.
        ///
        /// The only thing preventing that before was a guard written in Lua in the
        /// retired in-prefab level controller, which no C# caller inherits. Guarding here means the invariant holds for every
        /// caller instead of for the one that remembered.
        ///
        /// No reachable behaviour changes: the two existing callers were that guarded
        /// Lua path (since removed), and CalibrateLevel.Clear() (CalibrateLevel.cs:872) — and
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
        /// Use a Sequence-wide floor footprint and grid while retaining this
        /// level's own FloorCutouts. The package owns the calibrated grade; this
        /// generated floor is the active, level-specific collision/NavMesh skin.
        /// </summary>
        public void ConfigureSequenceFloor(Vector2 footprint, int density)
        {
            if (footprint.x <= 0f || footprint.y <= 0f || density < 1) return;
            bool changed = (sequenceFloorFootprint - footprint).sqrMagnitude > 0.000001f
                || sequenceFloorGridDensity != density;
            sequenceFloorFootprint = footprint;
            sequenceFloorGridDensity = density;
            if (changed && runtimePlane != null && generateFloor) RegenerateFloor();
        }

        /// <summary>
        /// Adaptive conform density: build the floor grid at roughly
        /// <paramref name="cellM"/> metres per cell instead of the authored
        /// gridDensity (cells across the short side). 0 clears the override.
        /// The cell is in park (= world) metres; ResolveFloorGrid converts it to
        /// this template's local units, and caps it when the level has cutouts.
        ///
        /// FIRST PLACEMENT: called BEFORE Start builds the floor — then it
        /// costs nothing, and a saved bake with grid metadata, a legacy bake,
        /// or a Sequence floor still wins (ResolveFloorGrid).
        ///
        /// MOVE COMMIT (<paramref name="replaceSavedGrid"/> = true): the level
        /// already stands on a floor, possibly restored from a saved bake whose
        /// grid would otherwise win. The move is about to re-bake at the new
        /// pose anyway, so that saved grid is set aside for this rebuild, and
        /// floorData is replaced by a capture of the new grid (its _meta
        /// records the new gridX/gridY; the next save writes it and a reload
        /// rebuilds it). A LEGACY bake (no metadata, applied by vertex index)
        /// is never re-gridded. A Sequence floor still wins.
        ///
        /// A move never loses a saved bake: while the calibrator still holds
        /// that bake as unapplied data (hasPendingCalibration or its floorData
        /// not yet consumed — iOS with the portal unsynced and no prior, or
        /// before ApplyPendingCalibration), nothing is re-gridded and false is
        /// returned, whatever `calibrated` says; likewise when the template has
        /// a bake and the live mesh has no grade to carry.
        ///
        /// If the floor exists and the resolved grid actually changes, the
        /// floor is rebuilt and any calibrated grade is carried across (same
        /// pattern as AttractionTemplate's footprint change). Returns true when
        /// it rebuilt the floor: the new CalibrateLevel's Start (mesh, layer
        /// masks) only runs next frame, so a caller that bakes must wait one.
        /// Internal: host-app wiring, not creator API.
        /// </summary>
        internal bool ConfigureConformCell(float cellM, bool replaceSavedGrid = false)
        {
            if (float.IsNaN(cellM) || float.IsInfinity(cellM) || cellM < 0f) cellM = 0f;
            bool cellChanged = Mathf.Abs(runtimeConformCellM - cellM) >= 0.0001f;
            runtimeConformCellM = cellM;
            if (runtimePlane == null || !generateFloor) return false;
            if (!cellChanged && !replaceSavedGrid) return false;
            bool hadBake = floorData != null && floorData.count > 0;
            if (replaceSavedGrid && hadBake && !CalibrateLevel.HasCalibrationMetadata(floorData)) return false;

            ignoreSavedGrid = replaceSavedGrid;
            try
            {
                Vector2 dims = sequenceFloorGridDensity > 0
                    ? sequenceFloorFootprint : RuntimeFootprintMeters;
                ResolveFloorGrid(dims.x, dims.y, out int nextX, out int nextY);
                if (nextX == gridX && nextY == gridY) return false;

                CalibrateLevel previous = runtimePlane.GetComponent<CalibrateLevel>();
                Vector3[] grade = previous != null ? previous.CaptureGradeWorldSamples() : null;
                // A MOVE NEVER LOSES A SAVED BAKE. The grade is carried from the
                // LIVE mesh. On iOS with the portal unsynced and no floor prior,
                // or before LevelAnchor's ApplyPendingCalibration, the saved bake
                // is still only data the calibrator holds (hasPendingCalibration /
                // floorData) and the mesh is not that bake: re-gridding now would
                // drop it with nothing to replace it. That holds even when the
                // mesh reports `calibrated` — a Scan-mode conform with no hits
                // sets it on a flat mesh, and carrying that flat grade would
                // replace the bake just the same. Also skip when the template
                // has a bake and the mesh has no grade at all. Keep the old grid
                // and the bake intact.
                bool unappliedBake = previous != null
                    && (previous.hasPendingCalibration
                        || (previous.floorData != null && previous.floorData.count > 0));
                if (replaceSavedGrid && (unappliedBake || (hadBake && grade == null)))
                {
                    Debug.Log($"[LevelTemplate] Move re-grid skipped for {name}: its saved floor bake is not applied yet — keeping grid {gridX}x{gridY}");
                    return false;
                }
                RegenerateFloor();
                CalibrateLevel current = runtimePlane != null
                    ? runtimePlane.GetComponent<CalibrateLevel>() : null;
                if (grade != null && current != null && current != previous && current.TransferGradeFrom(grade))
                {
                    floorData = current.CompileCalibrationData();
                    // The transferred mesh is already live. Do not let the new
                    // CalibrateLevel's Start queue the old topology for replay.
                    current.floorData = null;
                }
                if (replaceSavedGrid && floorData != null && floorData.count > 0
                    && (!CalibrateLevel.TryReadSavedGrid(floorData, out int keptX, out int keptY, out _, out _)
                        || keptX != nextX || keptY != nextY))
                {
                    // Still the OLD grid's bake (no grade to carry, or a flat
                    // transfer made CompileCalibrationData fall back to it). It
                    // no longer matches this mesh, and keeping it would snap the
                    // next rebuild back to the old grid; the move's bake writes
                    // the new one.
                    floorData = null;
                    if (current != null) current.floorData = null;
                }
                return true;
            }
            finally
            {
                ignoreSavedGrid = false;
            }
        }

        /// <summary>
        /// Floor grid resolution, in precedence order:
        ///  1. Sequence floor (ConfigureSequenceFloor) — the package owns it.
        ///  2. The SAVED bake's own grid: floorData metadata carries gridX/gridY
        ///     and widthM/heightM; when the footprint matches within 1 mm the
        ///     floor is rebuilt at exactly that grid, so CalibrateLevel's
        ///     topology check accepts the bake even if today's recommended
        ///     density differs (or none was set on this load).
        ///  3. A legacy bake (no metadata) keeps the authored grid — it is keyed
        ///     by vertex index with no topology check, so it must never meet a
        ///     different grid. So does a saved grid that fails the sanity bound
        ///     (CalibrateLevel.HasUnusableSavedGrid).
        ///  4. The runtime conform cell (ConfigureConformCell), converted from
        ///     metres to local units and never coarser than the authored grid
        ///     when the level has FloorCutouts (see below).
        ///  5. Authored gridDensity.
        /// Floor data is always assigned before Start (LevelAnchor.Spawn,
        /// ParkSimulator), so Start's GenerateFloorWithHoles sees it. Steps 2–3
        /// are skipped only during a move-commit re-grid (ignoreSavedGrid).
        /// </summary>
        private void ResolveFloorGrid(float width, float height, out int resolvedX, out int resolvedY)
        {
            if (!ignoreSavedGrid && sequenceFloorGridDensity <= 0 && floorData != null && floorData.count > 0)
            {
                if (CalibrateLevel.TryReadSavedGrid(floorData, out int savedX, out int savedY,
                        out float savedWidth, out float savedHeight))
                {
                    if (Mathf.Abs(savedWidth - width) <= 0.001f && Mathf.Abs(savedHeight - height) <= 0.001f)
                    {
                        resolvedX = savedX;
                        resolvedY = savedY;
                        return;
                    }
                }
                else if (!CalibrateLevel.HasCalibrationMetadata(floorData)
                    || CalibrateLevel.HasUnusableSavedGrid(floorData))
                {
                    // Legacy bake, or a current-version _meta grid that is
                    // non-positive, over CalibrateLevel.MaxSavedGridCells, or
                    // disagrees with its own vertexCount: never build a mesh
                    // from untrusted numbers — the authored grid, as for legacy.
                    DensityGrid(gridDensity, width, height, out resolvedX, out resolvedY);
                    return;
                }
            }
            if (sequenceFloorGridDensity <= 0 && runtimeConformCellM > 0f)
            {
                // METRES MEET LOCAL UNITS HERE, and only here. The runtime cell
                // is measured in park metres (SpaceMap is park-local; parks are
                // unscaled), while width/height are this template's LOCAL units
                // — the floor mesh is a unit-scale child, so a template scaled x2
                // spans 2 x width metres. Divide the cell by the template's world
                // scale per axis; a zero/non-finite axis keeps the metre cell.
                Vector3 scale = transform.lossyScale;
                float cellX = LocalCell(runtimeConformCellM, scale.x);
                float cellZ = LocalCell(runtimeConformCellM, scale.z);
                // Epsilon: 4.0 m / 1.0 m must stay 4 cells, not become 5 on float noise.
                resolvedX = Mathf.Max(1, Mathf.CeilToInt(width  / cellX - 0.0001f));
                resolvedY = Mathf.Max(1, Mathf.CeilToInt(height / cellZ - 0.0001f));
                // FLOOR CUTOUTS CAP THE CELL. Holes are cut by DROPPING whole
                // triangles (GenerateFloorWithHoles: any corner inside a hole or
                // any edge crossing it), and corners inside a hole are pushed out
                // in quarter-cell steps. With a runtime cell coarser than the one
                // the author tuned the cutouts against, a small pit can fall
                // inside one triangle and not be cut at all, and a large one is
                // over-cut to a coarse staircase. So with any cutout the runtime
                // grid is never coarser than the authored gridDensity grid —
                // per axis, cell = min(runtime cell, authored cell).
                if (HasOwnFloorCutouts())
                {
                    DensityGrid(gridDensity, width, height, out int authoredX, out int authoredY);
                    resolvedX = Mathf.Max(resolvedX, authoredX);
                    resolvedY = Mathf.Max(resolvedY, authoredY);
                }
                return;
            }
            int floorDensity = sequenceFloorGridDensity > 0 ? sequenceFloorGridDensity : gridDensity;
            DensityGrid(floorDensity, width, height, out resolvedX, out resolvedY);
        }

        private static float LocalCell(float cellM, float axisScale)
        {
            float s = Mathf.Abs(axisScale);
            if (s < 1e-4f || float.IsNaN(s) || float.IsInfinity(s)) return cellM;
            return cellM / s;
        }

        /// Same filter GenerateFloorWithHoles uses to gather holes: this
        /// template's own cutouts with a real polygon (a Sequence's nested
        /// attractions cut their own floors).
        private bool HasOwnFloorCutouts()
        {
            foreach (var pit in GetComponentsInChildren<FloorCutout>())
            {
                if (pit.GetComponentInParent<LevelTemplate>() != this) continue;
                if (pit.points != null && pit.points.Count >= 3) return true;
            }
            return false;
        }

        private static void DensityGrid(int density, float width, float height, out int resolvedX, out int resolvedY)
        {
            resolvedX = Mathf.Max(1, Mathf.RoundToInt(density * (width  / Mathf.Min(width, height))));
            resolvedY = Mathf.Max(1, Mathf.RoundToInt(density * (height / Mathf.Min(width, height))));
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor packing sliders can emit several values before Unity reaches the end
        /// of the frame. Destroy() is deferred until then, so the normal replacement
        /// path temporarily retains every generated mesh, collider and NavMeshSurface.
        /// The preview owns these generated objects outright and can replace them
        /// synchronously instead.
        /// </summary>
        internal void RegenerateFloorForEditorPreview()
        {
            if (!generateFloor) return;
            GenerateFloorWithHoles(true);
            SetFloorVisibilityForMode(isBuildMode);
            NotifyLevelTemplateChanged();
        }

        internal void RegenerateCeilingForEditorPreview()
        {
            if (generateCeiling && DepthMaskCeiling.AllowTemplateCeilings)
                GenerateDepthCeiling(true);
        }
#endif

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

            // THE SWAP IS FOR THE CAMERA, NOT FOR PHYSICS (Oct 4 2026). Water
            // does not collide with Default, Level, Entity, Item, Player,
            // Projectile or Triggers, so a floor parked on it was no floor at
            // all for any body that ran while build mode stood — and on the
            // AR-first phone build mode stands for the whole session while the
            // park runs live (LiveParkContent). Props on those floors fell to
            // whatever was underneath: the live LiDAR chunks during a scan,
            // nothing once they stopped colliding. Wherever the floor is put
            // for rendering, it collides exactly like a Level floor.
            ApplyPlayLayerContacts(runtimePlane, targetLayer, playLayer);

            if (!runtimePlane.activeSelf)
            {
                runtimePlane.SetActive(true);
            }
        }

        /// The floor's colliders contact what `playLayer` contacts while the
        /// floor sits on another layer (includeLayers is per-collider and
        /// affects contacts only — raycasts still see the floor's own layer).
        /// Cleared again on the play layer, where the matrix already says so.
        private static void ApplyPlayLayerContacts(GameObject floor, int floorLayer, int playLayer)
        {
            LayerMask include = 0;
            if (playLayer >= 0 && floorLayer != playLayer)
            {
                int mask = 0;
                for (int i = 0; i < 32; i++)
                    if (!Physics.GetIgnoreLayerCollision(playLayer, i)) mask |= 1 << i;
                include = mask;
            }
            foreach (var col in floor.GetComponents<Collider>())
                if (col != null && col.includeLayers != include) col.includeLayers = include;
        }

        private void GenerateFloorWithHoles(bool destroyImmediately = false)
{
    if (runtimePlane != null) DestroyRuntimeFloor(destroyImmediately);

    // Dimensions
    Vector2 dims = sequenceFloorGridDensity > 0
        ? sequenceFloorFootprint : RuntimeFootprintMeters;

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
    // This material is never modified per generated floor, so do not instantiate a
    // private copy on every preview tick.
    mr.sharedMaterial = floorMaterial ? floorMaterial : Resources.Load<Material>("Materials/Occlusion");
    // Oct 8 2026: never z-fight content authored at floor height
    // (GapFiller.cs FloorOccluderMaterial — a runtime swap, rendering only).
    mr.sharedMaterial = FloorOccluderMaterial.For(mr.sharedMaterial);
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
    // Sequence floor → saved bake grid → runtime conform cell → gridDensity.
    ResolveFloorGrid(width, height, out gridX, out gridY);
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
        // A Sequence keeps a plain calibration reference on its package root.
        // Each nested attraction cuts its own floor; its pits must not punch
        // holes in the reference surface or alter persisted floor topology.
        if (pit.GetComponentInParent<LevelTemplate>() != this) continue;
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

private void DestroyRuntimeFloor(bool destroyImmediately)
{
    GameObject previousFloor = runtimePlane;
    runtimePlane = null;
    if (previousFloor == null) return;

    // NavMeshSurface.RemoveData deliberately does not destroy its NavMeshData.
    // Both it and the procedural mesh are owned by this generated floor, so release
    // them along with the GameObject instead of leaking one copy per slider value.
    var surface = previousFloor.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
    NavMeshData navMeshData = surface != null ? surface.navMeshData : null;
    if (surface != null)
    {
        surface.RemoveData();
        surface.navMeshData = null;
    }

    var filter = previousFloor.GetComponent<MeshFilter>();
    var collider = previousFloor.GetComponent<MeshCollider>();
    var renderer = previousFloor.GetComponent<MeshRenderer>();
    // Destroy is deferred in play mode. Never let the outgoing floor keep a
    // collider under a newly opened cutout for the rest of this frame.
    if (collider != null) collider.enabled = false;
    if (renderer != null) renderer.enabled = false;
    Mesh generatedMesh = filter != null ? filter.sharedMesh : null;
    if (filter != null) filter.sharedMesh = null;
    if (collider != null) collider.sharedMesh = null;
    // Deactivate too, so for the rest of this frame the dying floor's
    // CalibrateLevel is invisible to GetComponentInChildren<CalibrateLevel>()
    // (saves, the move-commit bake, the Clear Floor button) and only the new
    // runtimePlane answers. Its NavMesh data was already removed above and
    // nothing reads this object again before Destroy.
    previousFloor.SetActive(false);

    DestroyGeneratedObject(previousFloor, destroyImmediately);
    DestroyGeneratedObject(generatedMesh, destroyImmediately);
    DestroyGeneratedObject(navMeshData, destroyImmediately);
}

private static void DestroyGeneratedObject(Object generatedObject, bool destroyImmediately)
{
    if (generatedObject == null) return;
#if UNITY_EDITOR
    if (destroyImmediately || !Application.isPlaying)
    {
        DestroyImmediate(generatedObject);
        return;
    }
#endif
    Destroy(generatedObject);
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

    // A package's direct children are structural containers (Levels, overlay,
    // manager, transition). Anchoring those would move entire levels instead of
    // conforming their authored FloorAnchors to the shared floor.
    if (GetComponent<DreamParkPackageHost>() == null)
    {
        foreach (Transform child in transform)
        {
            if (child.gameObject == runtimePlane || child.gameObject == gameObject) continue;
            Componentizer.DoComponent<FloorAnchor>(child.gameObject, true).calibrator = calibrator;
        }
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

            Gizmos.matrix = oldMatrix;
        }

    #endif
    }
}
