using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class HandTracker : MonoBehaviour
{
    private static readonly Color DreamBandGizmoColor = new Color(0.5f, 0f, 1f, 0.85f);
    private static readonly Vector3 DreamBandLocalOffset = new Vector3(-0.0003510714f, 0.01352847f, -0.004948974f);
    private static readonly Vector3 DreamBandLocalEulerOffset = new Vector3(-78.802f, 0f, -180f);
    private static readonly Vector3 DreamBandLocalScale = new Vector3(100f, 100f, 100f);
    private static readonly Color HandGizmoColor = new Color(0.96f, 0.91f, 0.75f, 0.2f);
    private static readonly Vector3 HandLocalOffset = new Vector3(0.00210005f, 0.01952827f, 0.006667495f);
    private static readonly Vector3 HandLocalEulerOffset = new Vector3(-98.565f, -180.001f, 6.408005f);
    private static readonly Vector3 HandLocalScale = new Vector3(99.99998f, 99.99998f, 99.99998f);
    private static Mesh dreamBandGizmoMesh;
    private static Mesh handGizmoMesh;

    public enum HandPreference
    {
        Left,
        Right,
        Both
    }

    public HandPreference handPreference = HandPreference.Both;
    public OVRHand leftHand, rightHand;
    public bool flipVisual = false;
    [ShowIf("flipVisual")]
    public Vector3 flipVisualRotation = new Vector3(180, 180, 0);
    [ShowIf("flipVisual")]
    public Vector3 flipVisualPosition = new Vector3(0, 0, 0);
    private Transform activeHandAnchor;
    private OVRHand activeHand;
    Transform leftAnchor;
    Transform rightAnchor;
    private Rigidbody rb;
    private bool isActive = false;

    public float enableDelay = 0.1f;

    // ── Jointed hand bodies (Oct 5 2026) ────────────────────────────────
    //
    // The common rig shape (LaserTag, Zombiez, ArcadeLand…): this GameObject
    // carries a KINEMATIC Rigidbody that UpdateStep drives with rb.position,
    // and a child carries a DYNAMIC Rigidbody FixedJoint-ed to it (HandTouch,
    // Collider — the blaster, weapon pickups, paddles hang off it). A
    // FixedJoint stores no offset of its own: it records the two bodies'
    // relative pose at the moment the constraint is (re)built — first
    // activation, every re-activation of the rig (PlayerRig Hide/Show, a
    // package handing the rig to a new owner, EnableChildren after tracking
    // loss) — and holds that pose forever after. rb.position only moves the
    // kinematic BODY; this transform (and so the child, which follows the
    // hierarchy) only catches up after the next physics step. So a rebuild
    // that lands between "tracker body teleported to the hand" and "child
    // caught up" records the whole jump as the joint's offset: the hand
    // object then rides metres away from the hand for the rest of the
    // session (a Zombiez creator measured 3.5 m). SuperAdventureLand never
    // hits it because its tracker has NO Rigidbody — it moves the transform,
    // so its jointed Fist and the kinematic FistAnchor it is jointed to move
    // together through the hierarchy and can never be apart at a rebuild.
    //
    // Fix: remember each such child's AUTHORED pose relative to this tracker,
    // and whenever a rebuild could have captured a stale offset (enable,
    // children re-enabled, a big jump of the target) or the child has
    // demonstrably drifted off its seat, put tracker + child back on the
    // authored relation and rebuild the joint there.
    private struct JointSeat
    {
        public Rigidbody body;
        public Joint joint;
        public Vector3 localPosition;   // in this transform's space (scale included)
        public Quaternion localRotation;
    }

    private readonly System.Collections.Generic.List<JointSeat> jointSeats =
        new System.Collections.Generic.List<JointSeat>();
    private bool reseatPending = true;
    private float driftSince = -1f;
    // A target that moves farther than this in one step is a teleport/jump.
    private const float ReseatJumpMeters = 0.5f;
    // A seated body this far from its seat for this long has a bad joint
    // offset; ordinary joint lag at hand speed is centimetres and transient.
    private const float DriftMeters = 0.25f;
    private const float DriftSeconds = 0.25f;

    public OVRHand ActiveHand
    {
        get { return activeHand; }
    }

    void Awake()
    {
        CaptureJointSeats();
        if (leftHand == null && rightHand == null)
        {
            OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.InstanceID);
            foreach (OVRHand hand in hands)
            {
                if (hand.GetHand() == OVRPlugin.Hand.HandLeft)
                {
                    leftHand = hand;
                }
                else if (hand.GetHand() == OVRPlugin.Hand.HandRight)
                {
                    rightHand = hand;
                }
            }
        }
    }

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        FindHandAnchors();
    }

    private void OnEnable()
    {
        // Every re-activation rebuilds the joints (see JointSeat).
        reseatPending = true;
        driftSince = -1f;
    }

    /// Record, at the authored pose (Awake runs before anything moves the
    /// rig), every dynamic body below this tracker that is FixedJoint-ed to
    /// the tracker's own Rigidbody.
    private void CaptureJointSeats()
    {
        jointSeats.Clear();
        Rigidbody own = GetComponent<Rigidbody>();
        if (own == null) return;
        foreach (FixedJoint joint in GetComponentsInChildren<FixedJoint>(true))
        {
            if (joint == null || joint.connectedBody != own) continue;
            Rigidbody body = joint.GetComponent<Rigidbody>();
            if (body == null || body == own) continue;
            Transform t = body.transform;
            jointSeats.Add(new JointSeat
            {
                body = body,
                joint = joint,
                localPosition = transform.InverseTransformPoint(t.position),
                localRotation = Quaternion.Inverse(transform.rotation) * t.rotation,
            });
        }
    }

    private bool SeatIsLive(JointSeat seat)
    {
        return seat.body != null && seat.joint != null && rb != null
            && seat.joint.connectedBody == rb
            && seat.body.gameObject.activeInHierarchy
            && seat.body.transform.IsChildOf(transform);
    }

    /// True when some live seated body sits off its seat (relative to the
    /// tracker's CURRENT body pose) for DriftSeconds.
    private bool SeatsDrifted(Vector3 trackerPosition, Quaternion trackerRotation)
    {
        bool off = false;
        Matrix4x4 frame = Matrix4x4.TRS(trackerPosition, trackerRotation, transform.lossyScale);
        foreach (JointSeat seat in jointSeats)
        {
            if (!SeatIsLive(seat)) continue;
            Vector3 want = frame.MultiplyPoint3x4(seat.localPosition);
            if ((seat.body.position - want).sqrMagnitude > DriftMeters * DriftMeters) { off = true; break; }
        }
        if (!off) { driftSince = -1f; return false; }
        if (driftSince < 0f) { driftSince = Time.time; return false; }
        return Time.time - driftSince >= DriftSeconds;
    }

    /// Put the tracker (body AND transform) on the target, every seated body
    /// on its authored seat, and rebuild each joint there so the relation it
    /// records is the authored one.
    private void ReseatJointedBodies(Vector3 position, Quaternion rotation)
    {
        reseatPending = false;
        driftSince = -1f;
        if (rb == null || jointSeats.Count == 0) return;
        rb.position = position;
        rb.rotation = rotation;
        transform.SetPositionAndRotation(position, rotation);
        int reseated = 0;
        foreach (JointSeat seat in jointSeats)
        {
            if (!SeatIsLive(seat)) continue;
            Vector3 p = transform.TransformPoint(seat.localPosition);
            Quaternion r = rotation * seat.localRotation;
            seat.body.transform.SetPositionAndRotation(p, r);
            seat.body.position = p;
            seat.body.rotation = r;
            if (!seat.body.isKinematic)
            {
                seat.body.linearVelocity = Vector3.zero;
                seat.body.angularVelocity = Vector3.zero;
            }
            // Re-assigning the connected body rebuilds the constraint from
            // the relative pose the two bodies have NOW — the authored one.
            Rigidbody connected = seat.joint.connectedBody;
            seat.joint.connectedBody = null;
            seat.joint.connectedBody = connected;
            reseated++;
        }
        if (reseated > 0) Physics.SyncTransforms();
    }

    void FindHandAnchors()
    {
        if (leftAnchor == null) {
            leftAnchor = GameObject.Find("LeftHandAnchor")?.transform;
        }
        if (rightAnchor == null) {
            rightAnchor = GameObject.Find("RightHandAnchor")?.transform;
        }

        if (leftAnchor == null || rightAnchor == null)
        {
            return;
        }

        // Bind by GetHand(), never by InstanceID order — that assignment
        // swapped left/right whenever the right hand happened to sort first,
        // and in the editor this method runs every frame.
        if (leftHand == null || rightHand == null)
        {
            OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            foreach (OVRHand hand in hands)
            {
                if (hand == null) continue;
                if (hand.GetHand() == OVRPlugin.Hand.HandLeft) leftHand = hand;
                else if (hand.GetHand() == OVRPlugin.Hand.HandRight) rightHand = hand;
            }
        }
        ChooseHand();
    }

    void ChooseHand()
    {
        switch (handPreference)
        {
            case HandPreference.Left:
                if (leftHand != null && leftHand.IsTracked)
                {
                    activeHandAnchor = leftAnchor;
                    activeHand = leftHand;
                }
                break;
            case HandPreference.Right:
                if (rightHand != null && rightHand.IsTracked)
                {
                    activeHandAnchor = rightAnchor;
                    activeHand = rightHand;
                }
                break;
            case HandPreference.Both:
                // Stick to the hand we already have while it is still
                // tracked. The old path always took left if left.IsTracked,
                // so a right-hand lock was stolen the moment the left hand
                // appeared and could never come back.
                {
                    bool leftTracked = leftHand != null && leftHand.IsTracked;
                    bool rightTracked = rightHand != null && rightHand.IsTracked;
                    bool currentTracked =
                        (activeHand == leftHand && leftTracked) ||
                        (activeHand == rightHand && rightTracked);

                    if (currentTracked)
                        break;

                    if (rightTracked && !leftTracked)
                    {
                        activeHand = rightHand;
                        activeHandAnchor = rightAnchor;
                    }
                    else if (leftTracked && !rightTracked)
                    {
                        activeHand = leftHand;
                        activeHandAnchor = leftAnchor;
                    }
                    else if (leftTracked && rightTracked)
                    {
                        // First lock, both already up. Keep a previous
                        // assignment if we have one; otherwise do not
                        // default to left.
                        if (activeHand == leftHand || activeHandAnchor == leftAnchor)
                        {
                            activeHand = leftHand;
                            activeHandAnchor = leftAnchor;
                        }
                        else
                        {
                            activeHand = rightHand;
                            activeHandAnchor = rightAnchor;
                        }
                    }
                }
                break;
        }
    }

    void UpdateStep()
    {
#if UNITY_EDITOR
        // FindHandAnchors() ends by calling ChooseHand(), so the device
        // selection logic runs here too — if the editor is reporting real
        // tracking (Meta XR Simulator, Link) this behaves exactly as a
        // headset does, handPreference and all.
        FindHandAnchors();

        // Nothing tracked is the NORMAL editor case: there is no hand to
        // raise. Fall back to the preferred hand so the rig is still usable,
        // which is why this branch existed at all — it just used to hardcode
        // the left one, so handPreference was unreachable in the editor and
        // right-handed content could not be tested without a headset.
        // DreamPark ▸ Hand Tracking sets that preference.
        if (!isActiveAndTracking)
        {
            bool preferRight = handPreference == HandPreference.Right;
            activeHand       = preferRight ? rightHand   : leftHand;
            activeHandAnchor = preferRight ? rightAnchor : leftAnchor;
        }
#else
        if (!activeHandAnchor) {
            FindHandAnchors();
            return;
        }
        ChooseHand();
#endif

        // No anchor to follow yet (rig still binding, or the preferred hand has no
        // anchor in this scene, e.g. the Park Simulator rig). The device path above
        // already returns here; without this the editor path threw an NRE every
        // frame at the NaN check below.
        if (!activeHandAnchor) return;

        if (!isActiveAndTracking && isActive) {
            DisableChildren();
            isActive = false;
        }

        if (activeHandAnchor.transform && (float.IsNaN(activeHandAnchor.position.x) || float.IsNaN(activeHandAnchor.position.y) || float.IsNaN(activeHandAnchor.position.z))) {
            Debug.Log("BIG WARNING: Hand tracking anchor is NaN, setting to zero");
            activeHandAnchor.transform.position = Vector3.zero;
            activeHandAnchor.transform.rotation = Quaternion.identity;
            return;
        }

        var flippedRotation = Quaternion.identity;
        var flippedPosition = Vector3.zero;

        if (flipVisual && activeHandAnchor == leftAnchor)
        {
            flippedRotation = Quaternion.Euler(flipVisualRotation);
            flippedPosition = flipVisualPosition;
        }

        if (rb != null)
        {
            Vector3 targetPosition = activeHandAnchor.TransformPoint(flippedPosition);
            Quaternion targetRotation = activeHandAnchor.rotation * flippedRotation;
            if (jointSeats.Count > 0
                && (reseatPending
                    || (targetPosition - rb.position).sqrMagnitude > ReseatJumpMeters * ReseatJumpMeters
                    || SeatsDrifted(rb.position, rb.rotation)))
            {
                ReseatJointedBodies(targetPosition, targetRotation);
            }
            else
            {
                rb.position = targetPosition;
                rb.rotation = targetRotation;
            }
        }
        else
        {
            transform.position = activeHandAnchor.TransformPoint(flippedPosition);
            transform.rotation = activeHandAnchor.rotation * flippedRotation;
        }

        if (isActiveAndTracking && !isActive) {
            if (enableDelay > 0) {
                Invoke("EnableChildren", enableDelay);
            } else {
                EnableChildren();
            }
            isActive = true;
        }
    }
    void LateUpdate()
    {
        UpdateStep();
    }
    void Update()
    {
        UpdateStep();
    }
    void FixedUpdate()
    {
        UpdateStep();
    }

    public bool isActiveAndTracking {
        get {
            return activeHand != null && activeHand.IsTracked;
        }
    }

    public bool isUsingLeftHand {
        get {
            return activeHand == leftHand;
        }
    }

    public bool isUsingRightHand {
        get {
            return activeHand == rightHand;
        }
    }

    /// <summary>
    /// True while the LEFT anchor drives this tracker — the case flipVisual applies to,
    /// and the side a hand-held prop must be mounted for (HandheldMath / dp.hold). Before
    /// any anchor is bound it falls back to handPreference (Both → right, the Editor default
    /// when nothing is tracked unless the Simulator prefers left).
    /// </summary>
    public bool DrivenByLeftAnchor {
        get {
            if (activeHandAnchor != null && (leftAnchor != null || rightAnchor != null))
                return activeHandAnchor == leftAnchor;
            return handPreference == HandPreference.Left;
        }
    }

    public void DisableChildren () {
        foreach (Transform child in transform)
        {
            child.gameObject.SetActive(false);
        }
    }

    public void EnableChildren () {
        if (!isActive) {
            return;
        }
        foreach (Transform child in transform)
        {
            child.gameObject.SetActive(true);
        }
        // Re-activated children rebuild their joints (see JointSeat).
        reseatPending = true;
    }

