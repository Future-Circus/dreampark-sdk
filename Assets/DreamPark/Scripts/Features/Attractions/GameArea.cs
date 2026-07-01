using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// Spatial zone detection for content areas. Each LevelTemplate/AttractionTemplate
    /// has a GameArea that tracks whether the player's head is inside its bounds.
    /// When the player enters a new zone, the corresponding PlayerRig
    /// are activated via the Show()/Hide() pattern.
    /// </summary>
    public class GameArea : MonoBehaviour
    {
        // ---- Static tracking ----
        public static GameArea currentGameArea;
        public static readonly List<GameArea> allGameAreas = new();

        /// <summary>
        /// Verbose zone enter/exit logging. Off by default: these logs fire on every
        /// transition and capture a managed stack trace, which causes frame spikes when
        /// zones change rapidly (and they run in builds too). Flip on for debugging.
        /// </summary>
        public static bool VerboseLogging = false;

        // Cached main-camera lookup shared by every GameArea. Camera.main does a tagged
        // object search internally; with one GameArea per prop that was happening twice
        // per prop, every frame. Resolve it once and reuse until the camera is replaced.
        static Camera _mainCamera;
        static Camera MainCamera => _mainCamera != null ? _mainCamera : (_mainCamera = Camera.main);

        /// <summary>
        /// Fired when the active content zone changes.
        /// Args: (previousGameArea, newGameArea). Either may be null.
        /// </summary>
        public static event Action<GameArea, GameArea> OnContentZoneChanged;

        // ---- Instance fields ----
        [ReadOnly] public string gameId;
        [ReadOnly] public string resourceName;

        public int priority = 0;
        [ReadOnly] public bool isPlaying = false;

        [Tooltip("Extra meters added around the template footprint on X and Z. " +
                 "Expands the activation zone so the player rig pre-switches before " +
                 "the player is strictly inside the template bounds.")]
        public float padding = 2f;

        [Tooltip("Show the activation-zone visualization in the Scene view. " +
                 "Disable to hide the padded boundary entirely.")]
        public bool showPaddingGizmo = true;

        /// <summary>
        /// World-space half-extents of this zone's bounding box, including <see cref="padding"/>.
        /// Computed from LevelTemplate size or PropTemplate footprint.
        /// </summary>
        [HideInInspector] public Vector3 halfExtents = Vector3.zero;

        /// <summary>
        /// Half-extents without padding — the "true" template footprint.
        /// Kept around for gizmos / debugging so we can visualize how much of the
        /// activation zone is padding vs. actual content.
        /// </summary>
        [HideInInspector] public Vector3 unpaddedHalfExtents = Vector3.zero;

        /// <summary>
        /// Unique color for debug visualization. Assigned automatically from gameId hash.
        /// </summary>
        [HideInInspector] public Color zoneColor = Color.white;

        void Awake()
        {
            ComputeBounds();
            zoneColor = GameAreaColorFromId(gameId);
            allGameAreas.Add(this);
        }

#if UNITY_EDITOR
        // Recompute bounds whenever the inspector mutates this component so that
        // padding / gameId changes are reflected immediately in the gizmo.
        void OnValidate()
        {
            ComputeBounds();
            zoneColor = GameAreaColorFromId(gameId);
        }
#endif

        void OnEnable()
        {
            if (!allGameAreas.Contains(this))
                allGameAreas.Add(this);
        }

        void OnDisable()
        {
            if (isPlaying)
                Exit();
            allGameAreas.Remove(this);
        }

        void OnDestroy() {
            allGameAreas.Remove(this);
            if (currentGameArea == this) {
                var previous = currentGameArea;
                currentGameArea = null;
                OnContentZoneChanged?.Invoke(previous, null);
            }
        }

        /// <summary>
        /// Compute the bounding half-extents from the attached LevelTemplate or PropTemplate.
        /// Called on Awake and can be called again if the template changes.
        /// </summary>
        public void ComputeBounds()
        {
            if (TryGetComponent(out LevelTemplate levelTemplate)) {
                Vector2 bounds2D;
                if (levelTemplate.size == GameLevelSize.Custom)
                    bounds2D = GameLevelDimensions.GetDimensionsInMeters(levelTemplate.customSize);
                else
                    bounds2D = GameLevelDimensions.GetDimensionsInMeters(levelTemplate.size);
                unpaddedHalfExtents = new Vector3(bounds2D.x / 2f, 50f, bounds2D.y / 2f);
            } else if (TryGetComponent(out PropTemplate propTemplate)) {
                // PropTemplates use custom footprint dimensions
                var footprint = propTemplate.customFootprintMeters;
                if (footprint.x > 0f && footprint.y > 0f)
                    unpaddedHalfExtents = new Vector3(footprint.x / 2f, 50f, footprint.y / 2f);
            }

            // Apply padding on the horizontal axes only; Y already covers a tall slab.
            float pad = Mathf.Max(0f, padding);
            halfExtents = new Vector3(
                unpaddedHalfExtents.x + pad,
                unpaddedHalfExtents.y,
                unpaddedHalfExtents.z + pad);
        }

        void Update () {
            var cam = MainCamera;
            if (!cam)
                return;

            if (IsPointWithinBounds(cam.transform.position)) {
                if (!isPlaying) {
                    Enter();
                }
            } else {
                if (isPlaying) {
                    Exit();
                }
            }
        }

        public void Enter()
        {
            if (currentGameArea == this)
                return;

            if (currentGameArea != null) {
                if (priority > currentGameArea.priority) {
                    currentGameArea.ForceExit();
                } else {
                    return;
                }
            }

            var previous = currentGameArea;
            currentGameArea = this;
            isPlaying = true;

            if (PlayerRig.instances != null && PlayerRig.instances.ContainsKey(gameId))
            {
                PlayerRig.instances[gameId].Show();
            }

            if (VerboseLogging)
                Debug.Log($"[GameArea] Entered zone: {gameId} (priority {priority})");
            OnContentZoneChanged?.Invoke(previous, this);
        }

        public void Exit()
        {
            if (!isPlaying)
                return;

            isPlaying = false;

            if (currentGameArea == this) {
                currentGameArea = null;
                if (VerboseLogging)
                    Debug.Log($"[GameArea] Exited zone: {gameId}");
                // Check if player is in another zone (nearest-zone fallback)
                // ContentZoneManager handles this via the event
                OnContentZoneChanged?.Invoke(this, null);
            }
        }

        /// <summary>
        /// Force exit without firing events (used when a higher-priority zone takes over).
        /// </summary>
        private void ForceExit()
        {
            isPlaying = false;
            // No event fired — the entering zone will fire the combined event
        }

        /// <summary>
        /// Test if a world-space point is inside this zone's oriented bounding box.
        /// </summary>
        public bool IsPointWithinBounds(Vector3 worldPoint)
        {
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
            return Mathf.Abs(localPoint.x) <= halfExtents.x &&
                Mathf.Abs(localPoint.y) <= halfExtents.y &&
                Mathf.Abs(localPoint.z) <= halfExtents.z;
        }

        /// <summary>
        /// Get the signed distance from a world point to the nearest edge of this zone.
        /// Negative = inside, positive = outside. Used for nearest-zone calculations.
        /// </summary>
        public float SignedDistanceTo(Vector3 worldPoint)
        {
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
            float dx = Mathf.Abs(localPoint.x) - halfExtents.x;
            float dz = Mathf.Abs(localPoint.z) - halfExtents.z;
            // Ignore Y for horizontal distance
            float outsideDist = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dz, 0f) * Mathf.Max(dz, 0f));
            float insideDist = Mathf.Min(Mathf.Max(dx, dz), 0f);
            return outsideDist + insideDist;
        }

        /// <summary>
        /// Get the 4 world-space corner points of this zone (on the XZ plane at y=0).
        /// Used for debug gizmo drawing.
        /// </summary>
        public Vector3[] GetWorldCorners()
        {
            var corners = new Vector3[4];
            corners[0] = transform.TransformPoint(new Vector3(-halfExtents.x, 0, -halfExtents.z));
            corners[1] = transform.TransformPoint(new Vector3(-halfExtents.x, 0,  halfExtents.z));
            corners[2] = transform.TransformPoint(new Vector3( halfExtents.x, 0,  halfExtents.z));
            corners[3] = transform.TransformPoint(new Vector3( halfExtents.x, 0, -halfExtents.z));
            return corners;
        }

        /// <summary>
        /// Deterministic color from gameId for debug visualization.
        /// </summary>
        private static Color GameAreaColorFromId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return Color.gray;
            int hash = id.GetHashCode();
            float h = Mathf.Abs(hash % 360) / 360f;
            return Color.HSVToRGB(h, 0.7f, 0.9f);
        }

        /// <summary>
        /// Find the nearest GameArea to a world position. Returns null if no GameAreas exist.
        /// </summary>
        public static GameArea FindNearest(Vector3 worldPosition)
        {
            GameArea nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var area in allGameAreas) {
                if (area == null) continue;
                float dist = area.SignedDistanceTo(worldPosition);
                if (dist < nearestDist) {
                    nearestDist = dist;
                    nearest = area;
                }
            }
            return nearest;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!showPaddingGizmo) return;

            // In edit mode, always recompute so inspector tweaks (padding here or
            // size on a sibling LevelTemplate) show up instantly in the gizmo.
            // At play-time we trust the cached bounds to avoid per-frame work.
            if (!Application.isPlaying)
                ComputeBounds();
            else if (halfExtents == Vector3.zero)
                ComputeBounds();

            if (halfExtents == Vector3.zero)
                return;

            // CHEAP per-frame draw only. Because every prop carries a GameArea
            // ([RequireComponent]), OnDrawGizmos runs for EVERY prop every editor frame.
            // Handles.* calls (DrawDottedLine, and especially the IMGUI-backed Label) are
            // 10-100x more expensive than Gizmos primitives and were the cause of the
            // 40ms+ editor frame spikes. Keep this path to a single batched wire cube; the
            // rich dotted-perimeter + label visualization moves to OnDrawGizmosSelected,
            // which only runs for the one selected object.
            Color c = GameAreaColorFromId(gameId);
            bool isActive = currentGameArea == this;

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(c.r, c.g, c.b, isActive ? 0.85f : 0.35f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(halfExtents.x * 2, 0.02f, halfExtents.z * 2));
            Gizmos.matrix = oldMatrix;
        }

        // Detailed visualization (dotted activation perimeter + label) for the SELECTED
        // zone only. Runs at most once per frame instead of once per prop, so the
        // expensive Handles.* calls no longer scale with prop count.
        void OnDrawGizmosSelected()
        {
            if (!showPaddingGizmo) return;

            if (!Application.isPlaying)
                ComputeBounds();
            if (halfExtents == Vector3.zero)
                return;

            Color c = GameAreaColorFromId(gameId);
            bool isActive = currentGameArea == this;

            // Outer "padded" activation perimeter — dotted line at floor level.
            Vector3 hx = halfExtents;
            Vector3[] padCorners = {
                transform.TransformPoint(new Vector3(-hx.x, 0, -hx.z)),
                transform.TransformPoint(new Vector3( hx.x, 0, -hx.z)),
                transform.TransformPoint(new Vector3( hx.x, 0,  hx.z)),
                transform.TransformPoint(new Vector3(-hx.x, 0,  hx.z)),
            };
            UnityEditor.Handles.color = new Color(c.r, c.g, c.b, isActive ? 0.85f : 0.35f);
            for (int i = 0; i < 4; i++)
                UnityEditor.Handles.DrawDottedLine(padCorners[i], padCorners[(i + 1) % 4], 4f);

            // Inner "true footprint" when padding actually contributes area.
            if (unpaddedHalfExtents != Vector3.zero &&
                (unpaddedHalfExtents.x < halfExtents.x || unpaddedHalfExtents.z < halfExtents.z))
            {
                Matrix4x4 oldMatrix = Gizmos.matrix;
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = new Color(c.r, c.g, c.b, isActive ? 0.5f : 0.2f);
                Gizmos.DrawWireCube(
                    Vector3.zero,
                    new Vector3(unpaddedHalfExtents.x * 2, 0.02f, unpaddedHalfExtents.z * 2));
                Gizmos.matrix = oldMatrix;
            }

            // Label, anchored just above the GameObject origin.
            UnityEditor.Handles.color = c;
            string label = isActive ? $"[ACTIVE] {gameId}" : gameId;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f, label);
        }
#endif
    }
}