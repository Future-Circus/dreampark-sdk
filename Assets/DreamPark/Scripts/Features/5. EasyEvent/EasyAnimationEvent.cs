using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
using System.Collections.Generic;
using System.Linq;
#endif

/// <summary>
/// Fires when its synced AnimationEvent hits on an AnimationClip.
/// </summary>
public class EasyAnimationEvent : EasyEvent
{
    [Serializable]
    public class EventDefinition
    {
        public AnimationClip clip;
        public float frame;
    }

    [SerializeField] public EventDefinition animationEvent;

    // Persistent identity used to uniquely track this component across domain reloads.
    // This is what prevents duplicate animation events from accumulating.
    [SerializeField, HideInInspector] private string persistentId;

    public string PersistentId
    {
        get
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(persistentId))
            {
                persistentId = UnityEditor.GUID.Generate().ToString();
                UnityEditor.EditorUtility.SetDirty(this);
            }
#endif
            // In builds, persistentId will be whatever was serialized in editor.
            return persistentId;
        }
    }

#if UNITY_EDITOR
    public override void OnValidate()
    {
        base.OnValidate();
        // Ensure it exists early (before inspector draws)
        _ = PersistentId;
    }
#endif

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
    }

    /// <summary>
    /// Stable entry point for AnimationEvents (direct call case).
    /// AnimationEvent stringParameter will be this component's PersistentId.
    /// </summary>
    public void OnAnimationEvent_Route(string id)
    {
        if (!isEnabled) return;
        if (string.IsNullOrEmpty(id)) return;

        // Only accept events meant for THIS component.
        if (id != PersistentId) return;

        OnEventDisable();
        onEvent?.Invoke(null);
    }
}


// -------------------------
// Editor
// -------------------------
#if UNITY_EDITOR

/// <summary>
/// Custom editor for EasyAnimationEvent.
/// Writes exactly ONE AnimationEvent per EasyAnimationEvent (per clip).
/// </summary>
[CustomEditor(typeof(EasyAnimationEvent), true)]
public class EasyAnimationEventEditor : EasyEventEditor
{
    public EasyAnimationEvent entity => (EasyAnimationEvent)target;

    private ReorderableList stateAnimationsList;

    // Optional: keep your existing custom list hookup if you had one.
    // If your EasyEvent has "stateAnimations", you can wire it here; otherwise it will simply not draw.
    private void OnEnable()
    {
        // Safe setup: only create list if the property exists.
        var prop = serializedObject.FindProperty("stateAnimations");
        if (prop != null)
        {
            stateAnimationsList = new ReorderableList(serializedObject, prop, true, true, true, true);
            stateAnimationsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "State Animations");
            stateAnimationsList.drawElementCallback = (rect, index, active, focused) =>
            {
                var el = prop.GetArrayElementAtIndex(index);
                EditorGUI.PropertyField(rect, el, GUIContent.none, true);
            };
            stateAnimationsList.elementHeightCallback = index =>
            {
                var el = prop.GetArrayElementAtIndex(index);
                return EditorGUI.GetPropertyHeight(el, true) + 4;
            };
        }

        // Ensure GUID exists
        _ = entity.PersistentId;
    }

    private void DrawAnimationEventUI()
    {
        SerializedProperty eventProp = serializedObject.FindProperty("animationEvent");
        if (eventProp == null)
        {
            EditorGUILayout.HelpBox("Missing 'animationEvent' property.", MessageType.Warning);
            return;
        }

        SerializedProperty clipProp = eventProp.FindPropertyRelative("clip");
        SerializedProperty frameProp = eventProp.FindPropertyRelative("frame");

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Animation Event (Auto Synced)", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(clipProp, new GUIContent("Clip"));

        AnimationClip clip = clipProp.objectReferenceValue as AnimationClip;

        if (clip != null)
        {
            float maxFrames = clip.length * clip.frameRate;
            frameProp.floatValue = Mathf.Clamp(frameProp.floatValue, 0f, maxFrames);
            EditorGUILayout.Slider(frameProp, 0f, maxFrames, $"Frame ({frameProp.floatValue:F1})");
        }
        else
        {
            EditorGUILayout.HelpBox("Assign an AnimationClip to enable syncing.", MessageType.Info);
        }
    }

    private void SyncAnimationEventToClip()
    {
        // Only sync in edit mode
        if (Application.isPlaying) return;

        Animator animator = entity.GetComponentInChildren<Animator>();
        if (animator == null) return;

        var e = entity.animationEvent;
        if (e == null || e.clip == null) return;

        AnimationClip clip = e.clip;

        // Convert frame -> time
        float time = 0f;
        if (clip.frameRate > 0f)
            time = Mathf.Clamp(e.frame / clip.frameRate, 0f, clip.length);

        GameObject animatorGO = animator.gameObject;
        GameObject methodTargetGO = entity.gameObject;

        // Stable identity
        string id = entity.PersistentId;

        // If the EasyAnimationEvent is not on the animator game object, we need a router.
        bool needsRouter = methodTargetGO != animatorGO;

        string functionName = needsRouter ? nameof(AnimationEventRouter.RouteEvent) : nameof(EasyAnimationEvent.OnAnimationEvent_Route);
        string stringParameter = id;

        AnimationEvent animEvent = new AnimationEvent
        {
            time = time,
            functionName = functionName,
            stringParameter = stringParameter
        };

        // Ensure router exists / is linked
        if (needsRouter)
        {
            var routers = animatorGO.GetComponents<AnimationEventRouter>();
            AnimationEventRouter router = null;

            // Find router already linked to THIS EasyAnimationEvent
            foreach (var r in routers)
            {
                if (r != null && r.linkedEvent == entity)
                {
                    router = r;
                    break;
                }
            }

            // If none found, create one
            if (router == null)
            {
                router = Undo.AddComponent<AnimationEventRouter>(animatorGO);
                router.linkedEvent = entity;
            }

            router.target = methodTargetGO;
            EditorUtility.SetDirty(router);

            // Remove any extra routers on this Animator GO that also point to this entity
            foreach (var r in routers)
            {
                if (r != null && r != router && r.linkedEvent == entity)
                {
                    Undo.DestroyObjectImmediate(r);
                }
            }
        }

        // Pull existing events, remove prior event(s) for this component by GUID, then add the new one.
        var currentEvents = AnimationUtility.GetAnimationEvents(clip).ToList();

        currentEvents.RemoveAll(ev =>
            (ev.functionName == nameof(EasyAnimationEvent.OnAnimationEvent_Route) && ev.stringParameter == id)
            || (ev.functionName == nameof(AnimationEventRouter.RouteEvent) && ev.stringParameter == id)
        );

        currentEvents.Add(animEvent);

        // Sort and set back
        var sorted = currentEvents.OrderBy(ev => ev.time).ToArray();
        AnimationUtility.SetAnimationEvents(clip, sorted);

        // Mark clip dirty so it saves
        EditorUtility.SetDirty(clip);
    }

    public override void OnInspectorGUI()
    {
        if (target == null || serializedObject == null) return;

        serializedObject.Update();

        // Draw everything except the fields we custom draw
        DrawPropertiesExcluding(serializedObject, "stateAnimations", "animationEvent", "persistentId");

        if (stateAnimationsList != null)
        {
            EditorGUILayout.Space(6);
            stateAnimationsList.DoLayoutList();
        }

        DrawAnimationEventUI();

        serializedObject.ApplyModifiedProperties();
        DrawEasyEventInspectorFooter(entity);

        // Sync after applying edits
        SyncAnimationEventToClip();
    }
}

#endif
