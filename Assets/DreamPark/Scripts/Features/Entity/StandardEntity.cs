using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System;
using UnityEngine.AI;
using UnityEngine.Events;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
public class StandardEntityEditor<TState> : EntityEditor where TState : Enum
{
    public bool showAnimationStates = false;
    public bool showAnimationEvents = false;
    public bool showAwareFeature = false;
    public bool showWaypoints = false;
    public StandardEntity<TState> entity {
        get {
            return (StandardEntity<TState>)target;
        }
    }
    private ReorderableList stateAnimationsList;
    private ReorderableList eventList;
    private SerializedProperty stateAnimationsProp;
    private ReorderableList waypointList;
    private static readonly Color[] indexColors = new Color[]
    {
        Color.red,
        Color.green,
        Color.blue,
        Color.yellow,
        Color.cyan,
        Color.magenta,
        new Color(1f, 0.5f, 0f), // orange
        new Color(0.5f, 0f, 1f), // purple
    };
    public void OnEnable_AnimationEvents() {
        stateAnimationsProp = serializedObject.FindProperty("stateAnimations");

        stateAnimationsList = new ReorderableList(serializedObject, stateAnimationsProp, true, true, true, true);

        stateAnimationsList.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "State Animations");
        };

        stateAnimationsList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            SerializedProperty element = stateAnimationsProp.GetArrayElementAtIndex(index);

            SerializedProperty stateProp = element.FindPropertyRelative("state");
            SerializedProperty animParamsProp = element.FindPropertyRelative("animationParameters");

            float lineHeight = EditorGUIUtility.singleLineHeight;
            float spacing = 2f;

            // Draw state field
            Rect stateRect = new Rect(rect.x, rect.y + spacing, rect.width, lineHeight);
            EditorGUI.PropertyField(stateRect, stateProp);

            // Draw animationParameters array with foldout and "+" support
            Rect paramRect = new Rect(rect.x + 10, stateRect.y + lineHeight + spacing, rect.width - 10, EditorGUI.GetPropertyHeight(animParamsProp, true));
            EditorGUI.PropertyField(paramRect, animParamsProp, new GUIContent("Animation Parameters"), true);
        };

        stateAnimationsList.elementHeightCallback = index =>
        {
            SerializedProperty element = stateAnimationsProp.GetArrayElementAtIndex(index);
            SerializedProperty animParamsProp = element.FindPropertyRelative("animationParameters");

            float lineHeight = EditorGUIUtility.singleLineHeight + 2f;
            float paramHeight = EditorGUI.GetPropertyHeight(animParamsProp, true);

            return lineHeight * 1 + paramHeight + 8f;
        };
    }
    public void OnInspectorGUI_AnimationEvents()
    {
        SerializedProperty listProp = serializedObject.FindProperty("animationEvents");

        List<string> GetValidMethods(GameObject go)
        {
            var methods = new List<string>();

            bool IsValidAnimationEventMethod(MethodInfo method)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0) return true;
                if (parameters.Length == 1)
                {
                    var type = parameters[0].ParameterType;
                    return type == typeof(string) || type == typeof(int) || type == typeof(float) || typeof(UnityEngine.Object).IsAssignableFrom(type);
                }
                return false;
            }

            foreach (var comp in go.GetComponents<MonoBehaviour>())
            {
                if (comp == null) continue;

                foreach (var method in comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (IsValidAnimationEventMethod(method))
                        methods.Add(method.Name);
                }
            }

            return methods.Distinct().OrderBy(n => n).ToList();
        }

        if (eventList == null)
        {
            eventList = new ReorderableList(serializedObject, listProp, true, true, true, true);

            eventList.drawHeaderCallback = rect =>
            {
                EditorGUI.LabelField(rect, "Animation Events (Auto Synced)");
            };

            eventList.elementHeightCallback = index =>
            {
                SerializedProperty element = listProp.GetArrayElementAtIndex(index);
                return EditorGUIUtility.singleLineHeight * 4 + 10f;
            };

            eventList.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                SerializedProperty element = listProp.GetArrayElementAtIndex(index);
                var clipProp = element.FindPropertyRelative("clip");
                var frameProp = element.FindPropertyRelative("frame");
                var methodProp = element.FindPropertyRelative("methodName");

                float line = EditorGUIUtility.singleLineHeight + 2f;
                float y = rect.y + 2;

                EditorGUI.PropertyField(new Rect(rect.x, y, rect.width, line), clipProp, new GUIContent("Clip"));
                y += line;

                AnimationClip clip = clipProp.objectReferenceValue as AnimationClip;
                if (clip != null)
                {
                    float maxFrames = clip.length * clip.frameRate;
                    frameProp.floatValue = Mathf.Clamp(frameProp.floatValue, 0, maxFrames);
                    EditorGUI.Slider(new Rect(rect.x, y, rect.width, line), frameProp, 0, maxFrames, $"Frame ({frameProp.floatValue:F1})");
                    y += line;

                    var gameObject = entity.gameObject;
                    var methods = GetValidMethods(gameObject);
                    int selected = Mathf.Max(0, methods.IndexOf(methodProp.stringValue));
                    selected = EditorGUI.Popup(new Rect(rect.x, y, rect.width, line), "Method", selected, methods.ToArray());
                    methodProp.stringValue = methods.Count > 0 ? methods[selected] : "";
                }
                else
                {
                    EditorGUI.LabelField(new Rect(rect.x, y, rect.width, line), "Assign an AnimationClip first.");
                }
            };
        }

        eventList.DoLayoutList();
    }
    public void OnInspectorGUI_SyncAnimationEvents()
    {
        Animator animator = entity.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            return;
        }

        GameObject animatorGO = animator.gameObject;

        if (entity.animationEvents == null) {
            return;
        }

        var grouped = entity.animationEvents
            .Where(e => e.clip && !string.IsNullOrEmpty(e.methodName))
            .GroupBy(e => e.clip);

        foreach (var group in grouped)
        {
            AnimationClip clip = group.Key;
            var desiredEvents = new List<AnimationEvent>();

            foreach (var e in group)
            {
                float time = e.frame / clip.frameRate;
                GameObject methodTarget = e.target ? e.target : entity.gameObject;
                bool needsRouter = methodTarget != animatorGO;

                AnimationEvent animEvent = new AnimationEvent
                {
                    time = time,
                    functionName = needsRouter ? "RouteEvent" : e.methodName,
                    stringParameter = needsRouter ? e.methodName : null
                };

                desiredEvents.Add(animEvent);

                if (needsRouter)
                {
                    var router = animatorGO.GetComponent<AnimationEventRouter>();
                    if (!router)
                    {
                        router = animatorGO.AddComponent<AnimationEventRouter>();
                    }
                    router.target = methodTarget;
                }
            }

            var currentEvents = AnimationUtility.GetAnimationEvents(clip).ToList();

            // Remove events not in our list
            currentEvents.RemoveAll(ev =>
                !desiredEvents.Any(d =>
                    Mathf.Approximately(d.time, ev.time) &&
                    d.functionName == ev.functionName &&
                    d.stringParameter == ev.stringParameter));

            // Add missing events
            foreach (var ev in desiredEvents)
            {
                bool alreadyExists = currentEvents.Any(existing =>
                    Mathf.Approximately(existing.time, ev.time) &&
                    existing.functionName == ev.functionName &&
                    existing.stringParameter == ev.stringParameter);

                if (!alreadyExists)
                    currentEvents.Add(ev);
            }

            AnimationUtility.SetAnimationEvents(clip, currentEvents.ToArray());
        }
    }
    public void OnSceneGUI_AwareCreature() {
        Handles.color = Color.red;

        entity.detectionRange = Handles.ScaleValueHandle(
            entity.detectionRange,
            entity.transform.position + Vector3.forward * entity.detectionRange,
            Quaternion.identity,
            1f,
            Handles.CubeHandleCap,
            0.1f
        );

        Handles.DrawWireDisc(entity.transform.position, Vector3.up, entity.detectionRange);

        if (entity.targetPlayer) {
            Handles.color = Color.green;

            entity.targetRange = Handles.ScaleValueHandle(
                entity.targetRange,
                entity.transform.position + Vector3.forward * entity.targetRange,
                Quaternion.identity,
                1f,
                Handles.CubeHandleCap,
                0.1f
            );

            Handles.DrawWireDisc(entity.transform.position, Vector3.up, entity.targetRange);
        }

        if (GUI.changed)
        {
            Undo.RecordObject(entity, "Change Range");
        }
    }
    public void OnEnable_Waypoints()
    {
        SerializedProperty waypointsProp = serializedObject.FindProperty("waypoints");

        waypointList = new ReorderableList(serializedObject, waypointsProp, true, true, true, true);

        waypointList.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Waypoints");
        };

        waypointList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            SerializedProperty element = waypointList.serializedProperty.GetArrayElementAtIndex(index);
            Color color = indexColors[index % indexColors.Length];

            Rect colorRect = new Rect(rect.x, rect.y + 2, 10, EditorGUIUtility.singleLineHeight);
            Rect fieldRect = new Rect(rect.x + 15, rect.y, rect.width - 15, EditorGUIUtility.singleLineHeight);

            EditorGUI.DrawRect(colorRect, color);
            EditorGUI.PropertyField(fieldRect, element, new GUIContent($"Waypoint {index}"), false);
        };
    }
    public void OnSceneGUI_Waypoints()
    {
        if (entity.waypoints == null || entity.waypoints.Length == 0) {
            return;
        }
        for (int i = 0; i < entity.waypoints.Length; i++)
        {
            Color color = indexColors[i % indexColors.Length];
            Handles.color = color;
            Vector3 worldPosition = entity.transform.TransformPoint(entity.waypoints[i]);
            Vector3 newWorldPosition = Handles.FreeMoveHandle(
                worldPosition,
                0.2f,
                Vector3.zero,
                Handles.SphereHandleCap
            );
            if (!Application.isPlaying && newWorldPosition != worldPosition)
            {
                Vector3 newLocalPosition = entity.transform.InverseTransformPoint(newWorldPosition);
                newLocalPosition.y = 0;
                entity.waypoints[i] = newLocalPosition;
                EditorUtility.SetDirty(entity);
                Undo.RecordObject(entity, "Move Handle " + i);
            }
        }
        Handles.color = Color.green;
        for (int i = 0; i < entity.waypoints.Length - 1; i++)
        {
            Handles.DrawLine(
                entity.transform.TransformPoint(entity.waypoints[i]),
                entity.transform.TransformPoint(entity.waypoints[i + 1])
            );
        }
        if (entity.waypoints.Length > 0)
        {
            Handles.DrawLine(
                entity.transform.TransformPoint(entity.waypoints[entity.waypoints.Length - 1]),
                entity.transform.TransformPoint(entity.waypoints[0])
            );
        }
    }
    public void OnInspectorGUI_AwareCreature() {
        EditorGUILayout.PropertyField(serializedObject.FindProperty("detectionRange"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("targetPlayer"));
        if (entity.targetPlayer) {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetRange"));
        }
    }
    public override void OnEnable()
    {
        if (target == null || serializedObject == null) {
            return;
        }
        base.OnEnable();
        if (showAnimationEvents) {
            OnEnable_AnimationEvents();
        }
        if (showWaypoints) {
            OnEnable_Waypoints();
        }
    }
    public override void OnInspectorGUI()
    {
        if (target == null || serializedObject == null) {
            return;
        }
        base.OnInspectorGUI();
        if (showAnimationEvents || showAnimationStates) {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("animator"));
        }
        if (showAnimationStates) {
            EditorGUILayout.Space(10);
            stateAnimationsList.DoLayoutList();
        }
        if (showAnimationEvents) {
            OnInspectorGUI_AnimationEvents();
        }
        if (showAwareFeature) {
            OnInspectorGUI_AwareCreature();
        }
        if (showWaypoints) {
            waypointList.DoLayoutList();
        }
        serializedObject.ApplyModifiedProperties();
        if (!Application.isPlaying) {
            OnInspectorGUI_SyncAnimationEvents();
        }
    }
    public override void OnSceneGUI() {
        if (target == null || serializedObject == null) {
            return;
        }
        base.OnSceneGUI();
        if (showAwareFeature) {
            OnSceneGUI_AwareCreature();
        }
        if (showWaypoints) {
            OnSceneGUI_Waypoints();
        }
    }

    public override string[] GetIgnorables() {
        return new string[] { "currentTargetPositionLocal", "animator", "mainRenderer", "stateAnimations", "animationEvents", "waypoints", "detectionRange", "targetPlayer", "targetRange", "interactionFilters", "agent", "_currentWaypointIndex", "currentTargetPosition" };
    }
}
#endif

