using System;
using System.Collections.Generic;
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Undoable edits of an attraction's layout-frame list (spec §6). Each call records
/// Undo on the attraction; the caller decides when to re-bake (RefreshBake).
/// </summary>
internal static class AttractionLayoutFrameAuthoring
{
    internal const float FeetToMeters = 0.3048f;
    internal const float MinimumFrameSideFeet = 0.5f;

    internal static string NewFrameId(AttractionTemplate attraction)
    {
        var used = new HashSet<string> { AttractionTemplate.BaseFrameId, AttractionTemplate.MinimumFrameId };
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
            if (!string.IsNullOrEmpty(frames[i]?.id)) used.Add(frames[i].id);
        while (true)
        {
            string id = "f" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (used.Add(id)) return id;
        }
    }

    /// <summary>
    /// Base size, no overrides. Returns the existing one when present. Legacy
    /// essentialProps that were never migrated (no bake since the SDK update) become
    /// this frame instead, so the creator's old essential selection is not dropped.
    /// </summary>
    internal static AttractionLayoutFrame CreateMinimumFrame(AttractionTemplate attraction)
    {
        if (attraction == null) return null;
        if (attraction.MinimumFrame != null) return attraction.MinimumFrame;
        Undo.RecordObject(attraction, "Create Minimum Frame");
        if (AttractionPackingBaker.MigrateLegacyEssentialProps(attraction))
        {
            MarkChanged(attraction);
            return attraction.MinimumFrame;
        }
        var frame = new AttractionLayoutFrame
        {
            id = AttractionTemplate.MinimumFrameId,
            name = "Minimum",
            kind = AttractionLayoutFrameKind.Minimum,
            sizeFeet = attraction.DimensionsInFeet,
        };
        Mutate(attraction, "Create Minimum Frame", frames => frames.Add(frame));
        return frame;
    }

    internal static AttractionLayoutFrame AddFrame(AttractionTemplate attraction, Vector2 sizeFeet, string name = null)
    {
        if (attraction == null) return null;
        var frame = new AttractionLayoutFrame
        {
            id = NewFrameId(attraction),
            name = string.IsNullOrEmpty(name) ? UniqueName(attraction, "Frame") : name,
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = ClampSize(sizeFeet),
        };
        Mutate(attraction, "Add Layout Frame", frames => frames.Add(frame));
        return frame;
    }

    /// <summary>A Custom copy (new id, same size and overrides) placed after the source.</summary>
    internal static AttractionLayoutFrame DuplicateFrame(AttractionTemplate attraction, string frameId)
    {
        AttractionLayoutFrame source = attraction != null ? attraction.FindFrame(frameId) : null;
        if (source == null) return null;
        string sourceName = AttractionLayoutFrameEditSession.Label(source, frameId);
        var copy = new AttractionLayoutFrame
        {
            id = NewFrameId(attraction),
            name = UniqueName(attraction, sourceName + " copy"),
            kind = AttractionLayoutFrameKind.Custom,
            sizeFeet = source.sizeFeet,
        };
        copy.overrides.AddRange(AttractionLayoutFrameEditSession.CloneOverrides(source.overrides));
        copy.overridePortals = source.overridePortals;
        copy.portals = AttractionPortal.CloneList(source.portals);
        Mutate(attraction, "Duplicate Layout Frame", frames =>
        {
            int index = frames.IndexOf(source);
            frames.Insert(index < 0 ? frames.Count : index + 1, copy);
        });
        return copy;
    }

    /// <summary>Objects that only this frame showed stay in the prefab, turned off.</summary>
    internal static bool DeleteFrame(AttractionTemplate attraction, string frameId)
    {
        AttractionLayoutFrame frame = attraction != null ? attraction.FindFrame(frameId) : null;
        if (frame == null) return false;
        Mutate(attraction, "Delete Layout Frame", frames => frames.Remove(frame));
        if (attraction.LayoutPreviewFrameId == frameId)
            attraction.SetLayoutPreview(attraction.LayoutPreviewFootprintMeters, "");
        return true;
    }

    internal static bool RenameFrame(AttractionTemplate attraction, string frameId, string name)
    {
        AttractionLayoutFrame frame = attraction != null ? attraction.FindFrame(frameId) : null;
        name = name?.Trim();
        if (frame == null || string.IsNullOrEmpty(name) || frame.name == name) return false;
        Undo.RecordObject(attraction, "Rename Layout Frame");
        frame.name = name;
        MarkChanged(attraction);
        return true;
    }

    internal static bool ResizeFrame(AttractionTemplate attraction, string frameId, Vector2 sizeFeet)
    {
        AttractionLayoutFrame frame = attraction != null ? attraction.FindFrame(frameId) : null;
        if (frame == null) return false;
        sizeFeet = ClampSize(sizeFeet);
        if (frame.sizeFeet == sizeFeet) return false;
        Undo.RecordObject(attraction, "Resize Layout Frame");
        frame.sizeFeet = sizeFeet;
        MarkChanged(attraction);
        return true;
    }

    /// <summary>The current debug-preview footprint rounded to 0.5 ft, else the Base size.</summary>
    internal static Vector2 DefaultNewFrameSizeFeet(AttractionTemplate attraction)
    {
        Vector2 meters = Vector2.zero;
        if (attraction.HasLayoutFrames)
            meters = attraction.LayoutPreviewFootprintMeters;
        else if (attraction.HasPackingBake && attraction.PackingPreviewScale != Vector2.one)
            meters = attraction.GetPackingFootprintMeters(attraction.PackingPreviewScale, false);
        if (meters.x <= 0f || meters.y <= 0f) return attraction.DimensionsInFeet;
        Vector2 feet = meters / FeetToMeters;
        return ClampSize(new Vector2(Mathf.Round(feet.x * 2f) * 0.5f, Mathf.Round(feet.y * 2f) * 0.5f));
    }

