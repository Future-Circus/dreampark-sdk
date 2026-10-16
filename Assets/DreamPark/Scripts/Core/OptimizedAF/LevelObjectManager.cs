using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Threading;
using UnityEngine.Analytics;
using TMPro;
using SuperAdventureLand;

namespace DreamPark.ParkBuilder {
    public class LevelObject {
        public GameObject gameObject;
        public ColliderSettings[] colliders;
        public RigidbodySettings[] rigidbodies;
        public ComponentSettings[] components;
        // ComponentSettings, not raw Animator[]: `animator.enabled = enabled` wrote
        // the LevelObject's flag straight onto the component with nothing snapshotted,
        // so an Animator authored disabled started playing after the first restore.
        // Animators are excluded from `components` below so nothing toggles twice.
        public ComponentSettings[] animators;
        public RendererSettings[] renderers;
        public ParticleSystemSettings[] particleSystems;
        public bool isRendererDisabled = false;
        public bool enabled = true;
        public string name;
        public string tag;
        public int layer;
        public bool isPriority = false;
        public bool ignoreOptimization = false;
        // WHY ignoreOptimization is set (Oct 2026), so dp.optimizer_ignore(go, false) can hand
        // back only what Lua asked for. ignoredByPlatform = the constructor's argument (an
        // OptimizedAFIgnore component: Game Manager, floors, transitions) and is never undone
        // from Lua; ignoredByLua = a "-- @optimizer-ignore" script or dp.optimizer_ignore.
        internal bool ignoredByPlatform = false;
        internal bool ignoredByLua = false;
        internal string ignoredByLuaTitle;
        public Bounds? _bounds = null;
        public bool? forceDisabled = null;

        // The AABB used for distance culling. It used to be computed once, on first
        // read, and cached forever — so anything the game MOVED after registration
        // went on being culled against the box it occupied when it spawned. Recompute
        // when the root has actually moved: walking every renderer is the expensive
        // part, a Vector3 compare is not, so static content (the common case) still
        // pays the walk exactly once.
        //
        // Known limit: a root that stays put while its CHILDREN animate far away
        // (a long swinging arm) still reads its original box.
        // 10 cm. The threshold can be this tight because the RE-WALK RATE is already
        // capped by the cull sweep itself: OptimizedAF runs every `frameInterval`
        // FixedUpdates (30 in the Release settings, so ~1.7 sweeps/second), and
        // renderBounds is read exactly once per object per sweep. A continuously
        // moving object therefore re-walks its renderers at most ~1.7x/second no
        // matter how fast it travels, and a static one — nearly all park content —
        // never re-walks at all after the first read.
        private Vector3 _boundsOrigin;
        private bool _boundsValid = false;
        private const float BoundsRecomputeThresholdSqr = 0.01f;   // 10 cm

        public Bounds renderBounds {
            get {
                var root = transform;
                if (_boundsValid && _bounds != null && root != null &&
                    (root.position - _boundsOrigin).sqrMagnitude <= BoundsRecomputeThresholdSqr)
                    return (Bounds)_bounds;

                // Particle-only (or currently inactive) objects have no stable
                // non-particle renderer bounds. An empty Bounds() is centered at
                // world origin, so distance culling would park an effect at a
                // distant venue even while the player is standing beside it.
                Vector3 fallbackCenter = root != null ? root.position : Vector3.zero;
                if (renderers == null || renderers.Length == 0)
                    return new Bounds(fallbackCenter, Vector3.zero);

                bool init = false;
                Bounds combined = new Bounds();

                for (int i = 0; i < renderers.Length; i++) {
                    var holder = renderers[i];
                    if (holder == null || holder.renderer == null || holder.renderer.IsDestroyed())
                        continue;

                    var r = holder.renderer;

                    // Skip types that commonly have huge/loose bounds
                    if (r is TrailRenderer || r is LineRenderer || r is ParticleSystemRenderer)
                        continue;

                    // Grab world AABB for this renderer
                    Bounds b = r.bounds;

                    // Ignore zero/invalid bounds
                    if (b.size.sqrMagnitude <= 0f) continue;

                    if (!init) {
                        combined = new Bounds(b.center, b.size);
                        init = true;
                    } else {
                        combined.Encapsulate(b);
                    }
                }

                if (!init) combined = new Bounds(fallbackCenter, Vector3.zero);

                _bounds = combined;
                _boundsOrigin = root != null ? root.position : Vector3.zero;
                _boundsValid = true;
                return combined;
            }
        }
        public class RigidbodySettings {
            public Rigidbody rigidbody;
            public Joint joint;
            public bool isKinematic;
            public bool detectCollisions;
            public bool useGravity;
            public Vector3 linearVelocity;
            public Vector3 angularVelocity;

            public RigidbodySettings(Rigidbody rigidbody) {
                this.rigidbody = rigidbody;
                isKinematic = rigidbody.isKinematic;
                detectCollisions = rigidbody.detectCollisions;
                useGravity = rigidbody.useGravity;
                linearVelocity = rigidbody.linearVelocity;
                angularVelocity = rigidbody.angularVelocity;
                joint = rigidbody.GetComponent<Joint>();
            }

            public bool Toggle(bool enabled) {
                if (rigidbody == null || rigidbody.IsDestroyed()) {
                    return false;
                }
                if (enabled) {
                    rigidbody.isKinematic = isKinematic;
                    rigidbody.detectCollisions = detectCollisions;
                    rigidbody.useGravity = useGravity;

                    // Momentum is restored AFTER isKinematic is back to its real
                    // value, and the test is on the SAVED field, not the live one.
                    // Parking forces isKinematic = true, so the old `if
                    // (!rigidbody.isKinematic)` here could never be true on the only
                    // path that reaches it — every physics prop silently lost all
                    // momentum the first time the player walked away and back.
                    if (!isKinematic) {
                        rigidbody.linearVelocity = linearVelocity;
                        rigidbody.angularVelocity = angularVelocity;
                    }
                } else {
                    if (joint != null && joint is CharacterJoint characterJoint) {
                        characterJoint.enableProjection = true;
                    }
                    //save the current state
                    isKinematic = rigidbody.isKinematic;
                    detectCollisions = rigidbody.detectCollisions;
                    useGravity = rigidbody.useGravity;
                    linearVelocity = rigidbody.linearVelocity;
                    angularVelocity = rigidbody.angularVelocity;

                    if (!rigidbody.isKinematic) {
                        rigidbody.linearVelocity = Vector3.zero;
                        rigidbody.angularVelocity = Vector3.zero;
                    }
                    rigidbody.isKinematic = true;
                    rigidbody.detectCollisions = false;
                    rigidbody.useGravity = false;
                    //rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                    //rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                }
                return true;
            }
        }
        public Transform transform {
            get {
                if (gameObject != null && gameObject.transform != null) {
                    return gameObject.transform;
                }
                return null;
            }
        }

