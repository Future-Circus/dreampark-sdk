namespace DreamPark
{
    using UnityEngine;

    /// <summary>
    /// Holds the overhead depth-mask plane LEVEL and a fixed distance above the eye.
    /// Authored onto a plane under HeadTracker, as a sibling of DepthMaskDistance.
    ///
    /// WHY THERE IS A SECOND MASK AT ALL. DepthMaskDistance is 40 x 15 m at z = 7, and
    /// at MaskBias 0.4 it writes its depth at 0.6 * 7 = 4.2 m — so it exempts real
    /// geometry FARTHER than 4.2 m and nothing nearer. A real ceiling sits under a
    /// metre and a half above your eyes, well inside that, so it keeps occluding and
    /// keeps flickering. This plane sits at 0.8 m, writes at ~0.5 m straight up, and
    /// clears exactly that near-overhead band. Being CLOSE is the whole point of it;
    /// it is not covering angles the front mask misses (that one pitches with the head
    /// and covers +/-47 degrees of wherever you look), it is covering distances.
    ///
    /// WHICH BOUNDS heightAboveHead FROM ABOVE. The exemption only reaches past a real
    /// surface while 0.4 * height stays below it, so a plane raised toward the real
    /// ceiling stops clearing it. Lower is stronger, and "just above everything" is the
    /// wrong instinct — content height is irrelevant, since the mask edits the
    /// real-world depth map and never clips virtual geometry.
    ///
    /// WHY IT CANNOT JUST BE PARENTED AND LEFT ALONE. HeadTracker copies the head's
    /// full rotation, and DepthMaskDistance WANTS that — staying square to your view is
    /// what keeps it in front of you. This one wants the inverse: the head's position,
    /// and of its orientation only the yaw. An untouched child pitches with the head and
    /// "up" swings behind you the moment you look up. That single inversion is the whole
    /// reason this file exists rather than the plane being pure authoring.
    /// </summary>
    [DisallowMultipleComponent]
    public class DepthMaskCeiling : MonoBehaviour
    {
        private static DepthMaskCeiling _instance;

        /// <summary>
        /// True while an enabled ceiling plane exists. A STATUS READOUT FOR DEBUG UI, and
        /// nothing more — it is not the gate, and nothing currently reads it.
        ///
        /// It once was the plan: let the templates check this, so unchecking the plane on
        /// the rig handed the ceilings back to them. That fails exactly where it matters.
        /// An SDK creator in the editor has no rig at all, so Active is false there and
        /// every attraction and prop would quietly build the legacy patchwork the plane
        /// exists to replace — the editor would stop resembling the headset precisely
        /// when someone was using it to judge occlusion. AllowTemplateCeilings below is
        /// the real switch, and it is off everywhere until a human turns it on.
        /// </summary>
        public static bool Active => _instance != null && _instance.isActiveAndEnabled;

        /// <summary>
        /// Master switch for the LEGACY per-attraction and per-prop ceilings. OFF.
        ///
        /// Deliberately a static rather than a lowered default on LevelTemplate's
        /// serialized generateCeiling flag. Unity only applies a field's default where no
        /// value was serialized, and every shipped attraction prefab already has
        /// generateCeiling serialized TRUE — so flipping that default would change
        /// nothing whatsoever for content already on disk, while looking like it had.
        /// A code-side gate is the only kind that is genuinely off for existing content.
        ///
        /// PropTemplate has no such flag and must not grow one. Props never shipped with
        /// ceiling fields, so adding any would write fresh lines into every prop prefab
        /// the next time ContentProcessor re-saves one — re-bundling the whole catalog to
        /// carry a value this gate already overrules. Both templates size their legacy
        /// ceilings from private CeilingPadding / CeilingHeight constants for the same
        /// reason: an author cannot reach the legacy path, so nothing about it belongs in
        /// serialized content.
        ///
        /// Set true (debug menu, test harness, one line in a bootstrap) to bring the old
        /// per-template ceilings back for a comparison run.
        /// </summary>
        public static bool AllowTemplateCeilings = false;

        [Tooltip("Metres above the player's EYE, not above the floor. LOWER IS STRONGER: " +
                 "it lowers the distance at which real geometry stops occluding. Must stay " +
                 "comfortably below the real ceiling, or the exemption no longer reaches it.")]
        public float heightAboveHead = 0.8f;

        [Tooltip("Orientation forced every frame. It has to point the mesh's normal DOWN at " +
                 "the player: mask meshes are rendered with back-face culling, so a plane " +
                 "facing up masks nothing at all. (180, 0, 0) is correct for Unity's built-in " +
                 "Plane, whose normal is +Y. A Quad, whose normal is -Z, wants (-90, 0, 0).")]
        public Vector3 levelEulerAngles = new Vector3(180f, 0f, 0f);

        [Tooltip("Turn with the head about the VERTICAL axis while staying flat. Only " +
                 "observable on a plane that is not square — a square one covers the same " +
                 "ground however it is spun. On a duplicate of DepthMaskDistance, which is " +
                 "40 x 15, this keeps the 40 m axis across your view.")]
        public bool matchHeadYaw = true;

        [Tooltip("Transform to follow. Empty uses Camera.main, which is the authoritative " +
                 "head pose — deliberately not the HeadTracker parent, which also writes " +
                 "itself in LateUpdate and would leave this a frame behind.")]
        public Transform head;

        private static Camera _mainCamera;

        private void OnEnable()
        {
            _instance = this;
            Snap();
            Debug.Log($"[DepthMaskCeiling] Active — level plane {heightAboveHead}m above the eye. " +
                      "Per-template ceilings are off.");
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            Snap();
        }

        private void Snap()
        {
            Transform h = ResolveHead();
            if (h == null) return;

            // Position comes from the head; orientation never does, beyond yaw. Note the
            // OFFSET has to be overridden too, not just the rotation: left as a plain
            // child at localPosition (0, 0.8, 0), the offset itself would rotate with the
            // parent and the plane would slide forward and back as you pitch.
            Quaternion flat = Quaternion.Euler(levelEulerAngles);

            transform.SetPositionAndRotation(
                h.position + Vector3.up * heightAboveHead,
                matchHeadYaw ? HeadYaw(h) * flat : flat);
        }

        /// <summary>
        /// The head's rotation about the vertical axis alone.
        ///
        /// NOT Quaternion.Euler(0, head.eulerAngles.y, 0), which is the obvious way to
        /// write this and fails exactly where this plane earns its keep. That euler
        /// decomposition is ambiguous as pitch approaches vertical, so the extracted yaw
        /// flips and the plane spins under you at the moment you look straight up at it.
        ///
        /// Projecting forward onto the horizontal is stable everywhere except the poles,
        /// where forward IS vertical and projects to nothing — and there the head's own
        /// up vector is horizontal and stands in for it.
        /// </summary>
        private static Quaternion HeadYaw(Transform head)
        {
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f)
                flat = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            return Quaternion.LookRotation(flat.normalized, Vector3.up);
        }

        private Transform ResolveHead()
        {
            if (head != null) return head;
            if (_mainCamera == null) _mainCamera = Camera.main;
            return _mainCamera != null ? _mainCamera.transform : null;
        }
    }
}
