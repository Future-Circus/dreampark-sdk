// ─────────────────────────────────────────────────────────────────────
//  OrientationFitter.cs — Stage 3. Standing the model up.
//
//  This is two operations with very different confidence levels, and
//  merging them is how this feature would get a reputation for mangling
//  assets. They are kept apart on purpose:
//
//   3a  GroundPivot   deterministic, always applied, reported as MEASURED.
//   3b  GuessUpAxis   a heuristic, reported as GUESSED, applied only after
//                     the creator has confirmed it.
//
//  ORDER, AND IT IS A THREE-WAY ORDER, NOT A TWO-WAY ONE.
//  The spec's stage table lists ground-pivot (3) before up-axis (3b). Applied
//  in that order the pivot is wrong: rotating the Visual node after grounding
//  it moves the geometry's minimum-Y somewhere else entirely, and the prop
//  ends up buried or floating. The rotation has to land first.
//
//  Stage 2's FIT then has to land between them. ScaleToFit multiplies the
//  Visual node's localScale, which moves the geometry's minimum-Y by the same
//  factor, so a pivot grounded before the fit is ungrounded by it. The one
//  correct order is:
//
//      ApplyUpAxis  →  ScaleToFit  →  GroundPivot
//
//  which is why grounding is NOT bundled into the rotation step. Stand()
//  takes an explicit groundPivot flag and a caller that fits afterwards must
//  pass false: grounding twice does not corrupt the prop, but it puts two
//  contradictory MEASURED lines in the report — "the mesh floated 100 m above
//  its own origin" and then "the mesh sat 99 m below its own origin" — one of
//  which describes a state that never existed, in a feature whose entire
//  value is that MEASURED lines can be trusted. The flag also exists because
//  grounding is a creator-facing toggle (plan.groundPivot, off in the
//  AudioEmitter preset); a rotation step that grounds unconditionally
//  overrides a checkbox the creator unticked.
//
//  THE OTHER CROSS-STAGE TRAP, WHICH IS INVISIBLE UNTIL IT ISN'T.
//  PrefabScaler.ScaleToFit measures MESH-LOCAL bounds, so its Axis argument
//  names an axis of the mesh, not of the world. Once ApplyUpAxis has rotated
//  the Visual node -90° about X, the mesh's local +Y points along world -Z:
//  asking for "Y" then fits the model's depth and the creator gets a prop
//  scaled by whatever the ratio between its height and its depth happens to
//  be. RemapFitAxis() converts a world-space intent into the mesh-space axis
//  that produces it. But an axis is half of a ScaleDecision — the other half,
//  targetMeters, was computed FROM that axis — so remapping the axis on its
//  own quietly changes what ScaleToFit divides by. Infer with the fix
//  (ScaleInference.Infer(path, visual, axis, fix)) or Retarget the decision;
//  never remap a bare axis.
//
//  Everything here writes only to the Visual node's own transform — never
//  the prop root. The root is the park loader's placement contract.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class OrientationFitter
    {
        /// <summary>
        /// Rotation applied for UpAxisFix.ZUpToYUp. -90° about X takes the
        /// model's local +Z (its "up" in a Z-up DCC tool) onto world +Y.
        /// </summary>
        public static readonly Quaternion ZUpToYUpRotation = Quaternion.Euler(-90f, 0f, 0f);

        // ── 3a. Ground the pivot ────────────────────────────────────────

        /// <summary>
        /// Offset <paramref name="visual"/>'s localPosition so the union of its
        /// renderer bounds has its minimum Y at zero in its PARENT's space.
        /// Returns the offset that was applied (0 when nothing moved).
        ///
        /// Deterministic and always correct for something that will be placed
        /// on a floor. It is also what makes PropTemplate's footprint and
        /// SurfaceHeight mean what they look like they mean.
        ///
        /// Y only. Centring X and Z is a different decision — it would silently
        /// destroy an authored offset on, say, a lamp whose post is deliberately
        /// off-centre — and this stage is the deterministic one, so it does not
        /// get to make that call.
        ///
        /// Registers undo. Pass registerUndo: false for a hierarchy the caller
        /// is going to destroy itself — see the overload.
        /// </summary>
        public static Vector3 GroundPivot(GameObject visual, ConversionResult r)
        {
            return GroundPivot(visual, r, true);
        }

        /// <summary>
        /// <paramref name="registerUndo"/> false for scratch hierarchies.
        ///
        /// The executor builds its canonical hierarchy in the OPEN scene and
        /// DestroyImmediates it before the run ends, so undo entries against it
        /// are entries for objects that no longer exist — and they get folded
        /// into the single collapsed "Convert to DreamPark-Ready" group the
        /// report tells the creator to press Cmd-Z on. RecordUndo already skips
        /// persistent and preview-scene objects, but a throwaway scene object is
        /// indistinguishable from a real one, so this has to be told.
        /// </summary>
        public static Vector3 GroundPivot(GameObject visual, ConversionResult r, bool registerUndo)
        {
            if (visual == null)
            {
                if (r != null) r.Skipped("ground pivot: no visual object");
                return Vector3.zero;
            }

            Bounds inParent;
            if (!TryGetBoundsInParentSpace(visual, out inParent))
            {
                if (r != null)
                    r.Skipped("ground pivot: '" + visual.name + "' has no renderer bounds to ground");
                return Vector3.zero;
            }

            float minY = inParent.min.y;

            // Sub-tenth-of-a-millimetre is below what any DCC tool authors
            // meaningfully and below what a headset can show. Moving anyway
            // would put a pointless MEASURED line in every report.
            if (Mathf.Abs(minY) < 0.0001f)
            {
                if (r != null) r.Skipped("pivot already sits at the base of the mesh");
                return Vector3.zero;
            }

            if (registerUndo) RecordUndo(visual, "Ground Pivot");

            Vector3 offset = new Vector3(0f, -minY, 0f);
            visual.transform.localPosition += offset;
            EditorUtility.SetDirty(visual);

            if (r != null)
            {
                string amount = ScaleInference.FormatMeters(Mathf.Abs(minY));
                r.Measured(minY < 0f
                    ? "pivot moved to base — the mesh sat " + amount + " below its own origin"
                    : "pivot moved to base — the mesh floated " + amount + " above its own origin");
            }

            return offset;
        }

        /// <summary>
        /// The union renderer bounds of <paramref name="visual"/> expressed in
        /// its parent's local space — i.e. including its own localPosition,
        /// localRotation and localScale. With no parent this is world space,
        /// which is the same thing for a prop root.
        /// </summary>
        public static bool TryGetBoundsInParentSpace(GameObject visual, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (visual == null) return false;

            Transform parent = visual.transform.parent;
            Matrix4x4 worldToParent = parent != null ? parent.worldToLocalMatrix : Matrix4x4.identity;

            return AssetClassifier.TryGetBoundsInSpace(visual, worldToParent, false, out bounds);
        }

        // ── 3b. Up-axis ─────────────────────────────────────────────────

        public static UpAxisFix GuessUpAxis(GameObject visual, out string confidence)
        {
            return GuessUpAxis(visual, ResolveModelPath(visual), out confidence);
        }

        /// <summary>
        /// A GUESS, and it must be reported as one. Z-up-to-Y-up cannot be
        /// detected reliably from geometry: the bounds heuristic works for trees
        /// and characters and fails outright for a rug, a table, or anything
        /// wider than it is tall. The caller shows both orientations and lets
        /// the creator click one; this only chooses which is preselected.
        ///
        /// <paramref name="confidence"/> comes back as a sentence for the
        /// GUESSED line in the report, never as a bare number.
        /// </summary>
        public static UpAxisFix GuessUpAxis(GameObject visual, string modelPath, out string confidence)
        {
            confidence = "kept as authored — nothing suggested otherwise";
            if (visual == null) return UpAxisFix.None;

            // Signal 1: the importer. bakeAxisConversion true means Unity has
            // already folded the source tool's axis convention into the mesh
            // data, so a manual -90 would rotate a model that is already right.
            // This is the only non-heuristic signal available and it outranks
            // the shape test.
            var importer = string.IsNullOrEmpty(modelPath)
                ? null
                : AssetImporter.GetAtPath(modelPath) as ModelImporter;

            if (importer != null && importer.bakeAxisConversion)
            {
                confidence = "the importer has already baked an axis conversion into this model, "
                           + "so it is treated as correct as authored";
                return UpAxisFix.None;
            }

            // Signal 2: shape. A Z-up export lands with its height along Z and
            // its footprint spread across X and Y.
            Bounds b;
            if (!AssetClassifier.TryGetLocalBounds(visual, false, out b))
            {
                confidence = "no renderer bounds to judge orientation from — kept as authored";
                return UpAxisFix.None;
            }

            Vector3 s = b.size;
            float x = Mathf.Max(s.x, 1e-6f);
            float y = Mathf.Max(s.y, 1e-6f);
            float z = Mathf.Max(s.z, 1e-6f);

            bool zDominant = z > y * DominanceRatio && z > x * DominanceRatio;

            // "an XY footprint": the two non-dominant axes are of comparable
            // extent, the way a trunk or a torso is. A rug (wide in X and Z,
            // thin in Y) never reaches this test because Z is not dominant
            // over X, which is exactly the failure case the spec calls out.
            bool xyFootprint = Mathf.Max(x, y) <= Mathf.Min(x, y) * FootprintRatio;

            if (zDominant && xyFootprint)
            {
                confidence = "low confidence — the model is " + ScaleInference.FormatMeters(z)
                           + " along Z with a roughly square X/Y footprint ("
                           + Ratio(x) + " x " + Ratio(y)
                           + "), which is the shape a Z-up export makes. Check the preview.";
                return UpAxisFix.ZUpToYUp;
            }

            confidence = "kept as authored — tallest axis is "
                       + (y >= x && y >= z ? "Y" : (x >= z ? "X" : "Z"))
                       + ", no Z-up signal in the bounds shape";
            return UpAxisFix.None;
        }

        // Deliberately loose. A tighter threshold catches more true Z-up models
        // and also flips more wide props onto their side, and a wrong flip is
        // far more expensive than a missed one — the creator sees the missed one
        // immediately in the preview and clicks the other button.
        private const float DominanceRatio = 1.3f;
        private const float FootprintRatio = 3f;

        /// <summary>
        /// Apply the up-axis fix to the Visual node — the whole of stage 3b, and
        /// nothing else. It does NOT ground the pivot: grounding has to happen
        /// after Stage 2's fit, see the file header.
        ///
        /// Idempotent: the node's localRotation is SET, not multiplied, because
        /// in the canonical hierarchy the only thing that ever rotates Visual is
        /// this fix. Multiplying would compound on every re-run and
        /// re-conversion is an explicit goal.
        /// </summary>
        public static void ApplyUpAxis(GameObject visual, UpAxisFix fix)
        {
            ApplyUpAxis(visual, fix, null, true);
        }

        public static void ApplyUpAxis(GameObject visual, UpAxisFix fix, ConversionResult r)
        {
            ApplyUpAxis(visual, fix, r, true);
        }

        /// <summary>
        /// <paramref name="registerUndo"/> false for a hierarchy the caller
        /// destroys itself — same reasoning as GroundPivot's overload.
        /// </summary>
        public static void ApplyUpAxis(GameObject visual, UpAxisFix fix, ConversionResult r,
                                       bool registerUndo)
        {
            if (visual == null)
            {
                if (r != null) r.Skipped("up-axis: no visual object");
                return;
            }

            Quaternion target = fix == UpAxisFix.ZUpToYUp ? ZUpToYUpRotation : Quaternion.identity;

            if (visual.transform.localRotation != target)
            {
                if (registerUndo) RecordUndo(visual, "Apply Up-Axis Fix");
                visual.transform.localRotation = target;
                EditorUtility.SetDirty(visual);
            }

            if (r == null) return;

            r.Guessed(fix == UpAxisFix.ZUpToYUp
                ? "up-axis: rotated -90 degrees about X to bring Z-up geometry upright"
                : "up-axis: kept as authored");
        }

        /// <summary>
        /// Both halves of stage 3 in the order that works: rotate first, then
        /// ground. Grounding before rotating measures the minimum-Y of geometry
        /// that is about to be turned on its side, so the offset it computes is
        /// for an orientation the prop will not be in.
        ///
        /// <paramref name="groundPivot"/> is MANDATORY rather than assumed, and
        /// there are two reasons it exists — the header spells both out. Pass
        /// the creator's plan.groundPivot, and pass FALSE from any caller that
        /// runs ScaleToFit after this and grounds afterwards (which is every
        /// caller in the mesh pipeline: the fit multiplies the offset this
        /// would compute). If you only need the rotation, call ApplyUpAxis —
        /// that is what this reduces to with the flag false.
        ///
        /// Returns the ground offset applied; zero when nothing was grounded.
        /// </summary>
        public static Vector3 Stand(GameObject visual, UpAxisFix fix, bool groundPivot,
                                    ConversionResult r)
        {
            return Stand(visual, fix, groundPivot, r, true);
        }

        public static Vector3 Stand(GameObject visual, UpAxisFix fix, bool groundPivot,
                                    ConversionResult r, bool registerUndo)
        {
            ApplyUpAxis(visual, fix, r, registerUndo);
            if (!groundPivot) return Vector3.zero;
            return GroundPivot(visual, r, registerUndo);
        }

        // ── The Stage 2 interlock ───────────────────────────────────────

        /// <summary>
        /// Convert a world-space fit intent ("make it 2 m tall") into the
        /// mesh-space axis PrefabScaler.ScaleToFit has to be given to produce
        /// it, given the up-axis fix on the Visual node.
        ///
        /// ScaleToFit measures mesh-LOCAL bounds, so its Axis names an axis of
        /// the mesh. After a -90° X rotation the mesh's local +Z is world up and
        /// its local +Y is world -Z, so "fit the height" means Axis.Z.
        ///
        /// Self-inverse — the same call maps mesh-space back to world-space.
        ///
        /// NEVER REMAP AN AXIS WITHOUT RETARGETING THE ScaleDecision THAT GOES
        /// WITH IT. ScaleInference.Infer computes targetMeters as
        /// fitSize[axis] × correction, so the target only means anything
        /// alongside the axis it was computed from: assigning a remapped axis
        /// back onto a finished decision leaves the number untouched and changes
        /// what ScaleToFit divides by, turning a 100× unit fix into whatever the
        /// ratio between two mesh dimensions happens to be. Use
        /// ScaleInference.Infer(path, visual, axis, fix), which infers against
        /// the remapped axis in the first place, or ScaleInference.Retarget on a
        /// decision that already exists. This function is the primitive those
        /// two are built from, not a call site.
        /// </summary>
        public static PrefabScalerAxis RemapFitAxis(PrefabScalerAxis worldAxis, UpAxisFix fix)
        {
            if (fix != UpAxisFix.ZUpToYUp) return worldAxis;

            switch (worldAxis)
            {
                case PrefabScalerAxis.Y: return PrefabScalerAxis.Z;
                case PrefabScalerAxis.Z: return PrefabScalerAxis.Y;
                default:                 return PrefabScalerAxis.X;
            }
        }

        // ── Internals ───────────────────────────────────────────────────

        /// <summary>
        /// The model asset behind a Visual node, or empty when there isn't one
        /// (a generated quad, a hand-built hierarchy). Handles both a raw asset
        /// GameObject and an instance of one.
        /// </summary>
        public static string ResolveModelPath(GameObject visual)
        {
            if (visual == null) return string.Empty;

            string direct = AssetDatabase.GetAssetPath(visual);
            if (AssetClassifier.IsModelPath(direct)) return direct;

            // GetCorrespondingObjectFromOriginalSource walks all the way back
            // through a variant chain to the imported model, which is what we
            // want — an intermediate prefab has no ModelImporter.
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(visual);
            if (source != null)
            {
                string sourcePath = AssetDatabase.GetAssetPath(source);
                if (AssetClassifier.IsModelPath(sourcePath)) return sourcePath;
            }

            // The model may hang one level down under a wrapper node.
            var renderer = visual.GetComponentInChildren<Renderer>(true);
            if (renderer != null)
            {
                Mesh mesh = AssetClassifier.MeshOf(renderer);
                if (mesh != null)
                {
                    string meshPath = AssetDatabase.GetAssetPath(mesh);
                    if (AssetClassifier.IsModelPath(meshPath)) return meshPath;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Undo, but only where undo exists. PrefabUtility.LoadPrefabContents
        /// hands back objects in an isolated PREVIEW scene; registering undo
        /// against those pollutes the creator's undo stack with entries that
        /// point at objects destroyed by UnloadPrefabContents, and Ctrl-Z then
        /// does nothing visible or logs a null reference.
        ///
        /// A scratch hierarchy built in the OPEN scene and destroyed by the
        /// caller has the same problem and is not detectable from here — that
        /// case is the registerUndo flag on the public mutators.
        /// </summary>
        private static void RecordUndo(GameObject go, string label)
        {
            if (go == null) return;
            if (EditorUtility.IsPersistent(go)) return;
            if (EditorSceneManager.IsPreviewSceneObject(go)) return;

            Undo.RegisterFullObjectHierarchyUndo(go, label);
        }

        private static string Ratio(float v)
        {
            return v.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
#endif
