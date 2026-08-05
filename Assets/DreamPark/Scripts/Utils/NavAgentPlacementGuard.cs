// ─────────────────────────────────────────────────────────────────────
//  NavAgentPlacementGuard.cs — SDK-shared (moved out of Assets/Scripts)
//
//  Lives under Assets/DreamPark so core and the SDK run the SAME guard.
//  The SDK's Park Simulator spawns attractions through the same shape as
//  LevelAnchor.Spawn, so without this a creator testing in the simulator
//  would watch their NavMeshAgent props snap off their spawn point — a bug
//  that does not exist in production and that they would then go hunting
//  for in their own content. The only core-specific thing in here is the
//  build-mode check, which is #if'd: the SDK has no build mode, so
//  IsBuildMode is constant false there and every suspension releases
//  immediately, which is exactly the play-mode behaviour.
//
//  THE PROP-SNAP BUG. Every placed attraction bakes its own NavMeshSurface
//  over its floor plane in LevelTemplate.Start(), so a prop carrying a
//  NavMeshAgent lands on live navmesh. A NavMeshAgent binds itself to the
//  NEAREST navmesh point the instant it ENABLES — which is at
//  Instantiate() time, while the object is still sitting at the prefab's
//  default pose. The creator's saved pose is written to the transform
//  afterwards (LevelAnchor.Spawn / LevelAnchor.NewLevel), but writing a
//  transform does NOT move an agent's internal nav position: that is what
//  Warp() is for. On the agent's next update `updatePosition` copies the
//  stale internal position back onto the transform and the prop visibly
//  jumps off where the creator put it.
//
//  WHAT ACTUALLY CAUSES THE JUMP IS `updatePosition`, NOT THE AGENT BEING
//  ALIVE. So the freeze suppresses the write-back and parks the agent in
//  place; it does NOT disable the component. That is deliberate, and it is
//  the same idiom PortalAnchor.FreezeNavAgents/RestoreNavAgents uses when
//  it re-anchors the whole park after a QR sync. Disabling the agent
//  instead would make every SetDestination call made from a prefab's
//  Start(), a LuaBehaviour tick or an EasyEvent chain log Unity's
//  "can only be called on an active agent that has been placed on a
//  NavMesh" — harmless but noisy, and in BUILD mode that window is the
//  whole session. Never swap this back to `agent.enabled = false`.
//
//    Suspend(root)         record each agent's updatePosition/
//                          updateRotation/isStopped, then pin it:
//                          updatePosition = updateRotation = false,
//                          isStopped = true. Called IMMEDIATELY after
//                          Instantiate, before any yield.
//    Release(root)         Warp onto the authored transform, restore the
//                          recorded flags.
//    ReleaseIfPlayMode()   the spawn paths' release — play mode re-seats
//                          right away, BUILD mode and an in-progress PARK
//                          LOAD both hold the freeze.
//
//  RELEASING AT SPAWN TIME WAS THE SECOND HALF OF THE BUG. Pinning the
//  agent stops it dragging the prop, but the release Warps it onto the
//  authored transform — and during a park load NOTHING IS READY at that
//  moment. The attraction's own NavMeshSurface has been baked over the flat
//  authored grid and not yet re-baked for the conformed floor; GapFiller
//  has not built the ground between attractions at all. So the Warp binds
//  the agent to whatever navmesh happens to exist nearby, which is some
//  OTHER attraction, and the prop is dragged there the moment
//  updatePosition comes back. In a scanned room every floor is at
//  roughly the same height so the wrong answer looks like the right one;
//  across a park with real spacing and relief the prop flies across the map.
//
//  So a freeze taken while LevelObjectManager.ParkContentLocked is held is
//  released on the UNLOCK EDGE instead — the same moment the loader
//  releases physics, which is by definition after the floors are calibrated
//  and the gap mesh exists. That is the one instant when "the nearest
//  navmesh" and "the floor this prop was placed on" are the same thing.
//
//  AND THE FLAGS ONLY COME BACK ONCE THE AGENT IS ACTUALLY SEATED.
//  agent.Warp returns a bool that the old code discarded. If it fails there
//  is no navmesh under the authored position, and restoring updatePosition
//  is PRECISELY the act that teleports the prop somewhere else. So a
//  failed seat keeps the agent pinned and retries; a prop that stands still
//  is a cosmetic problem, a prop that flies across the park is a bug
//  report. If it still cannot be seated after SeatTimeoutSeconds it stays
//  pinned for good and says so, naming the object.
//
//  BUILD MODE holds the suspension for the whole session: agents have no
//  purpose while a creator is arranging, and a live agent drags the prop
//  out from under the gizmo. The build→play edge releases —
//  BuildModeObjectController.Toggle(false) drives it, and this component's
//  own edge watch is the backstop for objects that carry no controller.
//
//  COSTS NOTHING in the common case: Suspend() early-outs on a zero-length
//  agent array and never adds the component, and the component switches
//  its own Update off the moment it is released.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace DreamPark
{
    [DisallowMultipleComponent]
    public class NavAgentPlacementGuard : MonoBehaviour
    {
        private struct AgentState
        {
            public NavMeshAgent agent;
            public bool wasEnabled;
            public bool updatePosition;
            public bool updateRotation;
            public bool hadStopFlag;   // isStopped was legible when we recorded
            public bool isStopped;
            public bool frozen;        // WE applied the pin (vs merely recorded)
            public bool released;      // handed back; do not touch again
        }

        /// How far from the authored position we will accept a navmesh sample.
        /// Deliberately about a metre: that is "the floor directly under this
        /// prop", not "the nearest navmesh anywhere", which is the failure
        /// being fixed.
        private const float SeatSearchRadius = 1f;

        /// How long to keep retrying a seat after the park unlocks before
        /// giving up and leaving the agent pinned.
        private const float SeatTimeoutSeconds = 5f;

        private readonly List<AgentState> _states = new List<AgentState>();
        private bool _suspended = false;
        private bool _suspendedInBuildMode = false;
        private bool _suspendedDuringParkLoad = false;
        private float _seatDeadline = -1f;
        private bool _gaveUpSeating = false;

        // Mirrors LevelAnchor.isBuildMode / BuildModeObjectController.isBuildMode.
        // NativeInterfaceManager is unqualified on purpose — it lives in the
        // global namespace, and `DreamPark.X` inside `namespace DreamPark`
        // resolves to class DreamPark (CS0117).
        //
        // The SDK has no NativeInterfaceManager and no build mode: a creator in
        // the editor is always "playing". Constant false is therefore the
        // correct answer there, not a degraded one — every spawn releases at
        // the end of the spawn block via ReleaseIfPlayMode, exactly as it does
        // in core play mode.
        private static bool IsBuildMode {
            get {
#if DREAMPARKCORE
                return NativeInterfaceManager.Instance != null && NativeInterfaceManager.Instance.buildMode;
#else
                return false;
#endif
            }
        }

        // isStopped both READS and WRITES warn when the agent is not active on a
        // navmesh, so every access is gated on this.
        private static bool CanTouchStopFlag(NavMeshAgent agent)
        {
            return agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
        }

        /// <summary>
        /// Pin every NavMeshAgent under <paramref name="root"/> so it cannot write
        /// its stale internal position onto the transform, remembering the state
        /// each one was in. IDEMPOTENT: a second call never overwrites the
        /// ORIGINAL recorded state — LevelAnchor suspends at spawn and
        /// BuildModeObjectController suspends again on its first Update, and the
        /// second pass must not record the PINNED values or the agent would never
        /// navigate again. Returns false (and adds no component) when the object
        /// has no agents at all.
        /// </summary>
        public static bool Suspend(GameObject root)
        {
            if (root == null) return false;

            var agents = root.GetComponentsInChildren<NavMeshAgent>(true);
            if (agents == null || agents.Length == 0) return false;

            var guard = root.GetComponent<NavAgentPlacementGuard>();
            if (guard == null) guard = root.AddComponent<NavAgentPlacementGuard>();
            guard.SuspendAgents(agents);
            return true;
        }

        /// <summary>
        /// Warp every agent this guard pinned onto its authored transform, then
        /// hand back the recorded flags. Safe on any object — a no-op when
        /// nothing was suspended (or when the object never had agents).
        /// </summary>
        public static void Release(GameObject root)
        {
            if (root == null) return;
            var guard = root.GetComponent<NavAgentPlacementGuard>();
            if (guard == null) return;
            // An explicit release is a fresh attempt: clear the give-up latch so
            // a caller that knows the ground now exists gets a real retry.
            guard._gaveUpSeating = false;
            guard._seatDeadline = -1f;
            if (!guard.ReleaseAgents()) guard.enabled = true;
        }

        /// <summary>
        /// Release unless the creator is still arranging. The spawn paths call
        /// this once the authored transform is written: in play mode the agent
        /// is re-seated immediately (the Warp fix), in build mode it stays
        /// pinned until the build→play edge.
        /// </summary>
        public static void ReleaseIfPlayMode(GameObject root)
        {
            if (IsBuildMode) return;
            // A park load has no ground yet — see the header. The freeze is
            // released on the unlock edge by Update instead.
            if (ParkBuilder.LevelObjectManager.ParkContentLocked) return;
            Release(root);
        }

        private void SuspendAgents(NavMeshAgent[] agents)
        {
            for (int i = 0; i < agents.Length; i++) {
                var agent = agents[i];
                if (agent == null) continue;

                int recorded = IndexOf(agent);
                if (recorded >= 0 && _states[recorded].frozen) {
                    // Already pinned by us. Do NOT re-record — that would store
                    // the pinned values and the agent would never navigate again.
                    continue;
                }

                bool stopLegible = CanTouchStopFlag(agent);
                var state = new AgentState {
                    agent = agent,
                    wasEnabled = agent.enabled,
                    updatePosition = agent.updatePosition,
                    updateRotation = agent.updateRotation,
                    hadStopFlag = stopLegible,
                    isStopped = stopLegible && agent.isStopped,
                    frozen = false,
                };

                // A prefab that ships its agent disabled is left exactly as it
                // is — that is a gameplay decision, not our snap window. It is
                // still RECORDED, so if gameplay enables it before release we
                // pick up its real state on the next Suspend pass.
                if (agent.enabled) {
                    // updatePosition is the one that actually drags the prop.
                    // updateRotation is the same story for orientation.
                    agent.updatePosition = false;
                    agent.updateRotation = false;
                    // Park it where it stands so it cannot consume a path while
                    // pinned. Only legible on a live, on-mesh agent.
                    if (stopLegible) agent.isStopped = true;
                    state.frozen = true;
                    _suspended = true;
                }

                if (recorded >= 0) {
                    _states[recorded] = state;
                } else {
                    _states.Add(state);
                }
            }

            // Arm the backstop ONLY for a build-mode freeze. A play-mode spawn is
            // released by ReleaseIfPlayMode at the end of the spawn block, and
            // LevelAnchor.Spawn has an `await UniTask.SwitchToMainThread()`
            // between the two. That await does not yield today (everything before
            // it is main-thread-only, so it is already complete) — but if a
            // refactor ever made it yield, an unarmed Update() would win the race,
            // release while the prop still sits at the PREFAB pose, and clear
            // _states so the real ReleaseIfPlayMode became a silent no-op. That
            // would reinstate the exact snap bug this file exists to fix, with no
            // error to notice. Never let Update() release a play-mode freeze.
            if (_suspended) {
                _suspendedInBuildMode = IsBuildMode;
                // Armed the same way, for the same reason: something other than
                // the spawn block has to own the release, because the spawn
                // block runs before the ground exists.
                _suspendedDuringParkLoad = ParkBuilder.LevelObjectManager.ParkContentLocked;
            }

            // Only pay for an Update while we are actually holding agents down.
            enabled = _suspended;
        }

        /// <summary>
        /// Hand every pinned agent back — but ONLY once it can actually be
        /// seated at the transform the creator authored. Returns true when
        /// nothing is left pinned.
        /// </summary>
        private bool ReleaseAgents()
        {
            bool allDone = true;

            for (int i = 0; i < _states.Count; i++) {
                var state = _states[i];
                var agent = state.agent;

                if (agent == null || state.released) continue;

                // Shipped disabled, or we never pinned it — leave it alone.
                if (!state.wasEnabled || !state.frozen) {
                    state.released = true; _states[i] = state; continue;
                }

                try {
                    // We never disabled the agent, so if it is disabled NOW that was
                    // gameplay's call (EasyEnemy on death, EasyThrow mid-flight).
                    // Restoring the flags is right; resurrecting the agent is not,
                    // and there is nothing to seat.
                    if (!agent.enabled) {
                        agent.updatePosition = state.updatePosition;
                        agent.updateRotation = state.updateRotation;
                        state.released = true; _states[i] = state;
                        continue;
                    }

                    if (!TrySeat(agent)) {
                        // No navmesh under where this prop was placed — the floor
                        // it belongs on does not exist yet, or never will. Handing
                        // updatePosition back now is exactly what drags it away, so
                        // it stays pinned and we try again next frame.
                        allDone = false;
                        continue;
                    }

                    agent.updatePosition = state.updatePosition;
                    agent.updateRotation = state.updateRotation;

                    // Only hand back a stop flag we were actually able to read, and
                    // only while it is legible again (the seat above re-binds the
                    // agent, so this is normally true).
                    if (state.hadStopFlag && CanTouchStopFlag(agent)) {
                        agent.isStopped = state.isStopped;
                    }

                    state.released = true; _states[i] = state;

                } catch (System.Exception e) {
                    // One bad agent must not strand the others pinned forever, and
                    // must not make Update re-enter the same throwing record every
                    // frame.
                    Debug.LogException(e, this);
                    state.released = true; _states[i] = state;
                }
            }

            if (allDone) {
                _states.Clear();
                _suspended = false;
                _suspendedInBuildMode = false;
                _suspendedDuringParkLoad = false;
                _seatDeadline = -1f;
                _gaveUpSeating = false;
                enabled = false;
            }
            return allDone;
        }

        /// <summary>
        /// Bind the agent's internal nav position to the transform the creator
        /// authored. Without this the agent is still bound to whatever navmesh
        /// point it snapped to back at Instantiate time and updatePosition
        /// would drag the prop straight back there.
        ///
        /// Warp's RETURN VALUE is the whole point — the old code discarded it.
        /// False means there is no navmesh where this object was placed, which
        /// is the state in which restoring updatePosition teleports it.
        /// </summary>
        private static bool TrySeat(NavMeshAgent agent)
        {
            Vector3 authored = agent.transform.position;

            if (agent.Warp(authored)) return true;

            // Near miss: accept the floor within a metre. Anything further and
            // we are guessing, which is the behaviour being fixed.
            if (NavMesh.SamplePosition(authored, out var hit, SeatSearchRadius, agent.areaMask)) {
                return agent.Warp(hit.position);
            }
            return false;
        }

        /// Bounded retry. Ground can arrive a frame or two late; it should not
        /// arrive five seconds late, and spinning forever would burn a frame
        /// callback for the rest of the session.
        private void TickSeatRetry()
        {
            if (_gaveUpSeating) { enabled = false; return; }

            if (_seatDeadline < 0f) _seatDeadline = Time.realtimeSinceStartup + SeatTimeoutSeconds;
            if (Time.realtimeSinceStartup < _seatDeadline) return;

            _gaveUpSeating = true;
            enabled = false;

            int stuck = 0;
            for (int i = 0; i < _states.Count; i++) {
                if (!_states[i].released && _states[i].frozen) stuck++;
            }

            Debug.LogWarning(string.Format(
                "[NavAgentPlacementGuard] {0} agent(s) on '{1}' could not be placed on a NavMesh at " +
                "their spawn position within {2:F0}s, so they are staying pinned. They will hold "  +
                "position instead of navigating — which is deliberate: releasing them would bind "   +
                "them to the nearest navmesh somewhere else and visibly teleport the object. Check " +
                "that this object was placed on a floor that bakes navmesh.",
                stuck, name, SeatTimeoutSeconds), this);
        }

        private int IndexOf(NavMeshAgent agent)
        {
            for (int i = 0; i < _states.Count; i++) {
                if (_states[i].agent == agent) return i;
            }
            return -1;
        }

        // Backstop for objects that carry no BuildModeObjectController (the
        // controller drives the mode edge for everything else — see its
        // Toggle). Only ever runs while suspended: ReleaseAgents switches this
        // component off, so a released prop costs literally zero per frame, and
        // an agent-less prop never gets the component in the first place.
        private void Update()
        {
            if (!_suspended) {
                enabled = false;
                return;
            }

            // BUILD-MODE freeze: released by the build->play edge.
            if (_suspendedInBuildMode) {
                if (IsBuildMode) return;
                if (!ReleaseAgents()) TickSeatRetry();
                return;
            }

            // PARK-LOAD freeze: released on the unlock edge, which the loader
            // raises only once the floors are calibrated and the gap mesh
            // exists. Before that there is no correct navmesh to seat onto.
            if (_suspendedDuringParkLoad) {
                if (ParkBuilder.LevelObjectManager.ParkContentLocked) return;
                if (!ReleaseAgents()) TickSeatRetry();
                return;
            }

            // Plain play-mode freeze: ReleaseIfPlayMode owns it, and Update must
            // never pre-empt it — see SuspendAgents.
        }
    }
}