        public T GetComponent<T>() where T : Component {
            if (gameObject != null) {
                return gameObject.GetComponent<T>();
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        //  THE RE-READ RULE (July 2026)
        //
        //  Every Settings class below snapshots one component so the optimizer can
        //  park it and put it back. The snapshot used to be taken once, in the
        //  constructor, and never refreshed — so anything the GAME changed at
        //  runtime was reverted the next time the player walked out of range and
        //  back. `col.enabled = false` to hide a collected pickup came back solid.
        //  A material assigned in Lua reverted to the prefab's. A script that
        //  disabled itself was switched back on.
        //
        //  None of it happened in a hand-authored scene, because nothing registers
        //  a scene prefab with LevelObjectManager — so this was the last place
        //  where "works in my scene" and "works in a park" genuinely disagreed.
        //
        //  The rule: re-read live state on the way OUT (the parking transition),
        //  restore it on the way back IN, and touch nothing while the object is
        //  live. RigidbodySettings already did exactly this; the rest now match.
        //
        //  Each Settings object tracks its own `parked` flag rather than trusting
        //  the caller, so a repeated Toggle in the same direction cannot overwrite
        //  a good snapshot with the parked values it just wrote.
        //
        //  KNOWN LIMIT, and it is deliberate: a change made to an object WHILE it
        //  is parked is not observed. Gameplay scripts on a parked object are
        //  themselves disabled, so the only way to hit this is to write to a
        //  culled object from somewhere else — and polling every component every
        //  frame to catch that would cost more than it saves.
        //
        //  OPT-IN WAY ROUND IT (Oct 2026): dp.enabled(c) / dp.set_enabled(c, on)
        //  (LevelObjectManager.TryGetIntendedEnabled / SetIntendedEnabled). While
        //  a component is parked they read and write the SNAPSHOT below instead of
        //  the live flag, so a read sees what the unpark will restore and a write
        //  lands at the unpark. Nothing here changes for content that never calls
        //  them: the members they use (IsParked, IntendedEnabled,
        //  SetIntendedEnabled) only read or write these same fields.
        //
        //  INVARIANT this depends on: a component appears in exactly ONE of the
        //  arrays below (see the LevelObject constructor). Two arrays toggling the
        //  same component would make the second read see what the first just
        //  wrote. Same reason RegisterLevelObject now refuses to build a second
        //  LevelObject over a GameObject it already tracks.
        // ─────────────────────────────────────────────────────────────
        public class ColliderSettings {
            public Collider collider;
            public bool enabled;
            private bool parked = false;

            public ColliderSettings(Collider collider) {
                this.collider = collider;
                enabled = collider.enabled;
            }

            public bool Toggle(bool enabled) {
                if (collider == null || collider.IsDestroyed()) {
                    return false;
                }
                if (enabled) {
                    if (parked) {
                        collider.enabled = this.enabled;
                        parked = false;
                    }
                } else {
                    if (!parked) {
                        this.enabled = collider.enabled;
                        parked = true;
                    }
                    collider.enabled = false;
                }
                return true;
            }

            /// <summary>True while the optimizer holds this collider parked.</summary>
            public bool IsParked => parked;
            /// <summary>What the game intends: the snapshot the unpark restores while parked, else live.</summary>
            public bool IntendedEnabled => parked ? enabled : collider.enabled;
            /// <summary>While parked, write the snapshot (applied at unpark; stays off meanwhile); else live.</summary>
            public void SetIntendedEnabled(bool on) {
                if (parked) enabled = on;
                else collider.enabled = on;
            }
        }

        public class ParticleSystemSettings {
            public ParticleSystem particleSystem;
            public bool enabled;
            private bool parked = false;
            private bool firstPark = true;
            // Its own flag, NOT folded into `parked`. `parked` keeps its exact
            // meaning (and its firstPark interaction); `posed` records only that
            // the last park froze a representative frame instead of clearing the
            // system, so the restore path knows it is holding a stale simulation
            // it has to throw away rather than resume.
            private bool posed = false;

            // Ceiling on the pose simulation. Simulate() is a synchronous catch-up
            // of the entire system, so an emitter with a long startLifetime would
            // otherwise stall the frame the creator enters build mode on.
            private const float MaxPoseSeconds = 5f;

            // Frame these settings were made (= the object's spawn/registration).
            // playOnAwake is only a PREDICTION of "Unity is about to start this",
            // valid until Unity has had a frame to act on it. A first park long
            // after spawn (an object registered at load and first culled minutes
            // later) must read the live state, or a system the game stopped
            // replays on the first re-approach (Oct 2026 watch item).
            private readonly int createdFrame;
            public static int FrameOverride = -1;     // tests only; -1 = Time.frameCount
            internal static int CurrentFrame => FrameOverride >= 0 ? FrameOverride : Time.frameCount;
            // Spawn frame + the next one: playOnAwake systems start on their first enabled frame.
            internal const int PlayOnAwakeTrustFrames = 1;

            public ParticleSystemSettings(ParticleSystem particleSystem) {
                this.particleSystem = particleSystem;
                enabled = particleSystem.isPlaying || particleSystem.main.playOnAwake;
                createdFrame = CurrentFrame;
            }

            /// <summary>What the next park would record as "playing" (tests / diagnostics).</summary>
            public bool ReadPlayingForPark() {
                bool trustPlayOnAwake = firstPark && CurrentFrame - createdFrame <= PlayOnAwakeTrustFrames;
                return trustPlayOnAwake
                    ? (particleSystem.isPlaying || particleSystem.isPaused || particleSystem.main.playOnAwake)
                    : (particleSystem.isPlaying || particleSystem.isPaused);
            }

            // Build mode is a STAGING view, not a paused game. An attraction's
            // ambient VFX — torch fire, waterfall mist, fog — is frequently the
            // thing that makes the space read at all, and clearing it leaves the
            // creator arranging an empty room. A frozen representative frame is
            // strictly better. A frozen EXPLOSION hanging in mid-air is not: a
            // one-shot posed mid-burst looks like a bug, so those still clear.
            //
            // NOTE: gating on main.prewarm was considered and REJECTED. Prewarm is
            // off by default, is only legal on looping systems, and essentially no
            // creator sets it — the predicate would pose almost nothing. It is read
            // below for one thing only: picking the simulate duration.
            private bool ShouldPose =>
                   particleSystem.gameObject.activeInHierarchy
                && particleSystem.main.playOnAwake   // starts itself; nothing else has to fire it
                && particleSystem.main.loop;         // persistent effect, not a one-shot

            public bool Toggle(bool enabled) {
                if (particleSystem == null || particleSystem.IsDestroyed()) {
                    return false;
                }
                if (enabled) {
                    if (parked) {
                        // A posed system is RESTARTED, never resumed. Play() after
                        // Simulate() continues from the simulated state, so the first
                        // second of gameplay would render a snapshot frozen back when
                        // the creator was still arranging the park. Same rule the Dream
                        // Sequence transition is written around: StopEmittingAndClear
                        // before Play(), because a looping system that thinks it is
                        // already playing makes Play() a no-op.
                        if (posed) {
                            particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                            posed = false;
                        }
                        if (this.enabled) particleSystem.Play();
                        parked = false;
                    }
                } else {
                    if (!parked) {
                        // First park can land in the same frame as the spawn, before
                        // Unity has actually started a playOnAwake system — so trust
                        // playOnAwake that once. After that isPlaying is the truth,
                        // which is what lets a system the game STARTED survive a cull
                        // and a one-shot burst that already finished stay finished.
                        //
                        // isPaused is read alongside it because a POSED system is
                        // paused, and isPlaying is false while it is. Without that
                        // term, a system posed for build mode and re-read on a later
                        // park records enabled=false and never plays again — particles
                        // that work the first time you enter build mode and never after.
                        //
                        // "Once" means the spawn frame: a FIRST park that comes later
                        // (registered at load, culled minutes after) reads the live
                        // state too — see createdFrame.
                        this.enabled = ReadPlayingForPark();
                        firstPark = false;
                        parked = true;
                    }
                    if (LevelObjectManager.BuildModeParked && ShouldPose) {
                        var m = particleSystem.main;
                        float t = m.prewarm ? m.duration                                  // match Unity's own prewarm semantics
                                            : Mathf.Min(m.duration, m.startLifetime.constantMax);
                        // withChildren MUST be false. The LevelObject constructor builds
                        // a FLAT GetComponentsInChildren<ParticleSystem>(true) array with
                        // one ParticleSystemSettings per system, so every nested system
                        // gets its own Toggle. The Stop()/Play() calls around this default
                        // to withChildren:true, which is redundant-but-idempotent — a
                        // second Stop changes nothing. Simulate is NOT idempotent: a
                        // system nested three deep would be simulated four times and drift
                        // out of phase with the parent it is supposed to match.
                        particleSystem.Simulate(Mathf.Clamp(t, 0.05f, MaxPoseSeconds), false, true, true);
                        // Simulate() leaves the system PAUSED: particles stay on screen,
                        // nothing advances.
                        posed = true;
                    } else {
                        particleSystem.Stop();
                        particleSystem.Clear();
                        posed = false;
                    }
                }
                return true;
            }
        }

        public class ComponentSettings {
            public Component component;
            public bool enabled = true;
            private static readonly Type[] ProtectedComponentTypes = new Type[] {
                typeof(MusicArea), typeof(GameArea), typeof(PlayerRig), typeof(TMP_Text), typeof(HandTracker),
                typeof(TextMeshProUGUI), typeof(TextMeshPro),
                typeof(LevelTemplate), typeof(PropTemplate), typeof(CalibrateLevel), typeof(CalibrateProp),
                typeof(DepthMask), typeof(FloorAnchor), typeof(FloorCutout)
            #if DREAMPARKCORE
                , typeof(PortalAnchor), typeof(ParkAnchor), typeof(LevelAnchor), typeof(DreamBand)
            #endif
            };

            private bool parked = false;

            /// <summary>Exempt from parking (and from the game pause's freeze).</summary>
            public bool IsProtected => isProtected;

            // Whether this component type is exempt from parking. Computed ONCE.
            //
            // This test used to run inside Toggle: a LINQ Any() over 19 entries doing
            // reflection IsAssignableFrom on every component, on every transition,
            // forever — and a component's type cannot change at runtime, so every one
            // of those calls after the first was recomputing a constant. Reflection
            // type tests are among the slowest things you can put in a per-object loop
            // on a Quest.
            private readonly bool isProtected;

            public ComponentSettings(Component component) {
                this.component = component;
                var componentType = component != null ? component.GetType() : null;
                isProtected = componentType != null &&
                              ProtectedComponentTypes.Any(type => type.IsAssignableFrom(componentType));
                // Behaviour, not MonoBehaviour. Toggle always WROTE plain Behaviours
                // (Light, AudioSource, Camera, AudioListener, Animator) but the
                // constructor only READ MonoBehaviours, so anything else kept the
                // `true` field default — and a component the creator shipped disabled
                // in their prefab switched itself ON the first time the optimizer
                // restored the object. Park-only, and invisible in the Inspector.
                if (component is Behaviour bh) {
                    enabled = bh.enabled;
                }
            }

            public bool Toggle(bool enabled = true, OptimizationSettings settings = null) {
                if (component == null || component.IsDestroyed()) {
                    return false;
                }
                if (isProtected) {
                    return false;
                }
                var behaviour = component as Behaviour;
                if (behaviour == null) {
                    return true;   // Collider/Rigidbody/Renderer: managed elsewhere
                }
                if (enabled) {
                    if (parked) {
                        behaviour.enabled = this.enabled;
                        parked = false;
                    }
                } else {
                    if (!parked) {
                        this.enabled = behaviour.enabled;
                        parked = true;
                    }
                    behaviour.enabled = false;
                }
                return true;
            }

            /// <summary>True while the optimizer holds this behaviour parked (only Behaviours ever park).</summary>
            public bool IsParked => parked;
            /// <summary>What the game intends: the snapshot the unpark restores while parked, else live.</summary>
            public bool IntendedEnabled => parked ? enabled : (component is Behaviour b && b.enabled);
            /// <summary>While parked, write the snapshot (applied at unpark; stays off meanwhile); else live.</summary>
            public void SetIntendedEnabled(bool on) {
                if (parked) enabled = on;
                else if (component is Behaviour b) b.enabled = on;
            }
        }
        public class RendererSettings {
            public Renderer renderer;
            // Original state
            private readonly Material[] originalSharedMats;
            private Material optimizedMaterial;
            private Material[] optimizedSharedMats; // same length as originalSharedMats
            private Material[] liveSharedMats;       // what the GAME had before parking
            private bool parked = false;
            // The level of the last park (1 = simplified, still drawn; 2 = hidden). Only
            // SetIntendedEnabled reads it, to apply the same visibility rule Toggle does.
            private int parkedLevel = 0;
            public bool enabled;
            public bool enableOptimizedMaterial = false;
            public RendererSettings(Renderer renderer) {
                this.renderer = renderer;
                enabled = this.renderer.enabled;

                originalSharedMats = renderer.sharedMaterials;

                // Build one optimized mat from the first slot (if any)
                var src = (originalSharedMats != null && originalSharedMats.Length > 0)
                        ? originalSharedMats[0]
                        : null;

                if (src)
                {
                    optimizedMaterial = new Material(src) { name = src.name + " (Optimized)" };
                    
                    if (!optimizedMaterial) return;

                    // Common Shader Graph / URP switches
                    // Surface type: 0 = Opaque, 1 = Transparent
                    if (optimizedMaterial.HasProperty("_Surface")) optimizedMaterial.SetFloat("_Surface", 0f);
                    // AlphaClip on
                    if (optimizedMaterial.HasProperty("_AlphaClip")) optimizedMaterial.SetFloat("_AlphaClip", 1f);
                    if (optimizedMaterial.HasProperty("_Cutoff")) optimizedMaterial.SetFloat("_Cutoff", Mathf.Clamp01(optimizedMaterial.GetFloat("_Cutoff"))); // keep existing cutoff
                    if (optimizedMaterial.HasProperty("_AlphaClipThreshold")) optimizedMaterial.SetFloat("_AlphaClipThreshold", optimizedMaterial.HasProperty("_Cutoff") ? optimizedMaterial.GetFloat("_Cutoff") : 0.4f);

                    // Disable blending, write depth, normal opaque queue
                    optimizedMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    optimizedMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    optimizedMaterial.SetInt("_ZWrite", 1);
                    optimizedMaterial.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    optimizedMaterial.EnableKeyword("_ALPHATEST_ON"); // Shader Graph/URP uses this for clipped pass

                    // Culling: keep backface culling unless you truly need double-sided
                    if (optimizedMaterial.HasProperty("_Cull")) optimizedMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);

                    // Ensure it's rendered with opaque queue
                    optimizedMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry; // 2000

                    // Optional: Alpha-to-coverage if MSAA is enabled in URP (nice for foliage edges)
                    if (optimizedMaterial.HasProperty("_AlphaToMask")) optimizedMaterial.SetFloat("_AlphaToMask", 1f);

                    // Apply optimized copy to all slots (cheap array)
                    optimizedSharedMats = new Material[originalSharedMats.Length];
                    for (int i = 0; i < optimizedSharedMats.Length; i++) {
                        if (originalSharedMats[i] != null && originalSharedMats[i].name == "Occlusion") {
                            optimizedSharedMats[i] = originalSharedMats[i];
                        } else {
                            optimizedSharedMats[i] = optimizedMaterial;
                        }
                    }
                }
                else
                {
                    optimizedSharedMats = System.Array.Empty<Material>();
                }
            }

            public bool Toggle(int optimizationLevel = 0) {
                if (renderer == null || renderer.IsDestroyed()) {
                    return false;
                }
                bool park = optimizationLevel > 0;

                bool willSwap = optimizedSharedMats != null && optimizedSharedMats.Length > 0;

                if (park) {
                    if (!parked) {
                        // Capture what the GAME has right now, not what the prefab
                        // shipped with: a material assigned in Lua, or the
                        // renderer.enabled = false that hid a collected pickup.
                        //
                        // The sharedMaterials GETTER allocates a fresh Material[] on
                        // every call, so it is paid only when we are actually about to
                        // overwrite the slots. A renderer with no optimized variant is
                        // never touched and never allocates.
                        if (willSwap) liveSharedMats = renderer.sharedMaterials;
                        enabled = renderer.enabled;
                        parked = true;
                    }
                    if (willSwap) {
                        renderer.sharedMaterials = optimizedSharedMats;
                        enableOptimizedMaterial = true;
                    }
                    parkedLevel = optimizationLevel;
                    renderer.enabled = optimizationLevel != 2 && this.enabled;
                } else if (parked) {
                    // Only write the slots back if we actually changed them.
                    if (enableOptimizedMaterial) {
                        var restore = liveSharedMats ?? originalSharedMats;
                        if (restore != null && restore.Length > 0) {
                            renderer.sharedMaterials = restore;
                        }
                        enableOptimizedMaterial = false;
                    }
                    renderer.enabled = this.enabled;
                    parked = false;
                }
                // Live and staying live: leave the renderer exactly as the game left
                // it. The old code reassigned originalSharedMats and rewrote
                // renderer.enabled on EVERY in-range frame, which clobbered any
                // runtime change the instant it was made.
                return true;
            }

            /// <summary>True while the optimizer holds this renderer parked (hidden or simplified).</summary>
            public bool IsParked => parked;
            /// <summary>What the game intends: the snapshot the unpark restores while parked, else live.</summary>
            public bool IntendedEnabled => parked ? enabled : renderer.enabled;
            /// <summary>
            /// While parked, write the snapshot (applied at unpark) and keep the park's own
            /// visibility rule meanwhile: a simplified park (level 1) still draws an enabled
            /// renderer, a hidden park (level 2) draws nothing. Else live.
            /// </summary>
            public void SetIntendedEnabled(bool on) {
                if (parked) {
                    enabled = on;
                    renderer.enabled = parkedLevel != 2 && on;
                } else {
                    renderer.enabled = on;
                }
            }
        }
        public bool FrustumCullCheck(Plane[] frustumPlanes) {
            if (renderers == null || renderers.Length == 0) {
                return false;
            }
            foreach (var renderer in renderers) {
                if (renderer == null || renderer.renderer == null || renderer.renderer.IsDestroyed()) {
                    continue;
                }
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.renderer.bounds)) {
                    return false;
                }
            }
            return true;
        }
        public LevelObject(GameObject gameObject, bool isPriority = false, bool ignoreOptimization = false) {
            this.gameObject = gameObject;
            this.isPriority = isPriority;
            this.ignoreOptimization = ignoreOptimization;
            ignoredByPlatform = ignoreOptimization;
            name = gameObject.name;
            tag = gameObject.tag;
            layer = gameObject.layer;
            // includeInactive: a child that is inactive at registration used to be
            // invisible to the optimizer forever, so a script the game activated
            // later kept running through Build Mode and every park teardown.
            //
            // The Where() clause is load-bearing, not tidiness: each of these types
            // has its own Settings array with its own live-state re-read, and a
            // component sitting in two arrays would have the second toggle read back
            // whatever the first one just wrote. Renderer/Collider/Rigidbody were
            // never actually written by ComponentSettings (they are not Behaviours),
            // but excluding them keeps the invariant true by construction instead of
            // by coincidence.
            //
            // LuaRelay is excluded for the same reason, between a component and its
            // OWNER (Oct 2026). A LuaBehaviour/EasyLua adds its message relays
            // (lateupdate, fixedupdate, ontrigger*, oncollision*, onzone*, onready,
            // onpause/onresume …) at boot and keeps them in lockstep with itself: its
            // OnDisable turns them off, its OnEnable turns every one back on. When a
            // script boots BEFORE it is registered — every live spawn on device
            // (LiveParkContent / a release while objectsEnabled is true), never the
            // Editor's Park Simulator — the relays used to be snapshotted too. On park
            // the host was disabled first, so each relay's entry then re-read "off";
            // on unpark the host turned them on and their entries restored "off" right
            // after. The relays stayed dead for the rest of the session: no
            // lateupdate(), no fixedupdate(), no onzoneenter() (BirthdayBash's Cake
            // Boss rendered inside its own cake). The host is managed here like any
            // other Behaviour, so its relays still park and unpark — through it.
            colliders = gameObject.GetComponentsInChildren<Collider>(true).Select(c => new ColliderSettings(c)).ToArray();
            rigidbodies = gameObject.GetComponentsInChildren<Rigidbody>(true).Select(r => new RigidbodySettings(r)).ToArray();
            components = gameObject.GetComponentsInChildren<Component>(true)
                .Where(c => !(c is ParticleSystem) && !(c is Animator)
                         && !(c is Renderer) && !(c is Collider) && !(c is Rigidbody)
                         && !(c is LuaRelay))
                .Select(c => new ComponentSettings(c))
                .ToArray();
            particleSystems = gameObject.GetComponentsInChildren<ParticleSystem>(true).Select(ps => new ParticleSystemSettings(ps)).ToArray();
            animators = gameObject.GetComponentsInChildren<Animator>(true).Select(a => new ComponentSettings(a)).ToArray();
            // Renderers stay active-only: renderBounds walks this array, and an
            // inactive renderer reports a stale world AABB that would corrupt culling
            // for the whole object. A renderer the game activates later is simply left
            // alone by the optimizer, which is the safe direction to fail.
            renderers = gameObject.GetComponentsInChildren<Renderer>().Select(r => new RendererSettings(r)).ToArray(); 
        }

