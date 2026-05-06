using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using System;
using UnityEngine.Analytics;
using TMPro;
using SuperAdventureLand;

namespace DreamPark.ParkBuilder {
    public class LevelObject {
        public GameObject gameObject;
        public ColliderSettings[] colliders;
        public RigidbodySettings[] rigidbodies;
        public ComponentSettings[] components;
        public Animator[] animators;
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
        public Bounds renderBounds {
            get {
                if (_bounds != null) return (Bounds)_bounds;

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
                    if (!rigidbody.isKinematic) {
                        rigidbody.linearVelocity = linearVelocity;
                        rigidbody.angularVelocity = angularVelocity;
                    }
                    rigidbody.isKinematic = isKinematic;
                    rigidbody.detectCollisions = detectCollisions;
                    rigidbody.useGravity = useGravity;
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

        public class ColliderSettings {
            public Collider collider;
            public bool enabled;

            public ColliderSettings(Collider collider) {
                this.collider = collider;
                enabled = collider.enabled;
            }

            public bool Toggle(bool enabled) {
                if (collider == null || collider.IsDestroyed()) {
                    return false;
                }
                collider.enabled = enabled ? this.enabled : false;
                return true;
            }
        }

        public class ParticleSystemSettings {
            public ParticleSystem particleSystem;
            public bool enabled;

            public ParticleSystemSettings(ParticleSystem particleSystem) {
                this.particleSystem = particleSystem;
                enabled = particleSystem.isPlaying || particleSystem.main.playOnAwake;
            }
            
            public bool Toggle(bool enabled) {
                if (particleSystem == null || particleSystem.IsDestroyed()) {
                    return false;
                }
                if (enabled ? this.enabled : false) {
                    particleSystem.Play();
                } else {
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
                typeof(TextMeshProUGUI), typeof(TextMeshPro), typeof(DreamBand),
                typeof(LevelTemplate), typeof(PropTemplate), typeof(CalibrateLevel), typeof(CalibrateProp),
                typeof(DepthMask), typeof(FloorAnchor), typeof(FloorCutout)
            #if DREAMPARKCORE
                , typeof(PortalAnchor), typeof(ParkAnchor), typeof(LevelAnchor)
            #endif
            #if SUPERADVENTURELAND
                , typeof(ProceduralLavaPit)
            #endif
            };

            public ComponentSettings(Component component) {
                this.component = component;
                if (component is MonoBehaviour mb) {
                    enabled = mb.enabled;
                }
            }

            public bool Toggle(bool enabled = true, OptimizationSettings settings = null) {
                if (component == null || component.IsDestroyed()) {
                    return false;
                }
                var componentType = component.GetType();
                if (ProtectedComponentTypes.Any(type => type.IsAssignableFrom(componentType))) {
                    return false;
                }
                if (component is MonoBehaviour mb) {
                    mb.enabled = enabled ? this.enabled : false;
                } else if (component is Behaviour b) {
                    b.enabled = enabled ? this.enabled : false;
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
                if (optimizationLevel > 0) {
                    if (optimizedSharedMats != null && optimizedSharedMats.Length > 0) {
                        renderer.sharedMaterials = optimizedSharedMats;
                        enableOptimizedMaterial = true;
                    }
                } else {
                    if (originalSharedMats != null && originalSharedMats.Length > 0) {
                        renderer.sharedMaterials = originalSharedMats;
                        enableOptimizedMaterial = false;
                    }
                }
                renderer.enabled = optimizationLevel != 2 ? this.enabled : false;
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
            colliders = gameObject.GetComponentsInChildren<Collider>().Select(c => new ColliderSettings(c)).ToArray();
            rigidbodies = gameObject.GetComponentsInChildren<Rigidbody>().Select(r => new RigidbodySettings(r)).ToArray();
            components = gameObject.GetComponentsInChildren<Component>()
                .Where(c => !(c is ParticleSystem))
                .Select(c => new ComponentSettings(c))
                .ToArray();
            particleSystems = gameObject.GetComponentsInChildren<ParticleSystem>().Select(ps => new ParticleSystemSettings(ps)).ToArray();
            animators = gameObject.GetComponentsInChildren<Animator>();
            renderers = gameObject.GetComponentsInChildren<Renderer>().Select(r => new RendererSettings(r)).ToArray(); 
        }

        public void Enable(bool enabled = true, OptimizationSettings settings = null, int optimizationLevel = 0) {
            if (isPriority) {
                enabled = true;
            }
            if (forceDisabled != null) {
                enabled = !forceDisabled.Value;
            }
            if (!LevelObjectManager.objectsEnabled) {
                enabled = false;
            }
            //we override the settings to always disable
            // but can use controls to decide what for performance checking
            if (settings != null && settings.disableTest) {
                enabled = false;
                optimizationLevel = 2;
            }
            
            if (this.enabled == enabled) {
                return;
            }
            
            this.enabled = enabled;
            
            var componentsRemove = new List<ComponentSettings>();
            var rigidbodiesRemove = new List<RigidbodySettings>();
            var collidersRemove = new List<ColliderSettings>();
            var animatorsRemove = new List<Animator>();
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
                    if (animator == null || animator.IsDestroyed()) {
                        animatorsRemove.Add(animator);
                        continue;
                    }
                    animator.enabled = enabled;
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
        public static bool objectsEnabled = true;
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

            //level template is a special case, it will register all its children as level objects
            if (obj.GetComponent<LevelTemplate>() != null || obj.GetComponent<PropTemplate>() != null) {
                foreach (Transform child in obj.transform) {
                    RegisterLevelObject(child.gameObject, startDisabled);
                }
                return true;
            }

            LevelObject levelObject = new LevelObject(obj, isPriority, ignoreOptimization);
            levelObjects.Add(levelObject);
            if (startDisabled) {
                levelObject.Disable();
            }
            return true;
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
        }
        public void DisableAllLevelObjects(bool force = false) {
            foreach (var levelObject in levelObjects) {
                if (force) {
                    levelObject.ForceDisable();
                } else {
                    levelObject.Disable();
                }
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
