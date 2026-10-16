using System.Collections.Generic;
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Entries & exits authoring for AttractionTemplate: the inspector section
/// (drawn by AttractionTemplatePackingEditor), always-on arrow gizmos (blue =
/// entry, red = exit) and, while the attraction is selected, handles that drag
/// a portal along the footprint edge (hold Ctrl/Cmd to snap to 1/16 turns).
///
/// The gizmos, the handles and the inspector all work on the same layout: the
/// frame being edited, else (Play Mode) the frame last applied, else the frame
/// the debug preview resolves to, else Base — drawn at that layout's footprint.
/// </summary>
internal static class AttractionPortalsEditor
{
    internal static readonly Color EntryColor = new Color(0.15f, 0.52f, 1f, 1f);
    internal static readonly Color ExitColor = new Color(1f, 0.22f, 0.2f, 1f);
    private const string ShowGizmosPref = "DreamPark.AttractionPortals.ShowGizmos";
    private const float SnapStep = 1f / 16f;

    private static GUIStyle entryLabelStyle;
    private static GUIStyle exitLabelStyle;

    internal static bool ShowGizmos
    {
        get => EditorPrefs.GetBool(ShowGizmosPref, true);
        set => EditorPrefs.SetBool(ShowGizmosPref, value);
    }

    // ------------------------------------------------------------------ layout

    /// <summary>The layout the portal UI is showing, and its footprint in prefab-local metres.</summary>
    internal static void ResolveLayout(AttractionTemplate attraction, out string frameId, out Vector2 footprintMeters)
    {
        if (AttractionLayoutFrameEditSession.IsEditing(attraction))
        {
            frameId = AttractionLayoutFrameEditSession.FrameId;
            AttractionLayoutFrame frame = attraction.FindFrame(frameId);
            footprintMeters = frame != null ? frame.SizeMeters : attraction.DimensionsInFeet * 0.3048f;
            return;
        }
        if (Application.isPlaying)
        {
            frameId = attraction.CurrentFrameId;
            footprintMeters = attraction.RuntimeFootprintMeters;
            return;
        }
        if (attraction.HasLayoutFrames)
        {
            attraction.TryResolveLayoutPreview(out frameId, out Vector2 frameScale);
            footprintMeters = attraction.GetFrameFootprintMeters(frameId, frameScale);
            return;
        }
        frameId = AttractionTemplate.BaseFrameId;
        footprintMeters = attraction.HasPackingBake && attraction.PackingPreviewScale != Vector2.one
            ? attraction.GetPackingFootprintMeters(attraction.PackingPreviewScale, false)
            : attraction.RuntimeFootprintMeters;
    }

    private static bool IsBase(string frameId) =>
        string.IsNullOrEmpty(frameId) || frameId == AttractionTemplate.BaseFrameId;

    /// <summary>True when the layout's list is Base's own or the frame's own (not inherited).</summary>
    private static bool OwnsList(AttractionTemplate attraction, string frameId, out AttractionLayoutFrame frame)
    {
        frame = IsBase(frameId) ? null : attraction.FindFrame(frameId);
        return frame == null || frame.overridePortals;
    }

    // ------------------------------------------------------------------ gizmos

