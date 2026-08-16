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
using System;
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
            // Deliberately empty. The previews are rendered from the first
            // OnGUI Repaint instead: OnEnable can run while the AssetDatabase
            // is still importing the very model we want to draw, and a preview
            // rendered from a half-imported mesh is a blank square with no way
            // to tell it apart from a real one.
        }

        // Bounded retry. RenderOrientation returns null while the
        // AssetDatabase is mid-refresh, and a modal window with no mouse input
        // generates no further repaints — so without an explicit Repaint the
        // dialog can sit on "no preview" forever. Bounded because the other
        // way to fail (no shader, an exception) never recovers, and retrying
        // that on every repaint means two full preview renders per mouse move.
        private const int PreviewAttempts = 8;
        private int previewTries;

        void EnsurePreviews()
        {
            if (previewSource == null) return;
            if (previewAsIs != null && previewRotated != null) return;
            if (previewTries >= PreviewAttempts) return;

            previewTries++;
            if (previewAsIs == null)
                previewAsIs = RenderOrientation(previewSource, Quaternion.identity);
            if (previewRotated == null)
                previewRotated = RenderOrientation(previewSource, OrientationFitter.ZUpToYUpRotation);

            if (previewAsIs == null || previewRotated == null) Repaint();
        }

        private void OnGUI()
        {
            // Only when the orientation section is actually on screen. The
            // common dialog is a unit-scale question with no up-axis suspicion,
            // and rendering two 256×256 previews nobody sees costs two
            // PreviewRenderUtility builds and two GPU read-backs.
            if (Event.current.type == EventType.Repaint && guessedUpAxis != UpAxisFix.None)
                EnsurePreviews();

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
                    EditorGUI.LabelField(box, "no preview", EditorStyles.centeredGreyMiniLabel);
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
            if (previewAsIs != null) DestroyImmediate(previewAsIs);
            if (previewRotated != null) DestroyImmediate(previewRotated);
            previewAsIs = null;
            previewRotated = null;
        }

        static Texture2D RenderOrientation(GameObject source, Quaternion rotation)
        {
            if (source == null) return null;
            // Draw the already-loaded meshes rather than instantiating the FBX:
            // this window opens in the middle of the convert run, and adding a
            // scene object built from an asset that is still being imported is
            // a reliable way to make Unity re-enter the importer underneath us.
            //
            // (The fatal "Copying Resources/unity_builtin_extra to
            // Temp/copyassets" dialog that showed up around here was NOT this —
            // it was AssetDatabase.CopyAsset being handed a built-in material's
            // pseudo-path in AssetClassifier.PackDependenciesIntoKit. See
            // AssetClassifier.IsWritableProjectAsset.)
            if (EditorApplication.isUpdating) return null;

            var util = new PreviewRenderUtility();
            Material mat = null;
            try
            {
                // Lit, so the two lights below actually do something. An unlit
                // preview is a single flat silhouette, and "is this standing up
                // or lying on its side" is exactly the question a silhouette
                // cannot answer for anything roughly symmetric.
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return null;
                mat = new Material(shader);
                mat.hideFlags = HideFlags.HideAndDontSave;
                mat.color = new Color(0.82f, 0.82f, 0.84f, 1f);
                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", mat.color);

                util.camera.clearFlags = CameraClearFlags.SolidColor;
                util.camera.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1f);
                util.ambientColor = new Color(0.55f, 0.55f, 0.55f, 1f);
                util.lights[0].intensity = 1.4f;
                util.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                util.lights[1].intensity = 0.6f;

                Matrix4x4 orient = Matrix4x4.Rotate(rotation);
                Matrix4x4 toRoot = source.transform.worldToLocalMatrix;

                Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
                bool any = false;
                CollectMeshes(source, orient * toRoot, delegate (Mesh mesh, Matrix4x4 matrix)
                {
                    Bounds mb = TransformBounds(mesh.bounds, matrix);
                    if (!any) { bounds = mb; any = true; }
                    else bounds.Encapsulate(mb);
                });
                if (!any) bounds.extents = Vector3.one * 0.5f;

                // Fit the bounding SPHERE, not its half-height. At fov 30 the
                // visible half-height at distance d is d·tan(15°) = 0.268d, so
                // the old 2.55r put only 0.68r on screen while the content
                // reaches r — every tall model was cropped top and bottom, in
                // both thumbnails, which is precisely the cue the creator is
                // being asked to judge. r/sin(fov/2) is the fit; ×1.1 is margin.
                const float fov = 30f;
                float radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
                float distance = radius / Mathf.Sin(fov * 0.5f * Mathf.Deg2Rad) * 1.1f;
                Vector3 dir = new Vector3(0.65f, 0.42f, -0.72f).normalized;
                util.camera.fieldOfView = fov;
                util.camera.transform.position = bounds.center + dir * distance;
                util.camera.transform.LookAt(bounds.center);
                util.camera.nearClipPlane = Mathf.Max(0.001f, radius * 0.02f);
                util.camera.farClipPlane = Mathf.Max(util.camera.nearClipPlane + 1f, distance + radius * 4f);

                const int size = 256;

                // BeginStaticPreview repoints the GUI's render target, viewport
                // and scissor, and ONLY EndStaticPreview puts them back. A throw
                // in between (a URP preview camera missing its additional data, a
                // mesh with no vertex buffer) would otherwise reach Cleanup(),
                // which destroys the RenderTexture while it is still
                // RenderTexture.active — "Releasing render texture that is set as
                // RenderTexture.active!" and a garbled editor for the rest of the
                // repaint. Hence its own try/finally.
                //
                // Do NOT set camera.pixelRect after this call. BeginStaticPreview
                // supersamples: it allocates the target at size × scaleFactor (2×,
                // or 4× on retina) and sets pixelRect to match. Overriding it back
                // to `size` drew the model into the bottom-left quadrant of a
                // mostly empty square, which then got blitted down whole.
                bool begun = false;
                try
                {
                    util.BeginStaticPreview(new Rect(0, 0, size, size));
                    begun = true;

                    CollectMeshes(source, orient * toRoot, delegate (Mesh mesh, Matrix4x4 matrix)
                    {
                        for (int sub = 0; sub < mesh.subMeshCount; sub++)
                            util.DrawMesh(mesh, matrix, mat, sub);
                    });

                    util.Render(true);
                    Texture2D shot = util.EndStaticPreview();
                    begun = false;
                    return shot;
                }
                finally
                {
                    // Balance the pair even on the way out of an exception, so
                    // the editor's render state is restored before Cleanup runs.
                    if (begun)
                    {
                        Texture2D discard = util.EndStaticPreview();
                        if (discard != null) DestroyImmediate(discard);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Convert Ready] orientation preview failed: " + e.Message);
                return null;
            }
            finally
            {
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                util.Cleanup();
            }
        }

        delegate void MeshConsumer(Mesh mesh, Matrix4x4 matrix);

        static void CollectMeshes(GameObject root, Matrix4x4 rootXform, MeshConsumer consume)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                consume(mf.sharedMesh, rootXform * mf.transform.localToWorldMatrix);
            }

            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                SkinnedMeshRenderer smr = skins[i];
                if (smr == null || smr.sharedMesh == null) continue;
                consume(smr.sharedMesh, rootXform * smr.transform.localToWorldMatrix);
            }
        }

        static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            Vector3 c = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            Vector3 axisX = m.MultiplyVector(new Vector3(e.x, 0, 0));
            Vector3 axisY = m.MultiplyVector(new Vector3(0, e.y, 0));
            Vector3 axisZ = m.MultiplyVector(new Vector3(0, 0, e.z));
            Vector3 worldE = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(c, worldE * 2f);
        }
    }
}
#endif
