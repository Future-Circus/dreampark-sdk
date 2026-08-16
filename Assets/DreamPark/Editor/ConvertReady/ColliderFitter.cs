// ─────────────────────────────────────────────────────────────────────
//  ColliderFitter.cs — Stage 4 of Convert to DreamPark-Ready
//
//  WHY THIS EXISTS
//
//  An imported model arrives with no collider. Adding one by hand is the step
//  creators skip, and the step they get wrong in two specific ways that both
//  fail silently on device:
//
//   1. A non-convex MeshCollider on anything with a non-kinematic Rigidbody is
//      DISABLED BY UNITY WITHOUT AN ERROR. It reads as "my prop falls through
//      the floor" and points at nothing. So plan.RequiresConvexCollider is a
//      hard constraint here, not a preference — see EnforceConvex below.
//   2. A collider parented to a transform that rotates every frame makes the
//      prop's FLOOR FOOTPRINT rotate every frame. PropTemplate.useColliderBounds
//      defaults true and TryGetColliderFootprint walks
//      GetComponentsInChildren<Collider>() (PropTemplate.cs:351), so GapFiller
//      and FloorCutout both consume that spinning box. A billboarded tree
//      re-cuts its hole in the floor every time the player walks around it.
//      That is why everything this file emits lands on ANCHOR, which by
//      construction never rotates, and never on Motion or Visual.
//
//  Because the collider lives on Anchor but the geometry lives on Visual —
//  which carries the Stage 2 fit scale and the Stage 3a ground-pivot offset —
//  every number here is computed in ANCHOR-LOCAL SPACE, not in the visual's
//  own space and not in world space. Getting that wrong puts the collider in
//  the wrong place for every model whose pivot was not centred, which is most
//  of them, and it is invisible in the inspector because the box still looks
//  plausibly sized.
//
//  The fill ratio that drives the ladder is an exact mesh volume via the
//  divergence theorem over the triangle soup (sum of v0·(v1×v2)/6). Exact for
//  a closed mesh, and good enough for a three-way classification on an open
//  one — we are choosing between four collider shapes, not integrating.
//
//  Primitives are preferred aggressively and the ladder is ordered to do that.
//  On Quest a BoxCollider is close to free and a MeshCollider is not.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class ColliderFitter
    {
        // ── Ladder thresholds ───────────────────────────────────────────
        // Named because the report quotes them and because a creator asking
        // "why did my rock get a capsule" should be able to read the answer.

        /// Rung 1: a solid, chunky shape. π/6 (a sphere) is 0.52, a cylinder is
        /// 0.79, a cube is 1.0 — so 0.6 splits "boxy" from "sculpted".
        public const float BoxFillMin = 0.60f;
        /// ...but not a plank or a blade. A thin axis under a fifth of the
        /// longest means a box swallows a lot of empty space at the ends.
        public const float BoxMinAxisRatio = 0.20f;
        /// ...UNLESS the mesh nearly IS its own bounding box. A door leaf
        /// (2.0 × 0.9 × 0.05), a plank, a book, a shelf, a blade: min-axis
        /// ratio 0.02–0.06, fill 0.95+. The thin-axis guard exists to stop a
        /// box swallowing empty space at the ends, and at this fill there is
        /// no empty space to swallow — so this rung runs FIRST. Without it the
        /// single most common prop silhouette in a park falls all the way
        /// through to a convex MeshCollider on Quest, which is the exact
        /// inverse of "prefer primitives aggressively".
        public const float BoxFillDominant = 0.85f;

        /// Rung 2: cubical bounds. Every axis within 20% of the longest.
        public const float SphereAxisRatio = 0.80f;
        /// A sphere inscribed in its own AABB fills π/6 = 0.5236. The band is
        /// wide enough to absorb a lumpy boulder and narrow enough to reject a
        /// hollow cube (fill → 0) and a solid one (fill → 1).
        public const float SphereFillMin = 0.35f;
        public const float SphereFillMax = 0.75f;

        /// Rung 3: one axis clearly longer than the other two...
        public const float CapsuleDominance = 1.25f;
        /// ...and the cross-section roughly circular, so a capsule's round
        /// sweep is not badly wrong about the corners.
        public const float CapsuleCrossRatio = 0.80f;

        /// Unity's convex hull caps at 255 faces and SILENTLY simplifies past
        /// it. Documented here because the report quotes the number; it is NOT
        /// the warning threshold — see HullWarnSourceTriangles.
        public const int ConvexHullTriangleCap = 255;

        /// The cap is on the HULL's output faces, and we cannot count those
        /// without cooking the hull. All we have is the source triangle count,
        /// which is an upper bound and usually a wildly loose one: a 20k-tri
        /// rock hulls to a few dozen faces and is not simplified at all.
        /// Warning at 255 source triangles therefore fires on essentially every
        /// convex conversion in a batch, and a warning that always fires is one
        /// creators learn to skip — which loses the one case (a real concavity)
        /// it exists for. So the trigger sits where source complexity starts to
        /// actually correlate with hull complexity, and the wording says out
        /// loud that it is a heuristic.
        public const int HullWarnSourceTriangles = 1000;

        /// A convex MeshCollider needs real volume to cook. Below this we drop
        /// to a box rather than hand PhysX a degenerate hull.
        public const int MinConvexTriangles = 4;

        /// A flat model (a quad, a decal, a sheet of foliage) has a zero-depth
        /// bounds axis. A BoxCollider with a zero extent is degenerate, so the
        /// thin axis is inflated to this and the report says it happened.
        public const float MinExtentMeters = 0.01f;

        /// Default depth for the texture track's plane box. Thin enough to read
        /// as a plane; thick enough that a hand or a thrown prop cannot tunnel
        /// through it in one physics step.
        public const float PlaneThicknessMeters = 0.02f;

        /// Mesh colliders that cannot sit directly on Anchor get one child each,
        /// named with this prefix so a re-run can find and replace them.
        public const string CollisionChildPrefix = "Collision";

        // ── Results ─────────────────────────────────────────────────────

        /// <summary>
        /// Everything Stage 4 learned about the geometry, in the space it was
        /// asked to measure in. Stage 6 wants <see cref="meshVolume"/> for the
        /// Rigidbody mass estimate and <see cref="bounds"/> for the EasyBend
        /// detection radius, so this is public rather than an implementation
        /// detail — measuring the triangle soup twice would be wasteful and
        /// would let the two numbers drift.
        /// </summary>
        public struct MeshMeasurement
        {
            public bool ok;
            /// Axis-aligned bounds in the measurement space (anchor-local, when
            /// called from Fit).
            public Bounds bounds;
            /// Absolute mesh volume in cubic metres of the measurement space.
            /// 0 when nothing readable was found.
            public float meshVolume;
            public float boundsVolume;
            /// meshVolume / boundsVolume. Only meaningful when
            /// <see cref="fillRatioKnown"/>.
            public float fillRatio;
            public bool fillRatioKnown;
            public int triangleCount;
            public int meshCount;
            /// Meshes whose vertex data could not be read (Read/Write disabled
            /// and the editor refused). Their bounds still count; their volume
            /// cannot.
            public int unreadableMeshCount;
            /// Renderers that were skipped because they are disabled or sit on
            /// an inactive GameObject — hidden proxy geometry (UCX_*, *_high,
            /// deactivated variants), which DCC pipelines disable rather than
            /// delete. Reported, never silently dropped.
            public int inactiveExcludedCount;
            /// True when the active-only pass found nothing and the measurement
            /// fell back to including inactive renderers.
            public bool includedInactive;
            public bool hasSkinnedMesh;
            /// At least one bounds axis is effectively zero — a flat model.
            public bool flat;
            public string error;
        }

        /// <summary>
        /// What Fit actually did. <see cref="collider"/> is the component on
        /// Anchor, or the first of several when a compound mesh collider was
        /// needed.
        /// </summary>
        public struct FitResult
        {
            public bool ok;
            public ColliderChoice choice;
            public Collider collider;
            public int colliderCount;
            /// The visual's bounds expressed in ANCHOR-LOCAL space. This is the
            /// number the rest of the pipeline should quote, not the visual's
            /// own bounds.
            public Bounds anchorLocalBounds;
            public MeshMeasurement measurement;
            /// True when the convex constraint overrode the ladder's or the
            /// plan's answer.
            public bool convexForced;
            /// True when a hull's SOURCE mesh is complex enough that Unity may
            /// silently simplify it past the 255-face convex cap. A heuristic
            /// on the input, not a measurement of the cooked hull — see
            /// HullWarnSourceTriangles.
            public bool hullOverCap;
            public string error;
        }

        // ── Entry points ────────────────────────────────────────────────

        /// <summary>
        /// Fit a collider to <paramref name="visual"/> and place it on
        /// <paramref name="anchor"/>. Returns the choice that was actually
        /// applied — which is not always the choice in the plan, because the
        /// convex constraint wins. Returns ColliderChoice.None when nothing was
        /// placed; the reason is always appended to <paramref name="r"/>.
        /// </summary>
        public static ColliderChoice Fit(GameObject anchor, GameObject visual,
                                         ConversionPlan plan, ConversionResult r)
        {
            FitResult fit;
            Fit(anchor, visual, plan, r, out fit);
            return fit.choice;
        }

        /// <summary>
        /// As above, but hands back the measurement and the placed collider so
        /// Stage 6 does not have to re-walk the triangle soup for its mass and
        /// detection-radius estimates.
        /// </summary>
        public static bool Fit(GameObject anchor, GameObject visual,
                               ConversionPlan plan, ConversionResult r, out FitResult fit)
        {
            fit = new FitResult();
            fit.choice = ColliderChoice.None;

            if (anchor == null)
            {
                fit.error = "collider: no Anchor object — nothing to place a collider on";
                Report(r, DecisionKind.Failed, fit.error);
                return false;
            }
            if (plan == null)
            {
                fit.error = "collider: no ConversionPlan";
                Report(r, DecisionKind.Failed, fit.error);
                return false;
            }

            RegisterUndo(anchor, "Fit Collider");

            if (visual == null)
            {
                // No geometry and no collider wanted is not an error — it is
                // the audio track. No geometry with a collider wanted is.
                if (plan.collider == ColliderChoice.None)
                {
                    ClearForNone(anchor, r);
                    fit.ok = true;
                    return true;
                }
                fit.error = "collider: no Visual object — nothing to measure";
                Report(r, DecisionKind.Failed, fit.error);
                return false;
            }

            // ── Measure, in ANCHOR-local space. ─────────────────────────
            // Not visual-local and not world: the collider will be evaluated
            // under the anchor's transform, so every extent and centre has to
            // be expressed there, with the visual's localPosition (the Stage 3a
            // ground offset), localRotation (the Stage 3b up-axis fix) and
            // localScale (the Stage 2 fit) already folded in.
            //
            // This runs BEFORE the ColliderChoice.None branch on purpose. The
            // out-param is the pipeline's only measurement of the geometry:
            // Stage 6 takes its Rigidbody mass from fit.measurement.meshVolume
            // and EasyBend's detectionRadius from fit.anchorLocalBounds. Return
            // those default-initialised on a None plan and a Custom
            // None + Bendy prop silently collapses to the 0.1 kg mass floor and
            // the 0.15 m radius floor — a metre-wide trigger around a coin, or
            // its inverse. Walking the triangles is cheap next to the prefab
            // save, and it is what makes the return value trustworthy on EVERY
            // track rather than on most of them.
            List<Sample> samples;
            MeshMeasurement m = Collect(visual, anchor.transform, out samples);
            fit.measurement = m;
            fit.anchorLocalBounds = m.bounds;

            // ColliderChoice.None is a real answer (the audio track uses it), so
            // honour it by CLEARING rather than by doing nothing — otherwise a
            // re-run with the plan changed to None leaves the old collider
            // behind and the footprint keeps coming from it.
            if (plan.collider == ColliderChoice.None)
            {
                ClearForNone(anchor, r);
                fit.ok = true;
                return true;
            }

            ReportExcludedGeometry(m, r);

            if (!m.ok)
            {
                ClearColliders(anchor, null);
                ClearCollisionChildren(anchor);
                Report(r, DecisionKind.Skipped, "collider: none — " + m.error);
                fit.ok = true;   // not a hard failure; a prefab without a collider still ships
                return true;
            }

            if (m.hasSkinnedMesh)
            {
                // A SkinnedMeshRenderer's shape is whatever the animation is
                // doing. We can only fit the bind pose, and the runtime shape
                // will exceed it. Say so rather than letting someone discover it.
                Report(r, DecisionKind.Measured,
                    "collider: fitted to the skinned mesh's BIND POSE — animation can move geometry outside it");
            }
            if (m.unreadableMeshCount > 0)
            {
                // The ladder really does still reach a primitive from bounds
                // alone (rung 3's capsule, and the bounds-only box fallback at
                // rung 4), so this sentence is true as written. It was not
                // before those rungs existed — every unreadable mesh fell
                // straight to a MeshCollider while the report said otherwise.
                Report(r, DecisionKind.Skipped, string.Format(
                    "fill-ratio for {0} mesh(es) — Read/Write is disabled on the model, so volume could not be " +
                    "measured and the collider was classified from bounds alone", m.unreadableMeshCount));
            }

            // ── Choose ─────────────────────────────────────────────────
            string why;
            ColliderChoice choice;
            if (plan.collider == ColliderChoice.Auto) choice = Classify(m, out why);
            else                                      choice = Explicit(plan.collider, m, out why);

            // ── Enforce the convex constraint ──────────────────────────
            // This is the one rule in this file that is not a preference. A
            // non-convex MeshCollider on a non-kinematic Rigidbody is disabled
            // by Unity with no error at all.
            if (choice == ColliderChoice.MeshStatic && plan.RequiresConvexCollider)
            {
                choice = ColliderChoice.ConvexMesh;
                fit.convexForced = true;
                Report(r, DecisionKind.Skipped, string.Format(
                    "non-convex MeshCollider — this prop {0}, and Unity silently DISABLES a non-convex " +
                    "MeshCollider on a moving Rigidbody. Forced convex instead.",
                    plan.behavior == BehaviorPack.Shatter ? "shatters into rigidbodies" : "has a Rigidbody"));
            }

            // A hull needs enough triangles to cook. Below that a box is both
            // correct and cheaper. Only trustworthy when every mesh was
            // readable — an unreadable model reports 0 triangles, and
            // downgrading THAT to a box would be a lie.
            if ((choice == ColliderChoice.ConvexMesh || choice == ColliderChoice.MeshStatic)
                && m.unreadableMeshCount == 0
                && m.triangleCount < MinConvexTriangles)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "MeshCollider — only {0} readable triangle(s); using a BoxCollider instead", m.triangleCount));
                choice = ColliderChoice.Box;
                why = "too few triangles for a mesh collider";
            }

            // ── Apply ──────────────────────────────────────────────────
            switch (choice)
            {
                case ColliderChoice.Box:     fit.collider = ApplyBox(anchor, m, r); fit.colliderCount = 1; break;
                case ColliderChoice.Sphere:  fit.collider = ApplySphere(anchor, m, r); fit.colliderCount = 1; break;
                case ColliderChoice.Capsule: fit.collider = ApplyCapsule(anchor, m, r); fit.colliderCount = 1; break;
                default:
                    bool convex = choice == ColliderChoice.ConvexMesh;
                    fit.collider = ApplyMesh(anchor, samples, convex, r, out fit.colliderCount, out fit.hullOverCap);
                    break;
            }

            if (fit.collider == null)
            {
                fit.error = "collider: could not place a " + Describe(choice);
                Report(r, DecisionKind.Failed, fit.error);
                return false;
            }

            fit.choice = choice;
            fit.ok = true;

            Vector3 s = m.bounds.size;
            Report(r, DecisionKind.Measured, string.Format(
                "collider: {0} on '{1}' — {2:0.###} × {3:0.###} × {4:0.###} m ({5})",
                Describe(choice), anchor.name, s.x, s.y, s.z, why));

            WarnOnNonUniformAnchor(anchor, choice, r);
            return true;
        }

        /// <summary>
        /// Measure a visual's geometry in <paramref name="space"/> (null = the
        /// visual's own local space). Exposed so other stages can reuse the
        /// volume and bounds without re-walking the triangles.
        /// </summary>
        public static MeshMeasurement Measure(GameObject visual, Transform space)
        {
            List<Sample> ignored;
            if (visual == null)
            {
                MeshMeasurement bad = new MeshMeasurement();
                bad.error = "no visual to measure";
                return bad;
            }
            return Collect(visual, space != null ? space : visual.transform, out ignored);
        }

        /// <summary>
        /// Half the larger horizontal extent, i.e. the radius of the circle
        /// that covers the prop's footprint. Stage 6 uses this to scale
        /// EasyBend's detectionRadius to the object instead of leaving the
        /// 0.75 m default around a 5 cm coin.
        /// </summary>
        public static float HorizontalRadius(Bounds b)
        {
            return 0.5f * Mathf.Max(Mathf.Abs(b.size.x), Mathf.Abs(b.size.z));
        }

        // ── Swept-volume helpers for the texture track ──────────────────

        /// <summary>
        /// Box size for a plane of <paramref name="widthMeters"/> ×
        /// <paramref name="heightMeters"/>, sized to the volume the plane
        /// SWEEPS rather than to the plane itself.
        ///
        /// A billboard's collider must cover every orientation the plane can
        /// take, because the collider on Anchor does not rotate with it — that
        /// is the whole point of the Anchor node. Size it to the plane and a
        /// yaw-billboarded tree pokes out of its own collider at 90°, and its
        /// GapFiller footprint is wrong for every angle but one.
        /// </summary>
        public static Vector3 BoxSizeForPlane(float widthMeters, float heightMeters, BillboardMode mode)
        {
            return BoxSizeForPlane(widthMeters, heightMeters, mode, PlaneThicknessMeters);
        }

        /// <summary>As above, with an explicit depth for the non-billboard case.</summary>
        public static Vector3 BoxSizeForPlane(float widthMeters, float heightMeters,
                                              BillboardMode mode, float thicknessMeters)
        {
            float w = Mathf.Max(MinExtentMeters, Mathf.Abs(widthMeters));
            float h = Mathf.Max(MinExtentMeters, Mathf.Abs(heightMeters));
            float t = Mathf.Max(MinExtentMeters, Mathf.Abs(thicknessMeters));

            switch (mode)
            {
                // Yaw only: the plane sweeps a cylinder of diameter W about Y.
                // Height is untouched because the plane never tips.
                case BillboardMode.YAxis:
                    return new Vector3(w, h, w);

                // Full facing: the plane sweeps a sphere on its own diagonal.
                case BillboardMode.Full:
                    float d = Mathf.Sqrt(w * w + h * h);
                    return new Vector3(d, d, d);

                // Nothing rotates, so the box is just the plane plus enough
                // depth to be a real solid.
                default:
                    return new Vector3(w, h, t);
            }
        }

        // ── Classification ──────────────────────────────────────────────

        static ColliderChoice Classify(MeshMeasurement m, out string why)
        {
            Vector3 size = m.bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            float minAxis = Mathf.Min(size.x, Mathf.Min(size.y, size.z));

            // A flat model has no volume to reason about and no third dimension
            // to fit a sphere or capsule to. Box, always.
            if (m.flat)
            {
                why = "flat — a box is the only shape that fits";
                return ColliderChoice.Box;
            }

            float minRatio = maxAxis > 0f ? minAxis / maxAxis : 0f;

            // Rung 0 — the mesh nearly IS its own AABB, whatever its aspect
            // ratio. This has to come before the thin-axis test or a door leaf,
            // a plank, a book, a shelf and a sword blade all fall past every
            // primitive rung and land on a convex MeshCollider: minRatio for a
            // 5 cm door is ~0.025, and m.flat does not catch it either (the
            // flat threshold is a millimetre, fifty times thinner).
            if (m.fillRatioKnown && m.fillRatio > BoxFillDominant)
            {
                why = string.Format("fill {0:0.00} — the mesh nearly fills its own bounds", m.fillRatio);
                return ColliderChoice.Box;
            }

            // Rung 1 — chunky and not thin anywhere.
            if (m.fillRatioKnown && m.fillRatio > BoxFillMin && minRatio >= BoxMinAxisRatio)
            {
                why = string.Format("fill {0:0.00}, no thin axis", m.fillRatio);
                return ColliderChoice.Box;
            }

            // Rung 2 — cubical bounds with about a sphere's worth of volume in
            // them. Both tests matter: cubical bounds alone also describe a
            // hollow crate.
            if (minRatio >= SphereAxisRatio && m.fillRatioKnown
                && m.fillRatio >= SphereFillMin && m.fillRatio <= SphereFillMax)
            {
                why = string.Format("fill {0:0.00}, axes within {1:0}%",
                    m.fillRatio, (1f - SphereAxisRatio) * 100f);
                return ColliderChoice.Sphere;
            }

            // Rung 3 — one dominant axis with a roughly circular cross-section.
            int dominant = DominantAxis(size);
            float a, b;
            OtherTwo(size, dominant, out a, out b);
            float longest = Component(size, dominant);
            float crossMax = Mathf.Max(a, b);
            float crossMin = Mathf.Min(a, b);
            bool circular = crossMax > 0f && (crossMin / crossMax) >= CapsuleCrossRatio;

            if (circular && crossMax > 0f && longest >= crossMax * CapsuleDominance)
            {
                why = m.fillRatioKnown
                    ? string.Format("fill {0:0.00}, one dominant axis ({1}), circular cross-section",
                        m.fillRatio, AxisName(dominant))
                    : string.Format("one dominant axis ({0}), circular cross-section", AxisName(dominant));
                return ColliderChoice.Capsule;
            }

            // Rung 4 — bounds-only fallback. One mesh with Read/Write disabled
            // zeroes fillRatioKnown for the WHOLE model, and rungs 0/1/2 are
            // all gated on it, so without this a chunky crate with one
            // unreadable sub-mesh gets a convex MeshCollider on Quest while the
            // report claims it was "classified from bounds alone". It sits
            // below the capsule rung deliberately: with no volume to go on, an
            // elongated circular-section shape is better served by a capsule
            // than by a box, and the box is what is left over.
            if (!m.fillRatioKnown && minRatio >= BoxMinAxisRatio)
            {
                why = string.Format("no thin axis (min/max {0:0.00}); volume unknown — Read/Write is disabled, " +
                                    "so a box is the safe fit", minRatio);
                return ColliderChoice.Box;
            }

            // Rungs 5 and 6 — the caller applies the convex constraint.
            why = m.fillRatioKnown
                ? string.Format("fill {0:0.00}, {1} tri — no primitive fits", m.fillRatio, m.triangleCount)
                : string.Format("{0} tri — no primitive fits", m.triangleCount);
            return ColliderChoice.MeshStatic;
        }

        static ColliderChoice Explicit(ColliderChoice requested, MeshMeasurement m, out string why)
        {
            why = m.fillRatioKnown
                ? string.Format("chosen in the plan; fill {0:0.00}", m.fillRatio)
                : "chosen in the plan";
            return requested;
        }

        // ── Application ─────────────────────────────────────────────────

        static BoxCollider ApplyBox(GameObject anchor, MeshMeasurement m, ConversionResult r)
        {
            ClearColliders(anchor, typeof(BoxCollider));
            ClearCollisionChildren(anchor);

            BoxCollider box = Componentizer.DoComponent<BoxCollider>(anchor, true);
            if (box == null) return null;

            Vector3 size = m.bounds.size;
            Vector3 fitted = new Vector3(
                Mathf.Max(MinExtentMeters, size.x),
                Mathf.Max(MinExtentMeters, size.y),
                Mathf.Max(MinExtentMeters, size.z));

            // Component-wise, NOT `fitted != size`: Vector3's == is approximate
            // (equal when the difference's sqrMagnitude < 1e-5, a tolerance of
            // ~3 mm), so a thin axis between ~0.007 m and 0.01 m would be
            // thickened and then compare equal — the report would silently omit
            // a mutation it had just performed, which is the one thing the
            // report format exists to prevent.
            if (fitted.x != size.x || fitted.y != size.y || fitted.z != size.z)
            {
                Report(r, DecisionKind.Measured, string.Format(
                    "collider: '{0}' is flat ({1:0.####} × {2:0.####} × {3:0.####} m) — the thin axis was " +
                    "thickened to {4:0.###} m so the box is not degenerate",
                    anchor.name, size.x, size.y, size.z, MinExtentMeters));
            }

            box.size = fitted;
            // The centre is the whole reason this is computed in anchor space.
            // A model whose pivot sits at its feet, or at the DCC scene origin,
            // has bounds.center far from zero; dropping it puts the box
            // somewhere the mesh is not.
            box.center = m.bounds.center;
            return box;
        }

        static SphereCollider ApplySphere(GameObject anchor, MeshMeasurement m, ConversionResult r)
        {
            ClearColliders(anchor, typeof(SphereCollider));
            ClearCollisionChildren(anchor);

            SphereCollider sphere = Componentizer.DoComponent<SphereCollider>(anchor, true);
            if (sphere == null) return null;

            // Half the LONGEST axis, not half the diagonal: this rung only fires
            // when the three axes are within 20% of each other, so the longest
            // axis is the mesh's own diameter. Half the diagonal would inflate a
            // ball's collider by ~70%.
            Vector3 size = m.bounds.size;
            sphere.radius = Mathf.Max(MinExtentMeters,
                0.5f * Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            sphere.center = m.bounds.center;
            return sphere;
        }

        static CapsuleCollider ApplyCapsule(GameObject anchor, MeshMeasurement m, ConversionResult r)
        {
            ClearColliders(anchor, typeof(CapsuleCollider));
            ClearCollisionChildren(anchor);

            CapsuleCollider capsule = Componentizer.DoComponent<CapsuleCollider>(anchor, true);
            if (capsule == null) return null;

            Vector3 size = m.bounds.size;
            int dominant = DominantAxis(size);
            float a, b;
            OtherTwo(size, dominant, out a, out b);

            // CapsuleCollider.direction is 0/1/2 for X/Y/Z, and `height` is the
            // TOTAL height including both hemispherical caps — Unity clamps it
            // up to 2*radius internally, so a near-spherical case degrades to a
            // sphere rather than inverting.
            capsule.direction = dominant;
            capsule.radius = Mathf.Max(MinExtentMeters, 0.5f * Mathf.Max(a, b));
            capsule.height = Mathf.Max(capsule.radius * 2f, Component(size, dominant));
            capsule.center = m.bounds.center;
            return capsule;
        }

        /// <summary>
        /// MeshColliders are the awkward case: unlike the primitives they carry
        /// no centre or size, so their geometry has to already sit correctly
        /// relative to the transform they are on. The Visual carries the fit
        /// scale and the ground offset, so its meshes do NOT sit correctly
        /// relative to Anchor.
        ///
        /// Rather than baking a transformed copy of the mesh — which would mean
        /// a generated sub-asset per prop, per re-run — each mesh gets a
        /// "Collision" child of Anchor whose world pose is copied from the
        /// source renderer. The child is a child of Anchor, so
        /// GetComponentsInChildren&lt;Collider&gt;() still finds it for the
        /// footprint, and nothing ever writes its rotation, so the footprint
        /// stays stable. The single-mesh, already-aligned case skips the child
        /// and sits straight on Anchor.
        /// </summary>
        static Collider ApplyMesh(GameObject anchor, List<Sample> samples, bool convex,
                                  ConversionResult r, out int count, out bool overCap)
        {
            count = 0;
            overCap = false;

            ClearColliders(anchor, null);
            ClearCollisionChildren(anchor);

            List<Sample> usable = new List<Sample>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].mesh != null) usable.Add(samples[i]);
            }
            if (usable.Count == 0) return null;

            Collider first = null;

            // Aligned single mesh → straight onto Anchor, no extra transform.
            if (usable.Count == 1 && IsAligned(anchor.transform, usable[0].space))
            {
                MeshCollider mc = Componentizer.DoComponent<MeshCollider>(anchor, true);
                if (mc == null) return null;
                mc.convex = convex;
                mc.sharedMesh = usable[0].mesh;
                first = mc;
                count = 1;
                WarnHullCap(usable[0], convex, r, ref overCap);
                return first;
            }

            for (int i = 0; i < usable.Count; i++)
            {
                Sample s = usable[i];
                string childName = usable.Count == 1
                    ? CollisionChildPrefix
                    : string.Format("{0}_{1:00}", CollisionChildPrefix, i);

                GameObject go = new GameObject(childName);
                go.layer = anchor.layer;

                // Copy the source renderer's world pose without touching a
                // matrix by hand: park the node under the source (identity
                // local TRS == the source's pose exactly), then reparent to
                // Anchor keeping the world pose.
                go.transform.SetParent(s.space, false);
                go.transform.SetParent(anchor.transform, true);

                // Registered AFTER parenting, and gated on the ANCHOR rather
                // than on the new object: `new GameObject` lands in the active
                // scene, so testing the fresh object would register an undo for
                // something that is about to move into a prefab preview scene.
                RegisterCreated(go, anchor, "Fit Collider");

                MeshCollider mc = Componentizer.DoComponent<MeshCollider>(go, true);
                if (mc == null) continue;
                mc.convex = convex;
                mc.sharedMesh = s.mesh;

                if (first == null) first = mc;
                count++;
                WarnHullCap(s, convex, r, ref overCap);
            }

            // Reaching the loop at all means children were used — the aligned
            // single-mesh case returned above.
            if (count > 0)
            {
                Report(r, DecisionKind.Added, string.Format(
                    "{0} '{1}' child object(s) under '{2}' — a MeshCollider carries no centre or size, so each " +
                    "source mesh needs a node reproducing its pose relative to the Anchor. They never rotate, " +
                    "so the PropTemplate footprint stays stable.",
                    count, CollisionChildPrefix, anchor.name));
            }

            return first;
        }

        static void WarnHullCap(Sample s, bool convex, ConversionResult r, ref bool overCap)
        {
            // Threshold on SOURCE triangles, not on the cap. The cap is on the
            // cooked hull's faces and a hull has far fewer faces than its input
            // — comparing the input count against 255 fires on every real model
            // in a batch and trains creators to ignore the line.
            if (!convex || s.triangles <= HullWarnSourceTriangles) return;

            overCap = true;
            // Emit the hull anyway — a simplified hull still collides, and no
            // collider at all is worse. The wording is deliberately conditional
            // ("if... may"): we have not cooked the hull, so we do not know
            // that it was simplified, only that it is complex enough to be
            // worth a look. Overclaiming here is how a MEASURED-vs-GUESSED
            // report stops being believed.
            Report(r, DecisionKind.Skipped, string.Format(
                "exact convex hull for '{0}' — {1:n0} source triangles. If this shape's hull exceeds Unity's " +
                "{2}-face convex cap, PhysX SIMPLIFIES it silently. Fine for a rock; visibly wrong for anything " +
                "with a concavity that matters (a bowl, a ring, a doorway) — check those by hand.",
                s.name, s.triangles, ConvexHullTriangleCap));
        }

        // ── Measurement ─────────────────────────────────────────────────

        struct Sample
        {
            public string name;
            public Transform space;   // the transform the mesh's vertices are authored under
            public Mesh mesh;
            public int triangles;
            public bool readable;
            public bool skinned;
        }

        /// <summary>
        /// Measure the visual's ACTIVE geometry, falling back to including the
        /// inactive geometry only if there was no active geometry at all.
        ///
        /// The distinction is not pedantry. FBX exports routinely ship hidden
        /// proxy meshes — UCX_*, *_collision, *_high, deactivated variants —
        /// and the DCC-side convention is to disable them rather than delete
        /// them. Counting those inflates both the AABB and the integrated
        /// volume, and it puts this stage out of step with the two that ran
        /// immediately before it: ScaleInference and OrientationFitter both
        /// size and ground the model from a single Renderer. Fit the collider
        /// to geometry that was never scaled and never grounded and the box
        /// visibly overhangs the model.
        ///
        /// The fallback exists because a fully-inactive Visual subtree is a
        /// legitimate input (an authoring rig, a disabled prefab variant), and
        /// "no collider at all" is a worse answer than "a collider fitted to
        /// hidden geometry, and the report says so".
        /// </summary>
        static MeshMeasurement Collect(GameObject visual, Transform frame, out List<Sample> samples)
        {
            MeshMeasurement active = Gather(visual, frame, false, out samples);
            if (active.ok || active.inactiveExcludedCount == 0) return active;

            List<Sample> withInactive;
            MeshMeasurement all = Gather(visual, frame, true, out withInactive);
            if (!all.ok) return active;   // the active pass's error is the more useful one

            all.includedInactive = true;
            samples = withInactive;
            return all;
        }

        static MeshMeasurement Gather(GameObject visual, Transform frame, bool includeInactive,
                                      out List<Sample> samples)
        {
            samples = new List<Sample>();
            MeshMeasurement m = new MeshMeasurement();

            if (visual == null)
            {
                m.error = "no Visual object";
                return m;
            }
            if (frame == null) frame = visual.transform;

            HashSet<Renderer> lodExcluded = CollectLodExcluded(visual);

            bool any = false;
            Bounds acc = new Bounds();
            double volume = 0.0;

            // MeshFilters, not Renderers: a ParticleSystemRenderer or a
            // TrailRenderer has no geometry to fit and would contribute a
            // meaningless bounds.
            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;

                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;                       // invisible geometry
                if (lodExcluded.Contains(mr)) continue;         // LOD1+ would double-count
                if (!includeInactive && !Renders(mr)) { m.inactiveExcludedCount++; continue; }

                Matrix4x4 toFrame = frame.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                Sample s = ReadSample(mf.name, mf.transform, mf.sharedMesh, false);

                Vector3[] verts;
                int[] tris;
                if (TryReadMesh(mf.sharedMesh, out verts, out tris))
                {
                    s.readable = true;
                    s.triangles = tris.Length / 3;
                    double meshVolume;
                    Accumulate(verts, tris, toFrame, ref acc, ref any, out meshVolume);
                    // Magnitude per mesh, never signed into a shared total —
                    // see Accumulate.
                    volume += System.Math.Abs(meshVolume);
                }
                else
                {
                    // Cannot integrate the volume, but the serialized bounds are
                    // still trustworthy — so the collider still gets fitted, it
                    // just gets classified without a fill ratio.
                    m.unreadableMeshCount++;
                    EncapsulateBounds(mf.sharedMesh.bounds, toFrame, ref acc, ref any);
                }

                samples.Add(s);
                m.meshCount++;
            }

            SkinnedMeshRenderer[] skins = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                SkinnedMeshRenderer smr = skins[i];
                if (smr == null || smr.sharedMesh == null) continue;
                if (lodExcluded.Contains(smr)) continue;
                if (!includeInactive && !Renders(smr)) { m.inactiveExcludedCount++; continue; }

                m.hasSkinnedMesh = true;

                // TWO different spaces here, deliberately, because a skinned
                // renderer's bounds and its vertices do not live in the same one.
                //
                // localBounds is expressed relative to ROOTBONE, not to the
                // renderer's own transform — that is precisely why dragging the
                // root bone moves a skinned renderer's world bounds. On any
                // ordinary FBX character the SkinnedMeshRenderer sits at the
                // model root while rootBone is "Hips"/"Armature", several levels
                // down and translated up, so pushing localBounds through the
                // renderer's matrix offsets the whole anchor-local AABB by the
                // root bone's pose and the BoxCollider lands where the mesh is
                // not — the exact failure this file's header exists to prevent.
                Transform boundsSpace = smr.rootBone != null ? smr.rootBone : smr.transform;
                Matrix4x4 boundsToFrame = frame.worldToLocalMatrix * boundsSpace.localToWorldMatrix;
                EncapsulateBounds(smr.localBounds, boundsToFrame, ref acc, ref any);

                // The vertices, by contrast, ARE authored under the renderer's
                // transform: a bindpose is bones[i].worldToLocalMatrix *
                // smr.transform.localToWorldMatrix, so bind-pose geometry is in
                // smr.transform's space by construction. That makes this the
                // right matrix both for the volume integral and for the pose
                // ApplyMesh copies onto a "Collision" child.
                Matrix4x4 vertsToFrame = frame.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                Sample s = ReadSample(smr.name, smr.transform, smr.sharedMesh, true);

                Vector3[] verts;
                int[] tris;
                if (TryReadMesh(smr.sharedMesh, out verts, out tris))
                {
                    s.readable = true;
                    s.triangles = tris.Length / 3;
                    // Bounds are already in from localBounds above; this call's
                    // job is the volume only, so its bounds output is discarded.
                    Bounds ignored = new Bounds();
                    bool ignoredAny = false;
                    double meshVolume;
                    Accumulate(verts, tris, vertsToFrame, ref ignored, ref ignoredAny, out meshVolume);
                    volume += System.Math.Abs(meshVolume);
                }
                else
                {
                    m.unreadableMeshCount++;
                }

                samples.Add(s);
                m.meshCount++;
            }

            if (!any)
            {
                if (m.meshCount > 0)
                    m.error = string.Format("'{0}' has {1} mesh(es) but no measurable geometry", visual.name, m.meshCount);
                else if (m.inactiveExcludedCount > 0)
                    m.error = string.Format("no ACTIVE mesh under '{0}' — {1} renderer(s) are disabled or on an inactive object",
                        visual.name, m.inactiveExcludedCount);
                else
                    m.error = string.Format("no mesh found under '{0}'", visual.name);
                return m;
            }

            for (int i = 0; i < samples.Count; i++) m.triangleCount += samples[i].triangles;

            m.bounds = acc;
            m.meshVolume = (float)volume;

            Vector3 size = acc.size;
            m.flat = size.x <= MinExtentMeters * 0.1f
                  || size.y <= MinExtentMeters * 0.1f
                  || size.z <= MinExtentMeters * 0.1f;

            m.boundsVolume = size.x * size.y * size.z;
            // One unreadable mesh is enough to make the fill ratio wrong in the
            // direction that matters: its volume is missing but its bounds are
            // not, so the ratio reads low and the ladder drops to a mesh
            // collider on something that wanted a box.
            if (!m.flat && m.boundsVolume > 1e-9f && m.unreadableMeshCount == 0)
            {
                // Clamped: an open mesh can integrate to more than its own
                // bounding box, and a fill ratio above 1 would read as a bug in
                // the report rather than as an open mesh.
                m.fillRatio = Mathf.Clamp(m.meshVolume / m.boundsVolume, 0f, 1f);
                m.fillRatioKnown = true;
            }

            m.ok = true;
            return m;
        }

        static Sample ReadSample(string name, Transform space, Mesh mesh, bool skinned)
        {
            Sample s = new Sample();
            s.name = name;
            s.space = space;
            s.mesh = mesh;
            s.skinned = skinned;
            return s;
        }

        /// <summary>
        /// Divergence theorem over the triangle soup: V = Σ v0·(v1×v2) / 6.
        /// Exact for a closed mesh; for an open one it is whatever the implied
        /// closure is, which is fine for a three-way classification.
        /// Accumulated in double because a 100k-triangle mesh sums a lot of
        /// small signed terms that mostly cancel, and float loses them.
        ///
        /// The result is returned PER MESH and signed, and the caller takes its
        /// magnitude before adding it to the model's total. Summing raw signed
        /// volumes across meshes is a real bug, not a theoretical one: the
        /// integral's sign is the winding, so an inward-wound shell (an
        /// interior/room piece, a lantern's glass, anything imported through a
        /// negative-scale FBX node — a toFrame with negative determinant flips
        /// the whole mesh) SUBTRACTS from its neighbours. Two comparable meshes
        /// of opposite winding integrate to ~0, the fill ratio collapses, and a
        /// solid chunky prop is classified as a MeshCollider.
        /// </summary>
        static void Accumulate(Vector3[] verts, int[] tris, Matrix4x4 toFrame,
                               ref Bounds acc, ref bool any, out double volume)
        {
            volume = 0.0;
            Vector3[] world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = toFrame.MultiplyPoint3x4(verts[i]);
                world[i] = p;
                if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                else acc.Encapsulate(p);
            }

            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = world[tris[i]];
                Vector3 b = world[tris[i + 1]];
                Vector3 c = world[tris[i + 2]];
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
        }

        static void EncapsulateBounds(Bounds local, Matrix4x4 toFrame, ref Bounds acc, ref bool any)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 p = toFrame.MultiplyPoint3x4(
                            new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));
                        if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                        else acc.Encapsulate(p);
                    }
        }

        /// <summary>
        /// Will this renderer actually put pixels on screen? A disabled
        /// component or an inactive GameObject is how hidden proxy geometry
        /// ships, and it must not contribute bounds or volume.
        /// </summary>
        static bool Renders(Renderer r)
        {
            return r != null && r.enabled && r.gameObject.activeInHierarchy;
        }

        /// <summary>
        /// Model meshes ship with Read/Write disabled by default. Depending on
        /// the Unity version that either throws or logs an error and hands back
        /// an empty array, so both are treated as "unreadable" — and neither is
        /// allowed to escape this module.
        /// </summary>
        static bool TryReadMesh(Mesh mesh, out Vector3[] verts, out int[] tris)
        {
            verts = null;
            tris = null;
            if (mesh == null) return false;

            try
            {
                verts = mesh.vertices;
                tris = mesh.triangles;
            }
            catch (System.Exception)
            {
                verts = null;
                tris = null;
                return false;
            }

            return verts != null && verts.Length > 0 && tris != null && tris.Length >= 3;
        }

        /// <summary>
        /// Renderers that belong only to LOD1 and below. Counting them would
        /// double or triple both the triangle total and the integrated volume
        /// of any model exported with LODs, which quietly pushes the fill ratio
        /// past the box threshold on things that are not boxes.
        /// </summary>
        static HashSet<Renderer> CollectLodExcluded(GameObject visual)
        {
            HashSet<Renderer> excluded = new HashSet<Renderer>();
            LODGroup[] groups = visual.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length == 0) return excluded;

            for (int g = 0; g < groups.Length; g++)
            {
                LOD[] lods = groups[g].GetLODs();
                for (int l = 1; l < lods.Length; l++)
                {
                    Renderer[] rends = lods[l].renderers;
                    if (rends == null) continue;
                    for (int i = 0; i < rends.Length; i++)
                        if (rends[i] != null) excluded.Add(rends[i]);
                }
            }

            // A renderer shared between LOD0 and a lower level must survive.
            for (int g = 0; g < groups.Length; g++)
            {
                LOD[] lods = groups[g].GetLODs();
                if (lods.Length == 0) continue;
                Renderer[] rends = lods[0].renderers;
                if (rends == null) continue;
                for (int i = 0; i < rends.Length; i++)
                    if (rends[i] != null) excluded.Remove(rends[i]);
            }

            return excluded;
        }

        // ── Housekeeping ────────────────────────────────────────────────

        /// <summary>
        /// Remove every collider on the anchor except one of
        /// <paramref name="keep"/>, which Componentizer.DoComponent then reuses.
        /// A plain DoComponent&lt;T&gt;(false) per type only removes ONE
        /// component each, so a hierarchy that picked up duplicates would keep
        /// them forever across re-runs.
        /// </summary>
        static int ClearColliders(GameObject anchor, System.Type keep)
        {
            Collider[] existing = anchor.GetComponents<Collider>();
            int removed = 0;
            bool kept = false;

            for (int i = 0; i < existing.Length; i++)
            {
                Collider c = existing[i];
                if (c == null) continue;
                if (keep != null && !kept && c.GetType() == keep) { kept = true; continue; }
                Componentizer.DoDestroy(c);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Clear every collider this module could have placed, for the
        /// ColliderChoice.None case and for the "nothing measurable" case. Both
        /// have to CLEAR rather than do nothing, or a re-run with the plan
        /// changed leaves the old collider behind and PropTemplate keeps taking
        /// the footprint from it.
        /// </summary>
        static void ClearForNone(GameObject anchor, ConversionResult r)
        {
            int removed = ClearColliders(anchor, null);
            removed += ClearCollisionChildren(anchor);
            Report(r, DecisionKind.Skipped, removed > 0
                ? string.Format("collider: none by request — removed {0} existing collider(s) from '{1}'", removed, anchor.name)
                : "collider: none by request");
        }

        /// <summary>
        /// Geometry that was measured but should not have been, or was excluded
        /// and might be missed. Both are reported by count so the number in the
        /// report can be reconciled against what is in the model.
        /// </summary>
        static void ReportExcludedGeometry(MeshMeasurement m, ConversionResult r)
        {
            if (m.includedInactive)
            {
                Report(r, DecisionKind.Skipped,
                    "active-only measurement — every renderer under the Visual is disabled or on an inactive " +
                    "object, so the collider was fitted to HIDDEN geometry rather than to nothing. If that " +
                    "geometry is a collision proxy or an unused variant, delete it and re-run.");
            }
            else if (m.inactiveExcludedCount > 0)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "{0} disabled/inactive renderer(s) — hidden proxy meshes (UCX_*, *_high, unused variants) " +
                    "would inflate both the bounds and the volume, and Stage 2 and 3 did not size or ground the " +
                    "model from them either", m.inactiveExcludedCount));
            }
        }

        /// <summary>
        /// True for a node this module generated: "Collision", or
        /// "Collision_00" and up. Exposed because PropPrefabEmitter has to be
        /// able to recognise them — when a re-run inserts the Motion node under
        /// Anchor it reparents Anchor's children, and sweeping the collider
        /// nodes onto the transform EasyBend/Billboard writes every frame is
        /// hazard 1 and hazard 2 at once.
        ///
        /// The name test is EXACT, not a prefix test. "CollisionProxy",
        /// "Collision_Low" and "CollisionHull" are all standard hand-authored
        /// names, and a hand-authored proxy is a bare MeshCollider with no
        /// children and no Renderer — indistinguishable from ours on every
        /// guard except the name. The creator who built one by hand is exactly
        /// the person re-running the converter to tidy up.
        /// </summary>
        public static bool IsGeneratedCollisionChild(Transform child)
        {
            if (child == null) return false;
            if (!HasGeneratedCollisionName(child.name)) return false;
            if (child.childCount > 0) return false;
            if (child.GetComponent<Renderer>() != null) return false;
            return child.GetComponent<MeshCollider>() != null;
        }

        static bool HasGeneratedCollisionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == CollisionChildPrefix) return true;

            // "Collision_00" … "Collision_99", and "_100" up if a model ever
            // has that many sub-meshes: prefix, underscore, digits only.
            int start = CollisionChildPrefix.Length + 1;
            if (name.Length <= start) return false;
            if (!name.StartsWith(CollisionChildPrefix + "_", System.StringComparison.Ordinal)) return false;
            for (int i = start; i < name.Length; i++)
                if (!char.IsDigit(name[i])) return false;
            return true;
        }

        static int ClearCollisionChildren(GameObject anchor)
        {
            Transform t = anchor.transform;
            int removed = 0;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                Transform child = t.GetChild(i);
                if (!IsGeneratedCollisionChild(child)) continue;
                Componentizer.DoDestroy(child.gameObject);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// A SphereCollider or CapsuleCollider under a non-uniformly scaled
        /// transform uses the LARGEST lossy-scale axis for its radius, so it
        /// stops matching the mesh. The canonical hierarchy keeps Anchor at
        /// identity so this cannot happen — but a hand-edited hierarchy or a
        /// shared-prop base can break that, and the failure is silent.
        /// </summary>
        static void WarnOnNonUniformAnchor(GameObject anchor, ColliderChoice choice, ConversionResult r)
        {
            if (choice != ColliderChoice.Sphere && choice != ColliderChoice.Capsule) return;

            Vector3 ls = anchor.transform.lossyScale;
            float max = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            float min = Mathf.Min(Mathf.Abs(ls.x), Mathf.Min(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            if (max <= 0f || (min / max) >= 0.999f) return;

            Report(r, DecisionKind.Skipped, string.Format(
                "exact {0} sizing — '{1}' has non-uniform scale ({2}); Unity sizes round colliders from the " +
                "largest axis, so the collider will not match the mesh. Reset the Anchor to (1,1,1).",
                Describe(choice), anchor.name, ls));
        }

        // Prefab-contents objects live in a preview scene where Undo is a no-op
        // and registering is at best wasted work. Real scene objects need it so
        // the whole conversion collapses into one Cmd-Z.
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

        static void RegisterCreated(GameObject created, GameObject context, string label)
        {
            if (created != null && IsUndoable(context)) Undo.RegisterCreatedObjectUndo(created, label);
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

        // ── Small maths ─────────────────────────────────────────────────

        static int DominantAxis(Vector3 size)
        {
            if (size.x >= size.y && size.x >= size.z) return 0;
            if (size.y >= size.z) return 1;
            return 2;
        }

        static float Component(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        static void OtherTwo(Vector3 v, int axis, out float a, out float b)
        {
            if (axis == 0)      { a = v.y; b = v.z; }
            else if (axis == 1) { a = v.x; b = v.z; }
            else                { a = v.x; b = v.y; }
        }

        static string AxisName(int axis)
        {
            return axis == 0 ? "X" : (axis == 1 ? "Y" : "Z");
        }

        /// <summary>
        /// True when <paramref name="child"/> sits at the same pose as
        /// <paramref name="parent"/>, so a mesh authored under the child can be
        /// used verbatim by a collider on the parent.
        /// </summary>
        static bool IsAligned(Transform parent, Transform child)
        {
            if (parent == null || child == null) return false;
            if (parent == child) return true;

            Matrix4x4 rel = parent.worldToLocalMatrix * child.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
            {
                float expected = (i % 5 == 0) ? 1f : 0f;   // identity on the diagonal
                if (Mathf.Abs(rel[i] - expected) > 1e-4f) return false;
            }
            return true;
        }

        static string Describe(ColliderChoice choice)
        {
            switch (choice)
            {
                case ColliderChoice.Box:        return "BoxCollider";
                case ColliderChoice.Sphere:     return "SphereCollider";
                case ColliderChoice.Capsule:    return "CapsuleCollider";
                case ColliderChoice.ConvexMesh: return "convex MeshCollider";
                case ColliderChoice.MeshStatic: return "non-convex MeshCollider";
                case ColliderChoice.None:       return "no collider";
                default:                        return "auto collider";
            }
        }
    }
}
#endif
