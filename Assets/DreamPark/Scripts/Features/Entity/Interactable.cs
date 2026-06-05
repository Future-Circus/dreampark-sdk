using UnityEngine;
using UnityEngine.Events;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
[CustomEditor(typeof(Interactable),true)]
public class InteractableEditor : Editor
{
    public bool showInteractionFilters = true;
    private ReorderableList filterList;
    public virtual void OnEnable()
    {
        string GetFilterLabel(SerializedProperty filterProp, int index)
        {
            var enterEvent = filterProp.FindPropertyRelative("onInteractionEnter");
            var exitEvent = filterProp.FindPropertyRelative("onInteractionExit");

            string methodName = GetFirstMethodLabel(enterEvent);
            if (string.IsNullOrEmpty(methodName))
                methodName = GetFirstMethodLabel(exitEvent);

            return string.IsNullOrEmpty(methodName) ? $"Element {index}" : methodName;
        }
        string GetFirstMethodLabel(SerializedProperty unityEventProp)
        {
            if (unityEventProp == null) return null;

            var calls = unityEventProp.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null || calls.arraySize == 0) return null;

            var call = calls.GetArrayElementAtIndex(0);
            var methodNameProp = call.FindPropertyRelative("m_MethodName");
            var targetProp = call.FindPropertyRelative("m_Target");

            if (targetProp != null && methodNameProp != null && targetProp.objectReferenceValue != null)
            {
                return $"{targetProp.objectReferenceValue.name}.{methodNameProp.stringValue}";
            }
            return null;
        }

        SerializedProperty interactionFiltersProp = serializedObject.FindProperty("interactionFilters");

        filterList = new ReorderableList(serializedObject, interactionFiltersProp, true, true, true, true);

        filterList.drawHeaderCallback = (Rect rect) =>
        {
            EditorGUI.LabelField(rect, "Interaction Filters");
        };

        filterList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
        {
            SerializedProperty element = interactionFiltersProp.GetArrayElementAtIndex(index);
            string label = GetFilterLabel(element, index);
            EditorGUI.PropertyField(rect, element, new GUIContent(label), true);
        };

        filterList.elementHeightCallback = (int index) =>
        {
            return EditorGUI.GetPropertyHeight(interactionFiltersProp.GetArrayElementAtIndex(index), true);
        };
    }
    public virtual void OnSceneGUI() {
        
    }
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        if (showInteractionFilters) {
            filterList.DoLayoutList();
        }
        DrawPropertiesExcluding(serializedObject, GetIgnorables());
        serializedObject.ApplyModifiedProperties();
    }
    public virtual string[] GetIgnorables() {
        return new string[] { "interactionFilters" };
    }
}
#endif


public class RigidbodySnapshot {
    public float mass;
    public bool useGravity;
    public bool isKinematic;
    public RigidbodyInterpolation interpolation;
    public CollisionDetectionMode collisionDetectionMode;
    public RigidbodyConstraints constraints;

    public RigidbodySnapshot(Rigidbody rb)
    {
        mass = rb.mass;
        useGravity = rb.useGravity;
        isKinematic = rb.isKinematic;
        interpolation = rb.interpolation;
        collisionDetectionMode = rb.collisionDetectionMode;
        constraints = rb.constraints;
    }

    public void Restore(Rigidbody rb)
    {
        // rb.mass = mass;
        // rb.useGravity = useGravity;
        // rb.isKinematic = isKinematic;
        // rb.interpolation = interpolation;
        // rb.collisionDetectionMode = collisionDetectionMode;
        // rb.constraints = constraints;
    }

    public void Freeze(Rigidbody rb) {
        // rb.useGravity = false;
        // rb.isKinematic = true;
        // rb.interpolation = RigidbodyInterpolation.None;
        // rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        // rb.constraints = RigidbodyConstraints.FreezeAll;
    }
}

public class CollisionWrapper
{
    public GameObject gameObject { get; private set; }
    public Collider collider { get; private set; }
    public Vector3 contactPoint { get; private set; }
    public Vector3 normal { get; private set; }
    public Vector3 relativeVelocity { get; private set; }
    public Vector3 collisionPoint { get; private set; }
    public string tag { get; private set; }
    public int layer { get; private set; }

    // Constructor for real Collision
    public CollisionWrapper(Collision collision)
    {
        gameObject = collision.gameObject;
        collider = collision.collider;
        contactPoint = collision.contacts.Length > 0 ? collision.contacts[0].point : Vector3.zero;
        normal = collision.contacts.Length > 0 ? collision.contacts[0].normal : Vector3.zero;
        relativeVelocity = collision.relativeVelocity;
        collisionPoint = collision.contacts.Length > 0 ? collision.contacts[0].point : gameObject.transform.position;
        tag = gameObject.tag;
        layer = gameObject.layer;
    }

    // Constructor for trigger Collider
    public CollisionWrapper(Collider collider, Vector3 fakeRelativeVelocity = default)
    {
        gameObject = collider.gameObject;
        this.collider = collider;
        contactPoint = collider.ClosestPoint(Vector3.zero); // Replace with actual point if available
        normal = Vector3.zero; // No real normal in triggers
        relativeVelocity = fakeRelativeVelocity;
        collisionPoint = gameObject.transform.position;
        tag = gameObject.tag;
        layer = gameObject.layer;
    }

    public CollisionWrapper(GameObject gameObject) {
        this.gameObject = gameObject;
        this.collider = null;
        contactPoint = gameObject.transform.position;
        collisionPoint = gameObject.transform.position;
        normal = Vector3.zero;
        relativeVelocity = Vector3.zero;
        tag = gameObject.tag;
        layer = gameObject.layer;
    }