    /// <summary>
    /// Re-bake after a frame-list edit, the same way Bake Packing Variants does: a
    /// scene instance writes into its prefab asset, anything else bakes in place (in
    /// the same Undo group as the edit when called right after it).
    /// </summary>
    internal static void RefreshBake(AttractionTemplate attraction)
    {
        if (attraction == null) return;
        if (AttractionLayoutFrameEditSession.IsScenePrefabInstanceRoot(attraction, out _))
        {
            AttractionPackingBaker.BakeIntoAsset(attraction);
            AssetDatabase.SaveAssets();
        }
        else
        {
            AttractionPackingBaker.Bake(attraction, true);
        }
        SceneView.RepaintAll();
    }

    internal static string FrameIdOf(AttractionLayoutFrame frame) =>
        frame.kind == AttractionLayoutFrameKind.Minimum ? AttractionTemplate.MinimumFrameId : frame.id;

    private static void Mutate(AttractionTemplate attraction, string undoName, Action<List<AttractionLayoutFrame>> change)
    {
        Undo.RecordObject(attraction, undoName);
        var frames = new List<AttractionLayoutFrame>(attraction.LayoutFrames);
        change(frames);
        attraction.SetLayoutFrames(frames);
        MarkChanged(attraction);
    }

    private static void MarkChanged(AttractionTemplate attraction)
    {
        EditorUtility.SetDirty(attraction);
        if (PrefabUtility.IsPartOfPrefabInstance(attraction))
            PrefabUtility.RecordPrefabInstancePropertyModifications(attraction);
    }

    private static Vector2 ClampSize(Vector2 feet) => new Vector2(
        Mathf.Max(MinimumFrameSideFeet, float.IsNaN(feet.x) ? 0f : feet.x),
        Mathf.Max(MinimumFrameSideFeet, float.IsNaN(feet.y) ? 0f : feet.y));

    private static string UniqueName(AttractionTemplate attraction, string stem)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
            if (!string.IsNullOrEmpty(frames[i]?.name)) names.Add(frames[i].name);
        if (stem != "Frame" && !names.Contains(stem)) return stem;
        for (int n = stem == "Frame" ? CountCustom(attraction) + 1 : 2; ; n++)
        {
            string candidate = $"{stem} {n}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private static int CountCustom(AttractionTemplate attraction)
    {
        int count = 0;
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        for (int i = 0; i < frames.Count; i++)
            if (frames[i] != null && frames[i].kind == AttractionLayoutFrameKind.Custom) count++;
        return count;
    }
}

/// <summary>
/// The Layout Frames inspector section (spec §6): Base row, pinned Minimum slot,
/// custom frame list, warnings, coverage chart, the feet-based debug preview and the
/// sticky frame-edit panel. Drawn by AttractionTemplatePackingEditor.
/// </summary>
internal static class AttractionLayoutFramesInspector
{
    private const float Ft = AttractionLayoutFrameAuthoring.FeetToMeters;
    private const float ChartHeight = 180f;
    private static readonly Vector2 SequenceRoomFeet = new Vector2(12f, 18f);
    private static readonly Color BaseColor = new Color(0.82f, 0.82f, 0.82f, 1f);
    private static readonly Color MinimumColor = new Color(1f, 0.25f, 0.8f, 1f);
    private static readonly Color EditColor = new Color(1f, 0.55f, 0.1f, 1f);

    private static string hoveredFrameId;
    private static GUIStyle chartLabelStyle;

    // ------------------------------------------------------------------ edit panel

    /// <summary>The sticky panel shown while a frame of this attraction is being edited. True when editing.</summary>
    internal static bool DrawEditSessionPanel(AttractionTemplate attraction)
    {
        if (!AttractionLayoutFrameEditSession.IsActive) return false;
        AttractionTemplate editing = AttractionLayoutFrameEditSession.Attraction;
        string label = AttractionLayoutFrameEditSession.CurrentLabel;
        bool wallLayer = AttractionLayoutFrameEditSession.IsEditingWallLayer;
        string noun = wallLayer ? "wall layer" : "frame";
        if (editing != attraction)
        {
            EditorGUILayout.HelpBox($"'{label}' of '{editing.name}' is being edited.", MessageType.Info);
            if (GUILayout.Button("Select It")) Selection.activeGameObject = editing.gameObject;
            return false;
        }

        AttractionLayoutFrame frame = AttractionLayoutFrameEditSession.CurrentFrame;
        string frameId = AttractionLayoutFrameEditSession.FrameId;
        Color previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(1f, 0.78f, 0.45f, 1f);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUI.backgroundColor = previousBackground;

        EditorGUILayout.LabelField($"Editing {noun} '{label}'", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            wallLayer
                ? "The highlighted edge is the wall. Move, rotate, scale or turn objects on and off, or add new objects " +
                  "(they appear only when this side is on a real wall). Only what you change is stored. Keep anything " +
                  "the game needs in Base. Don't delete objects: turn them off."
                : "Move, rotate, scale or turn objects on and off, or add new objects (they appear only in this frame). " +
                  "Only what you change is stored. Don't delete objects: turn them off. Component and material edits change every frame.",
            EditorStyles.wordWrappedMiniLabel);

        if (wallLayer)
        {
            if (AttractionLayoutFrameEditSession.CurrentWallLayer == null)
                EditorGUILayout.HelpBox($"This wall layer no longer exists on this attraction. Cancel to put Base back.",
                    MessageType.Warning);
        }
        else if (frame != null)
        {
            Vector2 size = frame.sizeFeet;
            EditorGUI.BeginChangeCheck();
            size.x = EditorGUILayout.DelayedFloatField("Width (ft)", size.x);
            size.y = EditorGUILayout.DelayedFloatField("Length (ft)", size.y);
            if (EditorGUI.EndChangeCheck())
            {
                AttractionLayoutFrameAuthoring.ResizeFrame(attraction, frameId, size);
                SceneView.RepaintAll();
            }
        }
        else
        {
            // The frame was removed underneath the session (e.g. its instance
            // override was reverted). Cancel puts the hierarchy back to Base.
            EditorGUILayout.HelpBox($"Frame '{frameId}' no longer exists on this attraction. Cancel to put Base back.",
                MessageType.Warning);
        }

        if (AttractionLayoutFrameEditSession.TryGetPendingChanges(
                out int changed, out int created, out List<string> deleted, out List<string> moved))
        {
            EditorGUILayout.LabelField($"{changed} changed object(s), {created} new object(s)", EditorStyles.miniLabel);
            if (deleted.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{deleted.Count} object(s) were deleted ({Join(deleted)}). Frames can't delete objects: " +
                    "Undo (Cmd/Ctrl+Z) to bring them back, then turn them off instead.",
                    MessageType.Error);
            if (moved.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{moved.Count} object(s) moved to a different parent ({Join(moved)}). Undo the move; " +
                    "frames can only move, rotate, scale and show or hide objects under the same parent.",
                    MessageType.Error);
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(wallLayer ? "Save Wall Layer" : "Save Frame", GUILayout.Height(24f)))
            Defer(() => AttractionLayoutFrameEditSession.SaveInteractive());
        if (GUILayout.Button("Cancel", GUILayout.Height(24f)))
            Defer(() => AttractionLayoutFrameEditSession.CancelInteractive());
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
        return true;
    }

