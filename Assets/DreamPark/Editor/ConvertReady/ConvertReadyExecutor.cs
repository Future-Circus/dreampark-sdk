// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyExecutor.cs — the one pipeline
//
//  Interactive, Bendy, Shatterable and Custom all come through here. There is
//  no second entry point and there must never be one: the moment a preset gets
//  its own executor, its scale inference starts drifting from everyone else's
//  and nobody notices for four months.
//
//  STAGE ORDER IS LOAD-BEARING AND NOT OBVIOUS
//    1  materials   — before anything else, so a failed extraction aborts the
//                     asset before we start writing prefabs for it
//    2  scale       — before pivot, because grounding depends on final size
//    3a ground      — before collider, because the collider is fitted to the
//                     posed visual
//    3b up-axis     — with 3a; both are confirmed by the creator first
//    4  collider    — before behavior, because Bendy needs the fitted radius
//                     and Interactive needs the measured volume for mass
//    5  emit        — the prefab asset
//    6  behavior    — inside the prefab contents
//    7  report
//
//  ON UNDO — READ THIS BEFORE CHANGING THE REPORT WINDOW
//
//  Unity's Undo system covers SCENE objects. It does NOT cover asset creation:
//  AssetDatabase.CreateAsset / PrefabUtility.SaveAsPrefabAsset are not undoable
//  and never have been. An earlier draft of the report window promised "⌘Z
//  undoes this entire run, including the created prefab assets", which is a
//  comfortable thing to say and completely false — the prefabs survive, and the
//  creator is left deleting them by hand while believing they already reverted.
//
//  So this class TRACKS every asset path it creates, and Revert() deletes them
//  explicitly. Undo is still used for the scene-side work; the two together are
//  what "undo the run" actually means here.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using DreamPark.EditorTools;

namespace DreamPark.ConvertReady
{
    public static class ConvertReadyExecutor
    {
        public const string UndoGroupName = "Convert to DreamPark-Ready";

        // Node names for the canonical hierarchy. Referenced by every builder,
        // so they live in exactly one place.
        public const string AnchorName = "Anchor";
        public const string MotionName = "Motion";
        public const string VisualName = "Visual";

        /// <summary>
        /// Everything this run wrote to disk, newest last. The report window
        /// uses it to offer a real revert — see the undo note at the top.
        /// </summary>
        public static List<string> LastRunCreatedAssets { get { return lastRunCreated; } }
        private static List<string> lastRunCreated = new List<string>();

        // ── Entry ───────────────────────────────────────────────────────

