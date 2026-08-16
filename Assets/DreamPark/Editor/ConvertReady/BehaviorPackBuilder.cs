// ─────────────────────────────────────────────────────────────────────
//  BehaviorPackBuilder.cs — Stage 6 of Convert to DreamPark-Ready
//
//  WHY THIS EXISTS
//
//  Stages 1–5 produce a prop that stands in the right place at the right
//  size. Stage 6 is the part a creator cannot do by hand without knowing
//  four things that are written down nowhere and fail SILENTLY when you
//  get them wrong:
//
//   1. EasyBend must not live on the prop root. EasyBend.Awake caches
//      baseLocalRotation and Update then writes transform.localRotation
//      EVERY FRAME for the object's whole life (EasyBend.cs:33, :68). The
//      park loader (LevelAnchor.Spawn) instantiates the prefab and THEN
//      writes localRotation on the spawned root — so Awake caches the
//      prefab's rotation, and the first Update overwrites the park's
//      authored yaw with it. Every bendy prop in the park snaps to the
//      same facing on the first live frame, and it looks like a park-data
//      bug. Hence the Motion node, and hence this file refuses to convert
//      a Bendy prop that was not given one.
//
//   2. EasyBend ships with three defaults that are wrong here, and each
//      one fails quietly rather than loudly:
//        eventOnStart  false → Update early-returns forever, because
//                              onlyWhileEnabled && !isEnabled and nothing
//                              is wired to fire OnEvent.
//        detectionMask ~0    → the prop bends away from the floor and the
//                              walls, permanently, because a wall never
//                              moves out of range.
//        detectionRadius 0.75→ a metre-wide bend trigger around a 5 cm coin.
//      All three are overridden here, and every override is stated in the
//      report. A converter that changes a default without saying so is a
//      converter people stop trusting.
//
//   3. Mass. A flat 1 kg on everything makes a wardrobe and a paper cup
//      throw identically — every prop feels like a crisp packet. The mass
//      comes from the mesh volume ColliderFitter already computed times a
//      density estimate, clamped to a range a human can throw, and the
//      report says the number.
//
//   4. The layer that matters is the COLLIDER's layer, not the root's.
//      Unity's collision matrix, EasyBend's OverlapSphere mask and every
//      other layer query read the GameObject the Collider sits on — which
//      in the canonical hierarchy is Anchor, not the root. Setting only
//      the root to "Item" produces a prop that looks correctly configured
//      in the inspector and is invisible to every layer-filtered query.
//      So the layer goes on the root AND on the anchor's colliders, and
//      deliberately NOT on anything under the Visual, whose layer drives
//      renderer culling — that exclusion is enforced against VISUAL, not
//      against Motion. An Interactive plan never builds a Motion node
//      (ConvertReadyExecutor: needsMotion is Bendy-and-not-skinned), so a
//      Motion-only guard is dead code and lets the walk reach straight into
//      the source model's own colliders and a previous run's jiggle bones.
//
//  THE RULE THAT COVERS ALL OF THE ABOVE: NOTHING THIS FILE EMITS OWNS THE
//  PROP ROOT'S TRANSFORM. Point 1 is the special case everyone hits first;
//  the Rigidbody is the same bug wearing a different hat.
//
//  The park loader places the root. A Rigidbody there hands that transform
//  to PhysX on the first live frame, so a prop settles, slides or topples
//  away from the spot the park authored — and the park data still says it
//  is where it was put. It also drags the prop's floor footprint with it:
//  GapFiller and FloorCutout consume PropTemplate's footprint, so a thrown
//  vase carries its hole in the floor around the room. (VoronoiFracture
//  detaches its shards for exactly this reason; a body on the root is the
//  same failure without the detach.)
//
//  SO THE BODY GOES ON THE ANCHOR. The root stays where the park put it and
//  the physics moves what is under it. Three things fall out for free:
//   • The fitted collider is already on Anchor, so it attaches to this body
//     rather than to an ancestor's.
//   • Unity delivers OnCollision*/OnTrigger* to the collider's GameObject
//     AND to the GameObject of its attached Rigidbody — both are Anchor, so
//     the messages land in one place whether or not the plan asked for a
//     body at all. The generated Lua script is on Anchor for that reason
//     (LuaMessageRelays.Bind adds its relay to the scripted object only,
//     with no ancestor walk), and the two decisions now agree instead of
//     depending on plan.addRigidbody.
//   • A prop converted by an earlier version has its body on the root; that
//     one is removed rather than left to nest, because a nested body's local
//     pose fights its parent's motion.
//
//  THE COST, STATED: PropTemplate.SurfaceHeight and the footprint math read
//  the ROOT transform, so for a prop that has been thrown they describe
//  where it was placed rather than where it now is. That is the right way
//  round — a hole in the floor that stays put beats one that follows a
//  vase across the room — but it does mean the footprint of an in-flight
//  prop is stale, by design.
//
//  WHAT THIS FILE DELIBERATELY DOES NOT DO: add EasyAudio. The spec offers
//  a contact sound as a Custom-only extra, and ConversionPlan carries no
//  flag for it, so nothing here adds it. A converter that silently adds
//  audio components is a converter people stop trusting — the same
//  reasoning as (2), one level up.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class BehaviorPackBuilder
    {
        // ── Mass ────────────────────────────────────────────────────────

        /// <summary>
        /// Between balsa (160) and oak (750). Props are shells, not solids:
        /// a crate is boards around air and a lantern is glass around
        /// nothing, and the mesh volume we multiply is the volume of the
        /// SHELL, not of the box it occupies. 400 puts a 0.34 m lantern at
        /// about 1.2 kg, which is what it should feel like in a hand.
        /// </summary>
        public const float DensityKgPerCubicMetre = 400f;

        /// Below this a prop has no throwable weight at all and reads as a
        /// paper cup; above it a two-handed lift feels like a bug.
        public const float MinMassKg = 0.1f;
        public const float MaxMassKg = 20f;

        /// When Read/Write is off on the model we cannot integrate the
        /// triangle soup, so we fall back to the bounds volume times a
        /// solidity guess. Half a bounding box is what most props measure.
        public const float UnreadableSolidity = 0.5f;

        /// Last resort when there is no measurable geometry at all.
        public const float FallbackMassKg = 1f;

        /// A prop whose thinnest axis is under this tunnels through the
        /// floor when thrown, at a Quest's fixed timestep. Speculative CCD
        /// is the cheap continuous mode and is the right trade here.
        public const float SmallPropMeters = 0.12f;

        // ── Bend ────────────────────────────────────────────────────────

        /// The bend should start before the hand touches, so the trigger is
        /// twice the prop's own footprint radius. Floored at
        /// BendSettings.minDetectionRadius, capped at MaxDetectionRadius.
        public const float DetectionRadiusScale = 2f;

        /// EasyBend queries with a 32-collider stack buffer
        /// (EasyBend.cs:24). A sphere big enough to overflow it picks an
        /// arbitrary 32 colliders per frame and bends towards whichever the
        /// broadphase happened to return, which reads as random twitching.
        public const float MaxDetectionRadius = 1.5f;

        // ── Layers ──────────────────────────────────────────────────────

        /// "Item" and "ItemNonInteractor" both exist; "Item" is the one that
        /// reads as "the player can touch this".
        public const string InteractiveLayerName = "Item";
        public const string PlayerLayerName = "Player";

        // DreamPark Extensions clamps throwables to these on release.
        public const float ThrowableLinearDamping = 0.2f;
        public const float ThrowableAngularDamping = 0.2f;

        // ── Result ──────────────────────────────────────────────────────

        /// <summary>
        /// What Stage 6 actually did. Every field here is also stated in the
        /// ConversionResult; this struct exists so the executor can branch
        /// (e.g. skip the fracture bake when the jiggle branch was taken)
        /// without re-deriving anything.
        /// </summary>
        public struct BehaviorResult
        {
            public bool ok;
            public BehaviorPack pack;

            public bool rigidbodyAdded;
            /// The mass actually written, in kg. 0 when no Rigidbody was added.
            public float massKg;

            public bool interactableAdded;
            /// True only when this run wrote the example filter. False when a
            /// filter was already there and was left alone.
            public bool interactionFilterSeeded;

            public bool layerApplied;
            public string layerName;

            /// EasyBend was placed on the Motion node.
            public bool bendApplied;
            /// World-space metres, as written to EasyBend.detectionRadius.
            public float detectionRadius;

            /// The skinned branch was taken instead of EasyBend.
            public bool jiggleApplied;
            public JiggleRigBuilder.RigResult jiggle;

            /// <summary>
            /// True when the jiggle rig put colliders inside the prop, which
            /// PropTemplate.TryGetColliderFootprint (PropTemplate.cs:354) will
            /// pick up and which MOVE every frame. The PropTemplate stage
            /// should answer this by setting useColliderBounds = false and
            /// writing <see cref="suggestedFootprintMeters"/> into
            /// customFootprintMeters, so GapFiller reads a stable footprint.
            /// </summary>
            public bool footprintNeedsManualOverride;

            /// <summary>
            /// Bind-pose footprint in metres (X, Z) taken from the Stage 4
            /// anchor-local bounds. Zero when nothing was measurable.
            /// </summary>
            public Vector2 suggestedFootprintMeters;

            /// The measurement the mass and radius came from, so the caller
            /// does not walk the triangle soup a third time.
            public ColliderFitter.MeshMeasurement measurement;

            public string error;
        }

        // ── Entry points ────────────────────────────────────────────────

        /// <summary>
        /// Apply the plan's behavior pack to an already-built canonical
        /// hierarchy. <paramref name="motion"/> may be null when the plan
        /// animates nothing; <paramref name="anchor"/> and
        /// <paramref name="visual"/> may be null on tracks that have neither.
        ///
        /// This overload measures the visual itself. Prefer the overload
        /// that takes the measurement Stage 4 already produced — walking the
        /// triangles twice is wasteful and lets the mass and the collider
        /// disagree about the same mesh.
        /// </summary>
        public static BehaviorResult Apply(GameObject root, GameObject anchor, GameObject motion,
                                           GameObject visual, ConversionPlan plan, ConversionResult r)
        {
            // Measure in the same space Stage 4 measures in — ANCHOR-local —
            // so the volume and bounds mean the same thing in both stages.
            Transform space = anchor != null ? anchor.transform
                            : (root != null ? root.transform : null);
            ColliderFitter.MeshMeasurement m = visual != null
                ? ColliderFitter.Measure(visual, space)
                : new ColliderFitter.MeshMeasurement();

            return Apply(root, anchor, motion, visual, plan, r, m);
        }

        /// <summary>
        /// As above, reusing the measurement from ColliderFitter.FitResult.
        /// The measurement must be in ANCHOR-local space, which is what
        /// ColliderFitter.Fit produces.
        /// </summary>
        public static BehaviorResult Apply(GameObject root, GameObject anchor, GameObject motion,
                                           GameObject visual, ConversionPlan plan, ConversionResult r,
                                           ColliderFitter.MeshMeasurement measurement)
        {
            BehaviorResult result = new BehaviorResult();
            result.measurement = measurement;

            if (root == null)
            {
                result.error = "behavior: no prop root — nothing to configure";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }
            if (plan == null)
            {
                result.error = "behavior: no ConversionPlan";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            result.pack = plan.behavior;
            RegisterUndo(root, "Convert to DreamPark-Ready (behavior)");

            // Anchor-local units are only metres when the anchor is unscaled.
            // Everything below that reaches world space — the Rigidbody mass
            // and EasyBend's OverlapSphere radius — has to be corrected, and
            // in the canonical hierarchy this is 1.0.
            float anchorScale = UniformScaleOf(anchor);

            // ── Rigidbody ───────────────────────────────────────────────
            // Driven by plan.addRigidbody rather than by the pack, so the
            // Custom sheet can put a body on anything. The mass estimate
            // lives in one place regardless of which preset asked for it.
            if (plan.addRigidbody)
            {
                ApplyRigidbody(root, anchor, plan, measurement, anchorScale, ref result, r);
            }
            else
            {
                // Componentizer removes when shouldExist is false, so a
                // re-run with the flag cleared takes the body back off
                // instead of leaving the old one behind. Both nodes: the
                // body used to be written to the root, so a prop converted
                // by an earlier version has one there.
                StripRigidbody(root, r);
                if (anchor != root) StripRigidbody(anchor, r);
            }

            // ── The pack ────────────────────────────────────────────────
            switch (plan.behavior)
            {
                case BehaviorPack.Interactive:
                    ApplyInteractive(root, anchor, motion, visual, plan, ref result, r);
                    break;

                case BehaviorPack.Bendy:
                    ApplyBendy(root, anchor, motion, visual, plan, measurement, anchorScale, ref result, r);
                    break;

                case BehaviorPack.Shatter:
                    // The fracture bake is the Shatter stage's job — it owns
                    // the piece meshes, the sub-assets and the generated Lua
                    // script. Stage 6's only contribution is the body the
                    // intact prop needs before it breaks, which was added
                    // above from plan.addRigidbody.
                    //
                    // ORDER MATTERS AT THE CALL SITE: VoronoiFracture.BakeInto
                    // checks for that body and reports its absence, so the
                    // executor must call this method BEFORE the fracture bake,
                    // not instead of it.
                    Report(r, DecisionKind.Skipped,
                        "behavior: fracture pieces are baked by the Shatter stage, not by the behavior pack");
                    break;

                default:
                    break;
            }

            if (string.IsNullOrEmpty(result.error)) result.ok = true;
            return result;
        }

        // ── Interactive ─────────────────────────────────────────────────

        static void ApplyInteractive(GameObject root, GameObject anchor, GameObject motion, GameObject visual,
                                     ConversionPlan plan, ref BehaviorResult result, ConversionResult r)
        {
            // Interactive is a physics pack plus a SCRIPT: Rigidbody (mass +
            // damping), the fitted collider, and a Lua file in the prop's kit
            // folder that detects a hit and reports it.
            //
            // Not an Interactable. That component's whole surface is a
            // serialized filter array wired through UnityEvents — it does not
            // diff, does not grep, and the only way to find out what a prop
            // does is to click through an inspector. The generated script says
            // the same thing in six lines of readable Lua, with the speed gate
            // and the tag gate visible, and it is somewhere the creator can
            // type the next line of their game.
            result.interactableAdded = false;

            ReportStrayInteractables(root, anchor, motion, visual, r);

            if (!plan.addRigidbody)
            {
                Report(r, DecisionKind.Skipped,
                    "Interactive without a Rigidbody is just the fitted collider — turn on addRigidbody for a throwable");
            }

            // On the anchor, not the root: LuaMessageRelays.Bind adds its
            // collision relay to the LuaBehaviour's own GameObject with no
            // ancestor walk, and Unity routes OnCollisionEnter to the
            // collider's GameObject and to that collider's attached body. The
            // anchor always has the collider; the root only has a body when
            // plan.addRigidbody is set. Anchor is the host that hears a hit
            // either way.
            LuaScriptEmitter.EmitInteractive(anchor, root != null ? root.name : anchor.name, plan, r);

            ApplyCollisionLayer(root, anchor, motion, visual, InteractiveLayerName, ref result, r);
        }

        /// <summary>
        /// REPORT, DO NOT DELETE.
        ///
        /// The first version of this removed any Interactable whose filter
        /// array was empty, on all four nodes, on every Interactive convert —
        /// on the theory that an empty one is a re-run artifact. It is not. A
        /// freshly added Interactable has an empty array by definition, so the
        /// component a creator dropped on their prop five seconds ago (or
        /// drives entirely from Lua, which needs no serialized filters) looks
        /// identical to leftover junk and got silently deleted. The one case
        /// the deletion was aimed at — a prop converted by an older version of
        /// this tool — carries a seeded filter and so takes the other branch
        /// anyway, which is to say the delete only ever fired on the case it
        /// was supposed to protect.
        ///
        /// Converting must never remove a component it did not add. Say what
        /// is there and let the creator decide.
        /// </summary>
        static void ReportStrayInteractables(GameObject root, GameObject anchor, GameObject motion,
                                             GameObject visual, ConversionResult r)
        {
            GameObject[] nodes = new GameObject[] { root, anchor, motion, visual };
            for (int i = 0; i < nodes.Length; i++)
            {
                GameObject node = nodes[i];
                if (node == null) continue;
                Interactable old = node.GetComponent<Interactable>();
                if (old == null) continue;

                int wired = old.interactionFilters != null ? old.interactionFilters.Length : 0;
                Report(r, DecisionKind.Skipped,
                    "'" + node.name + "' carries an Interactable with " + wired + " wired filter(s) — "
                    + "left exactly as it is. Interactive no longer adds that script (it is gameplay, "
                    + "not convert output), so if this one came from an older run and you do not want "
                    + "it, remove it by hand.");
            }
        }

        /// <summary>
        /// The GameObject that will actually receive OnCollision*/OnTrigger*
        /// when the prop has NO Rigidbody anywhere: the one carrying the fitted
        /// collider. Usually Anchor. But ColliderFitter puts a MeshCollider on
        /// a "Collision" child of Anchor whenever the source mesh is not
        /// aligned with it, and a static collider on a child delivers to that
        /// CHILD — not to Anchor, not to the root.
        ///
        /// One such child is a host; several is a compound that only a
        /// Rigidbody can aggregate, so that case says so and stays on Anchor
        /// rather than scattering N Interactables the creator would have to
        /// wire N times.
        /// </summary>
        static GameObject StaticCallbackHost(GameObject root, GameObject anchor, GameObject motion,
                                             GameObject visual, ConversionResult r)
        {
            if (anchor == null) return root;
            if (anchor.GetComponent<Collider>() != null) return anchor;

            GameObject only = null;
            bool several = false;
            foreach (Collider c in anchor.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue;
                // The model's own colliders and any jiggle-rig bones are not
                // the fitted collision and are not ours to build on.
                if (visual != null && IsDescendantOf(c.transform, visual.transform)) continue;
                if (motion != null && IsDescendantOf(c.transform, motion.transform)) continue;

                // Distinct OBJECTS are what matters, not distinct colliders:
                // two colliders on one node still deliver to that one node.
                if (only == null) { only = c.gameObject; continue; }
                if (c.gameObject != only) { several = true; break; }
            }

            if (!several && only != null)
            {
                Report(r, DecisionKind.Added, string.Format(
                    "the fitted collider sits on '{0}' rather than on '{1}', and with no Rigidbody a static collider "
                    + "delivers its callbacks to its OWN GameObject — so the Interactable went there.",
                    only.name, anchor.name));
                return only;
            }

            if (several)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "the fitted collision is spread over several objects under '{0}' and there is no Rigidbody to "
                    + "aggregate them, so each one receives its own callbacks and a single Interactable cannot see all "
                    + "of them. Turn on the Rigidbody (a body on the root collects every descendant collider), or accept "
                    + "that only '{0}' is wired.", anchor.name));
            }

            return anchor;
        }

        static void StripRigidbody(GameObject go, ConversionResult r)
        {
            if (go == null || go.GetComponent<Rigidbody>() == null) return;
            Componentizer.DoComponent<Rigidbody>(go, false);
            Report(r, DecisionKind.Skipped,
                "Rigidbody on '" + go.name + "' — the plan does not ask for one; removed");
        }

        /// <summary>
        /// The body goes on the ANCHOR. Nothing the converter emits owns the
        /// prop ROOT's transform — see the header.
        /// </summary>
        static void ApplyRigidbody(GameObject root, GameObject anchor, ConversionPlan plan,
                                   ColliderFitter.MeshMeasurement m, float anchorScale,
                                   ref BehaviorResult result, ConversionResult r)
        {
            GameObject host = anchor != null ? anchor : root;

            // A prop converted by an earlier version has its body on the root,
            // where physics drives the transform the park loader placed. Two
            // bodies would also nest, and a nested body's local pose fights
            // its parent's motion — so the old one comes off rather than
            // being left as a second, invisible simulation.
            if (host != root && root.GetComponent<Rigidbody>() != null)
            {
                Componentizer.DoComponent<Rigidbody>(root, false);
                Report(r, DecisionKind.Skipped, "Rigidbody on the root '" + root.name
                    + "' — removed and re-created on '" + host.name + "'. A body on the root drives the "
                    + "transform the park loader placed, so the prop drifts off its authored spot; on the "
                    + "anchor the root stays put and the physics moves what is under it.");
            }

            // Read the PREVIOUS state before Componentizer hands the body
            // back: converting an existing .prefab often finds one already
            // there, and the two fields that silently make the prop immovable
            // are exactly the two nothing used to write.
            Rigidbody existing = host.GetComponent<Rigidbody>();
            bool wasKinematic = existing != null && existing.isKinematic;
            bool wasConstrained = existing != null && existing.constraints != RigidbodyConstraints.None;

            Rigidbody rb = Componentizer.DoComponent<Rigidbody>(host, true);
            if (rb == null)
            {
                result.error = "Rigidbody could not be added to '" + host.name + "'";
                Report(r, DecisionKind.Failed, result.error);
                return;
            }

            string basis;
            float mass = EstimateMassKg(m, anchorScale, out basis);
            rb.mass = mass;
            rb.useGravity = true;
            // Match DreamPark's throwable clamp (Extensions sets both to 0.2
            // on release). Unity's defaults (0 / 0.05) leave a thrown prop
            // sliding and spinning forever.
            rb.linearDamping = ThrowableLinearDamping;
            rb.angularDamping = ThrowableAngularDamping;

            // Write the WHOLE state the converter owns, not just the fields
            // that differ from Unity's defaults on a fresh component. A body
            // inherited from a source prefab can arrive kinematic or with
            // FreezeAll, and either one produces "ADDED Rigidbody, 1.2 kg" on
            // a prop that never moves — and invalidates Stage 4's convex
            // enforcement, which is justified by the body moving.
            rb.isKinematic = false;
            rb.constraints = RigidbodyConstraints.None;
            // Reset before the thin-axis branch below may raise it: otherwise a
            // body left on ContinuousSpeculative by an earlier run keeps paying
            // for CCD after the prop is no longer thin.
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

            // A headset renders at 72–120 Hz against a 50 Hz fixed timestep.
            // Without interpolation a prop in the hand visibly stutters, and
            // it is the first thing anyone notices about a converted prop.
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            string ccd = "";
            float thinnest = MinAbsComponent(m.bounds.size) * anchorScale;
            if (m.ok && thinnest > 0f && thinnest < SmallPropMeters)
            {
                // Speculative rather than ContinuousDynamic: it costs a
                // broadphase inflation instead of a sweep, which is what a
                // Quest can afford.
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                ccd = string.Format(", speculative CCD (thinnest axis {0:0.###} m would tunnel when thrown)", thinnest);
            }

            EditorUtility.SetDirty(rb);
            result.rigidbodyAdded = true;
            result.massKg = mass;

            Report(r, DecisionKind.Added, string.Format(
                "Rigidbody on '{0}', {1:0.##} kg, damping {2:0.##}/{3:0.##} — {4}{5}",
                host.name, mass, ThrowableLinearDamping, ThrowableAngularDamping, basis, ccd));

            // Never silent: the creator authored one of these by hand, or
            // inherited it from the source prefab, and the prop's behaviour
            // just changed underneath them.
            if (wasKinematic || wasConstrained)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "the Rigidbody already on '{0}' was {1} — reset to a moving, unconstrained body, because a kinematic or "
                    + "frozen body makes the prop immovable while the report claims a mass, and Stage 4 forces a CONVEX "
                    + "collider on the assumption that this body moves. Re-apply it by hand if it was deliberate.",
                    root.name,
                    wasKinematic && wasConstrained ? "kinematic and constrained"
                                                   : (wasKinematic ? "kinematic" : "constrained")));
            }
        }

        /// <summary>
        /// Mass in kg from the mesh volume Stage 4 measured, times a density
        /// estimate, clamped to MinMassKg..MaxMassKg.
        /// <paramref name="anchorScale"/> converts the measurement's space
        /// into metres; pass 1 when the measurement is already in metres.
        /// <paramref name="basis"/> is the sentence the report quotes.
        /// </summary>
        public static float EstimateMassKg(ColliderFitter.MeshMeasurement m, float anchorScale, out string basis)
        {
            float cubic = anchorScale * anchorScale * anchorScale;

            float volume;
            if (m.ok && m.meshVolume > 0f)
            {
                volume = m.meshVolume * cubic;
                basis = string.Format("mesh volume {0:0.####} m³ × {1:0} kg/m³", volume, DensityKgPerCubicMetre);

                if (m.unreadableMeshCount > 0)
                {
                    // Some meshes contributed bounds but no volume, so the
                    // real prop is heavier than this. Say so rather than
                    // letting the number read as measured.
                    basis += string.Format(" (a LOWER BOUND — {0} mesh(es) have Read/Write disabled and could not be integrated)",
                        m.unreadableMeshCount);
                }
            }
            else if (m.ok && m.boundsVolume > 0f)
            {
                volume = m.boundsVolume * cubic * UnreadableSolidity;
                basis = string.Format("bounds volume × {0:0.##} solidity = {1:0.####} m³ × {2:0} kg/m³ "
                                    + "(the mesh is not readable, so the volume could not be integrated)",
                                    UnreadableSolidity, volume, DensityKgPerCubicMetre);
            }
            else
            {
                basis = "no measurable geometry, so " + FallbackMassKg.ToString("0.##") + " kg placeholder";
                return FallbackMassKg;
            }

            float raw = volume * DensityKgPerCubicMetre;
            float clamped = Mathf.Clamp(raw, MinMassKg, MaxMassKg);
            if (!Mathf.Approximately(raw, clamped))
            {
                basis += string.Format(" = {0:0.##} kg, clamped to {1:0.##}", raw, clamped);
            }
            return clamped;
        }

        // ── Bendy ───────────────────────────────────────────────────────

        static void ApplyBendy(GameObject root, GameObject anchor, GameObject motion, GameObject visual,
                               ConversionPlan plan, ColliderFitter.MeshMeasurement m, float anchorScale,
                               ref BehaviorResult result, ConversionResult r)
        {
            BendSettings bend = plan.bend != null ? plan.bend : new BendSettings();

            // A SkinnedMeshRenderer takes the other branch entirely: EasyBend
            // rotates ONE transform, so on a rig it swings the whole
            // character stiffly and looks broken.
            SkinnedMeshRenderer skinned = JiggleRigBuilder.FindSkinnedRenderer(visual);
            if (skinned != null)
            {
                // EasyBend must not survive on the Motion node from a previous
                // non-skinned run, or the rig gets bent AND jiggled.
                if (motion != null && motion.GetComponent<EasyBend>() != null)
                {
                    Componentizer.DoComponent<EasyBend>(motion, false);
                    Report(r, DecisionKind.Skipped, "EasyBend on '" + motion.name
                        + "' — removed; this model is skinned and takes the jiggle rig instead");
                }

                // The bind-pose footprint, in metres, for the PropTemplate
                // stage: the rig's bone colliders join PropTemplate's footprint
                // and then MOVE, so the prop needs a manual one. m.bounds is
                // anchor-local and, for a skinned renderer, comes from
                // localBounds — i.e. the bind pose, which is the stable shape.
                Vector2 bindPose = m.ok
                    ? new Vector2(Mathf.Abs(m.bounds.size.x) * anchorScale, Mathf.Abs(m.bounds.size.z) * anchorScale)
                    : Vector2.zero;

                result.jiggle = JiggleRigBuilder.Apply(visual, bend, r, bindPose);
                result.jiggleApplied = result.jiggle.ok;
                if (!result.jiggle.ok && !string.IsNullOrEmpty(result.jiggle.error))
                {
                    result.error = result.jiggle.error;
                }

                if (result.jiggleApplied)
                {
                    result.footprintNeedsManualOverride = result.jiggle.footprintNeedsManualOverride;
                    result.suggestedFootprintMeters = result.jiggle.suggestedFootprintMeters;
                    WarnJiggleVersusAnchorCollider(anchor, visual, r);
                }
                return;
            }

            // ── Non-skinned: EasyBend, on the Motion node, never the root ──
            if (motion == null)
            {
                // Refusing is the whole point. On the root, EasyBend takes
                // ownership of the transform the park loader poses, and every
                // bendy prop in the park snaps to the prefab's yaw on the
                // first live frame — which looks like a park-data bug and is
                // not one. See the hazard note at the top of this file.
                result.error = "Bendy: no Motion node was built, and EasyBend must not go on the prop root — "
                             + "it writes transform.localRotation every frame and would overwrite the rotation "
                             + "the park loader authored on the spawned root. Nothing was added.";
                Report(r, DecisionKind.Failed, result.error);
                return;
            }

            // A previous run may have left the C# component here.
            if (motion.GetComponent<EasyBend>() != null)
            {
                Componentizer.DoComponent<EasyBend>(motion, false);
                Report(r, DecisionKind.Skipped, "EasyBend on '" + motion.name
                    + "' — removed; the bend is a Lua script in this prop's own folder now, "
                    + "so you can change what leaning means instead of only how far it leans");
            }

            // ── detectionRadius ─────────────────────────────────────────
            // EasyBend ships 0.75 m: a metre-wide trigger around a 5 cm coin.
            string radiusBasis;
            float radius = DetectionRadiusFor(m, bend, anchorScale, out radiusBasis);

            // ── detectionOffset ─────────────────────────────────────────
            // Centre the query on the prop's own middle rather than on the
            // Motion node's origin, which after Stage 3a sits on the FLOOR.
            // A sphere centred at the base is half underground: it misses a
            // hand reaching for the top of a tall prop, and it is centred on
            // the one surface the prop is guaranteed to be touching.
            Vector3 offset = m.ok && anchor != null
                ? motion.transform.InverseTransformPoint(anchor.transform.TransformPoint(m.bounds.center))
                : Vector3.zero;

            string luaPath = LuaScriptEmitter.EmitBend(motion, root != null ? root.name : motion.name,
                                                       radius, offset, bend, r);
            if (string.IsNullOrEmpty(luaPath))
            {
                result.error = "Bendy: the bend script could not be written, so nothing bends this prop";
                Report(r, DecisionKind.Failed, result.error);
                return;
            }

            result.bendApplied = true;
            result.detectionRadius = radius;

            Report(r, DecisionKind.Added, string.Format(
                "bend on '{0}' — tilt {1:0.#}\u00b0, spring {2:0.#}, damping {3:0.#}",
                motion.name, bend.maxTiltAngle, bend.springStrength, bend.springDamping));

            // detectionMask is ~0 — EVERY layer — and that is deliberate.
            // EasyBend's shipped ~0 was a problem only because a wall or a
            // floor never moves out of range, so the prop leans over and
            // stays leaned. requireRigidbody is what actually solves that:
            // static geometry has no body, so it is filtered whatever layer
            // it is on. Masking by layer instead would mean a thrown prop or
            // a creature on some other layer could not push this one, and
            // that restriction is invisible until someone wonders why.
            Report(r, DecisionKind.Added,
                "bend.detectionMask = every layer, with requireRigidbody on — the Rigidbody test is what "
                + "keeps static walls and floors from leaning the prop over permanently, and it does it "
                + "without ruling out anything else that moves. The script also always accepts the player, "
                + "so the bend does not depend on whether the hand rig carries a body.");

            // MEASURED only when something was actually measured. With !m.ok
            // the radius is BendSettings.minDetectionRadius — a constant — and
            // filing a fallback constant as MEASURED is precisely what that
            // vocabulary exists to prevent (ConvertReadyPlan, DecisionKind).
            Report(r, m.ok ? DecisionKind.Measured : DecisionKind.Guessed, string.Format(
                "bend.detectionRadius = {0:0.###} m (EasyBend shipped 0.75 — a metre-wide trigger around a "
                + "5 cm prop): {1}", radius, radiusBasis));

            WarnSelfBend(root, anchor, plan, r);
        }

        /// <summary>
        /// EasyBend's detectionRadius, in WORLD metres — it feeds
        /// Physics.OverlapSphere, which does not care about the hierarchy the
        /// measurement was taken in. Twice the prop's own footprint radius,
        /// floored at <see cref="BendSettings.minDetectionRadius"/> and
        /// capped at <see cref="MaxDetectionRadius"/>.
        ///
        /// CALLER CONTRACT: when <paramref name="m"/>.ok is false this returns
        /// the floor CONSTANT and nothing was measured, so the caller must file
        /// the decision as GUESSED rather than MEASURED. The basis string says
        /// so in words; the kind has to say so in the report.
        /// </summary>
        public static float DetectionRadiusFor(ColliderFitter.MeshMeasurement m, BendSettings bend,
                                               float anchorScale, out string basis)
        {
            float floor = bend != null ? Mathf.Max(0.01f, bend.minDetectionRadius) : 0.15f;

            if (!m.ok)
            {
                basis = string.Format("nothing measurable to fit to, so the floor of {0:0.###} m was used", floor);
                return floor;
            }

            float footprint = ColliderFitter.HorizontalRadius(m.bounds) * anchorScale;
            float fitted = footprint * DetectionRadiusScale;
            float radius = Mathf.Max(floor, fitted);

            if (radius > MaxDetectionRadius)
            {
                // EasyBend's overlap buffer is 32 colliders. A sphere large
                // enough to overflow it samples an arbitrary subset each
                // frame and the bend direction flickers.
                basis = string.Format(
                    "footprint radius {0:0.###} m × {1:0.#} = {2:0.###} m, capped at {3:0.##} m — EasyBend's overlap buffer "
                    + "holds 32 colliders and a larger sphere overflows it",
                    footprint, DetectionRadiusScale, fitted, MaxDetectionRadius);
                return MaxDetectionRadius;
            }

            basis = radius > fitted
                ? string.Format("footprint radius {0:0.###} m × {1:0.#} = {2:0.###} m, raised to the {3:0.###} m floor",
                                footprint, DetectionRadiusScale, fitted, floor)
                : string.Format("footprint radius {0:0.###} m × {1:0.#}", footprint, DetectionRadiusScale);
            return radius;
        }

        /// <summary>
        /// The one combination that reintroduces the bug the detectionMask
        /// override exists to prevent. EasyBend recognises its own colliders
        /// through GetComponentsInChildren&lt;Collider&gt; on ITS OWN
        /// transform (EasyBend.cs:34) — and in the canonical hierarchy the
        /// collider is on Anchor, which is Motion's PARENT, not its child.
        /// So the prop's own collider is never recognised as self. It is
        /// normally filtered out anyway: it has no Rigidbody (requireRigidbody)
        /// and it is not on Player or Item (detectionMask). Put a Rigidbody on
        /// the anchor AND the prop on Item, and both filters stop applying — the
        /// prop then bends away from itself, at full weight, forever.
        /// </summary>
        static void WarnSelfBend(GameObject root, GameObject anchor, ConversionPlan plan, ConversionResult r)
        {
            if (!plan.addRigidbody) return;

            int item = LayerMask.NameToLayer(InteractiveLayerName);
            if (item < 0) return;

            bool onItem = root.layer == item || (anchor != null && anchor.layer == item);
            if (!onItem) return;

            Report(r, DecisionKind.Skipped, string.Format(
                "Bendy + Rigidbody + the '{0}' layer — EasyBend sits on '{1}' and looks for its own colliders among that "
                + "node's CHILDREN, but the collider is on its parent Anchor. With a Rigidbody on the root and the "
                + "collider on '{0}', neither requireRigidbody nor detectionMask filters it out, so the prop bends away "
                + "from ITSELF permanently. Turn off the Rigidbody, or move the collider off the '{0}' layer.",
                InteractiveLayerName, root.name));
        }

        /// <summary>
        /// The jiggle rig's collision comes from its bone colliders, and the
        /// Anchor collider Stage 4 fitted encloses the whole visual — so every
        /// bone starts the frame inside it and PhysX spends the frame pushing
        /// them out. This is not a warning about a rare case; it is what
        /// happens every time both exist.
        ///
        /// The walk is GetComponentsInChildren, NOT GetComponents, and that is
        /// the whole reason this warning fires at all on the case it was
        /// written for. On a skinned model Stage 4 almost never leaves a
        /// collider on Anchor itself: ColliderFitter samples the skinned mesh
        /// in smr.transform's space, and ApplyMesh's aligned-single-mesh fast
        /// path fails because Visual carries the Stage 2 fit scale and the
        /// Stage 3a ground offset — so the MeshCollider lands on a "Collision"
        /// CHILD of Anchor. Inspecting Anchor's own components found nothing
        /// and the warning stayed silent on exactly the skinned Bendy prop it
        /// exists for.
        /// </summary>
        static void WarnJiggleVersusAnchorCollider(GameObject anchor, GameObject visual, ConversionResult r)
        {
            if (anchor == null) return;

            Collider solid = null;
            foreach (Collider c in anchor.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                // The rig's own bone boxes live under Visual and are non-trigger,
                // so without this they match first and the warning ends up
                // describing the rig to itself.
                if (visual != null && IsDescendantOf(c.transform, visual.transform)) continue;
                solid = c;
                break;
            }
            if (solid == null) return;

            string where = solid.gameObject == anchor
                ? "'" + anchor.name + "'"
                : "'" + solid.gameObject.name + "' under '" + anchor.name + "'";

            Report(r, DecisionKind.Skipped, string.Format(
                "the jiggle rig's bone colliders start INSIDE the solid {0} on {1}, which encloses the whole visual — "
                + "PhysX will push them apart every frame and the rig will jitter in place. A skinned Bendy prop should "
                + "use ColliderChoice.None and let the bone colliders be its collision, or make that collider a trigger "
                + "(PropTemplate's footprint reads triggers too, so the prop's static shape stays in it either way). "
                + "Read that together with the footprint line above: the rig's bones are in the footprint as well, and "
                + "they move — so this prop needs a manual PropTemplate footprint whichever of the two you pick.",
                solid.GetType().Name, where));
        }

        // ── Layers ──────────────────────────────────────────────────────

        /// <summary>
        /// Put the prop on <paramref name="layerName"/> where it counts: the
        /// root (so anything walking up the hierarchy finds it) and every
        /// collider under Anchor (so the collision matrix and every
        /// layer-masked query find it). NOT anything under the Visual — a
        /// renderer's layer drives culling masks, and moving it silently
        /// changes what the prop is drawn by.
        ///
        /// The exclusion is tested against VISUAL, not against Motion. Guarding
        /// on Motion alone is dead code on the only path that reaches here:
        /// an Interactive plan never builds a Motion node (the executor sets
        /// needsMotion only for Bendy-and-not-skinned), so Visual is a direct
        /// child of Anchor and the walk goes straight into it — silently
        /// relayering colliders that came with a source .prefab's model, and
        /// the whole bone skeleton of a jiggle rig left by an earlier Bendy
        /// run. Visual is under Motion whenever Motion exists, so testing
        /// Visual subsumes the Motion test; both are kept because a collider
        /// authored on the Motion node itself is not ours to rewrite either.
        /// </summary>
        static void ApplyCollisionLayer(GameObject root, GameObject anchor, GameObject motion, GameObject visual,
                                        string layerName, ref BehaviorResult result, ConversionResult r)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                // A project missing a standard layer is broken in ways this
                // converter cannot fix, but it is not a reason to fail the
                // conversion — the prop is otherwise complete.
                Report(r, DecisionKind.Skipped, string.Format(
                    "layer '{0}' — not defined in this project's TagManager, so the prop was left on '{1}'. "
                    + "Interaction queries that filter on '{0}' will not see it.",
                    layerName, LayerMask.LayerToName(root.layer)));
                return;
            }

            int changed = 0;
            if (root.layer != layer) { root.layer = layer; changed++; }

            int colliders = 0;
            if (anchor != null)
            {
                if (anchor.layer != layer) { anchor.layer = layer; changed++; }

                // Mesh colliders that could not sit directly on Anchor get one
                // child each (ColliderFitter.CollisionChildPrefix). Those carry
                // the actual collision and need the layer just as much.
                int skippedUnderVisual = 0;
                foreach (Collider c in anchor.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    // Skip the rendered subtree and anything animated above it:
                    // those colliders belong to the source model or to a jiggle
                    // rig, and their layers are not ours to rewrite.
                    if (visual != null && IsDescendantOf(c.transform, visual.transform)) { skippedUnderVisual++; continue; }
                    if (motion != null && IsDescendantOf(c.transform, motion.transform)) { skippedUnderVisual++; continue; }
                    if (c.gameObject.layer != layer) { c.gameObject.layer = layer; changed++; }
                    colliders++;
                }

                if (skippedUnderVisual > 0)
                {
                    Report(r, DecisionKind.Skipped, string.Format(
                        "{0} collider(s) under '{1}' were left on their own layer — a renderer's layer drives culling masks, "
                        + "so relayering the model's own colliders changes what the prop is drawn by. Only the fitted "
                        + "collision under '{2}' was moved to '{3}'.",
                        skippedUnderVisual, visual != null ? visual.name : motion.name, anchor.name, layerName));
                }
            }

            result.layerApplied = true;
            result.layerName = layerName;
            EditorUtility.SetDirty(root);

            Report(r, DecisionKind.Added, string.Format(
                "layer '{0}' on '{1}' and its {2} collider object(s) — the collision matrix and every layer-masked query "
                + "read the COLLIDER's layer, not the root's, so setting only the root would look right in the inspector "
                + "and be invisible to physics ({3} object(s) changed)",
                layerName, root.name, colliders, changed));
        }

        /// <summary>
        /// A LayerMask built from names that actually exist.
        /// LayerMask.GetMask returns 0 for an unknown name, and a 0 mask means
        /// "detect nothing" — which is exactly as silent as the ~0 default
        /// this exists to replace. <paramref name="resolved"/> and
        /// <paramref name="missing"/> are pipe-joined for the report.
        /// </summary>
        public static int LayerMaskFor(string[] names, out string resolved, out string missing)
        {
            int mask = 0;
            resolved = "";
            missing = "";

            if (names == null) return 0;

            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                if (string.IsNullOrEmpty(n)) continue;

                int layer = LayerMask.NameToLayer(n);
                if (layer < 0)
                {
                    missing += (missing.Length > 0 ? "|" : "") + n;
                    continue;
                }
                mask |= 1 << layer;
                resolved += (resolved.Length > 0 ? "|" : "") + n;
            }
            return mask;
        }

        // ── Small helpers ───────────────────────────────────────────────

        static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            if (t == null || ancestor == null) return false;
            Transform cursor = t;
            while (cursor != null)
            {
                if (cursor == ancestor) return true;
                cursor = cursor.parent;
            }
            return false;
        }

        /// <summary>
        /// The factor that turns the measurement space into metres. The
        /// canonical hierarchy keeps Anchor unscaled, so this is 1 — but a
        /// hand-edited prefab can scale it, and a silently wrong mass is
        /// worse than a slightly defensive multiply.
        /// </summary>
        static float UniformScaleOf(GameObject go)
        {
            if (go == null) return 1f;
            Vector3 s = go.transform.lossyScale;
            float max = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            return max > 1e-6f ? max : 1f;
        }

        static float MinAbsComponent(Vector3 v)
        {
            return Mathf.Min(Mathf.Abs(v.x), Mathf.Min(Mathf.Abs(v.y), Mathf.Abs(v.z)));
        }

        static bool IsUndoable(GameObject go)
        {
            return go != null
                && !EditorUtility.IsPersistent(go)
                && go.scene.IsValid()
                && !EditorSceneManager.IsPreviewSceneObject(go);
        }

        static void RegisterUndo(GameObject go, string label)
        {
            if (IsUndoable(go)) Undo.RegisterFullObjectHierarchyUndo(go, label);
        }

        static void Report(ConversionResult r, DecisionKind kind, string message)
        {
            if (r == null) return;
            switch (kind)
            {
                case DecisionKind.Measured:  r.Measured(message);  break;
                case DecisionKind.Guessed:   r.Guessed(message);   break;
                case DecisionKind.Added:     r.Added(message);     break;
                case DecisionKind.Extracted: r.Extracted(message); break;
                case DecisionKind.Skipped:   r.Skipped(message);   break;
                default:                     r.Failed(message);    break;
            }
        }
    }
}
#endif
