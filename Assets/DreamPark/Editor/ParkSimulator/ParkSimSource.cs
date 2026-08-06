// ─────────────────────────────────────────────────────────────────────
//  ParkSimSource.cs — where the park comes from
//
//  The simulator was built around one park: park.fbx, its Empty markers,
//  and a synthetic layout shuffled by a seed. That is the right park when
//  you have nothing else, and it is the wrong one the moment a real park
//  document is available — the venue you are about to ship into has its
//  own attractions, its own spacing, its own scanned floors, and none of
//  that is reproduced by seventeen markers on a hillside.
//
//  So the park became pluggable. A ParkSimParkSource supplies the park;
//  everything else the simulator does — the overlay, the report, Go and
//  Patrol, the viewpoint restore, OptimizedAF, the load ladder for
//  anything the simulator itself places — is unchanged and unaware of
//  where the park came from. Loading a real park is not a competing
//  feature any more, it is a different SOURCE for the same simulation.
//
//  THE SDK NEVER KNOWS WHAT A REAL PARK IS. There is deliberately no
//  reference here to ParkAnchor, to a loader, or to any backend: the SDK
//  ships to creators who have no park documents at all. The host app
//  (dreampark-core) implements a source and registers it. If nothing
//  registers one, the simulator builds park.fbx exactly as before.
//
//  A SOURCE OWNS ITS OWN LOADING. Build is a coroutine run inside the
//  simulator's generate routine, so a source that needs thirty seconds of
//  network gets them without the simulator polling or timing out on its
//  behalf. It reports failure through the context rather than throwing,
//  because a park that failed to load still deserves an overlay saying so.
//
//  A SOURCE DOES NOT CALIBRATE. Objects a source spawns are the source's
//  business — the simulator marks them not-owned and keeps its hands off
//  their floors. It calibrates only what it placed itself.
// ─────────────────────────────────────────────────────────────────────

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.ParkSim
{
    /// <summary>
    /// Handed to <see cref="ParkSimParkSource.Build"/>. Carries the things a
    /// source needs from the simulator and the one thing the simulator needs
    /// back that a coroutine cannot return.
    /// </summary>
    public class ParkSimSourceContext
    {
        /// The simulator's root. A source SHOULD parent whatever it creates
        /// under this so teardown and regenerate dispose of it with everything
        /// else — but it is not required to, and a source that manages its own
        /// lifetime overrides Teardown instead.
        public Transform root;

        /// The generation seed, for sources that want their own variation.
        public int seed;

        /// Warnings surfaced in the overlay and the console. Same list the
        /// rest of the generation writes to.
        public List<string> notes;

        public bool Failed { get; private set; }
        public string FailureReason { get; private set; }

        public ParkSimSourceContext(Transform root, int seed, List<string> notes)
        {
            this.root = root;
            this.seed = seed;
            this.notes = notes;
        }

        /// <summary>
        /// Abandon this generation. The simulator stops before placing
        /// anything and the overlay shows the reason — which is far more use
        /// than an empty park and a console exception.
        /// </summary>
        public void Fail(string reason)
        {
            Failed = true;
            FailureReason = string.IsNullOrEmpty(reason) ? "The park source failed." : reason;
        }
    }

    /// <summary>
    /// Supplies the park the simulator drops content into.
    ///
    /// The default (no source registered) is the synthetic park built from
    /// park.fbx. A host app registers a source with
    /// <see cref="ParkSimulator.SetParkSource"/> to swap that out.
    /// </summary>
    public abstract class ParkSimParkSource
    {
        /// Shown in the overlay. Keep it short — "Wizards Way", not a GUID.
        public abstract string DisplayName { get; }

        /// Optional second line in the overlay: revision, object count, id.
        public virtual string Detail { get { return null; } }

        /// <summary>
        /// True when this source puts an ARMesh-layer collider in the world for
        /// fresh calibration to raycast against.
        ///
        /// A real park's objects carry floor data baked against a venue scan we
        /// do not have, so a real-park source answers FALSE and the simulator
        /// warns that anything IT places will sit flat rather than silently
        /// producing a wrong-looking floor.
        /// </summary>
        public virtual bool ProvidesGroundMesh { get { return false; } }

        /// <summary>
        /// Build the park. Runs after the content lock has been reset and
        /// OptimizedAF is up, and before the simulator places anything of its
        /// own — so a source is free to run the whole shipping load ladder,
        /// including its own BeginParkLoad/EndParkLoad pair.
        /// </summary>
        public abstract IEnumerator Build(ParkSimSourceContext context);

        /// <summary>
        /// Where the simulator may place content of its own — the attraction
        /// you are working on, and anything injected through
        /// <see cref="ParkSimExternalContent"/>. Called after Build.
        ///
        /// Return an empty list to say "this park is full"; the simulator will
        /// say so in the overlay rather than stacking things at the origin.
        /// </summary>
        public abstract List<SpawnPoint> CollectSpawnPoints(int seed, List<string> notes);

        /// <summary>
        /// Add what the source spawned to the report, so the overlay lists it
        /// and Go/Patrol/viewpoint can fly to it. Items added here MUST leave
        /// <see cref="PlacedItem.simulatorOwned"/> false — the simulator does
        /// not calibrate, register or cache floors for content it did not
        /// place.
        /// </summary>
        public virtual void Describe(ParkSimReport report) { }

        /// <summary>
        /// Called before the simulator destroys its root — which is to say
        /// before EVERY rebuild, not only at the end. A source is expected to
        /// be built and torn down repeatedly on the same instance, because
        /// that is exactly what Regenerate means for a real park: load it
        /// again.
        ///
        /// Must be safe to call when nothing was ever built.
        /// </summary>
        public virtual void Teardown() { }
    }
}
