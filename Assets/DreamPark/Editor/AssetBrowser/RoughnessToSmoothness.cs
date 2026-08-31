#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// Turns a roughness map into the smoothness map the DreamPark shader
    /// actually wants.
    ///
    /// ── WHY THIS HAS TO EXIST ──────────────────────────────────────────────
    ///
    /// DreamPark-UniversalShader has a property called `_rougnessMap` (the
    /// misspelling is in the shader) and it is wired STRAIGHT INTO
    /// SurfaceDescription.Smoothness. There is no invert anywhere in the
    /// graph. So despite the name, the shader wants SMOOTHNESS: 1 = glossy.
    ///
    /// Roughness is the opposite convention: 0 = mirror, 1 = matte. It is what
    /// glTF specifies and therefore what every AI mesh generator ships, and
    /// MaterialConverter maps `_RoughnessMap` into `_rougnessMap` without
    /// inverting it — there is a comment at MaterialConverter.cs:246 that
    /// contemplates inverting and then doesn't. Feed a real roughness map
    /// through that path and the shading is exactly backwards: matte surfaces
    /// render glossy and glossy ones render matte. On passthrough MR with real
    /// room lighting it reads as "everything looks wet."
    ///
    /// This is a PRE-EXISTING SDK bug, not one the Asset Browser introduces —
    /// any asset pack shipping true roughness hits it today. The correct fix
    /// is a OneMinus node in the shader graph, but that file must stay
    /// byte-identical to dreampark-core, so it is a core-side decision. Baking
    /// an inverted copy at import time fixes it here, now, without touching a
    /// synced file, and it is reversible: the original roughness map is left
    /// on disk untouched, just unbound.
    /// </summary>
    public static class RoughnessToSmoothness
    {
        public const string SuffixSmoothness = "__Smoothness";

        /// <summary>
        /// Write 1-x of the given roughness texture beside it. Returns the new
        /// asset path, or null if the source could not be read — in which case
        /// the caller leaves the roughness map unbound rather than binding
        /// something known to be inverted.
        /// </summary>
        public static string Bake(string roughnessAssetPath, string outAssetPath)
        {
            if (string.IsNullOrEmpty(roughnessAssetPath) || string.IsNullOrEmpty(outAssetPath)) return null;

            Texture2D source = null;
            try
            {
                source = DecodeOriginal(roughnessAssetPath);
                if (source == null)
                {
                    Debug.LogWarning($"[Dreamie] Could not read {roughnessAssetPath} to invert it — "
                                     + "the material will use a flat smoothness value instead.");
                    return null;
                }

                var pixels = source.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    // Invert the colour channels only. Alpha is not roughness
                    // and inverting it would turn a fully opaque map into a
                    // fully transparent one.
                    p.r = (byte)(255 - p.r);
                    p.g = (byte)(255 - p.g);
                    p.b = (byte)(255 - p.b);
                    pixels[i] = p;
                }

                var outTex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                try
                {
                    outTex.SetPixels32(pixels);
                    outTex.Apply(false, false);

                    string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                    string abs = Path.Combine(projectRoot, outAssetPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(abs));
                    File.WriteAllBytes(abs, outTex.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(outTex);
                }

                AssetDatabase.ImportAsset(outAssetPath, ImportAssetOptions.ForceSynchronousImport);
                // Linear, like every other data map — see DreamieTextureSetup.
                DreamieTextureSetup.ApplyRoleSettings(outAssetPath, DreamieTextureRole.Smoothness);
                return outAssetPath;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Dreamie] Smoothness bake failed for {roughnessAssetPath}: {e.Message}");
                return null;
            }
            finally
            {
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
            }
        }

        /// <summary>
        /// Decode the file on disk rather than the imported Texture2D.
        ///
        /// The imported version has already been resized and block-compressed,
        /// so reading it back would invert lossy data and re-encode it —
        /// compounding two generations of artefacts into a map that is meant
        /// to be smooth gradients. The bytes on disk are what the generator
        /// actually produced.
        /// </summary>
        private static Texture2D DecodeOriginal(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            string abs = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs)) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            // LoadImage covers PNG and JPEG, which is everything the generators
            // emit (Tripo is asked for PNG explicitly). Anything else falls
            // through to the readable-copy path below.
            if (tex.LoadImage(File.ReadAllBytes(abs))) return tex;
            UnityEngine.Object.DestroyImmediate(tex);

            return ReadViaImporter(assetPath);
        }

        /// <summary>
        /// Fallback for formats LoadImage will not decode (TGA, PSD, EXR).
        /// Flips isReadable just long enough to copy the pixels out, then puts
        /// it back — a texture left readable carries a permanent CPU-side copy.
        /// </summary>
        private static Texture2D ReadViaImporter(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return null;

            bool wasReadable = importer.isReadable;
            bool wasCompressed = importer.textureCompression != TextureImporterCompression.Uncompressed;
            try
            {
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();

                var src = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (src == null) return null;

                var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                copy.SetPixels32(src.GetPixels32());
                copy.Apply(false, false);
                return copy;
            }
            catch { return null; }
            finally
            {
                importer.isReadable = wasReadable;
                importer.textureCompression = wasCompressed
                    ? TextureImporterCompression.Compressed
                    : TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }
    }
}
#endif
