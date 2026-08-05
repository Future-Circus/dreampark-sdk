// ─────────────────────────────────────────────────────────────────────
//  ParkSimCamera.cs — move the Scene view. That is the whole job.
//
//  Assets/DreamPark/Scripts/Utils/Simulator.cs already copies
//  SceneView.lastActiveSceneView.camera's pose onto Camera.main every
//  frame. So there is exactly one thing to do to move the guest: set the
//  Scene view's pivot and rotation. Simulator does the rest.
//
//  NO GAMEOBJECT, NO MONOBEHAVIOUR, NO SPAWNED DRIVER. An earlier version
//  of this created a "[ParkSim] Camera Driver" object purely to borrow a
//  MonoBehaviour Update for the patrol animation, and spawned a Simulator
//  alongside it. Both were clutter in the creator's hierarchy for
//  something that is a pure editor concern: EditorApplication.update
//  supplies the tick, and the Scene view is an editor object. Nothing here
//  touches the scene.
//
//  TIME COMES FROM timeSinceStartup, not Time.deltaTime. This ticks from
//  an editor callback, which also fires while the editor is paused,
//  compiling, or between play-mode frames. The delta is clamped so that a
//  recompile stall does not teleport the patrol halfway across the park in
//  one step.
//
//  If the scene has no Simulator, nothing propagates the Scene view onto
//  Camera.main — the Scene view still moves here, but the guest does not,
//  so OptimizedAF is not exercised. ParkSimulator notes that once per
//  generation rather than this file spawning something to fix it.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    internal static class ParkSimCamera
    {
        /// How far back a Go lands from its target. Standing inside an
        /// attraction's bounds is the one place its culling cannot be observed.
        private const float ViewingDistance = 8f;
        private const float PatrolSpeed = 5f;
        private const float EyeHeight = 1.7f;
        private const float ArrivalRadius = 1.5f;

        private static List<Vector3> _route;
        private static int _index;
        private static bool _patrolling;
        private static double _lastTick;

        public static bool IsPatrolling { get { return _patrolling; } }

        /// <summary>
        /// Frame the Scene view on a target, a short distance back from it.
        /// </summary>
        public static void TeleportTo(Vector3 target)
        {
            SetPatrol(false, null);

            var view = SceneView.lastActiveSceneView;
            if (view == null) return;

            view.LookAt(target, view.rotation, ViewingDistance);
            view.Repaint();
        }

        /// <summary>
        /// Walk a loop through <paramref name="route"/> forever. Recovery bugs
        /// show up when an object parks and unparks REPEATEDLY, which nobody
        /// does by hand.
        /// </summary>
        public static void SetPatrol(bool on, List<Vector3> route)
        {
            bool want = on && route != null && route.Count > 1;

            if (want == _patrolling)
            {
                if (want) _route = route;
                return;
            }

            _patrolling = want;
            _route = want ? route : null;
            _index = 0;

            EditorApplication.update -= Tick;
            if (want)
            {
                _lastTick = EditorApplication.timeSinceStartup;
                EditorApplication.update += Tick;
            }
        }

        /// Stop everything. Called when the park is torn down so a patrol
        /// cannot keep driving the Scene view around a park that is gone.
        public static void Reset()
        {
            SetPatrol(false, null);
        }

        private static void Tick()
        {
            if (!_patrolling || _route == null || _route.Count < 2) { Reset(); return; }

            var view = SceneView.lastActiveSceneView;
            if (view == null) { Reset(); return; }

            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastTick);
            _lastTick = now;
            // A recompile or an asset import can stall this callback for
            // seconds. Without the clamp the next tick would jump the patrol
            // most of the way across the park.
            if (dt <= 0f || dt > 0.25f) dt = 0.016f;

            Vector3 target = _route[_index] + Vector3.up * EyeHeight;

            view.pivot = Vector3.MoveTowards(view.pivot, target, PatrolSpeed * dt);

            Vector3 look = target - view.pivot;
            if (look.sqrMagnitude > 1e-3f)
            {
                view.rotation = Quaternion.Slerp(
                    view.rotation, Quaternion.LookRotation(look.normalized, Vector3.up), dt * 2f);
            }
            view.Repaint();

            if (Vector3.Distance(view.pivot, target) < ArrivalRadius)
            {
                _index = (_index + 1) % _route.Count;
            }
        }
    }
}
