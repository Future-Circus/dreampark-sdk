using UnityEngine;

public class EasyBend : EasyEvent
{
    [Header("Detection")]
    public Transform detectionOrigin;
    public Vector3 detectionOffset = Vector3.zero;
    public float detectionRadius = 0.75f;
    public LayerMask detectionMask = ~0;
    public bool requireRigidbody = true;
    public bool ignoreTriggers = true;

    [Header("Bend")]
    public float maxTiltAngle = 22f;
    public float springStrength = 55f;
    public float springDamping = 7f;
    public float influenceMultiplier = 1f;
    public bool onlyWhileEnabled = true;

    [Header("Debug")]
    public bool showDetectionGizmo = false;
    public Color gizmoColor = new Color(0.2f, 1f, 0.5f, 0.35f);

    private readonly Collider[] overlapBuffer = new Collider[32];
    private Collider[] selfColliders;
    private Quaternion baseLocalRotation;
    private Vector2 currentTilt;
    private Vector2 tiltVelocity;

    public override void Awake()
    {
        base.Awake();
        baseLocalRotation = transform.localRotation;
        selfColliders = GetComponentsInChildren<Collider>(true);
    }

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        onEvent?.Invoke(arg0);
    }

    public override void OnEventDisable()
    {
        base.OnEventDisable();
    }

    private void Update()
    {
        if (onlyWhileEnabled && !isEnabled)
        {
            return;
        }

        Vector2 targetTilt = GetTargetTilt();
        float dt = Time.deltaTime;
        if (dt <= 0f)
        {
            return;
        }

        // Lightweight damped spring so it bends away, overshoots, and settles naturally.
        Vector2 accel = ((targetTilt - currentTilt) * springStrength) - (tiltVelocity * springDamping);
        tiltVelocity += accel * dt;
        currentTilt += tiltVelocity * dt;

        Quaternion bendRotation = Quaternion.Euler(currentTilt.x, 0f, currentTilt.y);
        transform.localRotation = baseLocalRotation * bendRotation;
    }

    private Vector2 GetTargetTilt()
    {
        Vector3 origin = GetDetectionOrigin();
        QueryTriggerInteraction query = ignoreTriggers ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide;
        int hitCount = Physics.OverlapSphereNonAlloc(origin, detectionRadius, overlapBuffer, detectionMask, query);

        float bestWeight = 0f;
        Vector2 bestTilt = Vector2.zero;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = overlapBuffer[i];
            if (hit == null || IsSelfCollider(hit))
            {
                continue;
            }

            if (requireRigidbody && hit.attachedRigidbody == null)
            {
                continue;
            }

            Vector3 nearest = hit.ClosestPoint(origin);
            Vector3 away = origin - nearest;
            float dist = away.magnitude;
            if (dist <= 0.0001f || dist > detectionRadius)
            {
                continue;
            }

            float weight = 1f - (dist / detectionRadius);
            if (weight <= bestWeight)
            {
                continue;
            }

            Vector3 localAway = transform.InverseTransformDirection(away.normalized);
            Vector2 tilt = new Vector2(localAway.z, -localAway.x) * (maxTiltAngle * weight * influenceMultiplier);
            bestWeight = weight;
            bestTilt = tilt;
        }

        return bestTilt;
    }

    private bool IsSelfCollider(Collider candidate)
    {
        if (candidate == null || selfColliders == null)
        {
            return false;
        }

        for (int i = 0; i < selfColliders.Length; i++)
        {
            if (candidate == selfColliders[i])
            {
                return true;
            }
        }

        return false;
    }

    private Vector3 GetDetectionOrigin()
    {
        Transform origin = detectionOrigin != null ? detectionOrigin : transform;
        return origin.TransformPoint(detectionOffset);
    }

    private void OnDrawGizmosSelected()
    {
        if (!showDetectionGizmo)
        {
            return;
        }

        Gizmos.color = gizmoColor;
        Gizmos.DrawSphere(GetDetectionOrigin(), detectionRadius);
    }
}
