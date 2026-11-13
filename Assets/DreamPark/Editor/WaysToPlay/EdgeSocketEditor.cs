// ─────────────────────────────────────────────────────────────────────
//  EdgeSocketEditor.cs — inspector + authoring helpers for Edge Sockets.
//  "Add Edge Sockets" puts one socket per side on a level's footprint edges
//  with placeholder Closed (wall) and Open (wall with a centred 4 ft doorway)
//  looks the author then replaces with their own art.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.WaysToPlay
{
    [CustomEditor(typeof(EdgeSocket))]
    public sealed class EdgeSocketEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var s = (EdgeSocket)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("State", s.State + (string.IsNullOrEmpty(s.Look) ? "" : " (" + s.Look + ")"));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Closed")) Preview(s, EdgeLookState.Closed, null);
            if (GUILayout.Button("Open")) Preview(s, EdgeLookState.Open, null);
            foreach (var n in s.namedOpenLooks)
                if (n != null && n.look != null && GUILayout.Button(n.name)) Preview(s, EdgeLookState.Open, n.name);
            if (GUILayout.Button("Locked")) Preview(s, EdgeLookState.Locked, null);
            EditorGUILayout.EndHorizontal();
            if (s.closedLook == null) EditorGUILayout.HelpBox("No Closed look: standing alone this side shows a gap. Levels must play fully closed.", MessageType.Warning);
            if (!s.HasOpenLook) EditorGUILayout.HelpBox("No Open look: a neighbouring tile joined here has no doorway (pre-upload blocks this).", MessageType.Error);
            if (s.closedLook == null || !s.HasOpenLook)
                if (GUILayout.Button("Create placeholder Closed / Open looks")) EdgeSocketAuthoring.CreatePlaceholderLooks(s, EdgeSocketAuthoring.EdgeLengthFeet(s));
            EditorGUILayout.HelpBox("Before saving the prefab, preview Closed: that is how the level spawns.", MessageType.None);
        }

        static void Preview(EdgeSocket s, EdgeLookState st, string look)
        {
            Undo.RecordObject(s, "Preview edge");
            if (s.closedLook) Undo.RecordObject(s.closedLook, "Preview edge");
            if (s.openLook) Undo.RecordObject(s.openLook, "Preview edge");
            s.SetState(st, look);
            EditorUtility.SetDirty(s);
        }
    }

    public static class EdgeSocketAuthoring
    {
        const string Menu = "GameObject/DreamPark/Add Edge Sockets (4 sides)";

        [MenuItem(Menu, false, 30)]
        static void AddToSelection()
        {
            foreach (var go in Selection.gameObjects) AddSockets(go, FootprintFeet(go));
        }

        [MenuItem(Menu, true)]
        static bool CanAdd() => Selection.activeGameObject != null;

        public static Vector2 FootprintFeet(GameObject level)
        {
            var lt = level.GetComponent<LevelTemplate>();
            if (lt != null) { Vector3 sz = lt.Size; if (sz.x > 0.01f && sz.z > 0.01f) return new Vector2(sz.x, sz.z) / 0.3048f; }
            return new Vector2(TitleLevels.TileWidthFeet, TitleLevels.TileDepthFeet);
        }

        public static float EdgeLengthFeet(EdgeSocket s)
        {
            Transform root = s.transform.parent;
            while (root != null && root.parent != null && root.GetComponent<LevelTemplate>() == null) root = root.parent;
            Vector2 fp = root != null ? FootprintFeet(root.gameObject) : new Vector2(TitleLevels.TileWidthFeet, TitleLevels.TileDepthFeet);
            return s.side == EdgeSide.Front || s.side == EdgeSide.Back ? fp.x : fp.y;
        }

        /// One socket per side at the footprint edge midpoints, +Z pointing out. Idempotent per side.
        public static List<EdgeSocket> AddSockets(GameObject level, Vector2 footprintFeet, bool placeholderLooks = true)
        {
            var result = new List<EdgeSocket>();
            float hx = footprintFeet.x * 0.3048f * 0.5f, hz = footprintFeet.y * 0.3048f * 0.5f;
            foreach (EdgeSide side in EdgeSides.All)
            {
                EdgeSocket s = EdgeSockets.Find(level, side);
                if (s == null)
                {
                    var go = new GameObject("Edge " + EdgeSides.Name(side));
                    Undo.RegisterCreatedObjectUndo(go, "Add edge socket");
                    go.transform.SetParent(level.transform, false);
                    s = go.AddComponent<EdgeSocket>();
                    s.side = side;
                }
                Vector3 pos; float yaw;
                switch (side)
                {
                    case EdgeSide.Back: pos = new Vector3(0, 0, hz); yaw = 0f; break;          // +Z, into the park
                    case EdgeSide.Right: pos = new Vector3(hx, 0, 0); yaw = 90f; break;
                    case EdgeSide.Left: pos = new Vector3(-hx, 0, 0); yaw = -90f; break;
                    default: pos = new Vector3(0, 0, -hz); yaw = 180f; break;                  // front: −Z, toward the portal
                }
                s.transform.localPosition = pos;
                s.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                if (placeholderLooks) CreatePlaceholderLooks(s, side == EdgeSide.Front || side == EdgeSide.Back ? footprintFeet.x : footprintFeet.y);
                result.Add(s);
            }
            return result;
        }

        /// Placeholder looks: Closed = a full wall; Open = the same wall with a centred doorway + lintel.
        public static void CreatePlaceholderLooks(EdgeSocket s, float edgeFeet, Material wall = null)
        {
            float len = edgeFeet * 0.3048f, door = s.socketWidthFeet * 0.3048f, h = 1.2f, thick = 0.12f;
            if (s.closedLook == null)
            {
                s.closedLook = new GameObject("Closed");
                s.closedLook.transform.SetParent(s.transform, false);
                Block(s.closedLook.transform, "Wall", new Vector3(0, h * 0.5f, 0), new Vector3(len, h, thick), wall);
            }
            if (s.openLook == null)
            {
                s.openLook = new GameObject("Open");
                s.openLook.transform.SetParent(s.transform, false);
                float half = (len - door) * 0.5f;
                Block(s.openLook.transform, "Wall L", new Vector3(-(door + half) * 0.5f, h * 0.5f, 0), new Vector3(half, h, thick), wall);
                Block(s.openLook.transform, "Wall R", new Vector3((door + half) * 0.5f, h * 0.5f, 0), new Vector3(half, h, thick), wall);
                Block(s.openLook.transform, "Lintel", new Vector3(0, 2.1f, 0), new Vector3(door + 0.3f, 0.2f, thick * 1.5f), wall);
                Block(s.openLook.transform, "Post L", new Vector3(-door * 0.5f - 0.08f, 1.05f, 0), new Vector3(0.16f, 2.1f, thick * 1.5f), wall);
                Block(s.openLook.transform, "Post R", new Vector3(door * 0.5f + 0.08f, 1.05f, 0), new Vector3(0.16f, 2.1f, thick * 1.5f), wall);
            }
            s.SetState(EdgeLookState.Closed);
            EditorUtility.SetDirty(s);
        }

        public static GameObject Block(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = name;
            b.transform.SetParent(parent, false);
            b.transform.localPosition = pos;
            b.transform.localScale = size;
            if (mat != null) b.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return b;
        }
    }
}
