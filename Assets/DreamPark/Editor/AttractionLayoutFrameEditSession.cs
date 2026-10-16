using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DreamPark;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Frame edit session. The creator edits a layout frame directly in the hierarchy:
/// Begin snapshots the Base (authored) state of every transform and applies the
/// frame; Commit diffs the hierarchy against Base, stores only what changed as the
/// frame's overrides, then puts Base back and re-bakes.
///
/// A frame state must never be saved as Base. Every save of the owning prefab or
/// scene (AttractionPackingPrefabSaveProcessor, PrefabStage.prefabSaving) writes the
/// overrides and restores Base first, then puts the working state back afterwards;
/// closing the stage, entering Play Mode and reloading scripts commit the session.
/// Only one session exists at a time.
/// </summary>
[InitializeOnLoad]
internal static class AttractionLayoutFrameEditSession
{
    internal const float PositionEpsilon = 1e-4f;
    internal const float ScaleEpsilon = 1e-4f;
    internal const float RotationEpsilonDegrees = 0.01f;
    private const float FeetToMeters = 0.3048f;

    internal sealed class Result
    {
        public bool success;
        public string message = "";
        public int changedCount;
        public int createdCount;
        public readonly List<string> deletedPaths = new List<string>();
        public readonly List<string> movedPaths = new List<string>();
    }

    private sealed class Diff
    {
        public readonly List<AttractionLayoutOverride> overrides = new List<AttractionLayoutOverride>();
        public readonly List<GameObject> createdRoots = new List<GameObject>();
        public readonly List<string> deletedPaths = new List<string>();
        public readonly List<string> movedPaths = new List<string>();
        public int changedCount;
        public bool Blocked => deletedPaths.Count > 0 || movedPaths.Count > 0;
    }

