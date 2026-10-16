namespace DreamPark
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    public enum AttractionLayoutFrameKind { Custom = 0, Minimum = 1 }

    /// <summary>
    /// One hand-authored difference between the Base layout and a layout frame.
    /// Only the flagged properties are applied; everything else keeps its Base value.
    /// </summary>
    [Serializable]
    public sealed class AttractionLayoutOverride
    {
        // Hierarchy path from the attraction root (AttractionPackingBaker.PathFrom
        // format). Authoritative: it survives copying into another hierarchy.
        public string path;
        // Resolved reference (fast path). Re-resolved from path when a frame is
        // copied into a different hierarchy such as the prefab asset.
        public Transform target;
        public bool setActive;
        public bool active;
        public bool setPosition;
        public Vector3 localPosition;
        public bool setRotation;
        public Quaternion localRotation = Quaternion.identity;
        public bool setScale;
        public Vector3 localScale = Vector3.one;

        /// <summary>The live transform this override targets under root, or null.</summary>
        public Transform Resolve(Transform root) =>
            AttractionLayoutPaths.Resolve(root, target, path);
    }

    /// <summary>
    /// A complete, hand-authored arrangement of an attraction at one width × length.
    /// Each frame carries its own automatic shrink/grow bake; frames never blend.
    /// </summary>
    [Serializable]
    public sealed class AttractionLayoutFrame
    {
        public string id;
        public string name;
        public AttractionLayoutFrameKind kind;
        // x = local-X width, y = local-Z length, in feet.
        public Vector2 sizeFeet;
        public List<AttractionLayoutOverride> overrides = new List<AttractionLayoutOverride>();
        // Entries/exits. false = this frame uses the Base list (the default, so
        // frames authored before portals existed keep following Base).
        public bool overridePortals;
        public List<AttractionPortal> portals = new List<AttractionPortal>();
        // Authored footprint of this bake == SizeMeters.
        [SerializeField] private AttractionPackingBake bake;

        public AttractionPackingBake Bake => bake;
        public Vector2 SizeMeters => sizeFeet * 0.3048f;
        public void SetBake(AttractionPackingBake value) => bake = value;
    }

    /// <summary>One side of an attraction's footprint, in prefab-local space.</summary>
    public enum AttractionWallSide { Front = 0, Back = 1, Right = 2, Left = 3 }   // +Z, -Z, +X, -X

    /// <summary>Which sides of a seated attraction stand on a real wall.</summary>
    [Flags]
    public enum AttractionWallMask { None = 0, Front = 1, Back = 2, Right = 4, Left = 8, All = 15 }

    /// <summary>
    /// Optional extras for one side of the attraction, used only when that side is
    /// seated on a rect edge core reports as a wall (WALL_LAYERS_SPEC.md). Same
    /// overrides as a layout frame, layered on top of whichever frame is applied.
    /// The attraction must work without any wall layer: they are bonuses.
    /// </summary>
    [Serializable]
    public sealed class AttractionWallLayer
    {
        public string id;
        public AttractionWallSide side;
        public List<AttractionLayoutOverride> overrides = new List<AttractionLayoutOverride>();
        // The Base footprint (local X × Z, metres) the layer was authored against.
        // Positions keep their distance from this side's edge and their relative
        // place along it when the applied layout is a different size.
        public Vector2 authoredFootprintMeters;
    }

    public static class AttractionWallSides
    {
        public static readonly AttractionWallSide[] All =
            { AttractionWallSide.Front, AttractionWallSide.Back, AttractionWallSide.Right, AttractionWallSide.Left };

        public static AttractionWallMask Mask(AttractionWallSide side) => (AttractionWallMask)(1 << (int)side);

        public static bool Has(AttractionWallMask mask, AttractionWallSide side) => (mask & Mask(side)) != 0;

        /// <summary>Wire / Lua name: "front" | "back" | "right" | "left".</summary>
        public static string Name(AttractionWallSide side)
        {
            switch (side)
            {
                case AttractionWallSide.Back: return "back";
                case AttractionWallSide.Right: return "right";
                case AttractionWallSide.Left: return "left";
                default: return "front";
            }
        }

        public static bool TryParse(string value, out AttractionWallSide side)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "front": case "+z": side = AttractionWallSide.Front; return true;
                case "back": case "-z": side = AttractionWallSide.Back; return true;
                case "right": case "+x": side = AttractionWallSide.Right; return true;
                case "left": case "-x": side = AttractionWallSide.Left; return true;
                default: side = AttractionWallSide.Front; return false;
            }
        }

        /// <summary>"front,left" (fixed Front, Back, Right, Left order); "" for None.</summary>
        public static string ToWire(AttractionWallMask mask)
        {
            var parts = new List<string>(4);
            for (int i = 0; i < All.Length; i++)
                if (Has(mask, All[i])) parts.Add(Name(All[i]));
            return string.Join(",", parts);
        }

        /// <summary>Lenient: unknown tokens are ignored.</summary>
        public static AttractionWallMask FromWire(string value)
        {
            var mask = AttractionWallMask.None;
            if (string.IsNullOrEmpty(value)) return mask;
            string[] parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
                if (TryParse(parts[i], out AttractionWallSide side)) mask |= Mask(side);
            return mask;
        }

        /// <summary>
        /// The prefab-local side facing a local outward direction (y ignored), or
        /// false when the direction is not within ~1° of a footprint axis.
        /// </summary>
        public static bool TryFromLocalDirection(Vector3 direction, out AttractionWallSide side)
        {
            side = AttractionWallSide.Front;
            var flat = new Vector2(direction.x, direction.z);
            if (flat.sqrMagnitude < 1e-8f) return false;
            flat.Normalize();
            const float cosOneDegree = 0.99985f;
            if (flat.y >= cosOneDegree) { side = AttractionWallSide.Front; return true; }
            if (flat.y <= -cosOneDegree) { side = AttractionWallSide.Back; return true; }
            if (flat.x >= cosOneDegree) { side = AttractionWallSide.Right; return true; }
            if (flat.x <= -cosOneDegree) { side = AttractionWallSide.Left; return true; }
            return false;
        }
    }

    /// <summary>
    /// The authored (Base) state of one transform that a frame or bake may touch.
    /// ApplyLayout restores these before applying any frame so switching frames on
    /// a live attraction is order-independent.
    /// </summary>
    [Serializable]
    public sealed class AttractionLayoutBaseState
    {
        public string path;
        public Transform target;
        public bool active;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;

        /// <summary>The live transform this state describes under root, or null.</summary>
        public Transform Resolve(Transform root) =>
            AttractionLayoutPaths.Resolve(root, target, path);
    }

    /// <summary>
    /// One frame's baked range. Scales are relative to that frame's own authored
    /// footprint; all dimensions are local-X/local-Z metres.
    /// </summary>
    [Serializable]
    public struct AttractionFrameEnvelope
    {
        public string frameId;
        public string kind;     // "base" | "custom" | "minimum"
        public string name;
        public Vector2 authoredMeters;
        public Vector2 shrinkMeters;
        public Vector2 growMeters;
        public Vector2 shrinkScale;
        public Vector2 growScale;
    }

    internal static class AttractionLayoutPaths
    {
        /// <summary>
        /// Prefer the serialized reference while it still lives under root; fall
        /// back to the hierarchy path. The attraction root itself is never a target.
        /// </summary>
        internal static Transform Resolve(Transform root, Transform target, string path)
        {
            if (root == null) return null;
            if (target != null && target != root && target.IsChildOf(root)) return target;
            if (string.IsNullOrEmpty(path)) return null;
            Transform found = root.Find(path);
            return found != root ? found : null;
        }
    }
}