    [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active)]
    private static void DrawPortalGizmos(AttractionTemplate attraction, GizmoType gizmoType)
    {
        if (!ShowGizmos || attraction == null || !attraction.isActiveAndEnabled) return;
        ResolveLayout(attraction, out string frameId, out Vector2 footprint);
        if (footprint.x <= 0f || footprint.y <= 0f) return;
        bool inherited = !OwnsList(attraction, frameId, out _);
        IReadOnlyList<AttractionPortal> portals = attraction.GetPortals(frameId);

        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;
        int entries = 0, exits = 0;
        for (int i = 0; i < portals.Count; i++)
        {
            AttractionPortal portal = portals[i];
            if (portal == null) continue;
            int number = portal.IsEntry ? ++entries : ++exits;
            DrawArrow(portal, footprint, number, inherited);
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
    }

    /// <summary>Called with Handles.matrix = the attraction's localToWorldMatrix.</summary>
    private static void DrawArrow(AttractionPortal portal, Vector2 footprint, int number, bool inherited)
    {
        Color color = portal.IsEntry ? EntryColor : ExitColor;
        Vector3 point = AttractionPortalGeometry.LocalPoint(portal.position, footprint) + Vector3.up * 0.02f;
        Vector3 dir = AttractionPortalGeometry.WalkDirection(portal);
        Vector3 side = new Vector3(dir.z, 0f, -dir.x);

        // A flat arrow straddling the edge, pointing the way guests walk.
        const float halfLength = 0.45f, shaftHalfWidth = 0.09f, headHalfWidth = 0.27f, headLength = 0.34f;
        Vector3 tail = point - dir * halfLength;
        Vector3 tip = point + dir * halfLength;
        Vector3 neck = tip - dir * headLength;
        Vector3[] shaft =
        {
            tail + side * shaftHalfWidth, neck + side * shaftHalfWidth,
            neck - side * shaftHalfWidth, tail - side * shaftHalfWidth,
        };
        Vector3[] head = { neck + side * headHalfWidth, tip, neck - side * headHalfWidth };

        Handles.color = new Color(color.r, color.g, color.b, inherited ? 0.35f : 0.7f);
        Handles.DrawAAConvexPolygon(shaft);
        Handles.DrawAAConvexPolygon(head);
        Handles.color = color;
        Handles.DrawAAPolyLine(3f,
            shaft[3], shaft[0], shaft[1], head[0], head[1], head[2], shaft[2], shaft[3]);

        // An upright marker so the portal reads from low and side-on angles too.
        Vector3 top = point + Vector3.up * 1.1f;
        Handles.DrawAAPolyLine(2f, point, top);
        if (Event.current != null && Event.current.type == EventType.Repaint)
            Handles.ArrowHandleCap(0, top - dir * 0.3f, Quaternion.LookRotation(dir, Vector3.up), 0.6f, EventType.Repaint);

        GUIStyle style = LabelStyle(portal.IsEntry);
        string label = (portal.IsEntry ? "ENTRY " : "EXIT ") + number + (inherited ? " (Base)" : "");
        Handles.Label(top + Vector3.up * 0.2f, label, style);
    }

    private static GUIStyle LabelStyle(bool entry)
    {
        if (entry && entryLabelStyle != null) return entryLabelStyle;
        if (!entry && exitLabelStyle != null) return exitLabelStyle;
        var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = entry ? EntryColor : ExitColor;
        if (entry) entryLabelStyle = style; else exitLabelStyle = style;
        return style;
    }

    // ------------------------------------------------------------------ scene handles

    internal static void DrawSceneHandles(AttractionTemplate attraction)
    {
        if (!ShowGizmos || attraction == null) return;
        ResolveLayout(attraction, out string frameId, out Vector2 footprint);
        if (footprint.x <= 0f || footprint.y <= 0f) return;
        if (!OwnsList(attraction, frameId, out AttractionLayoutFrame frame)) return; // inherited: edit Base
        IReadOnlyList<AttractionPortal> portals = attraction.GetPortals(frameId);
        Transform root = attraction.transform;
        var floor = new Plane(root.up, root.position);

        Color previousColor = Handles.color;
        for (int i = 0; i < portals.Count; i++)
        {
            AttractionPortal portal = portals[i];
            if (portal == null) continue;
            Vector3 world = root.TransformPoint(AttractionPortalGeometry.LocalPoint(portal.position, footprint));
            float size = HandleUtility.GetHandleSize(world) * 0.1f;
            Handles.color = portal.IsEntry ? EntryColor : ExitColor;
            EditorGUI.BeginChangeCheck();
            Handles.FreeMoveHandle(world, size, Vector3.zero, Handles.SphereHandleCap);
            if (!EditorGUI.EndChangeCheck()) continue;

            // Project the mouse onto the attraction's floor plane, then onto the edge.
            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            if (!floor.Raycast(ray, out float distance)) continue;
            Vector3 local = root.InverseTransformPoint(ray.GetPoint(distance));
            float t = AttractionPortalGeometry.FromLocalPoint(local, footprint);
            if (EditorGUI.actionKey) t = AttractionPortalGeometry.Wrap(Mathf.Round(t / SnapStep) * SnapStep);
            if (Mathf.Approximately(t, portal.position)) continue;
            Undo.RecordObject(attraction, portal.IsEntry ? "Move Attraction Entry" : "Move Attraction Exit");
            portal.position = t;
            MarkChanged(attraction);
        }
        Handles.color = previousColor;
    }

    // ------------------------------------------------------------------ inspector

    internal static void DrawInspectorSection(AttractionTemplate attraction)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Entries & Exits", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Where guests come in (blue) and leave (red) along the footprint edge. Position runs 0–1 around the " +
            "edge: 0 = middle of -Z, clockwise from above: 0.25 = -X, 0.5 = +Z, 0.75 = +X (×360 = degrees). " +
            "It stretches with the layout. Drag the handles in the Scene view; hold Ctrl/Cmd to snap.",
            EditorStyles.wordWrappedMiniLabel);

        bool show = EditorGUILayout.Toggle("Show Entry/Exit Gizmos", ShowGizmos);
        if (show != ShowGizmos)
        {
            ShowGizmos = show;
            SceneView.RepaintAll();
        }

        ResolveLayout(attraction, out string frameId, out _);
        if (attraction.HasLayoutFrames) DrawLayoutPicker(attraction, ref frameId);

        bool owns = OwnsList(attraction, frameId, out AttractionLayoutFrame frame);
        string layoutId = frameId;
        if (frame != null)
        {
            bool useBase = EditorGUILayout.ToggleLeft("Use Base entries & exits", !frame.overridePortals);
            if (useBase == frame.overridePortals)
            {
                // Deferred: the list switches between Base's and the frame's, which
                // changes the control count mid-event.
                Defer(() =>
                {
                    AttractionLayoutFrame target = attraction != null ? attraction.FindFrame(layoutId) : null;
                    if (target == null) return;
                    Undo.RecordObject(attraction, "Toggle Frame Entries & Exits");
                    target.overridePortals = !useBase;
                    // First time a frame takes its own list, start from Base's.
                    if (target.overridePortals && (target.portals == null || target.portals.Count == 0))
                        target.portals = AttractionPortal.CloneList(attraction.Portals);
                    MarkChanged(attraction);
                });
            }
        }

        IReadOnlyList<AttractionPortal> portals = attraction.GetPortals(frameId);
        using (new EditorGUI.DisabledScope(!owns))
        {
            int remove = -1;
            for (int i = 0; i < portals.Count; i++)
            {
                AttractionPortal portal = portals[i];
                if (portal == null) continue;
                EditorGUILayout.BeginHorizontal();
                Color previous = GUI.color;
                GUI.color = portal.IsEntry ? EntryColor : ExitColor;
                GUILayout.Label("■", GUILayout.Width(12f));
                GUI.color = previous;
                EditorGUI.BeginChangeCheck();
                var kind = (AttractionPortalKind)EditorGUILayout.EnumPopup(portal.kind, GUILayout.Width(62f));
                float position = EditorGUILayout.Slider(portal.position, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(attraction, "Edit Attraction Entry/Exit");
                    portal.kind = kind;
                    portal.position = AttractionPortalGeometry.Wrap(position);
                    MarkChanged(attraction);
                }
                GUILayout.Label($"{portal.Degrees:0}° {AttractionPortalGeometry.EdgeLabel(portal.position)}",
                    EditorStyles.miniLabel, GUILayout.Width(82f));
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f))) remove = i;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Entry"))
                Mutate(attraction, layoutId, "Add Attraction Entry", list =>
                    list.Add(new AttractionPortal(AttractionPortalKind.Entry, AttractionPortalGeometry.DefaultEntryPosition)));
            if (GUILayout.Button("Add Exit"))
                Mutate(attraction, layoutId, "Add Attraction Exit", list =>
                    list.Add(new AttractionPortal(AttractionPortalKind.Exit, 0.5f)));
            if (GUILayout.Button("Reset"))
                Mutate(attraction, layoutId, "Reset Attraction Entries & Exits", list =>
                {
                    list.Clear();
                    list.AddRange(AttractionPortal.DefaultList());
                });
            EditorGUILayout.EndHorizontal();
            if (remove >= 0)
                Mutate(attraction, layoutId, "Remove Attraction Entry/Exit", list =>
                {
                    if (remove < list.Count) list.RemoveAt(remove);
                });
        }

        if (!attraction.HasEntry(frameId))
            EditorGUILayout.HelpBox(
                "This layout has no entry. Add one so park placement knows where guests come in.",
                MessageType.Warning);
    }

    /// <summary>Base / Minimum / custom frames. Picking one points the debug preview at it.</summary>
    private static void DrawLayoutPicker(AttractionTemplate attraction, ref string frameId)
    {
        var ids = new List<string> { AttractionTemplate.BaseFrameId };
        var labels = new List<string> { "Base" };
        AttractionLayoutFrame minimum = attraction.MinimumFrame;
        if (minimum != null)
        {
            ids.Add(AttractionTemplate.MinimumFrameId);
            labels.Add("Minimum");
        }
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null || frame.kind != AttractionLayoutFrameKind.Custom) continue;
            ids.Add(frame.id);
            labels.Add(AttractionLayoutFrameEditSession.Label(frame, frame.id));
        }
        int current = Mathf.Max(0, ids.IndexOf(IsBase(frameId) ? AttractionTemplate.BaseFrameId : frameId));
        bool locked = AttractionLayoutFrameEditSession.IsEditing(attraction) || Application.isPlaying;
        using (new EditorGUI.DisabledScope(locked))
        {
            int picked = EditorGUILayout.Popup(
                new GUIContent("Layout", "Entries & exits are per layout frame. Picking a frame here shows it in the debug preview."),
                current, labels.ToArray());
            if (picked == current || locked) return;
            Undo.RecordObject(attraction, "Preview Layout Frame");
            attraction.SetLayoutPreview(Vector2.zero, ids[picked]);
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
            frameId = ids[picked];
        }
    }

    /// <summary>Deferred add/remove/reset on the layout's own list (Base or the frame's).</summary>
    private static void Mutate(AttractionTemplate attraction, string frameId, string undoName,
        System.Action<List<AttractionPortal>> change)
    {
        Defer(() =>
        {
            if (attraction == null) return;
            AttractionLayoutFrame frame = IsBase(frameId) ? null : attraction.FindFrame(frameId);
            if (frame != null && !frame.overridePortals) return;
            Undo.RecordObject(attraction, undoName);
            var list = AttractionPortal.CloneList(frame != null ? frame.portals : attraction.Portals);
            change(list);
            if (frame != null) frame.portals = list;
            else attraction.SetPortals(list);
            MarkChanged(attraction);
        });
    }

    private static void Defer(System.Action action) => EditorApplication.delayCall += () => action();

    private static void MarkChanged(AttractionTemplate attraction)
    {
        EditorUtility.SetDirty(attraction);
        if (PrefabUtility.IsPartOfPrefabInstance(attraction))
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
        SceneView.RepaintAll();
    }
}