public class StandardEntity<TState> : Entity<TState> where TState : Enum
{
    [SerializeField] public Animator animator;
    [SerializeField] public Renderer mainRenderer;
    [Serializable] public class AnimationParameter {
        public string name;
        public bool value;
    }
    [Serializable] public class StateAnimationMapping
    {
        public TState state;
        public AnimationParameter[] animationParameters;
    }
    [SerializeField] public StateAnimationMapping[] stateAnimations;

    [Serializable] public class EventDefinition
    {
        public AnimationClip clip;
        public float frame;
        public string methodName;
        public GameObject target;
    }
    [SerializeField] public EventDefinition[] animationEvents;
    [SerializeField] public Vector3[] waypoints;
    [SerializeField] public float detectionRange = 5f;
    [SerializeField] public bool targetPlayer = false;
    [SerializeField] public float targetRange = 5f;
    [SerializeField] public Dictionary<string, object> junkBucket = new Dictionary<string, object>();
    [SerializeField] public Vector3 currentTargetPosition;
    [SerializeField] public Vector3 currentTargetPositionLocal;
    [SerializeField] public NavMeshAgent agent;
    [SerializeField] public int _currentWaypointIndex = 0;

    public GameObject TrackJunk(string key, GameObject value) {
        junkBucket[key] = value;
        return junkBucket[key] as GameObject;
    }
    public object TrackJunk(string key, object value) {
        junkBucket[key] = value;
        return junkBucket[key];
    }
    public T TrackJunk<T>(string key, T value) where T : class {
        junkBucket[key] = value;
        return junkBucket[key] as T;
    }
    public T GetJunk<T>(string key)
    {
        if (junkBucket.ContainsKey(key) && junkBucket[key] != null)
        {
            object value = junkBucket[key];

            if (value is T tValue)
                return tValue;
        }

        return default;
    }

