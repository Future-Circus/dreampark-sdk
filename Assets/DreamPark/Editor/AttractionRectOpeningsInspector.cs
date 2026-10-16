using System.Collections.Generic;
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// "Rect Openings Preview" (RECT_EDGES_SPEC.md): authored test data so an
/// attraction that reads its rect's edges (dp.rect_openings, dp.rect_spans,
/// on_rect_changed, AttractionTemplate.RectOpenings) can be built and tested in
/// the Editor and the Park Simulator without a device. Never used on device —
/// there core's data wins, and with no data every side is all unknown.
/// Author each side's `spans` (wall / opening / unknown, tiling the side).
/// </summary>
internal static class AttractionRectOpeningsInspector
{
    private static readonly Color OpeningColor = new Color(0.3f, 0.95f, 0.45f, 1f);
    private static readonly Color WallColor = new Color(0.55f, 0.55f, 0.6f, 1f);
    private static readonly Color UnknownColor = new Color(0.95f, 0.85f, 0.3f, 1f);
    private static bool foldout;

    internal static void DrawSection(AttractionTemplate attraction, SerializedObject so)
    {
        EditorGUILayout.Space();
        foldout = EditorGUILayout.Foldout(foldout, "Rect Openings Preview", true, EditorStyles.foldoutHeader);
        if (!foldout) return;
        EditorGUILayout.LabelField(
            "The rect this attraction is seated on, side by side, as spans: wall (a real wall), opening (a RoomPlan " +
            "door / opening) or unknown (neither — passable; the default). `touches` names a neighbouring rect. " +
            "On device the park supplies it; here you can author a test rect. Sides are this attraction's " +
            "Front (+Z) / Back (−Z) / Right (+X) / Left (−X); a and b are metres from the side's LEFT corner " +
            "seen from inside looking out. Without data every side is all unknown — the attraction must work that way.",
            EditorStyles.wordWrappedMiniLabel);

        so.Update();
        SerializedProperty use = so.FindProperty("useRectOpeningsPreview");
        SerializedProperty data = so.FindProperty("rectOpeningsPreview");
        if (use != null) EditorGUILayout.PropertyField(use, new GUIContent("Use Preview Data"));
        if (data != null && use != null && use.boolValue) EditorGUILayout.PropertyField(data, new GUIContent("Preview Rect"), true);
        bool changed = so.ApplyModifiedProperties();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reset: all unknown (footprint)"))
                Apply(attraction, AttractionRectOpenings.AllUnknown(Footprint(attraction)));
            if (GUILayout.Button("Sample: walls + front door"))
                Apply(attraction, Sample(Footprint(attraction)));
        }
        if (changed && Application.isPlaying)
            attraction.SetRectOpeningsPreview(attraction.RectOpeningsPreview, attraction.UseRectOpeningsPreview);

        EditorGUILayout.LabelField(Application.isPlaying ? "Live:" : "Reads as:", attraction.RectOpenings.ToString(),
            EditorStyles.wordWrappedMiniLabel);
    }

    private static Vector2 Footprint(AttractionTemplate attraction) => Application.isPlaying
        ? attraction.RuntimeFootprintMeters
        : attraction.DimensionsInFeet * AttractionLayoutFrameAuthoring.FeetToMeters;

    /// A rect 1 m bigger than the footprint each way: walls on three sides (2.4 m),
    /// a 0.9 m door in the middle of the front, and a left side that is unknown
    /// where a neighbouring rect touches it.
    internal static AttractionRectOpenings Sample(Vector2 footprint)
    {
        var size = new Vector2(Mathf.Max(1f, footprint.x + 1f), Mathf.Max(1f, footprint.y + 1f));
        var o = new AttractionRectOpenings { rectId = "preview_rect", rectSize = size, hasData = true };
        foreach (AttractionWallSide side in AttractionRectOpenings.AllSides)
        {
            float len = AttractionRectOpenings.LengthFor(side, size);
            var s = new AttractionRectSide { side = side, length = len };
            if (side == AttractionWallSide.Front)
            {
                float a = Mathf.Max(0f, len * 0.5f - 0.45f), b = Mathf.Min(len, len * 0.5f + 0.45f);
                s.spans.Add(new AttractionRectSpan(0f, a, AttractionRectSpan.Wall, height: 2.4f));
                s.spans.Add(new AttractionRectSpan(a, b, AttractionRectSpan.Opening, AttractionRectOpening.Door, 2.0f, touches: "preview_hall"));
                s.spans.Add(new AttractionRectSpan(b, len, AttractionRectSpan.Wall, height: 2.4f));
            }
            else if (side == AttractionWallSide.Left)
                s.spans.Add(new AttractionRectSpan(0f, len, AttractionRectSpan.Unknown, touches: "preview_neighbour"));
            else
                s.spans.Add(new AttractionRectSpan(0f, len, AttractionRectSpan.Wall, height: 2.4f));
            o.sides.Add(s);
        }
        return o;
    }

    private static void Apply(AttractionTemplate attraction, AttractionRectOpenings value)
    {
        Undo.RecordObject(attraction, "Rect Openings Preview");
        attraction.SetRectOpeningsPreview(value, true);
        EditorUtility.SetDirty(attraction);
    }

    /// Scene view: the preview/live rect's spans — walls grey, openings green, unknown yellow.
    internal static void DrawSceneGizmo(AttractionTemplate attraction)
    {
        if (attraction == null || !foldout) return;
        AttractionRectOpenings data = attraction.RectOpenings;
        if (data == null || !data.hasData) return;
        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        Handles.matrix = attraction.transform.localToWorldMatrix;
        foreach (AttractionWallSide side in AttractionRectOpenings.AllSides)
        {
            IReadOnlyList<AttractionRectSpan> list = data.Spans(side);
            for (int i = 0; i < list.Count; i++)
            {
                AttractionRectSpan sp = list[i];
                Handles.color = sp.IsWall ? WallColor : sp.IsOpening ? OpeningColor : UnknownColor;
                Vector3 a = data.LocalPoint(side, sp.a) + Vector3.up * 0.01f;
                Vector3 b = data.LocalPoint(side, sp.b) + Vector3.up * 0.01f;
                Handles.DrawLine(a, b, sp.IsUnknown ? 2f : 6f);
                if (!sp.IsWall || sp.Touches) Handles.Label((a + b) * 0.5f + Vector3.up * 0.1f, sp.ToString());
            }
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
    }
}