        public void Enable(bool enabled = true, OptimizationSettings settings = null, int optimizationLevel = 0) {
            // The park-content lock outranks objectsEnabled and is checked with
            // it, not after: OptimizedAF calls this every physics tick for every
            // object within 35 m, and NativeInterfaceManager's leave-build
            // coroutine flips objectsEnabled TRUE mid-load on device. Without
            // this clause a prop is unfrozen within ~30 ticks of spawning,
            // before any floor exists. isPriority still wins below — the player
            // rig must never be parked (the July 2026 Zombiez bug).
            if (!LevelObjectManager.objectsEnabled || LevelObjectManager.ParkContentLocked) {
                enabled = false;
            }
            //we override the settings to always disable
            // but can use controls to decide what for performance checking
            if (settings != null && settings.disableTest) {
                enabled = false;
                optimizationLevel = 2;
            }

            // Precedence, most-specific last:
            //     global clamps  <  isPriority  <  forceDisabled
            //
            // isPriority is asserted AFTER the global clamps, on purpose.
            //
            // RegisterLevelObject marks the player rig priority (its name contains
            // "Player" and it carries a PlayerRig) precisely so it is never culled.
            // But this used to be the FIRST clause, so the global objectsEnabled
            // clamp below it silently overruled it — and LoadLevel holds
            // objectsEnabled false for the entire spawn window. Any Disable() that
            // reached the rig in that window ran the real teardown, which sets
            //
            //     rigidbody.isKinematic     = true
            //     rigidbody.detectCollisions = false
            //
            // on every Rigidbody in the rig. detectCollisions is a runtime-only
            // property, so nothing in the prefab or the Inspector shows it: the
            // hand colliders keep looking perfectly correct while generating ZERO
            // contacts. Attraction pickups carry no Rigidbody of their own, so the
            // rig is the only detecting body in the pair — the whole interaction
            // layer dies silently. That is the July 2026 Zombiez bug: supplies
            // uncollectable and the sledgehammer unpickable, with no error anywhere,
            // reproducible ONLY through the park loader (a Content Manager spawn is
            // never registered with LevelObjectManager, so nothing ever touched it).
            //
            // Priority now outranks the GLOBAL clamps, which is what "priority" was
            // always supposed to mean. forceDisabled still outranks priority: that is
            // an explicit, deliberate, per-object call (ForceDisable), not a side
            // effect of a load-window flag — so it stays the final word.
            if (isPriority) {
                enabled = true;
            }
            if (forceDisabled != null) {
                enabled = !forceDisabled.Value;
            }

            // GAME PAUSE (Oct 2026, GamePause.cs): an operator / parent hold.
            // Nothing parked may come back to life while the game is held —
            // the request is remembered and replayed on resume (through these
            // same clamps). Parking still works (the optimizer keeps culling),
            // but a FROZEN object is thawed first, so the re-read below sees
            // the game's own state, never the freeze's.
            if (GamePause.IsPaused && !isPriority) {
                if (enabled) {
                    if (!this.enabled) GamePause.DeferEnable(this);
                    return;
                }
                GamePause.ClearDeferred(this);
                if (this.enabled) GamePause.Thaw(this);
            }

            if (this.enabled == enabled) {
                return;
            }
            
            this.enabled = enabled;

            // Everything below is ENGINE PARKING, not gameplay. Toggling a
            // LuaBehaviour raises Unity's OnEnable/OnDisable, which used to be
            // forwarded straight into creator scripts as onenable()/ondisable() —
            // so a script saw load and cull artifacts as if they were gameplay
            // events, sometimes receiving ondisable() before its first onenable().
            // Mark the window so LuaBehaviour can suppress those callbacks; the
            // relays and boot path are untouched. try/finally because a throwing
            // Toggle must not strand the depth counter above zero and silence
            // every script in the park for the rest of the session.
            LuaBehaviour.OptimizerToggleDepth++;
            try {

            var componentsRemove = new List<ComponentSettings>();
            var rigidbodiesRemove = new List<RigidbodySettings>();
            var collidersRemove = new List<ColliderSettings>();
            var animatorsRemove = new List<ComponentSettings>();
            var renderersRemove = new List<RendererSettings>();
            var particleSystemsRemove = new List<ParticleSystemSettings>();
            if (settings == null || settings.controlColliders) {
                foreach (var collider in colliders) {
                    bool success = collider.Toggle(enabled);
                    if (!success) {
                        collidersRemove.Add(collider);
                    }
                }
            }
            if (settings == null || settings.controlRigidbodies) {
            foreach (var rigidbody in rigidbodies) {
                bool success = rigidbody.Toggle(enabled);
                if (!success) {
                        rigidbodiesRemove.Add(rigidbody);
                    }
                }
            }
            if (settings == null || settings.controlComponents) {
                foreach (var component in components) {
                    bool success = component.Toggle(enabled);
                    if (!success) {
                        componentsRemove.Add(component);
                    }
                }
            }
            if (settings == null || settings.controlAnimators) {
                foreach (var animator in animators) {
                    bool success = animator.Toggle(enabled);
                    if (!success) {
                        animatorsRemove.Add(animator);
                    }
                }
            }
            if (settings != null && settings.controlRenderers) {
                foreach (var renderer in renderers) {
                    bool success = renderer.Toggle(optimizationLevel);
                    if (!success) {
                        renderersRemove.Add(renderer);
                    }
                }
            }
            // Particles group with ANIMATORS (behaviour), not with renderers
            // (visibility). The renderer clause above is deliberate — you must still
            // SEE the park while arranging it — and this one was copy-pasted into the
            // same shape, which made particles the one thing build mode never stopped:
            // `settings` is non-null from exactly one caller (OptimizedAF.RunOptimizedFrame),
            // OptimizedAF.FixedUpdate hard-returns while build mode is active, and every
            // build-mode park therefore arrives here through Disable()/ForceDisable() —
            // i.e. Enable(false, null, 0), which this clause skipped.
            if (settings == null || settings.controlParticles) {
                foreach (var particleSystem in particleSystems) {
                    bool success = particleSystem.Toggle(enabled);
                    if (!success) {
                        particleSystemsRemove.Add(particleSystem);
                    }
                }
            }
            if (componentsRemove.Count > 0) {
                components = components.Except(componentsRemove).ToArray();
            }
            if (rigidbodiesRemove.Count > 0) {
                rigidbodies = rigidbodies.Except(rigidbodiesRemove).ToArray();
            }
            if (collidersRemove.Count > 0) {
                colliders = colliders.Except(collidersRemove).ToArray();
            }
            if (animatorsRemove.Count > 0) {
                animators = animators.Except(animatorsRemove).ToArray();
            }
            if (renderersRemove.Count > 0) {
                renderers = renderers.Except(renderersRemove).ToArray();
            }
            if (particleSystemsRemove.Count > 0) {
                particleSystems = particleSystems.Except(particleSystemsRemove).ToArray();
            }

            } finally {
                LuaBehaviour.OptimizerToggleDepth--;
            }
        }