    // ------------------------------------------------------------------ section

    internal static void DrawLayoutFramesSection(AttractionTemplate attraction, Editor editor)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Layout Frames", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "A frame is a hand-authored arrangement at another width × length. The park picks one frame for " +
            "the space it has, then shrinks or grows within that frame's own range.",
            EditorStyles.wordWrappedMiniLabel);

        bool locked = Application.isPlaying || AttractionLayoutFrameEditSession.IsEditing(attraction)
                      || EditorUtility.IsPersistent(attraction);
        using (new EditorGUI.DisabledScope(locked))
        {
            DrawBaseRow(attraction);
            DrawMinimumSlot(attraction);
            DrawCustomFrames(attraction);
            if (GUILayout.Button("Add Frame"))
            {
                Vector2 size = AttractionLayoutFrameAuthoring.DefaultNewFrameSizeFeet(attraction);
                Defer(() =>
                {
                    AttractionLayoutFrameAuthoring.AddFrame(attraction, size);
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
                });
            }
        }
        if (EditorUtility.IsPersistent(attraction))
            EditorGUILayout.HelpBox("Open the prefab to edit its layout frames.", MessageType.Info);

        // Writing or reverting the frame list mid-edit would pull the frame out from
        // under the session; save or cancel the frame first.
        using (new EditorGUI.DisabledScope(locked))
            DrawInstanceNotice(attraction);
        List<string> warnings = AttractionPackingBaker.GetFrameWarnings(attraction);
        for (int i = 0; i < warnings.Count; i++)
            EditorGUILayout.HelpBox(warnings[i], MessageType.Warning);
        DrawCoverageChart(attraction, editor);
    }

    private static void DrawBaseRow(AttractionTemplate attraction)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        DrawSwatch(BaseColor);
        EditorGUILayout.LabelField("Base", EditorStyles.boldLabel, GUILayout.Width(60f));
        EditorGUILayout.LabelField(AttractionLayoutFrameEditSession.FormatFeet(attraction.DimensionsInFeet) + " (as authored)");
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(RangeSummary(attraction, AttractionTemplate.BaseFrameId), EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();
    }

    private static void DrawMinimumSlot(AttractionTemplate attraction)
    {
        AttractionLayoutFrame minimum = attraction.MinimumFrame;
        if (minimum != null)
        {
            DrawFrameRow(attraction, minimum, MinimumColor);
            return;
        }
        Vector2 baseShrink = attraction.HasPackingBake
            ? attraction.PackingBake.ShrinkFootprintMeters
            : attraction.DimensionsInFeet * Ft;
        EditorGUILayout.HelpBox(
            $"No Minimum frame — this attraction can never be smaller than {FormatMeters(baseShrink)}. " +
            "Create one: turn off everything the game can live without.",
            MessageType.Warning);
        if (GUILayout.Button("Create Minimum Frame"))
        {
            Defer(() =>
            {
                AttractionLayoutFrameAuthoring.CreateMinimumFrame(attraction);
                AttractionLayoutFrameAuthoring.RefreshBake(attraction);
                AttractionLayoutFrameEditSession.BeginInteractive(attraction, AttractionTemplate.MinimumFrameId);
            });
        }
    }

    private static void DrawCustomFrames(AttractionTemplate attraction)
    {
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        int custom = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null || frame.kind != AttractionLayoutFrameKind.Custom) continue;
            DrawFrameRow(attraction, frame, CustomColor(custom++));
        }
    }

    private static void DrawFrameRow(AttractionTemplate attraction, AttractionLayoutFrame frame, Color color)
    {
        bool minimum = frame.kind == AttractionLayoutFrameKind.Minimum;
        string frameId = AttractionLayoutFrameAuthoring.FrameIdOf(frame);
        string label = AttractionLayoutFrameEditSession.Label(frame, frameId);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        DrawSwatch(color);
        if (minimum)
        {
            EditorGUILayout.LabelField("Minimum", EditorStyles.boldLabel, GUILayout.MinWidth(60f));
        }
        else
        {
            string renamed = EditorGUILayout.DelayedTextField(frame.name ?? "", GUILayout.MinWidth(60f));
            if (renamed != (frame.name ?? ""))
            {
                Defer(() =>
                {
                    if (!AttractionLayoutFrameAuthoring.RenameFrame(attraction, frameId, renamed)) return;
                    // A scene instance keeps frames in its prefab; write the new name there.
                    if (AttractionLayoutFrameEditSession.IsScenePrefabInstanceRoot(attraction, out _))
                        AttractionLayoutFrameAuthoring.RefreshBake(attraction);
                });
            }
        }
        Vector2 size = frame.sizeFeet;
        EditorGUI.BeginChangeCheck();
        size.x = EditorGUILayout.DelayedFloatField(size.x, GUILayout.Width(42f));
        GUILayout.Label("×", GUILayout.Width(12f));
        size.y = EditorGUILayout.DelayedFloatField(size.y, GUILayout.Width(42f));
        GUILayout.Label("ft", GUILayout.Width(16f));
        if (EditorGUI.EndChangeCheck())
        {
            Defer(() =>
            {
                if (AttractionLayoutFrameAuthoring.ResizeFrame(attraction, frameId, size))
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
            });
        }
        EditorGUILayout.EndHorizontal();

        int changes = frame.overrides != null ? frame.overrides.Count : 0;
        EditorGUILayout.LabelField(
            $"{changes} override{(changes == 1 ? "" : "s")} · {RangeSummary(attraction, frameId)}",
            EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Edit", EditorStyles.miniButtonLeft, GUILayout.Width(64f)))
            Defer(() => AttractionLayoutFrameEditSession.BeginInteractive(attraction, frameId));
        if (!minimum && GUILayout.Button("Duplicate", EditorStyles.miniButtonMid, GUILayout.Width(72f)))
        {
            Defer(() =>
            {
                if (AttractionLayoutFrameAuthoring.DuplicateFrame(attraction, frameId) != null)
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
            });
        }
        if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(64f)))
        {
            Defer(() =>
            {
                if (!EditorUtility.DisplayDialog(
                        "Delete Layout Frame",
                        $"Delete frame '{label}'? Objects that only appear in this frame stay in the prefab, turned off.",
                        "Delete", "Cancel")) return;
                if (AttractionLayoutFrameAuthoring.DeleteFrame(attraction, frameId))
                    AttractionLayoutFrameAuthoring.RefreshBake(attraction);
            });
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private static void DrawInstanceNotice(AttractionTemplate attraction)
    {
        if (!AttractionLayoutFrameEditSession.HasFrameInstanceOverrides(attraction)) return;
        EditorGUILayout.HelpBox(
            "This scene instance's layout frames differ from its prefab. Frames are prefab data: write them into the prefab or revert them.",
            MessageType.Warning);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Write Frames Into Prefab"))
            Defer(() => AttractionLayoutFrameAuthoring.RefreshBake(attraction));
        if (GUILayout.Button("Revert To Prefab"))
        {
            Defer(() =>
            {
                var serialized = new SerializedObject(attraction);
                SerializedProperty frames = serialized.FindProperty("layoutFrames");
                if (frames != null && frames.prefabOverride)
                    PrefabUtility.RevertPropertyOverride(frames, InteractionMode.UserAction);
            });
        }
        EditorGUILayout.EndHorizontal();
    }

    private static string RangeSummary(AttractionTemplate attraction, string frameId)
    {
        if (!attraction.TryGetFrameEnvelope(frameId, out AttractionFrameEnvelope envelope))
            return "Not baked yet (Bake Packing Variants).";
        return $"shrinks to {FormatMeters(envelope.shrinkMeters)}, grows to {FormatMeters(envelope.growMeters)}";
    }

    // ------------------------------------------------------------------ coverage chart

    private const int SelectionMapCells = 48;
    private static Texture2D selectionMap;
    private static int selectionMapKey;

    /// <summary>
    /// The per-cell decision of the coverage map, exactly TrySelectFrame's rule: the
    /// first ladder entry whose shrink fits the size. Returns its ladder index (-1:
    /// nothing fits) and whether its footprint is smaller than the size (the size is
    /// beyond that frame's grow, so it leaves margin).
    /// </summary>
    internal static int SelectFrameForSize(List<AttractionFrameEnvelope> ladder, Vector2 sizeMeters, out bool margin)
    {
        margin = false;
        for (int i = 0; i < ladder.Count; i++)
        {
            AttractionFrameEnvelope envelope = ladder[i];
            if (envelope.shrinkMeters.x > sizeMeters.x + 0.0001f
                || envelope.shrinkMeters.y > sizeMeters.y + 0.0001f) continue;
            margin = sizeMeters.x > envelope.growMeters.x + 0.001f || sizeMeters.y > envelope.growMeters.y + 0.001f;
            return i;
        }
        return -1;
    }

    /// <summary>Releases the cached coverage map (the packing inspector calls this from OnDisable).</summary>
    internal static void DisposeCoverageCache()
    {
        if (selectionMap != null) UnityEngine.Object.DestroyImmediate(selectionMap);
        selectionMap = null;
        selectionMapKey = 0;
    }

    // What the runtime picks for every width × length in the plot, one texel per cell.
    private static Texture2D GetSelectionMap(List<AttractionFrameEnvelope> ladder, Color[] colors, float maxFeet)
    {
        int key = 17;
        unchecked
        {
            key = key * 31 + maxFeet.GetHashCode();
            for (int i = 0; i < ladder.Count; i++)
            {
                key = key * 31 + (ladder[i].frameId ?? "").GetHashCode();
                key = key * 31 + ladder[i].shrinkMeters.GetHashCode();
                key = key * 31 + ladder[i].growMeters.GetHashCode();
                key = key * 31 + colors[i].GetHashCode();
            }
            key = key * 31 + (EditorGUIUtility.isProSkin ? 1 : 0);
        }
        if (selectionMap != null && selectionMapKey == key) return selectionMap;
        if (selectionMap == null)
        {
            selectionMap = new Texture2D(SelectionMapCells, SelectionMapCells, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }
        var pixels = new Color32[SelectionMapCells * SelectionMapCells];
        float cellFeet = maxFeet / SelectionMapCells;
        for (int cy = 0; cy < SelectionMapCells; cy++)
        for (int cx = 0; cx < SelectionMapCells; cx++)
        {
            Vector2 size = new Vector2((cx + 0.5f) * cellFeet, (cy + 0.5f) * cellFeet) * Ft;
            int index = SelectFrameForSize(ladder, size, out bool margin);
            Color color = Color.clear;
            if (index >= 0)
            {
                color = colors[index];
                // Margin cells are dimmer and hatched: that frame is at its maximum.
                color.a = margin ? (((cx + cy) & 1) == 0 ? 0.2f : 0.07f) : 0.38f;
            }
            pixels[cy * SelectionMapCells + cx] = color;
        }
        selectionMap.SetPixels32(pixels);
        selectionMap.Apply(false);
        selectionMapKey = key;
        return selectionMap;
    }

    private static void DrawCoverageChart(AttractionTemplate attraction, Editor editor)
    {
        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        if (ladder.Count == 0) return;

        EditorGUILayout.Space(2f);
        EditorGUILayout.LabelField("Coverage (width × length, ft)", EditorStyles.miniBoldLabel);
        Rect rect = GUILayoutUtility.GetRect(220f, ChartHeight, GUILayout.ExpandWidth(true));
        var colors = new Color[ladder.Count];
        for (int i = 0; i < ladder.Count; i++) colors[i] = EnvelopeColor(attraction, ladder[i]);
        Event current = Event.current;
        if (current.type == EventType.Repaint || current.type == EventType.MouseMove)
            DrawCoveragePlot(attraction, editor, ladder, colors, rect, current);

        // Legend: every frame's authored size and its baked range, no hover needed.
        for (int i = 0; i < ladder.Count; i++)
        {
            AttractionFrameEnvelope envelope = ladder[i];
            EditorGUILayout.BeginHorizontal();
            DrawSwatch(colors[i]);
            EditorGUILayout.LabelField(
                $"{envelope.name}  {CompactFeet(envelope.authoredMeters)} · " +
                $"{CompactFeet(envelope.shrinkMeters)} → {CompactFeet(envelope.growMeters)} ft",
                envelope.frameId == hoveredFrameId ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.LabelField(
            "Shaded: the frame the park picks for that space. Hatched: that frame at its maximum (it leaves margin). " +
            "Empty: nothing fits. Outlines: each frame's shrink → grow range; dotted: the Size slider's line.",
            EditorStyles.wordWrappedMiniLabel);
    }

    private static void DrawCoveragePlot(AttractionTemplate attraction, Editor editor,
        List<AttractionFrameEnvelope> ladder, Color[] colors, Rect rect, Event current)
    {
        float maxFeet = Mathf.Max(SequenceRoomFeet.x, SequenceRoomFeet.y);
        for (int i = 0; i < ladder.Count; i++)
        {
            maxFeet = Mathf.Max(maxFeet, ladder[i].growMeters.x / Ft, ladder[i].growMeters.y / Ft,
                ladder[i].authoredMeters.x / Ft, ladder[i].authoredMeters.y / Ft);
        }
        float step = maxFeet > 60f ? 20f : maxFeet > 30f ? 10f : 5f;
        maxFeet = Mathf.Ceil(maxFeet * 1.04f / step) * step;

        const float leftPad = 26f;
        const float bottomPad = 16f;
        float side = Mathf.Max(60f, Mathf.Min(rect.height - bottomPad - 4f, rect.width - leftPad - 4f));
        var plot = new Rect(rect.x + leftPad, rect.y + 4f, side, side);

        Vector2 ToGui(Vector2 feet) => new Vector2(
            plot.x + Mathf.Clamp01(feet.x / maxFeet) * plot.width,
            plot.yMax - Mathf.Clamp01(feet.y / maxFeet) * plot.height);
        Rect RangeRect(AttractionFrameEnvelope envelope)
        {
            Vector2 low = ToGui(envelope.shrinkMeters / Ft);
            Vector2 high = ToGui(envelope.growMeters / Ft);
            return Rect.MinMaxRect(low.x, high.y, Mathf.Max(high.x, low.x + 2f), Mathf.Max(low.y, high.y + 2f));
        }

        // Hover: an authored dot under the mouse, else the frame the park picks there.
        string hovered = null;
        Vector2 mouse = current.mousePosition;
        if (plot.Contains(mouse))
        {
            for (int i = 0; i < ladder.Count && hovered == null; i++)
                if ((ToGui(ladder[i].authoredMeters / Ft) - mouse).sqrMagnitude <= 36f)
                    hovered = ladder[i].frameId;
            if (hovered == null)
            {
                Vector2 feet = new Vector2((mouse.x - plot.x) / plot.width, (plot.yMax - mouse.y) / plot.height) * maxFeet;
                int index = SelectFrameForSize(ladder, feet * Ft, out _);
                if (index >= 0) hovered = ladder[index].frameId;
            }
        }
        if (current.type == EventType.MouseMove)
        {
            if (hovered != hoveredFrameId)
            {
                hoveredFrameId = hovered;
                editor?.Repaint();
            }
            return;
        }
        hoveredFrameId = hovered;

        if (chartLabelStyle == null)
            chartLabelStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Overflow };
        Color previousHandles = Handles.color;
        Color previousGui = GUI.color;
        bool dark = EditorGUIUtility.isProSkin;
        EditorGUI.DrawRect(plot, dark ? new Color(0.13f, 0.13f, 0.13f, 1f) : new Color(0.93f, 0.93f, 0.93f, 1f));

        // What the runtime picks for every size (TrySelectFrame's rule, cached).
        GUI.DrawTexture(plot, GetSelectionMap(ladder, colors, maxFeet), ScaleMode.StretchToFill, true);

        // Grid, ticks.
        Color grid = dark ? new Color(1f, 1f, 1f, 0.07f) : new Color(0f, 0f, 0f, 0.08f);
        Color axisText = dark ? new Color(1f, 1f, 1f, 0.55f) : new Color(0f, 0f, 0f, 0.55f);
        for (float feet = 0f; feet <= maxFeet + 0.01f; feet += step)
        {
            Vector2 x = ToGui(new Vector2(feet, 0f));
            Vector2 y = ToGui(new Vector2(0f, feet));
            Handles.color = grid;
            Handles.DrawLine(new Vector3(x.x, plot.y), new Vector3(x.x, plot.yMax));
            Handles.DrawLine(new Vector3(plot.x, y.y), new Vector3(plot.xMax, y.y));
            GUI.color = axisText;
            GUI.Label(new Rect(x.x - 12f, plot.yMax + 1f, 24f, 14f), feet.ToString("0"), CenteredMini());
            GUI.Label(new Rect(rect.x, y.y - 7f, leftPad - 3f, 14f), feet.ToString("0"), RightMini());
        }
        GUI.color = previousGui;

        // The Sequence room, both orientations.
        Handles.color = dark ? new Color(1f, 1f, 1f, 0.5f) : new Color(0f, 0f, 0f, 0.5f);
        DrawDashedBox(ToGui(Vector2.zero), ToGui(SequenceRoomFeet));
        DrawDashedBox(ToGui(Vector2.zero), ToGui(new Vector2(SequenceRoomFeet.y, SequenceRoomFeet.x)));
        GUI.color = axisText;
        Vector2 sequenceCorner = ToGui(SequenceRoomFeet);
        GUI.Label(new Rect(sequenceCorner.x + 2f, sequenceCorner.y - 1f, 90f, 14f), "Sequence 12×18", chartLabelStyle);
        GUI.color = previousGui;

        // Each frame's shrink → grow range as an outline.
        for (int i = 0; i < ladder.Count; i++)
        {
            bool hot = ladder[i].frameId == hovered;
            Color color = colors[i];
            Handles.DrawSolidRectangleWithOutline(RangeRect(ladder[i]),
                hot ? new Color(color.r, color.g, color.b, 0.12f) : Color.clear,
                new Color(color.r, color.g, color.b, hot ? 1f : 0.7f));
        }

        // The Size slider's line: smallest shrink → largest grow.
        AttractionPackingBaker.GetSizeLine(ladder, out Vector2 lineStart, out Vector2 lineEnd);
        Handles.color = dark ? new Color(1f, 1f, 1f, 0.6f) : new Color(0f, 0f, 0f, 0.6f);
        Vector2 lineFrom = ToGui(lineStart / Ft);
        Vector2 lineTo = ToGui(lineEnd / Ft);
        Handles.DrawDottedLine(new Vector3(lineFrom.x, lineFrom.y), new Vector3(lineTo.x, lineTo.y), 2f);

        for (int i = 0; i < ladder.Count; i++)
        {
            Color color = colors[i];
            Vector2 dot = ToGui(ladder[i].authoredMeters / Ft);
            Handles.color = color;
            Handles.DrawSolidDisc(new Vector3(dot.x, dot.y, 0f), Vector3.forward,
                ladder[i].frameId == hovered ? 4.5f : 3f);
            GUI.color = color;
            GUI.Label(new Rect(dot.x + 5f, dot.y - 14f, 110f, 14f), ladder[i].name, chartLabelStyle);
        }
        GUI.color = previousGui;

        // Where the debug preview currently points.
        Vector2 preview = attraction.LayoutPreviewFootprintMeters;
        if (attraction.HasLayoutFrames && preview.x > 0f && preview.y > 0f)
        {
            Vector2 cross = ToGui(preview / Ft);
            Handles.color = EditColor;
            Handles.DrawLine(new Vector3(cross.x - 5f, cross.y), new Vector3(cross.x + 5f, cross.y));
            Handles.DrawLine(new Vector3(cross.x, cross.y - 5f), new Vector3(cross.x, cross.y + 5f));
        }
        Handles.color = previousHandles;
    }

    private static string CompactFeet(Vector2 meters) =>
        $"{meters.x / Ft:0.#}×{meters.y / Ft:0.#}";

    private static void DrawDashedBox(Vector2 corner, Vector2 opposite)
    {
        var a = new Vector3(corner.x, corner.y);
        var b = new Vector3(opposite.x, corner.y);
        var c = new Vector3(opposite.x, opposite.y);
        var d = new Vector3(corner.x, opposite.y);
        Handles.DrawDottedLine(a, b, 3f);
        Handles.DrawDottedLine(b, c, 3f);
        Handles.DrawDottedLine(c, d, 3f);
        Handles.DrawDottedLine(d, a, 3f);
    }

    // ------------------------------------------------------------------ debug preview

    /// <summary>
    /// Feet-based debug preview for attractions with frames. The Size slider walks the
    /// Size line (smallest frame's shrink → largest grow) with width and length
    /// together; the width/length sliders follow it and can still be moved alone.
    /// </summary>
    internal static void DrawFramesDebugPreview(AttractionTemplate attraction)
    {
        List<AttractionFrameEnvelope> ladder = attraction.GetFrameEnvelopes();
        if (ladder.Count == 0) return;

        Vector2 minimum = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 maximum = Vector2.zero;
        Vector2 baseAuthored = ladder[0].authoredMeters;
        for (int i = 0; i < ladder.Count; i++)
        {
            minimum = Vector2.Min(minimum, ladder[i].shrinkMeters);
            maximum = Vector2.Max(maximum, ladder[i].growMeters);
            if (ladder[i].frameId == AttractionTemplate.BaseFrameId) baseAuthored = ladder[i].authoredMeters;
        }
        Vector2 minimumFeet = minimum / Ft;
        Vector2 maximumFeet = Vector2.Max(maximum / Ft, minimumFeet + new Vector2(0.01f, 0.01f));
        AttractionPackingBaker.GetSizeLine(ladder, out Vector2 lineStart, out Vector2 lineEnd);

        Vector2 stored = attraction.LayoutPreviewFootprintMeters;
        bool hasTarget = stored.x > 0f && stored.y > 0f;
        // Frame-less attractions keep their preview in the legacy Base multiplier.
        if (!hasTarget && !attraction.HasLayoutFrames)
            stored = Vector2.Scale(baseAuthored, attraction.PackingPreviewScale);
        Vector2 target = (hasTarget || !attraction.HasLayoutFrames ? stored : baseAuthored) / Ft;
        target = new Vector2(
            Mathf.Clamp(target.x, minimumFeet.x, maximumFeet.x),
            Mathf.Clamp(target.y, minimumFeet.y, maximumFeet.y));

        var options = new string[ladder.Count + 1];
        options[0] = "Auto (largest frame that fits)";
        int selected = 0;
        for (int i = 0; i < ladder.Count; i++)
        {
            options[i + 1] = $"{ladder[i].name} ({FormatMeters(ladder[i].authoredMeters)})";
            if (ladder[i].frameId == attraction.LayoutPreviewFrameId) selected = i + 1;
        }

        float size = SizeLineProgress(target * Ft, lineStart, lineEnd) * 100f;
        EditorGUI.BeginChangeCheck();
        if (ladder.Count > 1)
            selected = EditorGUILayout.Popup(new GUIContent("Debug Frame", "Auto picks the frame the park would pick for this space."), selected, options);
        float newSize = EditorGUILayout.Slider(new GUIContent("Size (%)",
                "Width and length together, from the smallest size any frame packs to (0%) to the largest grow (100%)."),
            size, 0f, 100f);
        if (!Mathf.Approximately(newSize, size))
            target = Vector2.Lerp(lineStart, lineEnd, newSize / 100f) / Ft;
        target.x = EditorGUILayout.Slider(new GUIContent("Debug Width (ft)", "Width of the space the attraction is given (local X)."),
            target.x, minimumFeet.x, maximumFeet.x);
        target.y = EditorGUILayout.Slider(new GUIContent("Debug Length (ft)", "Length of the space the attraction is given (local Z)."),
            target.y, minimumFeet.y, maximumFeet.y);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(attraction, "Preview attraction layout");
            attraction.SetLayoutPreview(target * Ft, selected == 0 ? "" : ladder[selected - 1].frameId);
            if (!attraction.HasLayoutFrames)
            {
                // Same sliders, legacy runtime path: drive the Base multiplier the
                // frame-less preview (Awake/LateUpdate, gizmos, ghosts) reads.
                attraction.TryResolveLayoutPreview(out _, out Vector2 baseScale);
                attraction.SetPackingPreview(baseScale, false);
            }
            EditorUtility.SetDirty(attraction);
            SceneView.RepaintAll();
        }

        bool fits = attraction.TryResolveLayoutPreview(out string frameId, out Vector2 frameScale);
        Vector2 footprint = attraction.GetFrameFootprintMeters(frameId, frameScale);
        string frameName = frameId;
        for (int i = 0; i < ladder.Count; i++)
            if (ladder[i].frameId == frameId) frameName = ladder[i].name;
        Vector2 space = target * Ft;
        Vector2 margin = Vector2.Max(Vector2.zero, space - footprint) / Ft;
        string note = !fits ? " — nothing fits; smallest frame at its minimum"
            : margin.x > 0.01f || margin.y > 0.01f ? $" (+margin {margin.x:0.##} × {margin.y:0.##} ft)" : "";
        EditorGUILayout.LabelField($"Chosen: {frameName} at {FormatMeters(footprint)}{note}", EditorStyles.boldLabel);
    }

    // Where a space sits on the Size line, 0..1 (the average of the two axes' progress).
    private static float SizeLineProgress(Vector2 spaceMeters, Vector2 start, Vector2 end)
    {
        float total = 0f;
        int axes = 0;
        for (int axis = 0; axis < 2; axis++)
        {
            float span = end[axis] - start[axis];
            if (span <= 0.00001f) continue;
            total += (spaceMeters[axis] - start[axis]) / span;
            axes++;
        }
        return axes == 0 ? 1f : Mathf.Clamp01(total / axes);
    }

    // ------------------------------------------------------------------ helpers

    internal static Color EnvelopeColor(AttractionTemplate attraction, AttractionFrameEnvelope envelope)
    {
        if (envelope.kind == "base") return BaseColor;
        if (envelope.kind == "minimum") return MinimumColor;
        IReadOnlyList<AttractionLayoutFrame> frames = attraction.LayoutFrames;
        int custom = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            AttractionLayoutFrame frame = frames[i];
            if (frame == null || frame.kind != AttractionLayoutFrameKind.Custom) continue;
            if (frame.id == envelope.frameId) return CustomColor(custom);
            custom++;
        }
        return CustomColor(custom);
    }

    private static Color CustomColor(int index) =>
        Color.HSVToRGB(Mathf.Repeat(0.52f + index * 0.17f, 1f), 0.6f, 0.95f);

    private static void DrawSwatch(Color color)
    {
        Rect swatch = GUILayoutUtility.GetRect(10f, 10f, GUILayout.Width(10f), GUILayout.Height(16f));
        EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + 4f, 8f, 8f), color);
    }

    internal static string FormatMeters(Vector2 meters) =>
        AttractionLayoutFrameEditSession.FormatFeet(meters / Ft);

    private static GUIStyle centeredMini;
    private static GUIStyle rightMini;
    private static GUIStyle CenteredMini() => centeredMini ?? (centeredMini =
        new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter, clipping = TextClipping.Overflow });
    private static GUIStyle RightMini() => rightMini ?? (rightMini =
        new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow });

    private static string Join(List<string> paths)
    {
        const int shown = 3;
        string text = "";
        for (int i = 0; i < paths.Count && i < shown; i++)
            text += (i > 0 ? ", " : "") + "'" + paths[i] + "'";
        if (paths.Count > shown) text += $" and {paths.Count - shown} more";
        return text;
    }

    /// <summary>Run structural changes after the current GUI pass (the list may change size).</summary>
    private static void Defer(Action action)
    {
        EditorApplication.delayCall += () => action();
    }
}

