using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// Spatial zone detection for content areas. Each LevelTemplate/AttractionTemplate
    /// has a GameArea that tracks whether the player's head is inside its bounds.
    /// When the player enters a new zone, the corresponding PlayerRig and DreamBand
    /// are activated via the Show()/Hide() pattern.
    /// </summary>
    public class GameArea : MonoBehaviour
    {
        // ---- Static tracking ----
        public static GameArea currentGameArea;
        public static readonly List<GameArea> allGameAreas = new();

        /// <summary>
        /// Fired when the active content zone changes.
        /// Args: (previousGameArea, newGameArea). Either may be null.
        /// </summary>
        public static event Action<GameArea, GameArea> OnContentZoneChanged;

        // ---- Instance fields ----
        [ReadOnly] public string gameId;
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
            if (Camera.main) {
                if (IsPointWithinBounds(Camera.main.transform.position)) {
                    if (!isPlaying) {
                        Enter();
                    }
                } else {
                    if (isPlaying) {
                        Exit();
                    }
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

            if (DreamBand.instances != null && DreamBand.instances.ContainsKey(gameId))
            {
                DreamBand.instances[gameId].Show();
            }

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

            Color c = GameAreaColorFromId(gameId);
            bool isActive = currentGameArea == this;

            // Outer "padded" perimeter — drawn as a dotted line at floor level.
            // This is the closest Unity's gizmo API gets to a "fuzzy / dithered"
            // boundary without a custom mesh+shader. Uses Handles in world space
            // (TransformPoint) so it rotates with the GameObject. The dot spacing
            // is in screen-space pixels (4f gives a fine, soft pattern that
            // scales naturally at any zoom level).
            Vector3 hx = halfExtents;
            Vector3[] padCorners = {
                transform.TransformPoint(new Vector3(-hx.x, 0, -hx.z)),
                transform.TransformPoint(new Vector3( hx.x, 0, -hx.z)),
                transform.TransformPoint(new Vector3( hx.x, 0,  hx.z)),
                transform.TransformPoint(new Vector3(-hx.x, 0,  hx.z)),
            };

            UnityEditor.Handles.color = new Color(c.r, c.g, c.b, isActive ? 0.85f : 0.35f);
            for (int i = 0; i < 4; i++)
            {
                UnityEditor.Handles.DrawDottedLine(padCorners[i], padCorners[(i + 1) % 4], 4f);
            }

            // Inner "true footprint" — drawn as a faint flat wireframe at floor
            // level only when padding is actually contributing area. This stays
            // out of the way until the creator wants to see exactly where the
            // play surface ends vs. where the activation zone begins.
            if (unpaddedHalfExtents != Vector3.zero &&
                (unpaddedHalfExtents.x < halfExtents.x || unpaddedHalfExtents.z < halfExtents.z))
            {
                Matrix4x4 oldMatrix = Gizmos.matrix;
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = new Color(c.r, c.g, c.b, isActive ? 0.5f : 0.2f);
                // Y extent is intentionally tiny (0.02) so this reads as a flat
                // rectangle outline rather than a tall extruded box.
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