#if UNITY_EDITOR
    private static Mesh ResolveGizmoMesh(ref Mesh cachedMesh, string resourceName, string dreamParkPath, string sharedPath)
    {
        if (cachedMesh != null) {
            return cachedMesh;
        }

        cachedMesh = Resources.Load<Mesh>(resourceName);

        if (cachedMesh == null) {
            cachedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(dreamParkPath);
        }

        if (cachedMesh == null) {
            cachedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(sharedPath);
        }

        return cachedMesh;
    }

    private static Mesh mirroredHandGizmoMesh;

    /// <summary>The gizmo hand mirrored in mesh space (x → −x, winding reversed) for the left
    /// hand. The S·M·S matrix above then draws it as a true left hand with no negative scale.</summary>
    private static Mesh MirroredHandMesh(Mesh source)
    {
        if (mirroredHandGizmoMesh != null) return mirroredHandGizmoMesh;
        var verts = source.vertices;
        var normals = source.normals;
        for (int i = 0; i < verts.Length; i++) verts[i].x = -verts[i].x;
        for (int i = 0; i < normals.Length; i++) normals[i].x = -normals[i].x;
        var tris = source.triangles;
        for (int i = 0; i + 2 < tris.Length; i += 3) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
        mirroredHandGizmoMesh = new Mesh { name = "Hand (left gizmo)", hideFlags = HideFlags.HideAndDontSave };
        mirroredHandGizmoMesh.vertices = verts;
        if (normals.Length == verts.Length) mirroredHandGizmoMesh.normals = normals;
        mirroredHandGizmoMesh.triangles = tris;
        mirroredHandGizmoMesh.RecalculateBounds();
        return mirroredHandGizmoMesh;
    }

    private void OnDrawGizmos()
    {
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Color oldColor = Gizmos.color;

        // Use the object's world position and rotation but a UNIT scale, so the gizmos are
        // sized only by the *LocalScale constants below and stay at a fixed real-world scale
        // regardless of this object's (or its parents') scale. The local offset is also
        // applied without scale, so its distance stays constant in world space too.
        Matrix4x4 worldNoScale = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        Mesh handMesh = ResolveGizmoMesh(
            ref handGizmoMesh,
            "Meshes/Hand",
            "Assets/DreamPark/Resources/Meshes/Hand.mesh",
            "Assets/Resources/Meshes/Hand.mesh");
        if (handMesh != null) {
            // The gizmo hand is drawn in the DEVICE wrist frame (OpenXR wrist joint, see
            // HANDHELD.md): +Z fingers, palm −Y, thumb −X for the right hand. The mesh is a
            // right hand authored in exactly that frame; the left hand is its mirror image
            // (thumb +X), and while flipVisual applies the tracker frame is raw * flip, so the
            // hand is drawn in the raw frame underneath it. It used to draw a right hand for
            // both sides, which taught the wrong frame for every left-hand mount.
            bool left = global::DreamPark.HandheldMath.TrackerIsLeft(this);
            Matrix4x4 raw = worldNoScale;
            if (flipVisual && left) {
                raw = worldNoScale * Matrix4x4.TRS(flipVisualPosition, Quaternion.Euler(flipVisualRotation), Vector3.one).inverse;
            }
            Mesh drawn = left ? MirroredHandMesh(handMesh) : handMesh;
            Matrix4x4 local = Matrix4x4.TRS(HandLocalOffset, Quaternion.Euler(HandLocalEulerOffset), HandLocalScale);
            if (left) {
                // Mirror the offset with the mesh: M_left = S · M_right · S, S = scale(-1,1,1).
                Matrix4x4 mirror = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
                local = mirror * local * mirror;
            }
            Gizmos.matrix = raw * local;
            Gizmos.color = HandGizmoColor;
            Gizmos.DrawMesh(drawn, Vector3.zero);
        }

        Mesh dreamBandMesh = ResolveGizmoMesh(
            ref dreamBandGizmoMesh,
            "Meshes/DreamBand",
            "Assets/DreamPark/Resources/Meshes/DreamBand.mesh",
            "Assets/Resources/Meshes/DreamBand.mesh");
        if (dreamBandMesh != null) {
            Gizmos.matrix = worldNoScale *
                            Matrix4x4.TRS(DreamBandLocalOffset, Quaternion.Euler(DreamBandLocalEulerOffset), DreamBandLocalScale);
            Gizmos.color = DreamBandGizmoColor;
            Gizmos.DrawMesh(dreamBandMesh, Vector3.zero);
            Gizmos.DrawWireMesh(dreamBandMesh, Vector3.zero);
        }

        Gizmos.color = oldColor;
        Gizmos.matrix = oldMatrix;
    }
#endif
}
