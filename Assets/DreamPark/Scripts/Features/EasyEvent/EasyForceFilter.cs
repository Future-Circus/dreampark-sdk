using UnityEngine;

public class EasyForceFilter : EasyEvent
{
    public enum ForceFilterMode {
        GREATER_THAN,
        LESS_THAN
    }
    public float minimumForce = 1f;
    public ForceFilterMode forceFilterMode = ForceFilterMode.GREATER_THAN;
    public override void OnEvent(object arg0 = null) {
        if (arg0 is CollisionWrapper collision) {
            if (forceFilterMode == ForceFilterMode.GREATER_THAN && collision.relativeVelocity.magnitude >= minimumForce || forceFilterMode == ForceFilterMode.LESS_THAN && collision.relativeVelocity.magnitude <= minimumForce) {
                onEvent?.Invoke(collision);
            } else {
                Debug.LogWarning("[EasyForceFilter] force is too low: " + collision.relativeVelocity.magnitude + " lower than " + minimumForce);
                aboveEvent?.OnEvent();
            }
        } else {
            Debug.LogWarning("[EasyForceFilter] you are not passing a collision! please use directly with InteractionFilter");
        }
    }
}