/// <summary>
/// Where every object of an attraction would be in one frame at one frame scale,
/// computed from serialized data without touching the hierarchy (Edit Mode ghosts).
/// </summary>
internal sealed class AttractionLayoutFrameGhostState
{
    private readonly Transform root;
    private readonly Dictionary<Transform, AttractionLayoutOverride> overrides =
        new Dictionary<Transform, AttractionLayoutOverride>();
    private readonly Dictionary<Transform, Vector3> propPositions = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, bool> propActive = new Dictionary<Transform, bool>();
    private readonly Dictionary<Transform, Matrix4x4> worldCache = new Dictionary<Transform, Matrix4x4>();

    public AttractionPackingBake Bake { get; }
    public string FrameId { get; }
    public Vector2 FrameScale { get; }
    public bool IsBase => FrameId == AttractionTemplate.BaseFrameId;

    private AttractionLayoutFrameGhostState(Transform root, AttractionLayoutFrame frame,
        AttractionPackingBake bake, string frameId, Vector2 frameScale)
    {
        this.root = root;
        Bake = bake;
        FrameId = frameId;
        FrameScale = frameScale;

        if (frame?.overrides != null)
        {
            for (int i = 0; i < frame.overrides.Count; i++)
            {
                AttractionLayoutOverride change = frame.overrides[i];
                Transform target = change?.Resolve(root);
                if (target == null) continue;
                if (!overrides.TryGetValue(target, out AttractionLayoutOverride merged))
                {
                    overrides[target] = AttractionLayoutFrameEditSession.CloneOverride(change);
                    continue;
                }
                if (change.setActive) { merged.setActive = true; merged.active = change.active; }
                if (change.setPosition) { merged.setPosition = true; merged.localPosition = change.localPosition; }
                if (change.setRotation) { merged.setRotation = true; merged.localRotation = change.localRotation; }
                if (change.setScale) { merged.setScale = true; merged.localScale = change.localScale; }
            }
        }

        Vector2 scale = new Vector2(
            Mathf.Clamp(frameScale.x, bake.ShrinkScale.x, bake.GrowScale.x),
            Mathf.Clamp(frameScale.y, bake.ShrinkScale.y, bake.GrowScale.y));
        IReadOnlyList<AttractionPropPackingPose> poses = bake.Props;
        for (int i = 0; i < poses.Count; i++)
        {
            AttractionPropPackingPose pose = poses[i];
            if (pose?.Prop == null) continue;
            // Same per-pose math as AttractionTemplate.ApplyLayout.
            propPositions[pose.Prop] = new Vector3(
                ResolveAxis(pose.AuthoredLocalPosition.x, pose.ShrunkLocalPosition.x, pose.GrownLocalPosition.x,
                    scale.x, bake.ShrinkScale.x, bake.GrowScale.x),
                pose.AuthoredLocalPosition.y,
                ResolveAxis(pose.AuthoredLocalPosition.z, pose.ShrunkLocalPosition.z, pose.GrownLocalPosition.z,
                    scale.y, bake.ShrinkScale.y, bake.GrowScale.y));
            propActive[pose.Prop] = pose.AuthoredActive;
        }
    }