    public CollisionWrapper () {
        gameObject = null;
        collider = null;
        contactPoint = Vector3.zero;
        normal = Vector3.zero;
        relativeVelocity = Vector3.zero;
        collisionPoint = Vector3.zero;
        tag = "Untagged";
        layer = LayerMask.NameToLayer("Default");
    }
}
public class Interactable : MonoBehaviour
{
    [System.Serializable] public class InteractionFilter
    {
        [SerializeField] public string[] layers;
        [SerializeField] public string[] tags;
        public UnityEvent<CollisionWrapper> onInteractionEnter;
        public UnityEvent<CollisionWrapper> onInteractionStay;
        public UnityEvent<CollisionWrapper> onInteractionExit;
    }
    [SerializeField] public InteractionFilter[] interactionFilters;
    [HideInInspector] private CollisionWrapper _lastCollision;
    [HideInInspector] public CollisionWrapper lastCollision {
        get {
            if (_lastCollision == null) {
                return new CollisionWrapper();
            }
            return _lastCollision;
        }
        set {
            _lastCollision = value;
            if (debugger) {
                Debug.Log(gameObject.name + " last collision: " + value.gameObject.name);
            }
        }
    }
    [HideInInspector] public Rigidbody rb { get; protected set; }
    [HideInInspector] protected Collider mainCollider;
    [HideInInspector] protected RigidbodySnapshot ogRigidbody;
    [HideInInspector] protected bool hasCollisionStayListeners;
    public virtual void Awake () {
        if (mainCollider == null) {
            mainCollider = GetComponent<Collider>();
        }
        if (rb == null) {
            rb = GetComponent<Rigidbody>();
        }

        interactionFilters.ToList().ForEach(filter =>
        {
            if (filter.onInteractionStay.GetPersistentEventCount() > 0)
            {
                hasCollisionStayListeners = true;
            }
        });
    }
    public virtual void SaveOriginalRigidbody(bool andFreeze = false) {
        ogRigidbody = new RigidbodySnapshot(rb);
        if (andFreeze) {
            ogRigidbody.Freeze(rb);
        }
    }
    public bool debugger = false;
    private void OnCollisionEnter(Collision collision) {
        GameObject other = collision.gameObject;
        CheckInteraction(other, true, new CollisionWrapper(collision));
    }
    private void OnCollisionExit(Collision collision) {
        GameObject other = collision.gameObject;
        CheckInteraction(other, false, new CollisionWrapper(collision)); 
    }
    private void OnTriggerEnter(Collider other) {
        CheckInteraction(other.gameObject, true, new CollisionWrapper(other));
    }
    private void OnTriggerExit(Collider other) {
        CheckInteraction(other.gameObject, false, new CollisionWrapper(other));
    }
    private void CheckInteraction(GameObject other, bool isEnter, CollisionWrapper collision = null) {
        foreach (InteractionFilter filter in interactionFilters) {
            bool layerMatch = false;
            bool tagMatch = false;

            // Check layers
            if (filter.layers != null && filter.layers.Length > 0) {
                foreach (string layerName in filter.layers) {
                    if (other.layer == LayerMask.NameToLayer(layerName)) {
                        layerMatch = true;
                        break;
                    }
                }
            } else {
                layerMatch = true; // No layer restrictions
            }

            // Check tags
            if (filter.tags != null && filter.tags.Length > 0) {
                foreach (string tag in filter.tags) {
                    if (other.CompareTag(tag)) {
                        tagMatch = true;
                        break;
                    }
                }
            } else {
                tagMatch = true; // No tag restrictions
            }

            // If both conditions are met, invoke appropriate event
            if (layerMatch && tagMatch) {
                if (debugger) {
                    Debug.Log(gameObject.name + " CheckInteraction " + other.name + " with layer " + other.layer + " and tag " + other.tag);
                }
                lastCollision = collision;
                if (isEnter) {
                    filter.onInteractionEnter?.Invoke(lastCollision);
                } else {
                    filter.onInteractionExit?.Invoke(lastCollision);
                }
                break;
            }
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        if (!hasCollisionStayListeners) return;
        GameObject other = collision.gameObject;
        CheckInteractionStay(other, new CollisionWrapper(collision));
    }

    private void OnTriggerStay(Collider other)
    {
        if (!hasCollisionStayListeners) return;
        CheckInteractionStay(other.gameObject, new CollisionWrapper(other));
    }

    private void CheckInteractionStay(GameObject other, CollisionWrapper collision)
    {
        foreach (InteractionFilter filter in interactionFilters)
        {
            bool layerMatch = false;
            bool tagMatch = false;

            // Check layers
            if (filter.layers != null && filter.layers.Length > 0)
            {
                foreach (string layerName in filter.layers)
                {
                    if (other.layer == LayerMask.NameToLayer(layerName))
                    {
                        layerMatch = true;
                        break;
                    }
                }
            }
            else
            {
                layerMatch = true;
            }

            // Check tags
            if (filter.tags != null && filter.tags.Length > 0)
            {
                foreach (string tag in filter.tags)
                {
                    if (other.CompareTag(tag))
                    {
                        tagMatch = true;
                        break;
                    }
                }
            }
            else
            {
                tagMatch = true;
            }

            if (layerMatch && tagMatch)
            {
                filter.onInteractionStay?.Invoke(collision);
                break;
            }
        }
    }
}