    private struct WorkingPose
    {
        public Transform transform;
        public bool active;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    private static readonly MethodInfo StageSaveMethod = typeof(PrefabStage).GetMethod(
        "Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
    private static readonly PropertyInfo StageUnsavedChangesProperty = typeof(PrefabStage).GetProperty(
        "hasUnsavedChanges", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static AttractionLayoutFrameEditState state;
    // Last observed state.active, to notice Undo/Redo crossing Begin.
    private static bool trackedActive;
    // Exact working (frame) state while the hierarchy is temporarily Base for a save.
    private static List<WorkingPose> suspendedWorkingState;
    private static GUIStyle sceneLabelStyle;

    static AttractionLayoutFrameEditSession()
    {
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        PrefabStage.prefabStageClosing += OnPrefabStageClosing;
        PrefabStage.prefabSaving += OnPrefabSaving;
        PrefabStage.prefabSaved += OnPrefabSaved;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.delayCall += ResumeAfterAssemblyReload;
    }

    // ------------------------------------------------------------------ queries

    internal static bool IsActive => state != null && state.active && state.attraction != null;
    internal static AttractionTemplate Attraction => IsActive ? state.attraction : null;
    internal static string FrameId => IsActive ? state.frameId : null;
    internal static bool IsSuspended => suspendedWorkingState != null;
    internal static AttractionLayoutFrame CurrentFrame =>
        IsActive && !state.wallLayer ? state.attraction.FindFrame(state.frameId) : null;
    /// <summary>The wall layer being edited, or null (a frame, or nothing, is being edited).</summary>
    internal static AttractionWallLayer CurrentWallLayer =>
        IsActive && state.wallLayer ? state.attraction.FindWallLayerById(state.frameId) : null;
    internal static bool IsEditingWallLayer => IsActive && state.wallLayer;
    /// <summary>What is being edited, for UI ("Minimum", "Big Room", "Front wall").</summary>
    internal static string CurrentLabel => IsActive ? SessionLabel(state) : "";

    internal static bool IsEditing(AttractionTemplate attraction) =>
        attraction != null && IsActive && state.attraction == attraction;

    internal static bool IsEditingWallLayerOf(AttractionTemplate attraction, string layerId) =>
        IsEditing(attraction) && state.wallLayer && state.frameId == layerId;

    /// <summary>The overrides the session edits: its frame's or its wall layer's.</summary>
    private static List<AttractionLayoutOverride> TargetOverrides(AttractionLayoutFrameEditState session)
    {
        if (session?.attraction == null) return null;
        if (session.wallLayer) return session.attraction.FindWallLayerById(session.frameId)?.overrides;
        return session.attraction.FindFrame(session.frameId)?.overrides;
    }

    private static void SetTargetOverrides(AttractionLayoutFrameEditState session, List<AttractionLayoutOverride> overrides)
    {
        if (session?.attraction == null) return;
        if (session.wallLayer)
        {
            AttractionWallLayer layer = session.attraction.FindWallLayerById(session.frameId);
            if (layer == null) return;
            layer.overrides = overrides;
            // Positions are authored against today's Base footprint; runtime keeps
            // them the same distance from this side's edge at any applied size.
            layer.authoredFootprintMeters = session.attraction.DimensionsInFeet * FeetToMeters;
            return;
        }
        AttractionLayoutFrame frame = session.attraction.FindFrame(session.frameId);
        if (frame != null) frame.overrides = overrides;
    }

    /// <summary>"Minimum", a frame's name, or "Front wall".</summary>
    internal static string SessionLabel(AttractionLayoutFrameEditState session)
    {
        if (session == null) return "";
        if (session.wallLayer)
        {
            AttractionWallLayer layer = session.attraction != null ? session.attraction.FindWallLayerById(session.frameId) : null;
            return layer != null ? WallLabel(layer.side) : session.frameId ?? "";
        }
        return Label(session.attraction != null ? session.attraction.FindFrame(session.frameId) : null, session.frameId);
    }

    internal static string WallLabel(AttractionWallSide side)
    {
        switch (side)
        {
            case AttractionWallSide.Back: return "Back wall";
            case AttractionWallSide.Right: return "Right wall";
            case AttractionWallSide.Left: return "Left wall";
            default: return "Front wall";
        }
    }

    private static string TargetNoun(AttractionLayoutFrameEditState session) =>
        session != null && session.wallLayer ? "wall layer" : "frame";

    /// <summary>What Save Frame would store right now (for the inspector panel).</summary>
    internal static bool TryGetPendingChanges(out int changed, out int created,
        out List<string> deletedPaths, out List<string> movedPaths)
    {
        changed = 0;
        created = 0;
        deletedPaths = new List<string>();
        movedPaths = new List<string>();
        if (!IsActive || IsSuspended) return false;
        Diff diff = ComputeDiff(state);
        changed = diff.changedCount;
        created = diff.createdRoots.Count;
        deletedPaths.AddRange(diff.deletedPaths);
        movedPaths.AddRange(diff.movedPaths);
        return true;
    }

    // ------------------------------------------------------------------ Begin

    internal static bool Begin(AttractionTemplate attraction, string frameId) =>
        Begin(attraction, frameId, null, out _);

    internal static bool BeginWallLayer(AttractionTemplate attraction, string layerId) =>
        BeginTarget(attraction, layerId, true, null, out _);

    internal static bool BeginWallLayer(AttractionTemplate attraction, string layerId, string assetPath, out string error) =>
        BeginTarget(attraction, layerId, true, assetPath, out error);

    /// <summary>
    /// Snapshot Base and apply the frame. assetPath names the prefab this hierarchy
    /// saves into when it cannot be inferred (null = infer from the stage/instance).
    /// </summary>
    internal static bool Begin(AttractionTemplate attraction, string frameId, string assetPath, out string error) =>
        BeginTarget(attraction, frameId, false, assetPath, out error);

    private static bool BeginTarget(AttractionTemplate attraction, string frameId, bool wallLayer,
        string assetPath, out string error)
    {
        error = null;
        if (attraction == null)
        {
            error = "There is no attraction to edit.";
            return false;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            error = "Layout frames can't be edited in Play Mode.";
            return false;
        }
        if (IsActive)
        {
            error = $"'{SessionLabel(state)}' of '{state.attraction.name}' is already being edited. Save or cancel it first.";
            return false;
        }
        AttractionLayoutFrame frame = wallLayer ? null : attraction.FindFrame(frameId);
        AttractionWallLayer layer = wallLayer ? attraction.FindWallLayerById(frameId) : null;
        if (!wallLayer && frame == null)
        {
            error = $"'{attraction.name}' has no layout frame '{frameId}'.";
            return false;
        }
        if (wallLayer && layer == null)
        {
            error = $"'{attraction.name}' has no wall layer '{frameId}'.";
            return false;
        }

        PrefabStage stage = PrefabStageUtility.GetPrefabStage(attraction.gameObject);
        bool stageWasClean = stage != null && !StageHasUnsavedChanges(stage);

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        string undoName = wallLayer
            ? $"Edit Wall Layer '{WallLabel(layer.side)}'"
            : $"Edit Layout Frame '{Label(frame, frameId)}'";
        Undo.SetCurrentGroupName(undoName);

        var next = ScriptableObject.CreateInstance<AttractionLayoutFrameEditState>();
        next.hideFlags = HideFlags.HideAndDontSave;
        // Recorded while inactive, so undoing past Begin ends the session.
        Undo.RegisterCompleteObjectUndo(next, undoName);
        // The complete attraction, so undoing past Begin also drops overrides and
        // bakes that a save wrote mid-session (those writes are not Undo-recorded).
        Undo.RegisterCompleteObjectUndo(attraction, undoName);

        next.attraction = attraction;
        next.wallLayer = wallLayer;
        next.frameId = wallLayer ? layer.id
            : frame.kind == AttractionLayoutFrameKind.Minimum ? AttractionTemplate.MinimumFrameId : frame.id;
        next.assetPath = assetPath ?? ResolveAssetPath(attraction);
        next.undoGroup = group;
        next.baseSnapshot = CaptureBase(attraction.transform);
        next.originalOverrides = CloneOverrides(wallLayer ? layer.overrides : frame.overrides);
        next.active = true;
        state = next;
        trackedActive = true;
        suspendedWorkingState = null;

        ApplyOverrides(attraction, wallLayer ? layer.overrides : frame.overrides, true, undoName);
        // The frame state is never something to save; don't let entering it
        // trigger Prefab Mode auto-save.
        if (stageWasClean) stage.ClearDirtiness();
        RepaintAll();
        return true;
    }

    // ------------------------------------------------------------------ Commit / Cancel

    /// <summary>
    /// Save Frame: store the changed properties as the frame's overrides, restore
    /// Base, re-bake and (persist = true) write the prefab asset. Deleted or
    /// re-parented objects block the commit; nothing is changed in that case.
    /// </summary>
    internal static Result Commit(bool persist = true)
    {
        var result = new Result();
        if (!IsActive)
        {
            result.message = "No layout frame is being edited.";
            return result;
        }
        ResumeAfterSave();

        AttractionLayoutFrameEditState session = state;
        AttractionTemplate attraction = session.attraction;
        Diff diff = ComputeDiff(session);
        result.deletedPaths.AddRange(diff.deletedPaths);
        result.movedPaths.AddRange(diff.movedPaths);
        if (diff.Blocked)
        {
            result.message = BlockedMessage(session, diff);
            return result;
        }

        string undoName = $"Save '{SessionLabel(session)}'";
        WriteOverridesAndRestoreBase(session, diff, true, undoName);
        EndState(session, true, undoName);

        string prefabPath = null;
        bool viaAsset = persist && IsScenePrefabInstanceRoot(attraction, out prefabPath);
        // BakeIntoAsset bakes the prefab copy itself; everything else bakes in place.
        if (!viaAsset) AttractionPackingBaker.Bake(attraction, true);
        Undo.CollapseUndoOperations(session.undoGroup);

        if (persist) Persist(attraction, session.assetPath, diff.createdRoots, viaAsset, prefabPath);

        result.success = true;
        result.changedCount = diff.changedCount;
        result.createdCount = diff.createdRoots.Count;
        result.message = $"Saved {TargetNoun(session)} '{SessionLabel(session)}': " +
                         $"{diff.changedCount} change(s), {diff.createdRoots.Count} new object(s).";
        RepaintAll();
        return result;
    }

    /// <summary>
    /// Discard the session: Base is restored, objects created during the session are
    /// destroyed (Undo brings them back) and the frame's overrides return to what they
    /// were at Begin. Objects deleted during the session stay deleted until Undo.
    /// </summary>
    internal static Result Cancel()
    {
        var result = new Result();
        if (!IsActive)
        {
            result.message = "No layout frame is being edited.";
            return result;
        }
        ResumeAfterSave();

        AttractionLayoutFrameEditState session = state;
        AttractionTemplate attraction = session.attraction;
        Diff diff = ComputeDiff(session);
        result.deletedPaths.AddRange(diff.deletedPaths);
        result.movedPaths.AddRange(diff.movedPaths);

        string undoName = $"Cancel '{SessionLabel(session)}'";
        Undo.RecordObject(attraction, undoName);
        if (session.wallLayer)
        {
            AttractionWallLayer layer = attraction.FindWallLayerById(session.frameId);
            if (layer != null) layer.overrides = CloneOverrides(session.originalOverrides);
        }
        else
        {
            AttractionLayoutFrame frame = attraction.FindFrame(session.frameId);
            if (frame != null) frame.overrides = CloneOverrides(session.originalOverrides);
        }
        MarkAttractionChanged(attraction);
        RestoreBase(session, true, undoName);
        for (int i = 0; i < diff.createdRoots.Count; i++)
            if (diff.createdRoots[i] != null) Undo.DestroyObjectImmediate(diff.createdRoots[i]);
        EndState(session, true, undoName);
        // A mid-session save baked the intermediate overrides; bake the originals again.
        if (session.savedDuringSession) AttractionPackingBaker.Bake(attraction, true);
        Undo.CollapseUndoOperations(session.undoGroup);

        result.success = true;
        result.message = diff.deletedPaths.Count > 0
            ? $"Cancelled. {diff.deletedPaths.Count} object(s) deleted while editing are still deleted; Undo brings them back."
            : "Cancelled.";
        RepaintAll();
        return result;
    }

    // ------------------------------------------------------------------ interactive (UI) entry points

    /// <summary>
    /// Inspector "Edit": resolves where the frame can be edited (a scene instance of a
    /// prefab opens that prefab in Prefab Mode, in context), then begins.
    /// </summary>
    internal static bool BeginInteractive(AttractionTemplate attraction, string frameId) =>
        BeginTargetInteractive(attraction, frameId, false);

    /// <summary>Inspector "Edit" on a wall layer; same rules as a frame.</summary>
    internal static bool BeginWallLayerInteractive(AttractionTemplate attraction, string layerId) =>
        BeginTargetInteractive(attraction, layerId, true);

    private static bool BeginTargetInteractive(AttractionTemplate attraction, string frameId, bool wallLayer)
    {
        if (attraction == null) return false;
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Edit Layout Frame", "Layout frames can't be edited in Play Mode.", "OK");
            return false;
        }
        if (IsActive)
        {
            if (IsEditing(attraction) && state.frameId == frameId && state.wallLayer == wallLayer) return true;
            int choice = EditorUtility.DisplayDialogComplex(
                "Edit In Progress",
                $"You are editing '{SessionLabel(state)}' of '{state.attraction.name}'. Save it first?",
                "Save", "Keep Editing", "Discard Changes");
            if (choice == 1) return false;
            if (choice == 0 && !SaveInteractive()) return false;
            if (choice == 2) Cancel();
        }

        AttractionTemplate target = ResolveEditTarget(attraction);
        if (target == null) return false;
        if (!BeginTarget(target, frameId, wallLayer, null, out string error))
        {
            EditorUtility.DisplayDialog(wallLayer ? "Edit Wall Layer" : "Edit Layout Frame", error, "OK");
            return false;
        }
        Selection.activeGameObject = target.gameObject;
        return true;
    }

    internal static bool SaveInteractive()
    {
        Result result = Commit(true);
        if (!result.success && !string.IsNullOrEmpty(result.message))
            EditorUtility.DisplayDialog("Can't Save Frame", result.message, "OK");
        return result.success;
    }

    internal static bool CancelInteractive()
    {
        if (!IsActive) return false;
        if (!IsSuspended)
        {
            Diff peek = ComputeDiff(state);
            if (peek.deletedPaths.Count > 0 && !EditorUtility.DisplayDialog(
                    "Cancel Frame Edit",
                    $"Cancel puts everything else back, but it can't restore the {peek.deletedPaths.Count} object(s) " +
                    $"deleted while editing ({JoinPaths(peek.deletedPaths)}). Undo right after cancelling brings them back.",
                    "Cancel Edit", "Keep Editing"))
                return false;
        }
        return Cancel().success;
    }

    /// <summary>
    /// Where a frame of this attraction is edited. A scene instance that is the root
    /// of its prefab is edited in Prefab Mode: created objects then belong to the
    /// prefab and Save Frame writes the asset. Other objects are edited in place.
    /// Returns null when Prefab Mode could not be opened.
    /// </summary>
    internal static AttractionTemplate ResolveEditTarget(AttractionTemplate attraction)
    {
        if (!IsScenePrefabInstanceRoot(attraction, out string prefabPath)) return attraction;
        // Push frame edits that only live on this instance into the prefab first,
        // so the stage shows the same frames the inspector did.
        if (HasFrameInstanceOverrides(attraction)) AttractionPackingBaker.BakeIntoAsset(attraction);
        PrefabStage stage = PrefabStageUtility.OpenPrefab(prefabPath, attraction.gameObject, PrefabStage.Mode.InContext);
        if (stage == null || stage.prefabContentsRoot == null
            || !string.Equals(stage.assetPath, prefabPath, StringComparison.OrdinalIgnoreCase))
            return null;
        return stage.prefabContentsRoot.GetComponent<AttractionTemplate>();
    }

    // ------------------------------------------------------------------ save guard

    /// <summary>
    /// Save guard (AttractionPackingPrefabSaveProcessor.OnWillSaveAssets): when the
    /// session's prefab or scene is among the saved paths, write the overrides and put
    /// Base back before anything is baked or written. The session resumes afterwards.
    /// </summary>
    internal static void PrepareForSave(string[] paths)
    {
        if (!IsActive || IsSuspended || paths == null) return;
        for (int i = 0; i < paths.Length; i++)
        {
            if (!OwnsPath(paths[i])) continue;
            Suspend();
            return;
        }
    }

    /// <summary>Put the working frame state back after a save. No-op unless suspended.</summary>
    internal static void ResumeAfterSave()
    {
        List<WorkingPose> working = suspendedWorkingState;
        if (working == null) return;
        suspendedWorkingState = null;
        EditorApplication.delayCall -= ResumeAfterSave;
        if (!IsActive) return;

        PrefabStage stage = PrefabStageUtility.GetPrefabStage(state.attraction.gameObject);
        bool stageWasClean = stage != null && !StageHasUnsavedChanges(stage);
        for (int i = 0; i < working.Count; i++)
        {
            WorkingPose pose = working[i];
            Transform target = pose.transform;
            if (target == null) continue;
            if (!target.localPosition.Equals(pose.position)) target.localPosition = pose.position;
            if (!target.localRotation.Equals(pose.rotation)) target.localRotation = pose.rotation;
            if (!target.localScale.Equals(pose.scale)) target.localScale = pose.scale;
        }
        for (int i = 0; i < working.Count; i++)
        {
            Transform target = working[i].transform;
            if (target != null && target.gameObject.activeSelf != working[i].active)
                target.gameObject.SetActive(working[i].active);
        }
        // Just saved: the restored frame state must not trigger another (auto-)save.
        if (stageWasClean) stage.ClearDirtiness();
        RepaintAll();
    }

    private static void Suspend()
    {
        AttractionLayoutFrameEditState session = state;
        Diff diff = ComputeDiff(session);
        if (diff.Blocked)
            Debug.LogWarning($"[AttractionPacking] Saving '{session.attraction.name}' while frame " +
                             $"'{SessionLabel(session)}' is being edited. {BlockedMessage(session, diff)}");

        var working = new List<WorkingPose>();
        Transform root = session.attraction.transform;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform target = all[i];
            if (target == root) continue;
            working.Add(new WorkingPose
            {
                transform = target,
                active = target.gameObject.activeSelf,
                position = target.localPosition,
                rotation = target.localRotation,
                scale = target.localScale,
            });
        }

        // Not Undo-recorded: the undo history keeps describing the live session.
        WriteOverridesAndRestoreBase(session, diff, false, null);
        session.savedDuringSession = true;
        suspendedWorkingState = working;
        EditorApplication.delayCall -= ResumeAfterSave;
        EditorApplication.delayCall += ResumeAfterSave;
    }

