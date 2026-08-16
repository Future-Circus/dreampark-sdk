// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyPlan.cs — the shared vocabulary for Convert to DreamPark-Ready
//
//  THE LOAD-BEARING DESIGN DECISION
//
//  Interactive / Bendy / Shatterable are NOT three converters. They are three
//  presets that fill one ConversionPlan differently, and "Custom…" is that same
//  plan with its fields exposed in a dialog. There is exactly one executor.
//
//  The alternative — a converter per preset — is how you end up with scale
//  inference that is correct on Interactive and subtly wrong on Shatterable,
//  and nobody notices for four months. Presets diverge over time. A flag sheet
//  cannot.
//
//  If you are adding a capability, add a FIELD here and a branch in the
//  executor. Do not add a second entry point.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    // ── What kind of thing are we converting ────────────────────────────

    public enum ConvertTrack
    {
        Mesh,        // .fbx/.glb/... or an existing .prefab
        Texture,     // .png/.jpg/... → aspect-accurate plane
        Audio,       // .wav/.mp3/... → emitter prop
        Unsupported,
    }

    // ── Stage 2: unit inference ─────────────────────────────────────────

    /// <summary>
    /// How much we trust the measured size. Drives whether the dialog
    /// pre-selects a rescale or leaves the asset alone.
    ///
    /// The naive heuristic ("if it's huge, divide by 100") is wrong in both
    /// directions: a genuinely 200 m skybox dome and a 2 m statue authored in
    /// centimetres both measure 200 units. So we never rescale silently — we
    /// measure, band the result, and let the creator confirm.
    /// </summary>
    public enum ScaleConfidence
    {
        /// 0.02 m – 5 m. A plausible prop. Accept as authored.
        Plausible,

        /// 5 m – 500 m. Could be a large object or a cm-authored small one.
        Ambiguous,

        /// > 500 m or < 0.005 m. Almost certainly a unit error.
        LikelyUnitError,
    }

    [Serializable]
    public struct ScaleDecision
    {
        public bool apply;                  // false → leave the model at authored size
        public float targetMeters;          // desired size along `axis`
        public PrefabScalerAxis axis;

        // Evidence, carried through to the report so the creator can see WHY.
        public float measuredMeters;        // longest-axis bounds at authored scale
        public float fileScale;             // ModelImporter.fileScale; 0 when unknown
        public ScaleConfidence confidence;
        public string humanReference;       // "about a door", "about a coffee mug"

        public static ScaleDecision Skip()
        {
            return new ScaleDecision { apply = false, axis = PrefabScalerAxis.Y };
        }
    }

    /// <summary>
    /// Mirrors DreamPark.EditorTools.PrefabScaler.Axis so this file does not
    /// have to reference the editor-tools assembly to declare a plan. The
    /// executor maps it across at the call site.
    /// </summary>
    public enum PrefabScalerAxis { X, Y, Z }

    // ── Stage 3: orientation ────────────────────────────────────────────

    public enum UpAxisFix
    {
        /// Leave the model as authored. Always the default.
        None,

        /// Rotate -90° about X. For Blender/Max exports that came out Z-up.
        ZUpToYUp,
    }

    // ── Stage 4: colliders ──────────────────────────────────────────────

    public enum ColliderChoice
    {
        /// Run the ladder in ColliderFitter. What every preset uses.
        Auto,
        Box,
        Sphere,
        Capsule,
        ConvexMesh,
        /// Non-convex MeshCollider. ONLY legal on something with no Rigidbody.
        MeshStatic,
        None,
    }

    // ── Stage 6: behavior ───────────────────────────────────────────────

    public enum BehaviorPack
    {
        None,
        Interactive,
        Bendy,
        Shatter,
    }

    public enum BillboardMode
    {
        /// Mode 1 — the default. A plain plane with a fixed facing.
        None,
        /// Mode 2 — yaw only. Trees, foliage, anything standing on the floor.
        YAxis,
        /// Mode 3 — full facing. Icons, pickups, floating sprites.
        Full,
    }

    [Serializable]
    public class BendSettings
    {
        // Calibrated against three shipped CrashCourse prefabs. 5,000,000 reads
        // as rigid; 3,000 is what makes E_FlipGate floppy. These two numbers ARE
        // the preset vocabulary — do not invent new ones without a prefab to
        // check them against.
        public const float SpringStiff = 5000000f;
        public const float SpringFloppy = 3000f;

        // ── Transform bend (EasyBend, non-skinned) ──
        // EasyBend ships with defaults that fail SILENTLY for this use:
        //   eventOnStart false   → Update early-returns forever, never bends
        //   detectionMask ~0     → bends away from walls and floors, permanently
        //   detectionRadius 0.75 → a metre-wide trigger around a 5 cm coin
        public float maxTiltAngle = 22f;
        public float springStrength = 55f;
        public float springDamping = 7f;
        /// Scaled from the model's own footprint at execute time, floored here.
        public float minDetectionRadius = 0.15f;
        public bool eventOnStart = true;

        // ── Jiggle ragdoll (skinned meshes) ──
        // Ported from CrashCourse InflatableRigBuilder. See JiggleRigBuilder.
        public float boneRadius = 0.42f;
        public float colliderSpacing = 0.01f;   // NEGATIVE deliberately overlaps
        public float jointSpring = SpringStiff;
        public float jointMaxForce = 500f;
        public bool archway = false;
    }

    [Serializable]
    public class FractureSettings
    {
        /// Every piece is a draw call and a rigidbody. 12 is the default for a
        /// reason; above WarnAbovePieces we say so in the report.
        public int pieces = 12;
        public const int WarnAbovePieces = 32;

        public Material insideMaterial;
        /// World units per UV unit on the cut caps, so interior texture does not
        /// scale with piece size.
        public float capUvScale = 1f;
        /// Impulse applied radially from the contact point when it shatters.
        public float burstImpulse = 2f;
        /// Seconds before pieces fade and despawn. 0 = never.
        public float pieceLifetime = 8f;
    }

    [Serializable]
    public class PlaneSettings
    {
        public BillboardMode billboard = BillboardMode.None;   // mode 1 is default
        /// Plane height in metres; width falls out of the texture's aspect.
        public float heightMeters = 1f;
        public bool twoSided = true;         // a one-sided plane vanishes from behind
        public bool alphaClip = true;
    }

    [Serializable]
    public class AudioSettings
    {
        public bool looping = false;
        public float audibleRadius = 8f;
        public float volume = 1f;
        public bool normalizeImportSettings = true;
    }

    [Serializable]
    public class SharedPropSettings
    {
        public bool enabled = false;
        /// Family name. Base becomes _P_{family}_Base, variants P_{family}_{model}.
        public string familyName = "";
        /// false → each variant overrides collider size/center (the default).
        /// true  → one collider on the base, fitted to the union of all models.
        public bool shareCollider = false;
    }

    // ── The plan ────────────────────────────────────────────────────────

    [Serializable]
    public class ConversionPlan
    {
        public ConvertTrack track = ConvertTrack.Mesh;
        public string presetName = "Custom";

        // Stage 1
        public bool convertMaterials = true;
        public bool extractEmbeddedMaterials = true;

        // Stage 2
        public bool fitScale = true;
        public ScaleDecision scale = ScaleDecision.Skip();

        // Stage 3
        public bool groundPivot = true;
        public UpAxisFix upAxis = UpAxisFix.None;

        // Stage 4
        public ColliderChoice collider = ColliderChoice.Auto;

        // Stage 5
        public bool emitPropPrefab = true;
        public PropCategory category = PropCategory.Generic;
        public bool affectsGapFiller = true;

        // Stage 6
        public bool addRigidbody = false;
        public BehaviorPack behavior = BehaviorPack.None;

        public BendSettings bend = new BendSettings();
        public FractureSettings fracture = new FractureSettings();
        public PlaneSettings plane = new PlaneSettings();
        public AudioSettings audio = new AudioSettings();
        public SharedPropSettings shared = new SharedPropSettings();

        /// <summary>
        /// A moving Rigidbody REQUIRES a convex collider. Unity silently
        /// disables a non-convex MeshCollider on a non-kinematic body, which
        /// reads as "my prop falls through the floor" and points at nothing.
        /// </summary>
        public bool RequiresConvexCollider
        {
            get { return addRigidbody || behavior == BehaviorPack.Shatter; }
        }

        // ── Presets ─────────────────────────────────────────────────────

        public static ConversionPlan Interactive()
        {
            return new ConversionPlan
            {
                presetName = "Interactive",
                addRigidbody = true,
                behavior = BehaviorPack.Interactive,
                category = PropCategory.Generic,
            };
        }

        public static ConversionPlan Bendy()
        {
            return new ConversionPlan
            {
                presetName = "Bendy",
                addRigidbody = false,          // the bend owns the transform, not physics
                behavior = BehaviorPack.Bendy,
                category = PropCategory.Decoration,
            };
        }

        public static ConversionPlan Shatterable()
        {
            return new ConversionPlan
            {
                presetName = "Shatterable",
                addRigidbody = true,
                behavior = BehaviorPack.Shatter,
                category = PropCategory.Generic,
            };
        }

        public static ConversionPlan TexturePlane()
        {
            return new ConversionPlan
            {
                presetName = "Texture Plane",
                track = ConvertTrack.Texture,
                convertMaterials = false,
                extractEmbeddedMaterials = false,
                fitScale = false,
                category = PropCategory.Decoration,
            };
        }

        public static ConversionPlan AudioEmitter()
        {
            return new ConversionPlan
            {
                presetName = "Audio Emitter",
                track = ConvertTrack.Audio,
                convertMaterials = false,
                extractEmbeddedMaterials = false,
                fitScale = false,
                groundPivot = false,
                collider = ColliderChoice.None,
                category = PropCategory.Decoration,
            };
        }

        public ConversionPlan Clone()
        {
            var c = (ConversionPlan)MemberwiseClone();
            c.bend     = bend.CloneViaJson();
            c.fracture = fracture.CloneViaJson();
            c.plane    = plane.CloneViaJson();
            c.audio    = audio.CloneViaJson();
            c.shared   = shared.CloneViaJson();

            // UnityEngine.Object references do NOT survive a JsonUtility
            // round-trip — they come back null. Re-attach by hand.
            if (fracture != null && c.fracture != null)
                c.fracture.insideMaterial = fracture.insideMaterial;

            return c;
        }
    }

    /// <summary>
    /// MemberwiseClone is protected, so the settings classes cannot be cloned
    /// from outside themselves. Rather than five near-identical Clone() methods,
    /// JsonUtility round-trips them — they are flat [Serializable] classes of
    /// primitives, which is exactly what JsonUtility handles correctly.
    ///
    /// Caveat that bit us once already: UnityEngine.Object fields (Material,
    /// Texture, …) come back NULL. Anything holding one must re-attach it after
    /// calling this. See ConversionPlan.Clone.
    /// </summary>
    internal static class SettingsCloneExtensions
    {
        public static T CloneViaJson<T>(this T src) where T : class, new()
        {
            if (src == null) return new T();
            return JsonUtility.FromJson<T>(JsonUtility.ToJson(src));
        }
    }

    // ── The report ──────────────────────────────────────────────────────

    /// <summary>
    /// Separating MEASURED from GUESSED is the difference between a tool people
    /// trust and a tool people stop using after it silently mangles one asset.
    /// There are exactly two guesses in this pipeline — unit interpretation and
    /// up-axis — and both are confirmed by the creator before anything is written.
    /// </summary>
    public enum DecisionKind
    {
        Measured,
        Guessed,
        Added,
        Extracted,
        Skipped,
        Failed,
    }

    public sealed class Decision
    {
        public DecisionKind kind;
        public string message;

        public Decision(DecisionKind kind, string message)
        {
            this.kind = kind;
            this.message = message;
        }

        public string Label
        {
            get
            {
                switch (kind)
                {
                    case DecisionKind.Measured:  return "MEASURED";
                    case DecisionKind.Guessed:   return "GUESSED";
                    case DecisionKind.Added:     return "ADDED";
                    case DecisionKind.Extracted: return "EXTRACTED";
                    case DecisionKind.Skipped:   return "SKIPPED";
                    default:                     return "FAILED";
                }
            }
        }
    }

    public sealed class ConversionResult
    {
        public string sourcePath;
        public string outputPath;            // null when nothing was written
        public bool ok;
        public string error;
        public List<Decision> decisions = new List<Decision>();

        public void Measured(string m)  { decisions.Add(new Decision(DecisionKind.Measured, m)); }
        public void Guessed(string m)   { decisions.Add(new Decision(DecisionKind.Guessed, m)); }
        public void Added(string m)     { decisions.Add(new Decision(DecisionKind.Added, m)); }
        public void Extracted(string m) { decisions.Add(new Decision(DecisionKind.Extracted, m)); }
        public void Skipped(string m)   { decisions.Add(new Decision(DecisionKind.Skipped, m)); }
        public void Failed(string m)    { decisions.Add(new Decision(DecisionKind.Failed, m)); ok = false; error = m; }
    }

    public sealed class ConversionReport
    {
        public List<ConversionResult> results = new List<ConversionResult>();

        public int OkCount     { get { return results.FindAll(r => r.ok).Count; } }
        public int FailedCount { get { return results.FindAll(r => !r.ok).Count; } }

        public string ToPlainText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine(r.outputPath != null
                    ? System.IO.Path.GetFileNameWithoutExtension(r.outputPath) + "  →  " + r.outputPath
                    : System.IO.Path.GetFileName(r.sourcePath) + "  →  (nothing written)");
                foreach (var d in r.decisions)
                {
                    sb.AppendLine("  " + d.Label.PadRight(10) + " " + d.message);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
#endif
