#if UNITY_EDITOR
using DreamPark;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Scene-view gizmo for hand-held props (HANDHELD.md). Select a prop that has a Grip or
/// Muzzle child (or the Grip/Muzzle itself) and you see:
///   • every grip marker (Grip, PalmGrip, GripLeft, PalmGripLeft, Grip2): +Z (blue, fingers)
///     and +Y (green, thumb side) axes;
///   • Muzzle: +Z (red, the shot direction) and +Y (light blue);
///   • when the prop hangs under a hand (HandTracker, RemoteRig LeftHand/RightHand/Hand): the
///     target the marker must land on (fist centre or palm surface, flipVisual included) as a
///     sphere with the expected marker axes — green when it sits there (ValidateRig tolerances),
///     orange when dp.hold / HandheldMath.Place would move it.
/// Editor-only, draws nothing on unselected objects, adds nothing to any prefab.
/// </summary>
static class HandheldGizmos
{
    static readonly Color GripForward = new Color(0.25f, 0.55f, 1f);
    static readonly Color GripUp = new Color(0.3f, 0.9f, 0.35f);
    static readonly Color MuzzleForward = new Color(1f, 0.25f, 0.2f);
    static readonly Color MuzzleUp = new Color(0.55f, 0.8f, 1f);

    [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
    static void Draw(Transform t, GizmoType type)
    {
        if (t == null) return;
        Transform prop = t;
        if (System.Array.IndexOf(HandheldMath.GripNames, t.name) >= 0 || t.name == HandheldMath.MuzzleName) prop = t.parent;
        if (prop == null || !HandheldMath.IsProp(prop)) return;

        foreach (var n in HandheldMath.GripNames)
        {
            var g = HandheldMath.FindMarker(prop, n);
            if (g != null) Axes(g, 0.06f, GripForward, GripUp, n);
        }
        var muzzle = HandheldMath.FindMuzzle(prop);
        if (muzzle != null) Axes(muzzle, 0.12f, MuzzleForward, MuzzleUp, "Muzzle");

        var node = HandheldMath.FindHandNode(prop, out var kind, out var tracker);
        if (node == null) return;
        bool left = kind == HandheldMath.HandNodeKind.Left
                    || (kind == HandheldMath.HandNodeKind.Tracker && HandheldMath.TrackerIsLeft(tracker));
        HandheldMath.FindGrip(prop, left, out bool palm);
        bool flip = tracker != null && tracker.flipVisual;
        HandheldMath.ExpectedGripPose(left, flip, tracker ? tracker.flipVisualRotation : Vector3.zero,
                                      tracker ? tracker.flipVisualPosition : Vector3.zero, out var ep, out var er, palm);
        Vector3 target = node.TransformPoint(ep);
        Quaternion targetRot = node.rotation * er;
        HandheldMath.MeasureMount(prop, node, left, tracker, out float dist, out float ang);
        bool ok = dist <= HandheldMath.GripTolerance && ang <= HandheldMath.AngleTolerance;
        Gizmos.color = ok ? new Color(0.2f, 1f, 0.3f, 0.8f) : new Color(1f, 0.6f, 0.1f, 0.9f);
        Gizmos.DrawWireSphere(target, 0.012f);
        Gizmos.color = GripForward;
        Gizmos.DrawLine(target, target + targetRot * Vector3.forward * 0.08f);
        Gizmos.color = GripUp;
        Gizmos.DrawLine(target, target + targetRot * Vector3.up * 0.05f);
        Handles.Label(target + Vector3.up * 0.02f,
            $"{(left ? "L" : "R")} {(palm ? "palm" : "fist")}{(ok ? "" : $"  {dist * 100f:0.0} cm / {ang:0}° off — dp.hold() will re-mount")}");
    }

    static void Axes(Transform t, float len, Color forward, Color up, string label)
    {
        Gizmos.color = forward;
        Gizmos.DrawLine(t.position, t.position + t.forward * len);
        Gizmos.color = up;
        Gizmos.DrawLine(t.position, t.position + t.up * len * 0.6f);
        Handles.Label(t.position + t.forward * len, label);
    }
}
#endif
