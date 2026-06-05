#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
#endif
using UnityEngine;
using UnityEngine.Events;
public class EasyEvent : MonoBehaviour
{
    [HideInInspector] public UnityEvent<object> onEvent = new UnityEvent<object>();
    [HideInInspector] public EasyEvent aboveEvent;
    [HideInInspector] public EasyEvent belowEvent;
    [ReadOnly] public bool eventOnStart = false;
    [HideInInspector] public bool isEnabled = false;
    [HideInInspector] public float lastDisabledAtRealtime = -999f;
    public virtual bool IgnoreAboveEventLink => false;

    public virtual void Awake() {
    }

    public virtual void Start()
    {
        if (Application.isPlaying && eventOnStart) {
            OnEvent();
        }
    }

    public virtual void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        onEvent?.Invoke(arg0);
    }

    public virtual void OnEventDisable() {
        if (isEnabled)
            lastDisabledAtRealtime = Time.realtimeSinceStartup;
        isEnabled = false;
    }

    public bool WasDisabledRecently(float seconds)
    {
        if (isEnabled || lastDisabledAtRealtime < 0f)
            return false;

        return Time.realtimeSinceStartup - lastDisabledAtRealtime <= seconds;
    }

    public virtual void OnValidate()
    {
        #if UNITY_EDITOR
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
            
        // Skip validation when this object is part of a prefab asset (during Apply/Save/etc)
        if (PrefabUtility.IsPartOfPrefabAsset(this))
            return;

        // Only rebuild if something actually changed
        if (NeedsRebuild())
            BuildSelfLink();
        #endif
    }

#if UNITY_EDITOR
    private bool NeedsRebuild()
    {
        EasyEvent[] allComponents = GetComponents<EasyEvent>();
        int myIndex = System.Array.IndexOf(allComponents, this);
        
        if (myIndex < 0) return false;
        
        // Check if my position in chain matches my current links
        EasyEvent expectedAbove = GetExpectedAbove(allComponents, myIndex);
        EasyEvent expectedBelow = GetExpectedBelow(allComponents, myIndex);
        bool expectedEventOnStart = (myIndex == 0);
        
        if (aboveEvent != expectedAbove) return true;
        if (belowEvent != expectedBelow) return true;
        if (eventOnStart != expectedEventOnStart) return true;
        
        // Check if persistent listener is wired correctly
        if (aboveEvent != null && aboveEvent.onEvent.GetPersistentEventCount() == 0)
            return true;
        
        return false;
    }

    private static EasyEvent GetExpectedAbove(EasyEvent[] allComponents, int index)
    {
        if (index <= 0)
            return null;

        EasyEvent current = allComponents[index];
        if (current != null && current.IgnoreAboveEventLink)
            return null;

        return allComponents[index - 1];
    }

    private static EasyEvent GetExpectedBelow(EasyEvent[] allComponents, int index)
    {
        if (index >= allComponents.Length - 1)
            return null;

        EasyEvent next = allComponents[index + 1];
        if (next != null && next.IgnoreAboveEventLink)
            return null;

        return next;
    }

    private void BuildSelfLink(bool relink = true)
    {
        try {
            Debug.Log("🚧 [EasyEvent] Rebuilding " + gameObject.name + " events");
            EasyEvent[] allComponents = GetComponents<EasyEvent>();
            
            // Rebuild the ENTIRE chain, not just this component
            for (int i = 0; i < allComponents.Length; i++)
            {
                EasyEvent current = allComponents[i];
                
                // Set above/below references
                current.aboveEvent = GetExpectedAbove(allComponents, i);
                current.belowEvent = GetExpectedBelow(allComponents, i);
                
                // First component starts the chain
                current.eventOnStart = (i == 0);
                
                // Wire up persistent listener from above component to this one
                if (current.aboveEvent != null && relink)
                {
                    // Remove all existing persistent listeners from above
                    for (int j = current.aboveEvent.onEvent.GetPersistentEventCount() - 1; j >= 0; j--)
                        UnityEventTools.RemovePersistentListener(current.aboveEvent.onEvent, j);

                    // Add persistent listener with explicit target type to handle namespaced classes
                    var targetType = current.GetType();
                    var method = targetType.GetMethod("OnEvent", new[] { typeof(object) });

                    UnityAction<object> action = (UnityAction<object>)Delegate.CreateDelegate(typeof(UnityAction<object>), current, method);
                    UnityEventTools.AddPersistentListener(current.aboveEvent.onEvent, action);
                }
                
                // Clear listeners on the LAST component (nothing should follow it)
                if (current.belowEvent == null && relink)
                {
                    for (int j = current.onEvent.GetPersistentEventCount() - 1; j >= 0; j--)
                        UnityEventTools.RemovePersistentListener(current.onEvent, j);
                }
                
                EditorUtility.SetDirty(current);
            }
            
            Debug.Log("✅ [EasyEvent] Rebuilt " + gameObject.name + " chain (" + allComponents.Length + " components)");
        } catch (Exception e) {
            Debug.LogError("❌ [EasyEvent] Error rebuilding " + gameObject.name + " events: " + e.Message);
        }
    }

    public void RemoveSelfLink() {
        // Remove listener from above component - chain ENDS here, don't wire to below
        if (aboveEvent != null)
        {
            for (int j = aboveEvent.onEvent.GetPersistentEventCount() - 1; j >= 0; j--)
                UnityEventTools.RemovePersistentListener(aboveEvent.onEvent, j);

            aboveEvent.belowEvent = null;
            EditorUtility.SetDirty(aboveEvent);
        }

        // Clear below's reference to this component - it becomes a new chain start
        if (belowEvent != null)
        {
            belowEvent.aboveEvent = null;
            belowEvent.eventOnStart = false; // Don't auto-start, this component controls it
            EditorUtility.SetDirty(belowEvent);
        }

        belowEvent = null;
        aboveEvent = null;
        EditorUtility.SetDirty(this);
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(EasyEvent), true)]
public class EasyEventEditor : Editor
{
    private static GUIStyle statusDotStyle;