    public T GetJunkOnce<T>(string key)
    {
        if (junkBucket.ContainsKey(key)) {
            object value = junkBucket[key];
            if (value is T tValue) {
                junkBucket.Remove(key);
                return tValue;
            }
        }
        return default;
    }
    public GameObject GetJunk(string key) {
        return junkBucket[key] as GameObject;
    }
    public void UntrackJunk(string key) {
        if (junkBucket.ContainsKey(key)) {
            if (junkBucket[key] is GameObject) {
                if ((junkBucket[key] as GameObject).GetComponent<ParticleSystem>() != null) {
                    (junkBucket[key] as GameObject).GetComponent<ParticleSystem>().Stop();
                } else {
                    Destroy(junkBucket[key] as GameObject);
                }
            }
            junkBucket.Remove(key);
        }
    }
    public bool HasJunk(string key) {
        if (junkBucket.ContainsKey(key) && junkBucket[key] is GameObject) {
            return junkBucket.ContainsKey(key) && junkBucket[key] != null && !(junkBucket[key] as GameObject).Equals(null);
        }
        return junkBucket.ContainsKey(key) && junkBucket[key] != null;
    }
    public virtual void NextWaypoint () {
        if (waypoints.Length == 0) {
            return;
        }
        currentTargetPosition = ogPosition.transform.TransformPoint(waypoints[_currentWaypointIndex]);
        currentTargetPositionLocal = ogPosition.parent != null ? ogPosition.parent.transform.InverseTransformPoint(currentTargetPosition) : currentTargetPosition;

        SetWaypoint();

        _currentWaypointIndex = (_currentWaypointIndex + 1) % waypoints.Length;
    }
    public virtual void SetWaypoint () {
        if (rb) {
            rb.useGravity = false;
        }
        if (agent != null && agent.enabled) {
            agent.SetDestination(currentTargetPosition);
        }
    }
    public virtual void NearestWaypoint()
    {
        if (waypoints == null || waypoints.Length == 0)
        {
            currentTargetPosition = ogPosition.position;
            currentTargetPositionLocal = ogPosition.localPosition;
        } else {
            currentTargetPosition = ogPosition.transform.TransformPoint(waypoints[0]);
            currentTargetPositionLocal = ogPosition.parent != null ? ogPosition.parent.transform.InverseTransformPoint(currentTargetPosition) : currentTargetPosition;
            Vector3 startPosition = transform.position;
            float shortestDistance = Vector3.Distance(startPosition, currentTargetPosition);

            foreach (Vector3 waypoint in waypoints)
            {
                Vector3 waypointPosition = ogPosition.transform.TransformPoint(waypoint);
                float distance = Vector3.Distance(startPosition, waypointPosition);
                if (distance < shortestDistance)
                {
                    shortestDistance = distance;
                    currentTargetPosition = waypointPosition;
                    currentTargetPositionLocal = ogPosition.parent != null ? ogPosition.parent.transform.InverseTransformPoint(currentTargetPosition) : currentTargetPosition;
                }
            }
        }
        SetWaypoint();
    }

