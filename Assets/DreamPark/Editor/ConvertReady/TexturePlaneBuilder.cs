// ─────────────────────────────────────────────────────────────────────
//  TexturePlaneBuilder.cs — the texture track. PNG in, placeable prop out.
//
//  WHY THIS FILE EXISTS
//
//  For a 2D artist this is the entire feature: the shortest path from a
//  drawing to something standing in a park. Everything it does is something
//  a creator would otherwise have to know: that Unity's built-in Quad is
//  1×1 and must be scaled non-uniformly, that a cutout needs alpha clip
//  rather than alpha blend, that clamp wrap is what stops the edge column
//  bleeding across the transparent border, that a single-sided plane
//  disappears when you walk behind it.
//
//  FOUR DECISIONS HERE ARE LOAD-BEARING. Each one is cheap to get wrong
//  and expensive to notice.
//
//  1. WE GENERATE A REAL QUAD AT THE TEXTURE'S ASPECT rather than
//     non-uniformly scaling a shared unit quad. A (1.78, 1, 1) localScale
//     would work and would poison three things downstream: PrefabScaler
//     resets localScale to identity to measure, so a re-run would silently
//     restore a square poster; PropTemplate's footprint numbers stop
//     matching what the inspector shows; and any collider fitted under a
//     non-uniform parent is skewed. Uniform scale everywhere is a property
//     worth a generated mesh. The mesh is a prefab SUB-ASSET — loose .asset
//     meshes are how a content folder ends up with 300 orphaned files nobody
//     dares delete, and how addressable groups churn on every re-bake.
//
//  2. THE PLANE IS ROOTED AT ITS BASE, not its centre. A poster stands on
//     the floor like every other prop. Stage 3a's ground-pivot rule is not
//     given an exception for textures, because the moment one track pivots
//     differently, GapFiller heights and SurfaceHeight mean two things.
//     (Mode 3, the full billboard, still ROTATES about the sprite's centre —
//     see ApplyMotionOffset, which deliberately deviates from the spec's
//     mode-3 table row and says so at its call site.)
//
//  3. THE VISIBLE FACE IS -Z, normals (0,0,-1), u increasing with +X. That
//     is exactly Unity's built-in Quad and SpriteRenderer convention, so the
//     texture reads left-to-right instead of mirrored and a creator's
//     intuition transfers. DreamPark.Billboard is written against the same
//     convention and points its forward AWAY from the head.
//
//  4. THE SOURCE TEXTURE IS SOMEBODY ELSE'S ASSET TOO. NormalizeTextureImport
//     used to reason carefully about why textureType must not be changed — "a
//     texture already imported as a Sprite is being used as a Sprite somewhere
//     else" — and then apply none of that reasoning to wrap mode, sRGB,
//     mipmaps or compression. Flipping wrap to Clamp is the one that bites:
//     the same PNG used as a tiling base map on a mesh material stops tiling
//     the moment somebody converts it to a plane, in a prefab nobody was
//     looking at. So the settings that change what the PIXELS MEAN are gated
//     on whether another material already references the texture, and when
//     they are gated the report names the other users instead of going quiet.
//     Only maxTextureSize is applied unconditionally, and only ever downward,
//     because the GPU budget genuinely is shared — that is the policy, stated.
//
//  THE ORDER IN AttachGeneratedAssets IS NOT INTERCHANGEABLE, AND IT IS NOT
//  WHAT IT USED TO BE
//
//  The old sequence was AddObjectToAsset → SaveAssets → LoadPrefabContents →
//  SaveAsPrefabAsset. Every step of that is wrong here:
//
//   • SaveAsPrefabAsset REPLACES the asset at the path, serializing from the
//     preview-scene copy. A sub-asset added out-of-band by AddObjectToAsset is
//     not in that object graph, so the mesh is dropped and the MeshFilter is
//     left pointing at an external reference into a file that no longer holds
//     it.
//   • The whole Build runs inside ConvertReadyExecutor's StartAssetEditing /
//     StopAssetEditing block, where AssetDatabase.SaveAssets() is a NO-OP. So
//     LoadPrefabContents was reading a file from disk that did not yet contain
//     the mesh.
//
//  Net effect: convert a PNG, come back after a domain reload, and MeshFilter
//  reads None while the report says the mesh was saved as a sub-asset. So the
//  second pass now edits the LOADED PREFAB ASSET in place and saves it with
//  PrefabUtility.SavePrefabAsset, the file is never rewritten from a copy, and
//  the last thing this file does is re-load the prefab and check that the
//  MeshFilter really does point at a mesh living inside it.
//
//  WHAT MODE 1 COSTS: NOTHING. BillboardMode.None is the default and is the
//  simplest path through the whole pipeline — no Billboard component, no
//  Motion node, a plain box collider, useColliderBounds = true. Billboarding
//  is an effect; a plane is the thing itself, and a plane that quietly turns
//  to follow the player is surprising in a way that is hard to undo once it
//  is placed across a park.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class TexturePlaneBuilder
    {
        // ── Shader ──────────────────────────────────────────────────────
        //
        // A Shader Graph's SHADER name is the graph's declared category path
        // plus the asset file name, NOT the file name alone.
        // DreamPark-Unlit.shadergraph declares m_Path "Shader Graphs", so
        // Shader.Find needs "Shader Graphs/DreamPark-Unlit". Read out of the
        // .shadergraph and cross-checked against MaterialConverter, which
        // holds the same two strings.

        public const string UnlitShaderName = "Shader Graphs/DreamPark-Unlit";
        public const string UnlitShaderAssetPath = "Assets/DreamPark/Shaders/DreamPark-Unlit.shadergraph";

        /// Reference names on the graph's exposed properties. Confirmed
        /// against MaterialConverter.ConvertMaterialToUnlit, which writes the
        /// same two.
        public const string BaseTexProperty = "_baseTex";
        public const string BaseColorProperty = "_baseColor";

        // ── Tuning ──────────────────────────────────────────────────────

        /// Quest-appropriate cap. Only ever lowered, never raised — a creator
        /// who deliberately set 2048 on a hero backdrop gets to keep it only
        /// if they set it above this, which they cannot, which is the point:
        /// a park is a hundred of these and the GPU budget is shared. This is
        /// the ONE import setting applied without asking whether the texture
        /// has other consumers, and that is a deliberate policy rather than an
        /// oversight — see decision 4 at the top of the file.
        public const int MaxTextureSize = 1024;

        /// Alpha below this is discarded. 0.5 is the neutral choice; foliage
        /// authored with soft edges usually wants it lower, which is one
        /// number in the material the creator can now see and change.
        public const float DefaultAlphaCutoff = 0.5f;

        /// Guards against a zero-height plane and a divide-by-zero aspect.
        public const float MinPlaneMeters = 0.01f;

        /// How many materials the shared-texture scan will look at before it
        /// gives up and says so. The scan is one FindAssets plus a direct (non-
        /// recursive) GetDependencies per material, which is cheap per item and
        /// still unbounded in a large project — and this runs per converted
        /// texture. Past this we report that we could not check rather than
        /// stalling a batch conversion.
        public const int MaxMaterialsScanned = 4000;

        /// Other users named in the report before it degrades to a count.
        public const int MaxNamedTextureUsers = 3;

        public const string MaterialsFolderName = "Materials";
        public const string MaterialPrefix = "M_";
        public const string MeshNamePrefix = "Plane_";

        // ── Entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Convert one texture asset into a P_*.prefab carrying the canonical
        /// hierarchy. Returns the saved prefab path, or null when nothing was
        /// written — every failure path appends to <paramref name="r"/> first.
        /// Never throws.
        /// </summary>
        public static string Build(string texturePath, ConversionPlan plan, ConversionResult r)
        {
            if (r == null) r = new ConversionResult { sourcePath = texturePath, ok = true };
            if (plan == null)
            {
                r.Failed("texture: no ConversionPlan");
                return null;
            }
            if (string.IsNullOrEmpty(texturePath))
            {
                r.Failed("texture: no source path");
                return null;
            }

            PlaneSettings plane = plan.plane != null ? plan.plane : new PlaneSettings();

            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null)
            {
                r.Failed("texture: '" + texturePath + "' has no TextureImporter — Unity has not ingested it as a texture");
                return null;
            }

            // ── Read the source BEFORE writing anything ─────────────────
            //
            // The no-alpha case in particular has to be reported before a
            // single asset is created. A creator who asked for a cut-out tree
            // and got an opaque rectangle, with no line in the report saying
            // why, files a bug against the converter and is right to.

            int texWidth, texHeight;
            ReadSourceSize(importer, texturePath, out texWidth, out texHeight, r);
            if (texWidth <= 0 || texHeight <= 0)
            {
                r.Failed("texture: could not read the dimensions of '" + texturePath + "'");
                return null;
            }

            bool hasAlpha = SourceHasAlpha(importer);
            bool alphaClip = plane.alphaClip;

            if (!hasAlpha)
            {
                r.Skipped(
                    "texture has NO ALPHA CHANNEL — the plane will be a solid rectangle, not a cut-out. "
                  + "Re-export as a PNG or TGA with transparency and re-run if you wanted a silhouette.");

                if (alphaClip)
                {
                    // Alpha-clipping an opaque texture costs a per-pixel
                    // discard and removes nothing, and it moves the material
                    // into the AlphaTest queue for no reason.
                    alphaClip = false;
                    r.Skipped("alpha clip turned off — there is no alpha to clip against");
                }
            }

            // ── Import settings ────────────────────────────────────────
            NormalizeTextureImport(texturePath, r);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                r.Failed("texture: '" + texturePath + "' did not load as a Texture2D after reimport");
                return null;
            }

            // ── Dimensions ─────────────────────────────────────────────
            float heightMeters = Mathf.Max(MinPlaneMeters, plane.heightMeters);
            float aspect = (float)texWidth / texHeight;
            float widthMeters = Mathf.Max(MinPlaneMeters, heightMeters * aspect);

            r.Measured(string.Format(CultureInfo.InvariantCulture,
                "plane: {0}×{1} px (aspect {2:0.###}) → {3:0.###} m wide × {4:0.###} m tall — "
              + "height is what you set, width falls out of the texture",
                texWidth, texHeight, aspect, widthMeters, heightMeters));

            // ── Hierarchy ──────────────────────────────────────────────
            //
            // Mode 1 has nothing that rotates, so NeedsMotionNode says no and
            // the prop is one transform lighter. That is not a micro-
            // optimisation: it is one fewer node per instance across every
            // poster in a park.

            string propName = AssetClassifier.SanitizeAssetName(
                Path.GetFileNameWithoutExtension(texturePath));

            bool needsMotion = plane.billboard != BillboardMode.None;
            GameObject root = PropPrefabEmitter.BuildHierarchy(propName, needsMotion);
            if (root == null)
            {
                r.Failed("texture: could not build the prop hierarchy");
                return null;
            }

            string savedPath = null;
            Mesh mesh = null;

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(root);
                Transform motion = PropPrefabEmitter.FindMotion(root);
                Transform visual = PropPrefabEmitter.FindVisual(root);

                if (anchor == null || visual == null)
                {
                    r.Failed("texture: hierarchy is malformed — expected Anchor/…/Visual");
                    return null;
                }

                // ── Rotation centre (mode 3 only) ──────────────────────
                //
                // DELIBERATE DEVIATION FROM THE SPEC'S MODE-3 TABLE ROW, which
                // reads "Centre, full billboard | pivot: centre". The geometry
                // here stays BASE-pivoted for all three modes and the rotation
                // CENTRE is raised to H/2 instead. The composite still spans
                // y ∈ [0, H] above the prop root, so rule 2 at the top of this
                // file — one ground-pivot contract for every track — survives
                // intact, and the Anchor box centred at (0, H/2, 0) is exactly
                // the centre of the D-diameter sweep sphere. The spec's row is
                // the older wording; this is the one that shipped.
                ApplyMotionOffset(motion, visual, plane.billboard, heightMeters, r);

                // ── Renderer ───────────────────────────────────────────
                //
                // The mesh and the material are both attached in the post-
                // save pass: the mesh has to become a sub-asset of a prefab
                // that does not exist yet, and naming the material after the
                // prefab's FINAL stem is the only way the two cannot drift
                // when Emit has to uniquify the name.

                MeshFilter filter = Componentizer.DoComponent<MeshFilter>(visual.gameObject, true);
                MeshRenderer renderer = Componentizer.DoComponent<MeshRenderer>(visual.gameObject, true);

                if (filter == null || renderer == null)
                {
                    r.Failed("texture: could not add MeshFilter/MeshRenderer to the Visual node");
                    return null;
                }

                // The shader is unlit, so nothing here is a visual
                // compromise — it is removing per-light work the plane cannot
                // use. A cut-out shadow caster costs a second alpha-tested
                // pass per light for a silhouette nobody asked for. All four
                // stay switchable in the inspector.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                // ── Billboard on the Motion node, never on the root ────
                if (needsMotion)
                {
                    if (motion == null)
                    {
                        r.Failed("texture: billboard mode " + plane.billboard
                                 + " needs a Motion node and the hierarchy has none");
                        return null;
                    }
                    if (!AttachBillboard(motion.gameObject, plane.billboard, r)) return null;
                }
                else
                {
                    r.Added("plane: fixed facing (mode 1) — no Billboard component and no Motion node, "
                          + "which is the simplest and cheapest shape this pipeline emits");
                }

                // ── Collider on the Anchor, sized to the SWEPT volume ──
                //
                // A failure here has to stop the run BEFORE Emit. Emit keeps
                // r.ok false on its own, but it still writes the .prefab, and
                // ConvertReadyExecutor.Track then puts that path in
                // LastRunCreatedAssets — so the creator gets a FAILED line in
                // the report sitting next to a successfully-written output
                // path, and a broken prop in the prop browser.
                Vector3 boxSize;
                if (!FitAnchorCollider(anchor.gameObject, plan, plane,
                                       widthMeters, heightMeters, r, out boxSize))
                {
                    return null;
                }

                // ── Emit ───────────────────────────────────────────────
                //
                // Mode 3 floats. It has no floor presence, so it must not
                // contribute a footprint to GapFiller — a hovering icon that
                // pushes the floor around underneath it is a bug that looks
                // like a floor bug. Done on a CLONE so the creator's plan
                // object is not silently rewritten under them.

                ConversionPlan emitPlan = plan;
                if (plane.billboard == BillboardMode.Full && plan.affectsGapFiller)
                {
                    emitPlan = plan.Clone();
                    emitPlan.affectsGapFiller = false;
                    r.Added("affectsGapFiller off — a full billboard floats, so it has no floor footprint "
                          + "for GapFiller to fill around");
                }

                savedPath = PropPrefabEmitter.Emit(root, propName, emitPlan, r);
                if (string.IsNullOrEmpty(savedPath))
                {
                    // Emit has already reported why.
                    return null;
                }

                // ── Post-save: mesh sub-asset + material ───────────────
                //
                // Two-sided geometry only ever applies to mode 1. A billboard
                // is by definition never seen from behind, so a back face
                // there is four vertices and two triangles of pure cost — and
                // the options window greys the toggle out for exactly that
                // reason, so honour it here rather than trusting the flag.
                bool doubleSided = plane.twoSided && plane.billboard == BillboardMode.None;

                string stem = Path.GetFileNameWithoutExtension(savedPath);
                mesh = BuildQuadMesh(widthMeters, heightMeters, doubleSided, MeshNamePrefix + stem);

                if (doubleSided)
                {
                    r.Added("plane: double-sided geometry — a single-sided plane is invisible from behind, "
                          + "and a static poster is the one case where nothing hides that. Real reversed "
                          + "triangles, not a cull-off material.");
                }
                else if (plane.billboard != BillboardMode.None)
                {
                    r.Skipped("plane: single-sided — a billboard is never seen from behind, so a back face "
                            + "would be triangles nobody ever renders");
                }
                else
                {
                    r.Skipped("plane: single-sided — it will be invisible from behind. Turn on 'two sided' "
                            + "if the park can walk around it.");
                }

                AttachGeneratedAssets(savedPath, mesh, texture, alphaClip, boxSize, r);
                return savedPath;
            }
            catch (Exception e)
            {
                r.Failed("texture: " + e.Message);

                // Only if it never made it into the prefab — destroying a mesh
                // that IS the sub-asset would delete it out of the saved file.
                if (mesh != null && !EditorUtility.IsPersistent(mesh))
                    UnityEngine.Object.DestroyImmediate(mesh);

                return savedPath;
            }
            finally
            {
                // The working root is a scene object; the prefab asset is the
                // deliverable. BuildHierarchy registered the creation with
                // Undo, so this destroy has to as well or Cmd-Z resurrects a
                // stray hierarchy in the creator's open scene.
                if (root != null) DestroyWorkingRoot(root);
            }
        }

        // ── Mesh ────────────────────────────────────────────────────────

        /// <summary>
        /// A quad of <paramref name="widthMeters"/> × <paramref name="heightMeters"/>,
        /// pivoted at the BOTTOM CENTRE, lying in the XY plane at z = 0.
        ///
        /// Winding and normals follow Unity's own built-in Quad exactly: the
        /// visible face is -Z with normal (0,0,-1), and u increases with +X so
        /// the texture reads left-to-right when you look at that face. Getting
        /// this backwards produces a mirrored image that nobody notices until
        /// there is text in the texture.
        ///
        /// The winding rule: Unity treats Cross(b-a, c-a) as a triangle's
        /// front normal (that is what Mesh.RecalculateNormals computes), so a
        /// -Z front face needs (v0,v2,v1) rather than (v0,v1,v2).
        ///
        /// <paramref name="twoSided"/> duplicates the four vertices with
        /// flipped normals and reversed winding. Real back-facing geometry,
        /// not a cull-off material: culling off makes the renderer draw twice
        /// with a normal that points the wrong way for half of it, and it
        /// silently disables a batching path.
        /// </summary>
        public static Mesh BuildQuadMesh(float widthMeters, float heightMeters, bool twoSided, string meshName)
        {
            float w = Mathf.Max(MinPlaneMeters, widthMeters) * 0.5f;
            float h = Mathf.Max(MinPlaneMeters, heightMeters);

            var positions = new List<Vector3>(8);
            var normals = new List<Vector3>(8);
            var uvs = new List<Vector2>(8);
            var tangents = new List<Vector4>(8);
            var triangles = new List<int>(12);

            // Front: visible from -Z.
            positions.Add(new Vector3(-w, 0f, 0f));   // 0 bottom-left
            positions.Add(new Vector3(w, 0f, 0f));    // 1 bottom-right
            positions.Add(new Vector3(-w, h, 0f));    // 2 top-left
            positions.Add(new Vector3(w, h, 0f));     // 3 top-right

            for (int i = 0; i < 4; i++) normals.Add(new Vector3(0f, 0f, -1f));
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 1f));

            // tangent.xyz is the direction of increasing u (+X). Unity derives
            // the binormal as Cross(normal, tangent.xyz) * tangent.w, and we
            // need that to come out as +Y (increasing v), which pins w to -1
            // on the -Z face and +1 on the +Z face.
            for (int i = 0; i < 4; i++) tangents.Add(new Vector4(1f, 0f, 0f, -1f));

            triangles.Add(0); triangles.Add(2); triangles.Add(1);
            triangles.Add(2); triangles.Add(3); triangles.Add(1);

            if (twoSided)
            {
                positions.Add(new Vector3(-w, 0f, 0f));   // 4
                positions.Add(new Vector3(w, 0f, 0f));    // 5
                positions.Add(new Vector3(-w, h, 0f));    // 6
                positions.Add(new Vector3(w, h, 0f));     // 7

                for (int i = 0; i < 4; i++) normals.Add(new Vector3(0f, 0f, 1f));

                // Same UVs, so the back reads as the mirror image — which is
                // what the back of a printed poster actually looks like, and
                // is what a creator expects when they walk around a sign.
                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(0f, 1f));
                uvs.Add(new Vector2(1f, 1f));

                for (int i = 0; i < 4; i++) tangents.Add(new Vector4(1f, 0f, 0f, 1f));

                triangles.Add(4); triangles.Add(5); triangles.Add(6);
                triangles.Add(6); triangles.Add(5); triangles.Add(7);
            }

            var mesh = new Mesh();
            mesh.name = string.IsNullOrEmpty(meshName) ? "Plane" : meshName;

            // Set BEFORE any index data: changing indexFormat clears the index
            // buffer, so doing this after SetTriangles silently empties the
            // mesh. 8 vertices never needs 32-bit indices, and pinning it
            // keeps the sub-asset small and Quest-friendly.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTangents(tangents);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            return mesh;
        }

        // ── Material ────────────────────────────────────────────────────

        /// <summary>
        /// Create the plane's material at <paramref name="materialPath"/>, or
        /// adopt the one already there, and return it. Null on failure.
        ///
        /// WHAT REUSE ACTUALLY CATCHES, AND WHY IT NO LONGER STOMPS.
        ///
        /// The original rationale for updating in place was "a re-run on the
        /// same texture should not leave M_Poster, M_Poster_2 and M_Poster_3
        /// behind". That rationale does not hold: PropPrefabEmitter.Emit
        /// uniquifies the prefab stem on every re-run (P_Poster → P_Poster_2)
        /// and this material is named from that FINAL stem, so a genuine re-run
        /// never lands on the old material at all.
        ///
        /// What the reuse path really catches is the creator who deleted or
        /// renamed the prefab but kept the material: the next run comes back to
        /// M_P_Poster.mat. Rewriting it wholesale there silently reverted a
        /// hand-tuned _Cutoff and tint to white/0.5. So:
        ///
        ///   • already on the Unlit graph → only the base texture is rewritten.
        ///     Tint, cutoff and surface mode are left exactly as tuned, and the
        ///     report says so, because that also means a changed alphaClip
        ///     setting will NOT be reflected.
        ///   • on some other shader → rewritten wholesale, because a plane on a
        ///     lit or third-party shader is not a plane. The report names every
        ///     property that was overwritten.
        /// </summary>
        public static Material CreatePlaneMaterial(string materialPath, Texture2D texture,
                                                   bool alphaClip, ConversionResult r)
        {
            Shader shader = Shader.Find(UnlitShaderName);
            if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>(UnlitShaderAssetPath);
            if (shader == null)
            {
                Report(r, DecisionKind.Failed,
                    "material: could not find '" + UnlitShaderName + "' or the graph at "
                    + UnlitShaderAssetPath + " — is the DreamPark SDK folder intact?");
                return null;
            }

            string folder = (Path.GetDirectoryName(materialPath) ?? string.Empty).Replace('\\', '/');
            if (!AssetClassifier.EnsureFolder(folder))
            {
                Report(r, DecisionKind.Failed, "material: could not create the folder " + folder);
                return null;
            }

            string matName = Path.GetFileNameWithoutExtension(materialPath);

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            bool reused = mat != null;
            bool keepTuning = false;
            string previousShaderName = null;

            if (reused)
            {
                previousShaderName = mat.shader != null ? mat.shader.name : "(none)";
                keepTuning = mat.shader == shader;
                if (!keepTuning) mat.shader = shader;
            }
            else
            {
                mat = new Material(shader);
                mat.name = matName;
                AssetDatabase.CreateAsset(mat, materialPath);
            }

            // The texture assignment is the one thing this call always owns —
            // it is what makes the material belong to THIS plane.
            if (mat.HasProperty(BaseTexProperty)) mat.SetTexture(BaseTexProperty, texture);

            if (!keepTuning)
            {
                if (mat.HasProperty(BaseColorProperty)) mat.SetColor(BaseColorProperty, Color.white);
                ApplySurfaceMode(mat, alphaClip);
            }

            EditorUtility.SetDirty(mat);

            string surface = alphaClip
                ? "alpha clip (cut-out, opaque queue, no sort order to get wrong)"
                : "opaque";

            if (!reused)
            {
                Report(r, DecisionKind.Added, string.Format(
                    "material: {0} on DreamPark-Unlit, {1} → {2}", matName, surface, materialPath));
            }
            else if (keepTuning)
            {
                Report(r, DecisionKind.Skipped, string.Format(
                    "material: {0}.mat already existed on DreamPark-Unlit, so only its base texture was "
                  + "rewritten — the tint (_baseColor), the alpha cutoff (_Cutoff) and the surface mode were "
                  + "LEFT AS THEY WERE. That means this run's '{1}' setting is NOT reflected in it. Delete "
                  + "{2} and convert again if you want a clean material.",
                    matName, surface, materialPath));
            }
            else
            {
                Report(r, DecisionKind.Guessed, string.Format(
                    "material: {0}.mat already existed on shader '{1}' and was moved onto DreamPark-Unlit — "
                  + "its shader, base texture, tint (_baseColor), alpha cutoff (_Cutoff) and surface mode "
                  + "were all OVERWRITTEN, because a plane on another shader is not a plane. If that "
                  + "material was tuned for something else, it is at {2} and needs checking.",
                    matName, previousShaderName, materialPath));
            }

            return mat;
        }

        /// <summary>
        /// Drive a URP Shader Graph material between opaque-clipped and
        /// blended. DreamPark-Unlit has "Allow Material Override" on, so
        /// _Surface / _AlphaClip / _SrcBlend / _DstBlend / _ZWrite are real
        /// material properties — but Unity only re-derives the KEYWORDS and
        /// the render queue from them when the material inspector runs, and
        /// nothing runs it when we assign from script. So both halves are
        /// written by hand, the same way LevelObjectManager already does it
        /// for the optimizer's clipped materials.
        ///
        /// Alpha clip is the default for a reason: no sort order to get
        /// wrong, no per-pixel blend cost, and correct for the cut-out
        /// foliage/character case that motivates the whole track.
        /// </summary>
        public static void ApplySurfaceMode(Material mat, bool alphaClip)
        {
            if (mat == null) return;

            if (alphaClip)
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);       // 0 = Opaque
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", DefaultAlphaCutoff);
                if (mat.HasProperty("_AlphaClipThreshold")) mat.SetFloat("_AlphaClipThreshold", DefaultAlphaCutoff);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);

                mat.EnableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");

                // AlphaTest, not Geometry: a clipped surface still wants to
                // draw after solid geometry so early-Z is not defeated by a
                // discard-heavy poster.
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

                // Alpha-to-coverage smooths cut-out edges wherever MSAA is on,
                // which on Quest it always is.
                if (mat.HasProperty("_AlphaToMask")) mat.SetFloat("_AlphaToMask", 1f);
            }
            else
            {
                if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
                if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);
                if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
                if (mat.HasProperty("_AlphaToMask")) mat.SetFloat("_AlphaToMask", 0f);

                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }

            // The graph itself declares Render Face = Front, and we emit real
            // back-facing triangles when the creator asks for two sides, so
            // back-face culling stays on. Cull-off would double the fill cost
            // of every plane in the park for nothing.
            if (mat.HasProperty("_Cull"))
                mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
        }

        // ── Texture import settings ─────────────────────────────────────

        /// <summary>
        /// Normalize the SOURCE texture's import settings for use as a
        /// cut-out plane. Returns true when something changed and the asset
        /// was reimported.
        ///
        /// Each of these is a support ticket otherwise:
        ///  • alphaIsTransparency — without it Unity's mip and compression
        ///    passes bleed the RGB of fully-transparent texels into the
        ///    visible edge, and cut-outs get a dark halo.
        ///  • Clamp wrap — Repeat makes the edge column bleed across the
        ///    opposite side of the cut-out at every mip level.
        ///  • Mipmaps ON — a plane in a park is viewed from across a room,
        ///    and an unmipped cut-out shimmers.
        ///  • maxTextureSize — a 4096 poster is 22 MB of VRAM per instance.
        ///
        /// Deliberately does NOT change textureType. A texture already
        /// imported as a Sprite is being used as a Sprite somewhere else, and
        /// re-typing it would break that silently. It still works fine as a
        /// plane's base map.
        ///
        /// AND — decision 4 at the top of the file — that same argument applies
        /// to every setting that changes what the pixels MEAN, not just to
        /// textureType. Clamp wrap on a PNG that is also a tiling base map on a
        /// mesh material stops it tiling, in a prefab nobody was looking at;
        /// sRGB on a packed mask corrupts it; forcing compression on a gradient
        /// ramp bands it. So those are proposed first, gated on whether any
        /// other material already references this texture, and NAMED in the
        /// report when they are withheld. maxTextureSize is the sole exception
        /// and is applied either way, downward only.
        /// </summary>
        public static bool NormalizeTextureImport(string texturePath, ConversionResult r)
        {
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) return false;

            var changes = new List<string>();

            // ── Applied whatever else uses this texture ────────────────
            if (importer.maxTextureSize > MaxTextureSize)
            {
                changes.Add("max size " + importer.maxTextureSize + " → " + MaxTextureSize);
                importer.maxTextureSize = MaxTextureSize;
            }

            // ── Proposed, then gated ───────────────────────────────────
            bool wantAlphaIsTransparency = !importer.alphaIsTransparency;
            bool wantClamp = importer.wrapMode != TextureWrapMode.Clamp;
            bool wantMipmaps = !importer.mipmapEnabled;
            bool wantSrgb = !importer.sRGBTexture;
            bool wantCompressed = importer.textureCompression == TextureImporterCompression.Uncompressed;

            bool anyGated = wantAlphaIsTransparency || wantClamp || wantMipmaps || wantSrgb || wantCompressed;
            bool applyGated = true;

            if (anyGated)
            {
                int otherUsers;
                bool scanned;
                List<string> names = FindMaterialUsers(texturePath, MaxNamedTextureUsers,
                                                       out otherUsers, out scanned);

                if (!scanned)
                {
                    // Applied rather than withheld: the overwhelmingly common
                    // case is a texture the creator just dragged in for this
                    // conversion, and a plane with the wrong wrap mode is the
                    // failure they WILL notice. GUESSED, not ADDED, because we
                    // did not actually check.
                    Report(r, DecisionKind.Guessed,
                        "could not check whether another material uses this texture (more than "
                      + MaxMaterialsScanned + " materials in the content folder), so the import settings "
                      + "below were applied anyway. If this PNG is also a tiling base map somewhere, its "
                      + "wrap mode has just changed to Clamp.");
                }
                else if (otherUsers > 0)
                {
                    applyGated = false;
                    Report(r, DecisionKind.Skipped, string.Format(
                        "import settings left alone — {0} already {1} this texture ({2}). Clamp wrap, sRGB, "
                      + "mipmaps and compression change what the PIXELS mean, and flipping wrap to Clamp "
                      + "would stop it tiling wherever it is used as a tiling base map. The plane still "
                      + "works; a cut-out edge may bleed at distance. Duplicate the PNG for the plane if "
                      + "you want both.",
                        otherUsers == 1 ? "one material" : otherUsers + " materials",
                        otherUsers == 1 ? "references" : "reference",
                        DescribeUsers(names, otherUsers)));
                }
            }

            if (applyGated)
            {
                if (wantAlphaIsTransparency)
                {
                    importer.alphaIsTransparency = true;
                    changes.Add("alphaIsTransparency on");
                }
                if (wantClamp)
                {
                    importer.wrapMode = TextureWrapMode.Clamp;
                    changes.Add("wrap → Clamp");
                }
                if (wantMipmaps)
                {
                    importer.mipmapEnabled = true;
                    changes.Add("mipmaps on");
                }
                if (wantSrgb)
                {
                    importer.sRGBTexture = true;
                    changes.Add("sRGB on");
                }
                if (wantCompressed)
                {
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    changes.Add("compression on");
                }
            }

            if (changes.Count == 0)
            {
                // Only when nothing was WITHHELD. Saying "already correct"
                // immediately after saying "left alone because three other
                // materials use it" reads as the tool contradicting itself.
                if (applyGated)
                    Report(r, DecisionKind.Skipped, "texture import settings already correct — nothing reimported");
                return false;
            }

            // Mirror onto the default platform entry. Without this, Unity's
            // per-platform override table can still build the default-platform
            // variant at the old size — the exact bug ContentProcessor
            // documents in its own thumbnail pass.
            var defaults = importer.GetDefaultPlatformTextureSettings();
            defaults.maxTextureSize = importer.maxTextureSize;
            defaults.textureCompression = importer.textureCompression;
            importer.SetPlatformTextureSettings(defaults);

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            Report(r, DecisionKind.Added, "texture import: " + string.Join(", ", changes.ToArray()));
            return true;
        }

        /// <summary>
        /// Materials that already reference <paramref name="texturePath"/>.
        ///
        /// There is no reverse-dependency index in AssetDatabase, so this is a
        /// forward scan: one FindAssets over the content root, then a
        /// NON-recursive GetDependencies per material, which reads the cached
        /// dependency list rather than re-parsing the asset. The scan is capped
        /// (<see cref="MaxMaterialsScanned"/>) and <paramref name="scanned"/>
        /// comes back false when the cap bites, so a caller can tell "no other
        /// users" apart from "did not look".
        ///
        /// Not TextureUsageGraph.Build, deliberately: that walks every texture,
        /// every material AND instantiates every prefab under a folder to
        /// measure renderer bounds — its own comment budgets 5–20 seconds — and
        /// this runs once per converted texture inside a batch.
        /// </summary>
        public static List<string> FindMaterialUsers(string texturePath, int maxNames,
                                                     out int totalCount, out bool scanned)
        {
            var names = new List<string>();
            totalCount = 0;
            scanned = false;

            if (string.IsNullOrEmpty(texturePath)) return names;

            try
            {
                string[] guids = AssetDatabase.IsValidFolder(ContentFolders.Root)
                    ? AssetDatabase.FindAssets("t:Material", new[] { ContentFolders.Root })
                    : AssetDatabase.FindAssets("t:Material");

                if (guids == null) return names;
                if (guids.Length > MaxMaterialsScanned) return names;

                for (int i = 0; i < guids.Length; i++)
                {
                    string matPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (string.IsNullOrEmpty(matPath)) continue;

                    // recursive: false — a material's direct dependencies are
                    // exactly its shader and its textures, which is all we ask.
                    string[] deps = AssetDatabase.GetDependencies(matPath, false);
                    if (deps == null) continue;

                    for (int d = 0; d < deps.Length; d++)
                    {
                        if (!string.Equals(deps[d], texturePath, StringComparison.Ordinal)) continue;

                        totalCount++;
                        if (names.Count < maxNames) names.Add(Path.GetFileNameWithoutExtension(matPath));
                        break;
                    }
                }

                scanned = true;
            }
            catch (Exception)
            {
                // A failed scan must read as "did not look", never as "nobody
                // else uses it" — the second one is what silently rewrites
                // somebody's tiling base map.
                scanned = false;
            }

            return names;
        }

        private static string DescribeUsers(List<string> names, int totalCount)
        {
            if (names == null || names.Count == 0) return totalCount + " material(s)";

            string listed = string.Join(", ", names.ToArray());
            int extra = totalCount - names.Count;
            return extra > 0 ? listed + " and " + extra + " more" : listed;
        }

        /// <summary>
        /// True when the file on disk carries an alpha channel. Note this asks
        /// the SOURCE, not the imported Texture2D — a compressed import can
        /// report an alpha-bearing format for a texture that has no alpha in
        /// it, which would make the "no alpha" warning never fire on exactly
        /// the assets that need it.
        /// </summary>
        public static bool SourceHasAlpha(TextureImporter importer)
        {
            if (importer == null) return false;
            try
            {
                return importer.DoesSourceTextureHaveAlpha();
            }
            catch (Exception)
            {
                // Throws for source files Unity cannot re-open (moved,
                // read-only, or an exotic format). Assume alpha rather than
                // firing a scary and possibly wrong warning.
                return true;
            }
        }

        // ── Pieces ──────────────────────────────────────────────────────

        /// <summary>
        /// Mode 3 rotates about the sprite's CENTRE, not its base.
        ///
        /// The geometry is base-pivoted for every mode — that is rule 2 at the
        /// top of this file and it is what keeps the ground-pivot contract
        /// honest. But a full billboard spinning about its bottom edge orbits
        /// rather than turns, and the sprite visibly swings away from where it
        /// was placed. So for mode 3 the Motion node is lifted to the sprite's
        /// mid-height and the Visual is pushed back down by the same amount:
        /// the composite still spans y ∈ [0, H] above the prop root, and the
        /// rotation happens around the middle.
        ///
        /// This is the deliberate deviation from the spec's mode-3 table row
        /// ("pivot: centre") flagged at the call site — the pivot stays at the
        /// base and only the rotation centre moves.
        ///
        /// Modes 1 and 2 need neither — yaw about the vertical axis through
        /// the base is exactly right for something standing on the floor.
        /// </summary>
        private static void ApplyMotionOffset(Transform motion, Transform visual,
                                              BillboardMode mode, float heightMeters, ConversionResult r)
        {
            if (mode != BillboardMode.Full || motion == null || visual == null) return;

            float half = heightMeters * 0.5f;
            motion.localPosition = new Vector3(0f, half, 0f);
            visual.localPosition = new Vector3(0f, -half, 0f);

            Report(r, DecisionKind.Measured, string.Format(CultureInfo.InvariantCulture,
                "full billboard: rotation centre raised to {0:0.###} m (the sprite's middle) while the pivot "
              + "stays at the base. Spinning a floating sprite about its bottom edge makes it orbit instead "
              + "of turn.",
                half));
        }

        /// <summary>
        /// Put the Billboard on the Motion node. Returns false when it could
        /// not be added, in which case the caller must not emit: a prop in a
        /// billboard mode with no Billboard component is a plane with a
        /// pointless extra transform and a collider sized for a rotation that
        /// never happens.
        /// </summary>
        private static bool AttachBillboard(GameObject motionNode, BillboardMode mode, ConversionResult r)
        {
            Billboard billboard = Componentizer.DoComponent<Billboard>(motionNode, true);
            if (billboard == null)
            {
                Report(r, DecisionKind.Failed, "could not add the Billboard component to the Motion node");
                return false;
            }

            billboard.axis = mode == BillboardMode.Full ? BillboardAxis.Full : BillboardAxis.YOnly;

            Report(r, DecisionKind.Added, mode == BillboardMode.Full
                ? "Billboard (full facing) on the Motion node — it resolves the player's head lazily through "
                + "DreamParkLuaAPI.Head(), the same call behind dp.head(), because the player rig is a "
                + "different addressable and there is nothing to serialize a reference to"
                : "Billboard (yaw only) on the Motion node — yaw-only is right for anything standing on the "
                + "floor; a full billboard tips as the player crouches and the base lifts off the ground");
            return true;
        }

        /// <summary>
        /// Box the SWEPT volume onto the Anchor. Returns false when the
        /// collider could not be added — <paramref name="size"/> is still
        /// filled in, so the caller can report dimensions, but nothing may be
        /// written to disk after a false.
        ///
        /// The collider goes on Anchor and not on Motion because
        /// PropTemplate.TryGetColliderFootprint walks
        /// GetComponentsInChildren&lt;Collider&gt;(), and GapFiller and
        /// FloorCutout both consume that footprint. On a rotating node the
        /// prop's floor footprint would spin with the player's head, and a
        /// billboarded tree would re-cut its hole in the floor every time
        /// somebody walked around it. Anchor never rotates, so
        /// useColliderBounds = true just works and no customFootprintMeters
        /// has to be kept in sync with the plane's dimensions by hand.
        /// </summary>
        private static bool FitAnchorCollider(GameObject anchor, ConversionPlan plan, PlaneSettings plane,
                                              float widthMeters, float heightMeters, ConversionResult r,
                                              out Vector3 size)
        {
            size = ColliderFitter.BoxSizeForPlane(widthMeters, heightMeters, plane.billboard);

            if (plan.collider == ColliderChoice.None)
            {
                Report(r, DecisionKind.Skipped,
                    "no collider — the footprint falls back to PropTemplate.customFootprintMeters, "
                  + "which is written from the plane's swept size below");
                return true;
            }

            BoxCollider box = Componentizer.DoComponent<BoxCollider>(anchor, true);
            if (box == null)
            {
                Report(r, DecisionKind.Failed, "could not add the BoxCollider to the Anchor node");
                return false;
            }

            box.size = size;

            // The plane's base sits at the Anchor's origin, so the box that
            // covers it is centred half a height up. True for all three modes:
            // mode 3's sweep is a sphere about the sprite's mid-height, which
            // is the same point.
            box.center = new Vector3(0f, heightMeters * 0.5f, 0f);

            string why;
            switch (plane.billboard)
            {
                case BillboardMode.YAxis:
                    why = "W × H × W — a yaw billboard sweeps a cylinder of diameter W, and the collider "
                        + "does not turn with it";
                    break;
                case BillboardMode.Full:
                    why = "D × D × D where D = √(W²+H²) — a full billboard sweeps a sphere on its own diagonal";
                    break;
                default:
                    why = "W × H × " + ColliderFitter.PlaneThicknessMeters.ToString("0.###", CultureInfo.InvariantCulture)
                        + " m — nothing rotates, so the box is just the plane";
                    break;
            }

            Report(r, DecisionKind.Measured, string.Format(CultureInfo.InvariantCulture,
                "collider: BoxCollider on Anchor, {0:0.###} × {1:0.###} × {2:0.###} m ({3})",
                size.x, size.y, size.z, why));

            return true;
        }

        // ── Post-save wiring ────────────────────────────────────────────

        /// <summary>
        /// Everything that can only happen once the .prefab exists on disk:
        /// the generated mesh becomes a sub-asset of it, the material is
        /// created next to it under the prefab's final name, and both are
        /// wired onto the Visual node inside the saved asset.
        ///
        /// THE ORDER IS NOT INTERCHANGEABLE — see the note at the top of the
        /// file for what the previous order did.
        ///
        ///  1. AddObjectToAsset FIRST, so the mesh is persistent BEFORE
        ///     anything references it. A MeshFilter pointing at an in-memory
        ///     Mesh serializes as null, and the creator gets an invisible prop
        ///     with a MeshFilter that looks populated until they click it.
        ///  2. Edit the LOADED PREFAB ASSET, not a LoadPrefabContents copy, and
        ///     save it with PrefabUtility.SavePrefabAsset. SaveAsPrefabAsset
        ///     would REPLACE the file from the preview-scene copy and drop the
        ///     sub-asset we just added. No AssetDatabase.SaveAssets() here
        ///     either: this whole run is inside the executor's
        ///     StartAssetEditing block where it is a no-op, and the executor
        ///     calls it once after StopAssetEditing.
        ///  3. Re-load and CHECK. This is the failure that has to be loud —
        ///     an invisible plane with a report line claiming the mesh was
        ///     saved is worse than an error.
        /// </summary>
        private static void AttachGeneratedAssets(string prefabPath, Mesh mesh, Texture2D texture,
                                                  bool alphaClip, Vector3 boxSize, ConversionResult r)
        {
            string stem = Path.GetFileNameWithoutExtension(prefabPath);

            // ── 1. Mesh → sub-asset ────────────────────────────────────
            try
            {
                AssetDatabase.AddObjectToAsset(mesh, prefabPath);
            }
            catch (Exception e)
            {
                Report(r, DecisionKind.Failed, "mesh: could not add the generated quad to " + prefabPath
                                             + " — " + e.Message);

                // A Mesh that never became an asset is a leaked UnityEngine
                // .Object; it survives until the next scene load and shows up
                // as a phantom in memory profiles.
                UnityEngine.Object.DestroyImmediate(mesh);
                return;
            }

            // ── 2. Material next to the prefab ─────────────────────────
            string materialPath = MaterialFolderFor(prefabPath) + "/" + MaterialPrefix + stem + ".mat";
            Material material = CreatePlaneMaterial(materialPath, texture, alphaClip, r);

            // ── 3. Wire both inside the saved prefab ───────────────────
            GameObject assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (assetRoot == null)
            {
                Report(r, DecisionKind.Failed,
                    "could not load " + prefabPath + " back to attach the mesh — the prefab is on disk "
                  + "with no mesh and no material on its Visual node");
                return;
            }

            Transform visual = PropPrefabEmitter.FindVisual(assetRoot);
            if (visual == null)
            {
                Report(r, DecisionKind.Failed, "the saved prefab has no Visual node to attach the mesh to");
                return;
            }

            var filter = visual.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = mesh;

            var renderer = visual.GetComponent<MeshRenderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            // The manual footprint is only READ when useColliderBounds is
            // off, but writing it costs nothing and means a creator who
            // later turns collider bounds off gets the plane's real size
            // rather than PropTemplate's (1,1) default.
            var prop = assetRoot.GetComponent<PropTemplate>();
            if (prop != null)
            {
                prop.customFootprintMeters = new Vector2(
                    Mathf.Max(MinPlaneMeters, boxSize.x),
                    Mathf.Max(MinPlaneMeters, boxSize.z));
            }

            EditorUtility.SetDirty(assetRoot);

            try
            {
                bool saved;
                PrefabUtility.SavePrefabAsset(assetRoot, out saved);
                if (!saved)
                {
                    Report(r, DecisionKind.Failed,
                        "Unity refused to save the mesh and material into " + prefabPath);
                    return;
                }
            }
            catch (Exception e)
            {
                Report(r, DecisionKind.Failed, "could not save the mesh and material into " + prefabPath
                                             + " — " + e.Message);
                return;
            }

            VerifyAttachment(prefabPath, material, r);
        }

        /// <summary>
        /// Re-open the saved prefab and confirm the plane is actually there.
        ///
        /// This exists because the previous version of AttachGeneratedAssets
        /// could report "mesh saved as a SUB-ASSET" while leaving a MeshFilter
        /// that reads None after the next domain reload. A converter whose
        /// report can be wrong about its own output is worse than one that
        /// fails, so the claim is now checked before it is made.
        /// </summary>
        private static void VerifyAttachment(string prefabPath, Material material, ConversionResult r)
        {
            GameObject check = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (check == null)
            {
                Report(r, DecisionKind.Failed,
                    "could not re-open " + prefabPath + " to verify the plane — treat this prop as suspect");
                return;
            }

            MeshFilter filter = check.GetComponentInChildren<MeshFilter>(true);
            Mesh saved = filter != null ? filter.sharedMesh : null;

            if (saved == null)
            {
                Report(r, DecisionKind.Failed,
                    "mesh: the saved prefab's MeshFilter has no mesh — the plane would be invisible. "
                  + "Nothing further was written; delete " + prefabPath + " and convert again.");
                return;
            }

            string meshOwner = AssetDatabase.GetAssetPath(saved);
            if (!string.Equals(meshOwner, prefabPath, StringComparison.Ordinal))
            {
                Report(r, DecisionKind.Failed, string.Format(
                    "mesh: '{0}' ended up at '{1}' instead of inside {2}. The MeshFilter points outside the "
                  + "prefab, so the plane breaks the moment that file moves.",
                    saved.name, string.IsNullOrEmpty(meshOwner) ? "(nowhere — it is still in memory)" : meshOwner,
                    prefabPath));
                return;
            }

            Report(r, DecisionKind.Added,
                "mesh: '" + saved.name + "' saved as a SUB-ASSET of the prefab and verified on the Visual "
              + "node — a loose .asset per plane is how a content folder fills with orphans and how "
              + "addressable groups churn on re-bake");

            MeshRenderer renderer = check.GetComponentInChildren<MeshRenderer>(true);
            if (material != null && (renderer == null || renderer.sharedMaterial == null))
            {
                Report(r, DecisionKind.Failed,
                    "material: the saved prefab's MeshRenderer has no material — the plane will render as "
                  + "Unity's magenta error shader");
            }
        }

        /// <summary>
        /// {content}/Materials for a prefab saved at {content}/Prefabs/P_X.prefab.
        /// Falls back to a Materials folder beside the prefab when it did not
        /// land in a Prefabs folder, so a moved destination cannot scatter
        /// materials into the content root.
        /// </summary>
        private static string MaterialFolderFor(string prefabPath)
        {
            string dir = (Path.GetDirectoryName(prefabPath) ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) return ContentFolders.Root + "/" + MaterialsFolderName;

            string leaf = Path.GetFileName(dir);
            if (string.Equals(leaf, PropPrefabEmitter.PrefabsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                string parent = (Path.GetDirectoryName(dir) ?? string.Empty).Replace('\\', '/');
                if (!string.IsNullOrEmpty(parent)) return parent + "/" + MaterialsFolderName;
            }
            return dir + "/" + MaterialsFolderName;
        }

        // ── Source dimensions ───────────────────────────────────────────

        private static void ReadSourceSize(TextureImporter importer, string texturePath,
                                           out int width, out int height, ConversionResult r)
        {
            width = 0;
            height = 0;

            try
            {
                importer.GetSourceTextureWidthAndHeight(out width, out height);
            }
            catch (Exception)
            {
                width = 0;
                height = 0;
            }

            if (width > 0 && height > 0) return;

            // Fall back to the IMPORTED texture. Its aspect survives Unity's
            // maxTextureSize clamp (both axes scale together), so it is a
            // correct answer for the only thing we need the numbers for.
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (tex != null)
            {
                width = tex.width;
                height = tex.height;
                Report(r, DecisionKind.Skipped,
                    "could not read the source file's dimensions — using the imported texture's "
                  + width + "×" + height + " instead (same aspect, so the plane is still correct)");
            }
        }

        // ── Internals ───────────────────────────────────────────────────

        private static void DestroyWorkingRoot(GameObject root)
        {
            if (EditorUtility.IsPersistent(root)) return;

            // Undo.DestroyObjectImmediate rather than DestroyImmediate: the
            // hierarchy was registered with Undo.RegisterCreatedObjectUndo,
            // and destroying it outside the undo system leaves the creator's
            // Cmd-Z pointing at an object that no longer exists.
            Undo.DestroyObjectImmediate(root);
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
    }
}
#endif
