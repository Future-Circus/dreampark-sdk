// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyOptionsWindow.cs — "Custom…" is the preset sheet, unlocked
//
//  There is no separate custom converter. This window edits the exact same
//  ConversionPlan the three named presets fill in, which is the whole reason
//  the presets cannot drift from each other: there is one struct and one
//  executor, and this is a view onto them.
//
//  If you are adding a capability, add a field to ConversionPlan and a row
//  here. If you find yourself adding a code path that only Custom can reach,
//  something has gone wrong upstream.
//
//  The window opens pre-filled from whichever preset the creator right-clicked
//  through, so "Custom…" reads as "…starting from Interactive" rather than as
//  a blank form. A blank form is how you get thirty props with the collider
//  mode left on whatever the default was.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public class ConvertReadyOptionsWindow : EditorWindow
    {
        private ConversionPlan plan;
        private Action<ConversionPlan> onConfirm;
        private Vector2 scroll;
        private int startingPreset;
        private bool confirmed;

        private static readonly string[] PresetNames =
        {
            "Interactive", "Bendy", "Shatterable", "Texture Plane", "Audio Emitter",
        };

        public static void Open(ConversionPlan seed, int assetCount, Action<ConversionPlan> onConfirm)
        {
            var w = GetWindow<ConvertReadyOptionsWindow>(true, "Convert to DreamPark-Ready", true);
            w.plan = seed != null ? seed.Clone() : ConversionPlan.Interactive();
            w.onConfirm = onConfirm;
            w.startingPreset = IndexOfPreset(w.plan.presetName);
            w.minSize = new Vector2(440, 560);
            w.confirmed = false;
            w.Show();
        }

        private static int IndexOfPreset(string name)
        {
            for (int i = 0; i < PresetNames.Length; i++)
                if (string.Equals(PresetNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        private static ConversionPlan PresetByIndex(int i)
        {
            switch (i)
            {
                case 1:  return ConversionPlan.Bendy();
                case 2:  return ConversionPlan.Shatterable();
                case 3:  return ConversionPlan.TexturePlane();
                case 4:  return ConversionPlan.AudioEmitter();
                default: return ConversionPlan.Interactive();
            }
        }

        private void OnGUI()
        {
            if (plan == null) { Close(); return; }

            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawPresetRow();
            EditorGUILayout.Space(6);

            DrawPipelineSection();

            switch (plan.track)
            {
                case ConvertTrack.Texture: DrawPlaneSection(); break;
                case ConvertTrack.Audio:   DrawAudioSection(); break;
                default:                   DrawBehaviorSection(); break;
            }

            EditorGUILayout.Space(6);
            DrawSharedPropSection();

            EditorGUILayout.EndScrollView();

            DrawButtons();
        }

        private void DrawPresetRow()
        {
            EditorGUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.Popup(
                new GUIContent("Start from", "Loads that preset's values. Your edits below then diverge from it."),
                startingPreset, PresetNames);
            if (EditorGUI.EndChangeCheck() && next != startingPreset)
            {
                startingPreset = next;
                plan = PresetByIndex(next);
            }
        }

        private void DrawPipelineSection()
        {
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                plan.convertMaterials = EditorGUILayout.ToggleLeft(
                    new GUIContent("Convert materials to DreamPark shaders",
                        "Routes each material through MaterialConverter: particle materials to "
                        + "DreamPark/Particles, everything else to DreamPark-UniversalShader."),
                    plan.convertMaterials);

                using (new EditorGUI.DisabledScope(!plan.convertMaterials))
                {
                    plan.extractEmbeddedMaterials = EditorGUILayout.ToggleLeft(
                        new GUIContent("Extract embedded materials first",
                            "Materials living inside an FBX are read-only, so they cannot be "
                            + "converted in place. This pulls them out into a Materials folder."),
                        plan.extractEmbeddedMaterials);
                }

                plan.fitScale = EditorGUILayout.ToggleLeft(
                    new GUIContent("Fit scale",
                        "Measures the asset and asks you to confirm its real-world size. "
                        + "Never rescales silently."),
                    plan.fitScale);

                plan.groundPivot = EditorGUILayout.ToggleLeft(
                    new GUIContent("Ground the pivot",
                        "Drops the model so its base sits at local zero. Deterministic — this "
                        + "is what makes a prop stand on the floor when a park places it."),
                    plan.groundPivot);

                plan.collider = (ColliderChoice)EditorGUILayout.EnumPopup(
                    new GUIContent("Collider",
                        "Auto runs the complexity ladder: box, sphere, capsule, then mesh. "
                        + "Primitives are close to free on Quest and mesh colliders are not."),
                    plan.collider);

                if (plan.RequiresConvexCollider && plan.collider == ColliderChoice.MeshStatic)
                {
                    EditorGUILayout.HelpBox(
                        "This preset adds a Rigidbody, and Unity silently disables a non-convex "
                        + "MeshCollider on a moving body — the prop will fall through the floor. "
                        + "Use ConvexMesh instead.",
                        MessageType.Error);
                }

                plan.category = (PropCategory)EditorGUILayout.EnumPopup("Prop category", plan.category);
                plan.affectsGapFiller = EditorGUILayout.ToggleLeft(
                    new GUIContent("Affects gap filler",
                        "Whether this prop's footprint takes part in floor generation. Turn off "
                        + "for anything that floats."),
                    plan.affectsGapFiller);
            }
        }

        private void DrawBehaviorSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Behavior", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                plan.behavior = (BehaviorPack)EditorGUILayout.EnumPopup("Pack", plan.behavior);
                plan.addRigidbody = EditorGUILayout.ToggleLeft("Add Rigidbody", plan.addRigidbody);

                if (plan.behavior == BehaviorPack.Bendy)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Bend", EditorStyles.miniBoldLabel);
                    plan.bend.maxTiltAngle = EditorGUILayout.Slider("Max tilt (°)", plan.bend.maxTiltAngle, 2f, 80f);
                    plan.bend.springStrength = EditorGUILayout.FloatField("Spring", plan.bend.springStrength);
                    plan.bend.springDamping = EditorGUILayout.FloatField("Damping", plan.bend.springDamping);
                    plan.bend.minDetectionRadius = EditorGUILayout.FloatField(
                        new GUIContent("Min detect radius (m)",
                            "The real radius is fitted to the object's footprint; this is the floor. "
                            + "EasyBend ships at 0.75 m, which is a metre-wide trigger around a coin."),
                        plan.bend.minDetectionRadius);

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Jiggle rig (rigged models only)", EditorStyles.miniBoldLabel);
                    EditorGUILayout.HelpBox(
                        "A rigged model gets a jointed jiggle rig instead of a transform bend — "
                        + "EasyBend rotates one transform, which on a skinned mesh swings the whole "
                        + "character rigidly and looks broken.",
                        MessageType.None);

                    // Stiff and Floppy are not invented numbers. They are the
                    // two calibration points from shipped CrashCourse prefabs:
                    // E_Pylon/E_Arch at 5,000,000 and E_FlipGate at 3,000.
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel("Stiffness");
                        if (GUILayout.Button("Stiff", EditorStyles.miniButtonLeft))
                            plan.bend.jointSpring = BendSettings.SpringStiff;
                        if (GUILayout.Button("Floppy", EditorStyles.miniButtonRight))
                            plan.bend.jointSpring = BendSettings.SpringFloppy;
                    }
                    plan.bend.jointSpring = EditorGUILayout.FloatField("Joint spring", plan.bend.jointSpring);
                    plan.bend.jointMaxForce = EditorGUILayout.FloatField("Joint max force", plan.bend.jointMaxForce);
                    plan.bend.boneRadius = EditorGUILayout.FloatField("Bone radius", plan.bend.boneRadius);
                    plan.bend.colliderSpacing = EditorGUILayout.FloatField(
                        new GUIContent("Collider spacing",
                            "Shrinks each bone collider. NEGATIVE deliberately overlaps neighbours — "
                            + "E_FlipGate uses -0.08 to keep its panel gap-free."),
                        plan.bend.colliderSpacing);
                    plan.bend.archway = EditorGUILayout.ToggleLeft(
                        new GUIContent("Archway",
                            "Split the bone list in half, rig each half, then hinge the free ends. "
                            + "For a mesh that is one skeleton describing two legs."),
                        plan.bend.archway);
                }

                if (plan.behavior == BehaviorPack.Shatter)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Fracture", EditorStyles.miniBoldLabel);
                    plan.fracture.pieces = EditorGUILayout.IntSlider("Pieces", plan.fracture.pieces, 2, 64);
                    if (plan.fracture.pieces > FractureSettings.WarnAbovePieces)
                    {
                        EditorGUILayout.HelpBox(
                            "Every piece is a draw call and a rigidbody. Above "
                            + FractureSettings.WarnAbovePieces + " you are paying for it on device.",
                            MessageType.Warning);
                    }
                    plan.fracture.insideMaterial = (Material)EditorGUILayout.ObjectField(
                        new GUIContent("Inside material", "Applied to the cut faces."),
                        plan.fracture.insideMaterial, typeof(Material), false);
                    plan.fracture.capUvScale = EditorGUILayout.FloatField("Cap UV scale", plan.fracture.capUvScale);
                    plan.fracture.burstImpulse = EditorGUILayout.FloatField("Burst impulse", plan.fracture.burstImpulse);
                    plan.fracture.pieceLifetime = EditorGUILayout.FloatField(
                        new GUIContent("Piece lifetime (s)", "0 keeps the pieces forever."),
                        plan.fracture.pieceLifetime);
                }
            }
        }

        private void DrawPlaneSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Plane", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                plan.plane.billboard = (BillboardMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Billboard",
                        "None is the default — a plain plane facing where you point it. "
                        + "Billboarding is an effect; a plane is the thing itself."),
                    plan.plane.billboard);

                plan.plane.heightMeters = EditorGUILayout.FloatField(
                    new GUIContent("Height (m)", "Width follows from the texture's aspect ratio."),
                    plan.plane.heightMeters);

                plan.plane.alphaClip = EditorGUILayout.ToggleLeft(
                    new GUIContent("Alpha clip",
                        "Cutout rather than blend: no sort order to get wrong, no per-pixel "
                        + "blend cost. Right for foliage and characters."),
                    plan.plane.alphaClip);

                using (new EditorGUI.DisabledScope(plan.plane.billboard != BillboardMode.None))
                {
                    plan.plane.twoSided = EditorGUILayout.ToggleLeft(
                        new GUIContent("Two-sided",
                            "A single-sided plane is invisible from behind. Billboarding hides "
                            + "that; a static plane does not."),
                        plan.plane.twoSided);
                }

                if (plan.plane.billboard == BillboardMode.Full && plan.affectsGapFiller)
                {
                    EditorGUILayout.HelpBox(
                        "A centre-billboarded plane floats, so it usually should not affect the "
                        + "gap filler. Consider turning that off above.",
                        MessageType.Info);
                }
            }
        }

        private void DrawAudioSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Audio", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The audio track is provisional — its design was never confirmed. Check the "
                + "result before relying on it.",
                MessageType.Warning);
            using (new EditorGUI.IndentLevelScope())
            {
                plan.audio.looping = EditorGUILayout.ToggleLeft("Looping ambience", plan.audio.looping);
                plan.audio.audibleRadius = EditorGUILayout.FloatField("Audible radius (m)", plan.audio.audibleRadius);
                plan.audio.volume = EditorGUILayout.Slider("Volume", plan.audio.volume, 0f, 1f);
                plan.audio.normalizeImportSettings = EditorGUILayout.ToggleLeft(
                    new GUIContent("Normalize import settings",
                        "Mono for 3D positional audio, Vorbis compression, and load type "
                        + "chosen by clip length."),
                    plan.audio.normalizeImportSettings);
            }
        }

        private void DrawSharedPropSection()
        {
            EditorGUILayout.LabelField("Shared prop family", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                plan.shared.enabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("Create as a variant family",
                        "One base prefab holding everything except the model, plus one prefab "
                        + "variant per selected model. Change the base, every variant follows."),
                    plan.shared.enabled);

                using (new EditorGUI.DisabledScope(!plan.shared.enabled))
                {
                    plan.shared.familyName = EditorGUILayout.TextField("Family name", plan.shared.familyName);
                    plan.shared.shareCollider = EditorGUILayout.ToggleLeft(
                        new GUIContent("Share one collider",
                            "Fits a single collider to the union of every model's bounds. Correct "
                            + "when the family is genuinely uniform, visibly wrong when it is not."),
                        plan.shared.shareCollider);

                    if (plan.shared.shareCollider)
                    {
                        EditorGUILayout.HelpBox(
                            "Every variant will use the same collider, sized to the largest model.",
                            MessageType.Info);
                    }
                }
            }
        }

        private void DrawButtons()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(90)))
                {
                    Close();
                }
                if (GUILayout.Button("Convert", GUILayout.Width(110)))
                {
                    confirmed = true;
                    var result = plan;
                    var cb = onConfirm;
                    Close();
                    if (cb != null) cb(result);
                }
                GUILayout.Space(4);
            }
            EditorGUILayout.Space(6);
        }

        private void OnDestroy()
        {
            // Closing without pressing Convert must not run anything. The
            // callback is only ever invoked from the button.
            if (!confirmed) onConfirm = null;
        }
    }
}
#endif
