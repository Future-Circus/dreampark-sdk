// ─────────────────────────────────────────────────────────────────────
//  PropPrefabEmitter.cs — Stage 5. The canonical hierarchy, and the only
//  place that writes a P_*.prefab.
//
//  WHY THIS IS ITS OWN FILE AND WHY EVERY TRACK GOES THROUGH IT
//
//  The mesh, texture and audio tracks each have an obvious "just parent the
//  thing under a root and save it" shortcut, and each of those shortcuts
//  produces a subtly different hierarchy. Three shapes means three sets of
//  bugs, and the bugs are not cosmetic:
//
//   • Anything that writes a transform's localRotation every frame — EasyBend,
//     Billboard — destroys the park's authored yaw if it sits on the ROOT.
//     LevelAnchor.Spawn instantiates, then writes localPosition/localRotation/
//     localScale on the spawned root. EasyBend.Awake has already cached the
//     PREFAB's rotation by then, and its first Update writes it straight back
//     over the park's. Every bendy prop in the park snaps to the same yaw on
//     frame one, and it reads as a park-data bug. Hence the Motion node.
//
//   • PropTemplate.TryGetColliderFootprint walks GetComponentsInChildren
//     <Collider>() and GapFiller/FloorCutout both consume the result. A
//     collider on a rotating node makes the floor footprint spin with the
//     player's head. Hence the Anchor node, which never rotates, and which
//     is why useColliderBounds = true "just works" instead of needing a
//     hand-maintained customFootprintMeters.
//
//   • PrefabScaler.ScaleToFit resets localScale to identity before measuring.
//     Point it at the root and every re-run wipes the scale a park author put
//     on a placed instance. Hence the fit scale living on Visual.
//
//  So: one shape, one emitter.
//
//      P_Name          PropTemplate (+ GameArea). NOTHING ELSE, EVER.
//      └── Anchor      collider(s). Never rotates.
//          └── Motion  owns localRotation per frame. OMITTED when nothing
//              │       animates — an empty transform per prop is not free.
//              └── Visual   renderer, fit scale, ground-pivot offset.
//
//  WHAT THIS FILE DELIBERATELY DOES NOT DO
//
//  It does not write PropTemplate.gameId or PropTemplate.resourceName, and it
//  does not touch Addressables. ContentProcessor stamps all three from the
//  asset's path ("{gameId}/Props/{category}/{name}") and its own comment
//  records why: "This is what lets the headset attribute revenue to the
//  individual attraction." Saving into the content folder IS the integration.
//  Hand-writing an address here is the address-vs-resourceName conflation
//  CLAUDE.md warns about, and it is a billing bug, not a naming one.
//
//  UNDO. Scene-object edits register undo. Creating the prefab ASSET does not
//  and cannot — Unity has no undo for AssetDatabase writes. A creator who
//  presses Cmd-Z gets their scene back, and the .prefab file stays on disk.
//  The report should say so; this file cannot.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class PropPrefabEmitter
    {
        /// Every prop prefab in the SDK is P_*. ContentProcessor derives the
        /// Addressables address from the file stem, so this prefix is part of
        /// the address and is not decoration.
        public const string RootPrefix = "P_";

        public const string AnchorNodeName = "Anchor";
        public const string MotionNodeName = "Motion";
        public const string VisualNodeName = "Visual";

        public const string PrefabsFolderName = "Prefabs";
        public const string PrefabExtension = ".prefab";

        /// Used when the caller hands us nothing usable to name the prop.
        /// Never null, never empty — an empty asset name throws inside
        /// AssetDatabase rather than returning an error.
        public const string FallbackPropName = "Prop";

        // ── Does this prop animate? ─────────────────────────────────────

        /// <summary>
        /// The single owner of "is there a Motion node". Both callers that
        /// matter — the Bendy pack and the texture track's billboard modes —
        /// have to agree with each other and with what Emit saved, or the
        /// behavior pack attaches EasyBend to a node that is not there.
        ///
        /// Shatter and Interactive do NOT get one: physics moves a Rigidbody
        /// on the root, it does not write a child's localRotation per frame.
        ///
        /// <paramref name="skinned"/> is the fork Bendy takes. A skinned prop
        /// gets a jiggle rig on its BONE CHAIN, not an EasyBend on a transform,
        /// so nothing writes a node's localRotation and a Motion node would be
        /// an empty transform per instance. The executor already branches this
        /// way (needsMotion = Bendy &amp;&amp; !skinned); this overload exists so
        /// that decision is made in ONE place instead of being re-derived here
        /// and then contradicted — the disagreement is what made every correct
        /// skinned-Bendy conversion end its report with a warning that the
        /// Motion node was missing.
        /// </summary>
        public static bool NeedsMotionNode(ConversionPlan plan, bool skinned)
        {
            if (plan == null) return false;

            // EasyBend writes transform.localRotation every Update, forever —
            // but only on the unskinned path. See the summary.
            if (plan.behavior == BehaviorPack.Bendy) return !skinned;

            // Billboard modes 2 and 3 do the same. Mode 1 (BillboardMode.None)
            // is a plain plane with a fixed facing and must not pay for a node.
            if (plan.plane != null && plan.plane.billboard != BillboardMode.None) return true;

            return false;
        }

        /// <summary>
        /// The unskinned answer. Callers that have not built a hierarchy yet —
        /// and therefore cannot know whether the source is skinned — use this.
        /// </summary>
        public static bool NeedsMotionNode(ConversionPlan plan)
        {
            return NeedsMotionNode(plan, false);
        }

        /// <summary>
        /// True when the prop's geometry is driven by bones. Read off the
        /// hierarchy rather than the plan, because "skinned" is a property of
        /// the source model and no plan field records it.
        /// </summary>
        public static bool IsSkinned(GameObject root)
        {
            return root != null && root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        // ── Build ───────────────────────────────────────────────────────

        /// <summary>
        /// Build the canonical hierarchy in memory. No components — Emit adds
        /// PropTemplate and GameArea, the behavior packs add theirs. The caller
        /// owns the returned object's lifetime: Emit saves it to disk but does
        /// not destroy or connect it.
        ///
        /// The scratch root lands in the active scene and its creation is
        /// registered with Undo, so dispose of it with DestroyWorkingRoot —
        /// a plain DestroyImmediate leaves a dead undo step behind.
        /// </summary>
        public static GameObject BuildHierarchy(string propName, bool needsMotionNode)
        {
            string rootName = string.IsNullOrEmpty(propName) ? FallbackPropName : propName;

            GameObject root = new GameObject(rootName);
            ResetLocal(root.transform);

            GameObject anchor = new GameObject(AnchorNodeName);
            Attach(anchor.transform, root.transform);

            Transform visualParent = anchor.transform;
            if (needsMotionNode)
            {
                GameObject motion = new GameObject(MotionNodeName);
                Attach(motion.transform, anchor.transform);
                visualParent = motion.transform;
            }

            GameObject visual = new GameObject(VisualNodeName);
            Attach(visual.transform, visualParent);

            // Registered as one created object: undoing removes the whole
            // subtree, not just the root, because the children were parented
            // before this call. (Skipped for preview-scene objects — see
            // RecordUndo.)
            RegisterCreated(root, "Convert to DreamPark-Ready");

            return root;
        }

        /// <summary>Convenience overload — asks the plan whether Motion is needed.</summary>
        public static GameObject BuildHierarchy(string propName, ConversionPlan plan)
        {
            return BuildHierarchy(propName, NeedsMotionNode(plan));
        }

        // ── Navigation ──────────────────────────────────────────────────
        //
        // Find by NAME and only among direct children. GetComponentInChildren
        // would happily return a node called "Anchor" belonging to an imported
        // model three levels down, and the collider would land inside the
        // artist's geometry instead of on the prop's own anchor.

        public static Transform FindAnchor(GameObject root)
        {
            return root == null ? null : root.transform.Find(AnchorNodeName);
        }

        public static Transform FindMotion(GameObject root)
        {
            Transform anchor = FindAnchor(root);
            return anchor == null ? null : anchor.Find(MotionNodeName);
        }

        public static Transform FindVisual(GameObject root)
        {
            Transform motion = FindMotion(root);
            if (motion != null)
            {
                Transform underMotion = motion.Find(VisualNodeName);
                if (underMotion != null) return underMotion;
            }

            Transform anchor = FindAnchor(root);
            return anchor == null ? null : anchor.Find(VisualNodeName);
        }

        /// <summary>
        /// Where the Visual node belongs right now: Motion if it exists,
        /// otherwise Anchor. Returns null when the hierarchy is not ours.
        /// </summary>
        public static Transform VisualParent(GameObject root)
        {
            Transform motion = FindMotion(root);
            if (motion != null) return motion;
            return FindAnchor(root);
        }

        /// <summary>
        /// Add or remove the Motion node on an existing hierarchy, moving the
        /// Visual (and anything else hanging off it) with it. This is what
        /// makes a re-run with a changed preset a diff rather than a second
        /// prefab — Bendy → Interactive has to be able to take the node away
        /// again, or a dead transform survives forever.
        /// </summary>
        public static Transform EnsureMotionNode(GameObject root, bool shouldExist, ConversionResult r)
        {
            Transform anchor = FindAnchor(root);
            if (anchor == null)
            {
                Report(r, DecisionKind.Failed,
                    "hierarchy: no '" + AnchorNodeName + "' node — cannot place the '" + MotionNodeName + "' node");
                return null;
            }

            Transform motion = anchor.Find(MotionNodeName);

            if (shouldExist)
            {
                if (motion != null) return motion;

                RecordUndo(root, "Convert to DreamPark-Ready");

                GameObject created = new GameObject(MotionNodeName);
                Attach(created.transform, anchor);

                // Everything that was under Anchor except the colliders' own
                // node moves under Motion. In practice that is the Visual.
                //
                // The collider exclusion is not cosmetic and it is not
                // theoretical: ColliderFitter.ApplyMesh puts its "Collision" /
                // "Collision_00" MeshCollider nodes directly under Anchor, and
                // a re-run that switches a preset to Bendy — or a Custom plan
                // that turns billboarding on — reaches this branch AFTER Stage
                // 4 has already run. Sweep those nodes under Motion and the
                // colliders end up on the transform EasyBend/Billboard writes
                // localRotation to every Update, so PropTemplate's
                // TryGetColliderFootprint (GetComponentsInChildren<Collider>)
                // hands GapFiller and FloorCutout a spinning footprint. That is
                // hazard 1 and hazard 2 in one move.
                var moving = new List<Transform>();
                for (int i = 0; i < anchor.childCount; i++)
                {
                    Transform child = anchor.GetChild(i);
                    if (child == created.transform) continue;
                    if (IsCollisionOnly(child)) continue;
                    moving.Add(child);
                }
                foreach (var child in moving) child.SetParent(created.transform, true);

                Report(r, DecisionKind.Added,
                    "hierarchy: added the '" + MotionNodeName + "' node — the component that rotates this prop "
                    + "each frame must own its own transform, or it overwrites the rotation the park authored");
                return created.transform;
            }

            if (motion == null) return null;

            RecordUndo(root, "Convert to DreamPark-Ready");

            var orphans = new List<Transform>();
            for (int i = 0; i < motion.childCount; i++) orphans.Add(motion.GetChild(i));
            foreach (var child in orphans) child.SetParent(anchor, true);

            // RegisterFullObjectHierarchyUndo above records the hierarchy's
            // STATE; it does not resurrect a GameObject that was destroyed
            // outside the undo system. Destroy this with DoDestroy in a scene
            // and the creator's Cmd-Z restores the parenting but not the node,
            // which is a half-undone prefab. Preview-scene and asset objects
            // have no undo at all, so they keep the plain destroy.
            DestroyNode(motion.gameObject);

            Report(r, DecisionKind.Skipped,
                "hierarchy: removed the '" + MotionNodeName + "' node — nothing on this prop animates any more");
            return null;
        }

        /// <summary>
        /// A collision-only subtree: colliders somewhere in it, renderers
        /// nowhere. That is ColliderFitter's generated "Collision" node and it
        /// is also what a hand-authored proxy looks like — both belong on the
        /// Anchor, which never rotates.
        ///
        /// Deliberately NOT "any child carrying a Collider": the Visual routinely
        /// carries colliders from the source model, and the Visual MUST move
        /// under Motion or the thing that rotates rotates nothing.
        ///
        /// There is no mirror of this in the Motion-removal branch above, and
        /// that is correct: moving children the other way lands them on the
        /// Anchor, which is where a collider wanted to be in the first place.
        /// </summary>
        static bool IsCollisionOnly(Transform child)
        {
            if (child == null) return false;
            if (ColliderFitter.IsGeneratedCollisionChild(child)) return true;
            if (child.GetComponentInChildren<Renderer>(true) != null) return false;
            return child.GetComponentInChildren<Collider>(true) != null;
        }

        // ── Naming ──────────────────────────────────────────────────────

        /// <summary>
        /// "OldLantern" → "P_OldLantern". "P_OldLantern" → "P_OldLantern".
        ///
        /// The idempotence matters: re-converting an already-converted prefab
        /// is a supported flow, and without the check it produces P_P_Coin,
        /// then P_P_P_Coin, each with its own Addressables address and its own
        /// revenue key.
        /// </summary>
        public static string PropAssetName(string propName)
        {
            // Guard BEFORE sanitizing, not after. SanitizeAssetName never
            // returns empty — it substitutes its own fallback, which is written
            // for material names — so a post-check is dead code and an empty
            // propName silently ships as P_Asset. That stem becomes the
            // Addressables address and the revenue key, so the wrong fallback
            // is a billing artefact, not a cosmetic one.
            if (string.IsNullOrEmpty(propName)) return RootPrefix + FallbackPropName;

            string cleaned = AssetClassifier.SanitizeAssetName(propName, FallbackPropName);

            // Leading underscores are the shared-prop base convention
            // (_P_Family_Base). Strip ours off the front before testing so
            // "_P_Coin_Base" is not re-prefixed into "P__P_Coin_Base".
            string trimmed = cleaned.TrimStart('_');
            if (trimmed.StartsWith(RootPrefix, StringComparison.Ordinal)) return cleaned;

            return RootPrefix + cleaned;
        }

        /// <summary>
        /// The folder this prop belongs in: r.kitFolder when the executor
        /// prepared one, otherwise the source's parent. Pseudo-paths from
        /// shared-prop / batch callers ("(shared prop)") fall back to the
        /// placeholder game folder — never a leftover Materials or Prefabs sibling.
        /// </summary>
        public static string DestinationFolder(ConversionResult r)
        {
            if (r != null && !string.IsNullOrEmpty(r.kitFolder))
                return r.kitFolder.Replace('\\', '/').TrimEnd('/');

            string path = r != null ? r.sourcePath : null;
            if (!string.IsNullOrEmpty(path) && path[0] != '(')
            {
                string dir = (Path.GetDirectoryName(path) ?? string.Empty).Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir)) return dir;
            }

            string game = ContentFolders.Sanitize(ContentFolders.GameFolderName());
            if (string.IsNullOrEmpty(game)
                || string.Equals(game, "Materials", StringComparison.OrdinalIgnoreCase)
                || string.Equals(game, PrefabsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                game = ContentFolders.PlaceholderName;
            }

            return ContentFolders.Root + "/" + game;
        }

        /// <summary>
        /// A free path near <paramref name="desiredPath"/>, and whether we had
        /// to move off it.
        ///
        /// AssetDatabase.GenerateUniqueAssetPath is the authority on "is this
        /// taken", but its own suffix is " 1" — a SPACE. That stem goes
        /// verbatim into the Addressables address and into resourceName, so we
        /// propose "_2" candidates and let GenerateUniqueAssetPath adjudicate
        /// each one.
        /// </summary>
        public static string UniqueAssetPath(string desiredPath, out bool uniquified)
        {
            uniquified = false;
            if (string.IsNullOrEmpty(desiredPath)) return desiredPath;

            string dir = (Path.GetDirectoryName(desiredPath) ?? string.Empty).Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(desiredPath);
            string ext = Path.GetExtension(desiredPath);

            string candidate = desiredPath;
            for (int n = 2; n < 1000; n++)
            {
                if (string.Equals(AssetDatabase.GenerateUniqueAssetPath(candidate), candidate, StringComparison.Ordinal)
                    && !PrefabStemTaken(candidate))
                {
                    uniquified = !string.Equals(candidate, desiredPath, StringComparison.Ordinal);
                    return candidate;
                }
                candidate = dir + "/" + stem + "_" + n.ToString(CultureInfo.InvariantCulture) + ext;
            }

            // 998 collisions is not a naming problem any more, but we still
            // must not clobber. Take Unity's answer, space and all.
            string last = AssetDatabase.GenerateUniqueAssetPath(desiredPath);
            uniquified = !string.Equals(last, desiredPath, StringComparison.Ordinal);
            return last;
        }

        /// <summary>
        /// Is any OTHER prefab in the project already using this file stem?
        ///
        /// A free path is not a free NAME. Everything downstream keys off the
        /// bare stem, not the folder: ContentProcessor's address, the preview
        /// PNG, the PreviewMetadataStore key, and GameArea.resourceName — the
        /// revenue-attribution key. While every prop landed in one shared
        /// {content}/Prefabs folder the two questions had the same answer, so
        /// GenerateUniqueAssetPath alone was enough. With one kit folder per
        /// asset they came apart: Models/Coin.fbx and Props/Coin.fbx both
        /// convert cleanly to their own P_Coin/P_Coin.prefab, neither
        /// uniquifies, and the run reports that nothing was overwritten —
        /// then DuplicateNamesCheck (Blocking) stops the upload with no clue
        /// as to which run caused it.
        /// </summary>
        static bool PrefabStemTaken(string candidatePath)
        {
            string stem = Path.GetFileNameWithoutExtension(candidatePath);
            if (string.IsNullOrEmpty(stem)) return false;

            // SCOPE IT TO THIS CONTENT PACKAGE. The collision that matters is
            // per-gameId: the address is "{gameId}/Props/…", the preview PNG
            // lives in that package's Previews folder, and resourceName is read
            // within it. A P_Rock in the Sample package, in the SDK's own
            // vendored assets, or in another creator's package cannot collide
            // with ours — and searching the whole project means the FIRST thing
            // WarnIfKitCannotShip tells a creator to do (move the source out of
            // ThirdPartyLocal and convert again) yields P_Rock_2, because the
            // build-excluded copy they were told to abandon still holds the
            // name. That suffix is permanent in the address and the revenue key.
            string own = ContentFolders.FolderOfAsset(candidatePath);
            string[] searchIn = string.IsNullOrEmpty(own)
                ? null
                : new string[] { ContentFolders.Root + "/" + own };

            string[] guids = searchIn == null
                ? AssetDatabase.FindAssets("\"" + stem + "\" t:Prefab")
                : AssetDatabase.FindAssets("\"" + stem + "\" t:Prefab", searchIn);
            if (guids == null) return false;

            for (int i = 0; i < guids.Length; i++)
            {
                string other = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(other)) continue;
                if (string.Equals(other, candidatePath, StringComparison.Ordinal)) continue;

                // t:Prefab also matches MODEL prefabs — the FBX itself. A source
                // already named P_Rock.fbx would otherwise be reported as
                // colliding with the prefab we are about to make from it.
                if (!other.EndsWith(PrefabExtension, StringComparison.OrdinalIgnoreCase)) continue;

                // Never ships, so it cannot collide with anything that does.
                if (other.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // The quoted filter is fuzzy, not exact — re-check the stem.
                if (string.Equals(Path.GetFileNameWithoutExtension(other), stem, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ── Emit ────────────────────────────────────────────────────────

        /// <summary>
        /// Stamp the root with PropTemplate + GameArea and save the hierarchy
        /// as {content}/Prefabs/P_{Name}.prefab. Returns the saved asset path,
        /// or null when nothing was written — every failure path appends to
        /// <paramref name="r"/> first. Never throws.
        ///
        /// <paramref name="root"/> must be a scene or preview-scene object.
        /// This does not connect it to the new asset; the caller decides
        /// whether the working object stays or is destroyed.
        ///
        /// Infers whether a Motion node was wanted from the hierarchy it is
        /// handed. Callers that already made that decision should pass it in
        /// through the overload below rather than let this guess.
        /// </summary>
        public static string Emit(GameObject root, string propName, ConversionPlan plan, ConversionResult r)
        {
            return Emit(root, propName, plan, NeedsMotionNode(plan, IsSkinned(root)), r);
        }

        /// <summary>
        /// As above, with the caller's own Motion-node decision. Only the
        /// structure REPORT uses it — the node itself is built by
        /// BuildHierarchy/EnsureMotionNode long before this — but reporting
        /// against a re-derived answer is how a correct skinned Bendy prop
        /// ended up being told its Motion node was missing.
        /// </summary>
        public static string Emit(GameObject root, string propName, ConversionPlan plan,
                                  bool needsMotionNode, ConversionResult r)
        {
            if (root == null)
            {
                Report(r, DecisionKind.Failed, "prefab: nothing to save — the prop root is null");
                return null;
            }
            if (plan == null)
            {
                Report(r, DecisionKind.Failed, "prefab: no ConversionPlan");
                return null;
            }
            if (!plan.emitPropPrefab)
            {
                Report(r, DecisionKind.Skipped, "prefab: not written — emitPropPrefab is off");
                return null;
            }
            if (EditorUtility.IsPersistent(root))
            {
                // SaveAsPrefabAsset on an object that IS an asset throws.
                // Callers editing an existing prefab must go through
                // PrefabUtility.LoadPrefabContents and hand us that root.
                Report(r, DecisionKind.Failed,
                    "prefab: '" + root.name + "' is already an asset — load it with "
                    + "PrefabUtility.LoadPrefabContents and pass the returned root");
                return null;
            }

            RecordUndo(root, "Convert to DreamPark-Ready");

            NormalizeRoot(root, r);
            WarnAboutStrayRootComponents(root, r);
            StampRoot(root, plan, r);
            ReportStructure(root, needsMotionNode, r);

            string folder = DestinationFolder(r);
            if (!AssetClassifier.EnsureFolder(folder) || !AssetClassifier.CommitFolder(folder))
            {
                Report(r, DecisionKind.Failed, "prefab: could not create the folder " + folder);
                return null;
            }

            string desiredPath = folder + "/" + PropAssetName(propName) + PrefabExtension;
            bool uniquified;
            string path = UniqueAssetPath(desiredPath, out uniquified);
            string stem = Path.GetFileNameWithoutExtension(path);

            if (uniquified)
            {
                Report(r, DecisionKind.Added, string.Format(
                    "prefab: '{0}' already exists — saved as '{1}' instead. Nothing was overwritten; "
                    + "delete or rename the old one if this was meant to replace it.",
                    Path.GetFileNameWithoutExtension(desiredPath), stem));
            }

            if (!string.Equals(root.name, stem, StringComparison.Ordinal)) root.name = stem;

            bool saved;
            GameObject asset = SavePrefab(root, path, out saved);
            if (!saved || asset == null)
            {
                AssetClassifier.CommitFolder(folder);
                asset = SavePrefab(root, path, out saved);
            }
            if (!saved || asset == null)
            {
                Report(r, DecisionKind.Failed, "prefab: Unity refused to save " + path);
                return null;
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            r.outputPath = path;
            // Do not resurrect a run that already failed a stage; Failed sets
            // both error and ok, and this must not paper over it.
            if (string.IsNullOrEmpty(r.error)) r.ok = true;

            Report(r, DecisionKind.Added, "prefab: " + stem + " → " + path);

            RecordInManifest(path, plan, asset, r);
            return path;
        }

        static GameObject SavePrefab(GameObject root, string path, out bool saved)
        {
            saved = false;
            try
            {
                return PrefabUtility.SaveAsPrefabAsset(root, path, out saved);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamPark] SaveAsPrefabAsset " + path + " — " + e.Message);
                saved = false;
                return null;
            }
        }

        // ── Root contract ───────────────────────────────────────────────

        /// <summary>
        /// The root is a placement contract and nothing else. LevelAnchor.Spawn
        /// overwrites localPosition/localRotation/localScale on the spawned root
        /// anyway, so anything we leave there is either ignored at runtime or —
        /// in the case of scale — actively misleading, because PropTemplate's
        /// footprint and SurfaceHeight numbers are read in the inspector against
        /// this transform.
        /// </summary>
        private static void NormalizeRoot(GameObject root, ConversionResult r)
        {
            Transform t = root.transform;
            bool scaled = t.localScale != Vector3.one;
            bool moved = t.localPosition != Vector3.zero;
            bool rotated = t.localRotation != Quaternion.identity;

            if (!scaled && !moved && !rotated) return;

            ResetLocal(t);

            if (scaled)
            {
                Report(r, DecisionKind.Measured,
                    "prop root: localScale reset to (1,1,1) — the fit scale belongs on the '"
                    + VisualNodeName + "' node, and a scaled root makes every footprint number "
                    + "in the PropTemplate inspector unreadable");
            }
            if (moved || rotated)
            {
                Report(r, DecisionKind.Measured,
                    "prop root: transform reset to identity — the park loader writes position and "
                    + "rotation on the spawned root, so anything authored here is overwritten at spawn");
            }
        }

        /// <summary>
        /// Report-only. Moving components between GameObjects means copying
        /// serialized state field by field, and getting that wrong on a
        /// creator's hand-edited prefab is worse than telling them about it.
        ///
        /// The whitelist is not "PropTemplate and GameArea". Stage 6 puts
        /// Rigidbody and Interactable on the ROOT on purpose — Unity routes
        /// collision callbacks to the nearest ancestor Rigidbody, so a compound
        /// collider on the Anchor still reaches handlers up here, and
        /// BehaviorPackBuilder's header records that as the resolved answer to
        /// the spec's "where does the Rigidbody go". Warning about them would
        /// tell a creator their correct Interactive prop is broken every time
        /// they re-run the converter on an already-converted prefab, which is a
        /// supported flow. What is left is the real hazard: per-frame rotation
        /// and colliders, which belong on Motion and Anchor respectively.
        /// </summary>
        private static void WarnAboutStrayRootComponents(GameObject root, ConversionResult r)
        {
            var strays = new List<string>();
            int missing = 0;

            foreach (var c in root.GetComponents<Component>())
            {
                if (c == null)
                {
                    // A null entry from GetComponents IS the missing-script
                    // reference — the type cannot be resolved, so there is no
                    // name to report, only a count.
                    missing++;
                    continue;
                }
                if (c is Transform) continue;
                if (c is PropTemplate) continue;
                if (c is GameArea) continue;
                if (c is Rigidbody) continue;       // Stage 6, deliberately on the root
                if (c is Interactable) continue;    // ditto — see the summary
                strays.Add(c.GetType().Name);
            }

            if (strays.Count > 0)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "prop root carries {0} — anything that writes this transform's rotation each frame belongs "
                    + "on '{1}', and colliders belong on '{2}'. Left in place, but check it: a component that "
                    + "poses the root fights the park loader for that transform.",
                    string.Join(", ", strays.ToArray()), MotionNodeName, AnchorNodeName));
            }

            if (missing > 0)
            {
                // Not cosmetic and not the creator's cosmetic problem to defer:
                // an authoring-only script that rides a content prefab into an
                // app that never compiled it lands as exactly this, and it has
                // already happened here — E_Arch, E_FlipGate and E_Pylon
                // shipped with InteractiveInflatableGenerator missing. Emitting
                // a prop with one is shipping it again, so this fails the asset
                // rather than adding a line nobody reads. The prefab is still
                // written; the report is what stops the upload.
                Report(r, DecisionKind.Failed, missing + (missing == 1 ? " missing script" : " missing scripts")
                    + " on the prop root — a broken script reference on a prefab root ships into the app as a "
                    + "missing component. Remove it (Inspector → the 'Missing (Mono Script)' entry → Remove "
                    + "Component) and convert again.");
            }
        }

        private static void StampRoot(GameObject root, ConversionPlan plan, ConversionResult r)
        {
            // GameArea explicitly and FIRST. PropTemplate's [RequireComponent]
            // only auto-adds through the editor's AddComponent path when the
            // object is in a scene context; adding it via script on a preview-
            // scene root does not reliably bring it along, and a PropTemplate
            // without a GameArea is a prop the player rig never switches for.
            bool hadArea = root.GetComponent<GameArea>() != null;
            GameArea area = Componentizer.DoComponent<GameArea>(root, true);
            if (!hadArea && area != null)
            {
                // PropTemplate.EnsureGameArea uses -1 when it creates one at
                // runtime, and GameArea.Enter only takes over on a STRICTLY
                // higher priority. Leave it at the field default of 0 and a
                // prop dropped inside an attraction ties with the attraction's
                // own zone instead of losing to it.
                area.priority = -1;
                Report(r, DecisionKind.Added, "GameArea (priority -1, so a containing level or attraction always wins)");
            }

            bool hadTemplate = root.GetComponent<PropTemplate>() != null;
            PropTemplate prop = Componentizer.DoComponent<PropTemplate>(root, true);
            if (prop == null)
            {
                Report(r, DecisionKind.Failed, "prefab: could not add PropTemplate to '" + root.name + "'");
                return;
            }

            prop.category = plan.category;
            prop.affectsGapFiller = plan.affectsGapFiller;

            // useColliderBounds is derived, not copied: with no collider there
            // is nothing to derive a footprint from, and TryGetWorldFootprint
            // silently falls through to customFootprintMeters — which the
            // inspector HIDES while useColliderBounds is true.
            bool hasCollider = plan.collider != ColliderChoice.None;
            prop.useColliderBounds = hasCollider;
            SyncManualFootprintFlag(prop);

            // gameId and resourceName are NOT set here, deliberately.
            // ContentProcessor stamps both from the asset path, and
            // resourceName is the headset's revenue-attribution key.

            Report(r, DecisionKind.Added, string.Format(
                "PropTemplate — category {0}, affectsGapFiller {1}, useColliderBounds {2}{3}",
                plan.category, plan.affectsGapFiller ? "on" : "off", hasCollider ? "on" : "off",
                hasCollider ? "" : " (no collider, so the footprint comes from customFootprintMeters)"));

            if (hadTemplate)
            {
                Report(r, DecisionKind.Skipped,
                    "PropTemplate was already present — settings updated in place rather than duplicated");
            }
        }

        /// <summary>
        /// PropTemplate keeps a private _isManualFootprint mirror of
        /// !useColliderBounds, and only OnValidate refreshes it. OnValidate does
        /// not run when a field is assigned from an editor script, so without
        /// this the [ShowIf] on customFootprintMeters stays stale: the creator
        /// turns off collider bounds and the field they now need is invisible.
        /// </summary>
        private static void SyncManualFootprintFlag(PropTemplate prop)
        {
            try
            {
                var so = new SerializedObject(prop);
                SerializedProperty p = so.FindProperty("_isManualFootprint");
                if (p == null) return;                    // renamed upstream — nothing to sync
                p.boolValue = !prop.useColliderBounds;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            catch (Exception)
            {
                // A private serialized mirror is a nice-to-have. Never fail an
                // emit over it.
            }
        }

        /// <summary>
        /// <paramref name="wantsMotion"/> is the CALLER's decision, not a
        /// re-derivation. See the Emit overload.
        /// </summary>
        private static void ReportStructure(GameObject root, bool wantsMotion, ConversionResult r)
        {
            Transform anchor = FindAnchor(root);
            Transform motion = FindMotion(root);
            Transform visual = FindVisual(root);

            if (anchor == null)
            {
                Report(r, DecisionKind.Skipped,
                    "hierarchy: no '" + AnchorNodeName + "' node — the collider and the footprint it feeds "
                    + "will come from wherever the geometry happens to sit");
            }
            if (visual == null)
            {
                Report(r, DecisionKind.Skipped,
                    "hierarchy: no '" + VisualNodeName + "' node — nothing to carry the fit scale or the ground offset");
            }

            if (wantsMotion && motion == null)
            {
                Report(r, DecisionKind.Skipped,
                    "hierarchy: this preset animates but there is no '" + MotionNodeName + "' node — the rotating "
                    + "component will have to share a transform, and the park's authored rotation loses");
            }
            else if (!wantsMotion && motion != null)
            {
                Report(r, DecisionKind.Skipped,
                    "hierarchy: a '" + MotionNodeName + "' node exists but nothing on this prop animates — harmless, "
                    + "but it is one extra transform per instance");
            }
        }

        // ── Manifest ────────────────────────────────────────────────────

        /// <summary>
        /// Recording the plan is what makes a second conversion a diff instead
        /// of a second prefab. A manifest failure must never take the emit with
        /// it — the prefab on disk is the deliverable.
        /// </summary>
        private static void RecordInManifest(string prefabPath, ConversionPlan plan, GameObject asset, ConversionResult r)
        {
            try
            {
                string guid = AssetDatabase.AssetPathToGUID(prefabPath);
                if (string.IsNullOrEmpty(guid))
                {
                    Report(r, DecisionKind.Skipped,
                        "manifest: " + prefabPath + " has no GUID yet — re-running the converter will not see it as a re-run");
                    return;
                }

                ConvertReadyManifest manifest = ConvertReadyManifest.LoadOrCreate();
                if (manifest == null) return;

                manifest.Record(guid, prefabPath, plan, asset, r != null ? r.sourcePath : null);
            }
            catch (Exception e)
            {
                Report(r, DecisionKind.Skipped, "manifest: not updated — " + e.Message);
            }
        }

        // ── Paths ───────────────────────────────────────────────────────

        /// <summary>
        /// "Anchor/Motion/Visual" for a node under <paramref name="root"/>, or
        /// empty when it is not a descendant. A serialized Transform reference
        /// cannot survive outside the prefab it lives in, so anything that
        /// records a node records this string instead.
        /// </summary>
        public static string RelativePath(Transform root, Transform node)
        {
            if (root == null || node == null || node == root) return string.Empty;

            var sb = new StringBuilder(node.name);
            Transform t = node.parent;
            while (t != null && t != root)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            return t == root ? sb.ToString() : string.Empty;
        }

        /// <summary>
        /// Resolve a path produced by RelativePath. Null when the path is empty
        /// or no longer resolves.
        ///
        /// EMPTY MEANS "THAT NODE DOES NOT EXIST", not "the root". That is the
        /// meaning RelativePath writes (it returns empty for a non-descendant
        /// and for the root itself) and the meaning the manifest documents for
        /// motionPath, which is legitimately empty on every prop that does not
        /// animate. Returning root.transform here instead handed a re-bake the
        /// PROP ROOT for exactly those props, so the behaviour pack attached
        /// EasyBend/Billboard to the root — the park's authored yaw gone on
        /// frame one, silently, because the caller's null check passed.
        /// </summary>
        public static Transform FindByPath(GameObject root, string relativePath)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(relativePath)) return null;
            return root.transform.Find(relativePath);
        }

        // ── Internals ───────────────────────────────────────────────────

        private static void Attach(Transform child, Transform parent)
        {
            child.SetParent(parent, false);
            ResetLocal(child);
        }

        private static void ResetLocal(Transform t)
        {
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        private static void Report(ConversionResult r, DecisionKind kind, string message)
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

        /// <summary>
        /// Undo, but only where undo exists. PrefabUtility.LoadPrefabContents
        /// hands back objects in an isolated PREVIEW scene; registering undo
        /// against those pollutes the creator's undo stack with entries
        /// pointing at objects UnloadPrefabContents has already destroyed.
        /// </summary>
        private static void RecordUndo(GameObject go, string label)
        {
            if (go == null) return;
            if (EditorUtility.IsPersistent(go)) return;
            if (EditorSceneManager.IsPreviewSceneObject(go)) return;

            Undo.RegisterFullObjectHierarchyUndo(go, label);
        }

        private static void RegisterCreated(GameObject go, string label)
        {
            if (go == null) return;
            if (EditorUtility.IsPersistent(go)) return;
            if (EditorSceneManager.IsPreviewSceneObject(go)) return;

            Undo.RegisterCreatedObjectUndo(go, label);
        }

        /// <summary>
        /// Destroy a node we created, through undo where undo exists. Mirrors
        /// RegisterCreated exactly, so the two cancel out on Cmd-Z.
        /// </summary>
        private static void DestroyNode(GameObject go)
        {
            if (go == null) return;

            if (EditorUtility.IsPersistent(go) || EditorSceneManager.IsPreviewSceneObject(go))
            {
                Componentizer.DoDestroy(go);
                return;
            }

            Undo.DestroyObjectImmediate(go);
        }

        /// <summary>
        /// Dispose of a working root from BuildHierarchy. Call this instead of
        /// DestroyImmediate: BuildHierarchy registered the creation with Undo
        /// for scene objects, and destroying one outside the undo system leaves
        /// the creator a "Convert to DreamPark-Ready" undo step that does
        /// nothing — while the report window tells them Cmd-Z undoes the run.
        ///
        /// Safe on a preview-scene root and a no-op on a persistent one, so
        /// every track can call it unconditionally in its finally block.
        /// </summary>
        public static void DestroyWorkingRoot(GameObject root)
        {
            if (root == null) return;
            if (EditorUtility.IsPersistent(root)) return;   // that is an asset, not our scratch object

            DestroyNode(root);
        }
    }
}
#endif
