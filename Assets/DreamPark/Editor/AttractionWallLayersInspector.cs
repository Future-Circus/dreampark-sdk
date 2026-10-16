using System;
using System.Collections.Generic;
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wall layer list mutations (WALL_LAYERS_SPEC.md §7). Every change is one Undo step.
/// </summary>
internal static class AttractionWallLayerAuthoring
{
    internal static string NewLayerId(AttractionTemplate attraction)
    {
        var used = new HashSet<string>();
        IReadOnlyList<AttractionWallLayer> layers = attraction.WallLayers;
        for (int i = 0; i < layers.Count; i++)
            if (!string.IsNullOrEmpty(layers[i]?.id)) used.Add(layers[i].id);
        while (true)
        {
            string id = "w" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (used.Add(id)) return id;
        }
    }

    /// <summary>Add a layer for an unused side (at the bottom = lowest priority). Null when the side is taken.</summary>
    internal static AttractionWallLayer AddLayer(AttractionTemplate attraction, AttractionWallSide side)
    {
        if (attraction == null || attraction.FindWallLayer(side) != null) return null;
        var layer = new AttractionWallLayer
        {
            id = NewLayerId(attraction),
            side = side,
            authoredFootprintMeters = attraction.DimensionsInFeet * AttractionLayoutFrameAuthoring.FeetToMeters,
        };
        Mutate(attraction, $"Add {AttractionLayoutFrameEditSession.WallLabel(side)} Layer", layers => layers.Add(layer));
        return layer;
    }

    internal static bool DeleteLayer(AttractionTemplate attraction, string layerId)
    {
        if (attraction?.FindWallLayerById(layerId) == null) return false;
        Mutate(attraction, "Delete Wall Layer", layers => layers.RemoveAll(l => l != null && l.id == layerId));
        return true;
    }

    /// <summary>Move a layer up (-1) or down (+1). The list order is its priority.</summary>
    internal static bool MoveLayer(AttractionTemplate attraction, string layerId, int direction)
    {
        var layers = new List<AttractionWallLayer>(attraction.WallLayers);
        int index = layers.FindIndex(l => l != null && l.id == layerId);
        int next = index + direction;
        if (index < 0 || next < 0 || next >= layers.Count) return false;
        Mutate(attraction, "Reorder Wall Layers", list =>
        {
            AttractionWallLayer moved = list[index];
            list[index] = list[next];
            list[next] = moved;
        });
        return true;
    }

    private static void Mutate(AttractionTemplate attraction, string undoName, Action<List<AttractionWallLayer>> change)
    {
        Undo.RecordObject(attraction, undoName);
        var layers = new List<AttractionWallLayer>(attraction.WallLayers);
        change(layers);
        attraction.SetWallLayers(layers);
        EditorUtility.SetDirty(attraction);
        if (PrefabUtility.IsPartOfPrefabInstance(attraction))
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
    }
}

/// <summary>
/// The Wall Layers inspector section: a reorderable list (order = priority), Edit /
/// Delete per layer, warnings, and a walled-sides preview. Drawn by
/// AttractionTemplatePackingEditor under Layout Frames.
/// </summary>
internal static class AttractionWallLayersInspector
{
    private static readonly Color WallColor = new Color(0.35f, 0.75f, 1f, 1f);

