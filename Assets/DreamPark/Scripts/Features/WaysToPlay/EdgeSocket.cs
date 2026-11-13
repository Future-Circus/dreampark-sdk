// ─────────────────────────────────────────────────────────────────────
//  EdgeSocket.cs — how one side of a level looks when it stands alone vs.
//  when a neighbouring tile is joined to it (contract ways-to-play.md §2/§4).
//
//  A board is 12 × 18 ft tiles. Rooms work standalone (every edge CLOSED:
//  a wall, a hedge, a fence) and grow intentional doorways / gates when the
//  tile next to them is joined (Rect Edges v3 'adjacent' openings) or when the
//  director asks (dp.edges.open / dp.edges.lock).
//
//  Authoring: put one EdgeSocket per side on the level, with the GameObject on
//  the edge midpoint and its +Z pointing out of the level. Sides are level-local
//  at the Base layout, the runtime's convention (RunnerRecord.cs
//  IRunnerEdgeSocket): front = −Z (toward the portal), back = +Z, right = +X,
//  left = −X. Give it a Closed child and an
//  Open child (doorway / gate / path). `sockets` are the doorway centres along
//  the edge in FEET from the edge midpoint (default one, centred) and every
//  level uses the same standard width, so two neighbours' doorways line up.
//
//  The platform (RunnerHost) owns WHEN a socket opens and calls ApplyEdge
//  (IRunnerEdgeSocket); this file only owns the looks. Default on spawn =
//  Closed: a level must play fully closed.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.WaysToPlay
{
    public enum EdgeLookState { Closed = 0, Open = 1, Locked = 2 }

    /// Level-local side, runtime convention: Front = −Z (toward the portal), Back = +Z, Right = +X, Left = −X.
    public enum EdgeSide { Front = 0, Back = 1, Right = 2, Left = 3 }

    public static class EdgeSides
    {
        public static readonly EdgeSide[] All = { EdgeSide.Front, EdgeSide.Back, EdgeSide.Right, EdgeSide.Left };
        public static string Name(EdgeSide s) => s == EdgeSide.Back ? "back" : s == EdgeSide.Right ? "right" : s == EdgeSide.Left ? "left" : "front";
        public static bool TryParse(string v, out EdgeSide s)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "front": case "-z": s = EdgeSide.Front; return true;
                case "back": case "+z": s = EdgeSide.Back; return true;
                case "right": case "+x": s = EdgeSide.Right; return true;
                case "left": case "-x": s = EdgeSide.Left; return true;
                default: s = EdgeSide.Front; return false;
            }
        }
        /// Outward direction in level-local space.
        public static Vector3 Outward(EdgeSide s) => s == EdgeSide.Back ? Vector3.forward : s == EdgeSide.Right ? Vector3.right : s == EdgeSide.Left ? Vector3.left : Vector3.back;
        /// The same side in AttractionRectOpenings terms (its Front is +Z).
        public static AttractionWallSide ToWallSide(EdgeSide s) => s == EdgeSide.Front ? AttractionWallSide.Back : s == EdgeSide.Back ? AttractionWallSide.Front : s == EdgeSide.Right ? AttractionWallSide.Right : AttractionWallSide.Left;
        /// The side a level-local outward direction points to (y ignored).
        public static EdgeSide FromOutward(Vector3 local)
        {
            if (Mathf.Abs(local.x) > Mathf.Abs(local.z)) return local.x >= 0f ? EdgeSide.Right : EdgeSide.Left;
            return local.z >= 0f ? EdgeSide.Back : EdgeSide.Front;
        }
    }

    [Serializable]
    public sealed class EdgeNamedLook
    {
        public string name = "gate";
        public GameObject look;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("DreamPark/Ways to Play/Edge Socket")]
    public sealed class EdgeSocket : MonoBehaviour, IRunnerEdgeSocket
    {
        public const float StandardWidthFeet = 4f;
        public const float FeetToMeters = 0.3048f;

        [Tooltip("Which side of the level this is (level-local: front = −Z toward the portal, back = +Z, right = +X, left = −X).")]
        public EdgeSide side = EdgeSide.Front;
        [Tooltip("Shown when this side stands alone (a wall, a hedge…). The level must play with every side closed.")]
        public GameObject closedLook;
        [Tooltip("The default open look (a doorway / arch) shown when a neighbouring tile is joined.")]
        public GameObject openLook;
        [Tooltip("Other open looks the director can ask for by name: dp.edges.open(tile, side, 'gate').")]
        public List<EdgeNamedLook> namedOpenLooks = new List<EdgeNamedLook>();
        [Tooltip("Optional: a shut gate, shown while the director locks this edge (dp.edges.lock). Falls back to Closed.")]
        public GameObject lockedLook;
        [Tooltip("Doorway centres along the edge, in feet from the edge midpoint (+ = this object's +X).")]
        public List<float> sockets = new List<float> { 0f };
        [Tooltip("Doorway width in feet. Keep the standard 4 ft so neighbours' doorways line up.")]
        public float socketWidthFeet = StandardWidthFeet;

        [SerializeField, HideInInspector] private EdgeLookState state = EdgeLookState.Closed;
        [SerializeField, HideInInspector] private string look = "";

        public EdgeLookState State => state;
        /// The open look in use ("" = the default openLook).
        public string Look => look;
        public string SideName => EdgeSides.Name(side);

        // ── IRunnerEdgeSocket (the runtime's seam, RunnerRecord.cs) ──
        string IRunnerEdgeSocket.EdgeSide => SideName;

        /// look: "closed" | "open" | "doorway" | a named look ("gate", …); unknown names = the default open look.
        public void ApplyEdge(string lookName, bool locked)
        {
            if (locked) { SetState(EdgeLookState.Locked); return; }
            string l = (lookName ?? "").Trim();
            if (l.Length == 0 || string.Equals(l, "closed", StringComparison.OrdinalIgnoreCase)) { SetState(EdgeLookState.Closed); return; }
            bool plain = string.Equals(l, "open", StringComparison.OrdinalIgnoreCase) || string.Equals(l, "doorway", StringComparison.OrdinalIgnoreCase);
            SetState(EdgeLookState.Open, plain && !HasNamedLook(l) ? null : l);
        }

        /// The side this socket actually faces (its +Z in the level root's frame) — authoring check.
        public EdgeSide FacingSide(Transform levelRoot)
        {
            Vector3 dir = levelRoot != null ? levelRoot.InverseTransformDirection(transform.forward) : transform.localRotation * Vector3.forward;
            return EdgeSides.FromOutward(dir);
        }

        /// Raised after every state change (socket, new state, look).
        public static event Action<EdgeSocket, EdgeLookState, string> Changed;

        void Awake()
        {
            if (Application.isPlaying) Apply();
        }

        /// Show one look. Open with a `look` name picks that named open look (unknown → default open look).
        public void SetState(EdgeLookState next, string lookName = null)
        {
            state = next;
            look = next == EdgeLookState.Open ? (lookName ?? "") : "";
            Apply();
            Changed?.Invoke(this, state, look);
        }

        public void SetOpen(bool open, string lookName = null) => SetState(open ? EdgeLookState.Open : EdgeLookState.Closed, lookName);

        /// Resolve the GameObject that should be visible for the current state.
        public GameObject ActiveLook()
        {
            switch (state)
            {
                case EdgeLookState.Open:
                    if (!string.IsNullOrEmpty(look))
                        for (int i = 0; i < namedOpenLooks.Count; i++)
                            if (namedOpenLooks[i] != null && namedOpenLooks[i].look != null &&
                                string.Equals(namedOpenLooks[i].name, look, StringComparison.OrdinalIgnoreCase))
                                return namedOpenLooks[i].look;
                    return openLook != null ? openLook : FirstNamedLook();
                case EdgeLookState.Locked:
                    return lockedLook != null ? lockedLook : closedLook;
                default:
                    return closedLook;
            }
        }

        /// True when the side has something to show when opened (pre-upload check).
        public bool HasOpenLook => openLook != null || FirstNamedLook() != null;

        public bool HasNamedLook(string lookName)
        {
            if (string.IsNullOrEmpty(lookName)) return HasOpenLook;
            for (int i = 0; i < namedOpenLooks.Count; i++)
                if (namedOpenLooks[i] != null && namedOpenLooks[i].look != null &&
                    string.Equals(namedOpenLooks[i].name, lookName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        GameObject FirstNamedLook()
        {
            for (int i = 0; i < namedOpenLooks.Count; i++)
                if (namedOpenLooks[i] != null && namedOpenLooks[i].look != null) return namedOpenLooks[i].look;
            return null;
        }

        /// Activate exactly the active look; every other look goes inactive.
        public void Apply()
        {
            GameObject show = ActiveLook();
            SetLook(closedLook, show);
            SetLook(openLook, show);
            SetLook(lockedLook, show);
            for (int i = 0; i < namedOpenLooks.Count; i++)
                if (namedOpenLooks[i] != null) SetLook(namedOpenLooks[i].look, show);
        }

        static void SetLook(GameObject go, GameObject show)
        {
            if (go == null) return;
            bool on = go == show;
            if (go.activeSelf != on) go.SetActive(on);
        }

        /// World position of socket `i` (its doorway centre on the edge).
        public Vector3 SocketWorldPosition(int i)
        {
            float feet = sockets != null && i >= 0 && i < sockets.Count ? sockets[i] : 0f;
            return transform.TransformPoint(new Vector3(feet * FeetToMeters, 0f, 0f));
        }

        public int SocketCount => sockets == null || sockets.Count == 0 ? 1 : sockets.Count;

#if UNITY_EDITOR
        void OnDrawGizmos() => DrawGizmo(false);
        void OnDrawGizmosSelected() => DrawGizmo(true);

        void DrawGizmo(bool selected)
        {
            Color c = state == EdgeLookState.Open ? new Color(0.2f, 0.9f, 0.4f)
                    : state == EdgeLookState.Locked ? new Color(1f, 0.7f, 0.1f)
                    : new Color(0.95f, 0.3f, 0.3f);
            Gizmos.matrix = transform.localToWorldMatrix;
            float w = Mathf.Max(0.5f, socketWidthFeet) * FeetToMeters;
            for (int i = 0; i < SocketCount; i++)
            {
                float x = (sockets != null && i < sockets.Count ? sockets[i] : 0f) * FeetToMeters;
                Gizmos.color = new Color(c.r, c.g, c.b, selected ? 0.55f : 0.3f);
                Gizmos.DrawCube(new Vector3(x, 1.1f, 0f), new Vector3(w, 2.2f, 0.06f));
                Gizmos.color = c;
                Gizmos.DrawWireCube(new Vector3(x, 1.1f, 0f), new Vector3(w, 2.2f, 0.06f));
            }
            // Outward arrow.
            Gizmos.color = c;
            Gizmos.DrawLine(new Vector3(0f, 0.05f, 0f), new Vector3(0f, 0.05f, 0.6f));
            Gizmos.DrawLine(new Vector3(0f, 0.05f, 0.6f), new Vector3(-0.15f, 0.05f, 0.42f));
            Gizmos.DrawLine(new Vector3(0f, 0.05f, 0.6f), new Vector3(0.15f, 0.05f, 0.42f));
            Gizmos.matrix = Matrix4x4.identity;
            if (selected)
                UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f,
                    $"{SideName} · {state}{(string.IsNullOrEmpty(look) ? "" : " (" + look + ")")}");
        }
#endif
    }

    /// A Link prefab that straddles the seam between two specific levels (a bridge, a shared
    /// archway). RUNTIME: when levelA and levelB sit in neighbouring tiles across `sideOfA`, spawn
    /// it on the seam (rotation = A's side outward) and open both sockets.
    [DisallowMultipleComponent]
    [AddComponentMenu("DreamPark/Ways to Play/Edge Link")]
    public sealed class EdgeLink : MonoBehaviour
    {
        [Tooltip("Level prefab name on this side of the seam.")] public string levelA = "";
        [Tooltip("Level prefab name on the other side.")] public string levelB = "";
        [Tooltip("The side of level A the seam is on.")] public EdgeSide sideOfA = EdgeSide.Right;
        [Tooltip("Width of the link along the seam, in feet.")] public float widthFeet = EdgeSocket.StandardWidthFeet;

        public bool Matches(string a, string b, EdgeSide side) =>
            (string.Equals(a, levelA, StringComparison.OrdinalIgnoreCase) && string.Equals(b, levelB, StringComparison.OrdinalIgnoreCase) && side == sideOfA);

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.4f, 0.6f, 1f, 0.8f);
            float w = widthFeet * EdgeSocket.FeetToMeters;
            Gizmos.DrawWireCube(new Vector3(0f, 0.1f, 0f), new Vector3(w, 0.2f, 1.2f));
            Gizmos.matrix = Matrix4x4.identity;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.6f, $"link {levelA} ⇄ {levelB}");
        }
