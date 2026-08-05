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

                if (renderers == null || renderers.Length == 0)
                    return new Bounds();

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

                if (!init) combined = new Bounds();

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
        }

        public class ParticleSystemSettings {
            public ParticleSystem particleSystem;
            public bool enabled;
            private bool parked = false;
            private bool firstPark = true;

            public ParticleSystemSettings(ParticleSystem particleSystem) {
                this.particleSystem = particleSystem;
                enabled = particleSystem.isPlaying || particleSystem.main.playOnAwake;
            }

            public bool Toggle(bool enabled) {
                if (particleSystem == null || particleSystem.IsDestroyed()) {
                    return false;
                }
                if (enabled) {
                    if (parked) {
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
                        this.enabled = firstPark
                            ? (particleSystem.isPlaying || particleSystem.main.playOnAwake)
                            : particleSystem.isPlaying;
                        firstPark = false;
                        parked = true;
                    }
                    particleSystem.Stop();
                    particleSystem.Clear();
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
            #if SUPERADVENTURELAND
                , typeof(ProceduralLavaPit)
            #endif
            };

            private bool parked = false;

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
        }
        public class RendererSettings {
            public Renderer renderer;
            // Original state
            private readonly Material[] originalSharedMats;
            private Material optimizedMaterial;
            private Material[] optimizedSharedMats; // same length as originalSharedMats
            private Material[] liveSharedMats;       // what the GAME had before parking
            private bool parked = false;
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
            colliders = gameObject.GetComponentsInChildren<Collider>(true).Select(c => new ColliderSettings(c)).ToArray();
            rigidbodies = gameObject.GetComponentsInChildren<Rigidbody>(true).Select(r => new RigidbodySettings(r)).ToArray();
            components = gameObject.GetComponentsInChildren<Component>(true)
                .Where(c => !(c is ParticleSystem) && !(c is Animator)
                         && !(c is Renderer) && !(c is Collider) && !(c is Rigidbody))
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
            if (settings != null && settings.controlParticles) {
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
            LevelObject existing = levelObjects.Find(lo => lo.gameObject == obj);
            if (existing != null) {
                if (isPriority) existing.isPriority = true;
                if (ignoreOptimization) existing.ignoreOptimization = true;
                if (startDisabled) existing.Disable();
                return true;
            }

            LevelObject levelObject = new LevelObject(obj, isPriority, ignoreOptimization);
            levelObjects.Add(levelObject);
            if (startDisabled) {
                levelObject.Disable();
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

            public void Toggle(bool enable) {
                // Same global clamps LevelObject.Enable applies. No isPriority
                // equivalent here: a template root is never the player rig.
                if (!LevelObjectManager.objectsEnabled || LevelObjectManager.ParkContentLocked) enable = false;
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