    /// <summary>The frame + scale the attraction's editor preview resolves to.</summary>
    internal static bool TryCreate(AttractionTemplate attraction, out AttractionLayoutFrameGhostState ghost)
    {
        ghost = null;
        if (attraction == null) return false;
        attraction.TryResolveLayoutPreview(out string frameId, out Vector2 frameScale);
        AttractionLayoutFrame frame = attraction.FindFrame(frameId);
        AttractionPackingBake bake;
        if (frame != null && attraction.FrameHasValidBake(frame))
        {
            bake = frame.Bake;
        }
        else
        {
            frame = null;
            frameId = AttractionTemplate.BaseFrameId;
            bake = attraction.HasPackingBake ? attraction.PackingBake : null;
        }
        if (bake == null) return false;
        ghost = new AttractionLayoutFrameGhostState(attraction.transform, frame, bake, frameId, frameScale);
        return true;
    }

    /// <summary>Active in this frame, relative to the attraction root.</summary>
    internal bool IsActive(Transform target)
    {
        for (Transform current = target; current != null && current != root; current = current.parent)
        {
            bool active = current.gameObject.activeSelf;
            if (overrides.TryGetValue(current, out AttractionLayoutOverride change) && change.setActive)
                active = change.active;
            if (propActive.TryGetValue(current, out bool poseActive)) active = poseActive;
            if (!active) return false;
        }
        return true;
    }