        public void Enable(OptimizationSettings settings) {
            Enable(true,settings);
        }

        public void Disable(OptimizationSettings settings = null) {
            Enable(false,settings,0);
        }
        public void DisableAndSimplifyRendering(OptimizationSettings settings) {
            Enable(false,settings,1);
        }
        public void DisableAndHide(OptimizationSettings settings) {
            Enable(false,settings, 2);
        }
        public void ForceDisable() {
            if (forceDisabled == true) {
                return;
            }
            forceDisabled = true;
            Disable();
        }
        public void ForceEnable() {
            if (forceDisabled != true) {
                return;
            }
            forceDisabled = null;
            Enable(true);
        }
    }

    public class LevelObjectManager : MonoBehaviour {
        private static bool _objectsEnabled = true;

        /// <summary>
        /// Raised whenever park content becomes live (true) or is parked (false).
        ///
        /// This flag is the single most load-bearing state in the load pipeline —
        /// it gates whether creator Lua is even allowed to boot
        /// (LuaBehaviour.ParkContentIsParked). It was a bare mutable static written
        /// from five sites with no notification of any kind, which meant content had
        /// no way to learn "the park is ready" and had to approximate it by retrying
        /// on a timer. Real shipped content retries for 300 frames and then gives up
        /// guessing.
        ///
        /// Making it a property catches every existing write site at once, so the
        /// signal cannot drift out of sync with the state it reports.
        ///
        /// Consumers must still treat this as STATE, not just an edge: a listener
        /// that wires up late has to read <see cref="objectsEnabled"/> itself rather
        /// than wait for a transition that already happened. That is the rule every
        /// correct event in this codebase follows, and every broken one didn't.
        /// </summary>
        public static event Action<bool> OnObjectsEnabledChanged;