        public static ConversionReport Run(IList<string> assetPaths, ConversionPlan plan)
        {
            var report = new ConversionReport();
            lastRunCreated = new List<string>();

            if (assetPaths == null || assetPaths.Count == 0 || plan == null) return report;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoGroupName);

            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    // Shared Prop is a different shape: one base plus N variants
                    // from N models, so it owns the whole selection rather than
                    // running per-asset.
                    if (plan.shared.enabled && plan.track == ConvertTrack.Mesh)
                    {
                        RunSharedFamily(assetPaths, plan, report);
                    }
                    else
                    {
                        for (int i = 0; i < assetPaths.Count; i++)
                        {
                            string path = assetPaths[i];
                            EditorUtility.DisplayProgressBar(UndoGroupName,
                                Path.GetFileName(path), (float)i / assetPaths.Count);
                            report.results.Add(RunOne(path, plan));
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    EditorUtility.ClearProgressBar();
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                // A throw here means a bug in the converter, not in the asset.
                // Surface it as a failed result rather than letting it escape
                // into the menu handler where it becomes a bare console error
                // with no indication of which asset was being processed.
                var r = new ConversionResult { sourcePath = "(run)", ok = false };
                r.Failed("converter error: " + e.Message);
                report.results.Add(r);
                Debug.LogException(e);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            return report;
        }

        // ── Per-asset ───────────────────────────────────────────────────

        private static ConversionResult RunOne(string assetPath, ConversionPlan plan)
        {
            var r = new ConversionResult { sourcePath = assetPath, ok = true };

            ConvertTrack track = AssetClassifier.Classify(assetPath);
            if (track == ConvertTrack.Unsupported)
            {
                r.Failed("not a model, texture or audio clip");
                return r;
            }

            switch (track)
            {
                case ConvertTrack.Texture:
                    r.outputPath = TexturePlaneBuilder.Build(assetPath, plan, r);
                    Track(r.outputPath);
                    return r;

                case ConvertTrack.Audio:
                    r.outputPath = AudioEmitterBuilder.Build(assetPath, plan, r);
                    Track(r.outputPath);
                    return r;

                default:
                    return RunMesh(assetPath, plan, r);
            }
        }

        private static ConversionResult RunMesh(string assetPath, ConversionPlan plan, ConversionResult r)
        {
            string propName = AssetClassifier.SanitizeAssetName(
                Path.GetFileNameWithoutExtension(assetPath));

            // ── Stage 1: materials ──
            if (plan.convertMaterials)
            {
                if (plan.extractEmbeddedMaterials && AssetClassifier.HasEmbeddedMaterials(assetPath))
                {
                    string dest = AssetClassifier.DefaultMaterialsFolder(assetPath);
                    int extracted;
                    if (!AssetClassifier.ExtractEmbeddedMaterials(assetPath, dest, r, out extracted))
                    {
                        // Still embedded means still read-only, which means the
                        // material conversion below would silently do nothing and
                        // we would emit a prefab that looks converted and is not.
                        // Stop here — a failed asset with a reason beats a wrong
                        // one without.
                        r.Failed("materials are still embedded and could not be extracted — "
                                 + "nothing written for this asset");
                        return r;
                    }
                }
                ConvertMaterialsOn(assetPath, r);
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (source == null)
            {
                r.Failed("could not load '" + assetPath + "' as a GameObject");
                return r;
            }

            // Build the canonical hierarchy in memory. The Motion node exists
            // only when something will own rotation every frame — see hazard 1.
            bool skinned = AssetClassifier.IsSkinned(source);
            bool needsMotion = plan.behavior == BehaviorPack.Bendy && !skinned;

            GameObject root = PropPrefabEmitter.BuildHierarchy(propName, needsMotion);
            if (root == null)
            {
                r.Failed("could not build the prop hierarchy");
                return r;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(root);
                Transform visualParent = PropPrefabEmitter.FindVisual(root);
                if (anchor == null || visualParent == null)
                {
                    r.Failed("hierarchy is malformed — expected " + AnchorName + "/…/" + VisualName);
                    return r;
                }

                // Instantiate the model UNDER Visual rather than making Visual
                // the model: Visual owns the fit scale and the ground offset,
                // and PrefabScaler resets localScale to identity to measure. If
                // the model were Visual, a re-run would fight the model's own
                // authored transform.
                GameObject modelInstance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                if (modelInstance == null)
                {
                    r.Failed("could not instantiate the source model");
                    return r;
                }
                modelInstance.transform.SetParent(visualParent, false);
                modelInstance.transform.localPosition = Vector3.zero;
                modelInstance.transform.localRotation = Quaternion.identity;
                modelInstance.transform.localScale = Vector3.one;

                GameObject visual = visualParent.gameObject;

                // ── Stage 2 + 3: size and orientation, confirmed together ──
                if (!ResolveSizeAndOrientation(assetPath, visual, source, plan, r))
                {
                    // Cancelled. Write nothing at all — falling through to a
                    // default here is exactly how a tool loses trust.
                    UnityEngine.Object.DestroyImmediate(root);
                    r.Skipped("cancelled at the size/orientation step — nothing written");
                    r.ok = true;
                    return r;
                }

                if (plan.groundPivot)
                {
                    OrientationFitter.GroundPivot(visual, r);
                }

                // ── Stage 4: collider on the ANCHOR, never on a rotating node ──
                ColliderFitter.FitResult fit;
                ColliderFitter.Fit(anchor.gameObject, visual, plan, r, out fit);

                // ── Stage 5: emit ──
                string outPath = PropPrefabEmitter.Emit(root, propName, plan, r);
                if (string.IsNullOrEmpty(outPath))
                {
                    r.Failed("prefab was not written");
                    return r;
                }
                r.outputPath = outPath;
                Track(outPath);

                // ── Stage 6: behavior, applied INSIDE the saved prefab ──
                // Stage 4's measurement rides along so Interactive's mass and
                // Bendy's detection radius are derived from the SAME triangle
                // walk the collider was. Re-measuring would let the two drift.
                ApplyBehaviorInPrefab(outPath, plan, r, fit.measurement);

                RecordInManifest(outPath, plan);
                return r;
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ── Stage 2 + 3 ─────────────────────────────────────────────────

        /// <summary>
        /// Returns false when the creator cancelled. The two guesses in this
        /// pipeline — unit interpretation and up-axis — are settled here and
        /// nowhere else, and both are confirmed before a single byte is written.
        ///
        /// TWO MEASUREMENT SPACES LIVE IN THIS METHOD. Keep them apart.
        ///
        ///   VISIBLE space   the union of every renderer's bounds. What a
        ///                   creator sees, types, and compares to a door.
        ///   ARGUMENT space  the chosen axis of the FIRST renderer's mesh-local
        ///                   bounds. What PrefabScaler.ScaleToFit divides by.
        ///
        /// On a single-mesh prop they are the same number. On a multi-part
        /// model they are not, and the error is their ratio — 20× or 40× on a
        /// rug or a car, not a rounding difference. So every creator-entered
        /// size goes through ScaleInference.TargetForVisibleSize before it
        /// reaches ScaleToFit, and ScaleDecision.targetMeters is ALREADY in
        /// argument space and must not be run through it twice.
        ///
        /// STAGE ORDER: up-axis → scale → ground. Grounding is done by the
        /// caller AFTER this returns, because the fit multiplies any offset
        /// applied before it — ground first and the prop floats by exactly the
        /// scale factor.
        /// </summary>
        private static bool ResolveSizeAndOrientation(string assetPath, GameObject visual,
                                                      GameObject previewSource,
                                                      ConversionPlan plan, ConversionResult r)
        {
            // Guess the orientation first so the size can be inferred against
            // the axis the model will actually have once it is stood up.
            string upAxisReason;
            UpAxisFix guessedUp = OrientationFitter.GuessUpAxis(visual, assetPath, out upAxisReason);

            ScaleDecision decision = plan.fitScale
                ? ScaleInference.Infer(assetPath, visual, plan.scale.axis, guessedUp)
                : ScaleDecision.Skip();

            UpAxisFix chosenUp = guessedUp;
            bool applyScale = decision.apply;
            PrefabScalerAxis axis = decision.axis;

            // Argument space by default; overwritten from the dialog below, in
            // which case it arrives in VISIBLE space and gets converted.
            float target = decision.targetMeters;
            bool targetIsVisibleSize = false;

            if (ScaleConfirmPopup.NeedsConfirmation(decision, guessedUp))
            {
                var answer = ScaleConfirmPopup.Ask(
                    Path.GetFileNameWithoutExtension(assetPath),
                    decision, guessedUp, upAxisReason, previewSource);

                if (answer.cancelled) return false;

                applyScale = answer.applyScale;
                axis = answer.axis;
                chosenUp = answer.upAxis;

                // Everything the dialog shows and accepts is a real-world size.
                target = answer.desiredVisibleMeters;
                targetIsVisibleSize = true;
            }

            // ── Up-axis (stage 3b). Rotation only — never grounds. ──
            if (chosenUp != UpAxisFix.None)
            {
                OrientationFitter.ApplyUpAxis(visual, chosenUp, r);
                r.Guessed("up-axis: stood up (Z-up → Y-up)"
                          + (string.IsNullOrEmpty(upAxisReason) ? "" : " — " + upAxisReason)
                          + ", confirmed by you");
            }
            else if (guessedUp != UpAxisFix.None)
            {
                r.Guessed("up-axis: left as authored — you declined the Z-up correction");
            }

            // If the creator picked a different axis than the one the decision
            // was inferred against, the stored target belongs to the old axis.
            // Retarget rescales it rather than silently fitting the wrong
            // quantity — never call RemapFitAxis without this.
            if (!targetIsVisibleSize && axis != decision.axis)
            {
                decision = ScaleInference.Retarget(decision, visual, axis, r);
                applyScale = applyScale && decision.apply;
                target = decision.targetMeters;
            }

            // ── Scale (stage 2) ──
            if (applyScale && target > 0f)
            {
                float visibleAsked = targetIsVisibleSize ? target : 0f;
                float fitArgument = targetIsVisibleSize
                    ? ScaleInference.TargetForVisibleSize(visual, target, axis)
                    : target;

                var result = PrefabScaler.ScaleToFit(visual, fitArgument, ToPrefabScalerAxis(axis));
                if (result.ok)
                {
                    string sized = targetIsVisibleSize
                        ? "resized to " + visibleAsked.ToString("0.###") + " m overall"
                        : "fit so its " + axis + " measures " + fitArgument.ToString("0.###") + " m";

                    r.Measured(ScaleInference.Explain(decision) + " → " + sized
                               + " (×" + result.factor.ToString("0.####") + ")");

                    string caveat = ScaleInference.FitCaveat(visual, axis, targetIsVisibleSize);
                    if (!string.IsNullOrEmpty(caveat)) r.Measured("scale: " + caveat);
                }
                else
                {
                    // Still emittable, just unscaled — an unscaled prop the
                    // creator can fix beats no prop at all.
                    r.Failed("scale: " + result.error);
                    return true;
                }
            }
            else
            {
                r.Measured(ScaleInference.Explain(decision) + " → left at authored size");
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

        // ── Stage 6 ─────────────────────────────────────────────────────

        /// <summary>
        /// Behavior is applied to the SAVED prefab through LoadPrefabContents
        /// rather than to the in-memory root before saving. Two reasons: the
        /// fracture baker needs a real asset path to add its meshes to as
        /// sub-assets, and editing the asset copy keeps us from fighting any
        /// instance the creator already has open.
        /// </summary>
        private static void ApplyBehaviorInPrefab(string prefabPath, ConversionPlan plan,
                                                  ConversionResult r,
                                                  ColliderFitter.MeshMeasurement measurement)
        {
            if (plan.behavior == BehaviorPack.None) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            if (contents == null)
            {
                r.Failed("could not open '" + prefabPath + "' to apply behavior");
                return;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(contents);
                Transform motion = PropPrefabEmitter.FindMotion(contents);
                Transform visual = PropPrefabEmitter.FindVisual(contents);

                if (anchor == null || visual == null)
                {
                    r.Failed("prefab hierarchy is malformed — cannot apply behavior");
                    return;
                }

                // Sequential, NOT exclusive. Stage 6 owns the intact prop —
                // the Rigidbody plan.addRigidbody asks for, the collision
                // layer, the Interactable — and the fracture bake owns the
                // pieces. Branching past Apply for Shatterable emitted a
                // static, ungrabbable prop on the Default layer with nothing
                // wired to fire Shatter(); BehaviorPackBuilder's own Shatter
                // case does nothing except say the pieces are baked elsewhere,
                // which is only true if Apply runs at all.
                //
                // Apply first, then bake: VoronoiFracture checks for the intact
                // prop's Rigidbody and reports when it is missing, and it wires
                // Shatter() to an Interactable if the prop has one by then.
                BehaviorPackBuilder.Apply(
                    contents,
                    anchor.gameObject,
                    motion != null ? motion.gameObject : null,
                    visual.gameObject,
                    plan, r, measurement);

                if (plan.behavior == BehaviorPack.Shatter)
                {
                    // The path is passed explicitly: for prefab CONTENTS,
                    // GetAssetPath is empty and there is no PrefabStage, so
                    // BakeInto's own resolver would be down to root.scene.path —
                    // an undocumented property of the preview scene
                    // LoadPrefabContents creates. We have the path right here.
                    VoronoiFracture.BakeInto(contents, visual.gameObject, plan, r, prefabPath);
                }

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ── Shared prop family ──────────────────────────────────────────

        private static void RunSharedFamily(IList<string> assetPaths, ConversionPlan plan,
                                            ConversionReport report)
        {
            var models = new List<string>();
            foreach (var p in assetPaths)
            {
                if (AssetClassifier.Classify(p) == ConvertTrack.Mesh) models.Add(p);
            }

            if (models.Count < 2)
            {
                var r = new ConversionResult { sourcePath = "(shared prop)", ok = false };
                r.Failed("a shared prop family needs at least two models; got " + models.Count);
                report.results.Add(r);
                return;
            }

            var created = SharedPropBuilder.BuildFamily(models, plan, report);
            if (created != null)
            {
                foreach (var path in created) Track(path);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static void ConvertMaterialsOn(string assetPath, ConversionResult r)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return;

            int opaque = 0, particle = 0, exotic = 0;
            var seen = new HashSet<int>();

            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in rend.sharedMaterials)
                {
                    if (m == null || !seen.Add(m.GetInstanceID())) continue;

                    if (DreamPark.EditorTools.MaterialConverter.IsParticleMaterial(m))
                    {
                        if (DreamPark.EditorTools.MaterialConverter.HasExoticParticleFeature(m)) { exotic++; continue; }
                        if (DreamPark.EditorTools.MaterialConverter.ConvertParticleMaterial(m)) particle++;
                    }
                    else if (DreamPark.EditorTools.MaterialConverter.ConvertMaterial(m))
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

        private static void Track(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath)) lastRunCreated.Add(assetPath);
        }

        private static void RecordInManifest(string prefabPath, ConversionPlan plan)
        {
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (string.IsNullOrEmpty(guid)) return;

            var manifest = ConvertReadyManifest.LoadOrCreate();
            if (manifest != null) manifest.Record(guid, prefabPath, plan);
        }

        // ── Revert ──────────────────────────────────────────────────────

        /// <summary>
        /// Delete every asset the last run created. This exists because Unity's
        /// Undo does not cover asset creation — see the note at the top of this
        /// file. Returns the number of assets deleted.
        /// </summary>
        public static int RevertLastRun(IList<string> assetPaths)
        {
            if (assetPaths == null || assetPaths.Count == 0) return 0;

            int deleted = 0;
            var failed = new List<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in assetPaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    if (AssetDatabase.LoadMainAssetAtPath(path) == null) continue;
                    if (AssetDatabase.DeleteAsset(path)) deleted++;
                    else failed.Add(path);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            if (failed.Count > 0)
            {
                Debug.LogWarning("[Convert to DreamPark-Ready] could not delete "
                                 + failed.Count + " asset(s):\n" + string.Join("\n", failed));
            }
            return deleted;
        }
    }
}
#endif
