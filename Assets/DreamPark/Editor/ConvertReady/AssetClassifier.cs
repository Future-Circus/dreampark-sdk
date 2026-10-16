// ─────────────────────────────────────────────────────────────────────
//  AssetClassifier.cs — Stage 0. "What did the creator actually select?"
//
//  Everything downstream branches on the answer, so getting it wrong is not
//  a cosmetic bug: a .psd routed down the mesh track loads as a Texture2D,
//  the mesh track asks it for renderers, gets none, and the creator is told
//  "no Renderer in children — cannot measure source size". That message is
//  true and completely useless. Classify first, fail with the right sentence.
//
//  The extension sets are COPIED from MaterialConverter.ModelExtensions
//  rather than referenced, because that field is private. If a format is
//  ever added there it must be added here too — otherwise the shader
//  converter's right-click menu accepts an asset that this one silently
//  calls Unsupported, which reads as the converter being broken for that
//  one file type.
//
//  This file also owns the measurement primitives every later stage needs:
//
//   • LocalBoundsOf — the union of every renderer's bounds expressed in the
//     ROOT's local space. A naive union of Renderer.bounds (which are
//     world-space AABBs) is wrong for a multi-part model the moment any part
//     is rotated: the world AABB of a rotated part is bigger than the part,
//     so the "measured size" grows and shrinks as you turn the prop. Every
//     scale and pivot number in this pipeline is derived from this, so the
//     wrong version here becomes a wrong prop.
//
//   • TriangleCount — via Mesh.GetIndexCount, NOT Mesh.triangles. Imported
//     meshes are non-readable by default; Mesh.triangles on one of those
//     throws at edit time on some import paths and returns an empty array on
//     others. GetIndexCount is metadata and always works.
//
//   • ExtractEmbeddedMaterials — the thing that replaces today's advice to
//     "open its import settings → Materials tab → 'Extract Materials'".
//     A creator who knew what the Materials tab was would not have needed
//     the converter in the first place. It returns a BOOL, not a count,
//     because a count of 0 cannot distinguish "nothing was embedded" from
//     "every extraction failed" and the caller has to abort on the second.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class AssetClassifier
    {
        // ── Extension sets ──────────────────────────────────────────────

        // Verbatim from MaterialConverter.ModelExtensions (private there).
        private static readonly HashSet<string> ModelExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".glb", ".gltf", ".obj", ".dae", ".blend", ".max", ".ma", ".mb", ".3ds", ".dxf"
        };

        // .psd and .tif are here deliberately: Unity imports both as Texture2D,
        // and a 2D artist who works in Photoshop drags the .psd, not an export.
        private static readonly HashSet<string> TextureExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".bmp", ".gif"
        };

        private static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".ogg", ".aiff", ".aif"
        };

        public const string PrefabExtension = ".prefab";

        public static bool IsModelExtension(string extension)
        {
            return !string.IsNullOrEmpty(extension) && ModelExtensions.Contains(extension);
        }

        public static bool IsTextureExtension(string extension)
        {
            return !string.IsNullOrEmpty(extension) && TextureExtensions.Contains(extension);
        }

        public static bool IsAudioExtension(string extension)
        {
            return !string.IsNullOrEmpty(extension) && AudioExtensions.Contains(extension);
        }

        public static bool IsModelPath(string assetPath)
        {
            return IsModelExtension(ExtensionOf(assetPath));
        }

        public static bool IsPrefabPath(string assetPath)
        {
            return string.Equals(ExtensionOf(assetPath), PrefabExtension, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTexturePath(string assetPath)
        {
            return IsTextureExtension(ExtensionOf(assetPath));
        }

        public static bool IsAudioPath(string assetPath)
        {
            return IsAudioExtension(ExtensionOf(assetPath));
        }

        public static bool IsFolder(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath);
        }

        // ── Classification ──────────────────────────────────────────────

        /// <summary>
        /// The Stage 0 answer. Folders are NOT a track — a folder is a
        /// container the caller expands into individual assets, each of which
        /// gets classified on its own. Use <see cref="IsFolder"/> for that,
        /// and <see cref="IsConvertible"/> for the menu validator.
        /// </summary>
        public static ConvertTrack Classify(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return ConvertTrack.Unsupported;

            // .prefab first: an already-converted prop is a legal input (re-run),
            // and a prefab extension can never also be a model extension.
            if (IsPrefabPath(assetPath)) return ConvertTrack.Mesh;
            if (IsModelPath(assetPath))  return ConvertTrack.Mesh;
            if (IsTexturePath(assetPath)) return ConvertTrack.Texture;
            if (IsAudioPath(assetPath))   return ConvertTrack.Audio;

            return ConvertTrack.Unsupported;
        }

        public static ConvertTrack Classify(UnityEngine.Object asset)
        {
            if (asset == null) return ConvertTrack.Unsupported;
            return Classify(AssetDatabase.GetAssetPath(asset));
        }

        /// <summary>
        /// True for anything the menu item should light up on — every track,
        /// plus folders. Mirrors MaterialConverter.ValidateRightClickConvert's
        /// shape so the two DreamPark items enable and disable together.
        /// </summary>
        public static bool IsConvertible(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (IsFolder(assetPath)) return true;
            return Classify(assetPath) != ConvertTrack.Unsupported;
        }

        /// <summary>
        /// Every convertible asset under a folder, recursively, sorted by path,
        /// ordinal. Returns the folder itself expanded — folders nested inside
        /// are walked, folders are never returned.
        ///
        /// The sort is deliberate and load-bearing for Shared Props, which take
        /// the FIRST entry as the family's base model: AssetDatabase.FindAssets
        /// order is GUID order, i.e. it changes when the project is reimported
        /// on another machine, so a folder selection would pick a different base
        /// each time. Path order is stable. It is NOT selection order — a caller
        /// that needs "the one the creator clicked first" must pass the
        /// individual paths, not the folder.
        /// </summary>
        public static List<string> EnumerateConvertible(string folderPath)
        {
            var found = new List<string>();
            if (!IsFolder(folderPath)) return found;

            // "t:Object" rather than an empty filter: an empty filter string is
            // not reliably "match everything" in AssetDatabase.FindAssets, and
            // silently returning nothing here would make folder selections look
            // like they contained no convertible assets.
            string[] guids = AssetDatabase.FindAssets("t:Object", new[] { folderPath });
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (Classify(path) == ConvertTrack.Unsupported) continue;
                found.Add(path);
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        // ── Geometry facts ──────────────────────────────────────────────

        /// <summary>
        /// True when anything under <paramref name="root"/> is skinned. This is
        /// the branch that sends Bendy down the jiggle-ragdoll path instead of
        /// EasyBend: EasyBend rotates ONE transform, so on a skinned mesh it
        /// swings the whole character rigidly and looks broken.
        /// </summary>
        public static bool IsSkinned(GameObject root)
        {
            if (root == null) return false;
            return root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        /// <summary>
        /// Total rendered triangles under <paramref name="root"/>, counting each
        /// instance separately (two copies of a 500-tri mesh cost 1000). Shared
        /// meshes are deliberately NOT deduplicated — the number is a draw cost,
        /// not an asset-size figure.
        ///
        /// Uses Mesh.GetIndexCount rather than Mesh.triangles: imported meshes
        /// are non-readable by default and Mesh.triangles does not survive that.
        /// </summary>
        public static int TriangleCount(GameObject root)
        {
            if (root == null) return 0;

            int total = 0;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Mesh mesh = MeshOf(renderers[i]);
                if (mesh == null) continue;

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    uint indices = mesh.GetIndexCount(sub);
                    switch (mesh.GetTopology(sub))
                    {
                        case MeshTopology.Triangles:
                            total += (int)(indices / 3);
                            break;
                        case MeshTopology.Quads:
                            // A quad is two triangles once the GPU sees it, and the
                            // convex-hull and fracture budgets both care about the
                            // triangle figure, not the primitive figure.
                            total += (int)(indices / 4) * 2;
                            break;
                        default:
                            // Points / Lines / LineStrip contribute no triangles.
                            break;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// The union of every ACTIVE renderer's bounds under
        /// <paramref name="root"/>, expressed in <paramref name="root"/>'s own
        /// local space. Zero-sized when there is nothing to measure — call
        /// <see cref="TryGetLocalBounds"/> when you need to tell "nothing here"
        /// apart from "a point at the origin".
        ///
        /// Root-local, not world: a world-AABB union grows when a sub-part is
        /// rotated, so the same model would measure differently depending on
        /// how its parts happen to be posed. Every scale, pivot and collider
        /// number downstream reads this, so that error would propagate to all
        /// of them.
        ///
        /// The root's OWN localScale is excluded (the matrix is relative to the
        /// root), which is what makes this comparable with what
        /// PrefabScaler.ScaleToFit measures — it resets localScale to identity
        /// before measuring for the same reason.
        /// </summary>
        public static Bounds LocalBoundsOf(GameObject root)
        {
            Bounds b;
            TryGetLocalBounds(root, false, out b);
            return b;
        }

        public static bool TryGetLocalBounds(GameObject root, bool includeInactive, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (root == null) return false;
            return TryGetBoundsInSpace(root, root.transform.worldToLocalMatrix, includeInactive, out bounds);
        }

        /// <summary>
        /// The same union, expressed in an arbitrary space.
        /// <paramref name="worldToSpace"/> is the matrix that takes a world
        /// point into the space you want the answer in — e.g.
        /// parent.worldToLocalMatrix to ground a pivot against its parent.
        /// </summary>
        public static bool TryGetBoundsInSpace(GameObject root, Matrix4x4 worldToSpace,
                                               bool includeInactive, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (root == null) return false;

            var renderers = root.GetComponentsInChildren<Renderer>(includeInactive);
            if (renderers.Length == 0) return false;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;

                Bounds source;
                Matrix4x4 sourceToWorld;

                Mesh mesh = MeshOf(r);
                if (mesh != null)
                {
                    // Mesh-local bounds through the renderer's own transform: exact,
                    // and unaffected by how the part is rotated.
                    source = SourceLocalBounds(r, mesh);
                    sourceToWorld = r.transform.localToWorldMatrix;
                }
                else
                {
                    // Particle / line / trail renderers have no shared mesh. Their
                    // world AABB is the only thing on offer; it is already in world
                    // space so it needs no extra transform.
                    source = r.bounds;
                    sourceToWorld = Matrix4x4.identity;
                }

                // A renderer with no geometry at all reports a zero box, usually
                // sitting at the origin. Encapsulating it would drag the union to
                // include the origin and quietly break the ground pivot.
                if (source.size.sqrMagnitude <= 1e-18f) continue;

                Matrix4x4 m = worldToSpace * sourceToWorld;
                Vector3 c = source.center;
                Vector3 e = source.extents;

                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = m.MultiplyPoint3x4(
                        new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));

                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                    any = true;
                }
            }

            if (!any) return false;

            var result = new Bounds();
            result.SetMinMax(min, max);
            bounds = result;
            return true;
        }

        /// <summary>Longest bounds axis in metres, or 0 when unmeasurable.</summary>
        public static float LongestAxisMeters(GameObject root)
        {
            Bounds b;
            if (!TryGetLocalBounds(root, false, out b)) return 0f;
            Vector3 s = b.size;
            return Mathf.Max(s.x, Mathf.Max(s.y, s.z));
        }

        /// <summary>
        /// The mesh a renderer actually draws, or null for renderers that draw
        /// without one (ParticleSystemRenderer, LineRenderer, TrailRenderer).
        /// </summary>
        public static Mesh MeshOf(Renderer r)
        {
            if (r == null) return null;

            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;

            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        // Mirrors the shape of PrefabScaler.TryGetLocalBounds (private there):
        // a SkinnedMeshRenderer's localBounds is authored and may be larger than
        // the bind-pose mesh, and it is what the renderer is actually culled by,
        // so prefer it over sharedMesh.bounds when both exist.
        private static Bounds SourceLocalBounds(Renderer r, Mesh mesh)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.localBounds;
            return mesh.bounds;
        }

        // ── Embedded materials (Stage 1 helper) ─────────────────────────

        /// <summary>
        /// True when the model file carries Materials as sub-assets — the state
        /// that makes MaterialConverter report "had no convertible materials"
        /// while the model is visibly pink. Sub-asset materials are read-only,
        /// so nothing can convert them until they are extracted to disk.
        /// </summary>
        public static bool HasEmbeddedMaterials(string modelPath)
        {
            if (!IsModelPath(modelPath)) return false;

            var all = AssetDatabase.LoadAllAssetsAtPath(modelPath);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is Material && AssetDatabase.IsSubAsset(all[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// Kit folder is P_{stem} next to the source, so it never shares a
        /// stem with Foo.fbx. Prefab, materials and the original file all
        /// land in that folder.
        /// </summary>
        public static string KitFolderFor(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string normalized = assetPath.Replace('\\', '/');
            string parent = (Path.GetDirectoryName(normalized) ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) return null;

            string stem = SanitizeAssetName(Path.GetFileNameWithoutExtension(normalized), "Asset");
            string kitName = stem.StartsWith("P_", StringComparison.Ordinal) ? stem : "P_" + stem;
            string parentLeaf = Path.GetFileName(parent);
            if (string.Equals(parentLeaf, kitName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parentLeaf, stem, StringComparison.OrdinalIgnoreCase))
                return parent;
            return parent + "/" + kitName;
        }

        /// <summary>
        /// Move the source into its P_{stem} kit. Uses CreateFolder + MoveAsset
        /// so the ModelImporter survives; a raw filesystem move turns the
        /// FBX into a DefaultAsset and LoadAssetAtPath&lt;GameObject&gt; returns null.
        /// </summary>
        public static string GatherIntoAssetFolder(ref string assetPath, ConversionResult r)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                if (r != null) r.Failed("could not gather the asset — no source path");
                return null;
            }

            string normalized = assetPath.Replace('\\', '/');
            string folder = KitFolderFor(normalized);
            if (string.IsNullOrEmpty(folder))
            {
                if (r != null) r.Failed("could not gather '" + assetPath + "' — it has no parent folder");
                return null;
            }

            string fileName = Path.GetFileName(normalized);
            string dest = folder + "/" + fileName;

            if (string.Equals(normalized, dest, StringComparison.Ordinal))
            {
                if (LoadModel(normalized) == null)
                {
                    if (r != null) r.Failed("could not load '" + normalized + "' as a GameObject");
                    return null;
                }
                assetPath = normalized;
                if (r != null) r.sourcePath = normalized;
                return folder;
            }

            if (!EnsureFolder(folder) || !CommitFolder(folder))
            {
                if (r != null) r.Failed("could not create the folder " + folder);
                return null;
            }

            string err = AssetDatabase.MoveAsset(normalized, dest);
            if (!string.IsNullOrEmpty(err))
            {
                if (r != null) r.Failed("could not move '" + fileName + "' into " + folder + " — " + err);
                return null;
            }
            AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceSynchronousImport);
            if (r != null) r.Added("gathered '" + fileName + "' into " + folder);

            if (LoadModel(dest) == null)
            {
                if (r != null) r.Failed("could not load '" + dest + "' as a GameObject");
                return null;
            }

            assetPath = dest;
            if (r != null) r.sourcePath = dest;
            return folder;
        }

        /// <summary>
        /// FBX/GLB as a prefab-instantiable GameObject. Reimports through the
        /// ModelImporter when a prior filesystem move left it as DefaultAsset.
        /// </summary>
        public static GameObject LoadModel(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go != null) return go;

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                importer.SaveAndReimport();
                go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (go != null) return go;
            }

            AssetDatabase.ImportAsset(assetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        }

        /// <summary>
        /// Materials dump next to the model, in the same per-asset folder
        /// GatherIntoAssetFolder just made. No more {content}/Materials.
        /// </summary>
        public static string DefaultMaterialsFolder(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return ContentFolders.Root;
            string dir = (Path.GetDirectoryName(modelPath) ?? string.Empty).Replace('\\', '/');
            return string.IsNullOrEmpty(dir) ? ContentFolders.Root : dir;
        }

        /// <summary>
        /// Pull every embedded Material out of <paramref name="modelPath"/> into
        /// <paramref name="destFolder"/> as real .mat assets, then reimport the
        /// model so it re-binds to them.
        ///
        /// RETURNS FALSE WHEN THE MATERIALS ARE STILL EMBEDDED. The caller must
        /// abort the asset on false — per the stage order, a failed extraction
        /// has to stop us BEFORE we start writing prefabs, otherwise we emit a
        /// prop wired to materials that are still read-only and pink and a
        /// report that says the conversion succeeded. The count alone could
        /// never carry that: 0 means both "nothing was embedded" (fine) and
        /// "every extraction failed" (fatal), which is why this returns a bool
        /// and hands the count back through <paramref name="extracted"/>.
        ///
        /// True with <paramref name="extracted"/> == 0 is the ordinary
        /// nothing-to-do case: not a model file, or no sub-asset materials.
        ///
        /// Idempotent: once extracted, the materials are no longer sub-assets,
        /// so a second run finds nothing, reports a skip and returns true.
        ///
        /// Failures are also reported via <c>r.Failed</c>, which sets
        /// <c>r.ok = false</c>, so a caller that checks r.ok after the call gets
        /// the same answer.
        /// </summary>
        public static bool ExtractEmbeddedMaterials(string modelPath, string destFolder,
                                                    ConversionResult r, out int extracted)
        {
            List<string> ignoredPaths;
            return ExtractEmbeddedMaterials(modelPath, destFolder, r, out extracted, out ignoredPaths);
        }

        /// <summary>
        /// Same extraction operation as the four-argument overload, additionally
        /// returning the material asset paths created by this invocation. Callers
        /// that need to perform a follow-up repair should use these paths rather than
        /// guessing from material names (which are sanitized and uniquified here).
        /// </summary>
        public static bool ExtractEmbeddedMaterials(string modelPath, string destFolder,
                                                    ConversionResult r, out int extracted,
                                                    out List<string> extractedMaterialPaths)
        {
            extracted = 0;
            extractedMaterialPaths = new List<string>();
            if (r == null) r = new ConversionResult();

            if (!IsModelPath(modelPath))
            {
                r.Skipped("material extraction: '" + modelPath + "' is not a model file");
                return true;
            }

            var embedded = new List<Material>();
            var all = AssetDatabase.LoadAllAssetsAtPath(modelPath);
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i] as Material;
                if (mat != null && AssetDatabase.IsSubAsset(mat)) embedded.Add(mat);
            }

            if (embedded.Count == 0)
            {
                r.Skipped("no embedded materials in " + Path.GetFileName(modelPath));
                return true;
            }

            if (!EnsureFolder(destFolder) || !CommitFolder(destFolder))
            {
                r.Failed("could not create the materials folder '" + destFolder + "'");
                return false;
            }

            var failures = new List<string>();
            // Names we have already handed to ExtractAsset in THIS batch.
            // GenerateUniqueAssetPath cannot see them: inside
            // StartAssetEditing/StopAssetEditing the database does not register
            // assets created earlier in the same batch, so it happily returns
            // the same path twice. SanitizeAssetName manufactures exactly those
            // collisions — it maps '/', '\' and ':' all to '_', so a Maya FBX
            // carrying "Wood/Bark" and "Wood:Bark" (or two namespaced
            // "lambert1:diffuse") lands both on Wood_Bark.mat, the second
            // extraction overwrites the first, and the model rebinds two slots
            // to one material with no failure reported.
            var issued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ExtractAsset queues an import of the model. Doing that once per
            // material reimports the whole FBX N times and — worse — invalidates
            // the sub-asset references we are still iterating, so later
            // extractions in the same loop silently no-op. Batch the whole set,
            // then reimport exactly once.
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < embedded.Count; i++)
                {
                    Material mat = embedded[i];
                    string leaf = SanitizeAssetName(mat.name, "Material");

                    // GenerateUniqueAssetPath first, so files already on disk are
                    // still avoided; the HashSet only covers this batch.
                    string newPath = AssetDatabase.GenerateUniqueAssetPath(destFolder + "/" + leaf + ".mat");

                    int n = 1;
                    while (!issued.Add(newPath))
                    {
                        newPath = destFolder + "/" + leaf + " " + n + ".mat";
                        n++;
                    }

                    // ExtractAsset returns an ERROR STRING; empty means success.
                    string err = AssetDatabase.ExtractAsset(mat, newPath);
                    if (string.IsNullOrEmpty(err))
                    {
                        extracted++;
                        extractedMaterialPaths.Add(newPath);
                    }
                    else failures.Add(mat.name + ": " + err);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // The extraction is recorded in the model's .meta (externalObjects).
            // Without WriteImportSettingsIfDirty the remap is only in memory and
            // the next reimport re-embeds everything.
            AssetDatabase.WriteImportSettingsIfDirty(modelPath);
            CommitFolder(destFolder);
            for (int i = 0; i < extractedMaterialPaths.Count; i++)
            {
                AssetDatabase.ImportAsset(extractedMaterialPaths[i], ImportAssetOptions.ForceSynchronousImport);
            }
            AssetDatabase.ImportAsset(modelPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            if (extracted > 0)
                r.Extracted(extracted + " embedded material" + (extracted == 1 ? "" : "s") + " → " + destFolder);

            for (int i = 0; i < failures.Count; i++)
                r.Failed("could not extract material " + failures[i]);

            return failures.Count == 0;
        }

        // ── Small utilities ─────────────────────────────────────────────

        // Folder timing. Unity's AssetDatabase is not the filesystem.
        // CreateFolder can return a GUID while IsValidFolder is still false
        // this frame, and SaveAsPrefabAsset / MoveAsset then fail with
        // "parent is not in the AssetDatabase". Every write into a kit
        // folder goes through this sequence and no other:
        //
        //   1. CreateFolder, or ImportAsset if the folder is already on disk
        //   2. ImportAsset(ForceSynchronousImport)
        //   3. If still not valid: SaveAssets + Refresh(ForceSynchronousImport)
        //   4. If still not valid: Start/StopAssetEditing to flush the queue,
        //      then ImportAsset again
        //   5. Retry the wait a few times. Only then write into the folder.
        //
        // A same-frame IsValidFolder miss is not a failure. Wait, then check
        // again. FAILED only if the folder is still missing after the wait.

        static string AbsoluteFromAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            assetPath = assetPath.Replace('\\', '/').TrimEnd('/');
            if (assetPath == "Assets") return Application.dataPath;
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            return Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
        }

        public static bool FolderExistsOnDisk(string assetPath)
        {
            string abs = AbsoluteFromAsset(assetPath);
            return !string.IsNullOrEmpty(abs) && Directory.Exists(abs);
        }

        /// <summary>
        /// Import / refresh until the AssetDatabase has <paramref name="folder"/>.
        /// Does not create it. Returns true only when IsValidFolder is true.
        /// </summary>
        public static bool CommitFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return true;

            const int attempts = 8;
            for (int i = 0; i < attempts; i++)
            {
                if (FolderExistsOnDisk(folder))
                    AssetDatabase.ImportAsset(folder, ImportAssetOptions.ForceSynchronousImport);
                else
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                if (AssetDatabase.IsValidFolder(folder)) return true;

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.IsValidFolder(folder)) return true;

                // Flush pending imports. Start+Stop with nothing queued is
                // how Unity lands CreateFolder / ExtractAsset work that is
                // still sitting in the edit batch.
                AssetDatabase.StartAssetEditing();
                AssetDatabase.StopAssetEditing();

                if (FolderExistsOnDisk(folder))
                    AssetDatabase.ImportAsset(folder, ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.IsValidFolder(folder)) return true;
            }

            return AssetDatabase.IsValidFolder(folder);
        }

        /// <summary>
        /// Creates <paramref name="assetFolderPath"/> and every missing parent,
        /// then waits until the AssetDatabase has it. CreateFolder only makes
        /// one level and returns an empty GUID when the parent is missing.
        /// </summary>
        public static bool EnsureFolder(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath)) return false;

            string path = assetFolderPath.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return true;
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && path != "Assets")
                return false;

            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                string next = current + "/" + parts[i];
                if (AssetDatabase.IsValidFolder(next))
                {
                    current = next;
                    continue;
                }

                if (FolderExistsOnDisk(next))
                {
                    if (!CommitFolder(next)) return false;
                    current = next;
                    continue;
                }

                string guid = AssetDatabase.CreateFolder(current, parts[i]);
                if (string.IsNullOrEmpty(guid) && !FolderExistsOnDisk(next))
                    return false;

                // Created (GUID or already on disk). Wait until Unity has it.
                // Disk-only is not enough — SaveAsPrefabAsset / MoveAsset
                // still fail while IsValidFolder is false.
                if (!CommitFolder(next)) return false;
                current = next;
            }

            return CommitFolder(path);
        }

        /// <summary>
        /// DCC material names routinely contain path separators and colons
        /// ("Wood/Bark", "lambert1:diffuse"). ContentFolders.Sanitize strips the
        /// platform's invalid filename characters, which on Linux/macOS does not
        /// include ':' or '\', so those are handled explicitly first.
        ///
        /// <paramref name="fallback"/> is what a name that sanitizes to nothing
        /// becomes. It belongs to the CALLER because this is no longer the
        /// material-only sanitizer it started as — the prop emitters run asset
        /// filenames through it too, and a hard-coded "Material" turned a
        /// CJK-only or punctuation-only filename into P_Material.prefab, where
        /// the second such asset silently collided with the first and only
        /// surfaced as a DuplicateNamesCheck failure at upload time.
        /// ExtractEmbeddedMaterials passes "Material" to keep its old behaviour.
        /// </summary>
        public static string SanitizeAssetName(string name, string fallback = "Asset")
        {
            if (string.IsNullOrEmpty(fallback)) fallback = "Asset";
            if (string.IsNullOrEmpty(name)) return fallback;

            string cleaned = name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
            cleaned = ContentFolders.Sanitize(cleaned);

            return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
        }

        private static string ExtensionOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            return Path.GetExtension(assetPath);
        }

        static bool IsUnder(string assetPath, string folder)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folder)) return false;
            assetPath = assetPath.Replace('\\', '/');
            folder = folder.Replace('\\', '/').TrimEnd('/');
            return assetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True only for a real file inside THIS project's Assets tree.
        ///
        /// THIS IS THE GUARD THAT KEEPS THE EDITOR ALIVE. A model with an
        /// unassigned slot renders with Unity's Default-Material, and
        /// AssetDatabase.GetAssetPath on that returns "Resources/unity_builtin_extra"
        /// — a path with no file behind it. Hand that to CopyAsset (or to
        /// anything else that stages a file) and Unity tries to copy it through
        /// Temp/copyassets, fails, and puts up the NATIVE modal
        ///
        ///     "Copying file failed
        ///      Copying Resources/unity_builtin_extra to Temp/copyassets/… :
        ///      No such file or directory"    [Try Again] [Force Quit] [Cancel]
        ///
        /// which is not an exception we can catch — it is the editor going down
        /// mid-import, taking the run and any unsaved scene with it.
        ///
        /// Assets under Packages/ are excluded for the second reason: they are
        /// read-only and not ours to rewrite.
        /// </summary>
        public static bool IsWritableProjectAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            string abs = AbsoluteFromAsset(assetPath);
            return !string.IsNullOrEmpty(abs) && File.Exists(abs);
        }

        /// <summary>
        /// True when there is a real file behind <paramref name="assetPath"/>,
        /// wherever it lives. This is the weaker of the two tests and it is the
        /// one that matters for CopyAsset: a texture under Packages/ is a
        /// perfectly good thing to duplicate into the kit — we only ever read
        /// it — whereas a built-in's pseudo-path is the fatal dialog. Use
        /// IsWritableProjectAsset instead before writing INTO an asset.
        /// </summary>
        public static bool IsCopyableAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            assetPath = assetPath.Replace('\\', '/');
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                string abs = AbsoluteFromAsset(assetPath);
                return !string.IsNullOrEmpty(abs) && File.Exists(abs);
            }
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return false;
            // Packages/ resolves through the package manager, not through
            // Application.dataPath. AssetDatabase is the only thing that knows
            // where it really is; a non-empty GUID means a real asset.
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));
        }

        /// <summary>
        /// True when the folder exists and holds nothing at all — the
        /// "a kit folder was created and then the run wrote nothing into it"
        /// case. Never true for a folder with content, so a cleanup built on
        /// this can't take a creator's work with it.
        /// </summary>
        public static bool IsEmptyFolder(string assetFolderPath)
        {
            string abs = AbsoluteFromAsset(assetFolderPath);
            if (string.IsNullOrEmpty(abs) || !Directory.Exists(abs)) return false;
            return Directory.GetFileSystemEntries(abs).Length == 0;
        }

        /// <summary>
        /// Copy the model's materials and textures into the kit so the folder
        /// is self-contained.
        ///
        /// Two rules, both learned the hard way:
        ///
        ///  1. NEVER TOUCH AN ASSET WE DO NOT OWN. Built-ins are the fatal
        ///     dialog described on IsWritableProjectAsset. Vendor and Sample
        ///     materials are the quieter version of the same mistake: the first
        ///     draft called mat.SetTexture on the ORIGINAL material, so a pack
        ///     material shared by forty models got permanently repointed at a
        ///     texture buried inside one prop's kit — and converting the next
        ///     model repointed it again, orphaning the first copy. Delete that
        ///     kit later and forty unrelated prefabs go pink.
        ///
        ///  2. COPY FIRST, THEN RETARGET THE COPY, THEN REMAP THE MODEL. A copy
        ///     nothing references is worse than no copy: the renderers still
        ///     point outside the kit (so the folder is still not self-contained
        ///     and OutsideContentFolderCheck still fires) and the bundle ships a
        ///     duplicate nobody reads. The importer remap is what makes the
        ///     copy the real one.
        ///
        /// Reimports the model when anything was remapped, so every object the
        /// CALLER loaded from modelPath is destroyed by the time this returns.
        /// Re-load after calling.
        /// </summary>
        public static void PackDependenciesIntoKit(string modelPath, string kitFolder, ConversionResult r)
        {
            if (string.IsNullOrEmpty(kitFolder)) return;
            kitFolder = kitFolder.Replace('\\', '/').TrimEnd('/');
            if (!EnsureFolder(kitFolder)) return;

            var go = LoadModel(modelPath);
            if (go == null) go = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (go == null) return;

            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;

            var materials = new HashSet<Material>();
            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                if (rend == null) continue;
                var mats = rend.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null) materials.Add(mats[i]);
            }

            int textures = 0;
            int matsCopied = 0;
            int untouchable = 0;
            int untouchableTextures = 0;
            int orphaned = 0;
            bool remapped = false;

            foreach (Material mat in materials)
            {
                string matPath = AssetDatabase.GetAssetPath(mat);

                // Rule 1. A built-in, or one still embedded in the FBX as a
                // sub-asset: no file to copy and nothing we may write to.
                // Skipping is the whole fix for the fatal dialog.
                if (!IsCopyableAsset(matPath) || AssetDatabase.IsSubAsset(mat))
                {
                    untouchable++;
                    continue;
                }

                // Rule 2. Work on the material the KIT owns: already inside the
                // kit (ExtractEmbeddedMaterials put it there) means ours to
                // retarget in place; outside means copy first.
                Material target = mat;
                if (!IsUnder(matPath, kitFolder))
                {
                    if (importer == null)
                    {
                        // No ModelImporter means no remap, which means any copy
                        // we made would be an orphan. Say so instead.
                        orphaned++;
                        continue;
                    }

                    string matDest = AssetDatabase.GenerateUniqueAssetPath(
                        kitFolder + "/" + Path.GetFileName(matPath));
                    if (!AssetDatabase.CopyAsset(matPath, matDest))
                    {
                        if (r != null) r.Skipped("could not pack material " + Path.GetFileName(matPath));
                        continue;
                    }
                    AssetDatabase.ImportAsset(matDest, ImportAssetOptions.ForceSynchronousImport);
                    Material copy = AssetDatabase.LoadAssetAtPath<Material>(matDest);
                    if (copy == null)
                    {
                        if (r != null) r.Skipped("could not pack material " + Path.GetFileName(matPath));
                        continue;
                    }

                    // KEY THE REMAP OFF THE EXISTING MAP, not off the material's
                    // name. SourceAssetIdentifier(Object) builds the key from
                    // type + mat.name, but the importer's keys are the FBX's
                    // INTERNAL material names. Those agree only when nothing was
                    // ever renamed. An FBX already remapping slot "lambert1" to
                    // a vendor "Rock_Grey.mat" would get a new entry keyed
                    // "Rock_Grey" that matches no slot: AddRemap silently
                    // no-ops, the old remap survives the reimport, the renderers
                    // still point outside the kit, and the report claims the
                    // opposite. Same for any extracted material whose filename
                    // got uniquified ("Wood 1.mat" from FBX material "Wood").
                    var id = new AssetImporter.SourceAssetIdentifier(mat);
                    foreach (var entry in importer.GetExternalObjectMap())
                    {
                        if (entry.Value == mat) { id = entry.Key; break; }
                    }
                    importer.AddRemap(id, copy);
                    remapped = true;
                    target = copy;
                    matsCopied++;
                }

                // Only write into a material this project owns. A package
                // material copied above is now a kit-local copy and passes;
                // one we decided to retarget in place is under Assets/ by
                // construction.
                if (!IsWritableProjectAsset(AssetDatabase.GetAssetPath(target))) continue;

                string[] props;
                try { props = target.GetTexturePropertyNames(); }
                catch { continue; }

                bool dirty = false;
                for (int i = 0; i < props.Length; i++)
                {
                    Texture tex = target.GetTexture(props[i]);
                    if (tex == null) continue;
                    string texPath = AssetDatabase.GetAssetPath(tex);
                    if (AssetDatabase.IsSubAsset(tex)) continue;
                    if (!IsCopyableAsset(texPath)) { untouchableTextures++; continue; }
                    if (IsUnder(texPath, kitFolder)) continue;

                    string dest = AssetDatabase.GenerateUniqueAssetPath(
                        kitFolder + "/" + Path.GetFileName(texPath));
                    if (!AssetDatabase.CopyAsset(texPath, dest))
                    {
                        if (r != null) r.Skipped("could not pack texture " + Path.GetFileName(texPath));
                        continue;
                    }
                    AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceSynchronousImport);
                    Texture copy = AssetDatabase.LoadAssetAtPath<Texture>(dest);
                    if (copy == null) continue;
                    target.SetTexture(props[i], copy);
                    dirty = true;
                    textures++;
                }

                if (dirty)
                {
                    EditorUtility.SetDirty(target);
                    AssetDatabase.SaveAssetIfDirty(target);
                }
            }

            // The remap lives in the model's .meta (externalObjects). Without
            // WriteImportSettingsIfDirty it is memory-only and the next
            // reimport points the renderers back at the originals.
            int stillOutside = 0;
            if (remapped)
            {
                AssetDatabase.WriteImportSettingsIfDirty(modelPath);
                AssetDatabase.ImportAsset(modelPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                // CHECK, don't assume. AddRemap is silent about a key that
                // matches no slot, so the only honest way to know whether the
                // model now uses the kit's copies is to re-read the renderers.
                // A report line claiming a self-contained kit that isn't one is
                // worse than no line at all — it is the difference between the
                // creator fixing this now and finding out at upload.
                var after = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (after != null)
                {
                    var seen = new HashSet<Material>();
                    foreach (var rend in after.GetComponentsInChildren<Renderer>(true))
                    {
                        if (rend == null) continue;
                        var mats = rend.sharedMaterials;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            Material m = mats[i];
                            if (m == null || !seen.Add(m)) continue;
                            string p = AssetDatabase.GetAssetPath(m);
                            if (!IsCopyableAsset(p) || AssetDatabase.IsSubAsset(m)) continue;
                            if (!IsUnder(p, kitFolder)) stillOutside++;
                        }
                    }
                }
            }

            if (r == null) return;
            if (textures > 0)
                r.Added("packed " + textures + " texture" + (textures == 1 ? "" : "s") + " into " + kitFolder);
            if (matsCopied > 0 && stillOutside == 0)
                r.Added("packed " + matsCopied + " material" + (matsCopied == 1 ? "" : "s")
                        + " into " + kitFolder + " and repointed the model at the copies");
            if (stillOutside > 0)
                r.Skipped(stillOutside + " material" + (stillOutside == 1 ? " is" : "s are")
                    + " still referenced from outside " + kitFolder + " after the remap — the model's "
                    + "internal material slot names do not match the material assets, so Unity ignored "
                    + "the remap. Assign the copies in the model's Materials tab by hand, or the kit is "
                    + "not self-contained and the upload's outside-content-folder check will say so.");
            if (untouchable > 0)
                r.Skipped(untouchable + " material" + (untouchable == 1 ? " is" : "s are")
                    + " a Unity built-in or still embedded in the model — left alone; the kit does not "
                    + "own them and copying a built-in is what takes the editor down");
            if (untouchableTextures > 0)
                r.Skipped(untouchableTextures + " texture" + (untouchableTextures == 1 ? " is" : "s are")
                    + " a Unity built-in — left alone");
            if (orphaned > 0)
                r.Skipped(orphaned + " material" + (orphaned == 1 ? "" : "s") + " outside " + kitFolder
                    + " could not be packed — this source has no ModelImporter to repoint, so a copy "
                    + "would be a duplicate nothing references");
        }
    }
}
#endif