        public static bool objectsEnabled {
            get => _objectsEnabled;
            set {
                if (_objectsEnabled == value) return;
                _objectsEnabled = value;
                try { OnObjectsEnabledChanged?.Invoke(value); }
                catch (Exception e) { Debug.LogError("[LevelObjectManager] OnObjectsEnabledChanged subscriber threw: " + e); }
            }
        }

        // ── PARK-CONTENT LOCK ────────────────────────────────────────────────
        //
        //  `objectsEnabled` was the ONLY thing keeping freshly-spawned content
        //  parked during a park load, and it is a bare global that two systems
        //  write from OUTSIDE the loader:
        //
        //   1. NativeInterfaceManager's LeaveBuildModeCoroutine (step 5) sets it
        //      TRUE and re-enables every LevelObject. On device that coroutine is
        //      kicked by ApplyRuntimeMode, which the LOAD PARK path itself calls —
        //      so it fires CONCURRENTLY with the spawn loop.
        //
        //   2. OptimizedAF.FixedUpdate then calls Enable() on every LevelObject
        //      inside distanceBands[0] — 35 m, i.e. an entire park — on the
        //      PHYSICS tick. Once (1) has flipped the clamp, every prop that
        //      registers afterwards is unfrozen within ~30 ticks of spawning,
        //      with no floor under it yet.
        //
        //  Neither knows a park is loading, and neither should have to. So the
        //  loader takes an explicit LOCK that outranks `objectsEnabled`, held from
        //  the first LoadLevel until the floors are built and the loader itself
        //  releases physics. Anything that flips `objectsEnabled` mid-load is now
        //  simply ignored until then.
        //
        //  This is the difference between iOS and an Editor park load: nothing in
        //  the Editor asserts a runtime mode mid-load, so the clamp was never
        //  broken there and the bug was invisible.
        // volatile / Interlocked throughout: BeginParkLoad and EndParkLoad run on
        // thread-pool threads (one LoadLevel per portal), while ParkContentLocked
        // is read from the main thread on the physics tick. A plain bool write
        // from a worker is not guaranteed to be visible to the reader, and this is
        // the one flag whose staleness drops props through the world.
        private static int _parkLoadDepth;
        private static volatile bool _parkContentLocked;
        private static long _parkLockDeadlineTicks = DateTime.MaxValue.Ticks;
        private static volatile bool _parkLockOverrunLogged;

        /// Ceiling on the lock. A park stuck frozen is worse than a park that
        /// dropped a prop, so a load that dies without releasing expires instead
        /// of wedging gameplay forever.
        private const float ParkLoadLockMaxSeconds = 120f;

        /// True while park content must stay parked regardless of `objectsEnabled`.
        /// Safe to read from any thread (no Unity API).
        public static bool ParkContentLocked {
            get {
                if (!_parkContentLocked) return false;
                if (DateTime.UtcNow.Ticks <= Interlocked.Read(ref _parkLockDeadlineTicks)) return true;

                if (!_parkLockOverrunLogged) {
                    _parkLockOverrunLogged = true;
                    Debug.LogWarning($"[LevelObjectManager] Park-content lock held for over {ParkLoadLockMaxSeconds:F0}s " +
                                     $"({_parkLoadDepth} level load(s) never released) — unlocking so the park can still play.");
                }
                return false;
            }
        }

        /// Outstanding level loads. Read by the loader to spot a park switch that
        /// started while it was waiting for floors.
        public static int ParkLoadDepth => _parkLoadDepth;

        /// One per LoadLevel, taken on entry. Thread-safe: level loads run
        /// concurrently on the thread pool, one per portal.
        public static void BeginParkLoad() {
            Interlocked.Increment(ref _parkLoadDepth);
            Interlocked.Exchange(ref _parkLockDeadlineTicks, DateTime.UtcNow.AddSeconds(ParkLoadLockMaxSeconds).Ticks);
            _parkLockOverrunLogged = false;
            // Written LAST: the deadline must be in place before any reader can
            // observe the lock, or ParkContentLocked could compare against the
            // previous park's expired deadline and unlock immediately.
            _parkContentLocked = true;
        }

        /// One per BeginParkLoad, in a finally. Returns true for the LAST load
        /// out — the one that owns releasing physics.
        ///
        /// Deliberately does NOT unlock: the floors do not exist yet at this
        /// point. Unlocking here would hand the park straight back to OptimizedAF
        /// while the gap mesh is still being built, which is the bug.
        public static bool EndParkLoad() {
            if (Interlocked.Decrement(ref _parkLoadDepth) > 0) return false;
            Interlocked.Exchange(ref _parkLoadDepth, 0);
            return true;
        }

        /// Called by the loader once the ground exists, immediately before it
        /// enables everything.
        public static void UnlockParkContent() {
            _parkContentLocked = false;
            Interlocked.Exchange(ref _parkLockDeadlineTicks, DateTime.MaxValue.Ticks);
        }

        /// Park teardown: whatever was loading is gone.
        public static void ResetParkLoad() {
            Interlocked.Exchange(ref _parkLoadDepth, 0);
            _parkContentLocked = false;
            Interlocked.Exchange(ref _parkLockDeadlineTicks, DateTime.MaxValue.Ticks);
            _parkLockOverrunLogged = false;
        }

