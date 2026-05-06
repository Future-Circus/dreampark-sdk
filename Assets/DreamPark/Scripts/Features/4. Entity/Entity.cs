using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
public class EntityEditor : InteractableEditor
{

}
#endif
public class Entity<TState> : Interactable where TState : Enum
{
    [HideInInspector] public TState state { get; protected set; }
    [HideInInspector] public List<TState> queue = new List<TState>();
    [HideInInspector] public float stateChangeTime = 0f;
    [HideInInspector] public Transform ogPosition;
    [HideInInspector] public bool use_late_update = false;
    private bool state_changed_frame = false;
    public virtual void OnValidate()
    {
         #if UNITY_EDITOR
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
            
        // Skip validation when this object is part of a prefab asset (during Apply/Save/etc)
        if (PrefabUtility.IsPartOfPrefabAsset(this))
            return;

        // If in prefab edit mode (Prefab Stage), only rebuild when user is actively editing the prefab contents
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.scene == gameObject.scene || PrefabUtility.IsPartOfPrefabInstance(this))
        {
            EditorUtility.SetDirty(this);
        }
        #endif
    }
    public virtual void Start()
    {
        //we only want to set the state if we're not in a queue
        if (queue.Count == 0) {
            SetState(state);
        }
    }

    private void UpdateStep() {
        state_changed_frame = false;
        if (queue.Count > 0) {
            if (state.ToString().EndsWith("ING")) {
                //we finish the state so stateWillChange is called
                PreExecuteState();
            }
            state = queue[0];
            queue.RemoveAt(0);
            stateChangeTime = Time.time;
            PreExecuteState();
        } else {
            if (!state.ToString().EndsWith("ING")) {
                var values = Enum.GetValues(state.GetType());
                int currentIndex = Array.IndexOf(values, state);
                if (currentIndex < values.Length - 1)
                {
                    state = (TState)values.GetValue(currentIndex + 1);
                    stateChangeTime = Time.time;
                }
            }
            PreExecuteState();
        }
    }

    public virtual void Update () {
        if (!use_late_update) {
            UpdateStep();
        }
    }
    public virtual void LateUpdate () {
        if (use_late_update) {
            UpdateStep();
        }
    }
    public virtual void SetState(TState newState) {
        if (queue.Count > 0 && queue.Last().Equals(newState)) return;
        state_changed_frame = true;
        queue.Add(newState);
    }

    public virtual void PreExecuteState () {
        ExecuteState();
    }
    public virtual void ExecuteState () {
        
    }
    public virtual bool StateCondition () {
        switch (state) {
            default:
                return true;
        }
    }
    public void NextState() {
        //shuffle from ACTION to ACTIONING
        var values = Enum.GetValues(state.GetType());
        int currentIndex = Array.IndexOf(values, state);
        if (currentIndex < values.Length - 1)
        {
            SetState((TState)values.GetValue(currentIndex + 1));
        }
    }
    public virtual void SaveOriginalPosition() {
        if (ogPosition == null) {
            ogPosition = new GameObject("ogPosition").transform;
            ogPosition.SetParent(transform.parent);
            ogPosition.position = transform.position;
            ogPosition.rotation = transform.rotation;
            ogPosition.localScale = transform.localScale;
        }
    }
    [HideInInspector] public float timeSinceStateChange {
        get {
            return Time.time-stateChangeTime;
        }
    }
    [HideInInspector] public bool stateWillChange {
        get {
            return !state_changed_frame && queue.Count > 0;
        }
    }
    [HideInInspector] public TState nextState {
        get {
            return queue.Count > 0 ? queue[0] : state;
        }
    }
    #if UNITY_EDITOR
    private void OnDrawGizmos() {
        if (debugger) {
            UnityEditor.Handles.Label(transform.position - Vector3.up * 0.5f, "State: " + state.ToString());
        }
    }
    #endif

    protected virtual void OnTransformParentChanged() {
        if (ogPosition != null) {
            ogPosition.SetParent(transform.parent);
        }
    }
}