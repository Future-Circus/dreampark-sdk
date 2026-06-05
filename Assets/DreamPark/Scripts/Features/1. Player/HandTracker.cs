using UnityEngine;

public class HandTracker : MonoBehaviour
{
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
    // We cache the hand/anchor references and normally do NOT re-scan the scene for them
    // (FindObjectsByType / GameObject.Find walk the whole scene — cost that scales with
    // scene object count, and this used to run every tick × Update/LateUpdate/FixedUpdate).
    // But recovery must still work: if the OVRHand objects are destroyed/recreated the
    // refs go null, so we re-scan whenever something is missing — throttled so a prolonged
    // hands-absent stretch (e.g. controller mode) can't reintroduce a per-tick scene scan.
    [SerializeField] private float handRescanInterval = 0.1f;
    private float _nextHandScanTime = 0f;

    // True only while every reference is present AND alive. Unity's overloaded == reports a
    // destroyed UnityEngine.Object as null, so this flips false the instant a hand/anchor is
    // destroyed — which is what drives re-discovery. (Tracking loss does NOT null the object,
    // so it stays true and ChooseHand/isActiveAndTracking handle that case live.)
    private bool HandsResolved =>
        leftAnchor != null && rightAnchor != null && leftHand != null && rightHand != null;

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

        // Only scan the scene while we still need the hands. This used to run on every
        // tick even after the hands were cached — a full-scene walk per Update +
        // LateUpdate + FixedUpdate, per HandTracker instance.
        if (leftHand == null || rightHand == null)
        {
            // Bind by actual handedness (FindObjectsByType order is NOT guaranteed to be
            // L/R), and only fill the slot that's currently empty. This way we recover
            // whichever hand is available — even if just one of them came back — instead
            // of requiring both to be present at once.
            OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.InstanceID);
            foreach (OVRHand hand in hands)
            {
                if (hand == null) continue;
                OVRPlugin.Hand which = hand.GetHand();
                if (which == OVRPlugin.Hand.HandLeft && leftHand == null)
                    leftHand = hand;
                else if (which == OVRPlugin.Hand.HandRight && rightHand == null)
                    rightHand = hand;
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
        // Re-scan only while something is missing (first resolve, or after the OVRHand
        // objects are destroyed), throttled. Once resolved this is a couple of cheap null
        // checks per tick instead of a full-scene FindObjectsByType.
        if (!HandsResolved && Time.unscaledTime >= _nextHandScanTime)
        {
            _nextHandScanTime = Time.unscaledTime + handRescanInterval;
            FindHandAnchors();
        }
        activeHand = leftHand;
        activeHandAnchor = leftAnchor;
#else
        // Recover anchors AND destroyed hand objects (throttled). FindHandAnchors only
        // touches the scene while something is actually null — GameObject.Find is guarded
        // by anchor==null and FindObjectsByType by hand==null — so once everything is
        // resolved this costs nothing. Tracking loss alone (IsTracked false, object alive)
        // keeps HandsResolved true and is handled live by ChooseHand below, no scan.
        if (!HandsResolved && Time.unscaledTime >= _nextHandScanTime)
        {
            _nextHandScanTime = Time.unscaledTime + handRescanInterval;
            FindHandAnchors();
        }
        if (!activeHandAnchor)
            return;
        ChooseHand();
#endif

        if (!isActiveAndTracking && isActive) {
            DisableChildren();
            isActive = false;
        }

        // No live anchor this tick (still resolving, or the hands/anchors were destroyed).
        // We've already hidden the hand above; bail before dereferencing the null anchor.
        if (!activeHandAnchor)
            return;

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
}