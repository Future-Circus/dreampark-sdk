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
    private static readonly Color HandGizmoColor = new Color(0.96f, 0.91f, 0.75f, 0.95f);
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

    public OVRHand ActiveHand
    {
        get { return activeHand; }
    }

    void Awake()
    {
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

        OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.InstanceID);
        if (hands.Length >= 2)
        {
            leftHand = hands[0];
            rightHand = hands[1];
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
                if (leftHand != null && leftHand.IsTracked)
                {
                    activeHandAnchor = leftAnchor;
                    activeHand = leftHand;
                }
                else if (rightHand != null && rightHand.IsTracked)
                {
                    activeHandAnchor = rightAnchor;
                    activeHand = rightHand;
                }
                break;
        }
    }

    void UpdateStep()
    {
#if UNITY_EDITOR
        FindHandAnchors();
        activeHand = leftHand;
        activeHandAnchor = leftAnchor;
#else
        if (!activeHandAnchor) {
            FindHandAnchors();
            return;
        }
        ChooseHand();
#endif

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
            rb.position = activeHandAnchor.TransformPoint(flippedPosition);
            rb.rotation = activeHandAnchor.rotation * flippedRotation;
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
            Gizmos.matrix = worldNoScale *
                            Matrix4x4.TRS(HandLocalOffset, Quaternion.Euler(HandLocalEulerOffset), HandLocalScale);
            Gizmos.color = HandGizmoColor;
            Gizmos.DrawMesh(handMesh, Vector3.zero);
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