    [HideInInspector] public virtual bool isInPhysicsRange {
        get {
            return Camera.main == null || Vector3.Distance(transform.position, Camera.main.transform.position) < 20f;
        }
    }
    
    [HideInInspector] public virtual bool hasWaypoints {
        get {
            return waypoints != null && waypoints.Length > 0;
        }
    }

    [HideInInspector] public virtual bool readyToNavigate {
        get {
            return agent == null || agent.enabled && agent.isOnNavMesh || !agent.enabled && agent.IsOnNavMeshWhileDisabled();
        }
    }

    [HideInInspector] public virtual bool isAgent {
        get {
            
            return agent != null && agent.enabled;
        }
    }
    [HideInInspector] public bool playerInRange {
        get {
            var p1 = new Vector3(Camera.main.transform.position.x,0,Camera.main.transform.position.z);
            var p2 = new Vector3(transform.position.x,0,transform.position.z);
            return Camera.main != null && p1.Distance(p2) < detectionRange;
        }
    }
    [HideInInspector] public bool playerInTargetingRange {
        get {
            var p1 = new Vector3(Camera.main.transform.position.x,0,Camera.main.transform.position.z);
            var p2 = new Vector3(transform.position.x,0,transform.position.z);
            return Camera.main != null && targetPlayer && p1.Distance(p2) < targetRange;
        }
    }
    public virtual void SetupInteractionFilters() {

    }
    public virtual UnityEvent<CollisionWrapper> FindInteractionWithCall(string methodName) {
        if (interactionFilters == null) {
            return null;
        }
        foreach (var filter in interactionFilters) {
            if (filter.onInteractionEnter.GetPersistentEventCount() > 0 && filter.onInteractionEnter.GetPersistentMethodName(0) == methodName) {
                return filter.onInteractionEnter;
            }
        }
        return null;
    }
    public virtual UnityEvent<CollisionWrapper> FindInteractionWithTag(string tag) {
        if (interactionFilters == null) {
            return null;
        }
        foreach (var filter in interactionFilters) {
            if (filter.tags.Contains(tag)) {
                return filter.onInteractionEnter;
            }
        }
        return null;
    }
    public virtual UnityEvent<CollisionWrapper> FindInteractionWithLayer(string layer) {
        if (interactionFilters == null) {
            return null;
        }
        foreach (var filter in interactionFilters) {
            if (filter.layers.Contains(layer)) {
                return filter.onInteractionEnter;
            }
        }
        return null;
    }

