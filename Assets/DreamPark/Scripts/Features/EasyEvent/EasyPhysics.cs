using UnityEngine;

public class EasyPhysics : EasyEvent
{
    public bool isKinematic = false;
    public bool useGravity = true;
    public bool enableRigidbody = true;
    public bool enableColliders = true;
    public RigidbodyConstraints constraints = RigidbodyConstraints.None;
    public bool breakJoint = false;

    public override void OnEvent(object arg0 = null)
    {
        var colliders = GetComponentsInChildren<Collider>();
        foreach (var collider in colliders) {
            collider.enabled = enableColliders;
        }   
        if (TryGetComponent<Rigidbody>(out var rb)) {
            rb.isKinematic = isKinematic;
            rb.useGravity = useGravity;
            rb.constraints = constraints;
            BreakJoint();
        } else {
            if (enableRigidbody) {
                rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = isKinematic;
                rb.useGravity = useGravity;
                rb.constraints = constraints;
                rb.mass = 1f;
                rb.linearDamping = 0.9f;
                rb.angularDamping = 0.9f;
            }
        }
        onEvent?.Invoke(null);
    }
    private void BreakJoint() {
        if (TryGetComponent<Joint>(out var joint)) {
            Destroy(joint);
        }
    }
}
