// ─────────────────────────────────────────────────────────────────────
//  ScaleInference.cs — Stage 2. The 1-vs-100 question, answered as evidence.
//
//  THIS FILE MUTATES NOTHING. It measures, reads the importer, bands the
//  result and hands back a ScaleDecision. The executor decides whether to
//  apply it, and only after the creator has confirmed. That separation is
//  the whole point: "if it's huge, divide by 100" is wrong in both
//  directions — a genuinely 200 m skybox dome and a 2 m statue authored in
//  centimetres both measure 200 units — so the one thing this must never do
//  is act on its own guess.
//
//  TWO SIGNALS, AND ONE TRAP IN HOW THEY COMBINE
//
//  1. ModelImporter.fileScale. Unity parses the FBX header's unit
//     declaration into it; 0.01 means the file declared centimetres. Free
//     and authoritative when present.
//
//     The trap: when useFileScale is TRUE (the default) Unity has ALREADY
//     applied that 0.01, so a well-formed centimetre FBX imports at the
//     right size and fileScale == 0.01 is evidence that nothing is wrong.
//     The dangerous combination is fileScale == 0.01 with useFileScale
//     FALSE — the file says centimetres and the importer is throwing that
//     away. That is a direct, non-heuristic explanation for a 100× prop,
//     and it is the only case where this file pre-selects a fix on a merely
//     ambiguous measurement.
//
//  2. Measured longest-axis bounds, post-import, in metres. The sanity
//     check on signal 1, and the thing the creator is actually shown.
//
//  WHY THERE ARE TWO DIFFERENT MEASUREMENTS IN HERE — AND WHY MIXING THEM
//  IS THE ONE MISTAKE THAT WILL SHIP A 280 m RUG
//
//  measuredMeters (shown to the creator) is the LONGEST AXIS of the UNION of
//  every renderer.
//  targetMeters (handed to PrefabScaler) is the CHOSEN AXIS COMPONENT of what
//  PrefabScaler.ScaleToFit itself measures, which is the FIRST renderer's
//  mesh-local bounds and nothing else — read its source, it does
//  GetComponentInChildren<Renderer>() singular. On a single-mesh model the
//  two are identical. On a multi-part model they are not, and computing the
//  target from the union would make ScaleToFit produce a factor that is not
//  the correction we asked for — a 100× fix would come out as 137×, or 61×,
//  depending on which part happened to be first in the hierarchy. So the
//  target is computed against the same quantity ScaleToFit will divide by,
//  and FitCaveat() reports the discrepancy instead of hiding it.
//
//  THEY ARE NOT INTERCHANGEABLE AND THEY ARE NOT EVEN THE SAME AXIS.
//  targetMeters is a ScaleToFit ARGUMENT. It is never a size to show a human
//  and never a size a human should type. A 700 m rug with mesh-local bounds
//  (700, 17.5, 350) bands LikelyUnitError; a dialog that prefills its
//  "Target size (m)" field from measuredMeters/100 and pairs it with axis Y
//  hands ScaleToFit 7 on an axis that measures 17.5, ScaleToFit divides,
//  factor 0.4, and the rug ships 280 m wide after the creator was shown
//  "700 m → 7 m". Anything whose longest axis is not the fit axis — rug, car,
//  sword, table — hits this.
//
//  So: a size a creator entered goes through TargetForVisibleSize() before it
//  reaches ScaleToFit, and an axis is never changed without Retarget()ing the
//  decision that goes with it.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class ScaleInference
    {
        // ── Confidence bands (spec, Stage 2) ────────────────────────────

        public const float PlausibleMin  = 0.02f;   // a coin
        public const float PlausibleMax  = 5f;      // a large car
        public const float AmbiguousMax  = 500f;
        public const float UnitErrorMin  = 0.005f;  // a grain of rice

        /// <summary>
        /// Band a measured longest-axis size, in metres.
        ///
        /// 0.005–0.02 m is a gap in the spec table (it is below Plausible but
        /// above the LikelyUnitError floor). It is banded Ambiguous: a grain of
        /// rice is 0.005 m and a coin is 0.02 m, so real props do live there and
        /// pre-selecting a 100× fix on them would be wrong.
        ///
        /// A size of 0 means we could not measure. That is NOT evidence of a
        /// unit error, so it bands Ambiguous rather than falling through the
        /// "&lt; 0.005" rule.
        /// </summary>
        public static ScaleConfidence Band(float longestAxisMeters)
        {
            if (longestAxisMeters <= 0f) return ScaleConfidence.Ambiguous;

            if (longestAxisMeters > AmbiguousMax)  return ScaleConfidence.LikelyUnitError;
            if (longestAxisMeters < UnitErrorMin)  return ScaleConfidence.LikelyUnitError;

            if (longestAxisMeters >= PlausibleMin && longestAxisMeters <= PlausibleMax)
                return ScaleConfidence.Plausible;

            return ScaleConfidence.Ambiguous;
        }

        // ── Importer signals ────────────────────────────────────────────

        /// <summary>
        /// The unit scale the model file declares, or 0 when the asset has no
        /// ModelImporter (a .prefab, a texture, a model format Unity imports
        /// through a scripted importer). 1 means "declares metres"; 0.01 means
        /// "declares centimetres".
        /// </summary>
        public static float ReadFileScale(string assetPath)
        {
            var importer = ImporterFor(assetPath);
            return importer != null ? importer.fileScale : 0f;
        }

        /// <summary>
        /// Whether the importer is honouring the file's own unit declaration.
        /// True when there is no ModelImporter at all — for a .prefab there is
        /// no declaration to ignore, so "is it being ignored" is vacuously no.
        /// </summary>
        public static bool ImporterHonorsFileScale(string assetPath)
        {
            var importer = ImporterFor(assetPath);
            return importer == null || importer.useFileScale;
        }

        private static ModelImporter ImporterFor(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (!AssetClassifier.IsModelPath(assetPath)) return null;
            return AssetImporter.GetAtPath(assetPath) as ModelImporter;
        }

        // ── Measurement ─────────────────────────────────────────────────

        /// <summary>
        /// Longest axis of the union of every renderer, in metres, at authored
        /// scale. This is the number the creator is shown and the number the
        /// confidence band is computed from. 0 when unmeasurable.
        /// </summary>
        public static float MeasureLongestAxisMeters(GameObject visual)
        {
            return AssetClassifier.LongestAxisMeters(visual);
        }

        /// <summary>
        /// The bounds size PrefabScaler.ScaleToFit will actually divide by:
        /// the FIRST active renderer's mesh-local bounds, ignoring every
        /// intermediate transform and every other renderer.
        ///
        /// This deliberately reproduces PrefabScaler's behaviour rather than
        /// improving on it. PrefabScaler.TryGetLocalBounds is private, so this
        /// is a copy — if that method ever changes, this one has to change with
        /// it or every computed targetMeters silently stops meaning what it says.
        /// Returns Vector3.zero when there is nothing to measure.
        /// </summary>
        public static Vector3 MeasureFitSourceSize(GameObject visual)
        {
            if (visual == null) return Vector3.zero;

            // GetComponentInChildren<Renderer>() — singular, active-only,
            // depth-first — is exactly what ScaleToFit calls.
            var renderer = visual.GetComponentInChildren<Renderer>();
            if (renderer == null) return Vector3.zero;

            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null) return smr.localBounds.size;

            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) return mf.sharedMesh.bounds.size;

            // ScaleToFit's own last resort: renderer.bounds, a WORLD-space AABB.
            // Only reachable for renderers that draw without a mesh
            // (ParticleSystemRenderer, LineRenderer, TrailRenderer,
            // SpriteRenderer).
            //
            // PrefabScaler reads that property with visual.transform.localScale
            // reset to Vector3.one (and a Physics.SyncTransforms first); we read
            // it with whatever scale the visual currently carries. On a re-run
            // over an already-fitted prefab those two differ by exactly the
            // previously applied factor, so an unadjusted read would make
            // targetMeters wrong by that factor — and re-runnability is an
            // explicit goal of this pipeline. Divide the current scale back out
            // so the number means the same thing on the first run and the fifth.
            Vector3 s = visual.transform.localScale;
            Vector3 b = renderer.bounds.size;
            return new Vector3(
                Mathf.Abs(s.x) > 1e-6f ? b.x / Mathf.Abs(s.x) : 0f,
                Mathf.Abs(s.y) > 1e-6f ? b.y / Mathf.Abs(s.y) : 0f,
                Mathf.Abs(s.z) > 1e-6f ? b.z / Mathf.Abs(s.z) : 0f);
        }

        /// <summary>
        /// Null when there is nothing to say; otherwise a factual sentence for
        /// the report: PrefabScaler measured the fit from ONE part of a
        /// multi-part model. Worth surfacing because it is invisible otherwise
        /// and it makes the reported factor look like an inference bug.
        ///
        /// It deliberately claims NO size error. On the paths this pipeline
        /// actually uses there isn't one: a target from <see cref="Infer"/> is
        /// computed against the same fit-source quantity ScaleToFit divides by,
        /// so the factor is exactly the unit correction; a creator-entered size
        /// routed through <see cref="TargetForVisibleSize"/> produces a factor
        /// of entered/union, so the finished model measures exactly what was
        /// asked for. The wrong-size sentence belongs only to the raw path —
        /// see the overload.
        ///
        /// Call it AFTER a successful ScaleToFit, with r.Measured.
        /// </summary>
        public static string FitCaveat(GameObject visual)
        {
            return FitCaveat(visual, PrefabScalerAxis.Y, false);
        }

        /// <summary>
        /// <paramref name="targetWasRawVisibleSize"/> is true ONLY when the
        /// value handed to ScaleToFit was a size the creator entered for the
        /// whole model and was NOT put through
        /// <see cref="TargetForVisibleSize"/>. That is the one case where the
        /// multi-renderer discrepancy becomes a size the creator did not ask
        /// for: ScaleToFit divides by the first renderer's
        /// <paramref name="axis"/> extent, so the finished model comes out
        /// union/thatExtent times too big.
        ///
        /// Pass false everywhere else. Saying "the prop is 1.37× the size you
        /// asked for" about a prop that is exactly the size they asked for is
        /// worse than saying nothing — the MEASURED lines are the product.
        /// </summary>
        public static string FitCaveat(GameObject visual, PrefabScalerAxis axis,
                                       bool targetWasRawVisibleSize)
        {
            if (visual == null) return null;

            float union = MeasureLongestAxisMeters(visual);
            Vector3 fitSize = MeasureFitSourceSize(visual);
            float fit = Mathf.Max(fitSize.x, Mathf.Max(fitSize.y, fitSize.z));

            if (union <= 0f || fit <= 0f) return null;
            if (Mathf.Abs(union - fit) <= union * 0.01f) return null;

            string s = "fit is measured from one renderer (" + FormatMeters(fit)
                     + ") but the model as a whole measures " + FormatMeters(union)
                     + " — PrefabScaler fits a single part";

            // The overshoot is union / the fitted part's extent ON THE FIT AXIS,
            // not union / its longest axis: ScaleToFit divides by the axis it
            // was given, and on a rug those two differ by 40×.
            float onAxis = AxisComponent(fitSize, axis);
            if (targetWasRawVisibleSize && onAxis > 1e-6f && union / onAxis > 1.01f)
                s += ", so the finished prop measures "
                   + (union / onAxis).ToString("F2", CultureInfo.InvariantCulture)
                   + "× the size you entered";

            return s;
        }

        // ── The correction ──────────────────────────────────────────────

        /// <summary>
        /// The multiplier to apply to the measured size to bring it into a
        /// plausible range: 1 (leave alone), 0.01 (it was authored in
        /// centimetres) or 100 (it was authored in metres but the numbers are
        /// centimetre-sized).
        ///
        /// Only three values, deliberately. Anything else is not a unit error —
        /// it is the creator wanting a specific size, which is a different
        /// field in the dialog.
        /// </summary>
        public static float SuggestedUnitCorrection(float longestAxisMeters, float fileScale,
                                                    bool importerHonorsFileScale)
        {
            if (longestAxisMeters <= 0f) return 1f;

            if (longestAxisMeters > AmbiguousMax)  return 0.01f;
            if (longestAxisMeters < UnitErrorMin)  return 100f;

            // The one case where signal 1 overrides an ambiguous measurement:
            // the file says centimetres and the importer is discarding that.
            // That is not a heuristic, it is the importer telling us the
            // geometry is 100× the size the author meant.
            if (DeclaresCentimetres(fileScale) && !importerHonorsFileScale
                && longestAxisMeters > PlausibleMax)
                return 0.01f;

            return 1f;
        }

        public static bool DeclaresCentimetres(float fileScale)
        {
            return fileScale > 0f && Mathf.Abs(fileScale - 0.01f) < 0.001f;
        }

        // ── The decision ────────────────────────────────────────────────

        public static ScaleDecision Infer(string assetPath, GameObject visual)
        {
            return Infer(assetPath, visual, PrefabScalerAxis.Y);
        }

        /// <summary>
        /// Produce a ScaleDecision. Applies nothing, writes nothing, touches no
        /// importer. <paramref name="axis"/> is the MESH-SPACE axis ScaleToFit
        /// will be given — Y for characters and most props, Z for weapons. If
        /// the Visual node is going to be rotated by an up-axis fix, use the
        /// <see cref="UpAxisFix"/> overload rather than remapping the axis
        /// afterwards; see <see cref="Retarget"/> for why.
        ///
        /// THE RETURNED targetMeters IS A ScaleToFit ARGUMENT AND NOTHING ELSE.
        /// It is the chosen axis component of the first renderer's mesh-local
        /// bounds times the unit correction, which is what makes
        /// (target / fitSource) come out as exactly the correction. It is NOT a
        /// size to display, NOT the size the finished prop will measure, and NOT
        /// a value a creator should type over — it is only comparable with
        /// measuredMeters on a single-mesh model whose longest axis happens to
        /// be the fit axis. A size a human entered becomes a legal target only
        /// by going through <see cref="TargetForVisibleSize"/>.
        ///
        /// If the requested axis is degenerate on this model (a flat sheet
        /// fitted on Y), the decision comes back with the longest axis
        /// substituted, because PrefabScaler fails outright on a ~0 source axis
        /// and "cannot fit" is a worse answer than "fitted the axis that
        /// exists". The substitution is visible in ScaleDecision.axis and is
        /// stated by Explain().
        /// </summary>
        public static ScaleDecision Infer(string assetPath, GameObject visual, PrefabScalerAxis axis)
        {
            var d = ScaleDecision.Skip();
            d.axis = axis;

            float fileScale = ReadFileScale(assetPath);
            bool honors = ImporterHonorsFileScale(assetPath);

            d.fileScale = fileScale;
            d.measuredMeters = MeasureLongestAxisMeters(visual);
            d.confidence = Band(d.measuredMeters);
            d.humanReference = HumanReference(d.measuredMeters);

            if (d.measuredMeters <= 0f)
            {
                // Nothing to measure. Report it and propose nothing — a prop with
                // no renderer has other problems, and inventing a target here
                // would hand PrefabScaler a number it cannot honour.
                return d;
            }

            float correction = SuggestedUnitCorrection(d.measuredMeters, fileScale, honors);
            if (Mathf.Approximately(correction, 1f)) return d;

            // Compute the target against what ScaleToFit measures, NOT against
            // the union — see the file header. This is what makes
            // (target / fitSource) come out as exactly `correction`.
            Vector3 fitSize = MeasureFitSourceSize(visual);
            float onAxis = AxisComponent(fitSize, d.axis);

            if (onAxis <= 1e-6f)
            {
                float longest = Mathf.Max(fitSize.x, Mathf.Max(fitSize.y, fitSize.z));
                if (longest <= 1e-6f) return d;    // nothing fittable; leave apply = false

                d.axis = LongestAxisOf(fitSize);
                onAxis = longest;
            }

            d.apply = true;
            d.targetMeters = onAxis * correction;
            return d;
        }

        /// <summary>
        /// The overload to use when an up-axis fix is in the plan. It remaps the
        /// world-space fit intent into mesh space FIRST and infers against that
        /// axis, so the axis and the target are computed together and can never
        /// be separated afterwards.
        ///
        /// Remapping the axis of an already-inferred decision is the bug this
        /// exists to prevent — see <see cref="Retarget"/>.
        /// </summary>
        public static ScaleDecision Infer(string assetPath, GameObject visual,
                                          PrefabScalerAxis axis, UpAxisFix fix)
        {
            return Infer(assetPath, visual, OrientationFitter.RemapFitAxis(axis, fix));
        }

        /// <summary>
        /// Move a decision onto a different fit axis, rescaling its target so it
        /// still means the same physical fit.
        ///
        /// A ScaleDecision's target is ONLY meaningful together with its axis:
        /// it is fitSize[axis] × correction, so swapping the axis underneath it
        /// silently changes what ScaleToFit computes. Mesh-local size
        /// (10, 200, 3), measured 800 m, correction 0.01 gives target 2 on Y;
        /// change the axis to Z by hand and ScaleToFit divides by 3 instead of
        /// 200 — factor 0.667, not 0.01, and the 800 m model ships at 533 m.
        /// The number looks untouched, which is what makes it hard to see.
        ///
        /// So: never write <c>d.axis = RemapFitAxis(d.axis, fix)</c>. Call this,
        /// or infer with the fix in the first place.
        ///
        /// Safe to call with an unrotated or rotated visual — MeasureFitSourceSize
        /// reads MESH-local bounds, which an up-axis fix on the Visual node does
        /// not touch.
        /// </summary>
        public static ScaleDecision Retarget(ScaleDecision d, GameObject visual, PrefabScalerAxis newAxis)
        {
            return Retarget(d, visual, newAxis, null);
        }

        public static ScaleDecision Retarget(ScaleDecision d, GameObject visual,
                                             PrefabScalerAxis newAxis, ConversionResult r)
        {
            if (d.axis == newAxis) return d;

            if (!d.apply)
            {
                // No target to preserve — the axis is just a record of intent.
                d.axis = newAxis;
                return d;
            }

            Vector3 fit = MeasureFitSourceSize(visual);
            float from = AxisComponent(fit, d.axis);
            float to = AxisComponent(fit, newAxis);

            if (from <= 1e-6f || to <= 1e-6f)
            {
                // One of the two axes is degenerate on this mesh — a flat sheet
                // retargeted onto its own thickness, or a target that was
                // computed against an axis with no extent. ScaleToFit fails
                // outright on a ~0 source axis, and carrying the old number
                // across would apply an arbitrary factor. Dropping the fit is
                // the only honest option, and it is reported rather than silent.
                float degenerate = to <= 1e-6f ? to : from;
                PrefabScalerAxis degenerateAxis = to <= 1e-6f ? newAxis : d.axis;

                d.axis = newAxis;
                d.apply = false;
                if (r != null)
                    r.Skipped("scale: cannot re-express the fit on " + newAxis
                              + " — the model measures " + FormatMeters(degenerate)
                              + " on " + degenerateAxis + "; left at authored size");
                return d;
            }

            d.targetMeters = d.targetMeters * (to / from);
            d.axis = newAxis;
            return d;
        }

        /// <summary>
        /// Convert a size a HUMAN entered ("make this 7 m") into the ScaleToFit
        /// argument that produces it on <paramref name="axis"/>.
        ///
        /// A creator reads and types the number this pipeline showed them, which
        /// is measuredMeters — the longest axis of the union of every renderer.
        /// ScaleToFit divides by something else entirely: the chosen axis
        /// component of the first renderer's mesh-local bounds. Handing a
        /// creator-entered size straight to ScaleToFit therefore fits the wrong
        /// quantity, and the error is the ratio between the two, which for a rug
        /// or a car or a sword is 20× or 40×, not a rounding difference.
        ///
        /// This scales the desired VISIBLE size down onto the fit quantity by
        /// exactly that ratio, so the finished prop's longest axis measures what
        /// the creator asked for. The return value is a ScaleToFit argument, not
        /// a size to show back to them.
        ///
        /// Falls back to <paramref name="desiredMeters"/> unchanged when either
        /// measurement is unavailable — with nothing to correct by, the caller's
        /// number is the best answer there is.
        /// </summary>
        public static float TargetForVisibleSize(GameObject visual, float desiredMeters,
                                                 PrefabScalerAxis axis)
        {
            float union = MeasureLongestAxisMeters(visual);
            float onAxis = AxisComponent(MeasureFitSourceSize(visual), axis);

            if (union <= 1e-6f || onAxis <= 1e-6f) return desiredMeters;

            return onAxis * (desiredMeters / union);
        }

        // ── Human reference ─────────────────────────────────────────────

        // Real-world sizes, in metres, along each object's longest dimension.
        // This exists because a creator who cannot picture 2.3 m can absolutely
        // picture a door, and unit interpretation is the one decision in this
        // pipeline they are best placed to make and we are worst.
        //
        // Ordered ascending for readability. House (10 m) and bus (12 m) overlap
        // in practice; nearest-by-log-ratio picks whichever is closer and ties
        // go to the earlier entry, which is fine — both convey "building-sized".
        private static readonly float[] ReferenceSizes =
        {
            0.005f, 0.02f, 0.09f, 0.3f, 0.9f, 2.0f, 4.5f, 10f, 12f, 105f
        };

        private static readonly string[] ReferenceNames =
        {
            "a grain of rice", "a coin", "a coffee mug", "a wine bottle", "a chair",
            "a door", "a car", "a house", "a bus", "a football pitch"
        };

        // Past this ratio the nearest entry stops being a useful picture — a
        // 5 km object is not "about a football pitch" — so we say "much bigger"
        // instead of quietly lying.
        private const float ReferenceStretchLimit = 8f;

        /// <summary>
        /// A real-world comparison for a measured size, phrased for the report:
        /// "about a door", "much smaller than a grain of rice". Empty string is
        /// never returned — an unmeasurable size says so.
        /// </summary>
        public static string HumanReference(float meters)
        {
            if (meters <= 0f) return "unmeasurable";

            int best = 0;
            float bestDistance = float.MaxValue;

            // Nearest by log-ratio, not by absolute difference: the difference
            // between 0.005 m and 0.02 m matters as much as the difference
            // between 12 m and 105 m, and only a ratio metric says so.
            for (int i = 0; i < ReferenceSizes.Length; i++)
            {
                float distance = Mathf.Abs(Mathf.Log(meters / ReferenceSizes[i]));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            float ratio = meters / ReferenceSizes[best];
            if (ratio > ReferenceStretchLimit)  return "much bigger than " + ReferenceNames[best];
            if (ratio < 1f / ReferenceStretchLimit) return "much smaller than " + ReferenceNames[best];

            return "about " + ReferenceNames[best];
        }

        /// <summary>
        /// Metres formatted at a precision that stays readable across five
        /// orders of magnitude. "0.0043 m" and "1240.0 m" both have to appear in
        /// the same report, and a single format string cannot serve both.
        /// </summary>
        public static string FormatMeters(float meters)
        {
            var c = CultureInfo.InvariantCulture;
            if (meters <= 0f)   return "0 m";
            if (meters < 0.01f) return meters.ToString("F4", c) + " m";
            if (meters < 1f)    return meters.ToString("F3", c) + " m";
            if (meters < 100f)  return meters.ToString("F2", c) + " m";
            return meters.ToString("F1", c) + " m";
        }

        /// <summary>
        /// One line for the Stage 7 report, stating the evidence and then the
        /// conclusion. Reads as MEASURED when nothing is being changed and as a
        /// proposal when it is.
        /// </summary>
        public static string Explain(ScaleDecision d)
        {
            if (d.measuredMeters <= 0f)
                return "could not measure the model — no renderer bounds; left at authored size";

            string s = FormatMeters(d.measuredMeters) + " at authored size (" + d.humanReference + ")";

            if (DeclaresCentimetres(d.fileScale))
                s += "; the file declares centimetres";
            else if (d.fileScale > 0f && !Mathf.Approximately(d.fileScale, 1f))
                s += "; the file declares a unit scale of "
                   + d.fileScale.ToString("G4", CultureInfo.InvariantCulture);

            switch (d.confidence)
            {
                case ScaleConfidence.Ambiguous:
                    s += "; could be a genuinely large object or a centimetre-authored small one";
                    break;
                case ScaleConfidence.LikelyUnitError:
                    s += "; almost certainly a unit error";
                    break;
            }

            // Phrased as "its <axis> measures X" rather than "fit to X",
            // because targetMeters is an axis size, not the model's overall
            // size — see the file header. On a rug the two differ by 40×, and a
            // report line that implied otherwise would be the same lie the
            // dialog used to tell.
            s += d.apply
                ? " → fit so its " + d.axis + " measures " + FormatMeters(d.targetMeters)
                : " → kept as authored";

            return s;
        }

        // ── Axis helpers ────────────────────────────────────────────────

        // Named AxisComponent, not Component: a static method called Component
        // inside a UnityEngine-facing class shadows the UnityEngine.Component
        // type at every unqualified call site in this namespace.
        public static float AxisComponent(Vector3 v, PrefabScalerAxis axis)
        {
            switch (axis)
            {
                case PrefabScalerAxis.X: return v.x;
                case PrefabScalerAxis.Y: return v.y;
                default:                 return v.z;
            }
        }

        public static PrefabScalerAxis LongestAxisOf(Vector3 size)
        {
            if (size.x >= size.y && size.x >= size.z) return PrefabScalerAxis.X;
            if (size.y >= size.z) return PrefabScalerAxis.Y;
            return PrefabScalerAxis.Z;
        }
    }
}
#endif
