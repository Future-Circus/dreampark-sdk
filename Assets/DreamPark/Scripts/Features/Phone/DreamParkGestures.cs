using System;
using System.Collections.Generic;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// <summary>Gesture kinds. Integer values are stable (Core submits them).</summary>
    public enum DreamParkGestureKind { Tap = 0, TouchDown = 1, TouchUp = 2, DragBegin = 3, DragMove = 4, DragEnd = 5, Pinch = 6 }

    /// <summary>
    /// One recognized gesture, as an input source hands it to
    /// <see cref="DreamParkGestures.Submit"/>. Recognition (tap vs drag
    /// thresholds, pinch pairing) is the SOURCE's job; the SDK only routes.
    /// </summary>
    public struct DreamParkGesture
    {
        public DreamParkGestureKind kind;
        public int fingerId;
        /// <summary>Normalized 0..1, origin bottom-left (Unity viewport space).</summary>
        public Vector2 screen;
        /// <summary>Normalized delta since the previous event of this gesture (drag/pinch), else zero.</summary>
        public Vector2 delta;
        /// <summary>Pinch only: multiplicative scale since the previous pinch event (1 = none).</summary>
        public float pinchScale;
    }

    /// <summary>A submitted gesture after the SDK raycast it into the world.</summary>
    public sealed class DreamParkRoutedGesture
    {
        public DreamParkGesture gesture;
        public bool hasHit;
        public Vector3 position;
        public Vector3 normal;
        public GameObject hitObject;
        public float distance;
        /// <summary>True when an object hook (ontap / ontouch) returned true for this gesture. Core uses it so a tap a game consumed never also opens AR tap-to-swap. Not exposed to Lua.</summary>
        [BlackList] public bool handled;

        public string KindName => DreamParkGestures.KindName(gesture.kind);
        public string PhaseName => DreamParkGestures.PhaseName(gesture.kind);
    }

    /// <summary>
    /// Phone touch input for content. Core's touch source (and, in the
    /// Editor, DreamParkEditorMouseGestures) calls <see cref="Submit"/>; the
    /// SDK raycasts each gesture from the camera through the screen point and
    /// delivers it two ways:
    ///
    ///   • Object hooks — Tap / TouchDown / TouchUp that hit something call
    ///     `ontap(t)` / `ontouch(t)` on every enabled LuaBehaviour on the hit
    ///     object and its ancestors, nearest first. A handler that returns
    ///     true stops the walk (the tap was handled).
    ///   • Global handlers — every gesture is queued for dp.on_tap /
    ///     dp.on_touch / dp.on_drag / dp.on_pinch, drained by the Lua pump the
    ///     same way dp.on_peer_join is (DreamParkLuaAPI).
    ///
    /// Event table t: { kind, phase, x, y, dx, dy, scale, fingerId,
    /// position, normal, object, distance } — position/normal/object/distance
    /// are nil when the ray hit nothing.
    ///
    /// No C#→Lua delegate types are involved: hooks are called through
    /// LuaFunction.Call, queued events cross as plain tables.
    /// </summary>
    public static class DreamParkGestures
    {
        /// <summary>Core turns this off unless the phone's AR view is on screen AND synced to the park (never in the map/diorama view, while relocalizing, or while the playtest agent plays). Dropped gestures are not queued.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Camera to raycast from. Null (default) = PlayerHead.Override's
        /// Camera when it has one, else Camera.main.
        /// </summary>
        public static Camera RaycastCamera { get; set; }

        /// <summary>Raised synchronously for every routed gesture (after the object hooks). For C# tools and tests.</summary>
        public static event Action<DreamParkRoutedGesture> Routed;

        public const int MaxPending = 64;
        const float MaxRayDistance = 1000f;

        static readonly List<DreamParkRoutedGesture> _pending = new List<DreamParkRoutedGesture>();
        static readonly RaycastHit[] _hits = new RaycastHit[16];

        /// <summary>Gestures waiting for the Lua pump.</summary>
        public static int PendingCount => _pending.Count;

        /// <summary>Take every queued gesture (oldest first). The Lua pump calls this every frame.</summary>
        public static List<DreamParkRoutedGesture> DrainPending()
        {
            var list = new List<DreamParkRoutedGesture>(_pending);
            _pending.Clear();
            return list;
        }

        public static void ClearPending() => _pending.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without a domain reload keeps statics alive.
            _pending.Clear();
            Enabled = true;
            RaycastCamera = null;
        }

        /// <summary>Route one gesture. Called by input sources.</summary>
        public static void Submit(DreamParkGesture g)
        {
            if (!Enabled) return;

            var routed = Raycast(g);

            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
            _pending.Add(routed);
            // The pump drains the queue every frame, listener or not, so
            // nothing stale is waiting for a handler that registers later.
            if (Application.isPlaying) DreamParkLuaAPI.EnsurePump();

            // Scripts only boot in Play mode (LuaBehaviour.ScriptScope), so there
            // is nothing to call outside it.
            if (Application.isPlaying && routed.hasHit && routed.hitObject != null)
            {
                string hook = g.kind == DreamParkGestureKind.Tap ? "ontap"
                    : g.kind == DreamParkGestureKind.TouchDown || g.kind == DreamParkGestureKind.TouchUp ? "ontouch"
                    : null;
                if (hook != null) routed.handled = CallObjectHooks(routed, hook);
            }

            try { Routed?.Invoke(routed); }
            catch (Exception e) { Debug.LogWarning("[DreamParkGestures] Routed handler threw: " + e.Message); }
        }

        static Camera ResolveCamera()
        {
            if (RaycastCamera != null) return RaycastCamera;
            var over = PlayerHead.Override;
            if (over != null)
            {
                var c = over.GetComponent<Camera>();
                if (c != null) return c;
            }
            return Camera.main;
        }

        static DreamParkRoutedGesture Raycast(DreamParkGesture g)
        {
            var routed = new DreamParkRoutedGesture { gesture = g };
            // default(DreamParkGesture) carries 0; "no scale change" is 1.
            if (g.pinchScale <= 0f) routed.gesture.pinchScale = 1f;

            var cam = ResolveCamera();
            if (cam == null) return routed;

            Ray ray = cam.ViewportPointToRay(new Vector3(g.screen.x, g.screen.y, 0f));
            int mask = Physics.DefaultRaycastLayers;
            int ignore = LayerMask.NameToLayer("Ignore Raycast");
            int ui = LayerMask.NameToLayer("UI");
            if (ignore >= 0) mask &= ~(1 << ignore);
            if (ui >= 0) mask &= ~(1 << ui);

            int n = Physics.RaycastNonAlloc(ray, _hits, MaxRayDistance, mask, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider == null || h.distance >= best) continue;
                // The local player's own rig (a phone rig's head/body volumes
                // sit right at the camera) is never what a tap means.
                if (h.collider.GetComponentInParent<PlayerRig>(true) != null) continue;
                best = h.distance;
                routed.hasHit = true;
                routed.position = h.point;
                routed.normal = h.normal;
                routed.hitObject = h.collider.gameObject;
                routed.distance = h.distance;
            }
            return routed;
        }

        /// <returns>True when a hook returned true (the gesture was handled).</returns>
        static bool CallObjectHooks(DreamParkRoutedGesture routed, string hook)
        {
            LuaEnv env = null;
            try { env = LuaBehaviour.GetLuaEnv(); } catch (Exception) { env = null; }
            if (env == null) return false;

            LuaTable t = null;
            try
            {
                for (Transform tr = routed.hitObject.transform; tr != null; tr = tr.parent)
                {
                    var behaviours = tr.GetComponents<LuaBehaviour>();
                    for (int i = 0; i < behaviours.Length; i++)
                    {
                        var lb = behaviours[i];
                        // A parked (optimizer-disabled) or inactive script is not
                        // live; writing to it would be lost on restore.
                        if (lb == null || !lb.isActiveAndEnabled) continue;
                        LuaTable scope;
                        try { scope = lb.ScriptScope; } catch (Exception) { continue; }
                        if (scope == null) continue;
                        LuaFunction fn = null;
                        try { fn = scope.Get<LuaFunction>(hook); } catch (Exception) { fn = null; }
                        if (fn == null) continue;
                        if (t == null) t = ToLuaTable(env, routed);
                        bool handled = false;
                        try
                        {
                            object[] ret = fn.Call(t);
                            handled = ret != null && ret.Length > 0 && ret[0] is bool b && b;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[DreamParkGestures] {hook} on '{lb.name}' threw: {e.Message}");
                        }
                        finally { fn.Dispose(); }
                        if (handled) return true;
                    }
                }
            }
            finally { t?.Dispose(); }
            return false;
        }

        /// <summary>The Lua event table for one routed gesture.</summary>
        public static LuaTable ToLuaTable(LuaEnv env, DreamParkRoutedGesture r)
        {
            var t = env.NewTable();
            var g = r.gesture;
            t.Set("kind", KindName(g.kind));
            t.Set("phase", PhaseName(g.kind));
            t.Set("x", g.screen.x);
            t.Set("y", g.screen.y);
            t.Set("dx", g.delta.x);
            t.Set("dy", g.delta.y);
            t.Set("scale", g.pinchScale);
            t.Set("fingerId", g.fingerId);
            if (r.hasHit)
            {
                t.Set("position", r.position);
                t.Set("normal", r.normal);
                if (r.hitObject != null) t.Set("object", r.hitObject);
                t.Set("distance", r.distance);
            }
            return t;
        }

        /// <summary>"tap" | "touch" | "drag" | "pinch".</summary>
        public static string KindName(DreamParkGestureKind k)
        {
            switch (k)
            {
                case DreamParkGestureKind.Tap: return "tap";
                case DreamParkGestureKind.TouchDown:
                case DreamParkGestureKind.TouchUp: return "touch";
                case DreamParkGestureKind.DragBegin:
                case DreamParkGestureKind.DragMove:
                case DreamParkGestureKind.DragEnd: return "drag";
                default: return "pinch";
            }
        }

        /// <summary>tap: "tap"; touch: "down"/"up"; drag: "begin"/"move"/"end"; pinch: "change".</summary>
        public static string PhaseName(DreamParkGestureKind k)
        {
            switch (k)
            {
                case DreamParkGestureKind.Tap: return "tap";
                case DreamParkGestureKind.TouchDown: return "down";
                case DreamParkGestureKind.TouchUp: return "up";
                case DreamParkGestureKind.DragBegin: return "begin";
                case DreamParkGestureKind.DragMove: return "move";
                case DreamParkGestureKind.DragEnd: return "end";
                default: return "change";
            }
        }
    }
}