#endif
    }

    /// Helpers the platform (RunnerHost / dp.edges) calls on a placed level.
    public static class EdgeSockets
    {
        public static List<EdgeSocket> All(GameObject levelRoot)
        {
            var list = new List<EdgeSocket>();
            if (levelRoot != null) levelRoot.GetComponentsInChildren(true, list);
            return list;
        }

        public static EdgeSocket Find(GameObject levelRoot, EdgeSide side)
        {
            foreach (var s in All(levelRoot)) if (s.side == side) return s;
            return null;
        }

        /// dp.edges.open(tile, side, look). Returns false when the level has no socket on that side.
        public static bool Open(GameObject levelRoot, EdgeSide side, string look = null)
        {
            bool any = false;
            foreach (var s in All(levelRoot)) if (s.side == side) { s.SetState(EdgeLookState.Open, look); any = true; }
            return any;
        }

        public static bool Close(GameObject levelRoot, EdgeSide side)
        {
            bool any = false;
            foreach (var s in All(levelRoot)) if (s.side == side) { s.SetState(EdgeLookState.Closed); any = true; }
            return any;
        }

        /// dp.edges.lock(tile, side, on). Unlocking returns the side to Open (it was a gate).
        public static bool Lock(GameObject levelRoot, EdgeSide side, bool on)
        {
            bool any = false;
            foreach (var s in All(levelRoot))
                if (s.side == side) { s.SetState(on ? EdgeLookState.Locked : EdgeLookState.Open, on ? null : s.Look); any = true; }
            return any;
        }

        public static void CloseAll(GameObject levelRoot)
        {
            foreach (var s in All(levelRoot)) s.SetState(EdgeLookState.Closed);
        }

        /// Open every socket an 'adjacent' Rect Edges v3 span covers, close the rest; a Locked
        /// socket stays Locked. `data` is the level's AttractionRectOpenings (attraction.RectOpenings).
        /// Returns how many sockets ended up Open.
        public static int ApplyAdjacency(GameObject levelRoot, AttractionRectOpenings data)
        {
            if (levelRoot == null || data == null) return 0;
            int open = 0;
            Transform root = levelRoot.transform;
            foreach (var s in All(levelRoot))
            {
                if (s.State == EdgeLookState.Locked) continue;
                bool joined = false;
                for (int i = 0; i < s.SocketCount && !joined; i++)
                {
                    Vector3 p = root.InverseTransformPoint(s.SocketWorldPosition(i));
                    AttractionWallSide ws = EdgeSides.ToWallSide(s.side);
                    joined = IsAdjacentAt(data, ws, AlongSide(data, ws, p));
                }
                s.SetState(joined ? EdgeLookState.Open : EdgeLookState.Closed, joined ? s.Look : null);
                if (joined) open++;
            }
            return open;
        }

        /// Inverse of AttractionRectOpenings.LocalPoint: metres along `side` for a level-local point.
        public static float AlongSide(AttractionRectOpenings data, AttractionWallSide side, Vector3 local)
        {
            float hx = data.rectSize.x * 0.5f, hz = data.rectSize.y * 0.5f;
            float cx = data.rectCenter.x, cz = data.rectCenter.y;
            switch (side)
            {
                case AttractionWallSide.Back: return cx + hx - local.x;
                case AttractionWallSide.Right: return cz + hz - local.z;
                case AttractionWallSide.Left: return local.z - (cz - hz);
                default: return local.x - (cx - hx);
            }
        }

        public static bool IsAdjacentAt(AttractionRectOpenings data, AttractionWallSide side, float t)
        {
            IReadOnlyList<AttractionRectSpan> spans = data.Spans(side);
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].IsAdjacent && t >= spans[i].a - 1e-3f && t <= spans[i].b + 1e-3f) return true;
            return false;
        }
    }
}
