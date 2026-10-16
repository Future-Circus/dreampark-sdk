namespace DreamPark
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>Entry, Exit, or Both (a doorway guests use either way — e.g. a piece that
    /// re-routes itself through whichever door a route picks). Values are serialized: append only.</summary>
    public enum AttractionPortalKind { Entry = 0, Exit = 1, Both = 2 }

    /// <summary>
    /// A guest entry or exit on the edge of an attraction's footprint. Pure
    /// metadata for placement: it does not spawn anything. Park packing can use
    /// it to orient an attraction so its entries face the flow of the space and
    /// line up with real, detected doorways.
    ///
    /// <see cref="position"/> is a 0–1 perimeter coordinate (see
    /// <see cref="AttractionPortalGeometry"/>): 0 = middle of the -Z edge, then
    /// clockwise seen from above (Unity yaw): 0.25 = middle of -X, 0.5 = middle
    /// of +Z, 0.75 = middle of +X. Corners sit at 0.125, 0.375, 0.625, 0.875.
    /// Read ×360 it is "degrees around the rectangle".
    /// </summary>
    [Serializable]
    public sealed class AttractionPortal
    {
        public AttractionPortalKind kind;
        [Range(0f, 1f)] public float position;
        /// <summary>Optional clear width this connector needs, metres (0 = any). Routes
        /// (dp.ports / dp.tour) only join it through an opening at least this wide.</summary>
        [Min(0f)] public float width;
        /// <summary>Optional free-form tag ("track", "hall", "stage" …) so a title can say
        /// which connectors join which. Empty = untagged.</summary>
        public string tag = "";

        public AttractionPortal() { }

        public AttractionPortal(AttractionPortalKind kind, float position)
        {
            this.kind = kind;
            this.position = AttractionPortalGeometry.Wrap(position);
        }

        public bool IsEntry => kind == AttractionPortalKind.Entry || kind == AttractionPortalKind.Both;
        public bool IsExit => kind == AttractionPortalKind.Exit || kind == AttractionPortalKind.Both;
        public float Degrees => AttractionPortalGeometry.Wrap(position) * 360f;

        public AttractionPortal Clone() => new AttractionPortal(kind, position) { width = width, tag = tag ?? "" };

        /// <summary>The default every attraction starts with: one entry, middle of -Z.</summary>
        public static List<AttractionPortal> DefaultList() => new List<AttractionPortal>
        {
            new AttractionPortal(AttractionPortalKind.Entry, AttractionPortalGeometry.DefaultEntryPosition),
        };

        public static List<AttractionPortal> CloneList(IReadOnlyList<AttractionPortal> source)
        {
            var copies = new List<AttractionPortal>();
            if (source == null) return copies;
            for (int i = 0; i < source.Count; i++)
                if (source[i] != null) copies.Add(source[i].Clone());
            return copies;
        }
    }

    /// <summary>
    /// Maps the 0–1 perimeter coordinate to attraction-local space and back.
    /// The coordinate is linear along each edge of a SQUARE (each edge spans
    /// 0.25) and that square is stretched to the footprint, so a portal keeps
    /// its relative place on its edge when a layout shrinks or grows on X and
    /// Z independently — it never slides around a corner. Local space: X =
    /// width, Z = length, origin at the footprint centre, y = 0.
    /// </summary>
    public static class AttractionPortalGeometry
    {
        public const float DefaultEntryPosition = 0f;

        public enum Edge { Back = 0, Left = 1, Front = 2, Right = 3 } // -Z, -X, +Z, +X

        public static float Wrap(float t)
        {
            if (float.IsNaN(t) || float.IsInfinity(t)) return 0f;
            t -= Mathf.Floor(t);
            return t >= 1f ? 0f : t;
        }

        /// <summary>The point on the footprint's edge, in attraction-local metres.</summary>
        public static Vector3 LocalPoint(float t, Vector2 footprintMeters)
        {
            Vector2 uv = UnitSquarePoint(t);
            return new Vector3(uv.x * footprintMeters.x * 0.5f, 0f, uv.y * footprintMeters.y * 0.5f);
        }

        /// <summary>Which edge the coordinate lies on (a corner belongs to the edge it starts).</summary>
        public static Edge EdgeOf(float t)
        {
            float s = Wrap(t) * 8f;
            if (s < 1f || s >= 7f) return Edge.Back;
            if (s < 3f) return Edge.Left;
            if (s < 5f) return Edge.Front;
            return Edge.Right;
        }

        /// <summary>Unit outward normal of the edge the coordinate lies on (local space).</summary>
        public static Vector3 OutwardNormal(float t)
        {
            switch (EdgeOf(t))
            {
                case Edge.Left: return Vector3.left;
                case Edge.Front: return Vector3.forward;
                case Edge.Right: return Vector3.right;
                default: return Vector3.back;
            }
        }

        /// <summary>
        /// The direction a guest walks through the portal (local space): into the
        /// attraction for an entry, out of it for an exit.
        /// </summary>
        public static Vector3 WalkDirection(AttractionPortal portal)
        {
            if (portal == null) return Vector3.forward;
            Vector3 outward = OutwardNormal(portal.position);
            // Both walks in, like an entry (its exit direction is simply the reverse).
            return portal.kind == AttractionPortalKind.Exit ? outward : -outward;
        }

        /// <summary>The coordinate of the edge point nearest a local position.</summary>
        public static float FromLocalPoint(Vector3 local, Vector2 footprintMeters)
        {
            float hx = Mathf.Max(footprintMeters.x * 0.5f, 1e-4f);
            float hz = Mathf.Max(footprintMeters.y * 0.5f, 1e-4f);
            float u = Mathf.Clamp(local.x / hx, -1f, 1f);
            float v = Mathf.Clamp(local.z / hz, -1f, 1f);
            // Nearest edge in metres, so a long thin footprint behaves as it looks.
            float back = Mathf.Abs(local.z + hz);
            float front = Mathf.Abs(local.z - hz);
            float left = Mathf.Abs(local.x + hx);
            float right = Mathf.Abs(local.x - hx);
            float nearest = Mathf.Min(Mathf.Min(back, front), Mathf.Min(left, right));
            float s;
            if (nearest == back) s = u <= 0f ? -u : 8f - u;
            else if (nearest == left) s = 2f + v;
            else if (nearest == front) s = 4f + u;
            else s = 6f - v;
            return Wrap(s / 8f);
        }

        public static string EdgeLabel(float t)
        {
            switch (EdgeOf(t))
            {
                case Edge.Left: return "-X edge";
                case Edge.Front: return "+Z edge";
                case Edge.Right: return "+X edge";
                default: return "-Z edge";
            }
        }

        private static Vector2 UnitSquarePoint(float t)
        {
            float s = Wrap(t) * 8f;
            if (s < 1f) return new Vector2(-s, -1f);
            if (s < 3f) return new Vector2(-1f, s - 2f);
            if (s < 5f) return new Vector2(s - 4f, 1f);
            if (s < 7f) return new Vector2(1f, 6f - s);
            return new Vector2(8f - s, -1f);
        }
    }
}
