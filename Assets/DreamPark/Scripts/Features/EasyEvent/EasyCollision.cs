using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public class EasyCollision : EasyEvent
{
    public string[] layers;
    public string[] tags;
    public enum CollisionEvent {
        ENTER,
        EXIT
    }
    public CollisionEvent collisionEvent;
    private bool detecting = false;
    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        detecting = true;
        //Debug.Log($"[EasyCollision] {gameObject.name} OnEvent - isEnabled:{isEnabled} detecting:{detecting}");
    }

    private void OnCollisionEnter(Collision collision) {
        //Debug.Log($"[EasyCollision] {gameObject.name} OnCollisionEnter - isEnabled:{isEnabled} detecting:{detecting} collisionEvent:{collisionEvent}");
        if (isEnabled && detecting && collisionEvent == CollisionEvent.ENTER) {
            GameObject other = collision.gameObject;
            CheckInteraction(other, new CollisionWrapper(collision));
        }
    }
    public void OnCollisionStay(Collision collision) {
        if (isEnabled && detecting && collisionEvent == CollisionEvent.ENTER) {
            GameObject other = collision.gameObject;
            CheckInteraction(other, new CollisionWrapper(collision));
        }
    }
    private void OnCollisionExit(Collision collision) {
        if (isEnabled && detecting && collisionEvent == CollisionEvent.EXIT) {
        GameObject other = collision.gameObject;
            CheckInteraction(other, new CollisionWrapper(collision)); 
        }
    }
    private void OnTriggerEnter(Collider other) {
        //Debug.Log($"[EasyCollision] {gameObject.name} OnTriggerEnter - isEnabled:{isEnabled} detecting:{detecting} collisionEvent:{collisionEvent}");
        if (isEnabled && detecting && collisionEvent == CollisionEvent.ENTER) {
            CheckInteraction(other.gameObject, new CollisionWrapper(other));
        }
    }
    public void OnTriggerStay(Collider other) {
        if (isEnabled && detecting && collisionEvent == CollisionEvent.ENTER) {
            CheckInteraction(other.gameObject, new CollisionWrapper(other));
        }
    }
    private void OnTriggerExit(Collider other) {
        if (isEnabled && detecting && collisionEvent == CollisionEvent.EXIT) {
            CheckInteraction(other.gameObject, new CollisionWrapper(other));
        }
    }

     private void CheckInteraction(GameObject other, CollisionWrapper collision = null) {
        //Debug.Log(gameObject.name + " [EasyCollision]");
        bool layerMatch = false;
        bool tagMatch = false;

        if (layers != null && layers.Length > 0) {
            foreach (string layerName in layers) {
                if (other.layer == LayerMask.NameToLayer(layerName)) {
                    layerMatch = true;
                    break;
                }
            }
        } else {
            layerMatch = true;
        }
        if (tags != null && tags.Length > 0) {
            foreach (string tag in tags) {
                if (other.CompareTag(tag)) {
                    tagMatch = true;
                    break;
                }
            }
        } else {
            tagMatch = true;
        }
        if (layerMatch && tagMatch) {
            OnEventDisable();
            onEvent?.Invoke(collision);
        }

        //Debug.Log(gameObject.name + " [EasyCollision] CheckInteraction - layerMatch: " + layerMatch + " tagMatch: " + tagMatch);
     }

    public override void OnEventDisable() {
        base.OnEventDisable();
        detecting = false;
    }
}
