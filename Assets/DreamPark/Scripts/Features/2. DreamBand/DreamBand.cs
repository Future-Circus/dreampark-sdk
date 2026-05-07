using UnityEngine;
using Text = TMPro.TMP_Text;
using System;
using System.Collections.Generic;
using XLua;
public enum DreamBandState {
    START,
    STANDBY,
    STANDBYING,
    PLAY,
    PLAYING,
    PAUSE,
    PAUSING,
    END,
    ENDING,
    COLLECT,
    COLLECTING,
    INJURE,
    INJURING,
    RESTART,
    RESTARTING,
    ACHIEVEMENT,
    ACHIEVEMENTING,
    WIN,
    WINNING,
    DESTROY,
    DESTROYING
}
public class DreamBand : StandardEntity<DreamBandState>
{
    public static Dictionary<string, DreamBand> instances;
    [ReadOnly] public string gameId;
    public static DreamBand Instance;
    public Text timerText;
    [Tooltip("When assigned, the Lua script drives DreamBand behavior instead of the C# state machine.")]
    public LuaBehaviour luaBehaviour;

    /// <summary>True when a LuaBehaviour is linked and should drive behavior.</summary>
    public bool isLuaDriven => luaBehaviour != null;

    // Lua callbacks — resolved once from the script scope
    private Action luaOnShow;
    private Action luaOnHide;
    private Action<string> luaOnStateChange;
    private bool luaCallbacksResolved;

    private void ResolveLuaCallbacks() {
        if (luaCallbacksResolved || !isLuaDriven) return;
        var scope = luaBehaviour.ScriptScope;
        if (scope == null) return;
        scope.Get("onshow", out luaOnShow);
        scope.Get("onhide", out luaOnHide);
        scope.Get("onstatechange", out luaOnStateChange);
        luaCallbacksResolved = true;
    }

    public override void SetState(DreamBandState newState) {
        base.SetState(newState);
        if (isLuaDriven) {
            ResolveLuaCallbacks();
            luaOnStateChange?.Invoke(newState.ToString());
        }
    }

    public override void ExecuteState()
    {
        // Registration (START) always runs in C#. After that, Lua takes over if linked.
        if (state != DreamBandState.START && isLuaDriven) return;

        switch (state) {
            case DreamBandState.START:

                if (instances == null) {
                    instances = new Dictionary<string, DreamBand>();
                }
                if (instances.ContainsKey(gameId)) {
                    Destroy(gameObject);
                    return;
                }
                instances.Add(gameId, this);
                Show();
                SetState(DreamBandState.STANDBY);
                break;
            case DreamBandState.STANDBY:
                break;
            case DreamBandState.STANDBYING:
                if (Mathf.Floor(Time.time) % 2 == 0) {
                    timerText.enabled = false;
                } else {
                    timerText.enabled = true;
                }
                #if DREAMPARK_CORE
                if (SessionTime.Instance != null && SessionTime.Instance.sessionActive) {
                    SetState(DreamBandState.PLAY);
                }
                #endif
                break;
            case DreamBandState.PLAY:
                timerText.enabled = true;
                break;
            case DreamBandState.PLAYING:
                #if DREAMPARK_CORE
                if (SessionTime.Instance != null && SessionTime.Instance.GetSessionTime() > 0) {
                    timerText.text = SessionTime.Instance.GetSessionTimeInMinutes();
                } else {
                    timerText.text = "00:00";
                }
                if (SessionTime.Instance != null && !SessionTime.Instance.sessionActive) {
                    if (SessionTime.Instance.sessionEnded) {
                        SetState(DreamBandState.END);
                    } else {
                        SetState(DreamBandState.STANDBY);
                    }
                }
                #endif
                break;
            case DreamBandState.PAUSE:
                break;
            case DreamBandState.PAUSING:
                if (Mathf.Floor(Time.time) % 2 == 0) {
                    timerText.enabled = false;
                } else {
                    timerText.enabled = true;
                }
                break;
            case DreamBandState.COLLECTING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.INJURING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.ACHIEVEMENTING:
                SetState(DreamBandState.PLAY);
                break;
            case DreamBandState.DESTROY:
                Destroy(gameObject);
                break;
            case DreamBandState.END:
                break;
            case DreamBandState.ENDING:
                break;
        }
    }

    public void Show() {
        if (isEnded) {
            return;
        }
        if (Instance) {
            Instance.Hide();
        }
        Instance = this;
        SetState(DreamBandState.PLAY);
        if (isLuaDriven) {
            ResolveLuaCallbacks();
            luaOnShow?.Invoke();
        }
    }

    public void Hide() {
        if (isEnded) {
            return;
        }
        SetState(DreamBandState.STANDBY);
        if (isLuaDriven) {
            ResolveLuaCallbacks();
            luaOnHide?.Invoke();
        }
    }
    
    public bool isPlaying {
        get {
            return state == DreamBandState.PLAY || state == DreamBandState.PLAYING;
        }
    }
    public bool isPaused {
        get {
            return state == DreamBandState.PAUSE || state == DreamBandState.PAUSING;
        }
    }
    public bool isEnded {
        get {
            return state == DreamBandState.END || state == DreamBandState.ENDING;
        }
    }

    public void OnDestroy() {
        if (DreamBand.instances != null && DreamBand.instances.ContainsKey(gameId)) {
            DreamBand.instances.Remove(gameId);
        }
        if (DreamBand.Instance == this) {
            DreamBand.Instance = null;
        }
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(DreamBand))]
public class DreamBandEditor : StandardEntityEditor<DreamBandState>
{
    private UnityEditor.SerializedProperty luaBehaviourProp;

    public override void OnEnable()
    {
        if (target == null || serializedObject == null) return;
        base.OnEnable();
        luaBehaviourProp = serializedObject.FindProperty("luaBehaviour");
    }

    public override void OnInspectorGUI()
    {
        if (target == null || serializedObject == null) return;

        serializedObject.Update();

        // Always show the Lua behaviour field
        UnityEditor.EditorGUILayout.PropertyField(luaBehaviourProp);

        if (luaBehaviourProp.objectReferenceValue != null)
        {
            UnityEditor.EditorGUILayout.Space(4);
            UnityEditor.EditorGUILayout.HelpBox(
                "DreamBand is in Lua mode.\n\n" +
                "All behavior is driven by the linked LuaBehaviour script. " +
                "To customize DreamBand, edit the Lua script instead of these C# properties.",
                UnityEditor.MessageType.Info
            );
        }
        else
        {
            // No Lua linked — show the normal C# inspector
            UnityEditor.EditorGUILayout.Space(4);
            base.OnInspectorGUI();
        }

        serializedObject.ApplyModifiedProperties();
    }

    public override string[] GetIgnorables()
    {
        // Hide luaBehaviour from the base draw since we draw it ourselves
        var baseIgnorables = base.GetIgnorables();
        var combined = new string[baseIgnorables.Length + 1];
        baseIgnorables.CopyTo(combined, 0);
        combined[combined.Length - 1] = "luaBehaviour";
        return combined;
    }
}
#endif