    public virtual UnityEvent<CollisionWrapper> FindInteractionWithLayerAndTag(string layer, string tag) {
        if (interactionFilters == null) {
            return null;
        }
        foreach (var filter in interactionFilters) {
            if (filter.tags.Contains(tag) && filter.layers.Contains(layer)) {
                return filter.onInteractionEnter;
            }
        }
        return null;
    }
    public override void OnValidate()
    {
        #if UNITY_EDITOR
        if (!Application.isPlaying && (interactionFilters == null || interactionFilters.Length == 0)) {
            SetupInteractionFilters();
        }
        base.OnValidate();
        #endif
    }
    public override void Awake () {
        if (animator == null) {
            animator = GetComponentInChildren<Animator>();
        }
        if (mainRenderer == null) {
            mainRenderer = GetComponentInChildren<Renderer>();
        }
        base.Awake();
    }
    public override void PreExecuteState() {
        if (animator != null) {
            SetAnimatorParams();
        }
        base.PreExecuteState();
    }
    public void SetAnimatorParams() {

        if (debugger) {
            Debug.Log(gameObject.name + " SetAnimatorParams during state: " + state);
        }

        var mapping = stateAnimations.Where(m => m.state.Equals(state)).FirstOrDefault();

        if (mapping == null)
            return;

        foreach (AnimationParameter parameter in mapping.animationParameters) {
            if (animator.GetBool(parameter.name) != parameter.value) {
                animator.SetBool(parameter.name, parameter.value);
                #if UNITY_EDITOR
                if (debugger) {
                    Debug.Log($"{gameObject.name} {parameter.name}: {animator.GetBool(parameter.name)} from {parameter.value}");
                }
                #endif
            }
        }
    }
}
