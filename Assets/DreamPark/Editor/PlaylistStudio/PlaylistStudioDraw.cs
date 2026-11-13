#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DreamPark.SpaceMap;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>The Studio's pictures: the arc (like the app's mini arc), fit thumbnails and the campaign map.</summary>
    public static class PlaylistStudioDraw
    {
        public static readonly Color Gold = new Color(1f, 0.78f, 0.25f);
        public static readonly Color Violet = new Color(0.62f, 0.5f, 1f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.35f);
        public static readonly Color FitsColor = new Color(0.35f, 0.8f, 0.45f);
        public static readonly Color StreamsColor = new Color(0.95f, 0.7f, 0.25f);
        public static readonly Color NoFitColor = new Color(0.9f, 0.35f, 0.35f);
        public static readonly Color SkipColor = new Color(0.5f, 0.5f, 0.55f);

        static readonly Dictionary<string, Color> BeatColors = new Dictionary<string, Color>
        {
            { "opening", new Color(0.45f, 0.75f, 1f) }, { "rising", Violet }, { "climax", Gold }, { "finale", new Color(0.55f, 0.9f, 0.7f) },
        };
        public static Color BeatColor(string beat) => beat != null && BeatColors.TryGetValue(beat, out var c) ? c : Color.gray;

        public static Color VerdictColor(string v) => v == PlaylistFitCheck.Fits ? FitsColor : v == PlaylistFitCheck.Streams ? StreamsColor
            : v == PlaylistFitCheck.NoFit ? NoFitColor : SkipColor;

        static GUIStyle _mini, _miniCenter, _beat;
        static GUIStyle Mini => _mini ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = false, clipping = TextClipping.Clip };
        static GUIStyle MiniCenter => _miniCenter ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
        static GUIStyle BeatLabel => _beat ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };

        /// The arc: a line with a dot per stop, leg spans coloured by beat, the climax boss as a big gold node,
        /// a wand (✦) between legs that swap in place, hollow rings for bookends.
        public static void Arc(Rect r, PackagePlaylist p, bool labels = true)
        {
            if (Event.current.type != EventType.Repaint || p == null) return;
            var nodes = PlaylistArcModel.Build(p, out var legs);
            if (nodes.Count == 0) { GUI.Label(r, "  (no stops yet: drag attractions from the library)", EditorStyles.miniLabel); return; }
            float pad = 26f, y = r.y + (labels ? 22f : r.height * 0.5f);
            float X(float t) => Mathf.Lerp(r.x + pad, r.xMax - pad, t);
            // One band per run of legs with the same beat (four rising one-stop legs read as one RISING band).
            for (int i = 0; i < legs.Count; )
            {
                int j = i;
                while (j + 1 < legs.Count && legs[j + 1].beat == legs[i].beat) j++;
                Color c = BeatColor(legs[i].beat);
                var span = new Rect(X(legs[i].x0) - 9f, r.y + 2f, X(legs[j].x1) - X(legs[i].x0) + 18f, labels ? 13f : 4f);
                EditorGUI.DrawRect(span, new Color(c.r, c.g, c.b, 0.18f));
                string text = legs[i].beat?.ToUpperInvariant() ?? "";
                if (labels && span.width >= BeatLabel.CalcSize(new GUIContent(text)).x + 4f) GUI.Label(span, text, BeatLabel);
                else if (labels && text.Length > 0) GUI.Label(span, text.Substring(0, 1), BeatLabel);
                i = j + 1;
            }
            EditorGUI.DrawRect(new Rect(X(0f), y - 1f, X(1f) - X(0f), 2f), Line);
            Handles.BeginGUI();
            foreach (var n in nodes)
            {
                var c = new Vector3(X(n.x), y, 0f);
                if (n.wandBefore)
                {
                    // The wand sits ON the line between the two legs (swap in place), so it never covers a label.
                    float prev = nodes[Mathf.Max(0, nodes.IndexOf(n) - 1)].x;
                    float mx = (X(prev) + X(n.x)) * 0.5f;
                    if (labels) GUI.Label(new Rect(mx - 6f, y - 8f, 14f, 14f), "✦", EditorStyles.miniBoldLabel);
                    else { Handles.color = Color.white; Handles.DrawSolidDisc(new Vector3(mx, y, 0f), Vector3.forward, 1.5f); }
                }
                if (n.gold)
                {
                    Handles.color = new Color(Gold.r, Gold.g, Gold.b, 0.35f); Handles.DrawSolidDisc(c, Vector3.forward, 11f);
                    Handles.color = Gold; Handles.DrawSolidDisc(c, Vector3.forward, 7.5f);
                }
                else if (n.bookend)
                {
                    Handles.color = Color.white; Handles.DrawWireDisc(c, Vector3.forward, 5f, 2f);
                }
                else
                {
                    Handles.color = n.legStart ? BeatColor(n.beat) : Color.white;
                    Handles.DrawSolidDisc(c, Vector3.forward, n.legStart ? 5.5f : 4f);
                }
                if (labels)
                    GUI.Label(new Rect(c.x - 44f, y + 10f, 88f, 30f), (n.gold ? "★ " : "") + n.label, MiniCenter);
            }
            Handles.EndGUI();
        }

        /// A top-down thumbnail of one fit row: the space's rects, then each placed stop (gold = climax, dashed
        /// look = streamed), numbered in play order.
        public static void Thumbnail(Rect r, PlaylistFitRow row)
        {
            if (Event.current.type != EventType.Repaint || row?.space?.map == null) return;
            EditorGUI.DrawRect(r, new Color(0.11f, 0.11f, 0.13f));
            var rects = row.space.map.rooms.SelectMany(room => room.rects).ToList();
            if (rects.Count == 0) return;
            float minX = rects.Min(x => x.x), minZ = rects.Min(x => x.z), maxX = rects.Max(x => x.x + x.w), maxZ = rects.Max(x => x.z + x.d);
            float s = Mathf.Min((r.width - 8f) / Mathf.Max(0.1f, maxX - minX), (r.height - 8f) / Mathf.Max(0.1f, maxZ - minZ));
            Vector2 o = new Vector2(r.x + (r.width - (maxX - minX) * s) * 0.5f, r.y + (r.height - (maxZ - minZ) * s) * 0.5f);
            Rect ToGui(float x, float z, float w, float d) => new Rect(o.x + (x - minX) * s, o.y + (maxZ - (z + d)) * s, w * s, d * s);
            foreach (var room in row.space.map.rooms)
                foreach (var rect in room.rects)
                {
                    var g = ToGui(rect.x, rect.z, rect.w, rect.d);
                    bool outdoor = room.groundMode == "terrain";
                    EditorGUI.DrawRect(g, outdoor ? new Color(0.16f, 0.3f, 0.18f) : new Color(0.24f, 0.24f, 0.28f));
                    DrawOutline(g, new Color(1f, 1f, 1f, 0.25f));
                }
            if (row.plan == null) return;
            foreach (PlaylistPlacedStop st in row.plan.stops.Where(x => x.placedOk))
            {
                var g = ToGui(st.center.x - st.size.x * 0.5f, st.center.y - st.size.y * 0.5f, st.size.x, st.size.y);
                bool climax = st.order == row.plan.climaxOrder;
                Color c = climax ? Gold : st.Bookend ? Color.white : BeatColor(st.beat);
                EditorGUI.DrawRect(g, new Color(c.r, c.g, c.b, st.streamed ? 0.18f : 0.42f));
                DrawOutline(g, c);
                GUI.Label(new Rect(g.x + 1f, g.y, 24f, 14f), (st.order + 1).ToString(), Mini);
            }
        }

        public static void DrawOutline(Rect g, Color c)
        {
            EditorGUI.DrawRect(new Rect(g.x, g.y, g.width, 1f), c);
            EditorGUI.DrawRect(new Rect(g.x, g.yMax - 1f, g.width, 1f), c);
            EditorGUI.DrawRect(new Rect(g.x, g.y, 1f, g.height), c);
            EditorGUI.DrawRect(new Rect(g.xMax - 1f, g.y, 1f, g.height), c);
        }

        public static void Chip(Rect r, string text, Color c)
        {
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, new Color(c.r, c.g, c.b, 0.28f));
            GUI.Label(r, text, MiniCenterOneLine);
        }
        static GUIStyle _mc1;
        static GUIStyle MiniCenterOneLine => _mc1 ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };

        public static float ChipWidth(string text) => Mathf.Max(28f, EditorStyles.miniLabel.CalcSize(new GUIContent(text)).x + 10f);

        /// Preview art by asset path (or absolute file path), cached.
        static readonly Dictionary<string, Texture2D> Previews = new Dictionary<string, Texture2D>();
        public static Texture2D Preview(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Previews.TryGetValue(path, out var t) && t != null) return t;
            t = path.StartsWith("Assets/") ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
            if (t == null && File.Exists(path)) { t = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave }; t.LoadImage(File.ReadAllBytes(path)); }
            Previews[path] = t;
            return t;
        }
    }
}
#endif
