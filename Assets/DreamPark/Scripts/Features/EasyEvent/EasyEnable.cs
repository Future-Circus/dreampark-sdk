using UnityEngine;

public class EasyEnable : EasyEvent
{
    public enum EnableType {
        GAMEOBJECT,
        COMPONENT,
        ANIMATOR,
        COMPONENTS,
        CHILDREN,
        CHILDREN_COMPONENTS
    }

    public EnableType enableType = EnableType.GAMEOBJECT;

    [Tooltip("If false, this will DISABLE instead of enable")]
    public bool enable = true;

    [Tooltip("If true, toggles the enable state each time OnEvent is called")]
    public bool toggleOnNext = false;

    [ShowIf("enableType", EnableType.GAMEOBJECT)] public GameObject gameObjectToEnable;
    [ShowIf("enableType", EnableType.COMPONENT)] public MonoBehaviour componentToEnable;
    [ShowIf("enableType", EnableType.ANIMATOR)] public Animator animatorToEnable;

    [ShowIf("enableType", EnableType.COMPONENTS)] public GameObject componentsTarget;
    [ShowIf("enableType", EnableType.COMPONENTS)] public bool enableRenderers = true;
    [ShowIf("enableType", EnableType.COMPONENTS)] public bool enableColliders = true;
    [ShowIf("enableType", EnableType.COMPONENTS)] public bool enableRigidbodies = true;
    [ShowIf("enableType", EnableType.COMPONENTS)] public bool enableBehaviours = true;

    [ShowIf("enableType", EnableType.CHILDREN)] public GameObject childrenParent;

    [ShowIf("enableType", EnableType.CHILDREN_COMPONENTS)] public GameObject childrenComponentsParent;
    [ShowIf("enableType", EnableType.CHILDREN_COMPONENTS)] public bool childEnableRenderers = true;
    [ShowIf("enableType", EnableType.CHILDREN_COMPONENTS)] public bool childEnableColliders = true;
    [ShowIf("enableType", EnableType.CHILDREN_COMPONENTS)] public bool childEnableRigidbodies = true;
    [ShowIf("enableType", EnableType.CHILDREN_COMPONENTS)] public bool childEnableBehaviours = true;

    public override void OnEvent(object arg0 = null) {
        switch (enableType) {
            case EnableType.GAMEOBJECT:
                if (gameObjectToEnable != null)
                    gameObjectToEnable.SetActive(enable);
                break;

            case EnableType.COMPONENT:
                if (componentToEnable != null)
                    componentToEnable.enabled = enable;
                break;

            case EnableType.ANIMATOR:
                if (animatorToEnable != null)
                    animatorToEnable.enabled = enable;
                break;

            case EnableType.COMPONENTS:
                GameObject target = componentsTarget != null ? componentsTarget : gameObject;
                SetComponents(target, enableRenderers, enableColliders, enableRigidbodies, enableBehaviours, enable);
                break;

            case EnableType.CHILDREN:
                GameObject parent = childrenParent != null ? childrenParent : gameObject;
                foreach (Transform child in parent.transform)
                {
                    child.gameObject.SetActive(enable);
                }
                break;

            case EnableType.CHILDREN_COMPONENTS:
                GameObject childParent = childrenComponentsParent != null ? childrenComponentsParent : gameObject;
                foreach (Transform child in childParent.transform)
                {
                    SetComponents(child.gameObject, childEnableRenderers, childEnableColliders, childEnableRigidbodies, childEnableBehaviours, enable);
                }
                break;

            default:
                Debug.LogError("invalid EnableType");
                break;
        }

        if (toggleOnNext)
        {
            enable = !enable;
        }

        onEvent?.Invoke(null);
    }

    private void SetComponents(GameObject obj, bool renderers, bool colliders, bool rigidbodies, bool behaviours, bool enableState)
    {
        if (renderers)
        {
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = enableState;
            }
        }

        if (colliders)
        {
            foreach (var collider in obj.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = enableState;
            }
        }

        if (rigidbodies)
        {
            foreach (var rb in obj.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = !enableState;
                rb.detectCollisions = enableState;
            }
        }

        if (behaviours)
        {
            foreach (var behaviour in obj.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is EasyEvent) continue;
                behaviour.enabled = enableState;
            }
        }
    }
}
