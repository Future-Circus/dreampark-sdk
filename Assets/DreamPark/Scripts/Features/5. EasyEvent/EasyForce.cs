using System.Collections;
using UnityEngine;

public class EasyForce : EasyEvent
{
    public Vector3 force;
    public Vector3 forceVariation;
    public Vector3 torque;
    public Vector3 torqueVariation;
    public Transform forceOrigin;
    public ForceMode forceMode = ForceMode.Impulse;
    private Rigidbody rb;
    public override void Awake() {
        base.Awake();
        rb = GetComponent<Rigidbody>();
    }
    public override void OnEvent(object arg0 = null)
    {
        if (rb) {            
            if (forceMode == ForceMode.Acceleration) {
                isEnabled = true;
            } else {
                Apply();
            } 
        }
        onEvent?.Invoke(null);
    }

    private void Apply() {
        if (forceOrigin) {
            Vector3 forceDirection = (transform.position - forceOrigin.position).normalized;
            Vector3 randomForce = new Vector3(Random.Range(force.x, forceVariation.x), Random.Range(force.y, forceVariation.y), Random.Range(force.z, forceVariation.z));
            Vector3 finalForce = new Vector3(forceDirection.x * randomForce.x, forceDirection.y * randomForce.y, forceDirection.z * randomForce.z);
            finalForce *= rb.mass;
            Debug.Log("Final Force: " + finalForce);
            rb.AddForce(finalForce, forceMode);
            rb.AddTorque(torque + new Vector3(Random.Range(-torqueVariation.x, torqueVariation.x), Random.Range(-torqueVariation.y, torqueVariation.y), Random.Range(-torqueVariation.z, torqueVariation.z)), ForceMode.Impulse);
        } else {
            Vector3 randomForce = force + new Vector3(Random.Range(-forceVariation.x, forceVariation.x), Random.Range(-forceVariation.y, forceVariation.y), Random.Range(-forceVariation.z, forceVariation.z));
            rb.AddForce(randomForce*rb.mass, forceMode);
            rb.AddTorque(torque + new Vector3(Random.Range(-torqueVariation.x, torqueVariation.x), Random.Range(-torqueVariation.y, torqueVariation.y), Random.Range(-torqueVariation.z, torqueVariation.z)), ForceMode.Impulse);
        }
    }

    void Update() {
        if (isEnabled) {
            Apply();
        }
    }
}
