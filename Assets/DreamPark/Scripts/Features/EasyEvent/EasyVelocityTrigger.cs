using UnityEngine;

public class EasyVelocityTrigger : EasyEvent
{
    public enum ForceFilterMode {
        GREATER_THAN,
        LESS_THAN
    }
    public float velocityThreshold = 1f;
    public ForceFilterMode velocityFilterMode = ForceFilterMode.GREATER_THAN;
    public bool debugger = false;
    private Rigidbody rb;
    private Vector3 lastPosition;
    private string debugText = "";

    public override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();
    } 
    public override void OnEvent(object arg0 = null)
    {
        lastPosition = transform.position;
        isEnabled = true;
    }

    public void Update()
    {
        if (!isEnabled) {
            debugText = "disabled";
            return;
        }
        if (rb != null) {
            if (velocityFilterMode == ForceFilterMode.GREATER_THAN && (rb.linearVelocity.magnitude >= velocityThreshold || rb.angularVelocity.magnitude >= velocityThreshold) || velocityFilterMode == ForceFilterMode.LESS_THAN && rb.linearVelocity.magnitude <= velocityThreshold && rb.angularVelocity.magnitude <= velocityThreshold) {
                isEnabled = false;
                onEvent?.Invoke(rb.linearVelocity.magnitude > rb.angularVelocity.magnitude ? rb.linearVelocity : rb.angularVelocity);
                return;
            } else {
                debugText = "RB velocity is too low: " + (rb.linearVelocity.magnitude > rb.angularVelocity.magnitude ? rb.linearVelocity : rb.angularVelocity).magnitude.ToString("F2");
            }
        }

        if (velocityFilterMode == ForceFilterMode.GREATER_THAN && (transform.position - lastPosition).magnitude >= velocityThreshold || velocityFilterMode == ForceFilterMode.LESS_THAN && (transform.position - lastPosition).magnitude <= velocityThreshold) {
            isEnabled = false;
            onEvent?.Invoke((transform.position - lastPosition).normalized);
            return;
        } else {
            debugText = "velocity is too low: " + (transform.position - lastPosition).magnitude.ToString("F2");
        }
    }

    #if UNITY_EDITOR
    public void OnDrawGizmos()
    {
        if (debugger) {
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.5f, debugText);
        }
    }
    #endif
}
