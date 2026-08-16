#if UNITY_EDITOR && !DREAMPARKCORE
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    public enum ModelKind
    {
        StaticProp,
        RiggedCharacter,
    }

    /// <summary>
    /// The import settings a Dreamie mesh arrives with.
    ///
    /// ── WHY THIS IS AN EXPLICIT PASS AND NOT AN AssetPostprocessor ─────────
    ///
    /// An OnPreprocessModel hook fires for EVERY model in the project and is
    /// scoped only by a path predicate you have to keep correct forever. That
    /// is the wrong default for something shipped to third parties: the failure
    /// mode is silently rewriting the import settings of a creator's own art.
    /// Worse, Unity reimports every asset a postprocessor is interested in
    /// whenever its GetVersion() changes — so tuning one number in this table
    /// would force a full reimport of every FBX in every creator's project —
    /// and it would revert, on the next reimport, any setting a creator changed
    /// by hand. "Ready to go on arrival" is the goal; "permanently frozen" is
    /// not.
    ///
    /// An explicit pass scopes itself by construction: it only touches paths it
    /// was handed. And nothing is lost by it — ModelImporter settings serialize
    /// into the .meta, so they survive reimports, Library wipes and platform
    /// switches exactly as a postprocessor's would. Assets dragged in by hand
    /// are covered by the right-click menu item.
    ///
    /// ── WHERE THESE NUMBERS COME FROM ──────────────────────────────────────
    ///
    /// Dreamie's own generation config, not guesswork:
    ///   • face_limit 5000 and topology "triangle" (dreamie/config.js) — these
    ///     are already budget meshes, which is why mesh compression is OFF
    ///     below rather than Low. At 5k triangles quantisation saves a rounding
    ///     error of bundle size and risks shading seams; the textures are three
    ///     orders of magnitude more expensive and are dealt with in
    ///     DreamieTextureSetup.
    ///   • auto_size true — "Scale the mesh to real-world metres... does not
    ///     change geometry, only the transform." So the file's own unit scale
    ///     is meaningful and useFileScale honours it.
    ///   • pivot_to_center_bottom true — props arrive with their origin at the
    ///     base, which is what you want for something standing on a floor.
    /// </summary>
    public class ModelImportProfile
    {
        public ModelKind kind;

        public static ModelImportProfile For(ModelKind kind)
            => kind == ModelKind.RiggedCharacter ? RiggedCharacter : StaticProp;

        public static readonly ModelImportProfile StaticProp =
            new ModelImportProfile { kind = ModelKind.StaticProp };

        public static readonly ModelImportProfile RiggedCharacter =
            new ModelImportProfile { kind = ModelKind.RiggedCharacter };

        /// <summary>
        /// Apply the profile. Does NOT call SaveAndReimport — the caller owns
        /// that, because it usually has more to change first and every
        /// reimport of an FBX is expensive.
        /// </summary>
        public void ApplyTo(ModelImporter importer)
        {
            if (importer == null) return;

            bool rigged = kind == ModelKind.RiggedCharacter;

            /* ── materials ─────────────────────────────────────────────────
             * ImportViaMaterialDescription is the only mode that runs URP's
             * FBX material preprocessors, which is what turns the file's own
             * texture bindings into something usable. ImportStandard produces
             * built-in-pipeline materials, which are magenta under URP.
             *
             * Materials stay InPrefab here and are extracted explicitly later
             * with AssetDatabase.ExtractAsset — that is what Unity's own
             * "Extract Materials..." button calls, and it lets us choose the
             * destination path. materialLocation = External cedes that choice
             * to Unity, and deterministic paths matter because every file in
             * this asset has to carry the unique stem (see DreamieFolders).
             *
             * materialSearch = Local, never Everywhere: a recursive search lets
             * a Dreamie import silently bind to some other asset pack's
             * material that happens to share a name.
             */
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            importer.materialSearch = ModelImporterMaterialSearch.Local;

            /* ── mesh ──────────────────────────────────────────────────── */

            importer.meshCompression = ModelImporterMeshCompression.Off;
            // A readable mesh keeps a full CPU-side copy — roughly double the
            // memory, which is real on Quest. The exception is anything headed
            // for DinoFracture, which needs CPU vertex access; that is a
            // per-asset decision, not a default.
            importer.isReadable = false;
            // Reorders vertices and indices for post-transform cache locality.
            // No visual change; measurable on tile-based mobile GPUs.
            //
            // These two bools, not meshOptimizationFlags. They are the
            // accessors Unity added alongside that flags enum (the thing they
            // replaced is the older single `optimizeMesh`), they say exactly
            // what they do, and they avoid depending on which namespace
            // MeshOptimizationFlags lives in — a detail that is easy to get
            // wrong and whose blast radius is the whole editor assembly
            // failing to compile.
            importer.optimizeMeshPolygons = true;
            importer.optimizeMeshVertices = true;
            // Unity's default. Welding runs before the normal/UV split, so hard
            // edges and seams survive it; turning it off just inflates the
            // vertex count.
            importer.weldVertices = true;
            importer.keepQuads = false;
            importer.indexFormat = ModelImporterIndexFormat.Auto;

            // Trust the exporter's normals. AI-generated geometry can ship
            // degenerate ones — if that turns out to be common, Calculate with
            // a 60 degree smoothing angle is the fallback, and the import
            // report is where it would show up first.
            importer.importNormals = ModelImporterNormals.Import;
            // MikkTSpace is the basis normal maps are baked against. Import is
            // only correct if the exporter also used MikkTSpace, which is not
            // knowable from here.
            importer.importTangents = ModelImporterTangents.CalculateMikk;

            // Lightmap UV unwrapping is slow at import and costs a UV channel.
            // DreamPark is passthrough MR with realtime lighting.
            importer.generateSecondaryUV = false;
            // Colliders come from PropTemplate / AttractionTemplate authoring.
            // An auto-added MeshCollider per prop is a Quest performance trap.
            importer.addCollider = false;

            /* ── things that must never come in ────────────────────────────
             * A stray Camera inside a prop prefab is a genuine runtime hazard
             * in VR, and a stray realtime light is a measurable frame cost that
             * PreUploadChecks already polices at the scene level. Exporters
             * routinely include both. Neither is ever wanted.
             *
             * importVisibility off because an FBX visibility flag becomes
             * MeshRenderer.enabled = false — a DCC-hidden helper object
             * arriving invisible reads as a broken asset.
             */
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importConstraints = false;

            importer.preserveHierarchy = false;
            // Deterministic child order. This one is load-bearing for
            // multiplayer: NetId.ComputeId hashes sibling index below a
            // NetScope, so a hierarchy whose order varies between reimports
            // would drift NetIds between two devices' builds.
            importer.sortHierarchyByName = true;

            /* ── scale ─────────────────────────────────────────────────────
             * useFileScale honours the unit metadata the exporter wrote, which
             * is exactly what Dreamie's auto_size is expressing. globalScale
             * stays 1: any other value is a per-exporter fudge factor that will
             * be wrong for the next exporter.
             *
             * If models arrive lying on their side, bakeAxisConversion is the
             * knob — a Z-up source lands rotated -90 on X. It is left off
             * because Dreamie's FBX comes out of a glTF (Y-up) conversion, and
             * the import report's bounds check is what would catch it being
             * wrong.
             */
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.bakeAxisConversion = false;

            /* ── rig and animation ─────────────────────────────────────── */

            if (rigged)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = true;
                importer.animationCompression = ModelImporterAnimationCompression.Optimal;
                importer.skinWeights = ModelImporterSkinWeights.Standard;
                importer.importBlendShapes = true;
                // FBX blend shape normals are frequently absent or wrong.
                importer.importBlendShapeNormals = ModelImporterNormals.Calculate;
                // Strips the transform hierarchy Unity does not need to
                // animate. Leave it off: DreamPark content routinely parents
                // things to bones (a held prop, an attached effect), and
                // optimizeGameObjects deletes exactly those Transforms.
                importer.optimizeGameObjects = false;
            }
            else
            {
                importer.animationType = ModelImporterAnimationType.None;
                importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
                importer.importAnimation = false;
                importer.importBlendShapes = false;
            }
        }

        /// <summary>
        /// Static prop or rigged character, decided from what actually came in
        /// rather than from the asset's tags — a mesh tagged "character" that
        /// arrived without a skeleton is still a static prop, and importing it
        /// as Generic would build an avatar out of nothing.
        /// </summary>
        public static ModelKind Detect(string modelAssetPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            if (go != null && go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                return ModelKind.RiggedCharacter;

            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(modelAssetPath))
                if (obj is AnimationClip) return ModelKind.RiggedCharacter;

            return ModelKind.StaticProp;
        }
    }
}
#endif