    /// <summary>World matrix of target with this frame's poses applied.</summary>
    internal Matrix4x4 WorldMatrix(Transform target)
    {
        if (target == null) return Matrix4x4.identity;
        if (target == root || !target.IsChildOf(root)) return target.localToWorldMatrix;
        if (worldCache.TryGetValue(target, out Matrix4x4 cached)) return cached;
        Vector3 position = target.localPosition;
        Quaternion rotation = target.localRotation;
        Vector3 scale = target.localScale;
        if (overrides.TryGetValue(target, out AttractionLayoutOverride change))
        {
            if (change.setPosition) position = change.localPosition;
            if (change.setRotation) rotation = change.localRotation;
            if (change.setScale) scale = change.localScale;
        }
        if (propPositions.TryGetValue(target, out Vector3 packed)) position = packed;
        Matrix4x4 world = WorldMatrix(target.parent) * Matrix4x4.TRS(position, rotation, scale);
        worldCache[target] = world;
        return world;
    }

    private static float ResolveAxis(float authored, float shrunk, float grown,
        float scale, float minimumScale, float maximumScale)
    {
        if (scale < 1f && minimumScale < 1f)
            return Mathf.Lerp(authored, shrunk, Mathf.InverseLerp(1f, minimumScale, scale));
        if (scale > 1f && maximumScale > 1f)
            return Mathf.Lerp(authored, grown, Mathf.InverseLerp(1f, maximumScale, scale));
        return authored;
    }
}