        /// <summary>
        /// True while the park is in BUILD MODE — the creator is arranging it, not
        /// playing it. Set by NativeInterfaceManager.ApplyRuntimeMode on both edges.
        ///
        /// Read by ParticleSystemSettings, and by nothing else: it is what lets the
        /// parking transition POSE a persistent effect instead of clearing it, so a
        /// staged park keeps its torch fire and its waterfall mist.
        ///
        /// Distinct from <see cref="objectsEnabled"/>, which is ALSO false during an
        /// ordinary park load — the two must not be conflated. Posing on every park
        /// load would pay a Simulate() per system for nothing, on the one code path
        /// where the frame budget is already gone.
        ///
        /// A plain static with a public setter on purpose: nothing needs an edge, so
        /// there is no change event to keep in sync with the state (the mistake the
        /// objectsEnabled comment above documents).
        /// </summary>
        public static bool BuildModeParked { get; set; }

        public static LevelObjectManager Instance;
        public bool gatherChildren = false;
        [HideInInspector] public List<LevelObject> levelObjects = new();

        void Awake() {
            Instance = this;
        }

        void Start()
        {
            if (gatherChildren) {
                foreach (Transform child in transform) {
                    RegisterLevelObject(child.gameObject);
                }
            }
        }

        public bool RegisterLevelObject(GameObject obj, bool startDisabled = false) {
            if (obj == null) {
                return false;
            }

            bool isPriority = (obj.name != null && obj.name.Contains("Player")) ||
                    obj.layer == LayerMask.NameToLayer("Triggers") ||
                    obj.layer == LayerMask.NameToLayer("Gizmo") ||
                    obj.GetComponent<PlayerRig>() != null;
            bool ignoreOptimization = obj.GetComponentInParent<OptimizedAFIgnore>() != null;

            // A LevelTemplate is a special case: it registers each of its children
            // individually so they stay separately cullable, and so the template root
            // itself (PropTemplate, GameArea, BuildModeObjectController) keeps working
            // while its contents are switched off. A prop the player placed on its own
            // is the same case - it has to stay grabbable in Build Mode.
            //
            // It is NOT the same case for a prop baked inside a level or attraction.
            // Recursing past that prop's root leaves the root unregistered, so anything
            // sitting on it - gameplay scripts, colliders, rigidbodies - is invisible to
            // Enable/DisableAllLevelObjects and keeps running in Build Mode. A nested
            // prop is authored content the template owns (PropTemplate suppresses its own
            // PropTemplate and GameArea in that case), so register it as one ordinary
            // LevelObject. Nothing is lost by not recursing: LevelObject already gathers
            // the whole subtree via GetComponentsInChildren.
            var propTemplate = obj.GetComponent<PropTemplate>();
            bool isNestedProp = propTemplate != null && propTemplate.IsNestedUnderTemplate;

            if (!isNestedProp && (obj.GetComponent<LevelTemplate>() != null || propTemplate != null)) {
                // The ROOT's own Rigidbodies still have to be parked, and the
                // recursion below structurally cannot reach them: each child
                // LevelObject snapshots GetComponentsInChildren<Rigidbody> FROM
                // THAT CHILD, which never sees a body on the parent. A
                // player-placed physics prop normally carries its Rigidbody on
                // the template root — so startDisabled:true froze nothing on it
                // and it fell from the frame it spawned, before the park had any
                // floor at all. That is the "props already fully fallen through"
                // report, and no amount of waiting later can fix it.
                RegisterTemplateRootBodies(obj, startDisabled);

                foreach (Transform child in obj.transform) {
                    RegisterLevelObject(child.gameObject, startDisabled);
                }
                return true;
            }

            // Two spawn paths reach the same object: CoreExtensions.SpawnPrefab
            // registers it, then LevelAnchor.SetupNewObject registers it again (and
            // likewise for the player rig via LevelAnchor / PortalAnchor). A second
            // LevelObject takes its OWN snapshot — AFTER the first one has already
            // parked the object, so it records the parked values as if they were the
            // authored ones — and the two then fight over the same components with
            // independent enabled flags, while UnregisterLevelObject only ever removes
            // the first. Harmless when nothing was re-read; corrupting now that
            // everything is.
            //
            // Fold the second registration into the first so intent still lands.
            // "-- @optimizer-ignore" in a script on this object, above it or below it, or
            // an earlier dp.optimizer_ignore(go, true) that arrived before registration
            // (awake() runs before SetupNewObject registers a live spawn on device). Read
            // HERE, not from Lua at runtime: on device content can be parked before its
            // awake() ever runs. An OptimizedAFIgnore component already covers it.
            bool luaIgnore = !ignoreOptimization && LuaAsksOptimizerIgnore(obj);

            LevelObject existing = levelObjects.Find(lo => lo.gameObject == obj);
            if (existing != null) {
                if (isPriority) existing.isPriority = true;
                if (ignoreOptimization) { existing.ignoreOptimization = true; existing.ignoredByPlatform = true; }
                if (luaIgnore) MarkLuaIgnored(existing);
                if (startDisabled) existing.Disable();
                return true;
            }

            LevelObject levelObject = new LevelObject(obj, isPriority, ignoreOptimization);
            if (luaIgnore) MarkLuaIgnored(levelObject);
            levelObjects.Add(levelObject);
            if (startDisabled) {
                levelObject.Disable();
            } else {
                // Content arriving live while the game is held is frozen on arrival.
                GamePause.NoteRegistered(levelObject);
            }
            return true;
        }

        // ── TEMPLATE-ROOT RIGIDBODIES ────────────────────────────────────────
        //
        //  RegisterLevelObject deliberately does NOT register a non-nested
        //  LevelTemplate/PropTemplate root as a LevelObject: the root has to keep
        //  working while its contents are parked (PropTemplate, GameArea and
        //  BuildModeObjectController all live there, and the prop must stay
        //  grabbable in Build Mode). So it recurses into the children instead —
        //  and the root's own Rigidbody falls through the gap between them.
        //
        //  This parks ONLY the root's own Rigidbodies: GetComponents, not
        //  GetComponentsInChildren, because every child is already covered by its
        //  own LevelObject and a body in two arrays would have the second toggle
        //  read back whatever the first one just wrote (the same invariant the
        //  LevelObject constructor's Where() clause protects).
        //
        //  Colliders on the root are deliberately left ALONE. Build Mode raycasts
        //  against them to select and drag the prop; parking them would make a
        //  freshly-spawned prop unselectable. Freezing the body is what stops the
        //  fall — the collider is not what moves it.
        public class TemplateRootBodies {
            public readonly GameObject gameObject;
            public LevelObject.RigidbodySettings[] rigidbodies;
            public bool enabled = true;

            public TemplateRootBodies(GameObject go) {
                gameObject = go;
                rigidbodies = go.GetComponents<Rigidbody>()
                                .Select(r => new LevelObject.RigidbodySettings(r))
                                .ToArray();
            }

            public bool IsAlive => gameObject != null;
            public bool HasBodies => rigidbodies != null && rigidbodies.Length > 0;
            /// Parked by the game pause, or asked to come back while paused: the resume releases it.
            internal bool pausedByGame;

            public void Toggle(bool enable) {
                // Same global clamps LevelObject.Enable applies. No isPriority
                // equivalent here: a template root is never the player rig.
                if (!LevelObjectManager.objectsEnabled || LevelObjectManager.ParkContentLocked) enable = false;
                // Game pause: the body stays parked; the resume replays this.
                if (enable && GamePause.IsPaused) { if (!enabled) pausedByGame = true; return; }
                if (!enable) pausedByGame = false;
                if (enabled == enable) return;
                enabled = enable;

                List<LevelObject.RigidbodySettings> dead = null;
                foreach (var rb in rigidbodies) {
                    if (!rb.Toggle(enable)) (dead ??= new List<LevelObject.RigidbodySettings>()).Add(rb);
                }
                if (dead != null) rigidbodies = rigidbodies.Except(dead).ToArray();
            }
        }

        private readonly List<TemplateRootBodies> templateRoots = new();

        private void RegisterTemplateRootBodies(GameObject obj, bool startDisabled) {
            var existing = templateRoots.Find(t => t.gameObject == obj);
            if (existing != null) {
                if (startDisabled) existing.Toggle(false);
                return;
            }

            var holder = new TemplateRootBodies(obj);
            // Nothing to park: don't grow the list with entries that can never
            // do anything (the overwhelmingly common case — most template roots
            // carry no Rigidbody at all).
            if (!holder.HasBodies) return;

            templateRoots.Add(holder);
            if (startDisabled) holder.Toggle(false);
        }

