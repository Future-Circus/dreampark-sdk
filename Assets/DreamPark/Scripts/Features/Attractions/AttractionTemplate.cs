namespace DreamPark {
    using UnityEngine;

    /// <summary>
    /// Which of an attraction's 4 footprint edges have a real-world wall behind
    /// them, in the attraction's OWN local frame — Forward/Back run along local Z
    /// (LevelTemplate's "length"), Right/Left along local X ("width"), the same
    /// axis convention LevelTemplate's floor and PropTemplate's footprint already
    /// use (transform.forward / transform.right). Combinable: two adjacent sides
    /// describe a corner, two opposite sides describe a through-wall (e.g. a
    /// portal window), one side is the common case.
    ///
    /// Published with the attraction's dimensions upload
    /// (POST /api/content/{id}/attractions/dimensions) as a comma-joined string —
    /// [Flags] enums stringify that way for free — so the developer portal,
    /// mobile app, and the park-packer's wall-placement pass can all read "which
    /// sides need a wall" without an SDK reference.
    /// </summary>
    [System.Flags]
    public enum WallSide
    {
        None = 0,
        Forward = 1 << 0,
        Back = 1 << 1,
        Right = 1 << 2,
        Left = 1 << 3
    }

    public class AttractionTemplate : LevelTemplate {

        // Clearer naming convention than LevelTemplate
        // Attractions are self-contained things like an arcade game, boss battle, or challenge course
        // Props are the individual elements that make up an Attraction
        // Parks contain Attractions & Props

        /// <summary>
        /// Which footprint edges this attraction needs a real wall behind, e.g. a
        /// portal that opens a room through it, or a torch/sign mount. Any
        /// combination is valid — a corner attraction sets two adjacent sides, a
        /// pass-through sets two opposite sides. Purely descriptive metadata: it
        /// does not affect the floor/footprint the rest of LevelTemplate builds,
        /// only what gets uploaded and drawn in the gizmo below.
        /// </summary>
        [Tooltip("Which of the 4 footprint edges need a real wall behind them. Combine any sides — two adjacent sides for a corner, two opposite sides for a pass-through (e.g. a portal). Published with this attraction's dimensions so layout/AI tools can place it against a real wall.")]
        public WallSide walls = WallSide.None;

        [Tooltip("Draw the required wall(s) as a gizmo plane when this attraction is selected.")]
        public bool showWallGizmos = true;

        /// <summary>
        /// Minimum wall height this SDK will ever ask for, in meters (10 ft).
        /// Matched by PropTemplate's own constant of the same value — kept as two
        /// separate consts rather than one shared one because the two components
        /// don't otherwise reference each other's internals.
        /// </summary>
        private const float DefaultWallHeightMeters = 3.048f;

        /// <summary>
        /// The wall height this attraction needs, in meters: the 10 ft default,
        /// or taller if the attraction's own content reaches higher (e.g. a tall
        /// portal frame) — never shorter, since a wall that stops short of the
        /// content it is meant to back is worse than an oversized one. Renderer
        /// bounds are walked fresh rather than cached because authored content
        /// changes size in the editor far more often than this is asked.
        /// </summary>
        public float GetWallHeightMeters()
        {
            float contentHeight = 0f;
            var renderers = GetComponentsInChildren<Renderer>(true);
            float floorY = transform.position.y;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                float top = r.bounds.max.y - floorY;
                if (top > contentHeight) contentHeight = top;
            }
            return Mathf.Max(DefaultWallHeightMeters, contentHeight);
        }

#if UNITY_EDITOR
        private static readonly Color WallGizmoColor = new Color(0.1f, 0.6f, 1f, 1f);
        private static readonly Color WallGizmoFill = new Color(0.1f, 0.6f, 1f, 0.15f);

        // Selected-only, matching PropTemplate's own footprint gizmo: walking
        // GetComponentsInChildren<Renderer> for GetWallHeightMeters() every
        // editor frame for every attraction in the scene would be the same
        // per-frame tax PropTemplate's footprint gizmo was moved off of.
        private void OnDrawGizmosSelected()
        {
            if (!showWallGizmos || walls == WallSide.None) return;

            // Local X = width ("Size.x"), local Z = length ("Size.z") — the same
            // frame LevelTemplate.OnDrawGizmos draws the floor rectangle in.
            Vector2 dims = new Vector2(Size.x, Size.z);
            float height = GetWallHeightMeters();

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            DrawWallIfSet(WallSide.Forward, new Vector3(0f, height * 0.5f, dims.y * 0.5f), new Vector3(dims.x, height, 0.05f));
            DrawWallIfSet(WallSide.Back, new Vector3(0f, height * 0.5f, -dims.y * 0.5f), new Vector3(dims.x, height, 0.05f));
            DrawWallIfSet(WallSide.Right, new Vector3(dims.x * 0.5f, height * 0.5f, 0f), new Vector3(0.05f, height, dims.y));
            DrawWallIfSet(WallSide.Left, new Vector3(-dims.x * 0.5f, height * 0.5f, 0f), new Vector3(0.05f, height, dims.y));

            Gizmos.matrix = oldMatrix;
        }

        private void DrawWallIfSet(WallSide side, Vector3 center, Vector3 size)
        {
            if ((walls & side) == 0) return;
            Gizmos.color = WallGizmoFill;
            Gizmos.DrawCube(center, size);
            Gizmos.color = WallGizmoColor;
            Gizmos.DrawWireCube(center, size);
        }
#endif
    }
}
