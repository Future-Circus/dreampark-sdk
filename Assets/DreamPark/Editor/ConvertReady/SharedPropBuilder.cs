// ─────────────────────────────────────────────────────────────────────
//  SharedPropBuilder.cs — one base prefab, one variant per model.
//
//  WHY THIS EXISTS
//
//  A family of thirty collectables converted one at a time is thirty places
//  to change the bend spring, the category, the layer and the collider mode.
//  Nobody changes thirty prefabs; they change three, and the other twenty-
//  seven drift. A prefab variant family makes that impossible by
//  construction: everything except the model itself lives on the base, and
//  the base is the only thing anyone edits.
//
//  THE CORRECTNESS QUESTION THIS FILE IS BUILT AROUND
//
//  PrefabUtility.SaveAsPrefabAsset(instanceRoot, path) creates a VARIANT
//  when — and only when — instanceRoot is a prefab INSTANCE root. Unity's
//  docs for that method: "If the input object is a Prefab instance root the
//  resulting Prefab will be a Prefab Variant." Hand it a plain GameObject,
//  or an instance somebody unpacked, and you get a flattened copy that looks
//  identical in the Project window and inherits nothing, forever. Nothing
//  errors. So every variant this file writes is instantiated with
//  PrefabUtility.InstantiatePrefab, checked with IsOutermostPrefabInstanceRoot
//  BEFORE the save, and checked again with GetPrefabAssetType AFTER it. A
//  member that comes back as anything other than PrefabAssetType.Variant is
//  DELETED rather than shipped — a flattened member is worse than a missing
//  one, because it looks fine until someone edits the base and half the
//  family does not move.
//
//  THE OVERRIDE CONTRACT — the whole feature is only safe because this list
//  is short and enumerable:
//
//      MeshFilter.sharedMesh          the model — the entire point
//      MeshRenderer.sharedMaterials   travels with the mesh
//      Visual.localScale              Stage 2 fit is per-model
//      Visual.localPosition           Stage 3a ground offset is per-model
//      Anchor collider size / centre  Stage 4 derives from the model
//                                     (absent when shareCollider is on)
//      the root GameObject's name     unavoidable: the asset stem and the
//                                     root name have to agree or the prop
//                                     browser and the address disagree
//
//  Everything else is inherited, and that inherited set is the product.
//
//  WHY THE MESH SWAP IS A PROPERTY OVERRIDE AND WHEN IT CANNOT BE
//
//  Two serialized fields instead of a whole subtree is what keeps a variant
//  legible in the inspector — you open it and see exactly what makes this
//  coin different from that one. It only works for a SINGLE-MESH STATIC
//  model whose renderer sits at identity under its own root. A multi-mesh
//  FBX, a skinned mesh, an FBX carrying an Animator or an LODGroup, or a
//  mesh parked on a child node that is offset, rotated or SCALED (uniformly
//  or not — the two-field swap drops the child transform, and the base's
//  collider was fitted with it) cannot be expressed as two fields, so those
//  fall back to adding the model as a CHILD of Visual. Both paths
//  work; they behave differently the first time someone restructures the
//  base, so the report says which path every member took by name. The added
//  subtree also has any Collider and Rigidbody it brought with it removed:
//  collision belongs on Anchor, which never rotates, and a collider that
//  rides in under Visual either spins the GapFiller footprint (Bendy) or is
//  silently disabled by Unity as part of the root Rigidbody's compound
//  collider (Interactive).
//
//  WHY THE COLLIDER TYPE IS A FAMILY DECISION
//
//  A per-variant override can only change a primitive's size and centre. It
//  cannot change a component's TYPE, and ColliderFitter's mesh-collider rung
//  builds "Collision" child objects — a subtree, not two fields. So the
//  family fits ONE collider type against the union of every member, and a
//  mesh collider is downgraded to a box with that stated in the report,
//  rather than silently giving thirty variants thirty convex hulls to
//  simulate on a Quest.
//
//  THE LANDMINE THIS FLOW CREATES, WRITTEN DOWN WHERE THE CREATOR WILL SEE IT
//
//  ContentProcessor stamps PropTemplate.resourceName / GameArea.resourceName
//  with the asset's address, and per its own comment that is "what lets the
//  headset attribute revenue to the individual attraction". On a variant that
//  stamp is a property override — so "Revert All" on a variant silently
//  repoints that variant's revenue at the BASE prefab's key and nothing
//  errors. It looks like a tidy-up. It is a billing bug. Hence the line this
//  file writes into every family's report, and hence ResourceNameAddressCheck
//  in PreUploadChecks/Checks, which catches it whether or not this flow was
//  ever used.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using DreamPark.EditorTools;

namespace DreamPark.ConvertReady
{
    public static class SharedPropBuilder
    {
        /// The base is "_P_{Family}_Base". The leading underscore is the
        /// convention PropPrefabEmitter.PropAssetName already knows about, and
        /// it is what an upstream prop-browser exclusion would key on.
        public const string BasePrefix = "_P_";
        public const string BaseSuffix = "_Base";

        public const string FallbackFamilyName = "Family";

        /// One model is not a family — there is nothing to share.
        public const int MinimumFamilySize = 2;

        /// How close to identity a model's renderer transform has to be before
        /// the mesh may be lifted onto Visual as a property override. Anything
        /// larger is an authored offset or rotation that the two-field path
        /// would silently throw away.
        public const float IdentityEpsilon = 1e-4f;

        /// <summary>Which of the two mesh-swap paths a family member took.</summary>
        public enum MeshSwapPath
        {
            /// MeshFilter.sharedMesh + MeshRenderer.sharedMaterials on Visual.
            PropertyOverride,
            /// The model instantiated as a child of Visual — an added subtree.
            ModelChild,
        }

        // ── Entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Build one base prefab plus one prefab variant per model. Returns
        /// every asset path written, base first, so the caller can track them
        /// for the run's revert. Never throws: every failure appends to a
        /// ConversionResult on <paramref name="report"/> and returns whatever
        /// was written before it.
        /// </summary>
        public static List<string> BuildFamily(IList<string> modelPaths, ConversionPlan plan,
                                               ConversionReport report)
        {
            var created = new List<string>();
            if (report == null) report = new ConversionReport();

            var family = new ConversionResult { sourcePath = "(shared prop family)", ok = true };
            report.results.Add(family);

            try
            {
                BuildFamilyInner(modelPaths, plan, report, family, created);
            }
            catch (Exception e)
            {
                // A throw here is a bug in the converter, not in the assets.
                // Surfacing it as a failed result keeps it attached to the
                // family it happened in instead of becoming a bare console
                // error nobody can trace back to a selection.
                family.Failed("shared prop family: converter error — " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return created;
        }

        // ── Naming ──────────────────────────────────────────────────────

        /// <summary>"Coin" → "_P_Coin_Base". Sanitized, never empty.</summary>
        public static string BaseAssetName(string familyName)
        {
            return PropPrefabEmitter.PropAssetName(BasePrefix + CleanFamilyName(familyName) + BaseSuffix);
        }

        /// <summary>
        /// Sanitized, never empty, and stripped of a leading "P_" — the family
        /// name is normally the models' common prefix, and a set of already-
        /// converted props (P_Coin_Gold, P_Coin_Silver) makes that prefix
        /// "P_Coin", which would otherwise produce "_P_P_Coin_Base".
        /// </summary>
        private static string CleanFamilyName(string familyName)
        {
            // The fallback belongs to the caller — SanitizeAssetName's default is
            // "Asset", and taking it here would name a CJK-only or
            // punctuation-only family "_P_Asset_Base" while this file's own
            // FallbackFamilyName sat unused.
            string clean = AssetClassifier.SanitizeAssetName(familyName, FallbackFamilyName);

            clean = clean.TrimStart('_');
            if (clean.StartsWith(PropPrefabEmitter.RootPrefix, StringComparison.Ordinal))
                clean = clean.Substring(PropPrefabEmitter.RootPrefix.Length);

            return string.IsNullOrEmpty(clean) ? FallbackFamilyName : clean;
        }

        /// <summary>
        /// The variant's asset stem. "Coin" + "Coin_Gold.fbx" → "P_Coin_Gold",
        /// NOT "P_Coin_Coin_Gold": the family name is normally the models'
        /// common prefix, so prefixing it again reads like a typo. A model that
        /// does not already carry the family name gets it.
        ///
        /// This stem is the Addressables address and the revenue key — see
        /// ContentProcessor's "{gameId}/Props/{category}/{name}" — so it is not
        /// cosmetic and it has to be distinct across the family.
        /// </summary>
        public static string VariantAssetName(string familyName, string modelPath)
        {
            // FallbackPropName, not SanitizeAssetName's own "Asset" default: two
            // CJK-named members would otherwise both sanitize to "Asset" and the
            // family would be refused by NamesAreDistinct for a reason that
            // reads like a bug in the converter.
            string stem = AssetClassifier.SanitizeAssetName(
                Path.GetFileNameWithoutExtension(modelPath), PropPrefabEmitter.FallbackPropName);

            string clean = CleanFamilyName(familyName);

            bool alreadyCarriesFamily =
                stem.StartsWith(clean, StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith(PropPrefabEmitter.RootPrefix + clean, StringComparison.OrdinalIgnoreCase);

            return PropPrefabEmitter.PropAssetName(alreadyCarriesFamily ? stem : clean + "_" + stem);
        }

        /// <summary>
        /// Longest common prefix of the model file names — "Coin_Gold",
        /// "Coin_Silver", "Coin_Ruby" → "Coin". Falls back to the first name.
        /// Mirrors the menu's suggestion so a family built from the API and one
        /// built from the menu land on the same names.
        /// </summary>
        public static string FamilyNameFrom(IList<string> modelPaths)
        {
            if (modelPaths == null || modelPaths.Count == 0) return FallbackFamilyName;

            string prefix = Path.GetFileNameWithoutExtension(modelPaths[0]) ?? string.Empty;
            for (int i = 1; i < modelPaths.Count; i++)
            {
                string other = Path.GetFileNameWithoutExtension(modelPaths[i]) ?? string.Empty;
                int n = 0;
                while (n < prefix.Length && n < other.Length && prefix[n] == other[n]) n++;
                prefix = prefix.Substring(0, n);
                if (prefix.Length == 0) break;
            }

            prefix = prefix.TrimEnd('_', '-', ' ', '.');
            if (prefix.Length < 2)
                prefix = Path.GetFileNameWithoutExtension(modelPaths[0]);

            return AssetClassifier.SanitizeAssetName(prefix, FallbackFamilyName);
        }

        // ── The two mesh-swap paths ─────────────────────────────────────

        /// <summary>
        /// True when this model can be expressed as two property overrides on
        /// the base's Visual. False — with a reason fit for the report — when
        /// it has to be added as a child instead.
        ///
        /// <paramref name="modelRoot"/> is an instantiated model, or the model
        /// asset's root GameObject; only its hierarchy is read.
        /// </summary>
        public static bool CanUseMeshOverride(GameObject modelRoot, out Mesh mesh,
                                              out Material[] materials, out string reason)
        {
            mesh = null;
            materials = null;
            reason = null;

            if (modelRoot == null)
            {
                reason = "the model could not be loaded";
                return false;
            }

            var renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                reason = "it has no renderer";
                return false;
            }
            if (renderers.Length > 1)
            {
                reason = renderers.Length + " renderers — a multi-part model is a subtree, not two fields";
                return false;
            }

            var skinned = renderers[0] as SkinnedMeshRenderer;
            if (skinned != null)
            {
                reason = "it is skinned — the bones are part of the model and cannot be a MeshFilter field";
                return false;
            }

            var mr = renderers[0] as MeshRenderer;
            if (mr == null)
            {
                reason = renderers[0].GetType().Name + " draws without a MeshFilter";
                return false;
            }

            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                reason = "its renderer has no shared mesh";
                return false;
            }

            // Anything beyond Transform/MeshFilter/MeshRenderer is state the
            // two-field path would drop on the floor. An Animator on an FBX
            // root is the common one, and losing it is invisible until the prop
            // is in a park not animating.
            string extra;
            if (HasComponentsBeyondStaticMesh(modelRoot, out extra))
            {
                reason = "it carries " + extra + ", which only travels with the whole subtree";
                return false;
            }

            // The mesh moves to Visual's own origin, so any transform between
            // the model root and the renderer would be silently discarded. A
            // rotated or offset mesh node is exactly the case where the two
            // paths would produce visibly different props.
            if (!IsIdentityUnder(modelRoot.transform, mr.transform, out extra))
            {
                reason = "its mesh sits on a " + extra + " child node, which the two-field swap would discard";
                return false;
            }

            mesh = mf.sharedMesh;
            materials = mr.sharedMaterials;
            return true;
        }

        // ── Implementation ──────────────────────────────────────────────

        private sealed class Member
        {
            public string modelPath;
            public string variantName;
            public string outputPath;
            public GameObject source;
            public ConversionResult result;

            public MeshSwapPath swapPath;

            /// False once material extraction failed on this model. It is not the
            /// same as result.ok: a member can fail a later stage and still have
            /// had usable materials, and only THIS failure has to stop the member
            /// before a prefab is written against it.
            public bool materialsOk = true;

            public GameObject jig;          // temporary, lives only while the base is fitted
            public Bounds anchorBounds;     // the posed variant's bounds, in Anchor space
            public bool anchorBoundsKnown;
            public bool skinned;
        }

        private static void BuildFamilyInner(IList<string> modelPaths, ConversionPlan plan,
                                             ConversionReport report, ConversionResult family,
                                             List<string> created)
        {
            if (plan == null) { family.Failed("shared prop family: no ConversionPlan"); return; }

            if (modelPaths == null || modelPaths.Count < MinimumFamilySize)
            {
                family.Failed("shared prop family: needs at least " + MinimumFamilySize
                              + " models; got " + (modelPaths == null ? 0 : modelPaths.Count));
                return;
            }

            if (!plan.emitPropPrefab)
            {
                family.Failed("shared prop family: emitPropPrefab is off, and a variant family is "
                              + "nothing but prefabs — turn it on or convert these models individually");
                return;
            }

            string familyName = plan.shared != null ? plan.shared.familyName : null;
            if (string.IsNullOrEmpty(familyName)) familyName = FamilyNameFrom(modelPaths);
            familyName = CleanFamilyName(familyName);

            bool shareCollider = plan.shared != null && plan.shared.shareCollider;

            // ── Members, and the names they will ship under ──────────────
            var members = new List<Member>();
            foreach (string path in modelPaths)
            {
                var m = new Member
                {
                    modelPath = path,
                    variantName = VariantAssetName(familyName, path),
                    result = new ConversionResult { sourcePath = path, ok = true },
                };
                members.Add(m);
                report.results.Add(m.result);
            }

            string baseName = BaseAssetName(familyName);

            // Distinct file names are MANDATORY and are checked before anything
            // is written. The address, the preview PNG, the PreviewMetadataStore
            // key and resourceName are all built from the bare stem, so two
            // members sharing one resolve nondeterministically and share a
            // revenue key — the exact failure DuplicateNamesCheck blocks on.
            // Better to refuse the whole family than to ship half of it and let
            // that check explain the mess afterwards.
            if (!NamesAreDistinct(members, baseName, family)) return;

            // WHERE THE FAMILY LIVES, decided before Stage 1 because the
            // material extraction below has to target it.
            //
            // It used to pass DefaultMaterialsFolder(m.modelPath), which after
            // the kit refactor returns THE MODEL'S OWN DIRECTORY. So a family
            // built from Assets/RawFBX/*.fbx extracted its .mat files into
            // Assets/RawFBX — outside Assets/Content, therefore outside the
            // bundle — while every prefab went inside it. Result: variants that
            // pass every check here and ship pink on device, with the missing
            // materials sitting in a folder the uploader never looks at.
            string prefabsFolder = PropPrefabEmitter.DestinationFolder(family);

            // ── Stage 1: materials, before a single prefab is written ────
            // Order is load-bearing: a failed extraction has to abort a member
            // BEFORE it is wired into a variant that points at read-only pink
            // materials.
            if (plan.convertMaterials)
            {
                foreach (Member m in members)
                {
                    if (plan.extractEmbeddedMaterials && AssetClassifier.HasEmbeddedMaterials(m.modelPath))
                    {
                        // False means the materials are STILL embedded, i.e. still
                        // read-only and pink. ExtractEmbeddedMaterials has already
                        // appended its own Failed line; the only thing left to do
                        // is drop this member, because a variant wired to
                        // unconvertible materials plus a green report is strictly
                        // worse than a family one member short.
                        int extracted;
                        if (!AssetClassifier.ExtractEmbeddedMaterials(
                                m.modelPath, prefabsFolder, m.result, out extracted))
                        {
                            m.materialsOk = false;
                            m.result.Skipped("dropped from the family: its materials could not be "
                                + "extracted, so every variant built from it would point at read-only "
                                + "materials that cannot be converted");
                            continue;
                        }
                    }
                    ConvertMaterialsOn(m.modelPath, m.result);
                }
            }

            // ── Load the sources ────────────────────────────────────────
            var usable = new List<Member>();
            foreach (Member m in members)
            {
                if (!m.materialsOk) continue;   // already reported on its own result

                m.source = AssetDatabase.LoadAssetAtPath<GameObject>(m.modelPath);
                if (m.source == null)
                {
                    m.result.Failed("could not load '" + m.modelPath + "' as a model");
                    continue;
                }
                m.skinned = AssetClassifier.IsSkinned(m.source);
                usable.Add(m);
            }

            if (usable.Count < MinimumFamilySize)
            {
                family.Failed("shared prop family: only " + usable.Count + " of " + members.Count
                              + " models are usable (the rest failed to load or to release their "
                              + "embedded materials — see their own lines) — nothing written");
                return;
            }

            // prefabsFolder was resolved above, before Stage 1, so the
            // extracted materials and the prefabs cannot disagree. Emit()
            // re-derives the same value from `family` for the base.
            if (!AssetClassifier.EnsureFolder(prefabsFolder) || !AssetClassifier.CommitFolder(prefabsFolder))
            {
                family.Failed("shared prop family: could not create the folder " + prefabsFolder);
                return;
            }

            // ── The base ────────────────────────────────────────────────
            string basePath = BuildBase(baseName, usable, plan, family);
            if (string.IsNullOrEmpty(basePath)) return;
            created.Add(basePath);

            GameObject baseAsset = LoadWritten(basePath);
            if (baseAsset == null)
            {
                family.Failed("shared prop family: '" + basePath + "' was written but Unity has not "
                              + "imported it yet, so no variant can be made from it. Nothing else was "
                              + "written; run Shared Prop again.");
                return;
            }

            string baseStem = Path.GetFileNameWithoutExtension(basePath);

            // ── The variants ────────────────────────────────────────────
            int built = 0;
            for (int i = 0; i < usable.Count; i++)
            {
                Member m = usable[i];
                EditorUtility.DisplayProgressBar("Shared Prop — " + familyName,
                    m.variantName, (float)i / usable.Count);

                string outPath = BuildVariant(m, baseAsset, baseStem, prefabsFolder, plan,
                                              shareCollider);
                if (string.IsNullOrEmpty(outPath)) continue;

                m.outputPath = outPath;
                m.result.outputPath = outPath;
                created.Add(outPath);
                built++;
            }
            EditorUtility.ClearProgressBar();

            // ── The shared collider, now that every member is posed ─────
            if (shareCollider) FitSharedCollider(basePath, usable, family);

            ReportFamilySummary(family, familyName, baseStem, basePath, usable, built,
                                shareCollider, plan);
        }

        // ── The base prefab ─────────────────────────────────────────────

        /// <summary>
        /// The base carries the full canonical hierarchy, an EMPTY Visual, the
        /// collider, and the behavior pack. Returns the asset path, or null
        /// when nothing was written.
        /// </summary>
        private static string BuildBase(string baseName, List<Member> members,
                                        ConversionPlan plan, ConversionResult family)
        {
            bool needsMotion = PropPrefabEmitter.NeedsMotionNode(plan);
            GameObject root = PropPrefabEmitter.BuildHierarchy(baseName, needsMotion);
            if (root == null)
            {
                family.Failed("shared prop family: could not build the base hierarchy");
                return null;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(root);
                Transform visual = PropPrefabEmitter.FindVisual(root);
                if (anchor == null || visual == null)
                {
                    family.Failed("shared prop family: the base hierarchy is malformed");
                    return null;
                }

                // Every member is instantiated under Visual as a fitting JIG.
                // The jigs exist only to give the base a collider fitted to the
                // whole family and a measurement for the behavior pack's mass
                // and detection radius; they are destroyed before the base is
                // saved, and none of their poses ship.
                foreach (Member m in members)
                {
                    m.jig = (GameObject)PrefabUtility.InstantiatePrefab(m.source);
                    if (m.jig == null) continue;
                    m.jig.transform.SetParent(visual, false);
                    ResetLocal(m.jig.transform);
                }

                // ── Stages 2 + 3b: asked ONCE for the whole family ──────
                //
                // Per model would mean thirty dialogs for thirty coins, which
                // is not a flow anyone completes. One answer for the family is
                // also the honest shape of the question: a family is a set of
                // things that should be the same size, exported from the same
                // pipeline.
                Member rep = Representative(members);
                if (!ResolveFamilySizeAndOrientation(rep, members, visual.gameObject, plan, family))
                    return null;   // cancelled — nothing written at all

                // ── Stage 4: the collider, on the Anchor, fitted to the union ──
                //
                // Fitted against a THROWAWAY result first. ColliderFitter has no
                // classify-without-applying entry point, and its MEASURED line is
                // written as soon as it places something — so reporting straight
                // into `family` would print "collider: MeshCollider on Anchor"
                // and then "collider: BoxCollider on Anchor" two lines later. A
                // MEASURED line naming a collider that did not ship is exactly
                // what this report format exists to prevent. The probe's lines
                // are replayed verbatim when they turn out to be true, so the
                // only cost is one extra triangle walk on the downgrade branch.
                var probe = new ConversionResult { sourcePath = family.sourcePath, ok = true };

                ColliderFitter.FitResult fit;
                ColliderFitter.Fit(anchor.gameObject, visual.gameObject, plan, probe, out fit);

                if (fit.choice == ColliderChoice.ConvexMesh || fit.choice == ColliderChoice.MeshStatic)
                {
                    // A mesh collider cannot be a per-variant property override
                    // (ColliderFitter expresses it as "Collision" child objects,
                    // i.e. a subtree), and shared it would put every member's
                    // geometry on every member. Both are worse than a box.
                    //
                    // The second Fit cleans up after the first: ApplyBox clears
                    // the anchor's colliders and the generated "Collision"
                    // children, so nothing from the probe survives onto the base.
                    ConversionPlan boxed = plan.Clone();
                    boxed.collider = ColliderChoice.Box;
                    ColliderFitter.Fit(anchor.gameObject, visual.gameObject, boxed, family, out fit);

                    family.Skipped(
                        "collider: the family measured as a mesh collider, which cannot be shared or "
                        + "overridden per variant — a Box fitted to the union is used instead. Convert "
                        + "these models individually if any of them needs mesh-accurate collision.");
                }
                else
                {
                    Replay(probe, family);
                }

                // The jigs have done their job. They go BEFORE the behavior pack
                // runs, so BehaviorPackBuilder sees the empty Visual it will see
                // on every re-run — with a skinned jig still parented it would
                // take the jiggle-rig branch and rig a temporary object.
                foreach (Member m in members)
                {
                    if (m.jig != null) UnityEngine.Object.DestroyImmediate(m.jig);
                    m.jig = null;
                }

                // ── The empty Visual ────────────────────────────────────
                // MeshFilter + MeshRenderer present with no mesh: that pair IS
                // the override slot every single-mesh variant writes into. A
                // renderer with a null mesh draws nothing and is skipped by
                // every bounds walk in this pipeline (AssetClassifier drops
                // zero-size sources deliberately), so it costs nothing on the
                // members that take the child path.
                Componentizer.DoComponent<MeshFilter>(visual.gameObject, true);
                MeshRenderer mr = Componentizer.DoComponent<MeshRenderer>(visual.gameObject, true);
                if (mr != null) mr.sharedMaterials = new Material[0];

                family.Added("base: '" + PropPrefabEmitter.VisualNodeName
                             + "' left empty (MeshFilter + MeshRenderer, no mesh) — that pair is the "
                             + "slot each variant overrides");

                // ── Stage 5: emit ───────────────────────────────────────
                string path = PropPrefabEmitter.Emit(root, baseName, plan, family);
                if (string.IsNullOrEmpty(path))
                {
                    family.Failed("shared prop family: the base prefab was not written");
                    return null;
                }

                // ── Stage 6: behavior, INSIDE the saved base ────────────
                // Applied to the asset rather than to this in-memory root, for
                // the same two reasons the single-asset path does it: Emit's
                // root contract check would otherwise report the Rigidbody and
                // Interactable it is about to add as stray root components, and
                // anything that needs a real asset path (sub-assets) needs the
                // prefab to exist first.
                ApplyFamilyBehavior(path, plan, family, fit.measurement, members);
                return path;
            }
            finally
            {
                foreach (Member m in members)
                {
                    if (m.jig != null) { UnityEngine.Object.DestroyImmediate(m.jig); m.jig = null; }
                }
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// One size question and one up-axis question for the whole family.
        /// Returns false only when the creator cancelled, in which case nothing
        /// is written — falling through to a default here is exactly how a
        /// converter loses trust.
        /// </summary>
        private static bool ResolveFamilySizeAndOrientation(Member rep, List<Member> members,
                                                            GameObject visual, ConversionPlan plan,
                                                            ConversionResult family)
        {
            if (rep == null || rep.jig == null)
            {
                family.Skipped("scale: no measurable model in the family — every member is left at "
                               + "its authored size");
                return true;
            }

            ScaleDecision decision = plan.fitScale
                ? ScaleInference.Infer(rep.modelPath, rep.jig, plan.scale.axis)
                : ScaleDecision.Skip();

            string upReason;
            UpAxisFix guessedUp = OrientationFitter.GuessUpAxis(rep.jig, rep.modelPath, out upReason);

            bool applyScale = decision.apply;
            float target = decision.targetMeters;
            PrefabScalerAxis axis = decision.axis;
            UpAxisFix chosenUp = guessedUp;

            if (ScaleConfirmPopup.NeedsConfirmation(decision, guessedUp))
            {
                var answer = ScaleConfirmPopup.Ask(
                    Path.GetFileNameWithoutExtension(rep.modelPath)
                        + "  (+" + (members.Count - 1) + " more in this family)",
                    decision, guessedUp, upReason, rep.source);

                if (answer.cancelled)
                {
                    family.Skipped("cancelled at the size/orientation step — nothing written");
                    return false;
                }

                applyScale = answer.applyScale;
                chosenUp = answer.upAxis;

                // Axis BEFORE target: the conversion below is axis-dependent.
                axis = answer.axis;

                // The dialog speaks VISIBLE size — the union of every renderer's
                // bounds along its longest axis, which is what the creator sees
                // and types. ScaleToFit divides by something else: the chosen
                // axis of the FIRST renderer's mesh-local bounds. On a
                // single-mesh prop those agree; on a multi-part model they are
                // off by their ratio, which is 20× or 40× on a rug or a car.
                // Convert here, once, so everything downstream — confirmed,
                // plan.scale, the manifest, the per-variant overrides — is in
                // argument space and stays there.
                target = ScaleInference.TargetForVisibleSize(rep.jig, answer.desiredVisibleMeters, axis);
            }

            // The confirmed answer, assembled BEFORE the up-axis fix touches it.
            // A ScaleDecision's target only means anything alongside the axis it
            // was computed from — it is fitSize[axis] × correction — so the two
            // have to move together from here on.
            ScaleDecision confirmed = new ScaleDecision
            {
                apply = applyScale,
                targetMeters = target,
                axis = axis,
                measuredMeters = decision.measuredMeters,
                fileScale = decision.fileScale,
                confidence = decision.confidence,
                humanReference = decision.humanReference,
            };

            // The up-axis fix lives on the BASE's Visual: it is a property of
            // the export pipeline, not of the individual model, so it is shared
            // and it is not in the override set. Orientation first — standing a
            // model up permutes which axis is "tall", and the fit axis is named
            // in the mesh's own space.
            if (chosenUp != UpAxisFix.None)
            {
                OrientationFitter.ApplyUpAxis(visual, chosenUp, family);

                // Retarget, NEVER a bare RemapFitAxis assignment. Remapping the
                // axis on a finished decision leaves targetMeters describing a
                // different mesh dimension: mesh-local size (10, 200, 3),
                // measured 800 m and correction 0.01 gives target 2 on Y, and
                // swapping the axis to Z by hand makes ScaleToFit divide by 3
                // instead of 200 — factor 0.667 rather than 0.01, and the model
                // ships at 533 m under a MEASURED line claiming 2 m. Retarget
                // rescales the target with the axis, and drops the fit (saying
                // so) when the new axis is degenerate on this mesh. It reads
                // MESH-local bounds, so the rotation just applied to Visual does
                // not disturb it.
                confirmed = ScaleInference.Retarget(
                    confirmed, rep.jig,
                    OrientationFitter.RemapFitAxis(confirmed.axis, chosenUp), family);

                family.Guessed("up-axis: applied to the base, so every variant inherits it — "
                               + (string.IsNullOrEmpty(upReason) ? "confirmed by you" : upReason + ", confirmed by you"));
            }
            else if (guessedUp != UpAxisFix.None)
            {
                family.Guessed("up-axis: left as authored — you declined the Z-up correction");
            }

            // Remember the answer for the per-variant pass. Writing it back into
            // the plan keeps one source of truth: the variants read exactly what
            // the creator confirmed, and the manifest records the same numbers.
            plan.scale = confirmed;
            plan.upAxis = chosenUp;

            // Read back off plan.scale rather than off the local answers: a
            // degenerate retarget can have turned the fit off, and reporting the
            // number the creator typed while shipping something else is the one
            // thing this report format exists to prevent.
            bool fitting = plan.scale.apply && plan.scale.targetMeters > 0f;

            if (fitting)
            {
                // Axis-qualified deliberately. plan.scale.targetMeters is a
                // ScaleToFit argument — the size of ONE axis of the first
                // renderer's mesh bounds — not the prop's overall size. Printing
                // it as "fitted to 0.05 m" next to a creator who typed 2 m is
                // exactly the confusion the two-spaces rule exists to prevent.
                family.Measured(ScaleInference.Explain(decision) + " → every member of this family is "
                                + "fitted so its " + plan.scale.axis + " measures "
                                + FormatMeters(plan.scale.targetMeters)
                                + ", so the set is uniform. The fit scale is a per-variant override.");
            }
            else
            {
                family.Measured(ScaleInference.Explain(decision) + " → left at authored size");
            }

            // Pose the jigs roughly the way their variants will be posed, so the
            // base's collider is fitted to something the family will actually
            // occupy. Silent by design: the real numbers are reported per
            // variant, and in shared-collider mode the base is re-fitted from
            // the variants' true bounds once they exist — which is what makes
            // this provisional pose good enough. (The jigs share one Visual, so
            // they are grounded individually rather than through it.)
            foreach (Member m in members)
            {
                if (m.jig == null) continue;
                // plan.scale, not the pre-retarget locals: the jigs have to be
                // posed exactly the way BuildVariant will pose the real thing,
                // or the base's collider is fitted to a size nothing ships at.
                if (fitting)
                    PrefabScaler.ScaleToFit(m.jig, plan.scale.targetMeters,
                                            ToPrefabScalerAxis(plan.scale.axis));
                if (plan.groundPivot)
                    OrientationFitter.GroundPivot(m.jig, null);
            }

            return true;
        }

        private static void ApplyFamilyBehavior(string basePath, ConversionPlan plan,
                                                ConversionResult family,
                                                ColliderFitter.MeshMeasurement measurement,
                                                List<Member> members)
        {
            if (plan.behavior == BehaviorPack.None && !plan.addRigidbody) return;

            if (plan.behavior == BehaviorPack.Shatter)
            {
                // The baked pieces ARE the model — there is nothing shareable
                // about them, and baking per variant would put a full fracture
                // subtree on every member, defeating the point of the family.
                family.Skipped(
                    "behavior: Shatterable is a per-model bake (the pieces are the model), so it "
                    + "cannot live on a shared base. This family was built without it — convert these "
                    + "models individually with the Shatterable preset if you need them to break.");
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(basePath);
            if (contents == null)
            {
                family.Failed("behavior: could not open '" + basePath + "' to configure the family");
                return;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(contents);
                Transform motion = PropPrefabEmitter.FindMotion(contents);
                Transform visual = PropPrefabEmitter.FindVisual(contents);

                if (anchor == null || visual == null)
                {
                    family.Failed("behavior: the base hierarchy is malformed — nothing configured");
                    return;
                }

                // The measurement is handed over rather than re-derived: the
                // base's Visual is empty, so BehaviorPackBuilder would measure
                // nothing and fall back to a default mass and a 0.75 m bend
                // radius around a 5 cm coin. This one came from the union of
                // every member, in Anchor space, which is the space it expects.
                BehaviorPackBuilder.Apply(contents, anchor.gameObject,
                                          motion != null ? motion.gameObject : null,
                                          visual.gameObject, plan, family, measurement);

                PrefabUtility.SaveAsPrefabAsset(contents, basePath);
                AssetDatabase.ImportAsset(basePath, ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            family.Added("behavior: configured once on the base — mass, layer, bend tuning and every "
                         + "other field are inherited by all " + members.Count + " variants; none of "
                         + "them is in the override set");

            if (plan.behavior == BehaviorPack.Bendy)
            {
                var skinnedNames = new List<string>();
                foreach (Member m in members) if (m.skinned) skinnedNames.Add(NameOf(m.modelPath));

                if (skinnedNames.Count > 0)
                {
                    // EasyBend rotates ONE transform. On a rig that swings the
                    // whole character stiffly; the individual Bendy path builds
                    // a jiggle rig instead, and a jiggle rig is per-model.
                    family.Skipped("behavior: " + string.Join(", ", skinnedNames.ToArray())
                        + " " + (skinnedNames.Count == 1 ? "is" : "are") + " skinned. EasyBend on the "
                        + "shared Motion node rotates the whole model rigidly on those — convert them "
                        + "individually with Bendy to get a jiggle rig instead.");
                }
            }
        }

        // ── One variant ─────────────────────────────────────────────────

        /// <summary>
        /// Instantiate the base, apply the enumerated overrides and NOTHING
        /// else, then save as a variant. Returns the asset path or null.
        /// </summary>
        private static string BuildVariant(Member m, GameObject baseAsset, string baseStem,
                                           string prefabsFolder, ConversionPlan plan,
                                           bool shareCollider)
        {
            ConversionResult r = m.result;

            GameObject inst = PrefabUtility.InstantiatePrefab(baseAsset) as GameObject;
            if (inst == null)
            {
                r.Failed("variant: could not instantiate the base prefab '" + baseStem + "'");
                return null;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(inst);
                Transform visual = PropPrefabEmitter.FindVisual(inst);
                if (anchor == null || visual == null)
                {
                    r.Failed("variant: the base hierarchy is malformed — expected "
                             + PropPrefabEmitter.AnchorNodeName + "/…/" + PropPrefabEmitter.VisualNodeName);
                    return null;
                }

                string desiredPath = prefabsFolder + "/" + m.variantName + PropPrefabEmitter.PrefabExtension;
                bool uniquified;
                string path = PropPrefabEmitter.UniqueAssetPath(desiredPath, out uniquified);
                string stem = Path.GetFileNameWithoutExtension(path);

                if (uniquified)
                {
                    r.Added("variant: '" + m.variantName + "' already existed — saved as '" + stem
                            + "' instead. Nothing was overwritten.");
                }

                // The root name is the sixth override and it is unavoidable: the
                // asset stem becomes the address, and a root object named after
                // the BASE is how someone ends up searching the prop browser for
                // the wrong string.
                //
                // GameObject.name is m_Name on the instance root and obeys the
                // same rule as every other field written from script: Unity
                // records a PropertyModification when the change comes through
                // the Inspector or a SerializedObject, not when it comes through
                // a field write. Unrecorded, the saved variant keeps the BASE's
                // name and thirty coins all ship with a root called
                // _P_Coin_Base.
                inst.name = stem;
                RecordOverride(inst);

                // ── Override: the model ─────────────────────────────────
                GameObject scaleTarget = ApplyModel(m, visual.gameObject, r);
                if (scaleTarget == null) return null;

                // ── Override: Stage 2 fit ───────────────────────────────
                if (plan.fitScale && plan.scale.apply && plan.scale.targetMeters > 0f)
                {
                    var scale = PrefabScaler.ScaleToFit(scaleTarget, plan.scale.targetMeters,
                                                        ToPrefabScalerAxis(plan.scale.axis));
                    if (scale.ok)
                    {
                        r.Measured("scale: fitted to " + FormatMeters(plan.scale.targetMeters) + " on "
                                   + plan.scale.axis + " (×" + scale.factor.ToString("0.####", Inv) + ")");

                        string caveat = ScaleInference.FitCaveat(scaleTarget);
                        if (!string.IsNullOrEmpty(caveat)) r.Measured("scale: " + caveat);
                    }
                    else
                    {
                        r.Failed("scale: " + scale.error);
                    }
                }

                // ── Override: Stage 3a ground pivot ─────────────────────
                // Grounded on VISUAL for both paths, never on the added child.
                // GroundPivot measures in the node's PARENT space, so pointing
                // it at the child would ground the model inside Visual's own
                // frame — and with an up-axis fix on Visual that frame is
                // rotated, which puts the "floor" on the model's side. Visual's
                // parent (Motion or Anchor) is the frame the park stands the
                // prop up in, and Visual.localPosition is the override slot the
                // contract names.
                if (plan.groundPivot) OrientationFitter.GroundPivot(visual.gameObject, r);

                // Direct field writes on a prefab instance are not automatically
                // recorded as overrides — Unity records them when the change goes
                // through the Inspector or a SerializedObject. Without this call
                // the transform values can be reverted the moment the instance is
                // reloaded, and the variant would ship at the base's scale.
                RecordOverride(visual.transform);
                if (scaleTarget != visual.gameObject) RecordOverride(scaleTarget.transform);

                // ── Override: Stage 4 collider size / centre ────────────
                MeasureInAnchorSpace(m, visual.gameObject, anchor);
                if (!shareCollider) OverrideCollider(m, anchor, r);
                else r.Skipped("collider: shared with the family — no per-variant override");

                // ── Save, and prove it really is a variant ──────────────
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(inst))
                {
                    r.Failed("variant: '" + stem + "' stopped being a prefab instance before it could be "
                             + "saved, so saving it would produce a flattened copy that inherits nothing "
                             + "from " + baseStem + ". Nothing was written.");
                    return null;
                }

                if (!AssetClassifier.CommitFolder(prefabsFolder))
                {
                    r.Failed("variant: folder is not in the AssetDatabase: " + prefabsFolder);
                    return null;
                }

                bool saved;
                GameObject asset;
                try
                {
                    asset = PrefabUtility.SaveAsPrefabAsset(inst, path, out saved);
                }
                catch (Exception e)
                {
                    r.Failed("variant: could not save " + path + " — " + e.Message);
                    return null;
                }

                if (!saved || asset == null)
                {
                    AssetClassifier.CommitFolder(prefabsFolder);
                    try { asset = PrefabUtility.SaveAsPrefabAsset(inst, path, out saved); }
                    catch (Exception e)
                    {
                        r.Failed("variant: could not save " + path + " — " + e.Message);
                        return null;
                    }
                }

                if (!saved || asset == null)
                {
                    r.Failed("variant: Unity refused to save " + path);
                    return null;
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                PrefabAssetType type = PrefabUtility.GetPrefabAssetType(asset);
                if (type != PrefabAssetType.Variant)
                {
                    // A flattened member is worse than a missing one: it looks
                    // right until someone edits the base and it does not follow.
                    AssetDatabase.DeleteAsset(path);
                    r.Failed("variant: " + stem + " saved as " + type + " rather than a prefab VARIANT, "
                             + "so it would have inherited nothing from " + baseStem
                             + ". The file was deleted rather than shipped.");
                    return null;
                }

                // Belt and braces on the name override above: if it did not take,
                // the file stem and the root object disagree, and the stem is
                // what ContentProcessor turns into the address. Reported rather
                // than deleted — the prefab is otherwise correct, and renaming
                // the root by hand fixes it.
                if (!string.Equals(asset.name, stem, StringComparison.Ordinal))
                {
                    r.Failed("variant: " + path + " saved with its root object still named '"
                             + asset.name + "'. The asset stem is what becomes the Addressables "
                             + "address and the prop-browser entry, so rename the root to '" + stem
                             + "' before uploading.");
                }

                // Do not resurrect a member that already failed a stage — Failed
                // sets both error and ok, and a saved file does not undo that.
                if (string.IsNullOrEmpty(r.error)) r.ok = true;
                r.Added("variant: " + stem + " → " + path + "  (variant of " + baseStem + ")");
                RecordInManifest(path, plan, asset, m.modelPath);
                return path;
            }
            finally
            {
                if (inst != null) UnityEngine.Object.DestroyImmediate(inst);
            }
        }

        /// <summary>
        /// Put the model on the variant, by whichever of the two paths this
        /// model supports, and return the node that carries the fit scale and
        /// the ground offset.
        /// </summary>
        private static GameObject ApplyModel(Member m, GameObject visual, ConversionResult r)
        {
            Mesh mesh;
            Material[] materials;
            string why;

            if (CanUseMeshOverride(m.source, out mesh, out materials, out why))
            {
                MeshFilter mf = visual.GetComponent<MeshFilter>();
                MeshRenderer mr = visual.GetComponent<MeshRenderer>();
                if (mf == null || mr == null)
                {
                    r.Failed("variant: the base's '" + PropPrefabEmitter.VisualNodeName
                             + "' has no MeshFilter/MeshRenderer to override");
                    return null;
                }

                mf.sharedMesh = mesh;
                mr.sharedMaterials = materials;
                RecordOverride(mf);
                RecordOverride(mr);

                m.swapPath = MeshSwapPath.PropertyOverride;

                r.Measured("mesh: swapped as two property overrides on '" + PropPrefabEmitter.VisualNodeName
                           + "' (MeshFilter.sharedMesh, MeshRenderer.sharedMaterials × "
                           + (materials != null ? materials.Length : 0) + ") — this variant adds no objects");
                return visual;
            }

            GameObject child = PrefabUtility.InstantiatePrefab(m.source) as GameObject;
            if (child == null)
            {
                r.Failed("variant: could not instantiate '" + m.modelPath + "'");
                return null;
            }

            child.transform.SetParent(visual.transform, false);
            ResetLocal(child.transform);

            // This path is taken exactly when the model carries components
            // beyond Transform/MeshFilter/MeshRenderer — i.e. precisely the
            // models likeliest to bring colliders, and a Rigidbody too when the
            // source is a .prefab. Both are hazards the emitted hierarchy owns:
            //
            //  - A non-convex MeshCollider arriving inside a prop whose root has
            //    a Rigidbody joins that body's compound collider, and Unity
            //    SILENTLY disables it. The prop falls through the floor and
            //    nothing points at the cause.
            //  - Under the Bendy preset Visual sits below Motion, whose
            //    localRotation EasyBend rewrites every Update. A collider there
            //    is picked up by PropTemplate.TryGetColliderFootprint
            //    (GetComponentsInChildren<Collider>), so GapFiller and
            //    FloorCutout get a floor footprint that spins.
            //
            // The collider the prop actually uses is the one ColliderFitter put
            // on Anchor, which never rotates, so nothing is lost. This runs
            // before MeasureInAnchorSpace so the reported bounds are the bounds
            // of what ships.
            StripModelPhysics(child, r);

            m.swapPath = MeshSwapPath.ModelChild;

            // Said out loud because the two paths diverge the first time someone
            // restructures the base: a property override follows the base's
            // Visual anywhere, an added child is re-parented by Unity's own
            // best guess.
            r.Skipped("mesh: added as a CHILD of '" + PropPrefabEmitter.VisualNodeName
                      + "' rather than swapped into it — " + why
                      + ". This variant carries an added object, not two property overrides, and the "
                      + "base's empty MeshRenderer stays unused on it.");
            return child;
        }

        /// <summary>
        /// Remove the colliders and rigidbodies that came in with an added model
        /// subtree. Reported, never silent — a stripped collider is a change to
        /// what the creator handed us, and a collider that REFUSED to go is a
        /// live hazard they need to know about.
        /// </summary>
        private static void StripModelPhysics(GameObject modelRoot, ConversionResult r)
        {
            if (modelRoot == null) return;

            int stripped = 0;
            var survivors = new List<string>();

            var physics = new List<Component>();
            physics.AddRange(modelRoot.GetComponentsInChildren<Collider>(true));
            physics.AddRange(modelRoot.GetComponentsInChildren<Rigidbody>(true));

            foreach (Component c in physics)
            {
                if (c == null) continue;
                string label = c.GetType().Name + " on '" + c.gameObject.name + "'";
                try
                {
                    Componentizer.DoDestroy(c);
                }
                catch (Exception e)
                {
                    survivors.Add(label + " (" + e.Message + ")");
                    continue;
                }

                // Unity refuses some removals on a prefab instance rather than
                // throwing, so the only reliable answer is whether the reference
                // is dead afterwards.
                if (c == null) stripped++;
                else survivors.Add(label);
            }

            if (stripped > 0)
            {
                r.Skipped("mesh: removed " + stripped + " collider/rigidbody component"
                    + (stripped == 1 ? "" : "s") + " that came in with the model — collision belongs on '"
                    + PropPrefabEmitter.AnchorNodeName + "', which never rotates, and a collider on a "
                    + "rotating node spins this prop's floor footprint");
            }

            if (survivors.Count > 0)
            {
                r.Failed("mesh: could not remove " + string.Join(", ", survivors.ToArray())
                    + " from the added model. A collider below '" + PropPrefabEmitter.VisualNodeName
                    + "' rotates with the bend and spins the GapFiller footprint, and a non-convex one "
                    + "is silently disabled by Unity when the root has a Rigidbody. Delete "
                    + (survivors.Count == 1 ? "it" : "them") + " on this variant by hand.");
            }
        }

        // ── Collider ────────────────────────────────────────────────────

        private static void MeasureInAnchorSpace(Member m, GameObject visual, Transform anchor)
        {
            Bounds b;
            m.anchorBoundsKnown = AssetClassifier.TryGetBoundsInSpace(
                visual, anchor.worldToLocalMatrix, false, out b);
            m.anchorBounds = b;
        }

        /// <summary>
        /// Write this model's size and centre onto the collider the base
        /// provides. Deliberately does NOT add or replace a collider: adding one
        /// would make the variant carry a second collider on top of the base's,
        /// and PropTemplate's footprint reads every collider in the hierarchy.
        /// </summary>
        private static void OverrideCollider(Member m, Transform anchor, ConversionResult r)
        {
            Collider col = anchor.GetComponent<Collider>();
            if (col == null) return;                    // ColliderChoice.None — nothing to override

            if (!m.anchorBoundsKnown)
            {
                r.Skipped("collider: nothing measurable on this model — it keeps the family's collider");
                return;
            }

            Bounds b = m.anchorBounds;
            float min = ColliderFitter.MinExtentMeters;

            var box = col as BoxCollider;
            if (box != null)
            {
                Vector3 size = new Vector3(
                    Mathf.Max(min, b.size.x), Mathf.Max(min, b.size.y), Mathf.Max(min, b.size.z));
                box.size = size;
                box.center = b.center;
                RecordOverride(box);
                r.Measured("collider: BoxCollider size " + FormatSize(size) + " and centre overridden "
                           + "for this variant; the type and everything else stay shared");
                return;
            }

            var sphere = col as SphereCollider;
            if (sphere != null)
            {
                float radius = Mathf.Max(min, 0.5f * Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)));
                sphere.radius = radius;
                sphere.center = b.center;
                RecordOverride(sphere);
                r.Measured("collider: SphereCollider radius " + FormatMeters(radius)
                           + " and centre overridden for this variant");
                return;
            }

            var capsule = col as CapsuleCollider;
            if (capsule != null)
            {
                // direction stays SHARED. It is the family's axis, and flipping
                // it per variant would be a third override for no gain — but a
                // member whose own dominant axis disagrees is worth saying out
                // loud, because its capsule will look wrong and nothing else
                // will explain why.
                int dir = Mathf.Clamp(capsule.direction, 0, 2);
                float along = Component(b.size, dir);
                float a, other;
                OtherTwo(b.size, dir, out a, out other);

                capsule.radius = Mathf.Max(min, 0.5f * Mathf.Max(a, other));
                capsule.height = Mathf.Max(capsule.radius * 2f, along);
                capsule.center = b.center;
                RecordOverride(capsule);

                int dominant = DominantAxis(b.size);
                string note = dominant == dir ? "" :
                    " — note this model is longest on " + AxisName(dominant)
                    + " while the family's capsule points " + AxisName(dir);
                r.Measured("collider: CapsuleCollider radius " + FormatMeters(capsule.radius)
                           + ", height " + FormatMeters(capsule.height)
                           + " and centre overridden for this variant" + note);
                return;
            }

            // A MeshCollider is downgraded to a Box when the family is built, so
            // this is only reachable if someone restructures the base by hand.
            r.Skipped("collider: '" + col.GetType().Name + "' has no size/centre to override per "
                      + "variant — this variant uses the family's collider as-is");
        }

        /// <summary>
        /// shareCollider: one collider on the base, fitted to the union of every
        /// member as it was actually posed. Done after the variants exist so the
        /// union is the union of what shipped, not of a provisional pose.
        /// </summary>
        private static void FitSharedCollider(string basePath, List<Member> members,
                                              ConversionResult family)
        {
            Bounds union = new Bounds();
            bool any = false;
            Member biggest = null;
            float biggestSize = 0f;

            foreach (Member m in members)
            {
                if (!m.anchorBoundsKnown || string.IsNullOrEmpty(m.outputPath)) continue;
                if (!any) { union = m.anchorBounds; any = true; }
                else union.Encapsulate(m.anchorBounds);

                float longest = Mathf.Max(m.anchorBounds.size.x,
                                Mathf.Max(m.anchorBounds.size.y, m.anchorBounds.size.z));
                if (longest > biggestSize) { biggestSize = longest; biggest = m; }
            }

            if (!any)
            {
                family.Skipped("collider: shared mode asked for the union of every model, but none of "
                               + "them measured — the base keeps the collider it was fitted with");
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(basePath);
            if (contents == null)
            {
                family.Skipped("collider: could not re-open the base to fit the shared collider");
                return;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(contents);
                Collider col = anchor != null ? anchor.GetComponent<Collider>() : null;
                if (col == null)
                {
                    family.Skipped("collider: the base has no collider to share");
                    return;
                }

                float min = ColliderFitter.MinExtentMeters;
                var box = col as BoxCollider;
                var sphere = col as SphereCollider;
                var capsule = col as CapsuleCollider;

                if (box != null)
                {
                    box.size = new Vector3(Mathf.Max(min, union.size.x),
                                           Mathf.Max(min, union.size.y),
                                           Mathf.Max(min, union.size.z));
                    box.center = union.center;
                }
                else if (sphere != null)
                {
                    sphere.radius = Mathf.Max(min, 0.5f * Mathf.Max(union.size.x,
                                              Mathf.Max(union.size.y, union.size.z)));
                    sphere.center = union.center;
                }
                else if (capsule != null)
                {
                    int dir = Mathf.Clamp(capsule.direction, 0, 2);
                    float a, other;
                    OtherTwo(union.size, dir, out a, out other);
                    capsule.radius = Mathf.Max(min, 0.5f * Mathf.Max(a, other));
                    capsule.height = Mathf.Max(capsule.radius * 2f, Component(union.size, dir));
                    capsule.center = union.center;
                }
                else
                {
                    family.Skipped("collider: '" + col.GetType().Name
                                   + "' cannot be re-fitted to the family's union");
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(contents, basePath);
                AssetDatabase.ImportAsset(basePath, ImportAssetOptions.ForceSynchronousImport);

                family.Measured("collider: SHARED — one " + col.GetType().Name + " on the base, "
                    + FormatSize(union.size) + ", fitted to the union of all " + members.Count
                    + " models" + (biggest != null ? " (largest: " + NameOf(biggest.modelPath) + ")" : "")
                    + ". Every smaller member now has a collider bigger than its own geometry, and its "
                    + "GapFiller footprint with it — switch off 'Share one collider' if this family is "
                    + "not uniform.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ── Report ──────────────────────────────────────────────────────

        private static void ReportFamilySummary(ConversionResult family, string familyName,
                                                string baseStem, string basePath, List<Member> members,
                                                int built, bool shareCollider, ConversionPlan plan)
        {
            family.outputPath = basePath;

            int overrides = 0, children = 0;
            foreach (Member m in members)
            {
                if (string.IsNullOrEmpty(m.outputPath)) continue;
                if (m.swapPath == MeshSwapPath.PropertyOverride) overrides++; else children++;
            }

            family.Added("family '" + familyName + "': " + built + " variant"
                + (built == 1 ? "" : "s") + " of " + baseStem + " — " + overrides
                + " by mesh property override, " + children + " by added child object");

            if (built < members.Count)
            {
                family.Failed("family '" + familyName + "': " + (members.Count - built) + " of "
                              + members.Count + " members did not build — see their own lines above");
            }

            family.Skipped(
                "resourceName: ContentProcessor stamps '{gameId}/Props/" + plan.category
                + "/{name}' onto every prop, and on a VARIANT that stamp is a property override. "
                + "'Revert All' on a variant silently repoints its revenue at " + baseStem
                + "'s key and nothing errors — it looks like a tidy-up and it is a billing bug. "
                + "Revert individual overrides instead. The pre-upload check "
                + "'resource-name-address' catches it if it happens.");

            family.Skipped(
                "base: " + baseStem + " ships. A prefab variant holds a hard reference to its base, so "
                + "the base is a build dependency and cannot be excluded — it will appear in the prop "
                + "browser as an empty prop until ContentProcessor learns to skip leading-underscore "
                + "prefabs. SmartBundleGrouper will move it into the Shared bundle by itself, which is "
                + "the point: the shared hierarchy is stored once instead of " + built + " times.");

            if (!shareCollider)
            {
                family.Measured("collider: PER-VARIANT — each variant overrides size and centre only. "
                    + "The type and every behaviour field stay on the base, so the geometry stays "
                    + "honest and the feel stays shared.");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static bool NamesAreDistinct(List<Member> members, string baseName,
                                             ConversionResult family)
        {
            // OrdinalIgnoreCase, not Ordinal: two stems differing only by case
            // cannot live in one folder on macOS or Windows, and their preview
            // PNGs collide even where their addresses would not.
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var clashes = new List<string>();

            seen[baseName] = "(the family base)";

            foreach (Member m in members)
            {
                string existing;
                if (seen.TryGetValue(m.variantName, out existing))
                    clashes.Add("'" + m.variantName + "' from " + NameOf(m.modelPath) + " and " + existing);
                else
                    seen[m.variantName] = NameOf(m.modelPath);
            }

            if (clashes.Count == 0) return true;

            family.Failed("shared prop family: these members would ship under the same name — "
                + string.Join("; ", clashes.ToArray())
                + ". The Addressables address, the preview PNG and the resourceName used for revenue "
                + "attribution are all built from the bare file name, so a collision resolves "
                + "nondeterministically. Rename the models and run this again. Nothing was written.");
            return false;
        }

        /// <summary>
        /// Move every decision a throwaway result collected onto the real one,
        /// in order and with its kind intact — including Failed, which carries
        /// ok/error with it. Used where a stage has to be run before we know
        /// whether its report lines describe what will actually ship.
        /// </summary>
        private static void Replay(ConversionResult from, ConversionResult to)
        {
            if (from == null || to == null) return;

            foreach (Decision d in from.decisions)
            {
                switch (d.kind)
                {
                    case DecisionKind.Measured:  to.Measured(d.message);  break;
                    case DecisionKind.Guessed:   to.Guessed(d.message);   break;
                    case DecisionKind.Added:     to.Added(d.message);     break;
                    case DecisionKind.Extracted: to.Extracted(d.message); break;
                    case DecisionKind.Skipped:   to.Skipped(d.message);   break;
                    default:                     to.Failed(d.message);    break;
                }
            }
        }

        private static void ConvertMaterialsOn(string assetPath, ConversionResult r)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return;

            int opaque = 0, particle = 0, exotic = 0;
            var seen = new HashSet<int>();

            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in rend.sharedMaterials)
                {
                    if (mat == null || !seen.Add(mat.GetInstanceID())) continue;

                    if (DreamPark.EditorTools.MaterialConverter.IsParticleMaterial(mat))
                    {
                        if (DreamPark.EditorTools.MaterialConverter.HasExoticParticleFeature(mat)) { exotic++; continue; }
                        if (DreamPark.EditorTools.MaterialConverter.ConvertParticleMaterial(mat)) particle++;
                    }
                    else if (DreamPark.EditorTools.MaterialConverter.ConvertMaterial(mat))
                    {
                        opaque++;
                    }
                }
            }

            if (opaque + particle > 0)
                r.Measured("materials: " + opaque + " → UniversalShader, " + particle + " → Particles");
            if (exotic > 0)
                r.Skipped("materials: " + exotic + " skipped (exotic particle features)");
        }

        /// <summary>
        /// The member the size question is asked about: the first one that can
        /// actually be measured, so the dialog never opens on an empty model.
        /// </summary>
        private static Member Representative(List<Member> members)
        {
            foreach (Member m in members)
            {
                if (m.jig != null && AssetClassifier.LongestAxisMeters(m.jig) > 0f) return m;
            }
            foreach (Member m in members) if (m.jig != null) return m;
            return null;
        }

        /// <summary>
        /// Load an asset this run just wrote. A freshly written prefab can
        /// come back null on the first Load; ForceSynchronousImport is the
        /// lever. Returns null when even that fails — the alternative is
        /// instantiating nothing and saving thirty flattened copies.
        /// </summary>
        private static GameObject LoadWritten(string assetPath)
        {
            GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go != null) return go;

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        }

        /// <summary>
        /// Tell Unity that a field on a prefab INSTANCE changed. Field writes
        /// from script do not create a PropertyModification on their own — the
        /// Inspector and SerializedObject do that — and an unrecorded change can
        /// be reverted the next time the instance is reloaded.
        /// </summary>
        private static void RecordOverride(UnityEngine.Object target)
        {
            if (target == null) return;
            try
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                EditorUtility.SetDirty(target);
            }
            catch (Exception)
            {
                // Not part of a prefab instance (a hand-built hierarchy in a
                // test, say). Nothing to record; never fail a build over it.
            }
        }

        private static void RecordInManifest(string prefabPath, ConversionPlan plan,
                                             GameObject asset, string sourcePath)
        {
            try
            {
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                if (string.IsNullOrEmpty(guid)) return;

                ConvertReadyManifest manifest = ConvertReadyManifest.LoadOrCreate();
                if (manifest != null) manifest.Record(guid, prefabPath, plan, asset, sourcePath);
            }
            catch (Exception e)
            {
                // The prefab on disk is the deliverable; the manifest is how a
                // re-run becomes a diff. Losing the second must never lose the first.
                Debug.LogWarning("[Convert to DreamPark-Ready] manifest not updated for "
                                 + prefabPath + " — " + e.Message);
            }
        }

        private static bool HasComponentsBeyondStaticMesh(GameObject root, out string what)
        {
            what = null;
            var names = new List<string>();

            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;                 // a missing script
                if (c is Transform) continue;
                if (c is MeshFilter) continue;
                if (c is MeshRenderer) continue;

                string n = c.GetType().Name;
                if (!names.Contains(n)) names.Add(n);
            }

            if (names.Count == 0) return false;
            what = string.Join(" + ", names.ToArray());
            return true;
        }

        private static bool IsIdentityUnder(Transform root, Transform node, out string what)
        {
            what = null;
            if (node == root) return true;

            Matrix4x4 rel = root.worldToLocalMatrix * node.localToWorldMatrix;

            Vector4 column = rel.GetColumn(3);
            Vector3 offset = new Vector3(column.x, column.y, column.z);
            if (offset.sqrMagnitude > IdentityEpsilon * IdentityEpsilon) { what = "offset"; return false; }

            if (Quaternion.Angle(rel.rotation, Quaternion.identity) > 0.01f) { what = "rotated"; return false; }

            // ANY child scale disqualifies, uniform or not. A uniform one looks
            // harmless and is not, in two ways the report would never mention:
            //
            //  - When the family is left at authored size (a Plausible-band
            //    family, or plan.fitScale off) BuildVariant runs no fit at all,
            //    so the jig — which carried the model's 0.01 child scale — and
            //    the property-override variant — which puts the bare mesh on
            //    Visual at localScale 1 — differ by 100×.
            //  - Even with a fit, ScaleToFit measures mesh-local bounds and
            //    applies the factor to the node it was handed, so the jig ends
            //    up at factor × childScale × mesh and the variant at
            //    factor × mesh. The base's collider, the shareCollider union and
            //    BehaviorPackBuilder's mass and detection radius all come off
            //    the jigs, so all three are wrong by exactly childScale.
            //
            // The ModelChild path instantiates the whole model and keeps the
            // child transform, so jig and variant agree there. Rejecting is one
            // line and cannot drift; folding the scale into Visual would have to
            // be kept in step with every caller that poses a jig.
            Vector3 s = rel.lossyScale;
            if (Mathf.Abs(s.x - 1f) > IdentityEpsilon ||
                Mathf.Abs(s.y - 1f) > IdentityEpsilon ||
                Mathf.Abs(s.z - 1f) > IdentityEpsilon)
            {
                what = "scaled";
                return false;
            }

            return true;
        }

        private static PrefabScaler.Axis ToPrefabScalerAxis(PrefabScalerAxis a)
        {
            switch (a)
            {
                case PrefabScalerAxis.X: return PrefabScaler.Axis.X;
                case PrefabScalerAxis.Z: return PrefabScaler.Axis.Z;
                default:                 return PrefabScaler.Axis.Y;
            }
        }

        private static void ResetLocal(Transform t)
        {
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        private static int DominantAxis(Vector3 size)
        {
            if (size.x >= size.y && size.x >= size.z) return 0;
            if (size.y >= size.z) return 1;
            return 2;
        }

        /// <summary>The two extents that are NOT along <paramref name="axis"/>.</summary>
        private static void OtherTwo(Vector3 size, int axis, out float a, out float b)
        {
            switch (axis)
            {
                case 0:  a = size.y; b = size.z; break;
                case 1:  a = size.x; b = size.z; break;
                default: a = size.x; b = size.y; break;
            }
        }

        private static float Component(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        private static string AxisName(int axis)
        {
            return axis == 0 ? "X" : (axis == 1 ? "Y" : "Z");
        }

        private static string NameOf(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? "(unknown)" : Path.GetFileName(assetPath);
        }

        private static string FormatMeters(float m)
        {
            return ScaleInference.FormatMeters(m);
        }

        private static string FormatSize(Vector3 s)
        {
            return s.x.ToString("0.###", Inv) + " × " + s.y.ToString("0.###", Inv)
                 + " × " + s.z.ToString("0.###", Inv) + " m";
        }

        private static CultureInfo Inv { get { return CultureInfo.InvariantCulture; } }
    }
}
#endif
