using UnityEngine;

public class EasyDisable : EasyEvent
{
    public enum DisableType {
        GAMEOBJECT,
        COMPONENT,
        COMPONENTS,
        CHILDREN,
        CHILDREN_COMPONENTS,
        ANIMATOR
    }

    public DisableType disableType = DisableType.GAMEOBJECT;
    
    [ShowIf("disableType", DisableType.GAMEOBJECT)] public GameObject gameObjectToDisable;
    [ShowIf("disableType", DisableType.COMPONENT)] public MonoBehaviour componentToDisable;
    
    [ShowIf("disableType", DisableType.COMPONENTS)] public GameObject componentsTarget;
    [ShowIf("disableType", DisableType.COMPONENTS)] public bool disableRenderers = true;
    [ShowIf("disableType", DisableType.COMPONENTS)] public bool disableColliders = true;
    [ShowIf("disableType", DisableType.COMPONENTS)] public bool disableRigidbodies = true;
    [ShowIf("disableType", DisableType.COMPONENTS)] public bool disableBehaviours = true;
    
    [ShowIf("disableType", DisableType.CHILDREN)] public GameObject childrenParent;
    
    [ShowIf("disableType", DisableType.CHILDREN_COMPONENTS)] public GameObject childrenComponentsParent;
    [ShowIf("disableType", DisableType.CHILDREN_COMPONENTS)] public bool childDisableRenderers = true;
    [ShowIf("disableType", DisableType.CHILDREN_COMPONENTS)] public bool childDisableColliders = true;
    [ShowIf("disableType", DisableType.CHILDREN_COMPONENTS)] public bool childDisableRigidbodies = true;
    [ShowIf("disableType", DisableType.CHILDREN_COMPONENTS)] public bool childDisableBehaviours = true;

    public override void OnEvent(object arg0 = null) {
        Debug.Log("EasyDisable: " + disableType);
        switch (disableType) {
            case DisableType.GAMEOBJECT:
                if (gameObjectToDisable == null) {
                    gameObjectToDisable = gameObject;
                }
                gameObjectToDisable.SetActive(false);
                break;
                
            case DisableType.COMPONENT:
                if (componentToDisable != null)
                    componentToDisable.enabled = false;
                break;
                
            case DisableType.COMPONENTS:
                GameObject target = componentsTarget != null ? componentsTarget : gameObject;
                DisableComponents(target, disableRenderers, disableColliders, disableRigidbodies, disableBehaviours);
                break;
                
            case DisableType.CHILDREN:
                GameObject parent = childrenParent != null ? childrenParent : gameObject;
                Debug.Log("EasyDisable: " + parent.name);
                foreach (Transform child in parent.transform)
                {
                    Debug.Log("EasyDisable disabling: " + child.gameObject.name);
                    child.gameObject.SetActive(false);
                }
                break;
                
            case DisableType.CHILDREN_COMPONENTS:
                GameObject childParent = childrenComponentsParent != null ? childrenComponentsParent : gameObject;
                foreach (Transform child in childParent.transform)
                {
                    DisableComponents(child.gameObject, childDisableRenderers, childDisableColliders, childDisableRigidbodies, childDisableBehaviours);
                }
                break;
                
            case DisableType.ANIMATOR:
                Animator animator = gameObject.GetComponentInChildren<Animator>();
                if (animator != null)
                    animator.enabled = false;
                break;
                
            default:
                Debug.LogError("invalid DisableType");
                break;
        }

        onEvent?.Invoke(null);
    }

    private void DisableComponents(GameObject obj, bool renderers, bool colliders, bool rigidbodies, bool behaviours)
    {
        if (renderers)
        {
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = false;
            }
        }

        if (colliders)
        {
            foreach (var collider in obj.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }
        }

        if (rigidbodies)
        {
            foreach (var rb in obj.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
        }

        if (behaviours)
        {
            foreach (var behaviour in obj.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is EasyEvent) continue;
                behaviour.enabled = false;
            }
        }
    }
}