        /// Toggle every tracked template root, pruning destroyed entries as it goes.
        private void ToggleTemplateRootBodies(bool enable) {
            for (int i = templateRoots.Count - 1; i >= 0; i--) {
                if (!templateRoots[i].IsAlive) {
                    templateRoots.RemoveAt(i);
                    continue;
                }
                templateRoots[i].Toggle(enable);
            }
        }

        /// GamePause: park every live template-root body (a placed prop's own Rigidbody).
        internal void PauseTemplateRootBodies() {
            for (int i = templateRoots.Count - 1; i >= 0; i--) {
                var t = templateRoots[i];
                if (!t.IsAlive) { templateRoots.RemoveAt(i); continue; }
                if (!t.enabled) continue;
                t.Toggle(false);
                t.pausedByGame = true;
            }
        }

        /// GamePause: release exactly the bodies the pause parked (or that asked to come back meanwhile).
        internal void ResumeTemplateRootBodies() {
            for (int i = templateRoots.Count - 1; i >= 0; i--) {
                var t = templateRoots[i];
                if (!t.IsAlive) { templateRoots.RemoveAt(i); continue; }
                if (!t.pausedByGame) continue;
                t.pausedByGame = false;
                t.Toggle(true);
            }
        }

        public bool PrioritizeLevelObject(GameObject obj) {
            LevelObject levelObject = levelObjects.Find(lo => lo.gameObject == obj);
            if (levelObject != null) {
                levelObject.ForceEnable();
                levelObject.isPriority = true;
                return true;
            }
            return false;
        }

        public bool UnregisterLevelObject(GameObject obj) {
            templateRoots.RemoveAll(t => !t.IsAlive || t.gameObject == obj);

            LevelObject levelObject = levelObjects.Find(lo => lo.gameObject == obj);
            if (levelObject != null) {
                levelObjects.Remove(levelObject);
                return true;
            }
            return false;
        }

        public void EnableAllLevelObjects(bool force = false) {
            foreach (var levelObject in levelObjects) {
                if (force) {
                    levelObject.ForceEnable();
                } else {
                    levelObject.Enable();
                }
            }
            ToggleTemplateRootBodies(true);
        }
        /// THE LOADER'S AUTHORITATIVE RELEASE. Use this, not EnableAllLevelObjects,
        /// when handing a finished park back to gameplay.
        ///
        /// A plain Enable() cannot undo a force-disable: `forceDisabled` is the
        /// final word in LevelObject.Enable, and ForceEnable() early-returns
        /// unless forceDisabled is already true — so an object that went through
        /// DisableAllLevelObjects(force: true) is unreachable by either. That call
        /// happens on every BUILD assert (NativeInterfaceManager, entering build),
        /// and on device those asserts land DURING a park load, so objects
        /// registered around one are left permanently force-disabled and the park
        /// never comes alive.
        ///
        /// This is the same clear-then-enable NativeInterfaceManager's leave-build
        /// step 5 does, for exactly the same reason; it lives here now so both
        /// callers share one implementation.
        public void ReleaseAllLevelObjects() {
            foreach (var levelObject in levelObjects) {
                if (levelObject == null) continue;
                levelObject.forceDisabled = null;
                levelObject.Enable(true);
            }
            ToggleTemplateRootBodies(true);
        }

        public void DisableAllLevelObjects(bool force = false) {
            foreach (var levelObject in levelObjects) {
                if (force) {
                    levelObject.ForceDisable();
                } else {
                    levelObject.Disable();
                }
            }
            ToggleTemplateRootBodies(false);
        }

        // ── OPTIMIZER IGNORE FROM LUA: -- @optimizer-ignore / dp.optimizer_ignore (Oct 2026) ──
        //
        //  The Lua equivalent of an OptimizedAFIgnore component, for managers and
        //  always-on systems that must keep running wherever the player stands. Same
        //  meaning as the component, exactly: the object is still registered, still
        //  parked by the park load, build mode and the operator pause, but the
        //  DISTANCE sweep (OptimizedAF.RunOptimizedFrame) skips it.
        //
        //   • "-- @optimizer-ignore" in a LuaBehaviour's script (a Lua comment, like
        //     "-- @packing-ignore"), read at REGISTRATION. It covers the object holding
        //     the script and its subtree. The optimizer's unit is the LevelObject, so
        //     every LevelObject at, under, or containing that object is exempt.
        //   • dp.optimizer_ignore(go, on) at runtime. true: restore whatever is parked
        //     (from its snapshot, renderers included) and exempt it; false: hand it back
        //     (the next sweep parks it if it is far). Idempotent, nil-safe, remembered
        //     for objects not registered yet. Never undoes an OptimizedAFIgnore.
        //
        //  Every exempt object costs Quest frame time all session, so the first title
        //  to exempt more than LuaIgnoreWarnThreshold objects gets one warning.

        public const string OptimizerIgnoreMarker = "@optimizer-ignore";
        public const int LuaIgnoreWarnThreshold = 20;

    #if !UNITY_EDITOR
        static readonly Dictionary<int, bool> s_scriptMarker = new();          // TextAsset id → has marker
    #endif
        static readonly HashSet<GameObject> s_ignoreRequests = new();          // dp.optimizer_ignore(go, true)
        static readonly HashSet<string> s_ignoreWarnedTitles = new();
        static OptimizationSettings s_restoreAll;                              // every control on

        /// <summary>The marker must be in a Lua comment ("-- @optimizer-ignore"), not just any string.</summary>
        public static bool ScriptHasOptimizerIgnore(string source) {
            if (string.IsNullOrEmpty(source)) return false;
            int at = source.IndexOf(OptimizerIgnoreMarker, StringComparison.Ordinal);
            while (at >= 0) {
                int lineStart = source.LastIndexOf('\n', at) + 1;
                if (source.IndexOf("--", lineStart, at - lineStart, StringComparison.Ordinal) >= 0) return true;
                at = source.IndexOf(OptimizerIgnoreMarker, at + OptimizerIgnoreMarker.Length, StringComparison.Ordinal);
            }
            return false;
        }

        static bool ScriptMarked(LuaBehaviour lb) {
            var src = lb != null ? lb.luaScript : null;
            if (src == null) return false;
        #if UNITY_EDITOR
            // A script edited between Play sessions keeps its instance id (and statics
            // survive when domain reload is off), so the Editor always reads the source.
            return ScriptHasOptimizerIgnore(src.text);
        #else
            int id = src.GetInstanceID();
            if (!s_scriptMarker.TryGetValue(id, out bool marked)) {
                marked = ScriptHasOptimizerIgnore(src.text);   // .text allocates: once per script asset
                s_scriptMarker[id] = marked;
            }
            return marked;
        #endif
        }

        static bool LuaAsksOptimizerIgnore(GameObject obj) {
            foreach (var lb in obj.GetComponentsInParent<LuaBehaviour>(true)) if (ScriptMarked(lb)) return true;
            foreach (var lb in obj.GetComponentsInChildren<LuaBehaviour>(true)) if (ScriptMarked(lb)) return true;
            if (s_ignoreRequests.Count == 0) return false;
            s_ignoreRequests.RemoveWhere(g => g == null);
            var t = obj.transform;
            foreach (var g in s_ignoreRequests)
                if (t.IsChildOf(g.transform) || g.transform.IsChildOf(t)) return true;
            return false;
        }

        void MarkLuaIgnored(LevelObject lo) {
            lo.ignoreOptimization = true;
            if (lo.ignoredByLua) return;
            lo.ignoredByLua = true;
            lo.ignoredByLuaTitle = DreamParkLuaAPI.GameId(lo.gameObject) ?? "";
            int count = 0;
            foreach (var x in levelObjects)
                if (x != null && x.ignoredByLua && x.gameObject != null && x.ignoredByLuaTitle == lo.ignoredByLuaTitle) count++;
            if (!levelObjects.Contains(lo)) count++;
            if (count > LuaIgnoreWarnThreshold && s_ignoreWarnedTitles.Add(lo.ignoredByLuaTitle)) {
                string title = lo.ignoredByLuaTitle.Length > 0 ? lo.ignoredByLuaTitle : "(no title)";
                Debug.LogWarning($"[OptimizedAF] {title}: {count} objects are exempt from distance culling " +
                                 "(-- @optimizer-ignore / dp.optimizer_ignore). Each one runs and renders all session " +
                                 "wherever the player stands, which costs Quest frame time. Keep it to managers and " +
                                 "always-on systems, not props.");
            }
        }

