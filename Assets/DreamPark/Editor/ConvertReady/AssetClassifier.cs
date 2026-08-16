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
        /// Where extracted materials belong: the model's own content package if
        /// it lives under Assets/Content, otherwise whichever package
        /// ContentFolders picks. Keeping them inside the content folder is the
        /// whole integration — ContentProcessor watches that tree.
        ///
        /// FolderOfAsset returns the first path SEGMENT after "Assets/Content/"
        /// without checking that the segment is a directory, so a model dropped
        /// straight into Assets/Content ("Assets/Content/tree.fbx") comes back
        /// as "tree.fbx". Trusting that gives
        /// "Assets/Content/tree.fbx/Materials", and EnsureFolder then either
        /// fails outright (no extraction, pink prop) or creates a directory
        /// whose name collides with the model file inside the very tree
        /// ContentProcessor watches. Hence the IsValidFolder check.
        /// </summary>
        public static string DefaultMaterialsFolder(string modelPath)
        {
            string game = ContentFolders.FolderOfAsset(modelPath);
            if (!string.IsNullOrEmpty(game)
                && AssetDatabase.IsValidFolder(ContentFolders.Root + "/" + game))
                return ContentFolders.Root + "/" + game + "/Materials";

            return ContentFolders.AutoPickContentFolder() + "/Materials";
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
            extracted = 0;
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

            if (!EnsureFolder(destFolder))
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
                    if (string.IsNullOrEmpty(err)) extracted++;
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
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);

            if (extracted > 0)
                r.Extracted(extracted + " embedded material" + (extracted == 1 ? "" : "s") + " → " + destFolder);

            for (int i = 0; i < failures.Count; i++)
                r.Failed("could not extract material " + failures[i]);

            return failures.Count == 0;
        }

        // ── Small utilities ─────────────────────────────────────────────

        /// <summary>
        /// Creates <paramref name="assetFolderPath"/> and every missing parent.
        /// AssetDatabase.CreateFolder only creates ONE level and returns an
        /// empty GUID rather than throwing when the parent is missing, which is
        /// how "Assets/Content/Game/Materials" silently fails to appear.
        /// </summary>
        public static bool EnsureFolder(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath)) return false;

            string normalized = assetFolderPath.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized)) return true;

            string[] parts = normalized.Split('/');
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.Ordinal))
                return false;

            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;

                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid)) return false;
                }
                current = next;
            }
            return AssetDatabase.IsValidFolder(current);
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
    }
}
#endif
