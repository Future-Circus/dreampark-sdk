// ─────────────────────────────────────────────────────────────────────
//  JiggleRigBuilder.cs — the skinned branch of Stage 6 (Bendy)
//
//  EasyBend rotates ONE transform. On a SkinnedMeshRenderer that swings the
//  whole character rigidly about its root and looks broken — so a Bendy
//  preset applied to a rigged model builds a jiggle ragdoll along the bone
//  chain instead.
//
//  This is a PORT of CrashCourse's InflatableRigBuilder
//  (Assets/Content/CrashCourse/Editor/InflatableRigBaker.cs), which is
//  itself the verbatim generation logic from the retired
//  InteractiveInflatableGenerator component. Only three things changed:
//  the namespace, the settings type (BendSettings off the ConversionPlan
//  instead of InflatableRigSettings), and the entry point signature.
//
//  EVERY NUMBER BELOW WAS TUNED AGAINST THREE SHIPPING PREFABS — E_Arch,
//  E_FlipGate and E_Pylon — and cannot be re-derived from first principles.
//  In particular:
//
//    • rb.mass = bones.Length - i. Heavy at the anchor, light at the tip.
//      That gradient is the single line separating a jiggle from a seizure,
//      and nobody would guess it.
//    • colliderSpacing is allowed to be NEGATIVE (E_FlipGate ships -0.08),
//      which deliberately overlaps neighbouring colliders so a panel has no
//      gaps. It is not a bug and must never be clamped to zero.
//    • spring 5,000,000 reads as rigid and 3,000 is what makes E_FlipGate
//      floppy. Those two values are the whole preset vocabulary; see
//      BendSettings.SpringStiff / SpringFloppy.
//    • bones ending in "_end" are excluded: Blender exports a zero-length
//      terminator per chain and colliding it only costs contacts.
//
//  ─────────────────────────────────────────────────────────────────────
//  THIS FILE REINTRODUCES HAZARD 2, AND IT CANNOT FIX IT ALONE
//
//  The canonical hierarchy parks colliders on Anchor so that PropTemplate's
//  floor footprint cannot rotate. A jiggle rig puts a BoxCollider on every
//  rigged bone, and the bones are under Visual — inside the prop root, which
//  is where PropTemplate.TryGetColliderFootprint looks
//  (GetComponentsInChildren<Collider>(true), PropTemplate.cs:354, skipping
//  only DISABLED colliders — not triggers, and it has never heard of a rig).
//  So the bones are the footprint, and physics moves them every frame:
//  GapFiller (GapFiller.cs:929) and FloorCutout then consume a footprint that
//  jiggles with the prop.
//
//  Nothing in this file can prevent that — the rig IS bone colliders. What it
//  can do is say so and hand back the stable answer: RigResult carries
//  footprintNeedsManualOverride and the bind-pose suggestedFootprintMeters,
//  and the report tells the creator to set PropTemplate.useColliderBounds =
//  false with those numbers in customFootprintMeters.
//
//  ─────────────────────────────────────────────────────────────────────
//  REQUIRED FOLLOW-UP, AND IT MUST RIDE AN APP RELEASE
//
//  This rig is built out of ConfigurableJoint, and ConfigurableJoint is NOT
//  registered on the Lua surface. Assets/DreamPark/Editor/DreamParkLuaConfig.cs
//  lists Joint, FixedJoint, HingeJoint, SpringJoint and CharacterJoint — and
//  the comment beside CharacterJoint records why it was added:
//
//      "CharacterJoint was the one joint type missing, and it is the one
//       ragdolls need: creature_juice.lua.txt does
//       `part.go:AddComponent(typeof(UE.CharacterJoint))` and silently got
//       nothing back on device."
//
//  A creator who tries to tune a jiggle rig from Lua — `go:AddComponent(
//  typeof(UE.ConfigurableJoint))` — hits that exact silent-nothing failure,
//  one joint type over. Add typeof(ConfigurableJoint) to that list in the
//  same change. XLua codegen ships with the APP, not with content, so the
//  fix does not reach a device until an app release goes out; content baked
//  by this file works regardless, because the joints are serialized into the
//  prefab rather than created at runtime.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class JiggleRigBuilder
    {
        /// <summary>
        /// What a bake did, and enough of it for the report to be specific
        /// about which bones were touched.
        /// </summary>
        public struct RigResult
        {
            public bool ok;
            public Transform rootBone;
            /// Bones considered after the root and "_end" exclusions.
            public int boneCount;
            /// Bones that actually received a collider + rigidbody (a bone
            /// with no child, or a zero-length one, is skipped).
            public int riggedCount;
            public int hingeCount;
            /// Components removed by Clear() before the rebuild.
            public int clearedComponents;
            public bool archway;
            /// The one-line summary, in the wording the original baker used.
            public string summary;
            public string error;

            /// <summary>
            /// True whenever bones were rigged: their colliders join
            /// PropTemplate's footprint and then move every frame, so the
            /// PropTemplate stage must switch the prop to a manual footprint.
            /// See the footprint note in Apply.
            /// </summary>
            public bool footprintNeedsManualOverride;

            /// <summary>
            /// What to write into PropTemplate.customFootprintMeters: the
            /// bind-pose footprint in metres (X, Z), as handed in by the
            /// caller. Zero when the caller had nothing measured.
            /// </summary>
            public Vector2 suggestedFootprintMeters;
        }

        // ── Entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Rig the skinned mesh under <paramref name="visual"/>. Clears any
        /// previous bake first, so a re-run of the converter replaces the rig
        /// rather than layering a second one on top of it.
        ///
        /// This overload has no footprint measurement to hand on, so the
        /// footprint warning it emits cannot quote numbers. Prefer the
        /// overload that takes the bind-pose footprint.
        /// </summary>
        public static RigResult Apply(GameObject visual, BendSettings settings, ConversionResult r)
        {
            return Apply(visual, settings, r, Vector2.zero);
        }

        /// <summary>
        /// As above, with the bind-pose footprint in metres (X, Z) that Stage 4
        /// already measured. It is carried straight back out on
        /// <see cref="RigResult.suggestedFootprintMeters"/> for the PropTemplate
        /// stage, and quoted in the footprint warning. Pass Vector2.zero when
        /// nothing was measurable.
        /// </summary>
        public static RigResult Apply(GameObject visual, BendSettings settings, ConversionResult r,
                                      Vector2 bindPoseFootprintMeters)
        {
            RigResult result = new RigResult();

            if (visual == null)
            {
                result.error = "jiggle rig: no Visual object";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            BendSettings s = settings != null ? settings : new BendSettings();

            SkinnedMeshRenderer smr = FindSkinnedRenderer(visual);
            if (smr == null)
            {
                result.error = "jiggle rig: no SkinnedMeshRenderer under '" + visual.name + "'";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            Transform rootBone = ResolveRootBone(smr, visual.transform);
            if (rootBone == null)
            {
                result.error = "jiggle rig: '" + smr.name + "' has no rootBone and no bones array — "
                             + "the model is skinned but carries no skeleton, so there is nothing to rig";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }
            result.rootBone = rootBone;

            RegisterUndo(visual, "Bake Jiggle Rig");

            // Always clear first. Without it a bake REUSES whatever is
            // already there, which is how the original behaved and why stale
            // colliders could survive a bone-count change.
            result.clearedComponents = Clear(rootBone);

            RigResult built = Build(rootBone, s);
            built.rootBone = rootBone;
            built.clearedComponents = result.clearedComponents;

            // Clear() ran either way, so the object changed either way.
            EditorUtility.SetDirty(visual);

            if (built.clearedComponents > 0)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "jiggle rig: removed {0} component(s) from a previous bake before rebuilding",
                    built.clearedComponents));
            }

            if (!built.ok)
            {
                // "Nothing to jiggle" is not a failed conversion. Build's
                // skipped-… summaries describe a model that simply cannot have
                // a rig — no bones under the root, an archway with one bone, a
                // chain with nothing to span — while the prefab itself was
                // written and is complete and shippable. ConversionResult.Failed
                // sets r.ok = false for the WHOLE asset and the report window
                // counts it as failed, which is a lie about a prop that works.
                // Everywhere else in this pipeline (ColliderFitter's
                // unmeasurable geometry, the Shatter hand-off) "nothing to do"
                // is SKIPPED. Failed stays for the genuinely broken inputs
                // handled above: no Visual, no SkinnedMeshRenderer, no skeleton.
                if (IsSkippedSummary(built.summary))
                {
                    built.error = null;
                    Report(r, DecisionKind.Skipped, "jiggle rig on '" + rootBone.name + "': " + built.summary);
                }
                else
                {
                    built.error = "jiggle rig: " + built.summary;
                    Report(r, DecisionKind.Failed, built.error);
                }
                return built;
            }

            built.footprintNeedsManualOverride = true;
            built.suggestedFootprintMeters = bindPoseFootprintMeters;

            Report(r, DecisionKind.Added, string.Format(
                "jiggle rig on '{0}' ({1}) — {2}; bone radius {3:0.###}, spacing {4:0.###}, spring {5:0}, max force {6:0}",
                rootBone.name, smr.name, built.summary,
                s.boneRadius, s.colliderSpacing, s.jointSpring, s.jointMaxForce));

            Report(r, DecisionKind.Added,
                "jiggle rig: " + built.riggedCount + " bone(s) carry a collider and a rigidbody, with mass running from "
                + "heaviest at the anchor down to 1 at the tip — that gradient is what makes the chain settle instead of "
                + "whipping, and it is the first thing to restore if someone flattens it");

            WarnFootprintContamination(built, r);
            WarnOnBranchingSkeleton(rootBone, r);
            WarnOnNonUniformScale(visual, r);

            return built;
        }

        /// <summary>Convenience overload for callers holding the whole plan.</summary>
        public static RigResult Apply(GameObject visual, ConversionPlan plan, ConversionResult r)
        {
            return Apply(visual, plan != null ? plan.bend : null, r);
        }

        /// <summary>
        /// A summary Build produced for "this model cannot have a rig", as
        /// opposed to one for a broken input. The "skipped — " prefix is the
        /// contract between Build and Apply; keep the two in step.
        /// </summary>
        static bool IsSkippedSummary(string summary)
        {
            return !string.IsNullOrEmpty(summary)
                && summary.StartsWith("skipped — ", System.StringComparison.Ordinal);
        }

        // ── The port ────────────────────────────────────────────────────

        /// <summary>
        /// Ported from InflatableRigBuilder.Build(). The walk, the filters,
        /// the joint limits and the mass gradient are unchanged; only the
        /// settings type and the return shape differ, and
        /// <see cref="RigResult.summary"/> carries the original's one-line
        /// wording verbatim.
        ///
        /// ok == false has two flavours and the SUMMARY is what distinguishes
        /// them: anything starting "skipped — " means this model cannot carry a
        /// rig (no bones, one bone, nothing to span) and is not a failed
        /// conversion. Apply keys off that prefix — see IsSkippedSummary.
        /// </summary>
        public static RigResult Build(Transform rootBone, BendSettings settings)
        {
            RigResult result = new RigResult();

            if (rootBone == null)
            {
                result.summary = "skipped — no root bone";
                return result;
            }

            BendSettings s = settings != null ? settings : new BendSettings();
            result.rootBone = rootBone;
            result.archway = s.archway;

            // Depth-first, root excluded, and "_end" tip bones excluded:
            // Blender exports a zero-length terminator bone per chain and
            // colliding it does nothing but cost contacts.
            Transform[] bones = rootBone.GetComponentsInChildren<Transform>(true)
                .Where(b => !b.name.EndsWith("_end"))
                .Where(b => b != rootBone)
                .ToArray();

            result.boneCount = bones.Length;

            if (bones.Length == 0)
            {
                result.summary = "skipped — no bones under root";
                return result;
            }

            if (s.archway)
            {
                // One skeleton describing two legs: rig each half on its own,
                // then hinge the free ends so the arch closes.
                Transform[] chain1 = bones.Take(bones.Length / 2).ToArray();
                Transform[] chain2 = bones.Skip(bones.Length / 2).ToArray();
                if (chain1.Length == 0 || chain2.Length == 0)
                {
                    result.summary = "skipped — archway needs at least 2 bones";
                    return result;
                }

                result.riggedCount = JointChain(chain1, s) + JointChain(chain2, s);

                // Same trap as the plain chain below — see the note there. It
                // also has to happen BEFORE the hinges: with nothing rigged
                // there are no rigidbodies to connect them to and both hinges
                // would silently bind to the world.
                if (result.riggedCount == 0)
                {
                    result.summary = "skipped — no bone in the chain has a child to span, so nothing could be rigged";
                    return result;
                }

                Transform last1 = chain1[chain1.Length - 1];
                Transform last2 = chain2[chain2.Length - 1];

                Componentizer.DoComponent<HingeJoint>(last1.gameObject, true).connectedBody =
                    last2.GetComponent<Rigidbody>();
                Componentizer.DoComponent<HingeJoint>(last2.gameObject, true).connectedBody =
                    last1.GetComponent<Rigidbody>();

                // NOTE — the original assigned `maxForce = 100f` to its own
                // serialized field here, AFTER building the chains. So the
                // first bake of an arch used whatever was in the inspector and
                // every bake after it used 100. E_Arch's stored value is
                // already 100, so both readings agree; the value stays under
                // the plan's control rather than being silently rewritten
                // behind the creator's back.
                result.hingeCount = 2;
                result.ok = true;
                result.summary = $"archway rigged — {chain1.Length}+{chain2.Length} bones, 2 hinges";
                return result;
            }

            result.riggedCount = JointChain(bones, s);

            // bones.Length is NOT the same question as "did anything get
            // rigged". JointChain skips a bone with no child and a bone whose
            // first child is coincident with it, so a single-bone chain
            // (rootBone → Bone1 leaf) or a skeleton of stacked bones rigs
            // ZERO: no collider, no rigidbody, no joint anywhere. Returning ok
            // there reports "chain rigged — 1 bones" over an inert prop.
            if (result.riggedCount == 0)
            {
                result.summary = "skipped — no bone in the chain has a child to span, so nothing could be rigged";
                return result;
            }

            result.ok = true;
            result.summary = $"chain rigged — {bones.Length} bones";
            return result;
        }

        /// <summary>
        /// Ported from InflatableRigBuilder.JointChain(). Every physics value
        /// is byte-for-byte what shipped; the only addition is the tally the
        /// report quotes.
        /// </summary>
        static int JointChain(Transform[] bones, BendSettings s)
        {
            int rigged = 0;

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone.childCount == 0) continue;

                Transform child = bone.GetChild(0);
                float length = (child.position - bone.position).magnitude;
                if (length < 0.001f) continue;

                // Collider spans bone→child along local +Y. Negative spacing
                // deliberately overlaps neighbours (E_FlipGate wants that).
                BoxCollider collider = Componentizer.DoComponent<BoxCollider>(bone.gameObject, true);
                collider.size = new Vector3(s.boneRadius,
                                            Mathf.Max(0.01f, length - s.colliderSpacing),
                                            s.boneRadius);
                collider.center = new Vector3(0, length / 2f, 0);

                Rigidbody rb = Componentizer.DoComponent<Rigidbody>(bone.gameObject, true);

                if (i == 0)
                {
                    // Anchor. Everything downstream hangs off this.
                    rb.isKinematic = true;
                }
                else
                {
                    ConfigurableJoint joint =
                        Componentizer.DoComponent<ConfigurableJoint>(bone.gameObject, true);
                    joint.connectedBody = bones[i - 1].GetComponent<Rigidbody>();

                    joint.xMotion = ConfigurableJointMotion.Locked;
                    joint.yMotion = ConfigurableJointMotion.Locked;
                    joint.zMotion = ConfigurableJointMotion.Locked;

                    joint.angularXMotion = ConfigurableJointMotion.Limited;
                    joint.angularYMotion = ConfigurableJointMotion.Limited;
                    joint.angularZMotion = ConfigurableJointMotion.Locked;

                    joint.angularXLimitSpring = new SoftJointLimitSpring { spring = 1f, damper = 0f };
                    joint.lowAngularXLimit  = new SoftJointLimit { limit = 30f,  bounciness = 1f, contactDistance = 0f };
                    joint.highAngularXLimit = new SoftJointLimit { limit = 150f, bounciness = 1f, contactDistance = 0f };
                    joint.angularYZLimitSpring = new SoftJointLimitSpring { spring = 1f, damper = 0f };
                    joint.angularYLimit = new SoftJointLimit { limit = 60f, bounciness = 0f, contactDistance = 0f };
                    joint.angularZLimit = new SoftJointLimit { limit = 0f,  bounciness = 1f, contactDistance = 0f };

                    joint.angularXDrive = new JointDrive
                    {
                        positionSpring = s.jointSpring,
                        positionDamper = 0f,
                        maximumForce = s.jointMaxForce,
                        useAcceleration = true
                    };
                    joint.angularYZDrive = new JointDrive
                    {
                        positionSpring = s.jointSpring,
                        positionDamper = 0f,
                        maximumForce = s.jointMaxForce,
                        useAcceleration = true
                    };

                    joint.projectionMode = JointProjectionMode.PositionAndRotation;
                    joint.projectionDistance = 0.01f;
                    joint.projectionAngle = 1f;
                }

                rb.useGravity = false;
                // Unity 6 renamed drag/angularDrag. Set through the new names,
                // which is what this project compiles against.
                rb.linearDamping = 0f;
                rb.angularDamping = 1f;
                rb.sleepThreshold = 0f;
                // Heavier at the anchor, lighter at the tip — that mass gradient
                // is what makes the chain settle instead of whipping.
                rb.mass = bones.Length - i;

                rigged++;
            }

            return rigged;
        }

        /// <summary>
        /// Remove everything a bake generates, so a re-rig starts clean.
        /// Ported verbatim. Scoped to the bone hierarchy, so the prop's own
        /// Anchor collider — which lives outside it — is never touched.
        /// </summary>
        public static int Clear(Transform rootBone)
        {
            if (rootBone == null) return 0;
            int n = 0;
            foreach (var t in rootBone.GetComponentsInChildren<Transform>(true))
            {
                if (t == rootBone) continue;
                foreach (var c in t.GetComponents<Joint>())         { Object.DestroyImmediate(c, true); n++; }
                foreach (var c in t.GetComponents<BoxCollider>())   { Object.DestroyImmediate(c, true); n++; }
                foreach (var c in t.GetComponents<Rigidbody>())     { Object.DestroyImmediate(c, true); n++; }
            }
            return n;
        }

        // ── Skeleton resolution ─────────────────────────────────────────

        /// <summary>
        /// The first SkinnedMeshRenderer under <paramref name="go"/>,
        /// including inactive ones. Also the test BehaviorPackBuilder uses to
        /// choose between EasyBend and this file.
        /// </summary>
        public static SkinnedMeshRenderer FindSkinnedRenderer(GameObject go)
        {
            if (go == null) return null;
            return go.GetComponentInChildren<SkinnedMeshRenderer>(true);
        }

        /// <summary>
        /// The transform to rig from. SkinnedMeshRenderer.rootBone is
        /// authoritative when the importer set it; when it did not — which
        /// happens with several exporters — climb from the first bound bone
        /// to the topmost transform still inside <paramref name="limit"/>,
        /// which is the Armature/skeleton root.
        ///
        /// Build EXCLUDES whatever comes back from here, so returning the
        /// armature root rather than the hips is deliberate: it makes the
        /// hips the kinematic anchor, which is where the chain should hang
        /// from.
        /// </summary>
        public static Transform ResolveRootBone(SkinnedMeshRenderer smr, Transform limit)
        {
            if (smr == null) return null;
            if (smr.rootBone != null) return smr.rootBone;

            Transform[] bones = smr.bones;
            if (bones == null) return null;

            Transform first = null;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null) { first = bones[i]; break; }
            }
            if (first == null) return null;

            Transform top = first;
            Transform cursor = first.parent;
            while (cursor != null && cursor != limit && IsDescendantOf(cursor, limit))
            {
                top = cursor;
                cursor = cursor.parent;
            }
            return top;
        }

        // ── Caveats worth saying out loud ───────────────────────────────

        /// <summary>
        /// HAZARD 2, REINTRODUCED BY THIS FILE, AND NOT OBVIOUS ANYWHERE ELSE.
        ///
        /// The canonical hierarchy keeps colliders on Anchor precisely so the
        /// PropTemplate footprint cannot rotate. This rig puts a BoxCollider on
        /// every rigged bone, and those bones live under Visual — inside the
        /// prop root. PropTemplate.TryGetColliderFootprint walks
        /// GetComponentsInChildren&lt;Collider&gt;(true) from the root
        /// (PropTemplate.cs:354) and skips only !collider.enabled — it does not
        /// skip triggers and it does not know about the rig. So every bone box
        /// is part of the prop's floor footprint, and every one of them is
        /// driven by a non-kinematic Rigidbody: the footprint GapFiller
        /// (GapFiller.cs:929) and FloorCutout consume moves and rotates as the
        /// rig jiggles.
        ///
        /// It gets WORSE with the remedy for the anchor-collider jitter
        /// (BehaviorPackBuilder.WarnJiggleVersusAnchorCollider): with
        /// ColliderChoice.None the footprint becomes ONLY the jiggling bones.
        ///
        /// The fix is not in this file — it is PropTemplate.useColliderBounds
        /// = false plus a customFootprintMeters taken from the bind pose, which
        /// is why RigResult carries both back out.
        /// </summary>
        static void WarnFootprintContamination(RigResult built, ConversionResult r)
        {
            string footprint = built.suggestedFootprintMeters.x > 0f && built.suggestedFootprintMeters.y > 0f
                ? string.Format("{0:0.###} × {1:0.###} m (the bind-pose bounds Stage 4 measured, X × Z)",
                                built.suggestedFootprintMeters.x, built.suggestedFootprintMeters.y)
                : "the bind-pose bounds, X × Z — nothing measurable was handed in, so measure them by hand";

            Report(r, DecisionKind.Skipped, string.Format(
                "the rig's {0} bone collider(s) become part of this prop's FLOOR FOOTPRINT: "
                + "PropTemplate.TryGetColliderFootprint walks every collider under the root (PropTemplate.cs:354) and skips "
                + "only disabled ones — triggers and rig bones included — and every bone but the kinematic anchor is moved "
                + "by physics each frame, so GapFiller and FloorCutout see a footprint that jiggles with the prop. "
                + "Set PropTemplate.useColliderBounds = false and customFootprintMeters = {1}. "
                + "This matters MORE, not less, with the ColliderChoice.None recommended against the anchor-collider "
                + "jitter: with no anchor collider the footprint is nothing but the moving bones.",
                built.riggedCount, footprint));
        }

        /// <summary>
        /// The port walks the skeleton depth-first and joints bones[i] to
        /// bones[i-1]. On a single chain — which is what an inflatable is —
        /// that is the parent. On a BRANCHING skeleton, depth-first order
        /// puts the first bone of one branch immediately after the last bone
        /// of another, so those two get jointed to each other. The rig still
        /// runs; it just links across the fork. Say so rather than letting
        /// someone discover it by watching an arm pull on a leg.
        /// </summary>
        static void WarnOnBranchingSkeleton(Transform rootBone, ConversionResult r)
        {
            if (rootBone == null) return;

            int forks = 0;
            foreach (Transform t in rootBone.GetComponentsInChildren<Transform>(true))
            {
                if (t == rootBone) continue;
                if (t.name.EndsWith("_end")) continue;

                int realChildren = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    if (!t.GetChild(i).name.EndsWith("_end")) realChildren++;
                }
                if (realChildren > 1) forks++;
            }

            if (forks == 0) return;

            Report(r, DecisionKind.Skipped, string.Format(
                "the skeleton under '{0}' branches at {1} bone(s). This rig walks depth-first and joints each bone to the "
                + "PREVIOUS one in that order, so at a fork the first bone of one branch is jointed to the last bone of "
                + "the branch before it — the two limbs will pull on each other. It was tuned for the single chains in "
                + "E_Arch / E_FlipGate / E_Pylon. Rig branches separately, or accept the coupling.",
                rootBone.name, forks));
        }

        /// <summary>
        /// BoxCollider.size and the joint anchors are in bone-local units, so
        /// the Stage 2 fit scale carries through and boneRadius keeps meaning
        /// what it meant on the source model. A NON-uniform scale does not
        /// carry through: PhysX bakes a single scale per collider and joint
        /// frames skew, so the rig deforms in ways nothing in the inspector
        /// shows.
        /// </summary>
        static void WarnOnNonUniformScale(GameObject visual, ConversionResult r)
        {
            if (visual == null) return;
            Vector3 s = visual.transform.lossyScale;
            float max = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            float min = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            if (max <= 1e-6f || (max - min) / max < 0.01f) return;

            Report(r, DecisionKind.Skipped, string.Format(
                "'{0}' has a NON-UNIFORM world scale ({1:0.###}, {2:0.###}, {3:0.###}). PhysX bakes one scale per "
                + "collider, so the jiggle rig's boxes and joint frames will not match the mesh. PrefabScaler only ever "
                + "applies uniform scale — this came from somewhere else in the hierarchy and should be fixed there.",
                visual.name, s.x, s.y, s.z));
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