    public override bool RequiresConstantRepaint()
    {
        EasyEvent easyEvent = target as EasyEvent;
        return Application.isPlaying
            && easyEvent != null
            && (!easyEvent.isEnabled && easyEvent.WasDisabledRecently(1f));
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        DrawEasyEventInspectorFooter((EasyEvent)target);
    }

    protected void DrawEasyEventInspectorFooter(EasyEvent easyEvent)
    {
        if (easyEvent == null)
            return;

        EditorGUILayout.Space(6);
        DrawEnabledStateIndicator(easyEvent);
        EditorGUILayout.Space(4);
        DrawChainInfo(easyEvent);
    }

    private static GUIStyle StatusDotStyle
    {
        get
        {
            if (statusDotStyle == null)
            {
                statusDotStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 14
                };
            }

            return statusDotStyle;
        }
    }

    private static void DrawEnabledStateIndicator(EasyEvent easyEvent)
    {
        bool isEnabled = Application.isPlaying ? easyEvent.isEnabled : easyEvent.eventOnStart;
        bool recentlyDisabled = Application.isPlaying && easyEvent.WasDisabledRecently(1f);

        Color dotColor = isEnabled
            ? new Color(0.22f, 0.75f, 0.30f)
            : (recentlyDisabled ? new Color(0.95f, 0.78f, 0.18f) : new Color(0.82f, 0.24f, 0.24f));

        string label = isEnabled ? "Enabled" : (recentlyDisabled ? "Recently Disabled" : "Disabled");

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("State", GUILayout.Width(40f));
            Color prevColor = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label("●", StatusDotStyle, GUILayout.Width(16f));
            GUI.color = prevColor;
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        }
    }

    private static void DrawChainInfo(EasyEvent easyEvent)
    {
        string aboveName = easyEvent.aboveEvent != null ? easyEvent.aboveEvent.GetType().Name : "None";
        string belowName = easyEvent.belowEvent != null ? easyEvent.belowEvent.GetType().Name : "None";
        int listenerCount = easyEvent.onEvent != null ? easyEvent.onEvent.GetPersistentEventCount() : 0;

        string info = $"↑ Above: {aboveName}  |  ↓ Below: {belowName}  |  Listeners: {listenerCount}";
        bool hasIssue = easyEvent.belowEvent != null && listenerCount == 0;

        EditorGUILayout.HelpBox(info, hasIssue ? MessageType.Warning : MessageType.Info);
    }
}
#endif
