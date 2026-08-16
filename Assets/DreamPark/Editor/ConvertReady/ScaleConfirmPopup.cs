// ─────────────────────────────────────────────────────────────────────
//  ScaleConfirmPopup.cs — the one question the creator is better placed to
//  answer than the tool
//
//  The 1-vs-100 problem cannot be solved by measurement alone. A genuinely
//  200 m skybox dome and a 2 m statue authored in centimetres both measure
//  200 units. ModelImporter.fileScale is the better signal — Unity parses the
//  FBX header's unit declaration into it — but it is frequently absent and
//  occasionally lying, so it cannot be trusted alone either.
//
//  Rather than pick a heuristic and be silently wrong on some fraction of
//  imports forever, we show the measurement and ask. The cost is one dialog.
//  The saving is never having to explain why the tool made someone's tree
//  100× too big and then wrote a prefab about it.
//
//  Two things make this dialog answerable in a second rather than a minute:
//
//   1. A HUMAN REFERENCE. "2.3 m" is a number a creator has to stop and
//      picture. "2.3 m — about a door" is not. The comparison is doing real
//      cognitive work, not decoration.
//   2. THE UP-AXIS PREVIEW. Z-up-vs-Y-up genuinely cannot be detected from
//      geometry — the bounds heuristic works for trees and fails for rugs.
//      Two thumbnails side by side answer it instantly and no heuristic can.
//
//  Skipped entirely when confidence is Plausible AND the up-axis guess is
//  None: there is nothing to ask, and a dialog with no question in it is how
//  you train people to click through the ones that matter.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public class ScaleConfirmPopup : EditorWindow
    {
        public struct Answer
        {
            public bool cancelled;
            public bool applyScale;

            /// <summary>
            /// A REAL-WORLD size — the union of every renderer's bounds along
            /// its longest axis, which is what the creator sees and types.
            ///
            /// It is NOT a PrefabScaler.ScaleToFit argument. ScaleToFit divides
            /// by the chosen axis of the FIRST renderer's mesh-local bounds,
            /// which on a multi-part model is a different quantity entirely —
            /// off by their ratio, 20× or 40× on a rug or a car. The caller
            /// must convert with ScaleInference.TargetForVisibleSize before
            /// scaling. Every number in this window is in this space.
            /// </summary>
            public float desiredVisibleMeters;

            public PrefabScalerAxis axis;
            public UpAxisFix upAxis;
        }

        private string assetName;
        private ScaleDecision decision;
        private UpAxisFix guessedUpAxis;
        private string upAxisReason;
        private GameObject previewSource;

        private bool applyScale;
        private float desiredVisibleMeters;   // VISIBLE space — see Answer
        private PrefabScalerAxis axis = PrefabScalerAxis.Y;
        private UpAxisFix chosenUpAxis;

        private Texture2D previewAsIs;
        private Texture2D previewRotated;

        private Answer answer;
        private bool answered;

        /// <summary>
        /// True when there is genuinely nothing to ask. The executor should
        /// skip the dialog entirely rather than showing an empty one.
        /// </summary>
        public static bool NeedsConfirmation(ScaleDecision d, UpAxisFix guessedUpAxis)
        {
            return d.confidence != ScaleConfidence.Plausible || guessedUpAxis != UpAxisFix.None;
        }

        /// <summary>
        /// Modal. Returns the creator's answer, or Answer.cancelled when they
        /// backed out — in which case the executor must abort THIS asset and
        /// write nothing, not fall through to a default.
        /// </summary>
        public static Answer Ask(string assetName, ScaleDecision d, UpAxisFix guessedUpAxis,
                                 string upAxisReason, GameObject previewSource)
        {
            var w = CreateInstance<ScaleConfirmPopup>();
            w.titleContent = new GUIContent("Confirm size and orientation");
            w.assetName = assetName;
            w.decision = d;
            w.guessedUpAxis = guessedUpAxis;
            w.upAxisReason = upAxisReason;
            w.previewSource = previewSource;

            w.chosenUpAxis = guessedUpAxis;
            w.applyScale = d.confidence == ScaleConfidence.LikelyUnitError;
            w.desiredVisibleMeters = w.applyScale ? SuggestTarget(d) : d.measuredMeters;
            w.axis = d.axis;

            w.answer = new Answer { cancelled = true };
            w.minSize = new Vector2(460, 340);
            w.ShowModal();
            return w.answer;
        }

        /// <summary>
        /// When the file declares centimetres, the honest suggestion is
        /// measured/100. When it declares nothing and the object is absurd,
        /// /100 is still the overwhelmingly common authoring error — but this
        /// is a SUGGESTION in an editable field, never an applied value.
        /// </summary>
        private static float SuggestTarget(ScaleDecision d)
        {
            if (d.fileScale > 0f && d.fileScale < 0.5f) return d.measuredMeters * d.fileScale;
            if (d.measuredMeters > 500f) return d.measuredMeters / 100f;
            if (d.measuredMeters < 0.005f) return d.measuredMeters * 100f;
            return d.measuredMeters;
        }

        private void OnEnable()
        {
            if (previewSource != null)
            {
                previewAsIs = AssetPreview.GetAssetPreview(previewSource);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(assetName, EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawSizeSection();

            if (guessedUpAxis != UpAxisFix.None)
            {
                EditorGUILayout.Space(8);
                DrawOrientationSection();
            }

            GUILayout.FlexibleSpace();
            DrawButtons();
        }

        private void DrawSizeSection()
        {
            EditorGUILayout.LabelField("Size", EditorStyles.boldLabel);

            string measured = decision.measuredMeters.ToString("0.###") + " m";
            if (!string.IsNullOrEmpty(decision.humanReference))
                measured += "  —  " + decision.humanReference;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("As authored", measured);

                if (decision.fileScale > 0f)
                {
                    EditorGUILayout.LabelField("File declares",
                        Mathf.Approximately(decision.fileScale, 0.01f)
                            ? "centimetres (fileScale 0.01)"
                            : "fileScale " + decision.fileScale.ToString("0.####"));
                }
                else
                {
                    EditorGUILayout.LabelField("File declares", "nothing — measured only");
                }
            }

            EditorGUILayout.Space(4);

            switch (decision.confidence)
            {
                case ScaleConfidence.LikelyUnitError:
                    EditorGUILayout.HelpBox(
                        "That size is almost certainly a unit error. The suggested fix is "
                        + "pre-selected below — check it looks right before converting.",
                        MessageType.Warning);
                    break;
                case ScaleConfidence.Ambiguous:
                    EditorGUILayout.HelpBox(
                        "This could be a large object authored in metres, or a small one "
                        + "authored in centimetres. Nothing in the file settles it.",
                        MessageType.Info);
                    break;
            }

            applyScale = EditorGUILayout.ToggleLeft("Resize this asset", applyScale);

            using (new EditorGUI.DisabledScope(!applyScale))
            using (new EditorGUI.IndentLevelScope())
            {
                desiredVisibleMeters = EditorGUILayout.FloatField(
                    new GUIContent("Target size (m)",
                        "The finished prop's real-world size along its longest axis."),
                    desiredVisibleMeters);
                axis = (PrefabScalerAxis)EditorGUILayout.EnumPopup(
                    new GUIContent("Measured along",
                        "Which axis the target applies to. Y for characters and most props, "
                        + "Z for weapons. Scaling stays uniform either way — the model cannot warp."),
                    axis);
            }
        }

        private void DrawOrientationSection()
        {
            EditorGUILayout.LabelField("Orientation", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "This looks like it was authored Z-up" +
                (string.IsNullOrEmpty(upAxisReason) ? "" : " (" + upAxisReason + ")") +
                ". That is a guess — geometry alone cannot settle it. Pick whichever "
                + "looks right.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawOrientationChoice("Leave as authored", UpAxisFix.None, previewAsIs);
                GUILayout.Space(8);
                DrawOrientationChoice("Stand it up (Z-up → Y-up)", UpAxisFix.ZUpToYUp, previewRotated);
            }
        }

        private void DrawOrientationChoice(string label, UpAxisFix value, Texture2D preview)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(200)))
            {
                bool selected = chosenUpAxis == value;

                var box = GUILayoutUtility.GetRect(180, 110);
                if (preview != null)
                {
                    GUI.DrawTexture(box, preview, ScaleMode.ScaleToFit);
                }
                else
                {
                    // A preview is not always available — AssetPreview is
                    // asynchronous and returns null until Unity has rendered
                    // one. Say so rather than showing an empty box that reads
                    // as "this option produces nothing".
                    EditorGUI.LabelField(box, "preview rendering…", EditorStyles.centeredGreyMiniLabel);
                    Repaint();
                }

                if (EditorGUILayout.ToggleLeft(label, selected) && !selected)
                {
                    chosenUpAxis = value;
                }
            }
        }

        private void DrawButtons()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(90)))
                {
                    answer = new Answer { cancelled = true };
                    answered = true;
                    Close();
                }
                if (GUILayout.Button("Convert", GUILayout.Width(110)))
                {
                    answer = new Answer
                    {
                        cancelled = false,
                        applyScale = applyScale,
                        desiredVisibleMeters = desiredVisibleMeters,
                        axis = axis,
                        upAxis = chosenUpAxis,
                    };
                    answered = true;
                    Close();
                }
            }
            EditorGUILayout.Space(8);
        }

        private void OnDestroy()
        {
            // Closing the window with the title-bar X is a cancel, not a
            // silent accept. Without this a stray click writes prefabs the
            // creator never agreed to.
            if (!answered) answer = new Answer { cancelled = true };
        }
    }
}
#endif
