using System;
using System.Collections.Generic;
using DreamPark;
using UnityEngine;

/// <summary>
/// The Undo-tracked state of one layout-frame edit session (see
/// AttractionLayoutFrameEditSession). Keeping the session in an object that Undo
/// records means stepping back past "Begin" ends the session and stepping forward
/// resumes it, instead of leaving a frame state stranded in the Base hierarchy.
/// Editor-only, HideAndDontSave, never written to disk.
/// </summary>
internal sealed class AttractionLayoutFrameEditState : ScriptableObject
{
    /// <summary>The Base (authored) pose of one transform under the attraction root.</summary>
    [Serializable]
    internal sealed class Entry
    {
        public Transform transform;
        public Transform parent;
        // Fallback when a reference went stale (Undo re-creates deleted objects).
        public string path;
        public bool active;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
    }

    public bool active;
    public AttractionTemplate attraction;
    public string frameId;
    // True when frameId names a wall layer (AttractionWallLayer.id) instead of a frame.
    public bool wallLayer;
    // Prefab asset (or stage asset) this hierarchy saves into; empty for plain scene objects.
    public string assetPath;
    public int undoGroup;
    // A save happened mid-session (the frame's overrides were written without Undo).
    public bool savedDuringSession;
    // Committed by a script reload; begin again once the reload is done.
    public bool resumeAfterReload;
    // Every transform under the root, in hierarchy order, as it was at Begin (Base).
    public List<Entry> baseSnapshot = new List<Entry>();
    // The frame's overrides at Begin, for Cancel.
    public List<AttractionLayoutOverride> originalOverrides = new List<AttractionLayoutOverride>();
}