    internal static void DrawSection(AttractionTemplate attraction)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Wall Layers", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Optional extras for sides that stand on a real wall. The park knows which edges of each space are walls; " +
            "when one of this attraction's sides is seated on one, that side's layer switches on. Without walls the " +
            "attraction must still be complete — never put anything the game needs in a wall layer. " +
            "Order is priority: an object two walled layers both move goes to the higher one.",
            EditorStyles.wordWrappedMiniLabel);

        bool locked = Application.isPlaying || AttractionLayoutFrameEditSession.IsEditing(attraction)
                      || EditorUtility.IsPersistent(attraction);
        using (new EditorGUI.DisabledScope(locked))
        {
            IReadOnlyList<AttractionWallLayer> layers = attraction.WallLayers;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i] != null) DrawLayerRow(attraction, layers[i], i, layers.Count);
            DrawAddButtons(attraction);
        }
        if (EditorUtility.IsPersistent(attraction))
            EditorGUILayout.HelpBox("Open the prefab to edit its wall layers.", MessageType.Info);

        List<string> warnings = AttractionPackingBaker.GetWallLayerWarnings(attraction);
        for (int i = 0; i < warnings.Count; i++)
            EditorGUILayout.HelpBox(warnings[i], MessageType.Warning);

        if (attraction.HasWallLayers) DrawPreview(attraction);
    }

    private static void DrawLayerRow(AttractionTemplate attraction, AttractionWallLayer layer, int index, int count)
    {
        string label = AttractionLayoutFrameEditSession.WallLabel(layer.side);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        Color previous = GUI.color;
        GUI.color = WallColor;
        GUILayout.Label("▌", GUILayout.Width(10f));
        GUI.color = previous;
        EditorGUILayout.LabelField($"{index + 1}. {label}", EditorStyles.boldLabel, GUILayout.MinWidth(90f));
        int changes = layer.overrides != null ? layer.overrides.Count : 0;
        GUILayout.Label($"{changes} override{(changes == 1 ? "" : "s")}", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        using (new EditorGUI.DisabledScope(index == 0))
            if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                Defer(() => Reorder(attraction, layer.id, -1));
        using (new EditorGUI.DisabledScope(index == count - 1))
            if (GUILayout.Button("▼", EditorStyles.miniButtonMid, GUILayout.Width(24f)))
                Defer(() => Reorder(attraction, layer.id, 1));
        if (GUILayout.Button("Edit", EditorStyles.miniButtonMid, GUILayout.Width(48f)))
            Defer(() => AttractionLayoutFrameEditSession.BeginWallLayerInteractive(attraction, layer.id));
        if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(54f)))
        {
            Defer(() =>
            {
                if (!EditorUtility.DisplayDialog(
                        "Delete Wall Layer",
                        $"Delete the {label} layer? Objects that only appear in it stay in the prefab, turned off.",
                        "Delete", "Cancel")) return;
                if (AttractionWallLayerAuthoring.DeleteLayer(attraction, layer.id))
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
            });
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private static void Reorder(AttractionTemplate attraction, string layerId, int direction)
    {
        if (AttractionWallLayerAuthoring.MoveLayer(attraction, layerId, direction))
            AttractionLayoutFrameAuthoring.RefreshBake(attraction);
    }

    private static void DrawAddButtons(AttractionTemplate attraction)
    {
        var free = new List<AttractionWallSide>();
        for (int i = 0; i < AttractionWallSides.All.Length; i++)
            if (attraction.FindWallLayer(AttractionWallSides.All[i]) == null) free.Add(AttractionWallSides.All[i]);
        if (free.Count == 0) return;
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Add wall layer:", GUILayout.Width(100f));
        for (int i = 0; i < free.Count; i++)
        {
            AttractionWallSide side = free[i];
            string name = AttractionLayoutFrameEditSession.WallLabel(side).Replace(" wall", "");
            if (GUILayout.Button($"{name} ({AxisName(side)})", EditorStyles.miniButton))
            {
                Defer(() =>
                {
                    AttractionWallLayer layer = AttractionWallLayerAuthoring.AddLayer(attraction, side);
                    if (layer == null) return;
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
                    AttractionLayoutFrameEditSession.BeginWallLayerInteractive(attraction, layer.id);
                });
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private static void DrawPreview(AttractionTemplate attraction)
    {
        EditorGUILayout.Space(2f);
        EditorGUILayout.LabelField("Walled sides preview", EditorStyles.miniBoldLabel);
        AttractionWallMask mask = attraction.LayoutPreviewWalls;
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < AttractionWallSides.All.Length; i++)
        {
            AttractionWallSide side = AttractionWallSides.All[i];
            bool on = AttractionWallSides.Has(mask, side);
            bool next = GUILayout.Toggle(on, $"{AttractionLayoutFrameEditSession.WallLabel(side).Replace(" wall", "")} ({AxisName(side)})",
                EditorStyles.miniButton);
            if (next) mask |= AttractionWallSides.Mask(side);
            else mask &= ~AttractionWallSides.Mask(side);
        }
        EditorGUILayout.EndHorizontal();
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(attraction, "Preview Walled Sides");
            attraction.SetLayoutPreviewWalls(mask);
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
        }
        EditorGUILayout.LabelField(
            Application.isPlaying
                ? "Play Mode: the chosen sides' layers are applied to the live attraction, resolved in list order."
                : "Edit Mode draws the walls only. Enter Play Mode to see the layers applied, or Edit a layer to change it.",
            EditorStyles.wordWrappedMiniLabel);
    }

    /// <summary>Scene view: ghost walls for the previewed sides (not while a session draws its own).</summary>
    internal static void DrawSceneGhosts(AttractionTemplate attraction)
    {
        if (attraction == null || AttractionLayoutFrameEditSession.IsEditing(attraction)) return;
        AttractionWallMask mask = attraction.LayoutPreviewWalls;
        if (Application.isPlaying && attraction.Walls != AttractionWallMask.None) mask |= attraction.Walls;
        if (mask == AttractionWallMask.None) return;
        Vector2 footprint = Application.isPlaying
            ? attraction.RuntimeFootprintMeters
            : attraction.DimensionsInFeet * AttractionLayoutFrameAuthoring.FeetToMeters;
        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;
        for (int i = 0; i < AttractionWallSides.All.Length; i++)
        {
            AttractionWallSide side = AttractionWallSides.All[i];
            if (!AttractionWallSides.Has(mask, side)) continue;
            Vector3[] quad = AttractionLayoutFrameEditSession.GhostWallQuad(
                footprint, side, AttractionLayoutFrameEditSession.GhostWallHeightMeters);
            if (quad == null) continue;
            Handles.DrawSolidRectangleWithOutline(quad, new Color(WallColor.r, WallColor.g, WallColor.b, 0.08f), WallColor);
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
    }

    private static string AxisName(AttractionWallSide side)
    {
        switch (side)
        {
            case AttractionWallSide.Back: return "-Z";
            case AttractionWallSide.Right: return "+X";
            case AttractionWallSide.Left: return "-X";
            default: return "+Z";
        }
    }

    private static void Defer(Action action)
    {
        EditorApplication.delayCall += () =>
        {
            action();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        };
    }
}