        /// <summary>
        /// dp.optimizer_ignore(go, on). Returns how many registered LevelObjects it changed
        /// (0 = none registered yet: the request is remembered and applied at registration).
        /// </summary>
        public static int SetOptimizerIgnore(GameObject go, bool on) {
            if (go == null) return 0;
            if (on) s_ignoreRequests.Add(go); else s_ignoreRequests.Remove(go);
            var lom = Instance;
            if (lom == null || lom.levelObjects == null) return 0;
            var t = go.transform;
            int changed = 0;
            foreach (var lo in lom.levelObjects.ToArray()) {
                if (lo == null || lo.gameObject == null) continue;
                var lt = lo.gameObject.transform;
                if (!lt.IsChildOf(t) && !t.IsChildOf(lt)) continue;
                if (on) {
                    lom.MarkLuaIgnored(lo);
                    RestoreParked(lo);
                    changed++;
                } else if (lo.ignoredByLua) {
                    lo.ignoredByLua = false;
                    lo.ignoreOptimization = lo.ignoredByPlatform;
                    changed++;
                }
            }
            return changed;
        }

        /// <summary>True when the distance sweep skips <paramref name="go"/>'s LevelObject (any reason).</summary>
        public static bool IsOptimizerIgnored(GameObject go) {
            var lom = Instance;
            if (go == null || lom == null || lom.levelObjects == null) return false;
            var t = go.transform;
            foreach (var lo in lom.levelObjects) {
                if (lo == null || lo.gameObject == null) continue;
                var lt = lo.gameObject.transform;
                if ((lt.IsChildOf(t) || t.IsChildOf(lt)) && lo.ignoreOptimization) return true;
            }
            return false;
        }

        // Bring a distance-parked object back from its snapshot. Enable() keeps every
        // global clamp (park load lock, build mode, forceDisabled, the operator pause),
        // so this only undoes the optimizer's own parking. Renderers are restored with
        // an all-on settings object: Enable(true) without settings never touches them,
        // and an exempt object will never be swept back in.
        static void RestoreParked(LevelObject lo) {
            if (s_restoreAll == null) {
                s_restoreAll = ScriptableObject.CreateInstance<OptimizationSettings>();
                s_restoreAll.hideFlags = HideFlags.HideAndDontSave;
            }
            if (!lo.enabled) lo.Enable(true, s_restoreAll, 0);
            if (!lo.enabled || lo.renderers == null) return;
            foreach (var rs in lo.renderers) if (rs != null && rs.IsParked) rs.Toggle(0);
        }

        // ── INTENDED ENABLED STATE: dp.enabled / dp.set_enabled (Oct 2026) ──────
        //
        //  Parking writes `enabled = false` onto the live component, so a script
        //  that reads `renderer.enabled` (or any `.enabled`) while the object it
        //  points at is parked reads the PARK, not the game. start() is the usual
        //  place: a manager boots live while the child it inspects is still parked
        //  by the load or culled by distance. BirthdayBash's Cake Boss recorded
        //  every rig renderer as "should be off" that way and never rendered again.
        //
        //  These two are the opt-in fix, and they are purely additive: they read and
        //  write the very snapshot the unpark restores, through the members the
        //  Settings classes expose for it, and touch nothing else. start() is NOT
        //  delayed (content depends on it arriving early), and nothing changes for
        //  content that never calls them.
        //
        //  Covered: anything with an enabled flag — every Behaviour (MonoBehaviour,
        //  LuaBehaviour, Light, AudioSource, Camera, Animator…), Renderer, Collider.
        //  A component the optimizer never snapshotted (unregistered content, the
        //  protected types, a renderer inactive at registration, a Lua relay) has no
        //  parked state, so both read and write the live flag. The game pause's
        //  freeze is honoured the same way (GamePause.TryGetFrozenIntended).
        //
        //  Main thread only. Cost is paid only by the caller: a walk up the
        //  component's parents, one reference scan of levelObjects per ancestor.

        /// <summary>
        /// The enabled state the game intends for <paramref name="component"/>: while the
        /// optimizer has it parked (or the game pause has it frozen), the value it will
        /// restore; otherwise the live <c>.enabled</c>. False when the component is null,
        /// destroyed, or has no enabled flag (Transform, Rigidbody, ParticleSystem…).
        /// </summary>
        public static bool TryGetIntendedEnabled(Component component, out bool on) {
            on = false;
            if (component == null || !HasEnabledFlag(component)) return false;
            var settings = FindSettings(component, out var owner);
            switch (settings) {
                case LevelObject.ColliderSettings cs when cs.IsParked:  on = cs.IntendedEnabled; return true;
                case LevelObject.RendererSettings rs when rs.IsParked:  on = rs.IntendedEnabled; return true;
                case LevelObject.ComponentSettings bs when bs.IsParked: on = bs.IntendedEnabled; return true;
            }
            if (owner != null && GamePause.TryGetFrozenIntended(owner, component, out on)) return true;
            on = LiveEnabled(component);
            return true;
        }

        /// <summary>
        /// Set the enabled state the game intends. Parked (or frozen by the game pause):
        /// the restore snapshot is written and the change lands at the unpark/resume, the
        /// component staying parked meanwhile. Otherwise the live <c>.enabled</c> is set,
        /// exactly as <c>component.enabled = on</c> would. False when the component is
        /// null, destroyed, or has no enabled flag.
        /// </summary>
        public static bool SetIntendedEnabled(Component component, bool on) {
            if (component == null || !HasEnabledFlag(component)) return false;
            var settings = FindSettings(component, out var owner);
            switch (settings) {
                case LevelObject.ColliderSettings cs when cs.IsParked:  cs.SetIntendedEnabled(on); return true;
                case LevelObject.RendererSettings rs when rs.IsParked:  rs.SetIntendedEnabled(on); return true;
                case LevelObject.ComponentSettings bs when bs.IsParked: bs.SetIntendedEnabled(on); return true;
            }
            if (owner != null && GamePause.TrySetFrozenIntended(owner, component, on)) return true;
            switch (component) {
                case Behaviour b: b.enabled = on; break;
                case Renderer r:  r.enabled = on; break;
                case Collider c:  c.enabled = on; break;
            }
            return true;
        }

        static bool HasEnabledFlag(Component c) => c is Behaviour || c is Renderer || c is Collider;

        static bool LiveEnabled(Component c) {
            switch (c) {
                case Behaviour b: return b.enabled;
                case Renderer r:  return r.enabled;
                case Collider col: return col.enabled;
            }
            return false;
        }

        /// <summary>
        /// The optimizer's snapshot of <paramref name="component"/> (a ColliderSettings,
        /// RendererSettings or ComponentSettings) and the LevelObject that holds it, or
        /// null/null when nothing registered snapshots it. Nearest registered ancestor
        /// first; a LevelObject whose arrays don't hold the component is passed over.
        /// </summary>
        internal static object FindSettings(Component component, out LevelObject owner) {
            owner = null;
            var lom = Instance;
            if (lom == null || lom.levelObjects == null || component == null) return null;
            var list = lom.levelObjects;
            for (var t = component.transform; t != null; t = t.parent) {
                var go = t.gameObject;
                for (int i = 0; i < list.Count; i++) {
                    var lo = list[i];
                    if (lo == null || !ReferenceEquals(lo.gameObject, go)) continue;
                    var s = SettingsIn(lo, component);
                    if (s != null) { owner = lo; return s; }
                }
            }
            return null;
        }

        static object SettingsIn(LevelObject lo, Component c) {
            switch (c) {
                case Collider _:
                    if (lo.colliders != null) foreach (var s in lo.colliders) if (s != null && ReferenceEquals(s.collider, c)) return s;
                    return null;
                case Renderer _:
                    if (lo.renderers != null) foreach (var s in lo.renderers) if (s != null && ReferenceEquals(s.renderer, c)) return s;
                    return null;
                case Animator _:
                    if (lo.animators != null) foreach (var s in lo.animators) if (s != null && ReferenceEquals(s.component, c)) return s;
                    return null;
                default:
                    if (lo.components != null) foreach (var s in lo.components) if (s != null && ReferenceEquals(s.component, c)) return s;
                    return null;
            }
        }

        public void Enable()
        {
            objectsEnabled = true;
        } 
        public void Disable()
        {
            objectsEnabled = false;
        }
    }
}