    private static bool OwnsPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !IsActive) return false;
        if (!string.IsNullOrEmpty(state.assetPath)
            && string.Equals(path, state.assetPath, StringComparison.OrdinalIgnoreCase)) return true;
        Scene scene = state.attraction.gameObject.scene;
        return scene.IsValid() && !string.IsNullOrEmpty(scene.path)
            && string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ other guards

    private static void OnPrefabSaving(GameObject root)
    {
        if (!IsActive || IsSuspended || root == null) return;
        if (state.attraction.transform.IsChildOf(root.transform)) Suspend();
    }

    private static void OnPrefabSaved(GameObject root)
    {
        if (!IsSuspended || !IsActive || root == null) return;
        if (state.attraction.transform.IsChildOf(root.transform)) ResumeAfterSave();
    }

    private static void OnPrefabStageClosing(PrefabStage stage)
    {
        if (!IsActive || stage == null || stage.prefabContentsRoot == null) return;
        if (!state.attraction.transform.IsChildOf(stage.prefabContentsRoot.transform)) return;
        // The stage and its objects are going away and the author has already chosen
        // to save or discard; nothing here is persisted or Undo-recorded.
        CommitForGuard("its prefab was closed", false, false);
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.ExitingEditMode || !IsActive) return;
        CommitForGuard("Play Mode started", true, true);
    }

    private static void OnBeforeAssemblyReload()
    {
        if (!IsActive) return;
        AttractionLayoutFrameEditState session = state;
        CommitForGuard("scripts are reloading", true, true);
        // HideAndDontSave objects survive the reload; pick the session back up after it.
        session.resumeAfterReload = true;
    }

    private static void ResumeAfterAssemblyReload()
    {
        AttractionLayoutFrameEditState[] states = Resources.FindObjectsOfTypeAll<AttractionLayoutFrameEditState>();
        AttractionLayoutFrameEditState resume = null;
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] == null || !states[i].resumeAfterReload) continue;
            states[i].resumeAfterReload = false;
            resume = states[i];
        }
        if (resume == null || resume.attraction == null || IsActive
            || EditorApplication.isPlayingOrWillChangePlaymode) return;
        BeginTarget(resume.attraction, resume.frameId, resume.wallLayer,
            string.IsNullOrEmpty(resume.assetPath) ? null : resume.assetPath, out _);
    }

    /// <summary>
    /// Commit without the interactive checks: deleted objects are gone either way, so
    /// their original overrides are kept and a warning explains how to get them back.
    /// </summary>
    private static void CommitForGuard(string reason, bool recordUndo, bool bake)
    {
        AttractionLayoutFrameEditState session = state;
        ResumeAfterSave();
        AttractionTemplate attraction = session.attraction;
        string label = SessionLabel(session);
        Diff diff = ComputeDiff(session);
        if (diff.Blocked)
            Debug.LogWarning($"[AttractionPacking] Frame '{label}' of '{attraction.name}' was saved because {reason}. {BlockedMessage(session, diff)}");

        string undoName = $"Save Layout Frame '{label}'";
        WriteOverridesAndRestoreBase(session, diff, recordUndo, undoName);
        EndState(session, recordUndo, undoName);
        if (bake) AttractionPackingBaker.Bake(attraction, recordUndo);
        if (recordUndo) Undo.CollapseUndoOperations(session.undoGroup);
        Debug.Log($"[AttractionPacking] Saved layout frame '{label}' of '{attraction.name}' because {reason}.");
        RepaintAll();
    }

    private static void OnUndoRedoPerformed()
    {
        if (state == null) return;
        bool active = IsActive;
        if (active != trackedActive)
        {
            // Undo stepped back past Begin (the hierarchy and frame data are Base
            // again), or Redo stepped forward into a session.
            trackedActive = active;
            suspendedWorkingState = null;
        }
        RepaintAll();
    }

    /// <summary>Test hook: forget any session without touching the hierarchy.</summary>
    internal static void ResetForTests()
    {
        suspendedWorkingState = null;
        EditorApplication.delayCall -= ResumeAfterSave;
        if (state != null) UnityEngine.Object.DestroyImmediate(state);
        state = null;
        trackedActive = false;
    }

    // ------------------------------------------------------------------ core

    private static List<AttractionLayoutFrameEditState.Entry> CaptureBase(Transform root)
    {
        var entries = new List<AttractionLayoutFrameEditState.Entry>();
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform target = all[i];
            if (target == root) continue;
            entries.Add(new AttractionLayoutFrameEditState.Entry
            {
                transform = target,
                parent = target.parent,
                path = AttractionPackingBaker.PathFrom(root, target),
                active = target.gameObject.activeSelf,
                localPosition = target.localPosition,
                localRotation = target.localRotation,
                localScale = target.localScale,
            });
        }
        return entries;
    }

    /// <summary>
    /// Resolve every snapshot entry: by reference first, then (for references Undo
    /// may have re-created) by path, never claiming a transform twice.
    /// </summary>
    private static Transform[] ResolveEntries(AttractionLayoutFrameEditState session)
    {
        Transform root = session.attraction.transform;
        List<AttractionLayoutFrameEditState.Entry> entries = session.baseSnapshot;
        var resolved = new Transform[entries.Count];
        var claimed = new HashSet<Transform>();
        for (int i = 0; i < entries.Count; i++)
        {
            Transform target = entries[i].transform;
            if (target == null) continue;
            resolved[i] = target;
            claimed.Add(target);
        }
        for (int i = 0; i < entries.Count; i++)
        {
            if (resolved[i] != null || string.IsNullOrEmpty(entries[i].path)) continue;
            Transform found = root.Find(entries[i].path);
            if (found == null || found == root || claimed.Contains(found)) continue;
            resolved[i] = found;
            claimed.Add(found);
        }
        return resolved;
    }

    private static Diff ComputeDiff(AttractionLayoutFrameEditState session)
    {
        var diff = new Diff();
        Transform root = session.attraction.transform;
        List<AttractionLayoutFrameEditState.Entry> entries = session.baseSnapshot;
        Transform[] resolved = ResolveEntries(session);
        var known = new HashSet<Transform>();
        var covered = new HashSet<Transform>();

        for (int i = 0; i < entries.Count; i++)
        {
            AttractionLayoutFrameEditState.Entry entry = entries[i];
            Transform target = resolved[i];
            if (target == null)
            {
                diff.deletedPaths.Add(entry.path);
                continue;
            }
            known.Add(target);
            if (!target.IsChildOf(root) || target.parent != ExpectedParent(entry, root, resolved, entries))
            {
                diff.movedPaths.Add(entry.path);
                continue;
            }
            covered.Add(target);

            var change = new AttractionLayoutOverride();
            if (target.gameObject.activeSelf != entry.active)
            {
                change.setActive = true;
                change.active = target.gameObject.activeSelf;
            }
            if (Differs(target.localPosition, entry.localPosition, PositionEpsilon))
            {
                change.setPosition = true;
                change.localPosition = target.localPosition;
            }
            if (AngleDegrees(entry.localRotation, target.localRotation) > RotationEpsilonDegrees)
            {
                change.setRotation = true;
                change.localRotation = target.localRotation;
            }
            if (Differs(target.localScale, entry.localScale, ScaleEpsilon))
            {
                change.setScale = true;
                change.localScale = target.localScale;
            }
            if (!change.setActive && !change.setPosition && !change.setRotation && !change.setScale) continue;
            change.path = AttractionPackingBaker.PathFrom(root, target);
            change.target = target;
            diff.overrides.Add(change);
            diff.changedCount++;
        }

        // Created during the session: only the top-most new object needs an entry;
        // its children come and go with it. It exists (inactive) in Base and is shown
        // by this frame.
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform target = all[i];
            if (target == root || known.Contains(target)) continue;
            if (target.parent != root && !known.Contains(target.parent)) continue;
            diff.createdRoots.Add(target.gameObject);
            if (!target.gameObject.activeSelf) continue;
            diff.overrides.Add(new AttractionLayoutOverride
            {
                path = AttractionPackingBaker.PathFrom(root, target),
                target = target,
                setActive = true,
                active = true,
            });
        }

        // Keep original overrides this session could not see (their target was already
        // missing, or was deleted/moved during the session), so no authored data is lost.
        List<AttractionLayoutOverride> originals = session.originalOverrides;
        for (int i = 0; i < originals.Count; i++)
        {
            AttractionLayoutOverride original = originals[i];
            if (original == null) continue;
            Transform target = original.Resolve(root);
            if (target != null && covered.Contains(target)) continue;
            diff.overrides.Add(CloneOverride(original));
        }
        return diff;
    }

    private static Transform ExpectedParent(
        AttractionLayoutFrameEditState.Entry entry,
        Transform root,
        Transform[] resolved,
        List<AttractionLayoutFrameEditState.Entry> entries)
    {
        if (entry.parent != null) return entry.parent;
        // The parent reference went stale (Undo re-created it): find its entry by path.
        int slash = entry.path.LastIndexOf('/');
        if (slash < 0) return root;
        string parentPath = entry.path.Substring(0, slash);
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].path == parentPath && resolved[i] != null) return resolved[i];
        return null;
    }

    private static void WriteOverridesAndRestoreBase(
        AttractionLayoutFrameEditState session, Diff diff, bool recordUndo, string undoName)
    {
        AttractionTemplate attraction = session.attraction;
        if (recordUndo) Undo.RecordObject(attraction, undoName);
        SetTargetOverrides(session, diff.overrides);
        MarkAttractionChanged(attraction);

        RestoreBase(session, recordUndo, undoName);
        for (int i = 0; i < diff.createdRoots.Count; i++)
        {
            GameObject created = diff.createdRoots[i];
            if (created == null || !created.activeSelf) continue;
            if (recordUndo) Undo.RecordObject(created, undoName);
            created.SetActive(false);
        }
    }

    private static void RestoreBase(AttractionLayoutFrameEditState session, bool recordUndo, string undoName)
    {
        Transform root = session.attraction.transform;
        List<AttractionLayoutFrameEditState.Entry> entries = session.baseSnapshot;
        Transform[] resolved = ResolveEntries(session);
        var restorable = new bool[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            Transform target = resolved[i];
            // A re-parented object's Base pose is relative to a parent it no longer has.
            restorable[i] = target != null && target.IsChildOf(root)
                && target.parent == ExpectedParent(entries[i], root, resolved, entries);
        }

        // Poses first, then active flags, so nothing is enabled at a frame pose.
        for (int i = 0; i < entries.Count; i++)
        {
            if (!restorable[i]) continue;
            Transform target = resolved[i];
            AttractionLayoutFrameEditState.Entry entry = entries[i];
            bool changed = !target.localPosition.Equals(entry.localPosition)
                || !target.localRotation.Equals(entry.localRotation)
                || !target.localScale.Equals(entry.localScale);
            if (!changed) continue;
            if (recordUndo) Undo.RecordObject(target, undoName);
            target.localPosition = entry.localPosition;
            target.localRotation = entry.localRotation;
            target.localScale = entry.localScale;
        }
        for (int i = 0; i < entries.Count; i++)
        {
            if (!restorable[i]) continue;
            GameObject target = resolved[i].gameObject;
            if (target.activeSelf == entries[i].active) continue;
            if (recordUndo) Undo.RecordObject(target, undoName);
            target.SetActive(entries[i].active);
        }
    }

    private static void ApplyOverrides(AttractionTemplate attraction, List<AttractionLayoutOverride> overrides,
        bool recordUndo, string undoName)
    {
        if (overrides == null) return;
        Transform root = attraction.transform;
        var activeChanges = new List<KeyValuePair<GameObject, bool>>();
        for (int i = 0; i < overrides.Count; i++)
        {
            AttractionLayoutOverride change = overrides[i];
            Transform target = AttractionPackingBaker.ResolveOverrideTarget(root, change);
            if (target == null) continue;
            if (change.setPosition || change.setRotation || change.setScale)
            {
                if (recordUndo) Undo.RecordObject(target, undoName);
                if (change.setPosition) target.localPosition = change.localPosition;
                if (change.setRotation) target.localRotation = change.localRotation;
                if (change.setScale) target.localScale = change.localScale;
            }
            if (change.setActive && target.gameObject.activeSelf != change.active)
                activeChanges.Add(new KeyValuePair<GameObject, bool>(target.gameObject, change.active));
        }
        for (int i = 0; i < activeChanges.Count; i++)
        {
            if (recordUndo) Undo.RecordObject(activeChanges[i].Key, undoName);
            activeChanges[i].Key.SetActive(activeChanges[i].Value);
        }
    }

    private static void EndState(AttractionLayoutFrameEditState session, bool recordUndo, string undoName)
    {
        if (recordUndo) Undo.RecordObject(session, undoName);
        session.active = false;
        if (session == state)
        {
            trackedActive = false;
            suspendedWorkingState = null;
            EditorApplication.delayCall -= ResumeAfterSave;
        }
    }

    private static void Persist(AttractionTemplate attraction, string assetPath,
        List<GameObject> createdRoots, bool viaAsset, string prefabPath)
    {
        PrefabStage stage = PrefabStageUtility.GetPrefabStage(attraction.gameObject);
        if (stage != null)
        {
            SaveStage(stage);
            return;
        }
        if (viaAsset)
        {
            // Objects added on the scene instance must exist in the prefab before
            // BakeIntoAsset re-resolves the frame's override paths there.
            for (int i = 0; i < createdRoots.Count; i++)
            {
                GameObject created = createdRoots[i];
                if (created != null && PrefabUtility.IsAddedGameObjectOverride(created))
                    PrefabUtility.ApplyAddedGameObject(created, prefabPath, InteractionMode.AutomatedAction);
            }
            AttractionPackingBaker.BakeIntoAsset(attraction);
            return;
        }
        Scene scene = attraction.gameObject.scene;
        if (!string.IsNullOrEmpty(assetPath)
            && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            && scene.IsValid() && EditorSceneManager.IsPreviewScene(scene)
            && attraction.transform.parent == null)
        {
            // Prefab contents opened with LoadPrefabContents (tooling and tests).
            WithoutSaveProcessorBake(() => PrefabUtility.SaveAsPrefabAsset(attraction.gameObject, assetPath));
            return;
        }
        if (scene.IsValid() && !EditorSceneManager.IsPreviewScene(scene))
            EditorSceneManager.MarkSceneDirty(scene);
    }

    /// <summary>Save a prefab stage exactly like Cmd/Ctrl+S (the caller has already baked).</summary>
    internal static bool SaveStage(PrefabStage stage)
    {
        if (stage == null) return false;
        bool saved = false;
        WithoutSaveProcessorBake(() =>
        {
            try
            {
                if (StageSaveMethod != null && StageSaveMethod.ReturnType == typeof(bool))
                {
                    saved = (bool)StageSaveMethod.Invoke(stage, null);
                    return;
                }
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out saved);
                if (saved) stage.ClearDirtiness();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AttractionPacking] Could not save '{stage.assetPath}': {e.GetBaseException().Message}");
            }
        });
        return saved;
    }

    private static void WithoutSaveProcessorBake(Action action)
    {
        bool previous = AttractionPackingBaker.AutoBakeInProgress;
        AttractionPackingBaker.AutoBakeInProgress = true;
        try { action(); }
        finally { AttractionPackingBaker.AutoBakeInProgress = previous; }
    }

    private static bool StageHasUnsavedChanges(PrefabStage stage)
    {
        if (stage == null) return false;
        if (StageUnsavedChangesProperty != null
            && StageUnsavedChangesProperty.GetValue(stage) is bool unsaved) return unsaved;
        return stage.scene.isDirty;
    }

    // ------------------------------------------------------------------ helpers

    internal static bool IsScenePrefabInstanceRoot(AttractionTemplate attraction, out string prefabPath)
    {
        prefabPath = null;
        if (attraction == null) return false;
        GameObject go = attraction.gameObject;
        if (PrefabStageUtility.GetPrefabStage(go) != null) return false;
        if (EditorUtility.IsPersistent(go) || !PrefabUtility.IsPartOfPrefabInstance(go)) return false;
        if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go) return false;
        string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
        if (string.IsNullOrEmpty(path)) return false;
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null || asset.GetComponent<AttractionTemplate>() == null) return false;
        prefabPath = path;
        return true;
    }

    internal static bool HasFrameInstanceOverrides(AttractionTemplate attraction)
    {
        if (attraction == null || !PrefabUtility.IsPartOfPrefabInstance(attraction)) return false;
        var serialized = new SerializedObject(attraction);
        SerializedProperty frames = serialized.FindProperty("layoutFrames");
        SerializedProperty walls = serialized.FindProperty("wallLayers");
        return (frames != null && frames.prefabOverride) || (walls != null && walls.prefabOverride);
    }

    private static string ResolveAssetPath(AttractionTemplate attraction)
    {
        GameObject go = attraction.gameObject;
        PrefabStage stage = PrefabStageUtility.GetPrefabStage(go);
        if (stage != null) return stage.assetPath ?? "";
        if (PrefabUtility.IsPartOfPrefabInstance(go))
            return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? "";
        return "";
    }

    private static void MarkAttractionChanged(AttractionTemplate attraction)
    {
        EditorUtility.SetDirty(attraction);
        if (PrefabUtility.IsPartOfPrefabInstance(attraction))
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
    }

    internal static List<AttractionLayoutOverride> CloneOverrides(List<AttractionLayoutOverride> source)
    {
        var copies = new List<AttractionLayoutOverride>();
        if (source == null) return copies;
        for (int i = 0; i < source.Count; i++)
            if (source[i] != null) copies.Add(CloneOverride(source[i]));
        return copies;
    }

    internal static AttractionLayoutOverride CloneOverride(AttractionLayoutOverride source) => new AttractionLayoutOverride
    {
        path = source.path,
        target = source.target,
        setActive = source.setActive,
        active = source.active,
        setPosition = source.setPosition,
        localPosition = source.localPosition,
        setRotation = source.setRotation,
        localRotation = source.localRotation,
        setScale = source.setScale,
        localScale = source.localScale,
    };

    private static bool Differs(Vector3 a, Vector3 b, float epsilon) =>
        Mathf.Abs(a.x - b.x) > epsilon || Mathf.Abs(a.y - b.y) > epsilon || Mathf.Abs(a.z - b.z) > epsilon;

    /// <summary>
    /// Rotation between two orientations in degrees, accurate for tiny angles
    /// (Quaternion.Angle reports 0 for anything below ~0.16°).
    /// </summary>
    internal static float AngleDegrees(Quaternion a, Quaternion b)
    {
        Quaternion delta = Quaternion.Inverse(a) * b;
        float vector = Mathf.Sqrt(delta.x * delta.x + delta.y * delta.y + delta.z * delta.z);
        return 2f * Mathf.Atan2(vector, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
    }

    private static string BlockedMessage(AttractionLayoutFrameEditState session, Diff diff)
    {
        var message = new StringBuilder();
        string label = SessionLabel(session);
        if (diff.deletedPaths.Count > 0)
            message.Append($"Frames can't delete objects, and {diff.deletedPaths.Count} object(s) were deleted while editing '{label}': " +
                           $"{JoinPaths(diff.deletedPaths)}. Undo (Cmd/Ctrl+Z) to bring them back, then turn them off instead — " +
                           "an object you turn off disappears only in this frame. ");
        if (diff.movedPaths.Count > 0)
            message.Append($"Frames can't move objects to a different parent, and {diff.movedPaths.Count} object(s) were: " +
                           $"{JoinPaths(diff.movedPaths)}. Undo the move; a frame can move, rotate, scale and show or hide objects under the same parent.");
        return message.ToString().Trim();
    }

    private static string JoinPaths(List<string> paths)
    {
        const int shown = 5;
        var builder = new StringBuilder();
        for (int i = 0; i < paths.Count && i < shown; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append('\'').Append(paths[i]).Append('\'');
        }
        if (paths.Count > shown) builder.Append($" and {paths.Count - shown} more");
        return builder.ToString();
    }

    internal static string Label(AttractionLayoutFrame frame, string fallbackId)
    {
        if (frame == null) return fallbackId ?? "";
        if (!string.IsNullOrEmpty(frame.name)) return frame.name;
        return frame.kind == AttractionLayoutFrameKind.Minimum ? "Minimum" : frame.id;
    }

    internal static string FormatFeet(Vector2 feet) => $"{feet.x:0.##} × {feet.y:0.##} ft";

    private static void RepaintAll()
    {
        SceneView.RepaintAll();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
    }

    // ------------------------------------------------------------------ Scene view

    private static void OnSceneGUI(SceneView view)
    {
        if (!IsActive || view == null) return;
        AttractionTemplate attraction = state.attraction;
        if (view.camera != null && !StageUtility.IsGameObjectRenderedByCamera(attraction.gameObject, view.camera)) return;
        if (state.wallLayer)
        {
            AttractionWallLayer layer = CurrentWallLayer;
            if (layer == null) return;
            DrawWallLayerScene(view, attraction, layer);
            return;
        }
        AttractionLayoutFrame frame = CurrentFrame;
        if (frame == null) return;

        string label = Label(frame, state.frameId);
        Vector2 size = frame.SizeMeters;
        Vector2 baseSize = attraction.DimensionsInFeet * FeetToMeters;
        Color frameColor = new Color(1f, 0.55f, 0.1f, 1f);

        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;
        DrawDashedRectangle(baseSize, 0.01f, new Color(1f, 1f, 1f, 0.45f));
        if (size.x > 0f && size.y > 0f)
        {
            Vector3[] corners = Rectangle(size, 0.02f);
            Handles.DrawSolidRectangleWithOutline(corners, new Color(frameColor.r, frameColor.g, frameColor.b, 0.06f), frameColor);
            if (sceneLabelStyle == null)
            {
                sceneLabelStyle = new GUIStyle(EditorStyles.boldLabel);
                sceneLabelStyle.normal.textColor = frameColor;
            }
            Handles.Label(new Vector3(-size.x * 0.5f, 0.05f, size.y * 0.5f),
                $"Editing frame {label} ({FormatFeet(frame.sizeFeet)})", sceneLabelStyle);
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;

        DrawScenePanel(view, attraction, frame, label);
    }

    private static void DrawScenePanel(SceneView view, AttractionTemplate attraction, AttractionLayoutFrame frame, string label)
    {
        float height = view.camera != null
            ? view.camera.pixelHeight / EditorGUIUtility.pixelsPerPoint
            : view.position.height;
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10f, Mathf.Max(10f, height - 82f), 320f, 72f), EditorStyles.helpBox);
        GUILayout.Label($"Editing frame '{label}' · {FormatFeet(frame.sizeFeet)}", EditorStyles.boldLabel);
        GUILayout.Label("Only what you move, rotate, scale, hide, show or add is stored.", EditorStyles.miniLabel);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save Frame")) EditorApplication.delayCall += () => SaveInteractive();
        if (GUILayout.Button("Cancel")) EditorApplication.delayCall += () => CancelInteractive();
        if (GUILayout.Button("Select Attraction")) Selection.activeGameObject = attraction.gameObject;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        Handles.EndGUI();
    }

    /// <summary>Height of the ghost wall drawn while editing a wall layer.</summary>
    internal const float GhostWallHeightMeters = 2.4f;

    private static void DrawWallLayerScene(SceneView view, AttractionTemplate attraction, AttractionWallLayer layer)
    {
        Vector2 baseSize = attraction.DimensionsInFeet * FeetToMeters;
        string label = WallLabel(layer.side);
        Color wallColor = new Color(0.35f, 0.75f, 1f, 1f);
        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;
        DrawDashedRectangle(baseSize, 0.01f, new Color(1f, 1f, 1f, 0.45f));
        Vector3[] wall = GhostWallQuad(baseSize, layer.side, GhostWallHeightMeters);
        if (wall != null)
        {
            Handles.DrawSolidRectangleWithOutline(wall, new Color(wallColor.r, wallColor.g, wallColor.b, 0.12f), wallColor);
            if (sceneLabelStyle == null)
            {
                sceneLabelStyle = new GUIStyle(EditorStyles.boldLabel);
                sceneLabelStyle.normal.textColor = wallColor;
            }
            Vector3 top = (wall[1] + wall[2]) * 0.5f + Vector3.up * 0.1f;
            Handles.Label(top, $"Editing {label} — this edge is the wall", sceneLabelStyle);
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;

        float height = view.camera != null
            ? view.camera.pixelHeight / EditorGUIUtility.pixelsPerPoint
            : view.position.height;
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10f, Mathf.Max(10f, height - 98f), 340f, 88f), EditorStyles.helpBox);
        GUILayout.Label($"Editing wall layer '{label}'", EditorStyles.boldLabel);
        GUILayout.Label("Shown only where this side stands on a real wall. Base must still work without it.", EditorStyles.miniLabel);
        GUILayout.Label("Only what you move, rotate, scale, hide, show or add is stored.", EditorStyles.miniLabel);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save Wall Layer")) EditorApplication.delayCall += () => SaveInteractive();
        if (GUILayout.Button("Cancel")) EditorApplication.delayCall += () => CancelInteractive();
        if (GUILayout.Button("Select Attraction")) Selection.activeGameObject = attraction.gameObject;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        Handles.EndGUI();
    }

    /// <summary>The vertical quad standing on one side's footprint edge (attraction-local).</summary>
    internal static Vector3[] GhostWallQuad(Vector2 footprint, AttractionWallSide side, float height)
    {
        if (footprint.x <= 0f || footprint.y <= 0f) return null;
        float hx = footprint.x * 0.5f, hz = footprint.y * 0.5f;
        Vector3 a, b;
        switch (side)
        {
            case AttractionWallSide.Back: a = new Vector3(hx, 0f, -hz); b = new Vector3(-hx, 0f, -hz); break;
            case AttractionWallSide.Right: a = new Vector3(hx, 0f, hz); b = new Vector3(hx, 0f, -hz); break;
            case AttractionWallSide.Left: a = new Vector3(-hx, 0f, -hz); b = new Vector3(-hx, 0f, hz); break;
            default: a = new Vector3(-hx, 0f, hz); b = new Vector3(hx, 0f, hz); break;
        }
        return new[] { a, a + Vector3.up * height, b + Vector3.up * height, b };
    }

    private static Vector3[] Rectangle(Vector2 size, float height) => new[]
    {
        new Vector3(-size.x * 0.5f, height, -size.y * 0.5f),
        new Vector3(-size.x * 0.5f, height, size.y * 0.5f),
        new Vector3(size.x * 0.5f, height, size.y * 0.5f),
        new Vector3(size.x * 0.5f, height, -size.y * 0.5f),
    };

    private static void DrawDashedRectangle(Vector2 size, float height, Color color)
    {
        if (size.x <= 0f || size.y <= 0f) return;
        Vector3[] corners = Rectangle(size, height);
        Handles.color = color;
        for (int i = 0; i < 4; i++)
            Handles.DrawDottedLine(corners[i], corners[(i + 1) % 4], 4f);
    }
}
