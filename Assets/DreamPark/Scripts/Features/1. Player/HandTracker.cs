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
}